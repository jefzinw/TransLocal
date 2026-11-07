using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DFactory = Vortice.Direct2D1.ID2D1Factory;

namespace TransLocal.Video;

/// <summary>
/// Caminho do PC todo na placa de vídeo, como Sunshine/Parsec/Xbox Cloud: o quadro capturado fica numa textura,
/// o cursor é desenhado por cima com Direct2D, o processador de vídeo da placa reduz a imagem e converte para NV12
/// (BT.601, faixa de vídeo) e o codificador da placa lê essa textura. Nada passa pela memória do processador —
/// antes, só reduzir 1920×1080 para 1366×768 levava ~11 ms por quadro.
/// </summary>
internal sealed unsafe class GpuEncodePipeline : IDisposable
{
    private readonly ID3D11Device _dev;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VideoDevice _vdev;
    private readonly ID3D11VideoContext _vctx;
    private readonly int _gen;
    private readonly bool _ownsDevice;
    private readonly int _srcW, _srcH, _dstW, _dstH;
    private readonly ID3D11VideoProcessorEnumerator _vpEnum;
    private readonly ID3D11VideoProcessor _vp;
    private readonly ID3D11Texture2D[] _ring;
    private readonly ID3D11VideoProcessorOutputView[] _outViews;
    private ID3D11Texture2D? _compose;
    private ID3D11VideoProcessorInputView? _inView;
    private Format _composeFormat;
    private int _next;

    // Cursor (Direct2D sobre a textura montada)
    private readonly D2DFactory _d2d;
    private ID2D1RenderTarget? _rt;
    private readonly Dictionary<IntPtr, (ID2D1Bitmap? Bitmap, int W, int H)> _cursors = new();
    private bool _cursorFailed;

    public H264Encoder Encoder { get; }

    private GpuEncodePipeline(ScreenCapturer cap, int dstW, int dstH, int fps, int bitrate, bool high)
    {
        if (cap is DxgiCapturer { Device: { } capDev } dx)
        {
            _dev = capDev;
            _gen = dx.DeviceGeneration;
        }
        else
        {
            // Captura sem placa (GDI, teste): a imagem sobe uma vez para a placa; redução e conversão continuam nela.
            var levels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0 };
            D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
                levels, out ID3D11Device? own).CheckError();
            _dev = own!;
            _ownsDevice = true;
            using (var mt = _dev.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        }
        _ctx = _dev.ImmediateContext;
        _vdev = _dev.QueryInterface<ID3D11VideoDevice>();
        _vctx = _ctx.QueryInterface<ID3D11VideoContext>();
        _srcW = cap.Width;
        _srcH = cap.Height;
        _dstW = dstW;
        _dstH = dstH;

        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)_srcW, InputHeight = (uint)_srcH,
            OutputWidth = (uint)dstW, OutputHeight = (uint)dstH,
            InputFrameRate = new Rational((uint)fps, 1), OutputFrameRate = new Rational((uint)fps, 1),
            Usage = VideoUsage.OptimalSpeed,
        };
        _vdev.CreateVideoProcessorEnumerator(ref desc, out _vpEnum!).CheckError();
        _vdev.CreateVideoProcessor(_vpEnum, 0, out _vp!).CheckError();
        _vctx.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
        _vctx.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        _vctx.VideoProcessorSetStreamColorSpace(_vp, 0, ColorSpace(0));       // entrada: RGB 0–255
        _vctx.VideoProcessorSetOutputColorSpace(_vp, ColorSpace(1u << 4));    // saída: YCbCr BT.601, 16–235
        _vctx.VideoProcessorSetStreamSourceRect(_vp, 0, true, new Vortice.RawRect(0, 0, _srcW, _srcH));
        _vctx.VideoProcessorSetStreamDestRect(_vp, 0, true, new Vortice.RawRect(0, 0, dstW, dstH));
        _vctx.VideoProcessorSetOutputTargetRect(_vp, true, new Vortice.RawRect(0, 0, dstW, dstH));

        // Várias texturas de saída: o codificador pode ainda estar lendo a anterior.
        _ring = new ID3D11Texture2D[4];
        _outViews = new ID3D11VideoProcessorOutputView[_ring.Length];
        for (int i = 0; i < _ring.Length; i++)
        {
            _ring[i] = _dev.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)dstW, Height = (uint)dstH, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            var od = new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            };
            _vdev.CreateVideoProcessorOutputView(_ring[i], _vpEnum, od, out _outViews[i]!).CheckError();
        }
        _d2d = D2D1.D2D1CreateFactory<D2DFactory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        Encoder = H264Encoder.TryCreateForTextures(_dev, dstW, dstH, fps, bitrate, high)
                  ?? throw new NotSupportedException("nenhum codificador da placa aceita texturas");
    }

    private static VideoProcessorColorSpace ColorSpace(uint bits)
    {
        var cs = default(VideoProcessorColorSpace);
        *(uint*)&cs = bits;
        return cs;
    }

    /// <summary>Monta o caminho na placa. Null se algo não for suportado (o PC segue pelo caminho do processador).</summary>
    public static GpuEncodePipeline? TryCreate(ScreenCapturer cap, int dstW, int dstH, int fps, int bitrate, bool high)
    {
        if (Environment.GetEnvironmentVariable("TRANSLOCAL_NO_GPU_ENCODE") == "1") return null;
        try { return new GpuEncodePipeline(cap, dstW, dstH, fps, bitrate, high); }
        catch (Exception ex)
        {
            Log.Info($"Caminho todo na placa de vídeo indisponível ({ex.Message}); reduzindo e convertendo pelo processador");
            return null;
        }
    }

    /// <summary>O dispositivo da captura foi recriado: este caminho precisa ser montado de novo.</summary>
    public bool IsStale(ScreenCapturer cap) => cap is DxgiCapturer dx
        ? _ownsDevice || dx.DeviceGeneration != _gen || dx.Device is null || dx.Device.NativePointer != _dev.NativePointer
        : !_ownsDevice;

    private void EnsureCompose(Format format)
    {
        if (_compose is not null && _composeFormat == format) return;
        _rt?.Dispose(); _rt = null;
        foreach (var c in _cursors.Values) c.Bitmap?.Dispose();
        _cursors.Clear();
        _inView?.Dispose();
        _compose?.Dispose();
        _compose = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_srcW, Height = (uint)_srcH, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        var d = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        };
        _vdev.CreateVideoProcessorInputView(_compose, _vpEnum, d, out _inView).CheckError();
        _composeFormat = format;
    }

    /// <summary>Monta (quadro + cursor), reduz/converte na placa e entrega ao codificador.</summary>
    public byte[]? Encode(ScreenCapturer cap, bool forceKey, out bool key, long captureTime)
    {
        if (cap is DxgiCapturer { GpuMode: true } dx)
        {
            var gpuFrame = dx.GpuFrame;
            if (gpuFrame is not null)
            {
                EnsureCompose(gpuFrame.Description.Format);
                _ctx.CopyResource(_compose!, gpuFrame);
            }
            else
            {
                // O quadro veio pela memória (GDI enquanto o DXGI não volta, tela segura do UAC): envia para a placa.
                EnsureCompose(Format.B8G8R8A8_UNorm);
                _ctx.UpdateSubresource(_compose!, 0, null, dx.CleanBits, (uint)(_srcW * 4), (uint)(_srcW * 4 * _srcH));
            }
            if (cap.CursorTopLeft(out var cursor, out int cx, out int cy)) DrawCursor(cursor, cx, cy);
        }
        else
        {
            // Outras capturas já trazem o cursor desenhado na imagem da memória.
            EnsureCompose(Format.B8G8R8A8_UNorm);
            _ctx.UpdateSubresource(_compose!, 0, null, cap.Bits, (uint)(_srcW * 4), (uint)(_srcW * 4 * _srcH));
        }

        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inView! };
        int slot = _next;
        _next = (_next + 1) % _ring.Length;
        _vctx.VideoProcessorBlt(_vp, _outViews[slot], 0, 1, new[] { stream }).CheckError();
        return Encoder.EncodeTexture(_ring[slot], forceKey, out key, captureTime);
    }

    private void DrawCursor(IntPtr handle, int x, int y)
    {
        if (_cursorFailed) return;
        try
        {
            if (_rt is null)
            {
                using var surface = _compose!.QueryInterface<IDXGISurface>();
                _rt = _d2d.CreateDxgiSurfaceRenderTarget(surface, new RenderTargetProperties(
                    new Vortice.DCommon.PixelFormat(_composeFormat, Vortice.DCommon.AlphaMode.Premultiplied)) { DpiX = 96, DpiY = 96 });
            }
            if (!_cursors.TryGetValue(handle, out var c))
            {
                if (_cursors.Count > 64) { foreach (var v in _cursors.Values) v.Bitmap?.Dispose(); _cursors.Clear(); }
                var pixels = CursorPixels(handle, out int w, out int h);
                ID2D1Bitmap? bmp = null;
                if (pixels is not null)
                {
                    fixed (byte* p = pixels)
                        bmp = _rt.CreateBitmap(new SizeI(w, h), (IntPtr)p, (uint)(w * 4),
                            new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
                }
                c = (bmp, w, h);
                _cursors[handle] = c;
            }
            if (c.Bitmap is null) return;
            _rt.BeginDraw();
            _rt.DrawBitmap(c.Bitmap, new Rect(x, y, c.W, c.H), 1f, BitmapInterpolationMode.NearestNeighbor, null);
            _rt.EndDraw();
        }
        catch (Exception ex)
        {
            _cursorFailed = true;
            Log.Warn("Cursor pela placa de vídeo indisponível (a imagem segue sem o cursor)", ex);
        }
    }

    /// <summary>
    /// Desenha o cursor do Windows sobre fundo preto e sobre fundo branco: a diferença dá a transparência de cada
    /// pixel (funciona também com cursores monocromáticos, como o “I” de texto). BGRA pré-multiplicado.
    /// </summary>
    private static byte[]? CursorPixels(IntPtr handle, out int w, out int h)
    {
        const int S = 128;
        w = h = 0;
        var black = Render(handle, 0x00, S);
        var white = Render(handle, 0xFF, S);
        if (black is null || white is null) return null;
        int maxX = -1, maxY = -1;
        var outp = new byte[S * S * 4];
        for (int i = 0; i < S * S; i++)
        {
            int o = i * 4;
            int alpha = 255 - Math.Clamp(white[o + 1] - black[o + 1], 0, 255);
            if (alpha <= 0) continue;
            outp[o] = black[o];
            outp[o + 1] = black[o + 1];
            outp[o + 2] = black[o + 2];
            outp[o + 3] = (byte)alpha;
            maxX = Math.Max(maxX, i % S);
            maxY = Math.Max(maxY, i / S);
        }
        if (maxX < 0) return null;
        w = maxX + 1;
        h = maxY + 1;
        var crop = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(outp, y * S * 4, crop, y * w * 4, w * 4);
        return crop;
    }

    private static byte[]? Render(IntPtr handle, byte background, int size)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        var dc = Native.CreateCompatibleDC(screen);
        try
        {
            var bmi = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32,
                }
            };
            var dib = Native.CreateDIBSection(screen, ref bmi, Native.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return null;
            var old = Native.SelectObject(dc, dib);
            try
            {
                new Span<byte>((void*)bits, size * size * 4).Fill(background);
                Native.DrawIconEx(dc, 0, 0, handle, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
                Native.GdiFlush();
                var result = new byte[size * size * 4];
                Marshal.Copy(bits, result, 0, result.Length);
                return result;
            }
            finally
            {
                Native.SelectObject(dc, old);
                Native.DeleteObject(dib);
            }
        }
        finally
        {
            Native.DeleteDC(dc);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose()
    {
        Encoder.Dispose();
        foreach (var c in _cursors.Values) c.Bitmap?.Dispose();
        _rt?.Dispose();
        _d2d.Dispose();
        _inView?.Dispose();
        _compose?.Dispose();
        foreach (var v in _outViews) v?.Dispose();
        foreach (var t in _ring) t?.Dispose();
        _vp.Dispose();
        _vpEnum.Dispose();
        _vctx.Dispose();
        _vdev.Dispose();
        _ctx.Dispose();
        if (_ownsDevice) _dev.Dispose();
    }
}
