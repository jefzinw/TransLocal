using System.Net;
using System.Net.Sockets;
using TransLocal.Security;

namespace TransLocal.Streaming;

internal sealed class StreamRequestMessage
{
    /// <summary>Código de autorização (TOTP) digitado, se houver.</summary>
    public string? Code { get; set; }
}

internal sealed class StreamDecisionMessage
{
    public bool Approved { get; set; }
    public string? Reason { get; set; }
    public string? ApprovedBy { get; set; }
}

internal sealed class ApprovalRequestMessage
{
    public string Id { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public DateTime TimeUtc { get; set; }
    public int TimeoutSeconds { get; set; }

    /// <summary>true = pedido encerrado (respondido em outro lugar ou expirado).</summary>
    public bool Cancelled { get; set; }
}

internal sealed class ApprovalResponseMessage
{
    public string Id { get; set; } = "";
    public bool Approved { get; set; }
}

internal sealed class ApproverStatusMessage
{
    public string PcName { get; set; } = "";
    public string PcId { get; set; } = "";
    public bool RemoteAccessEnabled { get; set; }
    public bool Streaming { get; set; }
    public string? StreamingTo { get; set; }

    /// <summary>Segredo do código de autorização (só para o celular pareado), em Base32.</summary>
    public string? TotpSecret { get; set; }
}

/// <summary>
/// No computador acessado: celulares pareados ficam conectados como “aprovadores”. Um pedido de acesso
/// é enviado a todos eles (e mostrado na tela do PC, para quem estiver em casa); vale a primeira resposta.
/// </summary>
internal sealed class ApprovalHub
{
    private readonly object _sync = new();
    private readonly List<(Connection Conn, PeerInfo Peer)> _approvers = new();
    private readonly Dictionary<string, TaskCompletionSource<(bool Approved, string By)>> _pending = new();

    public required Func<ApproverStatusMessage> StatusProvider { get; init; }

    public event Action? ApproversChanged;

    public IReadOnlyList<string> ConnectedNames
    {
        get { lock (_sync) return _approvers.Select(a => a.Peer.Name).ToList(); }
    }

    /// <summary>Atende um celular conectado; bloqueia até ele desconectar.</summary>
    public void Serve(Connection conn, PeerInfo phone)
    {
        lock (_sync) _approvers.Add((conn, phone));
        Log.Info($"Celular aprovador conectado: {phone.Name} ({phone.ShortId})");
        ApproversChanged?.Invoke();
        try
        {
            conn.SendJson(MsgType.ApproverStatus, StatusProvider());
            conn.ReceiveTimeoutMs = 90_000; // o celular manda Ping a cada ~30 s
            while (true)
            {
                using var m = conn.Receive();
                switch (m.Type)
                {
                    case MsgType.Ping:
                        conn.Send(MsgType.Pong, m.Span);
                        break;
                    case MsgType.ApproverHello:
                        conn.SendJson(MsgType.ApproverStatus, StatusProvider());
                        break;
                    case MsgType.ApprovalResponse:
                        var r = m.ReadJson<ApprovalResponseMessage>();
                        TaskCompletionSource<(bool, string)>? tcs;
                        lock (_sync) _pending.TryGetValue(r.Id, out tcs);
                        tcs?.TrySetResult((r.Approved, phone.Name));
                        break;
                    case MsgType.Bye:
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
        }
        finally
        {
            lock (_sync) _approvers.RemoveAll(a => a.Conn == conn);
            Log.Info($"Celular aprovador desconectado: {phone.Name}");
            ApproversChanged?.Invoke();
        }
    }

    /// <summary>Envia o estado atual (ex.: acesso remoto ligado/desligado) a todos os celulares.</summary>
    public void BroadcastStatus()
    {
        var status = StatusProvider();
        foreach (var (conn, _) in Snapshot())
        {
            try { conn.SendJson(MsgType.ApproverStatus, status); } catch { }
        }
    }

    /// <summary>
    /// Pede aprovação aos celulares e (opcional) a quem estiver no PC. Bloqueia até a primeira resposta
    /// ou até o tempo acabar (negado).
    /// </summary>
    public (bool Approved, string By) Request(PeerInfo requester, TimeSpan timeout, Func<CancellationToken, bool>? localPrompt)
    {
        string id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _pending[id] = tcs;
        var msg = new ApprovalRequestMessage
        {
            Id = id,
            DeviceName = requester.Name,
            DeviceId = requester.ShortId,
            TimeUtc = DateTime.UtcNow,
            TimeoutSeconds = (int)timeout.TotalSeconds,
        };
        var approvers = Snapshot();
        foreach (var (conn, _) in approvers)
        {
            try { conn.SendJson(MsgType.ApprovalRequest, msg); } catch { }
        }
        Log.Info($"Pedido de acesso de {requester.Name} enviado a {approvers.Count} celular(es)");

        using var cts = new CancellationTokenSource();
        if (localPrompt is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    bool r = localPrompt(cts.Token);
                    if (!cts.IsCancellationRequested) tcs.TrySetResult((r, "este computador"));
                }
                catch { }
            });
        }

        (bool Approved, string By) result;
        try
        {
            result = tcs.Task.Wait(timeout) ? tcs.Task.Result : (false, "tempo esgotado");
        }
        finally
        {
            cts.Cancel();
            lock (_sync) _pending.Remove(id);
            msg.Cancelled = true;
            foreach (var (conn, _) in Snapshot())
            {
                try { conn.SendJson(MsgType.ApprovalRequest, msg); } catch { }
            }
        }
        Log.Info($"Pedido de acesso de {requester.Name}: {(result.Approved ? "aprovado" : "negado")} ({result.By})");
        return result;
    }

    private List<(Connection Conn, PeerInfo Peer)> Snapshot()
    {
        lock (_sync) return _approvers.ToList();
    }
}

/// <summary>Lado de quem pede a tela (ex.: notebook fora de casa).</summary>
internal static class RemotePull
{
    public static async Task<(Connection Conn, PeerInfo Peer)> RequestAsync(string host, int port, SecureChannelOptions channel,
        string? code, Action<string> status, CancellationToken ct)
    {
        host = host.Trim();
        IPAddress address;
        if (!IPAddress.TryParse(host, out address!))
        {
            var all = await Dns.GetHostAddressesAsync(host, ct);
            address = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? throw new IOException($"Não foi possível encontrar “{host}”.");
        }

        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(address, port, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            client.Dispose();
            throw new IOException($"Sem resposta de {host}:{port}. Confira se o computador está ligado, com o TransLocal aberto, " +
                                  "e se os dois estão no Tailscale (ou na mesma rede).", ex);
        }

        return await Task.Run(() =>
        {
            var (conn, peer) = SecureChannel.Connect(client, ChannelPurpose.Pull, channel);
            try
            {
                status($"Conectado a {peer.Name}. Aguardando autorização…");
                conn.SendJson(MsgType.StreamRequest, new StreamRequestMessage { Code = code });
                conn.ReceiveTimeoutMs = 100_000;
                var d = conn.ReceiveJson<StreamDecisionMessage>(MsgType.StreamDecision);
                if (!d.Approved) throw new AuthException(d.Reason ?? "Acesso negado.");
                Log.Info($"Acesso a {peer.Name} autorizado ({d.ApprovedBy})");
                return (conn, peer);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }, ct);
    }
}
