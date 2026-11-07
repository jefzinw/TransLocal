using System.Net.Sockets;
using TransLocal.Input;
using TransLocal.Security;
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
    public const string QuitEventName = @"Local\TransLocal.Quit";

    private readonly NotifyIcon _tray;
    private readonly Control _ui;
    private readonly HotkeyWindow _hotkeys;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showWait;
    private readonly EventWaitHandle _quitEvent;
    private readonly RegisteredWaitHandle _quitWait;
    private System.Threading.Timer? _updateTimer;
    private bool _updating;
    private readonly ToolStripMenuItem _miSend;
    private readonly ToolStripMenuItem _miHdmi;
    private readonly ToolStripMenuItem _miReceive;

    private MainForm? _main;
    private readonly ReceiverServer _receiver;
    private Totp? _totp;
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
    public bool IsReceiving => _receiver.IsRunning && Settings.ReceiverEnabled;
    public DeviceIdentity Identity { get; }
    public SettingsTrustStore Trust { get; }
    public ApprovalHub Approvals { get; }
    public string? ReceiverError { get; private set; }
    public string? ViewingFrom => _viewSession?.SenderName;
    public bool IsSending => _sender?.IsRunning == true;
    public bool IsConnecting { get; private set; }
    public string? SendingTo => _sender is null ? null : $"{_sender.ReceiverName} ({_sender.RemoteEndPoint})";
    public string? SenderStats { get; private set; }
    public string? LastSendEnd { get; private set; }
    public bool IsHdmiOpen => _hdmi is not null;

    /// <summary>Texto sobre atualizações (para a aba Configurações).</summary>
    public string UpdateStatus { get; private set; } = $"Versão {Updater.Current}.";

    private bool IsBusy => IsSending || IsConnecting || _viewSession is not null || IsHdmiOpen;

    /// <summary>Disparado na thread da interface quando algo muda.</summary>
    public event Action? StateChanged;

    public TrayApp(bool startMinimized, bool justUpdated = false, bool autoUpdate = true)
    {
        Settings = AppSettings.Load();
        bool firstRun = !File.Exists(AppSettings.FilePath);

        _ui = new Control();
        _ = _ui.Handle; // cria a janela usada para despachar eventos para a thread da interface

        // Identidade criptográfica e aparelhos pareados (base de todas as conexões).
        try
        {
            Identity = DeviceIdentity.LoadOrCreate();
        }
        catch (Exception ex)
        {
            Log.Error("Não foi possível carregar a identidade deste aparelho; usando uma temporária (os pareamentos não valerão depois de reiniciar)", ex);
            Identity = DeviceIdentity.CreateEphemeral();
        }
        Trust = new SettingsTrustStore(Settings);
        Approvals = new ApprovalHub { StatusProvider = ApproverStatus };
        Approvals.ApproversChanged += () => Ui(Changed);
        Trust.Changed += () => Ui(Changed);
        _receiver = CreateServer();
        Log.Info($"Identidade deste aparelho: {Identity.ShortId}");

        Audio.AudioOutput.Initialize(Settings.ReceiverAudioDevice);
        Audio.AudioOutput.PreferredChanged += id =>
        {
            Settings.ReceiverAudioDevice = id;
            try { Settings.Save(); } catch (Exception ex) { Log.Warn("Falha ao salvar a saída de som", ex); }
        };

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
        // O instalador (atualização/desinstalação) pede para fechar com “TransLocal.exe --quit”.
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => Ui(Exit), null, -1, true);

        SystemIntegration.RefreshAutoStartPath();
        SystemIntegration.RestoreGameBarPromptIfGameBarInstalled();

        // Controles de jogo (SDL): iniciada só quando necessária (exibição com controle ou aba Receber),
        // para não abrir no PC que transmite os controles virtuais que ele mesmo cria.
        ControllerService.InitialMappings = () => Settings.ControllerMappings.ToArray();
        UpdateListening();
        if (firstRun) Settings.Save();

        if (!startMinimized) ShowMain();
        Log.Info($"TransLocal {SystemIntegration.Version} iniciado ({SystemIntegration.ExePath})");

        Updater.Cleanup();
        if (justUpdated)
        {
            Balloon("TransLocal atualizado", $"Agora na versão {Updater.Current}.");
            UpdateStatus = $"Atualizado para a versão {Updater.Current}.";
        }
        // Atualização automática: pouco depois de abrir e, depois, a cada 12 horas.
        if (autoUpdate)
        {
            _updateTimer = new System.Threading.Timer(_ => Ui(() =>
            {
                if (Settings.AutoUpdate) _ = CheckForUpdatesAsync(manual: false);
            }), null, TimeSpan.FromSeconds(8), TimeSpan.FromHours(12));
        }
    }

    // ================= Atualização automática =================

    private void SetUpdateStatus(string text)
    {
        UpdateStatus = text;
        Changed();
    }

    /// <summary>Procura, baixa e aplica uma atualização. <paramref name="manual"/> = pedido pelo botão.</summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating || IsExiting) return;
        _updating = true;
        try
        {
            bool installed = Updater.IsInstalled;
            SetUpdateStatus("Procurando atualizações…");
            var info = await Updater.CheckAsync(CancellationToken.None, installed);
            if (info is null)
            {
                SetUpdateStatus($"Você está na versão mais recente ({Updater.Current}).");
                return;
            }
            Log.Info($"Atualização disponível: {info.Version} ({info.AssetName})");

            // Evita ciclo: se a mesma versão já foi tentada há pouco e não “pegou”, só tenta de novo pelo botão.
            if (!manual && Settings.LastUpdateAttempt == info.Version.ToString() &&
                DateTime.UtcNow - Settings.LastUpdateAttemptUtc < TimeSpan.FromHours(24))
            {
                SetUpdateStatus($"Versão {info.Version} disponível. Use “Procurar atualizações agora” para instalar.");
                return;
            }

            await WaitUntilIdleAsync(info);
            SetUpdateStatus($"Baixando a versão {info.Version}…");
            if (!manual) Balloon("Atualização do TransLocal", $"Baixando a versão {info.Version} em segundo plano…");
            var progress = new Progress<int>(p => SetUpdateStatus($"Baixando a versão {info.Version}… {p}%"));
            string file = await Updater.DownloadAsync(info, progress, CancellationToken.None);
            await WaitUntilIdleAsync(info);

            Settings.LastUpdateAttempt = info.Version.ToString();
            Settings.LastUpdateAttemptUtc = DateTime.UtcNow;
            Settings.Save();

            if (installed)
            {
                SetUpdateStatus($"Instalando a versão {info.Version}… confirme a permissão do Windows.");
                Balloon("Instalando atualização", $"Versão {info.Version}: confirme a permissão do Windows. O TransLocal fecha e reabre sozinho.");
                var setup = Updater.StartInstaller(file);
                if (setup is not null)
                {
                    // Se o instalador terminar e este processo ainda estiver aberto, a instalação não aconteceu
                    // (permissão negada, por exemplo). Com sucesso, o instalador fecha este processo antes.
                    await setup.WaitForExitAsync();
                    SetUpdateStatus($"A instalação da versão {info.Version} não foi concluída (a permissão do Windows foi negada?). Tente pelo botão.");
                }
            }
            else
            {
                if (Updater.ReplaceExecutable(SystemIntegration.ExePath, file))
                {
                    Log.Info($"Executável atualizado para {info.Version}; reabrindo");
                    Updater.RelaunchUpdated();
                    Exit();
                }
                else
                {
                    SetUpdateStatus($"Versão {info.Version} baixada, mas sem permissão para trocar o arquivo nesta pasta. Baixe pelo GitHub.");
                    if (manual) SystemIntegration.OpenUrl(info.PageUrl);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Atualização automática falhou", ex);
            SetUpdateStatus("Não foi possível atualizar agora: " + ex.Message);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Não atualiza no meio de uma transmissão ou exibição.</summary>
    private async Task WaitUntilIdleAsync(UpdateInfo info)
    {
        while (IsBusy && !IsExiting)
        {
            SetUpdateStatus($"Versão {info.Version} disponível — será instalada quando a transmissão terminar.");
            await Task.Delay(TimeSpan.FromSeconds(20));
        }
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

    // ================= Segurança =================

    /// <summary>Opções do canal seguro (identidade, pareados, confirmação de pareamento na tela).</summary>
    public SecureChannelOptions Channel() => new()
    {
        Identity = Identity,
        Trust = Trust,
        LocalName = Settings.DeviceName,
        ConfirmPairing = p => ConfirmationForm.Ask(_ui, () => ConfirmationForm.ForPairing(p)),
    };

    /// <summary>Código de autorização do acesso remoto (segredo criado na primeira vez, guardado com DPAPI).</summary>
    public Totp RemoteTotp
    {
        get
        {
            if (_totp is not null) return _totp;
            var secret = LocalSecret.Unprotect(Settings.TotpSecretProtected);
            if (secret is null)
            {
                secret = Totp.NewSecret();
                Settings.TotpSecretProtected = LocalSecret.Protect(secret);
                Settings.Save();
            }
            return _totp = new Totp(secret);
        }
    }

    /// <summary>Gera um novo segredo (invalida autenticadores cadastrados antes).</summary>
    public void ResetRemoteTotp()
    {
        var secret = Totp.NewSecret();
        Settings.TotpSecretProtected = LocalSecret.Protect(secret);
        Settings.Save();
        _totp = new Totp(secret);
        Approvals.BroadcastStatus();
        Log.Info("Novo segredo do código de autorização gerado");
    }

    private ApproverStatusMessage ApproverStatus() => new()
    {
        PcName = Settings.DeviceName,
        PcId = Identity.ShortId,
        RemoteAccessEnabled = Settings.RemoteAccessEnabled,
        Streaming = IsSending,
        StreamingTo = _sender?.ReceiverName,
        TotpSecret = RemoteTotp.Base32Secret,
    };

    // ================= Receptor / conexões recebidas =================

    public void SetReceiverEnabled(bool enabled)
    {
        Settings.ReceiverEnabled = enabled;
        Settings.Save();
        UpdateListening();
        Changed();
    }

    public void SetRemoteAccessEnabled(bool enabled)
    {
        Settings.RemoteAccessEnabled = enabled;
        Settings.Save();
        Log.Info($"Acesso remoto {(enabled ? "ligado" : "desligado")}");
        UpdateListening();
        Approvals.BroadcastStatus();
        Changed();
    }

    private ReceiverServer CreateServer()
    {
        var server = new ReceiverServer(Channel, () => Settings.ReceiverPlayAudio, () =>
        {
            // A imagem chega no tamanho da tela onde será exibida (o transmissor reduz se for maior).
            var screen = Monitors.Resolve(Settings.ViewerMonitor).Bounds;
            return new ReceiverPrefs(screen.Width, screen.Height, Settings.ReceiverLight);
        }, acceptStreams: () => Settings.ReceiverEnabled);
        server.SessionStarted += s => Ui(() => OnViewStarted(s));
        server.SessionEnded += (s, reason) => Ui(() => OnViewEnded(s, reason));
        server.PeerAuthenticated += (peer, ip) => Trust.Touch(peer.Fingerprint, peer.Name, ip?.ToString());
        server.PullHandler = OnPullRequested;
        server.ApproverHandler = (conn, peer) => Approvals.Serve(conn, peer);
        return server;
    }

    /// <summary>Escuta conexões se for aceitar transmissões ou acesso remoto.</summary>
    private void UpdateListening()
    {
        bool want = Settings.ReceiverEnabled || Settings.RemoteAccessEnabled;
        if (want && !_receiver.IsRunning)
        {
            try
            {
                _receiver.Start();
                ReceiverError = null;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                ReceiverError = $"A porta {Proto.VideoPort} já está em uso por outro programa.";
                Log.Error(ReceiverError, ex);
                _receiver.Stop();
            }
            catch (Exception ex)
            {
                ReceiverError = "Não foi possível começar a receber: " + ex.Message;
                Log.Error(ReceiverError, ex);
                _receiver.Stop();
            }
        }
        else if (!want && _receiver.IsRunning)
        {
            _receiver.Stop();
            ReceiverError = null;
        }
    }

    private void StopReceiver()
    {
        _receiver.Stop();
        ReceiverError = null;
    }

    /// <summary>
    /// Um aparelho pareado pede a tela deste computador (acesso remoto). Roda na thread da conexão.
    /// Retorna true se a conexão passou a ser usada pela transmissão.
    /// </summary>
    private bool OnPullRequested(Connection conn, PeerInfo peer)
    {
        void Reject(string reason)
        {
            Log.Info($"Acesso remoto de {peer.Name} recusado: {reason}");
            try { conn.SendJson(MsgType.StreamDecision, new StreamDecisionMessage { Approved = false, Reason = reason }); } catch { }
        }

        if (!Settings.RemoteAccessEnabled)
        {
            Reject($"O acesso remoto está desligado em {Settings.DeviceName}.");
            return false;
        }
        StreamRequestMessage req;
        try
        {
            conn.ReceiveTimeoutMs = 15_000;
            req = conn.ReceiveJson<StreamRequestMessage>(MsgType.StreamRequest);
        }
        catch
        {
            return false;
        }
        if (IsSending || IsConnecting)
        {
            Reject($"{Settings.DeviceName} já está transmitindo para outro aparelho.");
            return false;
        }

        bool approved;
        string by;
        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            approved = RemoteTotp.Verify(req.Code, out var error);
            by = "código de autorização";
            if (!approved)
            {
                Reject(error ?? "Código inválido.");
                return false;
            }
        }
        else
        {
            Ui(() => Balloon("Pedido de acesso", $"{peer.Name} quer acessar esta tela. Aprove aqui ou pelo celular."));
            (approved, by) = Approvals.Request(peer, TimeSpan.FromSeconds(60),
                ct => ConfirmationForm.Ask(_ui, () => ConfirmationForm.ForAccess(peer.Name, peer.ShortId), ct));
            if (!approved)
            {
                Reject(by == "tempo esgotado"
                    ? "Ninguém autorizou a tempo. Aprove pelo celular ou digite o código de autorização."
                    : $"Acesso negado ({by}).");
                return false;
            }
        }

        try
        {
            conn.SendJson(MsgType.StreamDecision, new StreamDecisionMessage { Approved = true, ApprovedBy = by });
            Log.Info($"Acesso remoto de {peer.Name} autorizado ({by}); iniciando transmissão");
            var session = SenderSession.StartOnConnection(conn, BuildSenderOptions());
            Ui(() =>
            {
                RegisterSender(session);
                Balloon($"Acesso remoto: transmitindo para {peer.Name}", $"Autorizado por {by}. Ctrl+Alt+Shift+T encerra.");
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Falha ao iniciar o acesso remoto de {peer.Name}", ex);
        }
        return true; // a conexão pertence à transmissão (ou já foi fechada por ela)
    }

    /// <summary>Pede a tela de um computador pareado (acesso remoto) e a exibe aqui.</summary>
    public async Task RequestRemoteScreenAsync(string address, string? code, Action<string> status)
    {
        if (_viewSession is not null) throw new InvalidOperationException("Já há uma tela sendo exibida aqui.");
        string host = address.Trim();
        int port = Proto.VideoPort;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out var p))
        {
            port = p;
            host = host[..colon];
        }
        status($"Conectando a {host}…");
        var (conn, peer) = await RemotePull.RequestAsync(host, port, Channel(), code, s => Ui(() => status(s)), CancellationToken.None);
        Trust.Touch(peer.Fingerprint, peer.Name, port == Proto.VideoPort ? host : $"{host}:{port}");
        status($"Autorizado. Recebendo a tela de {peer.Name}…");
        _ = Task.Run(() =>
        {
            try { _receiver.ServeIncomingStream(conn, peer, requestedByUs: true); }
            finally { conn.Dispose(); }
        });
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
            startWithControl: Settings.ViewerAutoControl, audio: s.HasAudio);
        viewer.UserClosed += () => s.Close();
        s.FrameReady += viewer.NotifyFrame;
        s.StatsUpdated += viewer.SetStats;
        s.CursorModeChanged += viewer.SetRemoteCursorCaptured;
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
        string? lastNames = null;
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

            // Registra no outro computador quais controles estão aqui (ajuda a diagnosticar).
            string names = string.Join("; ", forwarder.Names);
            if (names != lastNames)
            {
                lastNames = names;
                s.SendInfo(names.Length == 0 ? "Controles no computador que exibe: nenhum" : "Controles no computador que exibe: " + names);
            }
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
            var session = await SenderSession.ConnectAsync(host, port, BuildSenderOptions(), Channel(), CancellationToken.None);
            RegisterSender(session);
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

    private SenderOptions BuildSenderOptions() => new()
    {
        Name = Settings.DeviceName,
        MonitorDeviceName = Settings.SenderMonitor,
        Fps = Settings.SenderFps,
        Quality = Settings.SenderQuality,
        Audio = Settings.SenderAudio,
        AllowControl = Settings.SenderAllowControl,
        Resolution = Settings.SenderResolution,
    };

    /// <summary>Passa a acompanhar uma transmissão iniciada (normal ou por acesso remoto). Thread da interface.</summary>
    private void RegisterSender(SenderSession session)
    {
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
            Approvals.BroadcastStatus();
            Changed();
        });
        _sender = session;
        Approvals.BroadcastStatus();
        Changed();
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
            new Size(format.Characteristics.Width, format.Characteristics.Height), audio: !string.IsNullOrEmpty(audioInputId));
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
        _updateTimer?.Dispose();
        _showWait.Unregister(null);
        _showEvent.Dispose();
        _quitWait.Unregister(null);
        _quitEvent.Dispose();
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
