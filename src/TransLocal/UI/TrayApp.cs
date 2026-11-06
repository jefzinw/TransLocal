using System.Net.Sockets;
using TransLocal.Input;
using TransLocal.Streaming;
using TransLocal.Video;

namespace TransLocal.UI;

/// <summary>
/// Núcleo do aplicativo: ícone na área de notificação, receptor em segundo plano, transmissão,
/// visualizador HDMI e atalhos globais.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    public const string ShowEventName = @"Local\TransLocal.Show";

    private readonly NotifyIcon _tray;
    private readonly Control _ui;
    private readonly HotkeyWindow _hotkeys;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showWait;
    private readonly ToolStripMenuItem _miSend;
    private readonly ToolStripMenuItem _miHdmi;
    private readonly ToolStripMenuItem _miReceive;

    private MainForm? _main;
    private ReceiverServer? _receiver;
    private ReceiverSession? _viewSession;
    private ViewerForm? _viewViewer;
    private GamepadForwarder? _gamepadForwarder;
    private System.Windows.Forms.Timer? _gamepadTimer;
    private bool _gamepadWarned;
    private SenderSession? _sender;
    private HdmiCaptureSession? _hdmi;
    private ViewerForm? _hdmiViewer;
    private bool _hiddenNoticeShown;

    public AppSettings Settings { get; }
    public bool IsExiting { get; private set; }
    public bool IsReceiving => _receiver?.IsRunning == true;
    public string? ReceiverError { get; private set; }
    public string? ViewingFrom => _viewSession?.SenderName;
    public bool IsSending => _sender?.IsRunning == true;
    public bool IsConnecting { get; private set; }
    public string? SendingTo => _sender is null ? null : $"{_sender.ReceiverName} ({_sender.RemoteEndPoint})";
    public string? SenderStats { get; private set; }
    public string? LastSendEnd { get; private set; }
    public bool IsHdmiOpen => _hdmi is not null;

    /// <summary>Disparado na thread da interface quando algo muda.</summary>
    public event Action? StateChanged;

    public TrayApp(bool startMinimized)
    {
        Settings = AppSettings.Load();
        bool firstRun = !File.Exists(AppSettings.FilePath);

        _ui = new Control();
        _ = _ui.Handle; // cria a janela usada para despachar eventos para a thread da interface

        var menu = new ContextMenuStrip();
        var miOpen = new ToolStripMenuItem("Abrir TransLocal", null, (_, _) => ShowMain()) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        _miSend = new ToolStripMenuItem("Transmitir", null, async (_, _) => await ToggleSendFromShortcutAsync());
        _miHdmi = new ToolStripMenuItem("Abrir visualização HDMI", null, async (_, _) => await ToggleHdmiFromShortcutAsync());
        _miReceive = new ToolStripMenuItem("Aceitar transmissões", null, (_, _) => SetReceiverEnabled(!Settings.ReceiverEnabled));
        menu.Items.AddRange(new ToolStripItem[]
        {
            miOpen,
            new ToolStripSeparator(),
            _miSend,
            new ToolStripMenuItem("Escolher receptor…", null, (_, _) => ShowMain("send")),
            _miHdmi,
            _miReceive,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Sair", null, (_, _) => Exit()),
        });
        menu.Opening += (_, _) => UpdateMenu();

        _tray = new NotifyIcon
        {
            Icon = SystemIntegration.AppIcon,
            Text = "TransLocal",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        _hotkeys = new HotkeyWindow();
        _hotkeys.Register(1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, Keys.T, async () => await ToggleSendFromShortcutAsync());
        _hotkeys.Register(2, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, Keys.H, async () => await ToggleHdmiFromShortcutAsync());

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Ui(() => ShowMain()), null, -1, false);

        SystemIntegration.RefreshAutoStartPath();
        SystemIntegration.RestoreGameBarPromptIfGameBarInstalled();

        // Controles de jogo (SDL): mapeamentos salvos e inicialização em segundo plano.
        ControllerService.InitialMappings = () => Settings.ControllerMappings.ToArray();
        _ = Task.Run(() => _ = ControllerService.Instance);
        if (Settings.ReceiverEnabled) StartReceiver();
        if (firstRun) Settings.Save();

        if (!startMinimized) ShowMain();
        Log.Info($"TransLocal {SystemIntegration.Version} iniciado ({SystemIntegration.ExePath})");
    }

    // ================= Utilidades =================

    private void Ui(Action a)
    {
        if (_ui.IsDisposed) return;
        try
        {
            if (_ui.InvokeRequired) _ui.BeginInvoke(a);
            else a();
        }
        catch (InvalidOperationException) { }
    }

    private void Changed()
    {
        // Enquanto exibe outra tela (rede ou HDMI), mantém o monitor ligado e o PC acordado.
        bool viewing = _viewSession is not null || _hdmi is not null;
        Native.SetThreadExecutionState(viewing
            ? Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED | Native.ES_SYSTEM_REQUIRED
            : Native.ES_CONTINUOUS);
        StateChanged?.Invoke();
    }

    private void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try { _tray.ShowBalloonTip(4000, title, text, icon); } catch { }
    }

    public void NotifyHiddenOnce()
    {
        if (_hiddenNoticeShown) return;
        _hiddenNoticeShown = true;
        Balloon("O TransLocal continua aberto", "Ele fica na área de notificação, pronto para receber. Para fechar de vez, use “Sair” no menu do ícone.");
    }

    public void ShowMain(string? tab = null)
    {
        if (_main is null || _main.IsDisposed) _main = new MainForm(this);
        if (tab is not null) _main.SelectTab(tab);
        if (!_main.Visible) _main.Show();
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
        _main.BringToFront();
    }

    private void UpdateMenu()
    {
        _miSend.Text = IsSending
            ? $"Parar transmissão para {_sender!.ReceiverName}"
            : Settings.LastReceiverAddress is { } last
                ? $"Transmitir para {Settings.LastReceiverName ?? last}"
                : "Transmitir esta tela…";
        _miHdmi.Text = IsHdmiOpen ? "Fechar visualização HDMI" : "Abrir visualização HDMI";
        _miReceive.Checked = Settings.ReceiverEnabled;
        _tray.Text = IsSending ? "TransLocal — transmitindo" : ViewingFrom is not null ? "TransLocal — exibindo" : "TransLocal";
    }

    // ================= Receptor =================

    public void SetReceiverEnabled(bool enabled)
    {
        Settings.ReceiverEnabled = enabled;
        Settings.Save();
        if (enabled) StartReceiver(); else StopReceiver();
        Changed();
    }

    private void StartReceiver()
    {
        if (_receiver is not null) return;
        var server = new ReceiverServer(() => Settings.DeviceName, () => Settings.ReceiverPlayAudio, () =>
        {
            // A imagem chega no tamanho da tela onde será exibida (o transmissor reduz se for maior).
            var screen = Monitors.Resolve(Settings.ViewerMonitor).Bounds;
            return new ReceiverPrefs(screen.Width, screen.Height, Settings.ReceiverLight);
        });
        server.SessionStarted += s => Ui(() => OnViewStarted(s));
        server.SessionEnded += (s, reason) => Ui(() => OnViewEnded(s, reason));
        try
        {
            server.Start();
            _receiver = server;
            ReceiverError = null;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            ReceiverError = $"A porta {Proto.VideoPort} já está em uso por outro programa.";
            Log.Error(ReceiverError, ex);
            server.Dispose();
        }
        catch (Exception ex)
        {
            ReceiverError = "Não foi possível começar a receber: " + ex.Message;
            Log.Error(ReceiverError, ex);
            server.Dispose();
        }
    }

    private void StopReceiver()
    {
        _receiver?.Dispose();
        _receiver = null;
        ReceiverError = null;
    }

    private void OnViewStarted(ReceiverSession s)
    {
        if (IsExiting)
        {
            s.Close();
            return;
        }
        _viewSession = s;
        var screen = Monitors.Resolve(Settings.ViewerMonitor);
        var viewer = new ViewerForm(s.SenderName, s.Surface, s.ControlAllowed ? s.SendInput : null, screen,
            Settings.ViewerFullscreen, Settings.ShowStats, new Size(s.InitialWidth, s.InitialHeight),
            startWithControl: Settings.ViewerAutoControl);
        viewer.UserClosed += () => s.Close();
        s.FrameReady += viewer.NotifyFrame;
        s.StatsUpdated += viewer.SetStats;
        _viewViewer = viewer;
        s.Surface.FastScaling = s.Light;
        if (s.ControlAllowed) StartGamepads(s, viewer);
        viewer.Show();
        viewer.Activate();
        Balloon($"Exibindo a tela de {s.SenderName}",
            s.ControlAllowed
                ? "Mouse, teclado e controles USB comandam o outro computador. Ctrl+Alt+Shift+C solta o controle; Ctrl+Alt+Shift+Q fecha."
                : "Leve o mouse ao topo da tela para ver os botões. Ctrl+Alt+Shift+Q fecha.");
        Changed();
    }

    /// <summary>Lê os controles USB deste computador e os envia enquanto a exibição estiver em foco.</summary>
    private void StartGamepads(ReceiverSession s, ViewerForm viewer)
    {
        StopGamepads();
        string status = s.GamepadStatus;
        var forwarder = new GamepadForwarder(s.SendGamepad, () => viewer.InputActive, canSend: status == VirtualGamepads.StatusOk);
        s.GamepadFeedback += forwarder.Rumble;
        _gamepadForwarder = forwarder;
        _gamepadWarned = false;
        _gamepadTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _gamepadTimer.Tick += (_, _) =>
        {
            int n = forwarder.ConnectedCount;
            string plural = n == 1 ? "controle" : "controles";
            string? info = n == 0 ? null : status switch
            {
                VirtualGamepads.StatusOk => viewer.InputActive ? $"Controles: {n} {plural} em uso" : $"Controles: {n} em espera",
                VirtualGamepads.StatusNoDriver => "Controle detectado — falta o driver no outro PC",
                "antigo" => "Controle detectado — atualize o TransLocal no outro PC",
                _ => null,
            };
            viewer.SetGamepadInfo(info);
            if (n > 0 && status != VirtualGamepads.StatusOk && !_gamepadWarned)
            {
                _gamepadWarned = true;
                Balloon("Controle de jogo detectado",
                    status == VirtualGamepads.StatusNoDriver
                        ? "Para usá-lo, no outro computador abra TransLocal › Transmitir › “Instalar suporte a controles” e transmita de novo."
                        : "Atualize o TransLocal no outro computador para usar controles de jogo.",
                    ToolTipIcon.Warning);
            }
        };
        _gamepadTimer.Start();
        Log.Info($"Controles de jogo: transmissor={status}, conectados aqui={XInput.CountConnected()}");
    }

    private void StopGamepads()
    {
        _gamepadTimer?.Stop();
        _gamepadTimer?.Dispose();
        _gamepadTimer = null;
        _gamepadForwarder?.Dispose();
        _gamepadForwarder = null;
    }

    private void OnViewEnded(ReceiverSession s, string? reason)
    {
        if (_viewSession == s)
        {
            StopGamepads();
            _viewViewer?.CloseFromCode();
            _viewViewer = null;
            _viewSession = null;
        }
        s.Surface.Dispose();
        if (reason is not null && !IsExiting) Balloon("Exibição encerrada", reason);
        Changed();
    }

    // ================= Transmissor =================

    public async Task StartSendingAsync(string address)
    {
        if (IsSending || IsConnecting) return;
        string host = address.Trim();
        int port = Proto.VideoPort;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out var p))
        {
            port = p;
            host = host[..colon];
        }

        IsConnecting = true;
        LastSendEnd = null;
        Changed();
        try
        {
            var opt = new SenderOptions
            {
                Name = Settings.DeviceName,
                MonitorDeviceName = Settings.SenderMonitor,
                Fps = Settings.SenderFps,
                Quality = Settings.SenderQuality,
                Audio = Settings.SenderAudio,
                AllowControl = Settings.SenderAllowControl,
                Resolution = Settings.SenderResolution,
            };
            var session = await SenderSession.ConnectAsync(host, port, opt, CancellationToken.None);
            session.StatsUpdated += text => Ui(() =>
            {
                SenderStats = text;
                Changed();
            });
            session.Ended += (ss, reason) => Ui(() =>
            {
                if (_sender == ss) _sender = null;
                SenderStats = null;
                LastSendEnd = reason is null ? "Transmissão parada." : reason;
                if (reason is not null && !IsExiting) Balloon("Transmissão encerrada", reason, ToolTipIcon.Warning);
                Changed();
            });
            _sender = session;
            Settings.LastReceiverAddress = port == Proto.VideoPort ? host : $"{host}:{port}";
            Settings.LastReceiverName = session.ReceiverName;
            Settings.Save();
            Balloon($"Transmitindo para {session.ReceiverName}", "Ctrl+Alt+Shift+T para parar.");
        }
        finally
        {
            IsConnecting = false;
            Changed();
        }
    }

    public void StopSending()
    {
        _sender?.Stop();
    }

    private async Task ToggleSendFromShortcutAsync()
    {
        if (IsSending)
        {
            StopSending();
            return;
        }
        if (Settings.LastReceiverAddress is not { } last)
        {
            ShowMain("send");
            return;
        }
        try
        {
            await StartSendingAsync(last);
        }
        catch (Exception ex)
        {
            Balloon("Não foi possível transmitir", ex.Message, ToolTipIcon.Warning);
            LastSendEnd = ex.Message;
            Changed();
        }
    }

    // ================= HDMI =================

    public async Task OpenHdmiAsync(HdmiDeviceItem device, HdmiFormatItem format, string? audioInputId)
    {
        await CloseHdmiAsync();
        var session = new HdmiCaptureSession();
        try
        {
            await session.StartAsync(device, format, audioInputId);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        var screen = Monitors.Resolve(Settings.ViewerMonitor);
        var viewer = new ViewerForm($"HDMI — {device.Descriptor.Name}", session.Surface, null, screen,
            Settings.HdmiFullscreen, Settings.ShowStats,
            new Size(format.Characteristics.Width, format.Characteristics.Height));
        session.FrameReady += viewer.NotifyFrame;
        session.StatsUpdated += viewer.SetStats;
        viewer.UserClosed += () => _ = CloseHdmiAsync();
        _hdmi = session;
        _hdmiViewer = viewer;
        viewer.Show();
        viewer.Activate();
        Changed();
    }

    public async Task CloseHdmiAsync()
    {
        var session = _hdmi;
        var viewer = _hdmiViewer;
        _hdmi = null;
        _hdmiViewer = null;
        viewer?.CloseFromCode();
        if (session is not null) await session.DisposeAsync();
        Changed();
    }

    private async Task ToggleHdmiFromShortcutAsync()
    {
        if (IsHdmiOpen)
        {
            await CloseHdmiAsync();
            return;
        }
        var devices = HdmiCaptureSession.ListDevices();
        var dev = devices.FirstOrDefault(d => d.Key == Settings.HdmiDevice) ?? (devices.Count == 1 ? devices[0] : null);
        if (dev is null)
        {
            ShowMain("hdmi");
            return;
        }
        var formats = HdmiCaptureSession.ListFormats(dev);
        var fmt = formats.FirstOrDefault(f => f.Key == Settings.HdmiFormat) ?? HdmiCaptureSession.PickBest(formats);
        if (fmt is null)
        {
            ShowMain("hdmi");
            return;
        }
        try
        {
            await OpenHdmiAsync(dev, fmt, Settings.HdmiAudioDeviceId);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir HDMI pelo atalho", ex);
            Balloon("Não foi possível abrir a placa de captura", ex.Message, ToolTipIcon.Warning);
        }
    }

    // ================= Saída =================

    public void Exit()
    {
        if (IsExiting) return;
        IsExiting = true;
        Log.Info("Encerrando");
        try { _sender?.Stop(); } catch { }
        StopGamepads();
        try { _viewSession?.Close(); } catch { }
        var hdmi = _hdmi;
        _hdmi = null;
        _hdmiViewer?.CloseFromCode();
        _hdmiViewer = null;
        if (hdmi is not null)
        {
            // Fora do contexto da interface, para a continuação não depender da thread bloqueada.
            try { Task.Run(async () => await hdmi.DisposeAsync()).Wait(3000); } catch { }
        }
        StopReceiver();
        try { ControllerService.Shutdown(); } catch { }
        _hotkeys.Dispose();
        _showWait.Unregister(null);
        _showEvent.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (_main is not null && !_main.IsDisposed) _main.Close();
        _viewViewer?.CloseFromCode();
        _ui.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsExiting) Exit();
        base.Dispose(disposing);
    }
}

/// <summary>Janela invisível que recebe os atalhos globais (RegisterHotKey).</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Dictionary<int, Action> _actions = new();

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public void Register(int id, uint modifiers, Keys key, Action action)
    {
        if (Native.RegisterHotKey(Handle, id, modifiers | Native.MOD_NOREPEAT, (uint)key))
            _actions[id] = action;
        else
            Log.Warn($"Atalho global {key} indisponível (já usado por outro programa)");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var a))
        {
            try { a(); } catch (Exception ex) { Log.Error("Erro no atalho", ex); }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(Handle, id);
        _actions.Clear();
        DestroyHandle();
    }
}
