using System.Buffers;
using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TransLocal.Video;

/// <summary>Quadro codificado pronto para envio (buffer do ArrayPool).</summary>
internal sealed class EncodedFrame : IDisposable
{
    public static readonly EncodedFrame Empty = new(Array.Empty<byte>(), 0, 0);

    public byte[] Buffer { get; }
    public int Length { get; }
    public int TileCount { get; }

    public EncodedFrame(byte[] buffer, int length, int tileCount)
    {
        Buffer = buffer;
        Length = length;
        TileCount = tileCount;
    }

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public void Dispose()
    {
        if (Buffer.Length > 0) ArrayPool<byte>.Shared.Return(Buffer);
    }
}

/// <summary>
/// Codificador por faixas: a tela é dividida em faixas horizontais de 64 px. Para cada faixa que mudou
/// desde o quadro anterior, só o trecho horizontal alterado (em blocos de 64 px) é codificado em JPEG.
/// As faixas são processadas em paralelo.
///
/// Formato do quadro: [frameId:int32][largura:uint16][altura:uint16][n:uint16]
///                    n × ([x:uint16][y:uint16][w:uint16][h:uint16][len:int32][jpeg])
/// </summary>
internal sealed unsafe class TileEncoder
{
    public const int Block = 64;
    private const int HeaderSize = 10;
    private const int TileHeaderSize = 12;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static readonly ParallelOptions Parallelism = new()
    {
        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
    };

    [ThreadStatic] private static MemoryStream? t_stream;
    [ThreadStatic] private static EncoderParameters? t_params;
    [ThreadStatic] private static int t_quality;

    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _bands;
    private readonly int _cols;
    private readonly byte[] _previous;
    private readonly IntPtr _previousPtr;
    private bool _forceFull = true;

    public TileEncoder(int width, int height)
    {
        if (width > ushort.MaxValue || height > ushort.MaxValue) throw new ArgumentException("Resolução muito grande");
        _width = width;
        _height = height;
        _stride = width * 4;
        _bands = (height + Block - 1) / Block;
        _cols = (width + Block - 1) / Block;
        // Alocado no heap fixo (pinned): o endereço não muda e pode ser usado pelas threads do Parallel.For.
        _previous = GC.AllocateUninitializedArray<byte>(_stride * height, pinned: true);
        _previousPtr = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_previous));
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>Faz o próximo quadro ser enviado inteiro (ex.: novo receptor).</summary>
    public void ForceFullFrame() => _forceFull = true;

    public EncodedFrame Encode(IntPtr bits, int quality, int frameId)
    {
        bool full = _forceFull;
        _forceFull = false;

        var tiles = new Tile[_bands];
        IntPtr prev = _previousPtr;
        Parallel.For(0, _bands, Parallelism, band => tiles[band] = EncodeBand((byte*)bits, (byte*)prev, band, full, quality));

        int count = 0, total = HeaderSize;
        foreach (var t in tiles)
        {
            if (t.Data is null) continue;
            count++;
            total += TileHeaderSize + t.Length;
        }
        if (count == 0) return EncodedFrame.Empty;

        var buf = ArrayPool<byte>.Shared.Rent(total);
        var span = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, frameId);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)_width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)_height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)count);
        int off = HeaderSize;
        foreach (var t in tiles)
        {
            if (t.Data is null) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(span[off..], (ushort)t.X);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(off + 2)..], (ushort)t.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(off + 4)..], (ushort)t.W);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(off + 6)..], (ushort)t.H);
            BinaryPrimitives.WriteInt32LittleEndian(span[(off + 8)..], t.Length);
            off += TileHeaderSize;
            t.Data.AsSpan(0, t.Length).CopyTo(span[off..]);
            off += t.Length;
            ArrayPool<byte>.Shared.Return(t.Data);
        }
        return new EncodedFrame(buf, total, count);
    }

    private Tile EncodeBand(byte* src, byte* prev, int band, bool full, int quality)
    {
        int y0 = band * Block;
        int bh = Math.Min(Block, _height - y0);
        int minCol, maxCol;

        if (full)
        {
            minCol = 0;
            maxCol = _cols - 1;
        }
        else
        {
            // 1) A faixa mudou? (comparação de linhas inteiras, rápida no caso comum de tela parada)
            bool changed = false;
            for (int r = 0; r < bh && !changed; r++)
            {
                long o = (long)(y0 + r) * _stride;
                changed = !new ReadOnlySpan<byte>(src + o, _stride).SequenceEqual(new ReadOnlySpan<byte>(prev + o, _stride));
            }
            if (!changed) return default;

            // 2) Delimita o trecho horizontal alterado.
            minCol = 0;
            while (minCol < _cols && !BlockChanged(src, prev, minCol, y0, bh)) minCol++;
            if (minCol == _cols) return default;
            maxCol = _cols - 1;
            while (maxCol > minCol && !BlockChanged(src, prev, maxCol, y0, bh)) maxCol--;
        }

        int sx = minCol * Block;
        int sw = Math.Min(_width, (maxCol + 1) * Block) - sx;

        for (int r = 0; r < bh; r++)
        {
            long o = (long)(y0 + r) * _stride + sx * 4L;
            Buffer.MemoryCopy(src + o, prev + o, sw * 4L, sw * 4L);
        }

        var ms = t_stream ??= new MemoryStream(512 * 1024);
        ms.SetLength(0);
        using (var bmp = new Bitmap(sw, bh, _stride, PixelFormat.Format32bppRgb, (IntPtr)(src + (long)y0 * _stride + sx * 4L)))
        {
            bmp.Save(ms, JpegCodec, GetParams(quality));
        }
        int len = (int)ms.Length;
        var data = ArrayPool<byte>.Shared.Rent(len);
        ms.GetBuffer().AsSpan(0, len).CopyTo(data);
        return new Tile(sx, y0, sw, bh, data, len);
    }

    private bool BlockChanged(byte* src, byte* prev, int col, int y0, int bh)
    {
        int x0 = col * Block;
        int bytes = Math.Min(Block, _width - x0) * 4;
        for (int r = 0; r < bh; r++)
        {
            long o = (long)(y0 + r) * _stride + x0 * 4L;
            if (!new ReadOnlySpan<byte>(src + o, bytes).SequenceEqual(new ReadOnlySpan<byte>(prev + o, bytes)))
                return true;
        }
        return false;
    }

    private static EncoderParameters GetParams(int quality)
    {
        if (t_params is null || t_quality != quality)
        {
            t_params?.Dispose();
            t_params = new EncoderParameters(1);
            t_params.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            t_quality = quality;
        }
        return t_params;
    }

    private readonly record struct Tile(int X, int Y, int W, int H, byte[]? Data, int Length);
}

internal static class TileDecoder
{
    private static readonly ParallelOptions Parallelism = new()
    {
        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
    };

    /// <summary>Decodifica um quadro na superfície. Retorna o frameId.</summary>
    public static int Decode(byte[] buffer, int length, FrameSurface surface)
    {
        var span = buffer.AsSpan(0, length);
        if (length < 10) throw new InvalidDataException("Quadro curto demais");
        int frameId = BinaryPrimitives.ReadInt32LittleEndian(span);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        if (width == 0 || height == 0) throw new InvalidDataException("Resolução inválida");

        var tiles = new (int X, int Y, int Offset, int Length)[count];
        int off = 10;
        for (int i = 0; i < count; i++)
        {
            if (off + 12 > length) throw new InvalidDataException("Quadro truncado");
            int x = BinaryPrimitives.ReadUInt16LittleEndian(span[off..]);
            int y = BinaryPrimitives.ReadUInt16LittleEndian(span[(off + 2)..]);
            int len = BinaryPrimitives.ReadInt32LittleEndian(span[(off + 8)..]);
            off += 12;
            if (len < 0 || off + len > length) throw new InvalidDataException("Quadro truncado");
            tiles[i] = (x, y, off, len);
            off += len;
        }

        surface.EnsureSize(width, height);

        if (count <= 1)
        {
            foreach (var t in tiles) DecodeTile(buffer, t.Offset, t.Length, t.X, t.Y, surface);
        }
        else
        {
            Parallel.For(0, count, Parallelism, i =>
            {
                var t = tiles[i];
                DecodeTile(buffer, t.Offset, t.Length, t.X, t.Y, surface);
            });
        }
        surface.MarkUpdated();
        return frameId;
    }

    private static void DecodeTile(byte[] buffer, int offset, int length, int x, int y, FrameSurface surface)
    {
        using var ms = new MemoryStream(buffer, offset, length, writable: false);
        using var img = (Bitmap)Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
        surface.CopyFromBitmap(img, x, y);
    }
}
