using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace TransLocal;

internal static class Proto
{
    public const string AppId = "TransLocal";
    public const int Version = 1;
    public const int VideoPort = 52020;
    public const int DiscoveryPort = 52021;
    public const string DiscoverRequest = "TRANSLOCAL?1";
    public const int MaxMessageBytes = 64 * 1024 * 1024;
    public const int MaxFramesInFlight = 2;

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
}

internal enum MsgType : byte
{
    Hello = 0x01,
    Welcome = 0x02,
    Frame = 0x10,
    FrameAck = 0x11,
    Audio = 0x20,
    Input = 0x30,
    Gamepad = 0x31,
    GamepadFeedback = 0x32,
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
}

internal sealed class DiscoveryReply
{
    public string App { get; set; } = Proto.AppId;
    public int ProtocolVersion { get; set; } = Proto.Version;
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public bool Busy { get; set; }
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
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();
    private readonly byte[] _header = new byte[5];
    private int _closed;

    public string RemoteEndPoint { get; }
    public System.Net.IPAddress? RemoteAddress { get; }
    public long BytesSent;
    public long BytesReceived;

    public Connection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _client.SendBufferSize = 2 * 1024 * 1024;
        _client.ReceiveBufferSize = 2 * 1024 * 1024;
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _stream = client.GetStream();
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        RemoteAddress = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address;
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
        if (len < 0 || len > Proto.MaxMessageBytes)
            throw new InvalidDataException($"Mensagem inválida ({len} bytes)");
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
