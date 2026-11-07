using System.Net.Sockets;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using TransLocal.Security;
using TransLocal.Streaming;

namespace TransLocal.Mobile;

[Activity(Label = "@string/app_name", MainLauncher = true, Theme = "@android:style/Theme.Material.Light.NoActionBar",
    LaunchMode = global::Android.Content.PM.LaunchMode.SingleTop)]
public class MainActivity : Activity
{
    private static readonly Color Accent = Color.Rgb(0, 103, 192);
    private static readonly Color Muted = Color.Rgb(100, 100, 100);

    private PhoneStore _store = null!;
    private TextView _status = null!;
    private LinearLayout _pending = null!;
    private TextView _code = null!;
    private TextView _codeInfo = null!;
    private TextView _pcInfo = null!;
    private Handler _handler = null!;
    private bool _busy;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _store = new PhoneStore(this);
        _handler = new Handler(Looper.MainLooper!);
        BuildUi();
        Notifications.EnsureChannels(this);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
            RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, 1);

        if (_store.HasPc) ApproverService.Start(this);
    }

    protected override void OnResume()
    {
        base.OnResume();
        PhoneState.Changed += OnStateChanged;
        PhoneState.Kick.Set();
        Tick();
    }

    protected override void OnPause()
    {
        PhoneState.Changed -= OnStateChanged;
        _handler.RemoveCallbacksAndMessages(null);
        base.OnPause();
    }

    private void OnStateChanged() => RunOnUiThread(Refresh);

    private void Tick()
    {
        Refresh();
        _handler.PostDelayed(Tick, 1000);
    }

    // ------------------------------------------------------------------ interface

    private int Dp(int v) => (int)(v * Resources!.DisplayMetrics!.Density);

    private TextView Text(string text, float sp, Color color, bool bold = false)
    {
        var t = new TextView(this) { Text = text };
        t.SetTextSize(global::Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(color);
        if (bold) t.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        t.SetPadding(0, Dp(6), 0, Dp(6));
        return t;
    }

    private Button Btn(string text, Action onClick, bool primary = false)
    {
        var b = new Button(this) { Text = text };
        b.SetAllCaps(false);
        if (primary)
        {
            b.SetBackgroundColor(Accent);
            b.SetTextColor(Color.White);
        }
        b.Click += (_, _) => onClick();
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) };
        b.LayoutParameters = lp;
        return b;
    }

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(20), Dp(28), Dp(20), Dp(28));

        root.AddView(Text("TransLocal", 26, Accent, bold: true));
        _status = Text("", 15, Muted);
        root.AddView(_status);

        _pending = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.AddView(_pending);

        root.AddView(Text("Código de autorização", 15, Color.Black, bold: true));
        _code = Text("— — —", 44, Accent, bold: true);
        _code.Typeface = Typeface.Monospace;
        _code.Gravity = GravityFlags.CenterHorizontal;
        root.AddView(_code);
        _codeInfo = Text("", 13, Muted);
        _codeInfo.Gravity = GravityFlags.CenterHorizontal;
        root.AddView(_codeInfo);
        root.AddView(Text("Para entrar sem esperar a aprovação, digite este código no aparelho que pede acesso (ele muda a cada 30 s).", 13, Muted));

        root.AddView(Text("Computador", 15, Color.Black, bold: true));
        _pcInfo = Text("", 14, Color.DarkGray);
        root.AddView(_pcInfo);
        root.AddView(Btn("Parear com um computador", StartPairing, primary: true));
        root.AddView(Btn("Alterar endereço do computador (ex.: Tailscale)", ChangeAddress));
        root.AddView(Btn("Permitir funcionar em segundo plano", AskBatteryExemption));
        root.AddView(Btn("Esquecer este computador", ForgetPc));

        root.AddView(Text(
            "Como funciona: com o celular pareado, o PC envia para cá cada pedido de acesso remoto e você aprova ou nega. " +
            "Para funcionar fora de casa, instale o Tailscale no PC, no notebook e neste celular, com a mesma conta, " +
            "e use o endereço 100.x.x.x do PC.", 13, Muted));

        var scroll = new ScrollView(this);
        scroll.AddView(root);
        SetContentView(scroll);
    }

    private void Refresh()
    {
        _status.Text = PhoneState.Status;
        _status.SetTextColor(PhoneState.Connected ? Color.Rgb(16, 124, 16) : Muted);

        // Pedidos pendentes
        _pending.RemoveAllViews();
        foreach (var r in PhoneState.Pending)
        {
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            card.SetBackgroundColor(Color.Rgb(255, 244, 214));
            card.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8), BottomMargin = Dp(8) };
            card.LayoutParameters = lp;
            card.AddView(Text($"{r.DeviceName} quer acessar {_store.PcName}", 17, Color.Black, bold: true));
            card.AddView(Text($"ID do aparelho: {r.DeviceId}. Permita só se foi você.", 13, Muted));
            string id = r.Id;
            card.AddView(Btn("Permitir", () => Respond(id, true), primary: true));
            card.AddView(Btn("Negar", () => Respond(id, false)));
            _pending.AddView(card);
        }

        // Código de autorização
        if (_store.TotpSecret is { } secret)
        {
            try
            {
                string c = Totp.FromBase32(secret).Current();
                _code.Text = $"{c[..3]} {c[3..]}";
                _codeInfo.Text = $"muda em {Totp.SecondsLeft} s";
            }
            catch
            {
                _code.Text = "— — —";
            }
        }
        else
        {
            _code.Text = "— — —";
            _codeInfo.Text = "aparece depois de conectar ao computador pareado";
        }

        _pcInfo.Text = _store.HasPc
            ? $"{_store.PcName} (ID {DeviceIdentity.FormatShortId(_store.PcFingerprint!)})\nEndereço: {_store.PcAddress}"
            : "Nenhum computador pareado.";
    }

    private void Respond(string id, bool approve)
    {
        Task.Run(() =>
        {
            bool ok = PhoneState.Respond(id, approve);
            Notifications.CancelRequest(this, id);
            if (!ok) RunOnUiThread(() => Toast.MakeText(this, "Sem conexão com o computador.", ToastLength.Long)!.Show());
        });
    }

    // ------------------------------------------------------------------ pareamento

    private async void StartPairing()
    {
        if (_busy) return;
        _busy = true;
        var progress = Toast.MakeText(this, "Procurando computadores na rede…", ToastLength.Short)!;
        progress.Show();
        List<ReceiverInfo> found;
        try
        {
            found = (await DiscoveryScanner.ScanAsync(Proto.DiscoveryPort, TimeSpan.FromSeconds(2), includeLoopback: false, CancellationToken.None))
                .Where(r => r.ProtocolVersion >= Proto.Version).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Busca na rede falhou", ex);
            found = new List<ReceiverInfo>();
        }
        finally
        {
            _busy = false;
        }

        var labels = found.Select(f => $"{f.Name}  (ID {f.Id})\n{f.Address}").ToList();
        labels.Add("Digitar o endereço…");
        new AlertDialog.Builder(this)
            .SetTitle(found.Count == 0 ? "Nenhum computador encontrado na rede" : "Escolha o computador")!
            .SetItems(labels.ToArray(), (_, e) =>
            {
                if (e.Which < found.Count) PairWith(found[e.Which].Address.ToString());
                else AskAddress("Endereço do computador", "Endereço (ex.: 192.168.1.18 ou 100.101.102.103)", PairWith);
            })!
            .SetNegativeButton("Cancelar", (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    private void AskAddress(string title, string hint, Action<string> onOk)
    {
        var input = new EditText(this) { Hint = hint, Text = _store.PcAddress ?? "" };
        input.SetSingleLine(true);
        input.InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationUri;
        new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetView(input)!
            .SetPositiveButton("OK", (_, _) =>
            {
                var a = input.Text?.Trim() ?? "";
                if (a.Length > 0) onOk(a);
            })!
            .SetNegativeButton("Cancelar", (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    /// <summary>Pareia (código igual nas duas telas) e passa a usar este computador.</summary>
    private void PairWith(string address)
    {
        if (_busy) return;
        _busy = true;
        Toast.MakeText(this, $"Conectando a {address}…", ToastLength.Short)!.Show();
        Task.Run(() =>
        {
            try
            {
                var temp = new PhoneStore(this);
                var identity = temp.LoadIdentity();
                string host = address;
                int port = Proto.VideoPort;
                int colon = address.LastIndexOf(':');
                if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out var p))
                {
                    host = address[..colon];
                    port = p;
                }
                using var client = new TcpClient(AddressFamily.InterNetwork);
                if (!client.ConnectAsync(host, port).Wait(8000)) throw new IOException($"Sem resposta de {address}.");

                // Durante o pareamento confiamos em qualquer chave; a confirmação do código decide.
                var pairing = new PairingTrust(temp);
                var (conn, peer) = SecureChannel.Connect(client, ChannelPurpose.Approver, new SecureChannelOptions
                {
                    Identity = identity,
                    Trust = pairing,
                    LocalName = temp.DeviceName,
                    LocalKind = "phone",
                    ConfirmPairing = AskPairingCode,
                });
                using (conn)
                {
                    var st = conn.ReceiveJson<ApproverStatusMessage>(MsgType.ApproverStatus);
                    temp.PcFingerprint = peer.Fingerprint;
                    temp.PcName = peer.Name;
                    temp.PcAddress = address;
                    if (!string.IsNullOrEmpty(st.TotpSecret)) temp.TotpSecret = st.TotpSecret;
                    conn.TrySendBye();
                }
                RunOnUiThread(() =>
                {
                    Toast.MakeText(this, $"Pareado com {peer.Name}!", ToastLength.Long)!.Show();
                    ApproverService.Start(this);
                    Refresh();
                });
            }
            catch (Exception ex)
            {
                string why = ex is AggregateException ae ? ae.InnerException?.Message ?? ex.Message : ex.Message;
                Log.Warn("Pareamento falhou", ex);
                RunOnUiThread(() => new AlertDialog.Builder(this).SetTitle("Não foi possível parear")!.SetMessage(why)!
                    .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!.Show());
            }
            finally
            {
                _busy = false;
            }
        });
    }

    /// <summary>Mostra o código e espera a confirmação (chamado fora da thread da interface).</summary>
    private bool AskPairingCode(PairingPrompt p)
    {
        var tcs = new TaskCompletionSource<bool>();
        RunOnUiThread(() => new AlertDialog.Builder(this)
            .SetTitle($"Parear com {p.PeerName}?")!
            .SetMessage($"Código:\n\n{p.Code}\n\nConfira se o MESMO código aparece na tela do computador e confirme lá também. " +
                        "Se for diferente, cancele.")!
            .SetCancelable(false)!
            .SetPositiveButton("Os códigos são iguais", (_, _) => tcs.TrySetResult(true))!
            .SetNegativeButton("Cancelar", (_, _) => tcs.TrySetResult(false))!
            .Show());
        return tcs.Task.Wait(TimeSpan.FromMinutes(2)) && tcs.Task.Result;
    }

    /// <summary>Durante o pareamento ainda não há computador salvo: nada é confiável até o código ser confirmado.</summary>
    private sealed class PairingTrust(PhoneStore store) : ITrustStore
    {
        public bool IsTrusted(string fingerprint) => false;
        public void Trust(string name, string fingerprint, string kind) => store.Trust(name, fingerprint, kind);
    }

    // ------------------------------------------------------------------ outras ações

    private void ChangeAddress()
    {
        if (!_store.HasPc)
        {
            StartPairing();
            return;
        }
        AskAddress("Endereço do computador", "ex.: 100.101.102.103 (Tailscale)", a =>
        {
            _store.PcAddress = a;
            PhoneState.Kick.Set();
            Refresh();
        });
    }

    private void AskBatteryExemption()
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                global::Android.Net.Uri.Parse("package:" + PackageName));
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            Log.Warn("Configuração de bateria indisponível", ex);
            StartActivity(new Intent(global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings));
        }
    }

    private void ForgetPc()
    {
        if (!_store.HasPc) return;
        new AlertDialog.Builder(this)
            .SetTitle("Esquecer o computador?")!
            .SetMessage($"O celular deixará de receber os pedidos de acesso de {_store.PcName}. No PC, remova também o pareamento do celular.")!
            .SetPositiveButton("Esquecer", (_, _) =>
            {
                _store.Forget();
                ApproverService.Stop(this);
                Refresh();
            })!
            .SetNegativeButton("Cancelar", (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }
}
