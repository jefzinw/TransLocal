using QRCoder;
using TransLocal.Security;

namespace TransLocal.UI;

/// <summary>Aba “Acesso remoto” e lista de aparelhos pareados.</summary>
internal sealed partial class MainForm
{
    private readonly PagePanel _tabRemote = new("Acesso remoto");
    private readonly CheckBox _chkRemote = new ToggleCheck();
    private readonly Label _remoteStatus = new();
    private readonly Label _tailscale = new();
    private readonly PictureBox _qr = new();
    private readonly Label _qrInfo = new();
    private readonly Button _btnShowQr = new PillButton();
    private readonly Button _btnNewSecret = new PillButton();
    private readonly ComboBox _cmbRemotePc = new();
    private readonly TextBox _txtRemoteAddress = new();
    private readonly TextBox _txtRemoteCode = new();
    private readonly Button _btnRequest = new PillButton();
    private readonly Label _requestStatus = new();
    private readonly ListView _lvPaired = new();
    private readonly Button _btnUnpair = new PillButton();
    private readonly Label _myId = new();
    private string _pairedKey = "";

    private void BuildRemoteTab()
    {
        var t = NewPage(_tabRemote);

        Add(t, Heading("Acessar este computador de fora de casa"));
        SetupCheck(_chkRemote, "Permitir acesso remoto a este computador (só aparelhos pareados; cada acesso precisa ser autorizado)");
        _chkRemote.CheckedChanged += (_, _) =>
        {
            if (!_loading) _app.SetRemoteAccessEnabled(_chkRemote.Checked);
        };
        Add(t, _chkRemote);
        SetupStatus(_remoteStatus);
        Add(t, _remoteStatus);
        Add(t, Note("Quando um aparelho pareado pede a tela, o acesso só começa depois de autorizado por um destes meios:\n" +
                    "• no celular, pelo app TransLocal (notificação “Permitir / Negar”);\n" +
                    "• na tela deste computador, se houver alguém nele;\n" +
                    "• digitando no aparelho que pede o código de 6 dígitos que muda a cada 30 s (mostrado no app do celular ou em qualquer app autenticador)."));

        SetupButton(_btnShowQr, "Cadastrar o código num app autenticador (QR)");
        _btnShowQr.Click += (_, _) => ShowTotpQr();
        SetupButton(_btnNewSecret, "Gerar novo código secreto");
        _btnNewSecret.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Os códigos cadastrados antes (app do celular ou autenticador) deixarão de valer. Continuar?",
                    "Código de autorização", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _app.ResetRemoteTotp();
            if (_qr.Visible) ShowTotpQr();
        };
        Add(t, Row(_btnShowQr, _btnNewSecret));
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.Size = new Size(180, 180);
        _qr.Visible = false;
        _qr.Margin = new Padding(0, 4, 12, 4);
        _qrInfo.AutoSize = true;
        _qrInfo.Visible = false;
        _qrInfo.MaximumSize = new Size(420, 0);
        _qrInfo.Margin = new Padding(0, 8, 0, 0);
        Add(t, Row(_qr, _qrInfo));

        Add(t, Heading("Pedir a tela de um computador pareado"));
        SetupCombo(_cmbRemotePc, 320);
        _cmbRemotePc.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbRemotePc.SelectedItem is ComboItem<TrustedDevice> it && !string.IsNullOrEmpty(it.Value.LastAddress))
                _txtRemoteAddress.Text = it.Value.LastAddress;
        };
        Add(t, Row(Caption("Computador:"), _cmbRemotePc));
        _txtRemoteAddress.Width = 230;
        _txtRemoteAddress.PlaceholderText = "ex.: pc-de-casa ou 100.101.102.103";
        _txtRemoteAddress.Margin = new Padding(0, 3, 12, 3);
        _txtRemoteCode.Width = 80;
        _txtRemoteCode.MaxLength = 7;
        _txtRemoteCode.PlaceholderText = "opcional";
        _txtRemoteCode.Margin = new Padding(0, 3, 12, 3);
        Add(t, Row(Caption("Endereço:"), _txtRemoteAddress, Caption("Código:"), _txtRemoteCode));
        SetupButton(_btnRequest, "Pedir a tela", primary: true);
        _btnRequest.Click += async (_, _) => await RequestRemoteAsync();
        Add(t, Row(_btnRequest));
        SetupStatus(_requestStatus);
        Add(t, _requestStatus);
        Add(t, Note("Sem código, o pedido aparece no celular (app TransLocal) e na tela do outro computador para ser aprovado. " +
                    "Com o código do celular, o acesso começa direto."));

        Add(t, Heading("Como alcançar o computador pela internet"));
        Add(t, Note("O TransLocal não expõe o computador na internet. Para conectar de outra rede, use o Tailscale (gratuito): instale-o no " +
                    "PC, no notebook e no celular, entrando com a mesma conta. Cada aparelho ganha um endereço fixo (100.x.x.x) que só os seus " +
                    "aparelhos enxergam, de qualquer lugar. Depois digite esse endereço (ou o nome do PC no Tailscale) acima. " +
                    "Não é preciso abrir portas no roteador."));
        _tailscale.AutoSize = true;
        _tailscale.Font = new Font("Consolas", 9.75F);
        _tailscale.Margin = new Padding(12, 0, 0, 6);
        Add(t, _tailscale);
        var btnTs = new PillButton();
        SetupButton(btnTs, "Abrir o site do Tailscale");
        btnTs.Click += (_, _) => SystemIntegration.OpenUrl("https://tailscale.com/download");
        Add(t, Row(btnTs));

        Add(t, Heading("App para celular (Android)"));
        Add(t, Note("Baixe o TransLocal-Android.apk na página de versões e instale no celular (permita “instalar apps desconhecidos” " +
                    "quando o Android pedir). Com o celular na mesma rede do PC (ou no Tailscale), abra o app, toque em “Parear com um computador”, " +
                    "escolha este PC e confirme o mesmo código nas duas telas. O app avisa os pedidos de acesso e mostra o código de autorização."));
        var btnApk = new PillButton();
        SetupButton(btnApk, "Abrir página de versões");
        btnApk.Click += (_, _) => SystemIntegration.OpenUrl(Updater.ReleasesPage);
        Add(t, Row(btnApk));
        Finish(t);
    }

    private void BuildPairedDevicesSection(TableLayoutPanel t)
    {
        Add(t, Heading("Aparelhos pareados"));
        _myId.AutoSize = true;
        _myId.Margin = new Padding(0, 0, 0, 4);
        _myId.Text = $"ID deste aparelho: {_app.Identity.ShortId}";
        Add(t, _myId);
        _lvPaired.View = View.Details;
        _lvPaired.FullRowSelect = true;
        _lvPaired.MultiSelect = false;
        _lvPaired.HideSelection = false;
        _lvPaired.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _lvPaired.Height = 100;
        _lvPaired.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lvPaired.Columns.Add("Aparelho", 170);
        _lvPaired.Columns.Add("Tipo", 80);
        _lvPaired.Columns.Add("ID", 90);
        _lvPaired.Columns.Add("Último endereço", 150);
        _lvPaired.Columns.Add("Visto por último", 130);
        _lvPaired.SelectedIndexChanged += (_, _) => _btnUnpair.Enabled = _lvPaired.SelectedItems.Count == 1;
        Add(t, _lvPaired);
        SetupButton(_btnUnpair, "Remover pareamento");
        _btnUnpair.Enabled = false;
        _btnUnpair.Click += (_, _) =>
        {
            if (_lvPaired.SelectedItems.Count != 1 || _lvPaired.SelectedItems[0].Tag is not TrustedDevice d) return;
            if (MessageBox.Show(this, $"Remover o pareamento com “{d.Name}”? Ele não poderá mais se conectar a este aparelho até ser pareado de novo.",
                    "Aparelhos pareados", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _app.Trust.Remove(d.Fingerprint);
        };
        Add(t, Row(_btnUnpair));
        Add(t, Note("Só aparelhos desta lista conseguem transmitir para cá, receber a tela daqui ou pedir acesso remoto. " +
                    "O primeiro contato entre dois aparelhos sempre pede a confirmação de um código igual nas duas telas."));
    }

    private void RefreshRemoteState()
    {
        _loading = true;
        _chkRemote.Checked = _app.Settings.RemoteAccessEnabled;
        _loading = false;

        var phones = _app.Approvals.ConnectedNames;
        if (!_app.Settings.RemoteAccessEnabled)
        {
            _remoteStatus.Text = "Acesso remoto desligado.";
            _remoteStatus.ForeColor = Muted;
        }
        else
        {
            _remoteStatus.Text = phones.Count == 0
                ? "Ligado. Nenhum celular conectado para aprovar agora — aprove na tela do PC ou use o código de autorização."
                : $"Ligado. Celular(es) prontos para aprovar: {string.Join(", ", phones)}.";
            _remoteStatus.ForeColor = Good;
        }

        var devices = _app.Trust.All();
        string key = string.Join("|", devices.Select(d => $"{d.Fingerprint}:{d.Name}:{d.LastAddress}:{d.LastSeenUtc:yyyyMMddHHmm}"));
        if (key == _pairedKey) return;
        _pairedKey = key;

        _lvPaired.BeginUpdate();
        _lvPaired.Items.Clear();
        foreach (var d in devices.OrderByDescending(d => d.LastSeenUtc))
        {
            _lvPaired.Items.Add(new ListViewItem(new[]
            {
                d.Name, d.Kind == "phone" ? "celular" : "computador", d.ShortId, d.LastAddress ?? "—",
                d.LastSeenUtc == default ? "—" : d.LastSeenUtc.ToLocalTime().ToString("dd/MM HH:mm"),
            }) { Tag = d });
        }
        _lvPaired.EndUpdate();
        _btnUnpair.Enabled = false;

        var selected = (_cmbRemotePc.SelectedItem as ComboItem<TrustedDevice>)?.Value.Fingerprint;
        _cmbRemotePc.Items.Clear();
        foreach (var d in devices.Where(d => d.Kind != "phone").OrderByDescending(d => d.LastSeenUtc))
            _cmbRemotePc.Items.Add(new ComboItem<TrustedDevice>($"{d.Name} ({d.ShortId})", d));
        int idx = _cmbRemotePc.Items.Cast<ComboItem<TrustedDevice>>().ToList().FindIndex(i => i.Value.Fingerprint == selected);
        if (idx >= 0) _cmbRemotePc.SelectedIndex = idx;
    }

    private void RefreshTailscale()
    {
        var ts = NetInfo.GetLocalAddresses().Where(a => a.Kind.StartsWith("Tailscale") || a.Kind.StartsWith("ZeroTier")).ToList();
        _tailscale.Text = ts.Count == 0
            ? "Tailscale não detectado neste computador."
            : "Endereço deste computador no Tailscale: " + string.Join(", ", ts.Select(a => a.Address.ToString()));
    }

    private void ShowTotpQr()
    {
        var totp = _app.RemoteTotp;
        string uri = totp.OtpAuthUri(_app.Settings.DeviceName);
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(6);
        using (var ms = new MemoryStream(png))
        {
            _qr.Image?.Dispose();
            _qr.Image = new Bitmap(Image.FromStream(ms));
        }
        _qr.Visible = true;
        _qrInfo.Text = "Leia com o app do celular (Google Authenticator, Microsoft Authenticator…) para ter o código de autorização " +
                       $"sem o app TransLocal. Chave manual: {totp.Base32Secret}\n\nNão mostre este QR a ninguém: ele autoriza o acesso a este computador.";
        _qrInfo.Visible = true;
    }

    private async Task RequestRemoteAsync()
    {
        string address = _txtRemoteAddress.Text.Trim();
        if (address.Length == 0 && _cmbRemotePc.SelectedItem is ComboItem<TrustedDevice> it) address = it.Value.LastAddress ?? "";
        if (address.Length == 0)
        {
            MessageBox.Show(this, "Digite o endereço do computador (ex.: o endereço dele no Tailscale).", "Acesso remoto",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string? code = _txtRemoteCode.Text.Trim();
        if (code.Length == 0) code = null;
        _btnRequest.Enabled = false;
        _requestStatus.ForeColor = Accent;
        try
        {
            await _app.RequestRemoteScreenAsync(address, code, s => _requestStatus.Text = s);
            _txtRemoteCode.Text = "";
        }
        catch (Exception ex)
        {
            _requestStatus.Text = ex.Message;
            _requestStatus.ForeColor = Bad;
        }
        finally
        {
            _btnRequest.Enabled = true;
        }
    }
}
