using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace TransLocal;

internal static class Proto
{
    public const string AppId = "TransLocal";

    /// <summary>2 = conexões com TLS, autenticação mútua e pareamento (incompatível com a 1).</summary>
    public const int Version = 2;
    public const int VideoPort = 52020;
    public const int DiscoveryPort = 52021;
    public const string DiscoverRequest = "TRANSLOCAL?1";

    /// <summary>Limite depois da autenticação (um quadro 1080p inteiro tem ~0,3–1 MB).</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Limite antes da autenticação: só mensagens pequenas do aperto de mão.</summary>
    public const int HandshakeMaxBytes = 16 * 1024;
    /// <summary>Quadros enviados ainda não mostrados do outro lado (TCP). Com 2, um atraso total acima de ~33 ms já
    /// fazia o PC pular quadros a 60 q/s.</summary>
    public const int MaxFramesInFlight = 4;

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
}

internal enum MsgType : byte
{
    Hello = 0x01,
    Welcome = 0x02,

    // Autenticação e pareamento (dentro do TLS, antes de qualquer outra mensagem)
    AuthChallenge = 0x03,
    AuthResponse = 0x04,
    AuthResult = 0x05,
    AuthDone = 0x06,
    PairCommit = 0x07,
    PairNonce = 0x08,
    PairReveal = 0x09,
    PairDecision = 0x0A,

    // Acesso remoto (o aparelho pede a tela) e aprovação pelo celular
    StreamRequest = 0x60,
    StreamDecision = 0x61,
    ApprovalRequest = 0x62,
    ApprovalResponse = 0x63,
    ApproverHello = 0x64,
    ApproverStatus = 0x65,

    Frame = 0x10,
    FrameAck = 0x11,
    VideoH264 = 0x12,       // transmissor → receptor: [id:int32][largura:u16][altura:u16][flags:u8 (1 = quadro-chave)][Annex B]
    KeyframeRequest = 0x13, // receptor → transmissor: decodificador perdeu a sequência, mande um quadro-chave
    UdpSetup = 0x14,        // transmissor → receptor: JSON {Port, Key, Session} — vídeo por UDP cifrado (chave pelo TLS)
    NetReport = 0x15,       // receptor → transmissor, 1×/s: [pacotes u32][perdidos u32][quadros perdidos u32][quadros recuperados u32]
    Audio = 0x20,
    Input = 0x30,
    Gamepad = 0x31,
    GamepadFeedback = 0x32,
    CursorMode = 0x33,      // transmissor → receptor: [1 = cursor preso por um jogo (mouse relativo)]
    Info = 0x34,            // receptor → transmissor: texto para o registro (ex.: controles conectados)
    Notice = 0x35,          // transmissor → receptor: aviso para mostrar na tela (UTF-8)
    Diag = 0x36,            // os dois sentidos: linhas do registro do outro lado (UTF-8, separadas por \n)
    Stats = 0x37,           // transmissor → receptor: resumo do atraso e da taxa de bits (UTF-8), uma vez por segundo
    Ping = 0x40,
    Pong = 0x41,
    Bye = 0x50,
}

internal sealed class HelloMessage
{
    public string App { get; set; } = Proto.AppId;
    public int ProtocolVersion { get; set; } = Proto.Version;
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Audio { get; set; }
    public int AudioRate { get; set; }
    public int AudioChannels { get; set; }
    public bool Control { get; set; }

    /// <summary>Controles de jogo: "ok", "sem-driver" ou "desligado" (null em versões antigas).</summary>
    public string? Gamepads { get; set; }
}

internal sealed class WelcomeMessage
{
    public bool Accepted { get; set; }
    public string Name { get; set; } = "";
    public string? Reason { get; set; }

    /// <summary>Tamanho da tela onde a transmissão será exibida (0 em versões antigas).</summary>
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }

    /// <summary>O receptor pede uma transmissão leve (computador mais simples).</summary>
    public bool Light { get; set; }

    /// <summary>Formatos de vídeo que este receptor decodifica além dos blocos JPEG (ex.: "h264"). Null em versões antigas.</summary>
    public string[]? Codecs { get; set; }

    /// <summary>Transportes de vídeo extras que o receptor aceita (ex.: "udp1"). Null em versões antigas (só TCP).</summary>
    public string[]? Transports { get; set; }
}

/// <summary>Combinação do vídeo por UDP, enviada pelo canal TLS já autenticado.</summary>
internal sealed class UdpSetupMessage
{
    public int Port { get; set; }
    public string Key { get; set; } = "";
    public uint Session { get; set; }
}

/// <summary>Preferências do receptor enviadas no handshake.</summary>
internal readonly record struct ReceiverPrefs(int ScreenWidth, int ScreenHeight, bool Light);

/// <summary>Limites da transmissão leve.</summary>
internal static class LightMode
{
    public const int MaxWidth = 1366;
    public const int MaxHeight = 768;
    public const int MaxFps = 30;
    public const int MaxQuality = 55;
}

internal sealed class DiscoveryReply
{
    public string App { get; set; } = Proto.AppId;
    public int ProtocolVersion { get; set; } = Proto.Version;
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public bool Busy { get; set; }

    /// <summary>Identificador curto da chave do aparelho (pública; mostra se já está pareado).</summary>
    public string? Id { get; set; }
}

/// <summary>Mensagem recebida. O buffer vem do ArrayPool e deve ser devolvido com Dispose().</summary>
internal readonly struct NetMessage : IDisposable
{
    public readonly MsgType Type;
    public readonly byte[] Buffer;
    public readonly int Length;

    public NetMessage(MsgType type, byte[] buffer, int length)
    {
        Type = type;
        Buffer = buffer;
        Length = length;
    }

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public T ReadJson<T>() => JsonSerializer.Deserialize<T>(Span, Proto.Json)
                              ?? throw new InvalidDataException("JSON vazio");

    public void Dispose()
    {
        if (Buffer.Length > 0) ArrayPool<byte>.Shared.Return(Buffer);
    }
}

/// <summary>
/// Conexão TCP com mensagens [tipo:1][tamanho:int32 LE][conteúdo].
/// Envio é thread-safe; leitura deve ser feita por uma única thread.
/// </summary>
internal sealed class Connection : IDisposable
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly object _writeLock = new();
    private readonly byte[] _header = new byte[5];
    private int _closed;

    public string RemoteEndPoint { get; }
    public System.Net.IPAddress? RemoteAddress { get; }
    public long BytesSent;
    public long BytesReceived;

    /// <summary>Maior mensagem aceita. Pequeno até a autenticação terminar.</summary>
    public int MaxMessageBytes { get; set; } = Proto.HandshakeMaxBytes;

    /// <param name="stream">Fluxo já protegido (TLS). null = o fluxo TCP do próprio cliente.</param>
    public Connection(TcpClient client, Stream? stream = null)
    {
        _client = client;
        PrepareSocket(client);
        _stream = stream ?? client.GetStream();
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        RemoteAddress = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address;
    }

    public static void PrepareSocket(TcpClient client)
    {
        client.NoDelay = true;
        client.SendBufferSize = 2 * 1024 * 1024;
        client.ReceiveBufferSize = 2 * 1024 * 1024;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    /// <summary>Recebe uma mensagem que precisa ser do tipo indicado.</summary>
    public NetMessage Expect(MsgType type)
    {
        var m = Receive();
        if (m.Type == type) return m;
        m.Dispose();
        throw new InvalidDataException($"Mensagem inesperada ({m.Type}; esperado {type}).");
    }

    public T ReceiveJson<T>(MsgType type)
    {
        using var m = Expect(type);
        return m.ReadJson<T>();
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Tempo máximo sem receber nada antes de considerar a ligação perdida.</summary>
    public int ReceiveTimeoutMs
    {
        get => _stream.ReadTimeout;
        set => _stream.ReadTimeout = value;
    }

    public void Send(MsgType type, ReadOnlySpan<byte> payload)
    {
        if (IsClosed) throw new ObjectDisposedException(nameof(Connection));
        int total = 5 + payload.Length;
        lock (_writeLock)
        {
            if (total <= 64 * 1024)
            {
                // Mensagens pequenas: um único write (um único pacote TCP com NoDelay).
                var buf = ArrayPool<byte>.Shared.Rent(total);
                try
                {
                    buf[0] = (byte)type;
                    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1), payload.Length);
                    payload.CopyTo(buf.AsSpan(5));
                    _stream.Write(buf, 0, total);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
            else
            {
                Span<byte> h = stackalloc byte[5];
                h[0] = (byte)type;
                BinaryPrimitives.WriteInt32LittleEndian(h[1..], payload.Length);
                _stream.Write(h);
                _stream.Write(payload);
            }
        }
        Interlocked.Add(ref BytesSent, total);
    }

    public void Send(MsgType type) => Send(type, ReadOnlySpan<byte>.Empty);

    public void SendJson<T>(MsgType type, T value) => Send(type, JsonSerializer.SerializeToUtf8Bytes(value, Proto.Json));

    public NetMessage Receive()
    {
        _stream.ReadExactly(_header, 0, 5);
        var type = (MsgType)_header[0];
        int len = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(1));
        if (len < 0 || len > MaxMessageBytes)
            throw new InvalidDataException($"Mensagem inválida ({len} bytes; limite {MaxMessageBytes})");
        if (len == 0)
            return new NetMessage(type, Array.Empty<byte>(), 0);
        var buf = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            _stream.ReadExactly(buf, 0, len);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }
        Interlocked.Add(ref BytesReceived, 5 + len);
        return new NetMessage(type, buf, len);
    }

    public void TrySendBye()
    {
        try { if (!IsClosed) Send(MsgType.Bye); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _client.Client.Shutdown(SocketShutdown.Both); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
