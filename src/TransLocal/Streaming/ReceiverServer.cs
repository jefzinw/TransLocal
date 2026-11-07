using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using TransLocal.Audio;
using TransLocal.Input;
using TransLocal.Security;
using TransLocal.Video;

namespace TransLocal.Streaming;

/// <summary>
/// Aceita conexões (TCP 52020) e responde às procuras (UDP 52021). Toda conexão passa pelo canal seguro
/// (TLS + autenticação + pareamento) e depois é encaminhada conforme a finalidade: receber uma tela,
/// pedido de acesso remoto (alguém quer a tela deste computador) ou celular aprovador.
/// Exibe uma transmissão por vez.
/// </summary>
internal sealed class ReceiverServer : IDisposable
{
    private const int MaxConcurrentHandshakes = 8;
    private const int MaxFailuresPerAddress = 5;
    private static readonly TimeSpan BlockTime = TimeSpan.FromMinutes(5);

    private readonly int _port;
    private readonly int _discoveryPort;
    private readonly Func<SecureChannelOptions> _channel;
    private readonly Func<bool> _playAudio;
    private readonly Func<ReceiverPrefs> _prefs;
    private readonly Func<bool> _acceptStreams;
    private readonly Dictionary<IPAddress, (int Failures, DateTime Until)> _failures = new();
    private TcpListener? _listener;
    private DiscoveryResponder? _discovery;
    private CancellationTokenSource? _cts;
    private ReceiverSession? _active;
    private int _handshakes;

    public event Action<ReceiverSession>? SessionStarted;
    public event Action<ReceiverSession, string?>? SessionEnded;

    /// <summary>Aparelho autenticado (para atualizar “visto por último”).</summary>
    public event Action<PeerInfo, IPAddress?>? PeerAuthenticated;

    /// <summary>Pedido de acesso remoto. Retorna true se assumiu a conexão (ela não é fechada aqui).</summary>
    public Func<Connection, PeerInfo, bool>? PullHandler { get; set; }

    /// <summary>Celular aprovador conectado; bloqueia até ele sair.</summary>
    public Action<Connection, PeerInfo>? ApproverHandler { get; set; }

    public ReceiverServer(Func<SecureChannelOptions> channel, Func<bool> playAudio, Func<ReceiverPrefs>? prefs = null,
        Func<bool>? acceptStreams = null, int port = Proto.VideoPort, int discoveryPort = Proto.DiscoveryPort)
    {
        _channel = channel;
        _prefs = prefs ?? (() => default);
        _playAudio = playAudio;
        _acceptStreams = acceptStreams ?? (() => true);
        _port = port;
        _discoveryPort = discoveryPort;
    }

    public bool IsRunning => _listener is not null;
    public ReceiverSession? Active => Volatile.Read(ref _active);
    public int Port => _port;

    public void Start()
    {
        if (_listener is not null) return;
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        try
        {
            _discovery = new DiscoveryResponder(_discoveryPort, _port, () => _channel().LocalName, () => Active is not null, _channel().Identity.ShortId);
            _discovery.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"Descoberta automática indisponível (porta UDP {_discoveryPort})", ex);
        }
        _ = AcceptLoopAsync(listener, _cts.Token);
        Log.Info($"Aguardando conexões seguras na porta TCP {_port} (descoberta UDP {_discoveryPort})");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _discovery?.Dispose();
        _discovery = null;
        Active?.Close();
        Log.Info("Receptor desligado");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Warn("Falha ao aceitar conexão", ex);
                continue;
            }
            var t = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "TransLocal conexão" };
            t.Start();
        }
    }

    private bool IsBlocked(IPAddress? ip)
    {
        if (ip is null) return false;
        lock (_failures)
            return _failures.TryGetValue(ip, out var f) && f.Failures >= MaxFailuresPerAddress && DateTime.UtcNow < f.Until;
    }

    private void RegisterFailure(IPAddress? ip)
    {
        if (ip is null) return;
        lock (_failures)
        {
            _failures.TryGetValue(ip, out var f);
            if (DateTime.UtcNow >= f.Until) f = (0, default);
            f = (f.Failures + 1, DateTime.UtcNow + BlockTime);
            _failures[ip] = f;
            if (f.Failures == MaxFailuresPerAddress)
                Log.Warn($"Endereço {ip} bloqueado por {BlockTime.TotalMinutes:0} minutos após {MaxFailuresPerAddress} conexões recusadas");
        }
    }

    private void HandleClient(TcpClient client)
    {
        var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (ip is not null && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IsBlocked(ip) || Interlocked.Increment(ref _handshakes) > MaxConcurrentHandshakes)
        {
            if (!IsBlocked(ip)) Interlocked.Decrement(ref _handshakes);
            client.Dispose();
            return;
        }

        Connection conn;
        PeerInfo peer;
        ChannelPurpose purpose;
        try
        {
            // Pareamento só pode ser feito para transmitir/aprovar (com alguém confirmando o código nas duas
            // telas) e só por quem está na mesma rede local (em casa). De outra rede — internet, Tailscale —
            // só entram aparelhos já pareados; um estranho que baixe o TransLocal nem chega a pedir o código.
            bool localNetwork = NetInfo.IsSameLocalNetwork(ip);
            (conn, peer, purpose) = SecureChannel.Accept(client, _channel(), p =>
            {
                if (p == ChannelPurpose.Pull) return false;
                if (!localNetwork) Log.Warn($"Pedido de pareamento de fora da rede local recusado ({ip})");
                return localNetwork;
            });
        }
        catch (Exception ex)
        {
            RegisterFailure(ip);
            Log.Warn($"Conexão recusada de {ip}: {ex.Message}");
            client.Dispose();
            return;
        }
        finally
        {
            Interlocked.Decrement(ref _handshakes);
        }

        Log.Info($"Conexão segura de {peer.Name} ({peer.ShortId}, {ip}) para {purpose}");
        try { PeerAuthenticated?.Invoke(peer, ip); } catch { }

        bool keepOpen = false;
        try
        {
            switch (purpose)
            {
                case ChannelPurpose.Stream:
                    ServeIncomingStream(conn, peer, requestedByUs: false);
                    break;
                case ChannelPurpose.Pull:
                    if (PullHandler is null)
                        conn.SendJson(MsgType.StreamDecision, new StreamDecisionMessage { Reason = "Acesso remoto indisponível." });
                    else
                        keepOpen = PullHandler(conn, peer);
                    break;
                case ChannelPurpose.Approver:
                    ApproverHandler?.Invoke(conn, peer);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Conexão de {peer.Name} encerrada", ex);
        }
        finally
        {
            if (!keepOpen) conn.Dispose();
        }
    }

    /// <summary>
    /// Exibe a tela que o outro aparelho envia por esta conexão (bloqueia até terminar).
    /// <paramref name="requestedByUs"/> = este aparelho pediu a tela (acesso remoto).
    /// </summary>
    public void ServeIncomingStream(Connection conn, PeerInfo peer, bool requestedByUs)
    {
        ReceiverSession? session = null;
        try
        {
            conn.ReceiveTimeoutMs = 15_000;
            var hello = conn.ReceiveJson<HelloMessage>(MsgType.Hello);
            if (hello.App != Proto.AppId || hello.ProtocolVersion != Proto.Version)
            {
                conn.SendJson(MsgType.Welcome, new WelcomeMessage { Accepted = false, Name = _channel().LocalName, Reason = "Versão do TransLocal incompatível. Atualize os dois computadores." });
                return;
            }
            if (!requestedByUs && !_acceptStreams())
            {
                conn.SendJson(MsgType.Welcome, new WelcomeMessage { Accepted = false, Name = _channel().LocalName, Reason = $"{_channel().LocalName} não está aceitando transmissões agora." });
                return;
            }
            // O nome exibido é o do aparelho autenticado, não o que veio na mensagem.
            hello.Name = peer.Name;

            var prefs = _prefs();
            session = new ReceiverSession(conn, hello, _playAudio(), prefs.Light);
            if (Interlocked.CompareExchange(ref _active, session, null) is not null)
            {
                conn.SendJson(MsgType.Welcome, new WelcomeMessage { Accepted = false, Name = _channel().LocalName, Reason = "Este computador já está exibindo outra transmissão." });
                session.Surface.Dispose();
                session = null;
                return;
            }

            conn.SendJson(MsgType.Welcome, new WelcomeMessage
            {
                Accepted = true,
                Name = _channel().LocalName,
                ScreenWidth = prefs.ScreenWidth,
                ScreenHeight = prefs.ScreenHeight,
                Light = prefs.Light,
                Codecs = Video.H264Decoder.IsAvailable ? new[] { "h264" } : null,
            });
            Log.Info($"Recebendo de {hello.Name} ({conn.RemoteEndPoint}) {hello.Width}×{hello.Height}, áudio={hello.Audio}, controle={hello.Control}, controles de jogo={hello.Gamepads ?? "?"}");
            session.StartDiagnostics();
            try { SessionStarted?.Invoke(session); } catch (Exception ex) { Log.Error("Erro ao abrir exibição", ex); }

            string? reason = session.Run();
            Log.Info($"Exibição de {hello.Name} terminou: {reason ?? "fechada neste computador"}");
            Interlocked.CompareExchange(ref _active, null, session);
            try { SessionEnded?.Invoke(session, reason); } catch (Exception ex) { Log.Error("Erro ao fechar exibição", ex); }
        }
        catch (Exception ex)
        {
            Log.Warn($"Exibição de {peer.Name} encerrada", ex);
            if (session is not null && Interlocked.CompareExchange(ref _active, null, session) == session)
            {
                try { SessionEnded?.Invoke(session, "A ligação foi perdida."); } catch { }
            }
        }
    }

    public void Dispose() => Stop();
}


/// <summary>Uma transmissão sendo exibida neste computador.</summary>
internal sealed class ReceiverSession
{
    private readonly Connection _conn;
    private readonly HelloMessage _hello;
    private readonly bool _playAudio;
    private volatile bool _closing;
    private Video.H264Decoder? _decoder;
    private bool _decoderFailed;
    private volatile string _codecLabel = "JPEG por blocos";
    private volatile string? _remoteStats;
    private long _lastKeyRequest;
    private readonly object _gpuGate = new();
    private Video.GpuDevice? _gpu;
    private bool _gpuTried;
    private int _gpuFrames;

    /// <summary>Placa de vídeo compartilhada pelo decodificador e pela exibição (null = desenhar pelo processador).</summary>
    public Video.GpuDevice? Gpu
    {
        get
        {
            lock (_gpuGate)
            {
                if (!_gpuTried)
                {
                    _gpuTried = true;
                    if (Environment.GetEnvironmentVariable("TRANSLOCAL_NO_GPU_PRESENT") != "1") _gpu = Video.GpuDevice.TryCreate();
                    if (_gpu is not null) Log.Info($"Exibição pela placa de vídeo: {_gpu.AdapterName}");
                }
                return _gpu;
            }
        }
    }

    /// <summary>Exibição na placa de vídeo, definida pela janela (os quadros H.264 vão direto para ela).</summary>
    public volatile Video.IGpuFrameSink? GpuSink;


    private int _statFrames;
    private long _statDecodeTicks;
    private long _statBytesAtLast;
    private long _statLastTs = Stopwatch.GetTimestamp();

    public FrameSurface Surface { get; } = new();
    public string SenderName => _hello.Name;
    public string RemoteEndPoint => _conn.RemoteEndPoint;

    /// <summary>A transmissão vem deste mesmo computador (teste). Controle fica bloqueado para não entrar em ciclo.</summary>
    public bool FromThisComputer { get; }

    public bool ControlAllowed => _hello.Control && !FromThisComputer;

    /// <summary>A transmissão traz som e este computador vai tocá-lo.</summary>
    public bool HasAudio => _hello.Audio && _playAudio && _hello.AudioRate > 0 && _hello.AudioChannels is 1 or 2;
    public int InitialWidth => _hello.Width;
    public int InitialHeight => _hello.Height;
    public long FramesReceived;

    /// <summary>Controles de jogo no transmissor: "ok", "sem-driver", "desligado" ou "antigo" (versão sem suporte).</summary>
    public string GamepadStatus => !ControlAllowed
        ? VirtualGamepads.StatusDisabled
        : _hello.Gamepads ?? "antigo";

    public event Action? FrameReady;
    public event Action<string>? StatsUpdated;

    /// <summary>Vibração pedida pelo jogo no outro computador: (slot, motor grande, motor pequeno).</summary>
    public event Action<int, byte, byte>? GamepadFeedback;

    /// <summary>Aviso do transmissor para mostrar na tela (programa de administrador, tela cheia exclusiva…).</summary>
    public event Action<string>? Notice;

    /// <summary>Diagnóstico da sessão (linhas dos dois lados), enquanto ela durar.</summary>
    public DiagRelay? Diag { get; private set; }

    /// <summary>O transmissor avisou que um jogo prendeu (true) ou soltou (false) o cursor.</summary>
    public event Action<bool>? CursorModeChanged;
    public bool RemoteCursorCaptured { get; private set; }

    /// <summary>Este receptor pediu transmissão leve (desenho mais rápido também).</summary>
    public bool Light { get; }

    public ReceiverSession(Connection conn, HelloMessage hello, bool playAudio, bool light = false)
    {
        Light = light;
        _conn = conn;
        _hello = hello;
        _playAudio = playAudio;
        var ip = conn.RemoteAddress;
        if (ip is not null && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        FromThisComputer = ip is not null &&
                           (System.Net.IPAddress.IsLoopback(ip) || NetInfo.GetLocalAddresses().Any(a => a.Address.Equals(ip)));
    }

    /// <summary>Executa até a transmissão terminar. Retorna o motivo (null se fechada aqui).</summary>
    public string? Run()
    {
        AudioPlayer? audio = null;
        if (HasAudio)
        {
            try { audio = new AudioPlayer(new WaveFormat(_hello.AudioRate, 16, _hello.AudioChannels)); }
            catch (Exception ex) { Log.Warn("Não foi possível tocar o áudio recebido", ex); }
        }
        using var statsTimer = new System.Threading.Timer(_ => ReportStats(), null, 1000, 1000);
        Span<byte> ack = stackalloc byte[4];
        // Thread de rede + decodificação + exibição: prioridade de jogo no agendador multimídia do Windows.
        var mmcss = Streaming.Realtime.BoostThread("Games");
        var priority = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        using var wifi = Streaming.Realtime.WifiLowLatency();
        Native.timeBeginPeriod(1);
        try
        {
            _conn.ReceiveTimeoutMs = 15_000;
            while (true)
            {
                using var m = _conn.Receive();
                switch (m.Type)
                {
                    case MsgType.Frame:
                        long t0 = Stopwatch.GetTimestamp();
                        int id = TileDecoder.Decode(m.Buffer, m.Length, Surface);
                        Interlocked.Add(ref _statDecodeTicks, Stopwatch.GetTimestamp() - t0);
                        Interlocked.Increment(ref _statFrames);
                        Interlocked.Increment(ref FramesReceived);
                        BinaryPrimitives.WriteInt32LittleEndian(ack, id);
                        _conn.Send(MsgType.FrameAck, ack);
                        FrameReady?.Invoke();
                        break;
                    case MsgType.VideoH264 when m.Length > 9:
                    {
                        long t1 = Stopwatch.GetTimestamp();
                        var span = m.Span;
                        int vid = BinaryPrimitives.ReadInt32LittleEndian(span);
                        int vw = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
                        int vh = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
                        if (_decoder is null && !_decoderFailed)
                        {
                            _decoder = Video.H264Decoder.TryCreate(hardware: true, Gpu, vw, vh);
                            _decoderFailed = _decoder is null;
                            _codecLabel = _decoder is null ? "H.264 sem decodificador" : $"H.264 decodificado pel{(_decoder.Hardware ? "a placa de vídeo" : "o processador")}";
                            Log.Info(_decoder is null ? "H.264: sem decodificador neste computador" : $"Decodificando H.264: {_decoder.Name} ({(_decoder.Hardware ? "placa de vídeo" : "processador")})");
                        }
                        bool produced = false, error = _decoder is null;
                        // Se a exibição pela placa de vídeo cair, o decodificador passa sozinho a entregar pelo processador.
                        var sink = GpuSink;
                        if (_decoder is not null && vw > 0 && vh > 0 && vw <= 8192 && vh <= 8192)
                            produced = _decoder.Decode(span[9..], vw, vh, Surface, sink, out error);
                        bool onGpu = produced && _decoder!.LastOnGpu;
                        Interlocked.Add(ref _statDecodeTicks, Stopwatch.GetTimestamp() - t1);
                        if (produced)
                        {
                            Interlocked.Increment(ref _statFrames);
                            Interlocked.Increment(ref FramesReceived);
                            if (onGpu) Interlocked.Increment(ref _gpuFrames);
                        }
                        // Confirmação depois do quadro já estar na tela (o transmissor mede o atraso real com ela).
                        BinaryPrimitives.WriteInt32LittleEndian(ack, vid);
                        _conn.Send(MsgType.FrameAck, ack);
                        if (error && Environment.TickCount64 - _lastKeyRequest > 1000)
                        {
                            _lastKeyRequest = Environment.TickCount64;
                            _conn.Send(MsgType.KeyframeRequest, ReadOnlySpan<byte>.Empty);
                        }
                        if (produced && !onGpu) FrameReady?.Invoke();
                        break;
                    }
                    case MsgType.Audio:
                        audio?.Add(m.Span);
                        break;
                    case MsgType.CursorMode when m.Length >= 1:
                        RemoteCursorCaptured = m.Span[0] != 0;
                        CursorModeChanged?.Invoke(RemoteCursorCaptured);
                        break;
                    case MsgType.Notice when m.Length > 0:
                        string notice = System.Text.Encoding.UTF8.GetString(m.Span[..Math.Min(m.Length, 1000)]);
                        Notice?.Invoke(notice);
                        break;
                    case MsgType.Diag:
                        Diag?.Received(m.Span);
                        break;
                    case MsgType.Stats when m.Length > 0:
                        _remoteStats = System.Text.Encoding.UTF8.GetString(m.Span[..Math.Min(m.Length, 400)]);
                        break;
                    case MsgType.GamepadFeedback when m.Length >= 3:
                        GamepadFeedback?.Invoke(m.Span[0], m.Span[1], m.Span[2]);
                        break;
                    case MsgType.Ping:
                        _conn.Send(MsgType.Pong, m.Span);
                        break;
                    case MsgType.Bye:
                        return "O outro computador parou de transmitir.";
                }
            }
        }
        catch (Exception ex)
        {
            if (_closing) return null;
            Log.Warn("Recepção interrompida", ex);
            return ex is IOException or ObjectDisposedException
                ? "A ligação com o outro computador foi perdida."
                : "Falha na exibição: " + ex.Message;
        }
        finally
        {
            audio?.Dispose();
            Diag?.Dispose();
            _decoder?.Dispose();
            _decoder = null;
            Native.timeEndPeriod(1);
            Thread.CurrentThread.Priority = priority;
            Streaming.Realtime.Revert(mmcss);
            lock (_gpuGate)
            {
                // A janela pode continuar usando a placa de vídeo (ela tem a própria referência).
                _gpu?.Dispose();
                _gpu = null;
            }
        }
    }

    /// <summary>Começa o diagnóstico compartilhado (chamado quando a exibição é aceita).</summary>
    public void StartDiagnostics() =>
        Diag ??= new DiagRelay(_hello.Name, "exibindo", DiagRelay.ShareEnabled ? SendDiag : null);

    private void SendDiag(byte[] payload)
    {
        if (_closing) return;
        try { _conn.Send(MsgType.Diag, payload); } catch { }
    }

    public void SendInput(byte[] payload)
    {
        if (!ControlAllowed || _closing) return;
        try { _conn.Send(MsgType.Input, payload); } catch { }
    }

    /// <summary>Texto para o registro do outro computador (diagnóstico).</summary>
    public void SendInfo(string text)
    {
        if (_closing) return;
        try { _conn.Send(MsgType.Info, System.Text.Encoding.UTF8.GetBytes(text)); } catch { }
    }

    public void SendGamepad(byte[] payload)
    {
        if (GamepadStatus != VirtualGamepads.StatusOk || _closing) return;
        try { _conn.Send(MsgType.Gamepad, payload); } catch { }
    }

    /// <summary>Encerra a exibição a partir deste computador.</summary>
    public void Close()
    {
        if (_closing) return;
        _closing = true;
        _conn.TrySendBye();
        _conn.Dispose();
    }

    private void ReportStats()
    {
        long now = Stopwatch.GetTimestamp();
        double secs = Math.Max(0.001, (now - Interlocked.Exchange(ref _statLastTs, now)) / (double)Stopwatch.Frequency);
        int frames = Interlocked.Exchange(ref _statFrames, 0);
        long dec = Interlocked.Exchange(ref _statDecodeTicks, 0);
        long bytes = Interlocked.Read(ref _conn.BytesReceived);
        long delta = bytes - Interlocked.Exchange(ref _statBytesAtLast, bytes);
        double decMs = frames > 0 ? dec * 1000.0 / Stopwatch.Frequency / frames : 0;
        int gpu = Interlocked.Exchange(ref _gpuFrames, 0);
        string label = _codecLabel + (gpu > 0 && gpu >= frames / 2 ? " → exibição pela placa de vídeo" : "");
        string remote = _remoteStats is { Length: > 0 } r ? $" • {r}" : "";
        string decWord = gpu > 0 ? "decodificação + exibição" : "decodificação";
        StatsUpdated?.Invoke($"{SenderName} • {Surface.Width}×{Surface.Height} • {frames / secs:0} quadros/s • {delta * 8 / secs / 1_000_000:0.0} Mbit/s • {label} • {decWord} {decMs:0.0} ms{remote}");
    }
}
