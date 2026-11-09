using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace TransLocal.Streaming;

/// <summary>
/// Código Reed-Solomon sobre GF(256) com matriz de Cauchy (correção de apagamentos): de k pedaços de dados gera m
/// de paridade; quaisquer k dos k+m que chegarem reconstroem os dados. É o que jogos na nuvem (Moonlight, Xbox
/// Cloud via WebRTC) usam para não depender de retransmissão quando o Wi-Fi perde pacotes.
/// </summary>
internal static class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] LogT = new byte[256];
    private static readonly byte[] MulT = new byte[256 * 256];

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            LogT[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        for (int a = 1; a < 256; a++)
            for (int b = 1; b < 256; b++)
                MulT[a * 256 + b] = Exp[LogT[a] + LogT[b]];
    }

    private static byte Mul(byte a, byte b) => MulT[a * 256 + b];
    private static byte Inv(byte a) => Exp[255 - LogT[a]];

    /// <summary>Coeficiente de Cauchy da paridade i para o dado j (k dados).</summary>
    private static byte Coef(int k, int i, int j) => Inv((byte)((k + i) ^ j));

    /// <summary>Preenche shards[k..k+m-1] (paridade) a partir de shards[0..k-1], todos com <paramref name="size"/> bytes.</summary>
    public static void Encode(byte[][] shards, int k, int m, int size)
    {
        for (int i = 0; i < m; i++)
        {
            var p = shards[k + i].AsSpan(0, size);
            p.Clear();
            for (int j = 0; j < k; j++) MulAdd(p, shards[j].AsSpan(0, size), Coef(k, i, j));
        }
    }

    private static void MulAdd(Span<byte> dst, ReadOnlySpan<byte> src, byte c)
    {
        if (c == 0) return;
        if (c == 1)
        {
            for (int t = 0; t < dst.Length; t++) dst[t] ^= src[t];
            return;
        }
        var row = MulT.AsSpan(c * 256, 256);
        for (int t = 0; t < dst.Length; t++) dst[t] ^= row[src[t]];
    }

    /// <summary>
    /// Reconstrói os pedaços de dados que faltam. <paramref name="present"/> indica quais dos k+m chegaram.
    /// False se chegaram menos de k.
    /// </summary>
    public static bool Recover(byte[][] shards, bool[] present, int k, int m, int size)
    {
        int missing = 0;
        for (int j = 0; j < k; j++) if (!present[j]) missing++;
        if (missing == 0) return true;
        // Linhas usadas: todos os dados presentes + paridades suficientes para cobrir os que faltam.
        var rows = new int[k];
        int n = 0;
        for (int j = 0; j < k; j++) if (present[j]) rows[n++] = j;
        for (int i = 0; i < m && n < k; i++) if (present[k + i]) rows[n++] = k + i;
        if (n < k) return false;

        // Matriz A (k×k): linha r = coeficientes do pedaço rows[r] em função dos dados.
        var a = new byte[k, k];
        for (int r = 0; r < k; r++)
        {
            int s = rows[r];
            if (s < k) a[r, s] = 1;
            else for (int j = 0; j < k; j++) a[r, j] = Coef(k, s - k, j);
        }
        // Inversa por Gauss-Jordan em GF(256).
        var inv = new byte[k, k];
        for (int r = 0; r < k; r++) inv[r, r] = 1;
        for (int c = 0; c < k; c++)
        {
            int piv = c;
            while (piv < k && a[piv, c] == 0) piv++;
            if (piv == k) return false;
            if (piv != c)
                for (int j = 0; j < k; j++)
                {
                    (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
                    (inv[c, j], inv[piv, j]) = (inv[piv, j], inv[c, j]);
                }
            byte d = Inv(a[c, c]);
            for (int j = 0; j < k; j++) { a[c, j] = Mul(a[c, j], d); inv[c, j] = Mul(inv[c, j], d); }
            for (int r = 0; r < k; r++)
            {
                if (r == c || a[r, c] == 0) continue;
                byte f = a[r, c];
                for (int j = 0; j < k; j++) { a[r, j] ^= Mul(f, a[c, j]); inv[r, j] ^= Mul(f, inv[c, j]); }
            }
        }
        // Dados que faltam = linha correspondente da inversa × pedaços recebidos.
        var tmp = new byte[size];
        for (int j = 0; j < k; j++)
        {
            if (present[j]) continue;
            Array.Clear(tmp);
            for (int r = 0; r < k; r++) MulAdd(tmp, shards[rows[r]].AsSpan(0, size), inv[j, r]);
            tmp.CopyTo(shards[j] ??= new byte[size], 0);
        }
        return true;
    }
}

/// <summary>Formato dos datagramas de vídeo (cabeçalho autenticado + conteúdo cifrado com AES-GCM).</summary>
internal static class UdpProto
{
    public const byte Magic = 0x54;
    public const byte KindVideo = 1, KindHello = 2;
    public const int HeaderLen = 14;      // magic, tipo, sessão u32, sequência u64
    public const int TagLen = 16;
    public const int VideoHeaderLen = 18; // quadro u32, flags u8, larg u16, alt u16, tamanho u32, bloco u16, pedaço u8, k u8, m u8
    public const int ShardSize = 1150;    // datagrama ≤ ~1200 bytes: cabe até no MTU de 1280 do Tailscale
    public const int MaxDataPerBlock = 48;

    /// <summary>Nonce único por direção e sequência (a chave é nova a cada transmissão).</summary>
    public static void Nonce(Span<byte> nonce, byte direction, ulong seq)
    {
        nonce.Clear();
        nonce[0] = direction;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], seq);
    }

    public static int Seal(AesGcm aes, byte direction, byte kind, uint session, ulong seq, ReadOnlySpan<byte> plain, byte[] output)
    {
        output[0] = Magic;
        output[1] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(2), session);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(6), seq);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, direction, seq);
        aes.Encrypt(nonce, plain, output.AsSpan(HeaderLen, plain.Length), output.AsSpan(HeaderLen + plain.Length, TagLen), output.AsSpan(0, HeaderLen));
        return HeaderLen + plain.Length + TagLen;
    }

    /// <summary>Confere e decifra um datagrama. Devolve o tamanho do conteúdo, ou -1 se não for desta sessão / foi alterado.</summary>
    public static int Open(AesGcm aes, byte direction, uint session, ReadOnlySpan<byte> packet, byte[] plain, out byte kind, out ulong seq)
    {
        kind = 0;
        seq = 0;
        if (packet.Length < HeaderLen + TagLen || packet[0] != Magic) return -1;
        if (BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]) != session) return -1;
        kind = packet[1];
        seq = BinaryPrimitives.ReadUInt64LittleEndian(packet[6..]);
        int len = packet.Length - HeaderLen - TagLen;
        if (len > plain.Length) return -1;
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, direction, seq);
        try
        {
            aes.Decrypt(nonce, packet.Slice(HeaderLen, len), packet.Slice(HeaderLen + len, TagLen), plain.AsSpan(0, len), packet[..HeaderLen]);
        }
        catch (CryptographicException) { return -1; }
        return len;
    }
}

/// <summary>
/// Envio de vídeo por UDP: cada quadro é cortado em pedaços de ~1,1 KB, com paridade Reed-Solomon por bloco e
/// cifragem AES-GCM por datagrama (chave nova por transmissão, combinada pelo canal TLS já autenticado).
/// O receptor se apresenta primeiro (pacote "olá" cifrado): só então o vídeo sai, e só para ele.
/// </summary>
internal sealed class UdpVideoSender : IDisposable
{
    private readonly Socket _socket;
    private readonly AesGcm _aes;
    private readonly byte[] _packet = new byte[2048];
    private readonly byte[] _plain = new byte[2048];
    private readonly IPAddress _expectedPeer;
    private ulong _seq;
    private volatile IPEndPoint? _peer;
    private long _lastHello;
    private volatile bool _disposed;
    private readonly double _simulatedLoss;
    private readonly Random _rng = new();

    public int Port { get; }
    public byte[] Key { get; }
    public uint Session { get; }
    public long PacketsSent;
    public long ParityPackets;

    /// <summary>Ritmo de envio dos pacotes de um quadro (bits/s); 0 = sem cadência.</summary>
    public double PaceBitsPerSecond { get; set; } = 40_000_000;

    /// <summary>Tempo máximo para espalhar um quadro (ms) — normalmente ~70% do intervalo entre quadros.</summary>
    public double MaxPaceMs { get; set; } = 11;

    /// <summary>Porcentagem de paridade (ajustada pela perda que o receptor relata).</summary>
    public double Fec { get; set; } = 0.20;

    /// <summary>O receptor confirmou que recebe por UDP (e continua se apresentando).</summary>
    public bool Active => _peer is not null && Environment.TickCount64 - Interlocked.Read(ref _lastHello) < 4000;

    /// <summary>O receptor se apresentou pela primeira vez (UDP liberado no caminho).</summary>
    public event Action<IPEndPoint>? PeerReady;

    public UdpVideoSender(IPAddress peer)
    {
        _expectedPeer = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SendBufferSize = 4 * 1024 * 1024;
        NetInfo.DisableUdpConnReset(_socket);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        Key = RandomNumberGenerator.GetBytes(32);
        Session = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
        _aes = new AesGcm(Key, UdpProto.TagLen);
        double.TryParse(Environment.GetEnvironmentVariable("TRANSLOCAL_UDP_LOSS"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _simulatedLoss); // testes: descarta essa fração dos pacotes
        new Thread(ReceiveLoop) { IsBackground = true, Name = "TransLocal UDP (olá)" }.Start();
    }

    private void ReceiveLoop()
    {
        var buf = new byte[2048];
        var plain = new byte[256];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_disposed)
        {
            int n;
            try { n = _socket.ReceiveFrom(buf, ref from); }
            catch (SocketException) { if (_disposed) return; continue; }
            catch (ObjectDisposedException) { return; }
            var ep = (IPEndPoint)from;
            // Só do mesmo endereço da conexão segura, e só com a chave desta transmissão.
            if (!ep.Address.Equals(_expectedPeer) && !(IPAddress.IsLoopback(ep.Address) && IPAddress.IsLoopback(_expectedPeer))) continue;
            if (UdpProto.Open(_aes, 1, Session, buf.AsSpan(0, n), plain, out byte kind, out _) < 0 || kind != UdpProto.KindHello) continue;
            Interlocked.Exchange(ref _lastHello, Environment.TickCount64);
            if (_peer is null || !_peer.Equals(ep))
            {
                _peer = new IPEndPoint(ep.Address, ep.Port);
                try { PeerReady?.Invoke(_peer); } catch { }
            }
        }
    }

    private readonly System.Collections.Concurrent.BlockingCollection<(int Id, bool Key, int W, int H, byte[] Data)> _queue = new(boundedCapacity: 8);
    private Thread? _sendThread;

    /// <summary>
    /// Entrega o quadro para a thread de envio, que espalha os pacotes no tempo sem segurar a captura e a
    /// codificação do quadro seguinte.
    /// </summary>
    public void Enqueue(int frameId, bool key, int width, int height, byte[] data)
    {
        if (_disposed) return;
        if (_sendThread is null)
        {
            _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "TransLocal UDP envio", Priority = ThreadPriority.Highest };
            _sendThread.Start();
        }
        // Fila cheia (rede travada): espera um pouco em vez de descartar — os quadros seguintes dependem deste.
        try { _queue.TryAdd((frameId, key, width, height, data), 50); } catch (InvalidOperationException) { }
    }

    private void SendLoop()
    {
        var mmcss = Realtime.BoostThread("Capture");
        try
        {
            foreach (var f in _queue.GetConsumingEnumerable())
            {
                if (_disposed) break;
                try { SendFrame(f.Id, f.Key, f.W, f.H, f.Data); }
                catch (Exception ex) { Log.Warn("UDP: falha ao enviar quadro", ex); }
            }
        }
        catch (ObjectDisposedException) { }
        finally { Realtime.Revert(mmcss); }
    }

    /// <summary>Envia um quadro H.264 inteiro (com paridade) já, nesta thread.</summary>
    public void SendFrame(int frameId, bool key, int width, int height, ReadOnlySpan<byte> data)
    {
        var peer = _peer;
        if (peer is null) return;
        int S = UdpProto.ShardSize;
        int dataShards = Math.Max(1, (data.Length + S - 1) / S);
        int blocks = (dataShards + UdpProto.MaxDataPerBlock - 1) / UdpProto.MaxDataPerBlock;
        double fec = key ? Math.Max(Fec, 0.25) : Fec; // o quadro-chave é o que mais custa perder
        // Cadência: os pacotes do quadro saem espaçados (grupos de 4) ao longo de parte do intervalo entre quadros,
        // em vez de uma rajada só — roteadores Wi-Fi descartam rajadas quando a fila enche (era a origem das perdas).
        int totalPackets = 0;
        for (int b = 0; b < blocks; b++)
        {
            int kk = Math.Min(UdpProto.MaxDataPerBlock, dataShards - b * UdpProto.MaxDataPerBlock);
            totalPackets += kk + (fec <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(kk * fec)));
        }
        double perPacketTicks = PaceBitsPerSecond > 0 ? (UdpProto.ShardSize + 60) * 8.0 * Stopwatch.Frequency / PaceBitsPerSecond : 0;
        double maxTicks = MaxPaceMs * Stopwatch.Frequency / 1000.0;
        if (perPacketTicks * totalPackets > maxTicks) perPacketTicks = maxTicks / Math.Max(1, totalPackets);
        long start = Stopwatch.GetTimestamp();
        int sent = 0;
        for (int b = 0; b < blocks; b++)
        {
            int first = b * UdpProto.MaxDataPerBlock;
            int k = Math.Min(UdpProto.MaxDataPerBlock, dataShards - first);
            int m = fec <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(k * fec));
            var shards = new byte[k + m][];
            for (int j = 0; j < k + m; j++) shards[j] = new byte[S];
            for (int j = 0; j < k; j++)
            {
                int off = (first + j) * S;
                int len = Math.Min(S, data.Length - off);
                if (len > 0) data.Slice(off, len).CopyTo(shards[j]);
            }
            if (m > 0) ReedSolomon.Encode(shards, k, m, S);
            for (int j = 0; j < k + m; j++)
            {
                var p = _plain.AsSpan();
                BinaryPrimitives.WriteInt32LittleEndian(p, frameId);
                p[4] = (byte)(key ? 1 : 0);
                BinaryPrimitives.WriteUInt16LittleEndian(p[5..], (ushort)width);
                BinaryPrimitives.WriteUInt16LittleEndian(p[7..], (ushort)height);
                BinaryPrimitives.WriteInt32LittleEndian(p[9..], data.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(p[13..], (ushort)b);
                p[15] = (byte)j;
                p[16] = (byte)k;
                p[17] = (byte)m;
                shards[j].CopyTo(p[UdpProto.VideoHeaderLen..]);
                int n = UdpProto.Seal(_aes, 0, UdpProto.KindVideo, Session, _seq++, p[..(UdpProto.VideoHeaderLen + S)], _packet);
                if (_simulatedLoss > 0 && _rng.NextDouble() < _simulatedLoss) continue;
                try { _socket.SendTo(_packet, 0, n, SocketFlags.None, peer); }
                catch (SocketException) { }
                Interlocked.Increment(ref PacketsSent);
                if (j >= k) Interlocked.Increment(ref ParityPackets);
                if (++sent % 4 == 0 && perPacketTicks > 0 && sent < totalPackets)
                {
                    long due = start + (long)(perPacketTicks * sent);
                    while (Stopwatch.GetTimestamp() < due)
                    {
                        if (due - Stopwatch.GetTimestamp() > Stopwatch.Frequency / 1000) Thread.Sleep(0); else Thread.SpinWait(50);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _queue.CompleteAdding(); } catch { }
        try { _sendThread?.Join(200); } catch { }
        try { _socket.Dispose(); } catch { }
        _aes.Dispose();
    }
}

/// <summary>Quadro de vídeo recebido por UDP, já remontado (e corrigido pela paridade, se preciso).</summary>
internal readonly record struct UdpFrame(int Id, bool Key, int Width, int Height, byte[] Data, int Length);

/// <summary>Recepção de vídeo por UDP: remonta quadros, recupera pacotes perdidos pela paridade e relata a perda.</summary>
internal sealed class UdpVideoReceiver : IDisposable
{
    private sealed class Block
    {
        public byte[][]? Shards;
        public bool[]? Present;
        public int K, M, Count;
        public bool Done;
    }

    private sealed class Assembly
    {
        public int Length, Width, Height, Blocks, BlocksDone;
        public bool Key;
        public Block[] Parts = Array.Empty<Block>();
        public byte[] Data = Array.Empty<byte>();
        public bool Recovered;
    }

    private readonly Socket _socket;
    private readonly AesGcm _aes;
    private readonly uint _session;
    private readonly IPEndPoint _sender;
    private readonly Dictionary<int, Assembly> _frames = new();
    private readonly System.Threading.Timer _helloTimer;
    private readonly byte[] _helloPacket = new byte[64];
    private ulong _helloSeq;
    private volatile bool _disposed;
    private int _newest = int.MinValue;
    private ulong _nextSeq;
    private bool _anySeq;

    // Estatísticas (zeradas a cada relatório)
    public long Packets, PacketsLost, FramesLost, FramesRecovered;
    public long TotalPackets;

    /// <summary>Quadro pronto (na thread de recepção UDP).</summary>
    public event Action<UdpFrame>? FrameReady;

    /// <summary>Um quadro não pôde ser remontado (perda além da paridade): o decodificador precisa de quadro-chave.</summary>
    public event Action<int>? FrameLost;

    public bool Receiving => Interlocked.Read(ref TotalPackets) > 0;

    /// <summary>Bytes já recebidos esperando processamento (quadros acumulados no receptor).</summary>
    public int PendingBytes
    {
        get { try { return _socket.Available; } catch { return 0; } }
    }

    public UdpVideoReceiver(byte[] key, uint session, IPEndPoint sender)
    {
        _aes = new AesGcm(key, UdpProto.TagLen);
        _session = session;
        _sender = sender;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.ReceiveBufferSize = 8 * 1024 * 1024;
        NetInfo.DisableUdpConnReset(_socket);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        _helloTimer = new System.Threading.Timer(_ => SendHello(), null, 0, 100);
        new Thread(ReceiveLoop) { IsBackground = true, Name = "TransLocal UDP vídeo", Priority = ThreadPriority.Highest }.Start();
    }

    /// <summary>"Olá" cifrado ao transmissor: abre o caminho no firewall e diz para onde mandar o vídeo.</summary>
    private void SendHello()
    {
        if (_disposed) return;
        try
        {
            int n = UdpProto.Seal(_aes, 1, UdpProto.KindHello, _session, _helloSeq++, stackalloc byte[1] { 1 }, _helloPacket);
            _socket.SendTo(_helloPacket, 0, n, SocketFlags.None, _sender);
            // Antes do primeiro vídeo: a cada 100 ms; depois, só para manter o caminho aberto.
            if (Receiving) _helloTimer.Change(1000, 1000);
        }
        catch { }
    }

    private void ReceiveLoop()
    {
        var mmcss = Realtime.BoostThread("Games");
        var buf = new byte[2048];
        var plain = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!_disposed)
            {
                int n;
                try { n = _socket.ReceiveFrom(buf, ref from); }
                catch (SocketException) { if (_disposed) return; continue; }
                catch (ObjectDisposedException) { return; }
                int len = UdpProto.Open(_aes, 0, _session, buf.AsSpan(0, n), plain, out byte kind, out ulong seq);
                if (len < UdpProto.VideoHeaderLen + UdpProto.ShardSize || kind != UdpProto.KindVideo) continue;
                CountSequence(seq);
                try { OnShard(plain.AsSpan(0, len)); }
                catch (Exception ex) { Log.Warn("UDP: pedaço de vídeo inválido", ex); }
            }
        }
        finally { Realtime.Revert(mmcss); }
    }

    private void CountSequence(ulong seq)
    {
        Interlocked.Increment(ref Packets);
        Interlocked.Increment(ref TotalPackets);
        if (!_anySeq) { _anySeq = true; _nextSeq = seq + 1; return; }
        if (seq >= _nextSeq)
        {
            ulong gap = seq - _nextSeq;
            if (gap > 0 && gap < 10_000) Interlocked.Add(ref PacketsLost, (long)gap);
            _nextSeq = seq + 1;
        }
    }

    private void OnShard(ReadOnlySpan<byte> p)
    {
        int id = BinaryPrimitives.ReadInt32LittleEndian(p);
        bool key = (p[4] & 1) != 0;
        int w = BinaryPrimitives.ReadUInt16LittleEndian(p[5..]);
        int h = BinaryPrimitives.ReadUInt16LittleEndian(p[7..]);
        int total = BinaryPrimitives.ReadInt32LittleEndian(p[9..]);
        int block = BinaryPrimitives.ReadUInt16LittleEndian(p[13..]);
        int shard = p[15], k = p[16], m = p[17];
        int S = UdpProto.ShardSize;
        if (total <= 0 || total > 16 * 1024 * 1024 || k == 0 || k > UdpProto.MaxDataPerBlock || shard >= k + m) return;
        if (_newest != int.MinValue && id <= _newest && !_frames.ContainsKey(id)) return; // atrasado ou repetido

        if (!_frames.TryGetValue(id, out var a))
        {
            int dataShards = (total + S - 1) / S;
            int blocks = (dataShards + UdpProto.MaxDataPerBlock - 1) / UdpProto.MaxDataPerBlock;
            a = new Assembly { Length = total, Width = w, Height = h, Key = key, Blocks = blocks, Parts = new Block[blocks], Data = new byte[dataShards * S] };
            for (int i = 0; i < blocks; i++) a.Parts[i] = new Block();
            // No máximo 16 quadros em montagem (limita a memória mesmo com pacotes estranhos).
            while (_frames.Count >= 16)
            {
                int oldest = _frames.Keys.Min();
                _frames.Remove(oldest);
                Interlocked.Increment(ref FramesLost);
            }
            _frames[id] = a;
        }
        if (block >= a.Blocks) return;
        var blk = a.Parts[block];
        if (blk.Done) return;
        if (blk.Shards is null)
        {
            blk.K = k;
            blk.M = m;
            blk.Shards = new byte[k + m][];
            blk.Present = new bool[k + m];
        }
        // Pedaço que não combina com o bloco (k/m diferentes) é descartado antes de qualquer acesso.
        if (k != blk.K || m != blk.M || shard >= blk.K + blk.M || blk.Present![shard]) return;
        var dst = blk.Shards[shard] = new byte[S];
        p.Slice(UdpProto.VideoHeaderLen, S).CopyTo(dst);
        blk.Present[shard] = true;
        blk.Count++;
        if (blk.Count < blk.K) return;

        // Bloco completo (ou recuperável pela paridade).
        bool lostData = false;
        for (int j = 0; j < blk.K; j++) if (!blk.Present[j]) { lostData = true; break; }
        if (lostData)
        {
            if (!ReedSolomon.Recover(blk.Shards, blk.Present, blk.K, blk.M, S)) return;
            a.Recovered = true;
        }
        int baseShard = block * UdpProto.MaxDataPerBlock;
        for (int j = 0; j < blk.K; j++) Buffer.BlockCopy(blk.Shards[j], 0, a.Data, (baseShard + j) * S, S);
        blk.Done = true;
        blk.Shards = null;
        if (++a.BlocksDone < a.Blocks) return;

        // Quadro completo: entrega e descarta os mais antigos que não completaram (perdidos).
        _frames.Remove(id);
        if (a.Recovered) Interlocked.Increment(ref FramesRecovered);
        foreach (var old in _frames.Keys.Where(x => x < id).ToList())
        {
            _frames.Remove(old);
            Interlocked.Increment(ref FramesLost);
            try { FrameLost?.Invoke(old); } catch { }
        }
        if (id > _newest || _newest == int.MinValue) _newest = id;
        FrameReady?.Invoke(new UdpFrame(id, a.Key, a.Width, a.Height, a.Data, a.Length));
    }

    public void Dispose()
    {
        _disposed = true;
        try { _helloTimer.Dispose(); } catch { }
        try { _socket.Dispose(); } catch { }
        _aes.Dispose();
    }
}
