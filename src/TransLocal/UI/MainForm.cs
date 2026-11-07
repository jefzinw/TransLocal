using TransLocal.Audio;
using TransLocal.Video;

namespace TransLocal.UI;

internal sealed class ComboItem<T>
{
    public ComboItem(string text, T value)
    {
        Text = text;
        Value = value;
    }

    public string Text { get; }
    public T Value { get; }
    public override string ToString() => Text;
}

internal sealed partial class MainForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 103, 192);
    private static readonly Color Muted = Color.FromArgb(96, 96, 96);
    private static readonly Color Good = Color.FromArgb(16, 124, 16);

    private readonly TrayApp _app;
    private bool _loading;
    private bool _scanning;
    private bool _scannedOnce;

    private readonly TabControl _tabs = new();
    private readonly TabPage _tabReceive = new("Receber nesta tela");
    private readonly TabPage _tabSend = new("Transmitir esta tela");
    private readonly TabPage _tabHdmi = new("HDMI (placa de captura)");
    private readonly TabPage _tabSettings = new("Configurações e ajuda");

    // Receber
    private readonly Label _recvStatus = new();
    private readonly CheckBox _chkReceive = new();
    private readonly Label _addresses = new();
    private readonly ComboBox _cmbViewerMonitor = new();
    private readonly CheckBox _chkFullscreen = new();
    private readonly CheckBox _chkPlayAudio = new();
    private readonly CheckBox _chkStats = new();
    private readonly CheckBox _chkAutoControl = new();
    private readonly Label _padsHere = new();
    private readonly ListBox _lstPads = new();
    private readonly Button _btnPadSetup = new();
    private readonly CheckBox _chkLight = new();
    private readonly ComboBox _cmbResolution = new();
    private readonly System.Windows.Forms.Timer _padsTimer = new() { Interval = 1500 };

    // Transmitir
    private readonly Button _btnScan = new();
    private readonly Label _scanStatus = new();
    private readonly ListView _lvReceivers = new();
    private readonly TextBox _txtAddress = new();
    private readonly ComboBox _cmbSendMonitor = new();
    private readonly ComboBox _cmbFps = new();
    private readonly ComboBox _cmbQuality = new();
    private readonly CheckBox _chkSendAudio = new();
    private readonly CheckBox _chkAllowControl = new();
    private readonly CheckBox _chkElevated = new();
    private readonly Label _elevatedStatus = new();
    private readonly CheckBox _chkShareDiag = new();
    private bool _elevatedBusy;
    private readonly Label _gpStatus = new();
    private readonly Button _btnGpInstall = new();
    private readonly Button _btnSend = new();
    private readonly Label _sendStatus = new();

    // HDMI
    private readonly ComboBox _cmbHdmiDevice = new();
    private readonly Button _btnHdmiRefresh = new();
    private readonly ComboBox _cmbHdmiFormat = new();
    private readonly ComboBox _cmbHdmiAudio = new();
    private readonly CheckBox _chkHdmiFullscreen = new();
    private readonly Button _btnHdmiOpen = new();
    private readonly Label _hdmiStatus = new();

    // Configurações
    private readonly TextBox _txtName = new();
    private readonly CheckBox _chkAutoUpdate = new();
    private readonly Button _btnCheckUpdate = new();
    private readonly Label _updateStatus = new();
    private readonly CheckBox _chkAutostart = new();
    private readonly Label _fwStatus = new();
    private readonly Button _btnFirewall = new();

    public MainForm(TrayApp app)
    {
        _app = app;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = "TransLocal — transmissão de tela por cabo";
        Icon = SystemIntegration.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 640);
        MinimumSize = new Size(640, 520);

        var header = BuildHeader();
        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(12, 6);
        _tabs.TabPages.AddRange(new[] { _tabReceive, _tabSend, _tabRemote, _tabHdmi, _tabSettings });
        _tabs.SelectedIndexChanged += (_, _) => OnTabChanged();

        BuildReceiveTab();
        BuildSendTab();
        BuildHdmiTab();
        BuildRemoteTab();
        BuildSettingsTab();

        Controls.Add(_tabs);
        Controls.Add(header);
        ResumeLayout(false);
        PerformLayout();

        LoadFromSettings();
        _app.StateChanged += RefreshState;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        RefreshState();
    }

    /// <summary>Mostrar sem tirar o foco de outra janela (usado pelo modo --ui-screenshot).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool NoActivate { get; set; }

    protected override bool ShowWithoutActivation => NoActivate;

    public void SelectTab(string name)
    {
        _tabs.SelectedTab = name switch
        {
            "send" => _tabSend,
            "hdmi" => _tabHdmi,
            "settings" => _tabSettings,
            "remote" => _tabRemote,
            _ => _tabReceive,
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Fechar a janela só esconde: o TransLocal continua na área de notificação.
        if (e.CloseReason == CloseReason.UserClosing && !_app.IsExiting)
        {
            e.Cancel = true;
            Hide();
            _app.NotifyHiddenOnce();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _padsTimer.Stop();
        else if (_tabs.SelectedTab == _tabReceive) _padsTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _padsTimer.Dispose();
        _app.StateChanged -= RefreshState;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        base.OnFormClosed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        OnTabChanged();
    }

    // ================= Construção da interface =================

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Accent, Padding = new Padding(16, 8, 16, 8) };
        var title = new Label
        {
            Text = "TransLocal",
            Font = new Font("Segoe UI Semibold", 16F),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(14, 6),
        };
        var sub = new Label
        {
            Text = "Veja e use a tela de um computador em outro — por cabo de rede, USB4/Thunderbolt, cabo bridge USB ou placa de captura HDMI.",
            ForeColor = Color.FromArgb(225, 236, 250),
            AutoSize = true,
            Location = new Point(16, 38),
        };
        panel.Controls.Add(title);
        panel.Controls.Add(sub);
        return panel;
    }

    private static TableLayoutPanel NewPage(TabPage page)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(14, 12, 14, 12),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(t);
        page.UseVisualStyleBackColor = true;
        return t;
    }

    private static void Add(TableLayoutPanel t, Control c)
    {
        t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(c, 0, t.RowCount - 1);
    }

    /// <summary>Linha final vazia que absorve a sobra de altura, mantendo o conteúdo no topo.</summary>
    private static void Finish(TableLayoutPanel t)
    {
        t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    }

    private static void SetupStatus(Label l)
    {
        l.AutoSize = true;
        l.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        l.Margin = new Padding(0, 2, 0, 6);
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 11F),
        AutoSize = true,
        Margin = new Padding(0, 6, 0, 6),
    };

    private static Label Note(string text) => new()
    {
        Text = text,
        ForeColor = Muted,
        AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 4, 0, 6),
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0, 2, 0, 2),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        f.Controls.AddRange(controls);
        return f;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 7, 6, 0) };

    private static void SetupCheck(CheckBox c, string text)
    {
        c.Text = text;
        c.AutoSize = true;
        c.Margin = new Padding(0, 3, 0, 3);
    }

    private static void SetupCombo(ComboBox c, int width)
    {
        c.DropDownStyle = ComboBoxStyle.DropDownList;
        c.Width = width;
        c.Margin = new Padding(0, 3, 12, 3);
    }

    private static void SetupButton(Button b, string text, bool primary = false)
    {
        b.Text = text;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(10, 3, 10, 3);
        b.Margin = new Padding(0, 3, 10, 3);
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.Font = new Font("Segoe UI Semibold", 9.75F);
        }
    }

    private void BuildReceiveTab()
    {
        var t = NewPage(_tabReceive);
        Add(t, Heading("Mostrar neste computador a tela de outro"));

        _recvStatus.AutoSize = true;
        _recvStatus.Font = new Font("Segoe UI Semibold", 10.5F);
        _recvStatus.Margin = new Padding(0, 0, 0, 8);
        Add(t, _recvStatus);

        SetupCheck(_chkReceive, "Aceitar transmissões de outros computadores (fica aguardando em segundo plano)");
        _chkReceive.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _app.SetReceiverEnabled(_chkReceive.Checked);
        };
        Add(t, _chkReceive);

        Add(t, Note("Endereços deste computador — se ele não aparecer na busca do outro computador, digite um destes lá:"));
        _addresses.AutoSize = true;
        _addresses.Font = new Font("Consolas", 9.75F);
        _addresses.Margin = new Padding(12, 0, 0, 10);
        Add(t, _addresses);

        SetupCombo(_cmbViewerMonitor, 300);
        _cmbViewerMonitor.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _cmbViewerMonitor.SelectedItem is not ComboItem<string> it) return;
            _app.Settings.ViewerMonitor = it.Value;
            _app.Settings.Save();
        };
        Add(t, Row(Caption("Exibir no monitor:"), _cmbViewerMonitor));

        SetupCheck(_chkFullscreen, "Abrir em tela cheia");
        _chkFullscreen.CheckedChanged += (_, _) => SaveSetting(s => s.ViewerFullscreen = _chkFullscreen.Checked);
        Add(t, _chkFullscreen);

        SetupCheck(_chkPlayAudio, "Tocar o som recebido");
        _chkPlayAudio.CheckedChanged += (_, _) => SaveSetting(s => s.ReceiverPlayAudio = _chkPlayAudio.Checked);
        Add(t, _chkPlayAudio);

        SetupCheck(_chkStats, "Mostrar estatísticas na imagem (quadros/s, banda)");
        _chkStats.CheckedChanged += (_, _) => SaveSetting(s => s.ShowStats = _chkStats.Checked);
        Add(t, _chkStats);

        SetupCheck(_chkLight, $"Pedir transmissão leve — para computadores mais simples (até {LightMode.MaxWidth}×{LightMode.MaxHeight}, {LightMode.MaxFps} quadros/s, mais compressão)");
        _chkLight.CheckedChanged += (_, _) => SaveSetting(s => s.ReceiverLight = _chkLight.Checked);
        Add(t, _chkLight);
        Add(t, Note("A imagem já chega na resolução desta tela quando o outro computador usa “Resolução enviada: Automática” (padrão), " +
                    "o que deixa a exibição mais leve. O modo leve reduz também os quadros por segundo e a banda."));

        Add(t, Heading("Controlar o outro computador daqui"));
        SetupCheck(_chkAutoControl, "Começar já controlando quando o outro computador permitir (mouse, teclado e controles de jogo)");
        _chkAutoControl.CheckedChanged += (_, _) => SaveSetting(s => s.ViewerAutoControl = _chkAutoControl.Checked);
        Add(t, _chkAutoControl);
        SetupStatus(_padsHere);
        Add(t, _padsHere);
        _lstPads.Height = 64;
        _lstPads.IntegralHeight = false;
        _lstPads.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lstPads.Margin = new Padding(0, 0, 0, 4);
        _lstPads.SelectedIndexChanged += (_, _) => _btnPadSetup.Enabled = _lstPads.SelectedItem is ComboItem<Input.ControllerInfo>;
        Add(t, _lstPads);
        SetupButton(_btnPadSetup, "Configurar botões do controle selecionado…");
        _btnPadSetup.Enabled = false;
        _btnPadSetup.Click += (_, _) => OpenControllerSetup();
        Add(t, Row(_btnPadSetup));
        _padsTimer.Tick += (_, _) => RefreshPadsHere();
        Add(t, Note("Funcionam controles USB e Bluetooth: Xbox, PlayStation 4/5, Switch Pro e centenas de modelos genéricos, todos vistos no outro " +
                    "computador como controle de Xbox (com vibração). Se um controle aparecer como “não reconhecido” ou com botões trocados, " +
                    "selecione-o e clique em “Configurar botões”.\n" +
                    "O outro computador precisa marcar “Permitir que o outro computador use o mouse, o teclado e os controles” na aba Transmitir " +
                    "e instalar o suporte a controles (botão na mesma aba). Os controles só são enviados com a janela de exibição em primeiro plano."));

        Add(t, Note("Na janela de exibição: F11 alterna tela cheia • Esc sai da tela cheia • leve o mouse ao topo da tela para ver a barra de botões.\n" +
                    "Ctrl+Alt+Shift+Q fecha • Ctrl+Alt+Shift+C liga/desliga o controle do outro computador • Ctrl+Alt+Shift+F tela cheia."));
        Finish(t);
    }

    private void RefreshPadsHere()
    {
        var svc = Input.ControllerService.Instance;
        if (!svc.Available)
        {
            int n = Input.XInput.CountConnected();
            _padsHere.Text = $"Controles de jogo conectados aqui: {(n == 0 ? "nenhum" : n.ToString())} (somente XInput — {svc.Error})";
            _padsHere.ForeColor = n > 0 ? Good : Muted;
            _lstPads.Visible = _btnPadSetup.Visible = false;
            return;
        }

        var list = svc.Snapshot.Controllers;
        _padsHere.Text = list.Count == 0
            ? "Controles de jogo conectados aqui: nenhum — conecte por USB ou pareie por Bluetooth"
            : $"Controles de jogo conectados aqui: {list.Count}";
        _padsHere.ForeColor = list.Count > 0 ? Good : Muted;

        var items = list.Select(c => new ComboItem<Input.ControllerInfo>(c.Describe(), c)).ToList();
        bool same = items.Count == _lstPads.Items.Count &&
                    items.Select(i => i.Text).SequenceEqual(_lstPads.Items.Cast<ComboItem<Input.ControllerInfo>>().Select(i => i.Text));
        if (!same)
        {
            var selectedGuid = (_lstPads.SelectedItem as ComboItem<Input.ControllerInfo>)?.Value.Id;
            _lstPads.BeginUpdate();
            _lstPads.Items.Clear();
            foreach (var it in items) _lstPads.Items.Add(it);
            int idx = items.FindIndex(i => i.Value.Id == selectedGuid);
            if (idx < 0) idx = items.FindIndex(i => !i.Value.Mapped);
            if (idx >= 0) _lstPads.SelectedIndex = idx;
            _lstPads.EndUpdate();
        }
        _btnPadSetup.Enabled = _lstPads.SelectedItem is ComboItem<Input.ControllerInfo>;
    }

    private void OpenControllerSetup()
    {
        if (_lstPads.SelectedItem is not ComboItem<Input.ControllerInfo> item) return;
        var svc = Input.ControllerService.Instance;
        using var form = new ControllerSetupForm(svc, item.Value, mapping =>
        {
            var guid = item.Value.Guid;
            _app.Settings.ControllerMappings.RemoveAll(m => m.StartsWith(guid + ",", StringComparison.OrdinalIgnoreCase));
            _app.Settings.ControllerMappings.Add(mapping);
            _app.Settings.Save();
        });
        form.ShowDialog(this);
        RefreshPadsHere();
    }

    private void BuildSendTab()
    {
        var t = NewPage(_tabSend);
        Add(t, Heading("Enviar a tela deste computador para outro"));

        SetupButton(_btnScan, "Procurar receptores");
        _btnScan.Click += async (_, _) => await ScanAsync();
        Add(t, Row(_btnScan));
        SetupStatus(_scanStatus);
        _scanStatus.ForeColor = Muted;
        Add(t, _scanStatus);

        _lvReceivers.View = View.Details;
        _lvReceivers.FullRowSelect = true;
        _lvReceivers.MultiSelect = false;
        _lvReceivers.HideSelection = false;
        _lvReceivers.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _lvReceivers.Height = 120;
        _lvReceivers.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lvReceivers.Margin = new Padding(0, 4, 0, 6);
        _lvReceivers.Columns.Add("Computador", 220);
        _lvReceivers.Columns.Add("Endereço", 170);
        _lvReceivers.Columns.Add("Ligação", 260);
        _lvReceivers.SelectedIndexChanged += (_, _) =>
        {
            if (_lvReceivers.SelectedItems.Count == 1 && _lvReceivers.SelectedItems[0].Tag is ReceiverInfo r)
                _txtAddress.Text = r.Port == Proto.VideoPort ? r.Address.ToString() : r.Endpoint;
        };
        _lvReceivers.DoubleClick += async (_, _) => await ToggleSendAsync();
        Add(t, _lvReceivers);

        _txtAddress.Width = 220;
        _txtAddress.PlaceholderText = "ex.: 169.254.10.20";
        _txtAddress.Margin = new Padding(0, 3, 12, 3);
        Add(t, Row(Caption("Endereço do receptor:"), _txtAddress));

        SetupCombo(_cmbSendMonitor, 260);
        _cmbSendMonitor.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _cmbSendMonitor.SelectedItem is not ComboItem<string> it) return;
            SaveSetting(s => s.SenderMonitor = it.Value);
        };
        SetupCombo(_cmbFps, 70);
        foreach (var f in new[] { 15, 24, 30, 45, 60 }) _cmbFps.Items.Add(new ComboItem<int>(f.ToString(), f));
        _cmbFps.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbFps.SelectedItem is ComboItem<int> it) SaveSetting(s => s.SenderFps = it.Value);
        };
        SetupCombo(_cmbQuality, 190);
        _cmbQuality.Items.Add(new ComboItem<int>("Econômica (menos banda)", 55));
        _cmbQuality.Items.Add(new ComboItem<int>("Equilibrada", 75));
        _cmbQuality.Items.Add(new ComboItem<int>("Alta (texto nítido)", 85));
        _cmbQuality.Items.Add(new ComboItem<int>("Máxima (mais banda)", 92));
        _cmbQuality.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbQuality.SelectedItem is ComboItem<int> it) SaveSetting(s => s.SenderQuality = it.Value);
        };
        SetupCombo(_cmbResolution, 290);
        _cmbResolution.Items.Add(new ComboItem<string>("Automática (tela do outro computador)", "auto"));
        _cmbResolution.Items.Add(new ComboItem<string>("Original (sem reduzir)", "original"));
        foreach (var r in new[] { "1600x900", "1366x768", "1280x720", "1024x576", "854x480" })
            _cmbResolution.Items.Add(new ComboItem<string>("Até " + r.Replace('x', '×'), r));
        _cmbResolution.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbResolution.SelectedItem is ComboItem<string> it) SaveSetting(s => s.SenderResolution = it.Value);
        };
        Add(t, Row(Caption("Monitor:"), _cmbSendMonitor));
        Add(t, Row(Caption("Resolução enviada:"), _cmbResolution));
        Add(t, Row(Caption("Quadros por segundo:"), _cmbFps, Caption("Qualidade:"), _cmbQuality));

        SetupCheck(_chkSendAudio, "Transmitir também o som deste computador");
        _chkSendAudio.CheckedChanged += (_, _) => SaveSetting(s => s.SenderAudio = _chkSendAudio.Checked);
        Add(t, _chkSendAudio);

        SetupCheck(_chkAllowControl, "Permitir que o outro computador use o mouse, o teclado e os controles de jogo dele neste computador");
        _chkAllowControl.CheckedChanged += (_, _) => SaveSetting(s => s.SenderAllowControl = _chkAllowControl.Checked);
        Add(t, _chkAllowControl);

        SetupCheck(_chkElevated, "Controlar programas de administrador e confirmações do Windows (UAC)");
        _chkElevated.Margin = new Padding(20, 0, 0, 0);
        _chkElevated.CheckedChanged += async (_, _) =>
        {
            if (_loading || _elevatedBusy) return;
            _elevatedBusy = true;
            _chkElevated.Enabled = false;
            bool want = _chkElevated.Checked;
            _elevatedStatus.Text = want ? "Aceite o pedido de permissão do Windows…" : "Desativando…";
            var (ok, message) = await _app.SetElevatedControlAsync(want);
            _loading = true;
            _chkElevated.Checked = _app.Settings.ElevatedControl;
            _loading = false;
            _chkElevated.Enabled = true;
            _elevatedBusy = false;
            UpdateElevatedStatus();
            if (!ok && message is not null)
                MessageBox.Show(this, message, "Controle de programas de administrador", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        Add(t, _chkElevated);
        SetupStatus(_elevatedStatus);
        _elevatedStatus.ForeColor = Muted;
        _elevatedStatus.Margin = new Padding(40, 0, 0, 6);
        Add(t, _elevatedStatus);

        _gpStatus.AutoSize = true;
        _gpStatus.Margin = new Padding(20, 8, 10, 0);
        _gpStatus.Text = "Controles de jogo: verificando…";
        _gpStatus.ForeColor = Muted;
        SetupButton(_btnGpInstall, "Instalar suporte a controles");
        _btnGpInstall.Visible = false;
        _btnGpInstall.Click += async (_, _) =>
        {
            _btnGpInstall.Enabled = false;
            _gpStatus.Text = "Controles de jogo: instalando o driver ViGEmBus (aceite o pedido do Windows)…";
            bool ok = await SystemIntegration.InstallGamepadDriverAsync();
            _btnGpInstall.Enabled = true;
            SetGamepadDriverLabel(ok);
            if (!ok)
                MessageBox.Show(this, "O driver não foi instalado. Se a página de download abriu, instale o ViGEmBus por ela e volte aqui.",
                    "Controles de jogo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        Add(t, Row(_gpStatus, _btnGpInstall));

        SetupButton(_btnSend, "Transmitir", primary: true);
        _btnSend.Click += async (_, _) => await ToggleSendAsync();
        Add(t, Row(_btnSend));
        SetupStatus(_sendStatus);
        Add(t, _sendStatus);

        Add(t, Note("Atalho global: Ctrl+Alt+Shift+T inicia ou para a transmissão para o último receptor usado.\n" +
                    "Dica: para usar este computador como segundo monitor de verdade (área estendida), instale um driver de monitor virtual " +
                    "e escolha esse monitor acima — veja a aba “Configurações e ajuda”."));
        Finish(t);
    }

    private void BuildHdmiTab()
    {
        var t = NewPage(_tabHdmi);
        Add(t, Heading("Exibir um notebook ligado por HDMI"));
        Add(t, Note("A porta HDMI de notebooks e PCs comuns só envia imagem — ligar HDMI com HDMI entre os dois não funciona. " +
                    "Para receber pelo HDMI, ligue a saída HDMI do notebook a uma placa de captura USB (UVC, “HDMI capture”) conectada a este PC. " +
                    "O notebook passa a enxergar um monitor externo: use Win+P nele para Duplicar ou Estender. Não precisa instalar nada no notebook."));

        SetupCombo(_cmbHdmiDevice, 300);
        _cmbHdmiDevice.SelectedIndexChanged += (_, _) => { if (!_loading) LoadHdmiFormats(); };
        SetupButton(_btnHdmiRefresh, "Atualizar lista");
        _btnHdmiRefresh.Click += (_, _) => LoadHdmiDevices();
        Add(t, Row(Caption("Placa de captura:"), _cmbHdmiDevice, _btnHdmiRefresh));

        SetupCombo(_cmbHdmiFormat, 300);
        Add(t, Row(Caption("Formato do vídeo:"), _cmbHdmiFormat));

        SetupCombo(_cmbHdmiAudio, 300);
        Add(t, Row(Caption("Som da placa:"), _cmbHdmiAudio));

        SetupCheck(_chkHdmiFullscreen, "Abrir em tela cheia");
        _chkHdmiFullscreen.CheckedChanged += (_, _) => SaveSetting(s => s.HdmiFullscreen = _chkHdmiFullscreen.Checked);
        Add(t, _chkHdmiFullscreen);

        SetupButton(_btnHdmiOpen, "Abrir visualização", primary: true);
        _btnHdmiOpen.Click += async (_, _) => await ToggleHdmiAsync();
        Add(t, Row(_btnHdmiOpen));
        SetupStatus(_hdmiStatus);
        Add(t, _hdmiStatus);

        Add(t, Note("Atalho global: Ctrl+Alt+Shift+H abre ou fecha a visualização HDMI.\n" +
                    "Se a imagem travar em 5 quadros/s, escolha um formato MJPEG — placas USB 2.0 só chegam a 30/60 quadros/s com compressão."));
        Finish(t);
    }

    private void BuildSettingsTab()
    {
        var t = NewPage(_tabSettings);
        Add(t, Heading("Este computador"));

        _txtName.Width = 260;
        _txtName.MaxLength = 40;
        _txtName.Margin = new Padding(0, 3, 12, 3);
        _txtName.Leave += (_, _) => SaveSetting(s => s.DeviceName = _txtName.Text);
        Add(t, Row(Caption("Nome exibido nos outros computadores:"), _txtName));
        BuildPairedDevicesSection(t);

        SetupCheck(_chkAutostart, "Iniciar com o Windows (o TransLocal fica na área de notificação, pronto para receber)");
        _chkAutostart.CheckedChanged += (_, _) =>
        {
            if (!_loading) SystemIntegration.SetAutoStart(_chkAutostart.Checked);
        };
        Add(t, _chkAutostart);

        _fwStatus.AutoSize = true;
        _fwStatus.Margin = new Padding(0, 8, 10, 0);
        SetupButton(_btnFirewall, "Liberar no Firewall do Windows");
        _btnFirewall.Click += async (_, _) =>
        {
            _btnFirewall.Enabled = false;
            bool ok = await Task.Run(SystemIntegration.AddFirewallRule);
            _btnFirewall.Enabled = true;
            RefreshFirewall();
            if (!ok)
                MessageBox.Show(this, "A regra não foi criada. É preciso aceitar o pedido de permissão de administrador.",
                    "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        Add(t, Row(Caption("Firewall:"), _fwStatus, _btnFirewall));
        Add(t, Note("Ligações diretas por cabo aparecem no Windows como “rede não identificada” (pública), onde conexões de entrada são bloqueadas. " +
                    "A regra libera o TransLocal em todos os tipos de rede. O instalador já cria essa regra."));

        Add(t, Heading("Atualizações"));
        SetupCheck(_chkAutoUpdate, "Atualizar automaticamente ao abrir (baixa a versão nova do GitHub e instala)");
        _chkAutoUpdate.CheckedChanged += (_, _) => SaveSetting(s => s.AutoUpdate = _chkAutoUpdate.Checked);
        Add(t, _chkAutoUpdate);
        SetupButton(_btnCheckUpdate, "Procurar atualizações agora");
        _btnCheckUpdate.Click += async (_, _) =>
        {
            _btnCheckUpdate.Enabled = false;
            try { await _app.CheckForUpdatesAsync(manual: true); }
            finally { _btnCheckUpdate.Enabled = true; }
        };
        var btnReleases = new Button();
        SetupButton(btnReleases, "Abrir página de versões");
        btnReleases.Click += (_, _) => SystemIntegration.OpenUrl(Updater.ReleasesPage);
        Add(t, Row(_btnCheckUpdate, btnReleases));
        SetupStatus(_updateStatus);
        _updateStatus.ForeColor = Muted;
        Add(t, _updateStatus);
        Add(t, Note("A atualização espera terminar qualquer transmissão. Na cópia instalada, o Windows pede permissão de administrador " +
                    "uma vez; o TransLocal fecha e reabre sozinho, mantendo as configurações."));

        Add(t, Heading("Quais ligações funcionam"));
        Add(t, Note(
            "✔ Cabo de rede (RJ45) direto entre os dois computadores — funciona sem configurar nada (o Windows usa endereços 169.254.x.x). " +
            "Se o notebook não tiver porta de rede, use um adaptador USB-Ethernet.\n" +
            "✔ USB-C ↔ USB-C, quando os DOIS computadores têm USB4 ou Thunderbolt 3/4 — o Windows 11 cria sozinho o “USB4 P2P Network Adapter”. " +
            "Use cabo Thunderbolt/USB4 (cabo de carregamento comum não serve).\n" +
            "✔ Cabo “bridge” / “data link” USB 3.0 (chip Prolific PL27A1 ou similar) instalado no modo rede.\n" +
            "✔ Wi-Fi ou roteador: funciona se os dois estiverem na mesma rede (com um pouco mais de atraso).\n" +
            "✔ HDMI → placa de captura USB → PC: use a aba HDMI.\n" +
            "✘ Cabo USB-A ↔ USB-A comum: não funciona e pode danificar as portas.\n" +
            "✘ HDMI ↔ HDMI direto entre notebook e PC: não funciona (as duas portas são saídas)."));

        Add(t, Heading("Usar como segundo monitor (área estendida)"));
        Add(t, Note("O TransLocal transmite um monitor que já existe. Para que o outro computador funcione como monitor extra (e não espelho), " +
                    "instale no computador que transmite um driver de monitor virtual (Indirect Display Driver, ex.: “Virtual Display Driver”), " +
                    "ajuste-o em Configurações > Sistema > Tela e escolha esse monitor na aba “Transmitir”. Com a placa de captura HDMI isso já acontece naturalmente."));

        Add(t, Heading("Diagnóstico"));
        SetupCheck(_chkShareDiag, "Compartilhar o registro de erros entre os dois computadores durante a transmissão");
        _chkShareDiag.CheckedChanged += (_, _) =>
        {
            SaveSetting(s => s.ShareDiagnostics = _chkShareDiag.Checked);
            if (!_loading) Streaming.DiagRelay.ShareEnabled = _chkShareDiag.Checked;
        };
        Add(t, _chkShareDiag);
        var btnDiag = new Button();
        SetupButton(btnDiag, "Abrir relatórios de diagnóstico");
        btnDiag.Click += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(Streaming.DiagFiles.Directory);
            SystemIntegration.OpenFolder(Streaming.DiagFiles.Directory);
        };
        var btnIssue = new Button();
        SetupButton(btnIssue, "Relatar um problema no GitHub…");
        btnIssue.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Vai abrir uma página do GitHub com um resumo da última transmissão (avisos e erros dos dois computadores; " +
                    "endereços IP e o nome do usuário são removidos). Nada é enviado até você revisar e clicar em “Submit” no site.\n\nContinuar?",
                    "Relatar um problema", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
                SystemIntegration.OpenUrl(Streaming.DiagFiles.IssueUrl(Updater.Repository));
        };
        Add(t, Row(btnDiag, btnIssue));
        Add(t, Note("Durante a transmissão, cada computador envia ao outro (pela mesma conexão criptografada, uma vez por segundo) os avisos e erros " +
                    "do registro e um resumo do estado (captura, programa em primeiro plano, controle). Fica salvo nos dois, um arquivo por sessão. " +
                    "Na janela de exibição, Ctrl+Alt+Shift+D mostra o diagnóstico ao vivo. Nada sai dos seus computadores sem você mandar."));

        var btnLogs = new Button();
        SetupButton(btnLogs, "Abrir pasta de registros");
        btnLogs.Click += (_, _) => SystemIntegration.OpenFolder(Log.DataDirectory);
        Add(t, Row(btnLogs, Caption($"Versão {SystemIntegration.Version} • portas TCP {Proto.VideoPort} e UDP {Proto.DiscoveryPort}")));
        Finish(t);
    }

    // ================= Dados =================

    private void SaveSetting(Action<AppSettings> apply)
    {
        if (_loading) return;
        apply(_app.Settings);
        _app.Settings.Save();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var s = _app.Settings;
            _chkReceive.Checked = s.ReceiverEnabled;
            _chkFullscreen.Checked = s.ViewerFullscreen;
            _chkPlayAudio.Checked = s.ReceiverPlayAudio;
            _chkStats.Checked = s.ShowStats;
            _chkSendAudio.Checked = s.SenderAudio;
            _chkAllowControl.Checked = s.SenderAllowControl;
            _chkElevated.Checked = s.ElevatedControl;
            _chkShareDiag.Checked = s.ShareDiagnostics;
            _chkHdmiFullscreen.Checked = s.HdmiFullscreen;
            _txtName.Text = s.DeviceName;
            _chkAutoUpdate.Checked = s.AutoUpdate;
            _txtAddress.Text = s.LastReceiverAddress ?? "";
            _chkAutostart.Checked = SystemIntegration.IsAutoStartEnabled();

            FillMonitors(_cmbViewerMonitor, s.ViewerMonitor);
            FillMonitors(_cmbSendMonitor, s.SenderMonitor);
            SelectByValue(_cmbFps, s.SenderFps, 30);
            SelectByValue(_cmbQuality, s.SenderQuality, 75);
            int resIdx = _cmbResolution.Items.Cast<ComboItem<string>>().ToList()
                .FindIndex(i => string.Equals(i.Value, s.SenderResolution, StringComparison.OrdinalIgnoreCase));
            _cmbResolution.SelectedIndex = Math.Max(0, resIdx);
            _chkLight.Checked = s.ReceiverLight;
            _chkAutoControl.Checked = s.ViewerAutoControl;
            RefreshAddresses();
        }
        finally
        {
            _loading = false;
        }
        _ = Task.Run(() =>
        {
            bool fw = SystemIntegration.HasFirewallRule();
            bool gp = Input.VirtualGamepads.IsDriverInstalled(out _);
            try
            {
                BeginInvoke(() =>
                {
                    SetFirewallLabel(fw);
                    SetGamepadDriverLabel(gp);
                });
            }
            catch { }
        });
    }

    private void SetGamepadDriverLabel(bool installed)
    {
        _gpStatus.Text = installed
            ? "Controles de jogo: suporte instalado ✔ (driver ViGEmBus)"
            : "Controles de jogo: falta o driver ViGEmBus neste computador";
        _gpStatus.ForeColor = installed ? Good : Muted;
        _btnGpInstall.Visible = !installed;
    }

    private static void FillMonitors(ComboBox combo, string? selected)
    {
        combo.Items.Clear();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
            combo.Items.Add(new ComboItem<string>(Monitors.Describe(screens[i], i), screens[i].DeviceName));
        int idx = Array.FindIndex(screens, s => s.DeviceName == selected);
        if (idx < 0) idx = Array.FindIndex(screens, s => s.Primary);
        combo.SelectedIndex = Math.Max(0, idx);
    }

    private static void SelectByValue(ComboBox combo, int value, int fallback)
    {
        int best = -1, bestDiff = int.MaxValue;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is not ComboItem<int> it) continue;
            int d = Math.Abs(it.Value - value);
            if (d < bestDiff) { best = i; bestDiff = d; }
        }
        if (best < 0)
        {
            for (int i = 0; i < combo.Items.Count; i++)
                if (combo.Items[i] is ComboItem<int> it && it.Value == fallback) best = i;
        }
        combo.SelectedIndex = Math.Max(0, best);
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        try { BeginInvoke(RefreshAddresses); } catch { }
    }

    private void RefreshAddresses()
    {
        var list = NetInfo.GetLocalAddresses();
        _addresses.Text = list.Count == 0
            ? "(nenhuma ligação de rede ativa — conecte o cabo)"
            : string.Join("\n", list.Select(a => $"{a.Address,-16} {a.Kind} — {a.InterfaceName}"));
    }

    private void RefreshFirewall()
    {
        bool ok = SystemIntegration.HasFirewallRule();
        SetFirewallLabel(ok);
    }

    private void SetFirewallLabel(bool ok)
    {
        _fwStatus.Text = ok ? "liberado ✔" : "não liberado — o outro computador pode não conseguir conectar";
        _fwStatus.ForeColor = ok ? Good : Color.FromArgb(168, 0, 0);
        _btnFirewall.Visible = !ok;
    }

    private void OnTabChanged()
    {
        if (_tabs.SelectedTab == _tabSend && !_scannedOnce)
        {
            _scannedOnce = true;
            _ = ScanAsync();
        }
        else if (_tabs.SelectedTab == _tabHdmi && _cmbHdmiDevice.Items.Count == 0)
        {
            LoadHdmiDevices();
        }
        if (_tabs.SelectedTab == _tabRemote) RefreshTailscale();
        if (_tabs.SelectedTab == _tabReceive)
        {
            RefreshAddresses();
            RefreshPadsHere();
            _padsTimer.Start();
        }
        else
        {
            _padsTimer.Stop();
        }
    }

    // ================= Transmitir =================

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        _btnScan.Enabled = false;
        _scanStatus.Text = "Procurando…";
        try
        {
            var found = await DiscoveryScanner.ScanAsync(Proto.DiscoveryPort, TimeSpan.FromSeconds(1.5), includeLoopback: false, CancellationToken.None);
            _lvReceivers.BeginUpdate();
            _lvReceivers.Items.Clear();
            foreach (var r in found)
            {
                bool paired = r.Id is not null && _app.Trust.All().Any(d => d.ShortId == r.Id);
                string name = r.Name + (r.IsThisComputer ? " (este computador)" : "") + (paired ? " ✔ pareado" : "") +
                              (r.ProtocolVersion < Proto.Version ? " — versão antiga, atualize" : "") + (r.Busy ? " — ocupado" : "");
                var item = new ListViewItem(new[] { name, r.Endpoint, r.Via }) { Tag = r };
                if (r.IsThisComputer || r.Busy) item.ForeColor = Muted;
                _lvReceivers.Items.Add(item);
            }
            _lvReceivers.EndUpdate();
            var first = _lvReceivers.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is ReceiverInfo { IsThisComputer: false });
            if (first is not null && string.IsNullOrWhiteSpace(_txtAddress.Text)) first.Selected = true;

            int others = found.Count(r => !r.IsThisComputer);
            _scanStatus.Text = others switch
            {
                0 => "Nenhum receptor encontrado. Abra o TransLocal no outro computador, confira o cabo e o firewall — ou digite o endereço.",
                1 => "1 receptor encontrado.",
                _ => $"{others} receptores encontrados (prefira a ligação por cabo).",
            };
        }
        catch (Exception ex)
        {
            _scanStatus.Text = "Falha na busca: " + ex.Message;
        }
        finally
        {
            _scanning = false;
            _btnScan.Enabled = true;
        }
    }

    private async Task ToggleSendAsync()
    {
        if (_app.IsSending)
        {
            _app.StopSending();
            return;
        }
        if (_app.IsConnecting) return;

        string address = _txtAddress.Text.Trim();
        if (_lvReceivers.SelectedItems.Count == 1 && _lvReceivers.SelectedItems[0].Tag is ReceiverInfo r && string.IsNullOrEmpty(address))
            address = r.Endpoint;
        if (string.IsNullOrEmpty(address))
        {
            MessageBox.Show(this, "Escolha um receptor da lista ou digite o endereço do outro computador.", "Transmitir",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            await _app.StartSendingAsync(address);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Não foi possível transmitir", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ================= HDMI =================

    private void LoadHdmiDevices()
    {
        _loading = true;
        try
        {
            _cmbHdmiDevice.Items.Clear();
            var devices = HdmiCaptureSession.ListDevices();
            foreach (var d in devices) _cmbHdmiDevice.Items.Add(d);
            if (devices.Count == 0)
            {
                _hdmiStatus.Text = "Nenhuma placa de captura encontrada. Conecte-a na USB deste PC e clique em “Atualizar lista”.";
                _hdmiStatus.ForeColor = Muted;
            }
            else
            {
                int idx = devices.FindIndex(d => d.Key == _app.Settings.HdmiDevice);
                _cmbHdmiDevice.SelectedIndex = Math.Max(0, idx);
                if (!_app.IsHdmiOpen) _hdmiStatus.Text = "";
            }

            _cmbHdmiAudio.Items.Clear();
            _cmbHdmiAudio.Items.Add(new ComboItem<string?>("Sem som", null));
            var inputs = AudioPassthrough.ListInputs();
            foreach (var (id, name) in inputs) _cmbHdmiAudio.Items.Add(new ComboItem<string?>(name, id));
            string? audioId = _app.Settings.HdmiAudioDeviceId
                              ?? HdmiCaptureSession.GuessAudioInput(inputs, (_cmbHdmiDevice.SelectedItem as HdmiDeviceItem)?.Descriptor.Name);
            int aidx = _cmbHdmiAudio.Items.Cast<ComboItem<string?>>().ToList().FindIndex(i => i.Value == audioId);
            _cmbHdmiAudio.SelectedIndex = Math.Max(0, aidx);
        }
        finally
        {
            _loading = false;
        }
        LoadHdmiFormats();
    }

    private void LoadHdmiFormats()
    {
        _cmbHdmiFormat.Items.Clear();
        if (_cmbHdmiDevice.SelectedItem is not HdmiDeviceItem dev) return;
        var formats = HdmiCaptureSession.ListFormats(dev);
        foreach (var f in formats) _cmbHdmiFormat.Items.Add(f);
        var saved = formats.FirstOrDefault(f => f.Key == _app.Settings.HdmiFormat && dev.Key == _app.Settings.HdmiDevice);
        var pick = saved ?? HdmiCaptureSession.PickBest(formats);
        if (pick is not null) _cmbHdmiFormat.SelectedItem = pick;
    }

    private async Task ToggleHdmiAsync()
    {
        if (_app.IsHdmiOpen)
        {
            await _app.CloseHdmiAsync();
            return;
        }
        if (_cmbHdmiDevice.SelectedItem is not HdmiDeviceItem dev || _cmbHdmiFormat.SelectedItem is not HdmiFormatItem fmt)
        {
            MessageBox.Show(this, "Conecte uma placa de captura HDMI e escolha-a na lista.", "HDMI", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var audio = (_cmbHdmiAudio.SelectedItem as ComboItem<string?>)?.Value;
        _app.Settings.HdmiDevice = dev.Key;
        _app.Settings.HdmiFormat = fmt.Key;
        _app.Settings.HdmiAudioDeviceId = audio;
        _app.Settings.Save();

        _btnHdmiOpen.Enabled = false;
        _hdmiStatus.Text = "Abrindo…";
        try
        {
            await _app.OpenHdmiAsync(dev, fmt, audio);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir HDMI", ex);
            MessageBox.Show(this, "Não foi possível abrir a placa de captura: " + ex.Message +
                                  "\n\nFeche outros programas que estejam usando a placa (OBS, Câmera, Teams) e tente outro formato.",
                "HDMI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnHdmiOpen.Enabled = true;
            RefreshState();
        }
    }

    // ================= Estado =================

    private void UpdateElevatedStatus()
    {
        if (_elevatedBusy) return;
        var bridge = Input.ElevatedInputBridge.Current;
        bool service = Input.ServiceSetup.IsInstalled();
        _elevatedStatus.Text =
            !Input.ElevatedInput.TrustedLocation && !_app.Settings.ElevatedControl
                ? "Disponível na versão instalada (instalador). Instala o serviço do TransLocal; o Windows pede permissão uma vez."
            : !_app.Settings.ElevatedControl
                ? "Desligado: Gerenciador de Tarefas, jogos “como administrador” e confirmações do UAC não recebem mouse/teclado do outro computador."
            : !service
                ? "Ativo pela versão anterior (sem as confirmações do UAC). Desmarque e marque de novo para instalar o serviço."
            : bridge?.IsConnected == true && bridge.IsSystemAgent
                ? "Ativo: serviço do TransLocal ligado (programas de administrador e confirmações do UAC)."
                : "Serviço instalado — aguardando o agente iniciar…";
    }

    private void RefreshState()
    {
        if (IsDisposed) return;
        UpdateElevatedStatus();
        _updateStatus.Text = _app.UpdateStatus;
        RefreshRemoteState();

        // Receber
        if (_app.ReceiverError is { } err)
        {
            _recvStatus.Text = "⚠ " + err;
            _recvStatus.ForeColor = Color.FromArgb(168, 0, 0);
        }
        else if (_app.ViewingFrom is { } from)
        {
            _recvStatus.Text = $"▶ Exibindo a tela de {from}";
            _recvStatus.ForeColor = Accent;
        }
        else if (_app.IsReceiving)
        {
            _recvStatus.Text = "● Pronto — aguardando transmissão";
            _recvStatus.ForeColor = Good;
        }
        else
        {
            _recvStatus.Text = "○ Desligado — este computador não aceita transmissões";
            _recvStatus.ForeColor = Muted;
        }
        _loading = true;
        _chkReceive.Checked = _app.Settings.ReceiverEnabled;
        _loading = false;

        // Transmitir
        if (_app.IsSending)
        {
            _btnSend.Text = "Parar transmissão";
            _sendStatus.Text = $"Transmitindo para {_app.SendingTo}" + (_app.SenderStats is { } st ? $"\n{st}" : "");
            _sendStatus.ForeColor = Accent;
        }
        else if (_app.IsConnecting)
        {
            _btnSend.Text = "Conectando…";
            _sendStatus.Text = "";
        }
        else
        {
            _btnSend.Text = "Transmitir";
            _sendStatus.Text = _app.LastSendEnd ?? "";
            _sendStatus.ForeColor = Muted;
        }
        _btnSend.Enabled = !_app.IsConnecting;
        bool idle = !_app.IsSending && !_app.IsConnecting;
        _cmbSendMonitor.Enabled = _cmbFps.Enabled = _cmbQuality.Enabled = _cmbResolution.Enabled =
            _chkSendAudio.Enabled = _chkAllowControl.Enabled = idle;

        // HDMI
        _btnHdmiOpen.Text = _app.IsHdmiOpen ? "Fechar visualização" : "Abrir visualização";
        if (_app.IsHdmiOpen)
        {
            _hdmiStatus.Text = "Visualização aberta.";
            _hdmiStatus.ForeColor = Accent;
        }
        else if (_hdmiStatus.Text == "Visualização aberta." || _hdmiStatus.Text == "Abrindo…")
        {
            _hdmiStatus.Text = "";
        }
    }
}
