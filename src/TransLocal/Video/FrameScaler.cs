using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TransLocal.Video;

/// <summary>
/// Reduz o quadro capturado para a resolução de envio por média de área (cada pixel de saída é a média
/// ponderada da região que ele cobre na origem), o que mantém o texto legível. Separável (horizontal e depois
/// vertical), com pesos pré-calculados em ponto fixo e linhas processadas em paralelo.
/// </summary>
internal sealed unsafe class FrameScaler
{
    private const int Shift = 14;
    private const int One = 1 << Shift;
    private const int Round = 1 << (Shift - 1);

    private static readonly ParallelOptions Parallelism = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };

    private readonly int _srcW, _srcH;
    private readonly Taps _x, _y;
    private readonly byte[] _tmp;  // srcH linhas × largura de saída
    private readonly byte[] _out;
    private readonly byte* _tmpPtr;
    private readonly byte* _outPtr;

    public int Width { get; }
    public int Height { get; }
    public IntPtr Bits => (IntPtr)_outPtr;

    public FrameScaler(int srcWidth, int srcHeight, int width, int height)
    {
        if (width > srcWidth || height > srcHeight) throw new ArgumentException("O redutor não amplia imagens");
        _srcW = srcWidth;
        _srcH = srcHeight;
        Width = width;
        Height = height;
        _x = Taps.Create(srcWidth, width);
        _y = Taps.Create(srcHeight, height);
        _tmp = GC.AllocateUninitializedArray<byte>(width * srcHeight * 4, pinned: true);
        _out = GC.AllocateUninitializedArray<byte>(width * height * 4, pinned: true);
        _tmpPtr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_tmp));
        _outPtr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_out));
    }

    public void Scale(ScreenCapturer source) => Scale(source.Bits);

    public void Scale(IntPtr sourceBits)
    {
        IntPtr src = sourceBits;
        // 1) Horizontal: cada linha da origem vira uma linha com a largura de saída.
        Parallel.For(0, _srcH, Parallelism, y => HorizontalRow((byte*)src + (long)y * _srcW * 4, _tmpPtr + (long)y * Width * 4));
        // 2) Vertical: combina as linhas intermediárias.
        Parallel.For(0, Height, Parallelism, VerticalRow);
    }

    private void HorizontalRow(byte* src, byte* dst)
    {
        var t = _x;
        for (int ox = 0; ox < Width; ox++)
        {
            int start = t.Start[ox], count = t.Count[ox], wo = t.WeightOffset[ox];
            int b = Round, g = Round, r = Round;
            byte* p = src + start * 4;
            for (int k = 0; k < count; k++, p += 4)
            {
                int w = t.Weights[wo + k];
                b += p[0] * w;
                g += p[1] * w;
                r += p[2] * w;
            }
            byte* d = dst + ox * 4;
            d[0] = (byte)(b >> Shift);
            d[1] = (byte)(g >> Shift);
            d[2] = (byte)(r >> Shift);
            d[3] = 255;
        }
    }

    private void VerticalRow(int oy)
    {
        var t = _y;
        int start = t.Start[oy], count = t.Count[oy], wo = t.WeightOffset[oy];
        int rowBytes = Width * 4;
        byte* dst = _outPtr + (long)oy * rowBytes;
        if (count == 1)
        {
            Buffer.MemoryCopy(_tmpPtr + (long)start * rowBytes, dst, rowBytes, rowBytes);
            return;
        }
        int n = Width * 3;
        int[] acc = System.Buffers.ArrayPool<int>.Shared.Rent(n);
        try
        {
            Array.Fill(acc, Round, 0, n);
            for (int k = 0; k < count; k++)
            {
                int w = t.Weights[wo + k];
                byte* s = _tmpPtr + (long)(start + k) * rowBytes;
                for (int ox = 0, a = 0; ox < Width; ox++, a += 3, s += 4)
                {
                    acc[a] += s[0] * w;
                    acc[a + 1] += s[1] * w;
                    acc[a + 2] += s[2] * w;
                }
            }
            for (int ox = 0, a = 0; ox < Width; ox++, a += 3, dst += 4)
            {
                dst[0] = (byte)(acc[a] >> Shift);
                dst[1] = (byte)(acc[a + 1] >> Shift);
                dst[2] = (byte)(acc[a + 2] >> Shift);
                dst[3] = 255;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(acc);
        }
    }

    /// <summary>
    /// Tamanho de envio que cabe em <paramref name="maxW"/>×<paramref name="maxH"/> mantendo a proporção, sem
    /// nunca aumentar. Se a proporção difere menos de 1% da tela de destino, usa exatamente o tamanho dela
    /// (ex.: 1920×1080 → 1366×768), para o outro computador exibir 1:1, sem redimensionar. 0 = sem limite.
    /// </summary>
    public static Size Fit(Size source, int maxW, int maxH)
    {
        if (maxW <= 0 || maxH <= 0 || (source.Width <= maxW && source.Height <= maxH)) return source;
        double srcAspect = source.Width / (double)source.Height;
        double boxAspect = maxW / (double)maxH;
        if (Math.Abs(srcAspect / boxAspect - 1) < 0.01) return new Size(maxW, maxH);
        double s = Math.Min(maxW / (double)source.Width, maxH / (double)source.Height);
        return new Size(Math.Max(2, (int)Math.Round(source.Width * s)), Math.Max(2, (int)Math.Round(source.Height * s)));
    }

    /// <summary>Pesos de cobertura de área (ponto fixo, soma = 1) de cada pixel de saída numa dimensão.</summary>
    private sealed class Taps
    {
        public required int[] Start;
        public required int[] Count;
        public required int[] WeightOffset;
        public required int[] Weights;

        public static Taps Create(int src, int dst)
        {
            double scale = src / (double)dst;
            var start = new int[dst];
            var count = new int[dst];
            var offset = new int[dst];
            var weights = new List<int>(dst * 3);
            for (int i = 0; i < dst; i++)
            {
                double a = i * scale, b = Math.Min(src, (i + 1) * scale);
                int j0 = (int)Math.Floor(a);
                int j1 = Math.Min(src - 1, (int)Math.Ceiling(b) - 1);
                start[i] = j0;
                count[i] = j1 - j0 + 1;
                offset[i] = weights.Count;
                int sum = 0;
                for (int j = j0; j <= j1; j++)
                {
                    double cover = Math.Min(j + 1, b) - Math.Max(j, a);
                    int w = (int)Math.Round(Math.Max(0, cover) / scale * One);
                    weights.Add(w);
                    sum += w;
                }
                weights[offset[i] + count[i] - 1] += One - sum; // corrige o arredondamento
            }
            return new Taps { Start = start, Count = count, WeightOffset = offset, Weights = weights.ToArray() };
        }
    }
}
