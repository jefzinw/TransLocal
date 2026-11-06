using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TransLocal.Video;

/// <summary>
/// Imagem recebida (DIB 32 bpp num DC de memória). Decodificadores escrevem nela a partir de outras
/// threads; a janela de exibição a desenha com BitBlt/StretchBlt. Todo acesso é feito sob <see cref="Sync"/>.
/// </summary>
internal sealed unsafe class FrameSurface : IDisposable
{
    public readonly object Sync = new();

    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private byte* _bits;
    private int _width;
    private int _height;
    private long _updates;
    private bool _disposed;

    public int Width { get { lock (Sync) return _width; } }
    public int Height { get { lock (Sync) return _height; } }
    public bool HasImage => Interlocked.Read(ref _updates) > 0;

    public void MarkUpdated() => Interlocked.Increment(ref _updates);

    public void EnsureSize(int width, int height)
    {
        lock (Sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FrameSurface));
            if (_dib != IntPtr.Zero && width == _width && height == _height) return;
            Free();

            var screenDc = Native.GetDC(IntPtr.Zero);
            try
            {
                _memDc = Native.CreateCompatibleDC(screenDc);
                var bmi = new Native.BITMAPINFO
                {
                    bmiHeader = new Native.BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                    }
                };
                _dib = Native.CreateDIBSection(screenDc, ref bmi, Native.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
                if (_dib == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    Native.DeleteDC(_memDc);
                    _memDc = IntPtr.Zero;
                    throw new InvalidOperationException("Não foi possível alocar a imagem de exibição.");
                }
                _oldBitmap = Native.SelectObject(_memDc, _dib);
                _bits = (byte*)bits; // DIB nova já vem zerada (preta)
                _width = width;
                _height = height;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    public void CopyFromBitmap(Bitmap bmp, int x, int y)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            lock (Sync)
            {
                if (_bits == null) return;
                int w = Math.Min(data.Width, _width - x);
                int h = Math.Min(data.Height, _height - y);
                if (w <= 0 || h <= 0 || x < 0 || y < 0) return;
                int dstStride = _width * 4;
                byte* src = (byte*)data.Scan0;
                for (int r = 0; r < h; r++)
                {
                    Buffer.MemoryCopy(src + (long)r * data.Stride, _bits + (long)(y + r) * dstStride + x * 4L, w * 4L, w * 4L);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Caminho rápido para imagens BMP (24/32 bpp), formato entregue pela placa de captura
    /// quando o vídeo é descompactado (YUY2/NV12 convertidos). Retorna false se não for BMP.</summary>
    public bool TryCopyFromBmpFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M') return false;
        int pixOffset = BinaryPrimitives.ReadInt32LittleEndian(data[10..]);
        int w = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int hRaw = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        int bpp = BinaryPrimitives.ReadInt16LittleEndian(data[28..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(data[30..]);
        if (w <= 0 || hRaw == 0 || (bpp != 24 && bpp != 32) || (compression != 0 && compression != 3)) return false;
        bool bottomUp = hRaw > 0;
        int h = Math.Abs(hRaw);
        int srcStride = ((w * bpp + 31) / 32) * 4;
        if (pixOffset < 0 || (long)pixOffset + (long)srcStride * h > data.Length) return false;

        EnsureSize(w, h);
        lock (Sync)
        {
            if (_bits == null) return true;
            int dstStride = _width * 4;
            fixed (byte* p = data)
            {
                byte* pix = p + pixOffset;
                for (int r = 0; r < h; r++)
                {
                    byte* srow = pix + (long)(bottomUp ? h - 1 - r : r) * srcStride;
                    byte* drow = _bits + (long)r * dstStride;
                    if (bpp == 32)
                    {
                        Buffer.MemoryCopy(srow, drow, w * 4L, w * 4L);
                    }
                    else
                    {
                        for (int i = 0; i < w; i++)
                        {
                            drow[0] = srow[0];
                            drow[1] = srow[1];
                            drow[2] = srow[2];
                            drow[3] = 255;
                            srow += 3;
                            drow += 4;
                        }
                    }
                }
            }
        }
        MarkUpdated();
        return true;
    }

    /// <summary>Calcula o retângulo de destino mantendo a proporção (barras pretas nas sobras).</summary>
    public static Rectangle Fit(Rectangle area, int w, int h)
    {
        if (w <= 0 || h <= 0 || area.Width <= 0 || area.Height <= 0) return Rectangle.Empty;
        double s = Math.Min(area.Width / (double)w, area.Height / (double)h);
        int dw = Math.Max(1, (int)Math.Round(w * s));
        int dh = Math.Max(1, (int)Math.Round(h * s));
        return new Rectangle(area.X + (area.Width - dw) / 2, area.Y + (area.Height - dh) / 2, dw, dh);
    }

    /// <summary>Desenha no DC informado; retorna o retângulo ocupado pela imagem.</summary>
    public Rectangle Paint(IntPtr hdc, Rectangle client)
    {
        var black = Native.GetStockObject(Native.BLACK_BRUSH);
        lock (Sync)
        {
            if (_dib == IntPtr.Zero || _disposed)
            {
                var all = new Native.RECT(client.Left, client.Top, client.Right, client.Bottom);
                Native.FillRect(hdc, ref all, black);
                return Rectangle.Empty;
            }

            var dest = Fit(client, _width, _height);
            if (dest.Top > client.Top) FillBlack(hdc, client.Left, client.Top, client.Right, dest.Top, black);
            if (dest.Bottom < client.Bottom) FillBlack(hdc, client.Left, dest.Bottom, client.Right, client.Bottom, black);
            if (dest.Left > client.Left) FillBlack(hdc, client.Left, dest.Top, dest.Left, dest.Bottom, black);
            if (dest.Right < client.Right) FillBlack(hdc, dest.Right, dest.Top, client.Right, dest.Bottom, black);

            if (dest.Width == _width && dest.Height == _height)
            {
                Native.BitBlt(hdc, dest.X, dest.Y, _width, _height, _memDc, 0, 0, Native.SRCCOPY);
            }
            else
            {
                Native.SetStretchBltMode(hdc, Native.HALFTONE);
                Native.SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                Native.StretchBlt(hdc, dest.X, dest.Y, dest.Width, dest.Height, _memDc, 0, 0, _width, _height, Native.SRCCOPY);
            }
            return dest;
        }
    }

    private static void FillBlack(IntPtr hdc, int l, int t, int r, int b, IntPtr brush)
    {
        var rc = new Native.RECT(l, t, r, b);
        Native.FillRect(hdc, ref rc, brush);
    }

    public void SavePng(string path)
    {
        lock (Sync)
        {
            if (_bits == null) throw new InvalidOperationException("Nenhuma imagem recebida");
            using var bmp = new Bitmap(_width, _height, _width * 4, PixelFormat.Format32bppRgb, (IntPtr)_bits);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    private void Free()
    {
        if (_memDc != IntPtr.Zero)
        {
            Native.SelectObject(_memDc, _oldBitmap);
            Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero)
        {
            Native.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
        _bits = null;
        _width = _height = 0;
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_disposed) return;
            _disposed = true;
            Free();
        }
    }
}
