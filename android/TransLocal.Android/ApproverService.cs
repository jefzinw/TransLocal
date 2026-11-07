using System.Buffers.Binary;
using System.Net.Sockets;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using TransLocal.Security;
using TransLocal.Streaming;

namespace TransLocal.Mobile;

/// <summary>
/// Mantém o celular conectado ao computador pareado (conexão protegida, papel “aprovador”) para receber
/// pedidos de acesso. Roda como serviço em primeiro plano (notificação fixa), reconectando sozinho.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeRemoteMessaging)]
internal sealed class ApproverService : Service
{
    private Thread? _thread;
    private volatile bool _running;

    public static void Start(Context ctx)
    {
        var intent = new Intent(ctx, typeof(ApproverService));
        ctx.StartForegroundService(intent);
        PhoneState.Kick.Set();
    }

    public static void Stop(Context ctx) => ctx.StopService(new Intent(ctx, typeof(ApproverService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        Notifications.EnsureChannels(this);
        var n = Notifications.Service(this, "Conectando ao computador…");
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(Notifications.ServiceId, n, ForegroundService.TypeRemoteMessaging);
        else
            StartForeground(Notifications.ServiceId, n);

        if (_thread is null)
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "TransLocal aprovador" };
            _thread.Start();
        }
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _running = false;
        PhoneState.Kick.Set();
        PhoneState.SetConnection(null);
        PhoneState.SetStatus("Desligado", false);
        base.OnDestroy();
    }

    private void SetStatus(string text, bool connected)
    {
        PhoneState.SetStatus(text, connected);
        try { Notifications.UpdateService(this, text); } catch { }
    }

    private void Loop()
    {
        var store = new PhoneStore(this);
        DeviceIdentity identity;
        try
        {
            identity = store.LoadIdentity();
        }
        catch (Exception ex)
        {
            Log.Error("Identidade do celular indisponível", ex);
            SetStatus("Erro na identidade do app: " + ex.Message, false);
            return;
        }

        int backoff = 3000;
        while (_running)
        {
            if (!store.HasPc)
            {
                SetStatus("Nenhum computador pareado", false);
                PhoneState.Kick.WaitOne(5000);
                continue;
            }

            var (host, port) = store.Endpoint();
            try
            {
                SetStatus($"Conectando a {store.PcName}…", false);
                using var client = new TcpClient(AddressFamily.InterNetwork);
                if (!client.ConnectAsync(host, port).Wait(8000))
                    throw new IOException("sem resposta");
                var (conn, peer) = SecureChannel.Connect(client, ChannelPurpose.Approver, new SecureChannelOptions
                {
                    Identity = identity,
                    Trust = store,
                    LocalName = store.DeviceName,
                    LocalKind = "phone",
                    ConfirmPairing = null, // pareamento só pela tela do app
                });
                using (conn)
                {
                    if (peer.Fingerprint != store.PcFingerprint) throw new AuthException("Computador diferente do pareado.");
                    PhoneState.SetConnection(conn);
                    backoff = 3000;
                    SetStatus($"Conectado a {peer.Name} — pronto para aprovar", true);
                    Serve(conn, store);
                }
            }
            catch (Exception ex)
            {
                string why = ex is AggregateException ae ? ae.InnerException?.Message ?? ex.Message : ex.Message;
                Log.Warn("Conexão com o computador perdida", ex);
                SetStatus($"Sem conexão com {store.PcName} ({why}). Nova tentativa em {backoff / 1000} s.", false);
            }
            finally
            {
                PhoneState.SetConnection(null);
            }
            if (!_running) break;
            PhoneState.Kick.WaitOne(backoff);
            backoff = Math.Min(backoff * 2, 60_000);
        }
    }

    private void Serve(Connection conn, PhoneStore store)
    {
        conn.ReceiveTimeoutMs = 75_000;
        using var pinger = new Timer(_ =>
        {
            try
            {
                var ts = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(ts, System.Environment.TickCount64);
                conn.Send(MsgType.Ping, ts);
            }
            catch { }
        }, null, 25_000, 25_000);

        while (_running)
        {
            using var m = conn.Receive();
            switch (m.Type)
            {
                case MsgType.ApproverStatus:
                    var st = m.ReadJson<ApproverStatusMessage>();
                    if (!string.IsNullOrEmpty(st.TotpSecret)) store.TotpSecret = st.TotpSecret;
                    if (!string.IsNullOrEmpty(st.PcName)) store.PcName = st.PcName;
                    PhoneState.LastStatus = st;
                    SetStatus(st.RemoteAccessEnabled
                        ? $"Conectado a {st.PcName} — pronto para aprovar"
                        : $"Conectado a {st.PcName} — acesso remoto DESLIGADO no PC", true);
                    break;
                case MsgType.ApprovalRequest:
                    var r = m.ReadJson<ApprovalRequestMessage>();
                    if (r.Cancelled)
                    {
                        PhoneState.RemovePending(r.Id);
                        Notifications.CancelRequest(this, r.Id);
                    }
                    else
                    {
                        PhoneState.AddPending(r);
                        Notifications.ShowRequest(this, r, store.PcName ?? "o computador");
                    }
                    break;
                case MsgType.Bye:
                    return;
            }
        }
    }
}
