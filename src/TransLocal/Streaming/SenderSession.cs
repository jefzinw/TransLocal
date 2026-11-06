using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TransLocal.Audio;
using TransLocal.Input;
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
}

/// <summary>Transmite a tela (e o som) deste computador para um receptor.</summary>
internal sealed class SenderSession : IDisposable
{
    private readonly Connection _conn;
    private readonly SenderOptions _opt;
    private readonly Screen _screen;
    private readonly LoopbackAudioSource? _audio;
    private readonly InputInjector? _injector;
    private readonly SemaphoreSlim _inflight = new(Proto.MaxFramesInFlight, Proto.MaxFramesInFlight);
    private readonly CancellationTokenSource _cts = new();
    private System.Threading.Timer? _pingTimer;
    private int _ended;
    private int _frameId;

    // Estatísticas (janela de ~1 s)
    private int _statFrames;
    private long _statEncodeTicks;
    private long _statBytesAtLast;
    private long _statLastTs = Stopwatch.GetTimestamp();
    private double _rttMs = double.NaN;

    public string ReceiverName { get; }
    public string RemoteEndPoint => _conn.RemoteEndPoint;
    public bool IsRunning => Volatile.Read(ref _ended) == 0;

    /// <summary>Texto de estatísticas a cada segundo (thread de segundo plano).</summary>
    public event Action<string>? StatsUpdated;

    /// <summary>Fim da sessão; o texto é o motivo (null quando parada localmente).</summary>
    public event Action<SenderSession, string?>? Ended;

    private SenderSession(Connection conn, SenderOptions opt, string receiverName, Screen screen, LoopbackAudioSource? audio)
    {
        _conn = conn;
        _opt = opt;
        _screen = screen;
        _audio = audio;
        ReceiverName = receiverName;
        if (opt.AllowControl) _injector = new InputInjector(screen.Bounds);
    }

    public static async Task<SenderSession> ConnectAsync(string host, int port, SenderOptions opt, CancellationToken ct)
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

        var conn = new Connection(client);
        try
        {
            return await Task.Run(() => Handshake(conn, opt), ct);
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
            });

            WelcomeMessage welcome;
            using (var m = conn.Receive())
            {
                if (m.Type != MsgType.Welcome) throw new InvalidDataException("Resposta inesperada do receptor.");
                welcome = m.ReadJson<WelcomeMessage>();
            }
            if (!welcome.Accepted)
                throw new InvalidOperationException(welcome.Reason ?? "O receptor recusou a transmissão.");

            var session = new SenderSession(conn, opt, welcome.Name, screen, audio);
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
        Log.Info($"Transmitindo {_screen.DeviceName} {_screen.Bounds} para {ReceiverName} ({RemoteEndPoint}), {_opt.Fps} fps, qualidade {_opt.Quality}, áudio={_audio is not null}, controle={_opt.AllowControl}");

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
        var ct = _cts.Token;
        try
        {
            long interval = Stopwatch.Frequency / Math.Clamp(_opt.Fps, 1, 120);
            long next = Stopwatch.GetTimestamp();
            long lastBoundsCheck = Stopwatch.GetTimestamp();
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
                    encoder = new TileEncoder(capturer.Width, capturer.Height);
                    _injector?.SetMonitor(capturer.Bounds);
                    Log.Info($"Captura {capturer.Method} de {screen.DeviceName} {capturer.Width}×{capturer.Height}");
                }

                if (!_inflight.Wait(500, ct)) continue;

                bool sent = false;
                try
                {
                    if (capturer.Capture(drawCursor: true))
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        using var frame = encoder!.Encode(capturer.Bits, _opt.Quality, Interlocked.Increment(ref _frameId));
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
                        if (_opt.AllowControl) _injector?.Apply(m.Span);
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
        StatsUpdated?.Invoke($"{frames / secs:0} quadros/s • {mbps:0.0} Mbit/s • codificação {encMs:0} ms • ida e volta {rtt}");
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
        Log.Info($"Transmissão encerrada: {reason ?? "parada pelo usuário"}");
        try { Ended?.Invoke(this, reason); } catch (Exception ex) { Log.Error("Erro no evento de fim", ex); }
    }

    public void Stop() => End(null);

    public void Dispose() => End(null);
}
