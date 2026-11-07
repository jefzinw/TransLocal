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

[Activity(Label = "@string/app_name", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar",
    LaunchMode = global::Android.Content.PM.LaunchMode.SingleTop)]
public class MainActivity : Activity
{
    private PhoneStore _store = null!;
    private TextView _status = null!;
    private View _statusDot = null!;
    private LinearLayout _pending = null!;
    private CodeRing _ring = null!;
    private TextView _codeInfo = null!;
    private TextView _pcName = null!;
    private TextView _pcId = null!;
    private TextView _pcAddress = null!;
    private TextView _pcLink = null!;
    private TextView _pendingCount = null!;
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

    private int Dp(int v) => Ui.Dp(this, v);

    private LinearLayout Row(params View[] views)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(12) };
        for (int i = 0; i < views.Length; i++)
            row.AddView(views[i], new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = i == 0 ? 0 : Dp(8) });
        return row;
    }

    private static LinearLayout CardHeader(Activity a, string title, View? right = null)
    {
        var row = new LinearLayout(a) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.AddView(Ui.Label(a, title, 17, Ui.Text, bold: true), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        if (right is not null) row.AddView(right);
        return row;
    }

    private void BuildUi()
    {
        Window!.SetStatusBarColor(Ui.Background);
        Window.SetNavigationBarColor(Ui.Background);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(18), Dp(18), Dp(18), Dp(28));
        root.SetBackgroundColor(Ui.Background);

        // Topo: logotipo + nome, selo de conexão à direita.
        var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);
        var logo = new FrameLayout(this) { Background = Ui.Oval(Color.Rgb(246, 246, 248)) };
        var glyph = Ui.Label(this, "▭", 18, Color.Rgb(18, 18, 20), bold: true);
        glyph.Gravity = GravityFlags.Center;
        logo.AddView(glyph, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        logo.AddView(new View(this) { Background = Ui.Oval(Ui.Accent) }, new FrameLayout.LayoutParams(Dp(10), Dp(10)) { Gravity = GravityFlags.Top | GravityFlags.End, TopMargin = Dp(2), RightMargin = Dp(2) });
        top.AddView(logo, new LinearLayout.LayoutParams(Dp(40), Dp(40)) { RightMargin = Dp(10) });
        top.AddView(Ui.Label(this, "TransLocal", 19, Ui.Text, bold: true), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var chip = Ui.Chip(this, out _statusDot, out _status);
        top.AddView(chip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        root.AddView(top);

        var title = Ui.Label(this, "Aprovações", 30, Ui.Text, light: true);
        title.SetPadding(0, Dp(22), 0, 0);
        root.AddView(title);
        root.AddView(Ui.Label(this, "Autorize quem pede a tela do seu computador.", 14, Ui.Muted));

        // Pedidos pendentes (aparecem em destaque laranja).
        _pending = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.AddView(_pending);

        // Código de autorização no anel.
        var codeCard = Ui.Card(this);
        _codeInfo = Ui.Label(this, "", 12, Ui.Text, bold: true);
        _codeInfo.Background = Ui.Rounded(this, Color.Rgb(30, 30, 34), 999, Ui.Border);
        _codeInfo.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        codeCard.AddView(CardHeader(this, "Código de autorização", _codeInfo));
        _ring = new CodeRing(this);
        codeCard.AddView(_ring, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(14), BottomMargin = Dp(6) });
        var codeHelp = Ui.Label(this, "Para entrar sem esperar a aprovação, digite este código no aparelho que pede acesso. Ele muda a cada 30 s.", 13, Ui.Muted);
        codeHelp.Gravity = GravityFlags.CenterHorizontal;
        codeCard.AddView(codeHelp);
        root.AddView(codeCard);

        // Computador pareado: blocos de informação.
        var pcCard = Ui.Card(this);
        _pendingCount = Ui.Label(this, "", 12, Ui.Text, bold: true);
        _pendingCount.Background = Ui.Rounded(this, Color.Rgb(30, 30, 34), 999, Ui.Border);
        _pendingCount.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        pcCard.AddView(CardHeader(this, "Computador", _pendingCount));
        _pcName = Ui.Label(this, "", 24, Ui.Text, light: true);
        _pcName.SetPadding(0, Dp(8), 0, 0);
        pcCard.AddView(_pcName);
        pcCard.AddView(Row(Ui.Tile(this, "Conexão", out _pcLink, Ui.Accent), Ui.Tile(this, "ID", out _pcId, Ui.Good)));
        var addrTile = Ui.Tile(this, "Endereço", out _pcAddress, Ui.Accent2);
        addrTile.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) };
        pcCard.AddView(addrTile);
        pcCard.AddView(Ui.Pill(this, "Parear com um computador", StartPairing, primary: true));
        pcCard.AddView(Row(Ui.Pill(this, "Alterar endereço", ChangeAddress), Ui.Pill(this, "Esquecer", ForgetPc)));
        pcCard.AddView(Ui.Pill(this, "Permitir funcionar em segundo plano", AskBatteryExemption));
        root.AddView(pcCard);

        var help = Ui.Card(this);
        help.AddView(CardHeader(this, "Como funciona"));
        var how = Ui.Label(this,
            "Com o celular pareado, o PC envia para cá cada pedido de acesso remoto e você aprova ou nega. " +
            "Para funcionar fora de casa, instale o Tailscale no PC, no notebook e neste celular, com a mesma conta, " +
            "e use o endereço 100.x.x.x do PC.", 13.5f, Ui.Muted);
        how.SetPadding(0, Dp(6), 0, 0);
        help.AddView(how);
        root.AddView(help);

        var scroll = new ScrollView(this) { FillViewport = true };
        scroll.SetBackgroundColor(Ui.Background);
        scroll.AddView(root);
        SetContentView(scroll);
    }

    private void Refresh()
    {
        _status.Text = PhoneState.Connected ? "Conectado" : "Desconectado";
        _statusDot.Background = Ui.Oval(PhoneState.Connected ? Ui.Good : Ui.Muted);

        // Pedidos pendentes
        _pending.RemoveAllViews();
        foreach (var r in PhoneState.Pending)
        {
            var card = Ui.Card(this, highlight: true);
            var badge = Ui.Label(this, "Pedido de acesso", 12, Color.White, bold: true);
            badge.Background = Ui.Gradient(this, 999, Ui.Accent, Ui.AccentLight);
            badge.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
            card.AddView(badge, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            var who = Ui.Label(this, $"{r.DeviceName} quer acessar {_store.PcName}", 19, Ui.Text, bold: true);
            who.SetPadding(0, Dp(10), 0, Dp(2));
            card.AddView(who);
            card.AddView(Ui.Label(this, $"ID do aparelho: {r.DeviceId}. Permita só se foi você.", 13, Ui.Muted));
            string id = r.Id;
            card.AddView(Row(Ui.Pill(this, "Negar", () => Respond(id, false)), Ui.Pill(this, "Permitir", () => Respond(id, true), primary: true)));
            _pending.AddView(card);
        }

        // Código de autorização
        if (_store.TotpSecret is { } secret)
        {
            try
            {
                string c = Totp.FromBase32(secret).Current();
                _ring.Code = $"{c[..3]} {c[3..]}";
                int left = Totp.SecondsLeft;
                _ring.Caption = $"muda em {left} s";
                _ring.Fraction = left / 30f;
                _codeInfo.Text = $"{left} s";
            }
            catch
            {
                _ring.Code = "— — —";
            }
        }
        else
        {
            _ring.Code = "— — —";
            _ring.Caption = "conecte ao computador pareado";
            _ring.Fraction = 0;
            _codeInfo.Text = "aguardando";
        }
        _ring.Invalidate();

        int pending = PhoneState.Pending.Count;
        _pendingCount.Text = pending == 0 ? "nenhum pedido" : $"{pending} pedido(s)";
        if (_store.HasPc)
        {
            _pcName.Text = _store.PcName;
            _pcId.Text = DeviceIdentity.FormatShortId(_store.PcFingerprint!);
            _pcAddress.Text = _store.PcAddress ?? "—";
            _pcLink.Text = PhoneState.Connected ? "Ativa" : "Sem sinal";
        }
        else
        {
            _pcName.Text = "Nenhum computador pareado";
            _pcId.Text = _pcAddress.Text = _pcLink.Text = "—";
        }
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
        new AlertDialog.Builder(this, global::Android.Resource.Style.ThemeMaterialDialogAlert)
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
        new AlertDialog.Builder(this, global::Android.Resource.Style.ThemeMaterialDialogAlert)
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
                RunOnUiThread(() => new AlertDialog.Builder(this, global::Android.Resource.Style.ThemeMaterialDialogAlert).SetTitle("Não foi possível parear")!.SetMessage(why)!
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
        RunOnUiThread(() => new AlertDialog.Builder(this, global::Android.Resource.Style.ThemeMaterialDialogAlert)
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
        new AlertDialog.Builder(this, global::Android.Resource.Style.ThemeMaterialDialogAlert)
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
