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

    /// <summary>"auto" (tela do receptor), "original" ou um limite como "1280x720".</summary>
    public string Resolution { get; init; } = "auto";
}

/// <summary>Limites efetivos da transmissão, combinando a configuração local e o pedido do receptor.</summary>
internal readonly record struct StreamLimits(int MaxWidth, int MaxHeight, int Fps, int Quality, bool Light)
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
        int fps = opt.Fps, quality = opt.Quality;
        if (welcome.Light)
        {
            mw = mw <= 0 ? LightMode.MaxWidth : Math.Min(mw, LightMode.MaxWidth);
            mh = mh <= 0 ? LightMode.MaxHeight : Math.Min(mh, LightMode.MaxHeight);
            fps = Math.Min(fps, LightMode.MaxFps);
            quality = Math.Min(quality, LightMode.MaxQuality);
        }
        return new StreamLimits(mw, mh, fps, quality, welcome.Light);
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
    private readonly SemaphoreSlim _inflight = new(Proto.MaxFramesInFlight, Proto.MaxFramesInFlight);
    private readonly CancellationTokenSource _cts = new();
    private System.Threading.Timer? _pingTimer;
    private int _ended;
    private int _frameId;

    // Diagnóstico e avisos ao receptor
    private DiagRelay? _diag;
    private volatile string _captureState = "";
    private volatile bool _captureBlocked;
    private bool _blockedNoticeSent;
    private int _elevatedNoticePid;
    private long _lastSnapshotTick;
    private string? _lastSnapshot;

    // Estatísticas (janela de ~1 s)
    private int _statFrames;
    private long _statEncodeTicks;
    private long _statBytesAtLast;
    private long _statLastTs = Stopwatch.GetTimestamp();
    private double _rttMs = double.NaN;

    public string ReceiverName { get; }
    public string RemoteEndPoint => _conn.RemoteEndPoint;
    public bool IsRunning => Volatile.Read(ref _ended) == 0;

    /// <summary>Controles de jogo: "ok", "sem-driver" ou "desligado".</summary>
    public string GamepadStatus { get; }

    /// <summary>Texto de estatísticas a cada segundo (thread de segundo plano).</summary>
    public event Action<string>? StatsUpdated;

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

        new Thread(VideoLoop) { IsBackground = true, Name = "TransLocal vídeo", Priority = ThreadPriority.AboveNormal }.Start();
        new Thread(ReadLoop) { IsBackground = true, Name = "TransLocal leitura" }.Start();

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
        ScreenCapturer? capturer = null;
        TileEncoder? encoder = null;
        FrameScaler? scaler = null;
        bool needFrame = true;
        var ct = _cts.Token;
        try
        {
            long interval = Stopwatch.Frequency / Math.Clamp(_limits.Fps, 1, 120);
            long next = Stopwatch.GetTimestamp();
            long lastBoundsCheck = Stopwatch.GetTimestamp();
            long lastCursorCheck = 0;
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
                if (capturer is null)
                {
                    capturer = ScreenCapturer.Create(screen);
                    var size = FrameScaler.Fit(new Size(capturer.Width, capturer.Height), _limits.MaxWidth, _limits.MaxHeight);
                    scaler = size == new Size(capturer.Width, capturer.Height)
                        ? null
                        : new FrameScaler(capturer.Width, capturer.Height, size.Width, size.Height);
                    encoder = new TileEncoder(size.Width, size.Height);
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

                if (!_inflight.Wait(500, ct)) continue;

                bool sent = false;
                try
                {
                    // Sem mudança na tela (nem no cursor): não precisa reduzir nem comparar a imagem.
                    bool got = capturer.Capture(drawCursor: true);
                    if (capturer is DxgiCapturer dx)
                    {
                        _captureState = dx.State;
                        _captureBlocked = dx.Blocked;
                    }
                    else _captureState = capturer.Method;
                    if (got && (capturer.LastCaptureChanged || needFrame))
                    {
                        needFrame = false;
                        long t0 = Stopwatch.GetTimestamp();
                        IntPtr bits = capturer.Bits;
                        if (scaler is not null)
                        {
                            scaler.Scale(capturer);
                            bits = scaler.Bits;
                        }
                        using var frame = encoder!.Encode(bits, _limits.Quality, Interlocked.Increment(ref _frameId));
                        Interlocked.Add(ref _statEncodeTicks, Stopwatch.GetTimestamp() - t0);
                        if (frame.TileCount > 0)
                        {
                            _conn.Send(MsgType.Frame, frame.Span);
                            sent = true;
                            Interlocked.Increment(ref _statFrames);
                        }
                    }
                }
                finally
                {
                    if (!sent) _inflight.Release();
                }

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
            capturer?.Dispose();
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
                        break;
                    case MsgType.Input:
                        if (_opt.AllowControl) ApplyInput(m.Span);
                        break;
                    case MsgType.Diag:
                        _diag?.Received(m.Span);
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
        long bytes = Interlocked.Read(ref _conn.BytesSent);
        long deltaBytes = bytes - Interlocked.Exchange(ref _statBytesAtLast, bytes);
        double mbps = deltaBytes * 8 / secs / 1_000_000;
        double encMs = frames > 0 ? encTicks * 1000.0 / Stopwatch.Frequency / frames : 0;
        string rtt = double.IsNaN(_rttMs) ? "–" : $"{_rttMs:0.#} ms";
        string size = _sendSize.IsEmpty ? "" : $"{_sendSize.Width}×{_sendSize.Height}{(_limits.Light ? " (leve)" : "")} • ";
        StatsUpdated?.Invoke($"{size}{frames / secs:0} quadros/s • {mbps:0.0} Mbit/s • codificação {encMs:0} ms • ida e volta {rtt}");

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
                    ? "No PC, aba Transmitir, ative “Controlar programas abertos como administrador”."
                    : "O controle de administrador está ativado, mas o auxiliar não respondeu; reinicie o TransLocal no PC.";
                SendNotice($"{fg.Name} está aberto como administrador no PC: o Windows bloqueia mouse e teclado vindos de outros programas. {how}");
            }
        }
        else if (!fg.Elevated)
        {
            _elevatedNoticePid = 0;
        }

        if (_captureBlocked && !_blockedNoticeSent)
        {
            _blockedNoticeSent = true;
            SendNotice($"{(fg.Pid != 0 ? fg.Name : "O programa em tela cheia")} não deixa capturar a tela (tela cheia exclusiva). " +
                       "No jogo, troque o modo de tela para “Janela sem bordas” (Borderless) — o controle continua funcionando.");
        }
        else if (!_captureBlocked) _blockedNoticeSent = false;

        // Estado resumido: só quando muda (ou a cada 30 s), para o arquivo de diagnóstico ser leve.
        string control = !_opt.AllowControl ? "desligado" : bridge is null ? "comum" : elevatedOk ? "comum + administrador" : "comum (auxiliar ausente)";
        string snapshot = $"captura: {_captureState} • primeiro plano: {fg} • cursor {(_cursorCaptured ? "preso (relativo)" : "livre")} • controle: {control}";
        long now = Environment.TickCount64;
        if (snapshot != _lastSnapshot || now - _lastSnapshotTick > 30_000)
        {
            _lastSnapshot = snapshot;
            _lastSnapshotTick = now;
            _diag?.Snapshot($"{snapshot} • {fps:0} q/s • {mbps:0.0} Mbit/s • ida e volta {rtt}");
        }
    }

    internal void SendNotice(string text)
    {
        Log.Warn("Aviso ao receptor: " + text);
        if (!IsRunning) return;
        try { _conn.Send(MsgType.Notice, System.Text.Encoding.UTF8.GetBytes(text)); } catch { }
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
        Log.Info($"Transmissão encerrada: {reason ?? "parada pelo usuário"}");
        _diag?.Dispose();
        try { Ended?.Invoke(this, reason); } catch (Exception ex) { Log.Error("Erro no evento de fim", ex); }
    }

    public void Stop() => End(null);

    public void Dispose() => End(null);
}
