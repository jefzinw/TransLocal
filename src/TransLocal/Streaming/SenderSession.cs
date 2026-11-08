using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TransLocal.Audio;
using TransLocal.Input;
using TransLocal.Security;
using TransLocal.Video;

namespace TransLocal.Streaming;

internal sealed class SenderOptions
{
    public string Name { get; init; } = Environment.MachineName;
    public string? MonitorDeviceName { get; init; }
    public int Fps { get; init; } = 30;
    public int Quality { get; init; } = 75;
    public bool Audio { get; init; } = true;
    public bool AllowControl { get; init; }

    /// <summary>Usar vídeo H.264 quando o receptor suportar (muito menos rede e processador).</summary>
    public bool H264 { get; init; } = true;

    /// <summary>"auto" (tela do receptor), "original" ou um limite como "1280x720".</summary>
    public string Resolution { get; init; } = "auto";
}

/// <summary>Limites efetivos da transmissão, combinando a configuração local e o pedido do receptor.</summary>
internal readonly record struct StreamLimits(int MaxWidth, int MaxHeight, int Fps, int Quality, bool Light, bool H264 = false, bool Udp = false,
    bool HardwareDecode = false)
{
    public static StreamLimits Compute(SenderOptions opt, WelcomeMessage welcome)
    {
        int mw = 0, mh = 0;
        switch (opt.Resolution?.Trim().ToLowerInvariant())
        {
            case "original":
                break;
            case null or "" or "auto":
                mw = welcome.ScreenWidth;
                mh = welcome.ScreenHeight;
                break;
            default:
                var parts = opt.Resolution.ToLowerInvariant().Split('x', '×');
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
                {
                    mw = w;
                    mh = h;
                }
                break;
        }
        // Com H.264, mandar mais pixels do que a tela de quem exibe só custa: rede, decodificação e redução no
        // receptor (no notebook com Intel UHD 600, 1080p → 768p gerava quadros de 40–67 ms e picos de atraso).
        // A redução aqui, na placa do PC, tem a mesma qualidade e é de graça.
        if (opt.H264 && welcome.Codecs?.Contains("h264", StringComparer.OrdinalIgnoreCase) == true &&
            welcome.ScreenWidth > 0 && welcome.ScreenHeight > 0)
        {
            mw = mw <= 0 ? welcome.ScreenWidth : Math.Min(mw, welcome.ScreenWidth);
            mh = mh <= 0 ? welcome.ScreenHeight : Math.Min(mh, welcome.ScreenHeight);
        }
        int fps = opt.Fps, quality = opt.Quality;
        bool h264 = opt.H264 && welcome.Codecs?.Contains("h264", StringComparer.OrdinalIgnoreCase) == true;
        if (welcome.Light)
        {
            mw = mw <= 0 ? LightMode.MaxWidth : Math.Min(mw, LightMode.MaxWidth);
            mh = mh <= 0 ? LightMode.MaxHeight : Math.Min(mh, LightMode.MaxHeight);
            // Com H.264 o notebook decodifica pela placa de vídeo: 60 quadros/s cabem no modo leve.
            fps = Math.Min(fps, h264 ? 60 : LightMode.MaxFps);
            // O limite de qualidade do modo leve é para os blocos JPEG (pesados de decodificar). No H.264 o custo de
            // decodificar não depende da qualidade: reduzi-la só pixelava a imagem.
            if (!h264) quality = Math.Min(quality, LightMode.MaxQuality);
        }
        bool udp = h264 && welcome.Transports?.Contains("udp1", StringComparer.OrdinalIgnoreCase) == true &&
                   Environment.GetEnvironmentVariable("TRANSLOCAL_NO_UDP") != "1";
        bool hw = h264 && welcome.Codecs?.Contains("h264-hw", StringComparer.OrdinalIgnoreCase) == true;
        return new StreamLimits(mw, mh, fps, quality, welcome.Light, h264, udp, hw);
    }
}

/// <summary>Transmite a tela (e o som) deste computador para um receptor.</summary>
internal sealed class SenderSession : IDisposable
{
    private readonly Connection _conn;
    private readonly SenderOptions _opt;
    private readonly Screen _screen;
    private readonly LoopbackAudioSource? _audio;
    private readonly InputInjector? _injector;
    private readonly VirtualGamepads? _gamepads;
    private readonly StreamLimits _limits;
    private Size _sendSize;
    private bool _cursorCaptured;
    private SemaphoreSlim _inflight = new(Proto.MaxFramesInFlight, Proto.MaxFramesInFlight);

    // Vídeo por UDP (com paridade Reed-Solomon e AES-GCM); o TCP fica para controle, som e confirmações.
    private UdpVideoSender? _udp;
    private volatile bool _udpWasActive;
    private int _lastAckedId;
    private double _lossRate;
    private int _framesLostWindow, _framesRecoveredWindow;
    private volatile string _transportLabel = "TCP";
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _keyRequested;
    private volatile string _codecLabel = "JPEG por blocos";
    private System.Threading.Timer? _pingTimer;
    private int _ended;
    private int _frameId;

    // Diagnóstico e avisos ao receptor
    private DiagRelay? _diag;
    private volatile string _captureState = "";
    private volatile bool _captureBlocked;
    private volatile bool _captureSecure;
    private bool _secureNoticeSent;
    private bool _blockedNoticeSent;
    private int _elevatedNoticePid;
    private long _lastSnapshotTick;
    private string? _lastSnapshot;

    // Estatísticas (janela de ~1 s)
    private int _statFrames;
    private long _statEncodeTicks, _statCaptureTicks, _statScaleTicks;
    private long _statBytesAtLast;
    private long _statLastTs = Stopwatch.GetTimestamp();
    private double _rttMs = double.NaN;
    private int _statBlocked;

    // Atraso de cada quadro: da captura até o receptor confirmar que ele já está na tela.
    private readonly long[] _captureTs = new long[256];
    private readonly int[] _captureId = new int[256];
    private readonly object _latGate = new();
    private double _latSum, _latMax;
    private int _latCount;

    // Taxa de bits adaptativa (H.264): cai rápido quando a rede engasga e sobe devagar quando volta.
    private volatile int _targetBitrate;
    private int _maxBitrate, _goodSeconds, _badSeconds;
    private readonly Queue<double> _latHistory = new();
    private IDisposable? _wifi;

    public string ReceiverName { get; }
    public string RemoteEndPoint => _conn.RemoteEndPoint;
    public bool IsRunning => Volatile.Read(ref _ended) == 0;

    /// <summary>Controles de jogo: "ok", "sem-driver" ou "desligado".</summary>
    public string GamepadStatus { get; }

    /// <summary>Texto de estatísticas a cada segundo (thread de segundo plano).</summary>
    public event Action<string>? StatsUpdated;

    /// <summary>Um programa de administrador está em primeiro plano e o controle não chega a ele (nome do programa).</summary>
    public event Action<string>? ElevatedBlocked;

    /// <summary>Fim da sessão; o texto é o motivo (null quando parada localmente).</summary>
    public event Action<SenderSession, string?>? Ended;

    /// <summary>Limites efetivos (resolução máxima, quadros/s, qualidade, modo leve).</summary>
    public StreamLimits Limits => _limits;

    private SenderSession(Connection conn, SenderOptions opt, string receiverName, Screen screen, LoopbackAudioSource? audio,
        string gamepadStatus, StreamLimits limits)
    {
        _conn = conn;
        _opt = opt;
        _screen = screen;
        _audio = audio;
        _limits = limits;
        ReceiverName = receiverName;
        GamepadStatus = gamepadStatus;
        if (opt.AllowControl) _injector = new InputInjector(screen.Bounds);
        if (gamepadStatus == VirtualGamepads.StatusOk)
        {
            _gamepads = new VirtualGamepads();
            _gamepads.Rumble += (slot, large, small) =>
            {
                if (!IsRunning) return;
                try { _conn.Send(MsgType.GamepadFeedback, GamepadCodec.EncodeRumble(slot, large, small)); } catch { }
            };
        }
    }

    /// <summary>Conecta (canal seguro, pareando se preciso) e começa a transmitir para o receptor.</summary>
    public static async Task<SenderSession> ConnectAsync(string host, int port, SenderOptions opt, SecureChannelOptions channel, CancellationToken ct)
    {
        var address = await ResolveAsync(host, ct);
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(address, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Sem resposta de {address}:{port}. Confira o cabo, o endereço e se o TransLocal está aberto e liberado no firewall do outro computador.");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new IOException($"Não foi possível conectar a {address}:{port} ({ex.SocketErrorCode}). Confira se o outro computador está com “Aceitar transmissões” ligado e liberado no firewall.", ex);
        }

        return await Task.Run(() =>
        {
            var (conn, peer) = SecureChannel.Connect(client, ChannelPurpose.Stream, channel);
            try
            {
                return Handshake(conn, opt);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }, ct);
    }

    /// <summary>
    /// Começa a transmitir numa conexão já autenticada (acesso remoto: o outro aparelho pediu a tela).
    /// Bloqueante. Em caso de erro, a conexão é fechada.
    /// </summary>
    public static SenderSession StartOnConnection(Connection conn, SenderOptions opt)
    {
        try
        {
            return Handshake(conn, opt);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static SenderSession Handshake(Connection conn, SenderOptions opt)
    {
        var screen = Monitors.Resolve(opt.MonitorDeviceName);
        LoopbackAudioSource? audio = null;
        if (opt.Audio)
        {
            try { audio = new LoopbackAudioSource(); }
            catch (Exception ex) { Log.Warn("Som do sistema indisponível; transmitindo sem áudio", ex); }
        }

        string gamepadStatus = VirtualGamepads.StatusDisabled;
        if (opt.AllowControl)
        {
            gamepadStatus = VirtualGamepads.IsDriverInstalled(out var gpError) ? VirtualGamepads.StatusOk : VirtualGamepads.StatusNoDriver;
            if (gpError is not null) Log.Info("Controles de jogo indisponíveis: " + gpError);
        }

        try
        {
            conn.ReceiveTimeoutMs = 10_000;
            conn.SendJson(MsgType.Hello, new HelloMessage
            {
                Name = opt.Name,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                Audio = audio is not null,
                AudioRate = audio?.SampleRate ?? 0,
                AudioChannels = audio?.Channels ?? 0,
                Control = opt.AllowControl,
                Gamepads = gamepadStatus,
            });

            WelcomeMessage welcome;
            using (var m = conn.Receive())
            {
                if (m.Type != MsgType.Welcome) throw new InvalidDataException("Resposta inesperada do receptor.");
                welcome = m.ReadJson<WelcomeMessage>();
            }
            if (!welcome.Accepted)
                throw new InvalidOperationException(welcome.Reason ?? "O receptor recusou a transmissão.");

            var session = new SenderSession(conn, opt, welcome.Name, screen, audio, gamepadStatus, StreamLimits.Compute(opt, welcome));
            session.Start();
            return session;
        }
        catch
        {
            audio?.Dispose();
            throw;
        }
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = await Dns.GetHostAddressesAsync(host, ct);
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? throw new IOException($"Não foi possível encontrar o computador “{host}”.");
    }

    private void Start()
    {
        string maxRes = _limits.MaxWidth > 0 ? $"até {_limits.MaxWidth}×{_limits.MaxHeight}" : "original";
        Log.Info($"Transmitindo {_screen.DeviceName} {_screen.Bounds} para {ReceiverName} ({RemoteEndPoint}), resolução {maxRes}, " +
                 $"{_limits.Fps} fps, qualidade {_limits.Quality}{(_limits.Light ? " (modo leve pedido pelo receptor)" : "")}, " +
                 $"áudio={_audio is not null}, controle={_opt.AllowControl}, controles de jogo={GamepadStatus}");

        _diag = new DiagRelay(ReceiverName, "transmitindo", DiagRelay.ShareEnabled ? SendDiag : null);
        if (_opt.AllowControl)
        {
            var bridge = ElevatedInputBridge.Current;
            Log.Info(bridge is null
                ? "Controle de programas de administrador: desligado (Gerenciador de Tarefas e jogos “como administrador” não recebem mouse/teclado)"
                : $"Controle de programas de administrador: {(bridge.IsConnected ? "conectado" : "aguardando o auxiliar")}");
        }

        _wifi = Realtime.WifiLowLatency();
        if (_limits.Udp && _conn.RemoteAddress is { } peer)
        {
            try
            {
                _udp = new UdpVideoSender(peer);
                _udp.PeerReady += ep => Log.Info($"Vídeo por UDP com correção de erros: {ReceiverName} recebe em {ep} (paridade {_udp.Fec:P0})");
                _conn.SendJson(MsgType.UdpSetup, new UdpSetupMessage { Port = _udp.Port, Key = Convert.ToBase64String(_udp.Key), Session = _udp.Session });
                _transportLabel = "TCP (aguardando UDP)";
            }
            catch (Exception ex)
            {
                Log.Warn("UDP indisponível; vídeo segue pelo TCP", ex);
                _udp?.Dispose();
                _udp = null;
            }
        }
        new Thread(VideoLoop) { IsBackground = true, Name = "TransLocal vídeo", Priority = ThreadPriority.Highest }.Start();
        new Thread(ReadLoop) { IsBackground = true, Name = "TransLocal leitura", Priority = ThreadPriority.AboveNormal }.Start();

        if (_audio is not null)
        {
            _audio.Pcm16Available += data =>
            {
                if (!IsRunning) return;
                try { _conn.Send(MsgType.Audio, data); } catch { }
            };
            try { _audio.Start(); }
            catch (Exception ex) { Log.Warn("Falha ao iniciar captura de áudio", ex); }
        }

        _pingTimer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    private void VideoLoop()
    {
        Native.timeBeginPeriod(1);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
        var mmcss = Realtime.BoostThread("Capture");
        ScreenCapturer? capturer = null;
        TileEncoder? encoder = null;
        H264Encoder? h264 = null;
        GpuEncodePipeline? gpuPipe = null;
        FrameScaler? scaler = null;

        // Caminho todo na placa (captura → cursor → redução/conversão → codificador). Devolve false se não deu.
        bool StartGpuPipeline(ScreenCapturer cap, Size size, int bitrate)
        {
            gpuPipe?.Dispose();
            gpuPipe = null;
            if (cap is DxgiCapturer dxc0) dxc0.GpuMode = false;
            if (!_limits.H264) return false;
            gpuPipe = GpuEncodePipeline.TryCreate(cap, size.Width, size.Height, _limits.Fps, bitrate, _limits.HardwareDecode);
            if (gpuPipe is null) return false;
            if (cap is DxgiCapturer dxc) dxc.GpuMode = true;
            h264?.Dispose();
            h264 = gpuPipe.Encoder;
            return true;
        }

        void StopGpuPipeline(ScreenCapturer? cap, string why)
        {
            if (gpuPipe is null) return;
            Log.Warn($"Caminho na placa de vídeo desligado ({why}); reduzindo e convertendo pelo processador");
            if (ReferenceEquals(h264, gpuPipe.Encoder)) h264 = null;
            gpuPipe.Dispose();
            gpuPipe = null;
            if (cap is DxgiCapturer dxc) dxc.GpuMode = false;
        }
        bool needFrame = true;
        bool needKey = true;
        var ct = _cts.Token;
        try
        {
            long interval = Stopwatch.Frequency / Math.Clamp(_limits.Fps, 1, 120);
            int intervalMs = (int)Math.Max(1, interval * 1000 / Stopwatch.Frequency);
            long next = Stopwatch.GetTimestamp();
            long lastFrame = 0;
            long lastBoundsCheck = Stopwatch.GetTimestamp();
            long lastCursorCheck = 0;
            long lastDxgiRetry = 0;
            var screen = _screen;

            while (!ct.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                if (now - lastBoundsCheck > Stopwatch.Frequency)
                {
                    lastBoundsCheck = now;
                    var s = Monitors.Resolve(_opt.MonitorDeviceName);
                    if (s.Bounds != screen.Bounds || s.DeviceName != screen.DeviceName)
                    {
                        Log.Info($"Monitor mudou: {screen.DeviceName} {screen.Bounds} -> {s.DeviceName} {s.Bounds}");
                        screen = s;
                        capturer?.Dispose();
                        capturer = null;
                    }
                }
                // Começou na captura GDI porque o DXGI recusou (ex.: confirmação do UAC na tela quando o jogo abriu):
                // tenta voltar ao DXGI a cada 5 s, mantendo a mesma resolução e o mesmo codificador.
                if (capturer is GdiCapturer gdi && now - lastDxgiRetry > Stopwatch.Frequency * 5)
                {
                    lastDxgiRetry = now;
                    try
                    {
                        var dx = new DxgiCapturer(screen);
                        if (dx.Width == gdi.Width && dx.Height == gdi.Height)
                        {
                            if (gpuPipe is not null)
                            {
                                // O caminho na placa estava lendo a captura GDI: passa a usar a placa da captura DXGI.
                                StopGpuPipeline(gdi, "troca de captura");
                                StartGpuPipeline(dx, _sendSize, _targetBitrate);
                            }
                            gdi.Dispose();
                            capturer = dx;
                            needFrame = true;
                            Log.Info($"Captura DXGI disponível de novo: saindo da captura GDI ({dx.State})");
                        }
                        else dx.Dispose();
                    }
                    catch { /* continua no GDI; nova tentativa em 5 s */ }
                }
                if (capturer is null)
                {
                    capturer = ScreenCapturer.Create(screen);
                    lastDxgiRetry = Stopwatch.GetTimestamp();
                    var size = FrameScaler.Fit(new Size(capturer.Width, capturer.Height), _limits.MaxWidth, _limits.MaxHeight);
                    if (_limits.H264) size = new Size(size.Width & ~1, size.Height & ~1); // H.264 pede dimensões pares
                    scaler = size == new Size(capturer.Width, capturer.Height)
                        ? null
                        : new FrameScaler(capturer.Width, capturer.Height, size.Width, size.Height);
                    encoder = new TileEncoder(size.Width, size.Height);
                    if (gpuPipe is not null) StopGpuPipeline(null, "captura recriada");
                    h264?.Dispose();
                    h264 = null;
                    if (_limits.H264)
                    {
                        int bitrate = H264Encoder.BitrateFor(size.Width, size.Height, _limits.Fps, _limits.Quality);
                        if (!StartGpuPipeline(capturer, size, bitrate))
                        {
                            try { h264 = H264Encoder.TryCreate(size.Width, size.Height, _limits.Fps, bitrate, high: _limits.HardwareDecode); }
                            catch (Exception ex) { Log.Warn("H.264: falha ao criar o codificador", ex); }
                        }
                        _codecLabel = h264 is null ? "JPEG por blocos (H.264 indisponível)"
                            : gpuPipe is not null ? "H.264 placa de vídeo (captura→codificação toda na placa)"
                            : $"H.264 {(h264.Hardware ? "placa de vídeo" : "software")}";
                        if (gpuPipe is not null) Log.Info($"Captura, cursor, redução e conversão na placa de vídeo; codificador {h264!.Name} lendo a textura direto ({(h264.Profile == 100 ? "perfil High" : h264.Profile == 77 ? "perfil Main" : "perfil Baseline")})");
                        _maxBitrate = bitrate;
                        _targetBitrate = bitrate;
                        Log.Info(h264 is null ? "H.264 indisponível neste PC; usando blocos JPEG" : $"Vídeo H.264: {h264.Name} ({(h264.Hardware ? "placa de vídeo" : "software")}), {size.Width}×{size.Height} a {_limits.Fps} q/s, {bitrate / 1e6:0.0} Mbit/s");
                    }
                    _sendSize = size;
                    needFrame = true;
                    _injector?.SetMonitor(capturer.Bounds);
                    ElevatedInputBridge.Current?.SetMonitor(capturer.Bounds);
                    Log.Info($"Captura {capturer.Method} de {screen.DeviceName} {capturer.Width}×{capturer.Height}" +
                             (scaler is null ? "" : $", enviada em {size.Width}×{size.Height}"));
                }

                // Jogo prendeu/escondeu o cursor? O receptor passa a enviar movimento relativo do mouse.
                if (_opt.AllowControl && now - lastCursorCheck > Stopwatch.Frequency / 10)
                {
                    lastCursorCheck = now;
                    bool captured = CursorCapture.IsCaptured(capturer.Bounds);
                    if (captured != _cursorCaptured)
                    {
                        _cursorCaptured = captured;
                        Log.Info(captured ? "Cursor preso por um jogo: mouse relativo" : "Cursor livre: mouse absoluto");
                        _conn.Send(MsgType.CursorMode, new[] { (byte)(captured ? 1 : 0) });
                    }
                }

                bool viaUdp = _udp?.Active == true;
                var inflight = _inflight;
                bool acquired = false;
                if (viaUdp)
                {
                    // Por UDP nada fica preso na fila: só segura se o receptor estiver muito atrás (~0,4 s a 60 q/s).
                    // Com 8 quadros, um atraso curto nas confirmações (Wi-Fi do notebook) congelava a imagem à toa.
                    if (Volatile.Read(ref _frameId) - Volatile.Read(ref _lastAckedId) > 24)
                    {
                        Interlocked.Increment(ref _statBlocked);
                        Thread.Sleep(2);
                        continue;
                    }
                }
                else
                {
                    if (!inflight.Wait(0))
                    {
                        // O receptor ainda não mostrou os quadros anteriores: a rede ou ele estão atrasados.
                        Interlocked.Increment(ref _statBlocked);
                        if (!inflight.Wait(500, ct)) continue;
                    }
                    acquired = true;
                }

                // Taxa de bits adaptativa decidida em Tick(): aplicada aqui, na thread do codificador.
                if (h264 is not null && _targetBitrate > 0 && _targetBitrate != h264.CurrentBitrate && !h264.SetBitrate(_targetBitrate))
                    _targetBitrate = h264.CurrentBitrate;

                bool sent = false;
                try
                {
                    bool frameSync = capturer.WaitsForFrames;
                    if (frameSync && lastFrame != 0)
                    {
                        // Limite de quadros/s: não captura antes de ~7/8 do intervalo desde o último quadro.
                        long early = lastFrame + interval - interval / 8 - Stopwatch.GetTimestamp();
                        if (early > 0) Thread.Sleep((int)Math.Max(1, early * 1000 / Stopwatch.Frequency));
                    }
                    // DXGI: espera o próximo quadro da tela (até um intervalo) e captura assim que o jogo apresenta.
                    int waitMs = !frameSync ? 0 : h264 is { Pending: true } ? 1 : needFrame || _keyRequested ? 0 : intervalMs;
                    // Sem mudança na tela (nem no cursor): não precisa reduzir nem comparar a imagem.
                    long tc = Stopwatch.GetTimestamp();
                    bool got = capturer.Capture(drawCursor: true, waitMs);
                    if (got && capturer.LastCaptureChanged) Interlocked.Add(ref _statCaptureTicks, Stopwatch.GetTimestamp() - tc);
                    if (capturer is DxgiCapturer dx)
                    {
                        _captureState = dx.State;
                        _captureBlocked = dx.Blocked;
                        _captureSecure = dx.SecureDesktop;
                    }
                    else _captureState = capturer.Method;
                    if (h264 is { Pending: true } && !(got && (capturer.LastCaptureChanged || needFrame)))
                    {
                        // Quadro que o codificador da placa de vídeo terminou depois: envia agora.
                        var late = h264.Poll(out bool lateKey);
                        if (late is { Length: > 0 }) sent = SendH264(late, lateKey, h264.LastOutputCaptureTime);
                    }
                    else if (got && (capturer.LastCaptureChanged || needFrame || (_keyRequested && h264 is not null)))
                    {
                        if (needFrame) needKey = true;
                        needFrame = false;
                        long t0 = Stopwatch.GetTimestamp();
                        lastFrame = t0;
                        IntPtr bits = capturer.Bits;
                        if (gpuPipe is not null && gpuPipe.IsStale(capturer))
                        {
                            // Dispositivo da captura recriado (driver, perda seguida): monta o caminho de novo.
                            StopGpuPipeline(capturer, "dispositivo da captura recriado");
                            if (!StartGpuPipeline(capturer, _sendSize, _targetBitrate))
                            {
                                try { h264 = H264Encoder.TryCreate(_sendSize.Width, _sendSize.Height, _limits.Fps, _targetBitrate, high: _limits.HardwareDecode); }
                                catch (Exception ex) { Log.Warn("H.264: falha ao recriar o codificador", ex); }
                            }
                            needKey = true;
                        }
                        if (scaler is not null && gpuPipe is null)
                        {
                            long ts = Stopwatch.GetTimestamp();
                            scaler.Scale(capturer);
                            Interlocked.Add(ref _statScaleTicks, Stopwatch.GetTimestamp() - ts);
                            bits = scaler.Bits;
                        }
                        if (h264 is not null)
                        {
                            byte[]? data = null;
                            bool key = false;
                            bool force = _keyRequested || needKey;
                            _keyRequested = false;
                            needKey = false;
                            try
                            {
                                data = gpuPipe is not null
                                    ? gpuPipe.Encode(capturer, force, out key, t0)
                                    : h264.Encode(bits, _sendSize.Width * 4, force, out key, t0);
                            }
                            catch (Exception ex) when (gpuPipe is not null)
                            {
                                // Falhou na placa: segue pelo processador com o codificador comum (sem derrubar o vídeo).
                                StopGpuPipeline(capturer, ex.Message);
                                try { h264 = H264Encoder.TryCreate(_sendSize.Width, _sendSize.Height, _limits.Fps, _targetBitrate, high: _limits.HardwareDecode); }
                                catch (Exception ex2) { Log.Warn("H.264: falha ao recriar o codificador", ex2); }
                                _codecLabel = h264 is null ? "JPEG por blocos (H.264 falhou)" : $"H.264 {(h264.Hardware ? "placa de vídeo" : "software")}";
                                needKey = true;
                                if (h264 is null) needFrame = true;
                            }
                            catch (Exception ex)
                            {
                                // Nunca derruba a transmissão: volta aos blocos JPEG (o receptor entende os dois).
                                Log.Warn("H.264: falha ao codificar; voltando aos blocos JPEG", ex);
                                h264.Dispose();
                                h264 = null;
                                _codecLabel = "JPEG por blocos (H.264 falhou)";
                            }
                            Interlocked.Add(ref _statEncodeTicks, Stopwatch.GetTimestamp() - t0);
                            if (data is { Length: > 0 }) sent = SendH264(data, key, h264?.LastOutputCaptureTime ?? t0);
                            if (h264 is not null && h264.FramesWithoutOutput > Math.Max(60, _limits.Fps * 2))
                            {
                                // Rede de segurança: o codificador aceita os quadros mas não devolve imagem (placa que
                                // recusa algum ajuste em silêncio). Desce um nível em vez de deixar a tela congelada.
                                int level = h264.Conservative ? 2 : h264.Hardware ? 1 : 3;
                                if (gpuPipe is not null)
                                {
                                    // Na placa não entregou imagem: tenta o caminho comum com os ajustes normais primeiro.
                                    StopGpuPipeline(capturer, "o codificador não entregou imagem");
                                    level = 0;
                                    h264 = null;
                                }
                                if (h264 is not null)
                                {
                                    Log.Warn($"H.264: {h264.Name} passou {h264.FramesWithoutOutput} quadros sem entregar imagem; " +
                                             (level switch { 1 => "tentando os ajustes conservadores", 2 => "usando o codificador por software", _ => "voltando aos blocos JPEG" }));
                                    h264.Dispose();
                                    h264 = null;
                                }
                                if (level <= 2)
                                {
                                    try { h264 = H264Encoder.TryCreate(_sendSize.Width, _sendSize.Height, _limits.Fps, Math.Max(_targetBitrate, 1_000_000), level, _limits.HardwareDecode); }
                                    catch (Exception ex) { Log.Warn("H.264: falha ao recriar o codificador", ex); }
                                }
                                needKey = true;
                                _codecLabel = h264 is null ? "JPEG por blocos (H.264 não entregou imagem)" : $"H.264 {(h264.Hardware ? "placa de vídeo" : "software")}{(h264.Conservative ? " (ajustes conservadores)" : "")}";
                                if (h264 is null) needFrame = true;
                            }
                        }
                        else
                        {
                            int jid = Interlocked.Increment(ref _frameId);
                            using var frame = encoder!.Encode(bits, _limits.Quality, jid);
                            Interlocked.Add(ref _statEncodeTicks, Stopwatch.GetTimestamp() - t0);
                            if (frame.TileCount > 0)
                            {
                                RememberCapture(jid, t0);
                                _conn.Send(MsgType.Frame, frame.Span);
                                sent = true;
                                Interlocked.Increment(ref _statFrames);
                            }
                        }
                    }
                }
                finally
                {
                    if (acquired && !sent) try { inflight.Release(); } catch (SemaphoreFullException) { }
                }

                if (capturer?.WaitsForFrames == true) continue; // o ritmo vem da própria tela
                next += interval;
                now = Stopwatch.GetTimestamp();
                if (next < now - interval) next = now;
                long wait = next - now;
                if (wait > 0) Thread.Sleep((int)(wait * 1000 / Stopwatch.Frequency));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Log.Error("Falha no envio de vídeo", ex);
                End(ex is IOException or ObjectDisposedException
                    ? "A ligação com o receptor foi perdida."
                    : "Falha na transmissão: " + ex.Message);
            }
        }
        finally
        {
            if (gpuPipe is not null && ReferenceEquals(h264, gpuPipe.Encoder)) h264 = null;
            gpuPipe?.Dispose();
            capturer?.Dispose();
            h264?.Dispose();
            Realtime.Revert(mmcss);
            Native.timeEndPeriod(1);
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        }
    }

    private void ReadLoop()
    {
        try
        {
            _conn.ReceiveTimeoutMs = 15_000;
            while (!_cts.IsCancellationRequested)
            {
                using var m = _conn.Receive();
                switch (m.Type)
                {
                    case MsgType.FrameAck:
                        try { _inflight.Release(); } catch (SemaphoreFullException) { }
                        if (m.Length >= 4)
                        {
                            int acked = BinaryPrimitives.ReadInt32LittleEndian(m.Span);
                            int prev;
                            while ((prev = Volatile.Read(ref _lastAckedId)) < acked && Interlocked.CompareExchange(ref _lastAckedId, acked, prev) != prev) { }
                            FrameShown(acked);
                        }
                        break;
                    case MsgType.Input:
                        if (_opt.AllowControl) ApplyInput(m.Span);
                        break;
                    case MsgType.Diag:
                        _diag?.Received(m.Span);
                        break;
                    case MsgType.NetReport when m.Length >= 16:
                        OnNetReport(m.Span);
                        break;
                    case MsgType.KeyframeRequest:
                        _keyRequested = true;
                        break;
                    case MsgType.Info when m.Length > 0:
                        Log.Info($"[{ReceiverName}] {System.Text.Encoding.UTF8.GetString(m.Span[..Math.Min(m.Length, 2000)])}");
                        break;
                    case MsgType.Gamepad:
                        if (_gamepads is not null && GamepadCodec.TryDecode(m.Span, out var pad))
                        {
                            try { _gamepads.Apply(pad); }
                            catch (Exception ex) { Log.Error($"Falha no controle virtual {pad.Slot + 1} (conectado={pad.Connected})", ex); }
                        }
                        break;
                    case MsgType.Ping:
                        _conn.Send(MsgType.Pong, m.Span);
                        break;
                    case MsgType.Pong when m.Length >= 8:
                        long sentAt = BinaryPrimitives.ReadInt64LittleEndian(m.Span);
                        _rttMs = (Stopwatch.GetTimestamp() - sentAt) * 1000.0 / Stopwatch.Frequency;
                        break;
                    case MsgType.Bye:
                        End("O outro computador encerrou a exibição.");
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                Log.Warn("Leitura do receptor terminou", ex);
                End("A ligação com o receptor foi perdida.");
            }
        }
    }

    private void Tick()
    {
        if (!IsRunning) return;
        try
        {
            Span<byte> ts = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(ts, Stopwatch.GetTimestamp());
            _conn.Send(MsgType.Ping, ts);
        }
        catch { }

        long now = Stopwatch.GetTimestamp();
        double secs = Math.Max(0.001, (now - Interlocked.Exchange(ref _statLastTs, now)) / (double)Stopwatch.Frequency);
        int frames = Interlocked.Exchange(ref _statFrames, 0);
        long encTicks = Interlocked.Exchange(ref _statEncodeTicks, 0);
        long capTicks = Interlocked.Exchange(ref _statCaptureTicks, 0), scaleTicks = Interlocked.Exchange(ref _statScaleTicks, 0);
        string Per(long t) => frames > 0 ? $"{t * 1000.0 / Stopwatch.Frequency / frames:0.0}" : "0";
        long bytes = Interlocked.Read(ref _conn.BytesSent);
        long deltaBytes = bytes - Interlocked.Exchange(ref _statBytesAtLast, bytes);
        double mbps = deltaBytes * 8 / secs / 1_000_000;
        double encMs = frames > 0 ? encTicks * 1000.0 / Stopwatch.Frequency / frames : 0;
        string rtt = double.IsNaN(_rttMs) ? "–" : $"{_rttMs:0.#} ms";
        string size = _sendSize.IsEmpty ? "" : $"{_sendSize.Width}×{_sendSize.Height}{(_limits.Light ? " (leve)" : "")} • ";
        int blocked = Interlocked.Exchange(ref _statBlocked, 0);
        if (_udpWasActive && _udp is { Active: false })
        {
            // O receptor parou de se apresentar por UDP (rede mudou, firewall): volta ao TCP com quadro-chave.
            _udpWasActive = false;
            _transportLabel = "TCP (UDP interrompido)";
            _inflight = new SemaphoreSlim(Proto.MaxFramesInFlight, Proto.MaxFramesInFlight);
            _keyRequested = true;
            Log.Warn("UDP: o receptor parou de responder; vídeo voltou ao TCP");
        }
        double latAvg = double.NaN, latMax = double.NaN;
        lock (_latGate)
        {
            if (_latCount > 0)
            {
                latAvg = _latSum / _latCount;
                latMax = _latMax;
            }
            _latSum = _latMax = 0;
            _latCount = 0;
        }
        // Atraso até a tela do outro lado ≈ captura → confirmação, menos a volta da confirmação (meia ida e volta).
        double half = double.IsNaN(_rttMs) ? 0 : _rttMs / 2;
        string lat = double.IsNaN(latAvg) ? "–" : $"{Math.Max(1, latAvg - half):0} ms (pico {Math.Max(1, latMax - half):0})";
        AdaptBitrate(latAvg, blocked, frames / secs);
        string rate = _targetBitrate > 0 && _limits.H264 && _maxBitrate > 0
            ? $" • alvo {_targetBitrate / 1e6:0.0} Mbit/s{(_targetBitrate < _maxBitrate ? " (rede limitada)" : "")}"
            : "";
        string transport = _udpWasActive ? $"UDP (perda {_lossRate:P1}, paridade {_udp?.Fec ?? 0:P0})" : _transportLabel;
        StatsUpdated?.Invoke($"{size}{frames / secs:0} quadros/s • {mbps:0.0} Mbit/s{rate} • {_codecLabel} • {transport} • codificação {encMs:0} ms (captura {Per(capTicks)} • redução {Per(scaleTicks)}) • " +
                             $"atraso da imagem {lat} • ida e volta {rtt}");
        try { _conn.Send(MsgType.Stats, System.Text.Encoding.UTF8.GetBytes($"atraso da imagem {lat} • ida e volta {rtt} • enviado {mbps:0.0} Mbit/s{rate} • {transport}")); }
        catch { }

        try { CheckCompatibility(frames / secs, mbps, rtt); }
        catch (Exception ex) { Log.Warn("Falha no diagnóstico", ex); }
    }

    /// <summary>
    /// Uma vez por segundo: avisa o receptor sobre o que impede o controle ou a imagem (programa de
    /// administrador em primeiro plano, tela cheia exclusiva) e registra um estado resumido para o diagnóstico.
    /// </summary>
    private void CheckCompatibility(double fps, double mbps, string rtt)
    {
        var fg = ProcessInfo.Foreground();
        var bridge = ElevatedInputBridge.Current;
        bool elevatedOk = bridge?.IsConnected == true;

        if (_opt.AllowControl && fg.Elevated && !elevatedOk)
        {
            if (fg.Pid != _elevatedNoticePid)
            {
                _elevatedNoticePid = fg.Pid;
                string how = bridge is null
                    ? "No PC, aba Transmitir, ative “Controlar programas de administrador e confirmações do Windows”."
                    : "O controle de administrador está ativado, mas o auxiliar não respondeu; reinicie o TransLocal no PC.";
                SendNotice($"{fg.Name} está aberto como administrador no PC: o Windows bloqueia mouse e teclado vindos de outros programas. {how}");
                if (bridge is null) try { ElevatedBlocked?.Invoke(fg.Name); } catch { }
            }
        }
        else if (!fg.Elevated)
        {
            _elevatedNoticePid = 0;
        }

        // Confirmação do Windows (UAC) ou tela de bloqueio à frente, sem o serviço para capturá-la e respondê-la.
        if (_captureSecure && !_secureNoticeSent)
        {
            _secureNoticeSent = true;
            SendNotice("O PC está mostrando uma confirmação do Windows (UAC) ou a tela de bloqueio. Ela só pode ser vista e respondida daqui " +
                       "com o serviço do TransLocal: no PC, aba Transmitir, ative “Controlar programas de administrador e confirmações do Windows”. " +
                       "Até lá, responda no próprio PC.");
            try { ElevatedBlocked?.Invoke("Uma confirmação do Windows (UAC)"); } catch { }
        }
        else if (!_captureSecure) _secureNoticeSent = false;

        if (_captureBlocked && !_blockedNoticeSent)
        {
            _blockedNoticeSent = true;
            SendNotice($"{(fg.Pid != 0 ? fg.Name : "O programa em tela cheia")} não deixa capturar a tela (tela cheia exclusiva). " +
                       "No jogo, troque o modo de tela para “Janela sem bordas” (Borderless) — o controle continua funcionando.");
        }
        else if (!_captureBlocked) _blockedNoticeSent = false;

        // Estado resumido: só quando muda (ou a cada 30 s), para o arquivo de diagnóstico ser leve.
        string control = !_opt.AllowControl ? "desligado" : bridge is null ? "comum"
            : elevatedOk ? (bridge.IsSystemAgent ? "comum + administrador + UAC (serviço)" : "comum + administrador (tarefa)") : "comum (auxiliar ausente)";
        string snapshot = $"captura: {_captureState} • vídeo: {_codecLabel} • primeiro plano: {fg} • cursor {(_cursorCaptured ? "preso (relativo)" : "livre")} • controle: {control}";
        long now = Environment.TickCount64;
        if (snapshot != _lastSnapshot || now - _lastSnapshotTick > 30_000)
        {
            _lastSnapshot = snapshot;
            _lastSnapshotTick = now;
            _diag?.Snapshot($"{snapshot} • {fps:0} q/s • {mbps:0.0} Mbit/s • ida e volta {rtt}");
        }
    }

    /// <summary>Relatório do receptor (UDP): perda de pacotes antes da correção e quadros perdidos/recuperados.</summary>
    private void OnNetReport(ReadOnlySpan<byte> r)
    {
        long packets = BinaryPrimitives.ReadUInt32LittleEndian(r);
        long lost = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
        int framesLost = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[8..]);
        int recovered = (int)BinaryPrimitives.ReadUInt32LittleEndian(r[12..]);
        double rate = packets + lost > 0 ? lost / (double)(packets + lost) : 0;
        _lossRate = rate;
        Interlocked.Add(ref _framesLostWindow, framesLost);
        Interlocked.Add(ref _framesRecoveredWindow, recovered);
        if (_udp is { } udp)
        {
            // Paridade proporcional à perda medida: 10% com a rede limpa, até 50% num Wi-Fi ruim.
            double fec = Math.Clamp(0.10 + rate * 4, 0.10, 0.50);
            if (Math.Abs(fec - udp.Fec) >= 0.05)
            {
                Log.Info($"UDP: perda de {rate:P1} → paridade {fec:P0} (quadros recuperados {recovered}, perdidos {framesLost})");
                udp.Fec = fec;
            }
        }
    }

    /// <summary>
    /// Controle de taxa de bits como nos serviços de jogos na nuvem: se o atraso sobe acima do normal desta ligação
    /// (fila se formando no Wi-Fi) ou o receptor deixa quadros esperando, reduz 30% na hora; com a rede folgada por
    /// 3 s seguidos, sobe 10% até o valor escolhido. Mantém a imagem fluida em vez de acumular atraso.
    /// </summary>
    private void AdaptBitrate(double latAvg, int blocked, double fps)
    {
        if (!_limits.H264 || _maxBitrate <= 0 || _targetBitrate <= 0) return;
        if (double.IsNaN(latAvg)) return;
        _latHistory.Enqueue(latAvg);
        while (_latHistory.Count > 10) _latHistory.Dequeue();
        double baseline = _latHistory.Min();
        double frameMs = 1000.0 / Math.Max(1, _limits.Fps);
        // Só reage a sinais claros de rede saturada: quadros perdidos por UDP / perda alta, ou atraso bem acima do
        // normal por 2 s seguidos. Variações pequenas de atraso (decodificação, Wi-Fi) não derrubam mais a qualidade.
        int framesLost = Interlocked.Exchange(ref _framesLostWindow, 0);
        Interlocked.Exchange(ref _framesRecoveredWindow, 0);
        bool udpBad = _udpWasActive && (framesLost > 0 || _lossRate > 0.08);
        bool latencyBad = latAvg > baseline + Math.Max(40, frameMs * 3);
        _badSeconds = latencyBad ? _badSeconds + 1 : 0;
        bool congested = udpBad || _badSeconds >= 2;
        // Piso alto: abaixo da metade da taxa escolhida a imagem pixela — melhor perder alguns quadros.
        int min = Math.Max(3_000_000, _maxBitrate / 2);
        int target = _targetBitrate;
        if (congested)
        {
            _goodSeconds = 0;
            target = Math.Max(min, (int)(target * 0.85));
            _badSeconds = 0;
        }
        else if (++_goodSeconds >= 2 && target < _maxBitrate)
        {
            target = Math.Min(_maxBitrate, (int)(target * 1.1));
        }
        if (target != _targetBitrate)
        {
            Log.Info($"Taxa de bits {(target < _targetBitrate ? "reduzida" : "aumentada")}: {_targetBitrate / 1e6:0.0} → {target / 1e6:0.0} Mbit/s " +
                     $"(atraso {latAvg:0} ms, normal {baseline:0} ms, {blocked} quadro(s) esperando)");
            _targetBitrate = target;
        }
    }

    internal void SendNotice(string text)
    {
        Log.Warn("Aviso ao receptor: " + text);
        if (!IsRunning) return;
        try { _conn.Send(MsgType.Notice, System.Text.Encoding.UTF8.GetBytes(text)); } catch { }
    }

    private void RememberCapture(int id, long captureTs)
    {
        lock (_latGate)
        {
            _captureId[id & 255] = id;
            _captureTs[id & 255] = captureTs;
        }
    }

    /// <summary>O receptor confirmou que o quadro já está na tela: registra o atraso desde a captura.</summary>
    private void FrameShown(int id)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_latGate)
        {
            if (_captureId[id & 255] != id || _captureTs[id & 255] == 0) return;
            double ms = (now - _captureTs[id & 255]) * 1000.0 / Stopwatch.Frequency;
            _captureTs[id & 255] = 0;
            if (ms < 0 || ms > 5000) return;
            _latSum += ms;
            _latCount++;
            if (ms > _latMax) _latMax = ms;
        }
    }

    private bool SendH264(byte[] data, bool key, long captureTs)
    {
        var msg = new byte[9 + data.Length];
        int id = Interlocked.Increment(ref _frameId);
        if (captureTs != 0) RememberCapture(id, captureTs);
        if (_udp is { Active: true } udp)
        {
            if (!_udpWasActive)
            {
                _udpWasActive = true;
                _transportLabel = "UDP + correção de erros";
            }
            udp.SendFrame(id, key, _sendSize.Width, _sendSize.Height, data);
            Interlocked.Add(ref _conn.BytesSent, data.Length); // estatística de taxa
            Interlocked.Increment(ref _statFrames);
            return true;
        }
        BinaryPrimitives.WriteInt32LittleEndian(msg, id);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(4), (ushort)_sendSize.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(6), (ushort)_sendSize.Height);
        msg[8] = (byte)(key ? 1 : 0);
        data.CopyTo(msg, 9);
        _conn.Send(MsgType.VideoH264, msg);
        Interlocked.Increment(ref _statFrames);
        return true;
    }

    private void SendDiag(byte[] payload)
    {
        if (!IsRunning) return;
        try { _conn.Send(MsgType.Diag, payload); } catch { }
    }

    /// <summary>Aplica mouse/teclado: pelo auxiliar elevado quando ligado (alcança janelas de administrador).</summary>
    private void ApplyInput(ReadOnlySpan<byte> payload)
    {
        var bridge = ElevatedInputBridge.Current;
        if (bridge is not null && bridge.TrySendInput(payload)) return;
        _injector?.Apply(payload);
    }

    private void End(string? reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _cts.Cancel();
        _pingTimer?.Dispose();
        if (reason is null) _conn.TrySendBye();
        _conn.Dispose();
        _audio?.Dispose();
        _injector?.ReleaseAll();
        ElevatedInputBridge.Current?.TrySendInput(InputCodec.EncodeReleaseAll());
        _gamepads?.Dispose();
        _wifi?.Dispose();
        _udp?.Dispose();
        Log.Info($"Transmissão encerrada: {reason ?? "parada pelo usuário"}");
        _diag?.Dispose();
        try { Ended?.Invoke(this, reason); } catch (Exception ex) { Log.Error("Erro no evento de fim", ex); }
    }

    public void Stop() => End(null);

    public void Dispose() => End(null);
}
