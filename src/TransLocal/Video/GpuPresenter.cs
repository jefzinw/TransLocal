using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DFactory = Vortice.Direct2D1.ID2D1Factory;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode;

namespace TransLocal.Video;

/// <summary>
/// Dispositivo Direct3D 11 compartilhado entre o decodificador H.264 (DXVA) e a exibição: o quadro decodificado
/// nunca sai da placa de vídeo (como Moonlight/Parsec/Xbox Cloud fazem).
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VideoDevice VideoDevice { get; }
    public ID3D11VideoContext VideoContext { get; }
    public string AdapterName { get; }

    private GpuDevice(ID3D11Device device)
    {
        Device = device;
        Context = device.ImmediateContext;
        VideoDevice = device.QueryInterface<ID3D11VideoDevice>();
        VideoContext = Context.QueryInterface<ID3D11VideoContext>();
        using (var mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice1>();
            dxgi.MaximumFrameLatency = 1; // nada de fila de quadros: o mais novo vai para a tela
            using var adapter = dxgi.GetAdapter();
            AdapterName = adapter.Description.Description;
        }
        catch { AdapterName = "?"; }
    }

    public static GpuDevice? TryCreate()
    {
        try
        {
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
                levels, out ID3D11Device? device).CheckError();
            return new GpuDevice(device!);
        }
        catch (Exception ex)
        {
            Log.Info($"Placa de vídeo indisponível para exibir ({ex.Message}); desenhando pelo processador");
            return null;
        }
    }

    private int _refs = 1;

    /// <summary>Mais um usuário do dispositivo (a exibição); cada um chama <see cref="Dispose"/> ao terminar.</summary>
    public GpuDevice Acquire()
    {
        Interlocked.Increment(ref _refs);
        return this;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refs) != 0) return;
        VideoContext.Dispose();
        VideoDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>Recebe quadros decodificados que ainda estão na placa de vídeo.</summary>
internal interface IGpuFrameSink
{
    /// <summary>Mostra a fatia <paramref name="slice"/> de uma textura NV12. False = não deu (use o caminho do processador).</summary>
    bool PresentNv12(ID3D11Texture2D texture, int slice, int width, int height);

    /// <summary>Mostra um quadro NV12 que está na memória (decodificação por software): só a cópia vai para a placa.</summary>
    unsafe bool PresentNv12Memory(byte* y, int yStride, byte* uv, int uvStride, int width, int height);

    bool Failed { get; }
}

/// <summary>Textos desenhados por cima da imagem.</summary>
internal readonly record struct OverlayText(string? Stats, string? Notice, string? Waiting);

/// <summary>
/// Exibe a imagem numa cadeia de troca DXGI (modelo flip) na janela: conversão de cor e redimensionamento pelo
/// processador de vídeo da placa (alta qualidade, quase sem custo), textos por Direct2D e apresentação imediata
/// — sem esperar a fila de mensagens do Windows nem copiar a imagem para a memória do processador.
/// Thread-safe: quadros chegam da thread de rede; redimensionamento e textos, da interface.
/// </summary>
internal sealed unsafe class GpuPresenter : IGpuFrameSink, IDisposable
{
    private readonly object _lock = new();
    private readonly GpuDevice _gpu;
    private readonly IDXGISwapChain1 _swap;
    private readonly D2DFactory _d2d;
    private readonly IDWriteFactory _dwrite;
    private IDWriteTextFormat? _font;
    private float _fontPx;
    private ID2D1RenderTarget? _rt;
    private ID2D1SolidColorBrush? _white, _gray, _statsBg, _noticeBg;

    private ID3D11Texture2D? _back;
    private ID3D11VideoProcessorOutputView? _outView;
    private ID3D11VideoProcessorEnumerator? _vpEnum;
    private ID3D11VideoProcessor? _vp;
    private int _vpW, _vpH;
    private ID3D11Texture2D? _nv12, _bgra;
    private ID3D11VideoProcessorInputView? _nv12View, _bgraView;
    private enum Source { None, Nv12, Bgra }
    private Source _source;
    private int _srcW, _srcH;
    private int _width, _height, _wantW, _wantH;
    private bool _disposed;

    public bool Failed { get; private set; }

    /// <summary>Quadros mostrados (estatística).</summary>
    public long Presented;

    /// <summary>Testes: recebe a imagem pronta (antes de ir para a tela).</summary>
    internal Action<ID3D11Texture2D>? BeforePresent { get; set; }

    internal GpuDevice Device => _gpu;

    /// <summary>Textos atuais (lidos a cada desenho).</summary>
    public Func<OverlayText>? Overlay { get; set; }

    /// <summary>Retângulo ocupado pela imagem na janela (para mapear o mouse).</summary>
    public Rectangle Dest { get; private set; }

    /// <summary>A placa de vídeo parou de exibir (driver reiniciado etc.): a janela volta ao desenho pelo processador.</summary>
    public event Action? Lost;

    public int SourceWidth => _srcW;
    public int SourceHeight => _srcH;
    public bool HasImage => _source != Source.None;

    private GpuPresenter(GpuDevice gpu, IntPtr hwnd, int width, int height)
    {
        _gpu = gpu.Acquire();
        _width = _wantW = Math.Max(1, width);
        _height = _wantH = Math.Max(1, height);
        using var dxgi = gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swap = factory.CreateSwapChainForHwnd(gpu.Device, hwnd, new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        }, null, null);
        // Alt+Enter e afins vão para o jogo no outro computador, não para o DXGI.
        try { factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll); } catch { }
        _d2d = D2D1.D2D1CreateFactory<D2DFactory>(Vortice.Direct2D1.FactoryType.MultiThreaded);
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        CreateTargets();
    }

    public static GpuPresenter? TryCreate(GpuDevice gpu, IntPtr hwnd, int width, int height)
    {
        try { return new GpuPresenter(gpu, hwnd, width, height); }
        catch (Exception ex)
        {
            // O construtor pode ter falhado depois de Acquire(): a contagem fica um a mais (o dispositivo vive até o fim
            // do app nesse caso raro — preferível a liberá-lo enquanto o decodificador o usa).
            Log.Warn("Exibição pela placa de vídeo indisponível; desenhando pelo processador", ex);
            return null;
        }
    }

    public void SetFont(string family, float px)
    {
        lock (_lock)
        {
            if (_font is not null && Math.Abs(px - _fontPx) < 0.1f) return;
            _font?.Dispose();
            _font = _dwrite.CreateTextFormat(family, null, FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, px, "pt-br");
            _fontPx = px;
        }
    }

    private void CreateTargets()
    {
        _back = _swap.GetBuffer<ID3D11Texture2D>(0);
        using var surface = _back.QueryInterface<IDXGISurface>();
        _rt = _d2d.CreateDxgiSurfaceRenderTarget(surface, new RenderTargetProperties(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)) { DpiX = 96, DpiY = 96 });
        _rt.TextAntialiasMode = TextAntialiasMode.Grayscale;
        _white = _rt.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        _gray = _rt.CreateSolidColorBrush(new Color4(0.55f, 0.55f, 0.55f, 1f));
        _statsBg = _rt.CreateSolidColorBrush(new Color4(20 / 255f, 20 / 255f, 20 / 255f, 200 / 255f));
        _noticeBg = _rt.CreateSolidColorBrush(new Color4(120 / 255f, 72 / 255f, 0f, 225 / 255f));
        _outView?.Dispose();
        _outView = null;
    }

    private void ReleaseTargets()
    {
        _white?.Dispose(); _gray?.Dispose(); _statsBg?.Dispose(); _noticeBg?.Dispose();
        _white = _gray = _statsBg = _noticeBg = null;
        _rt?.Dispose(); _rt = null;
        _outView?.Dispose(); _outView = null;
        _back?.Dispose(); _back = null;
    }

    /// <summary>Novo tamanho da janela (aplicado no próximo desenho).</summary>
    public void Resize(int width, int height)
    {
        lock (_lock)
        {
            _wantW = Math.Max(1, width);
            _wantH = Math.Max(1, height);
        }
    }

    /// <summary>Processador de vídeo para o tamanho de imagem atual (recriado só quando a resolução muda).</summary>
    private void EnsureProcessor(int w, int h)
    {
        if (_vp is not null && _vpW == w && _vpH == h) return;
        _nv12View?.Dispose(); _nv12View = null;
        _bgraView?.Dispose(); _bgraView = null;
        _outView?.Dispose(); _outView = null;
        _vp?.Dispose(); _vp = null;
        _vpEnum?.Dispose(); _vpEnum = null;
        var desc = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)w,
            InputHeight = (uint)h,
            OutputWidth = (uint)Math.Max(w, _width),
            OutputHeight = (uint)Math.Max(h, _height),
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        };
        _gpu.VideoDevice.CreateVideoProcessorEnumerator(ref desc, out _vpEnum).CheckError();
        _gpu.VideoDevice.CreateVideoProcessor(_vpEnum!, 0, out _vp).CheckError();
        var vc = _gpu.VideoContext;
        // Sem "melhorias" automáticas do driver (cada uma acrescenta atraso).
        vc.VideoProcessorSetStreamAutoProcessingMode(_vp!, 0, false);
        vc.VideoProcessorSetStreamFrameFormat(_vp!, 0, VideoFrameFormat.Progressive);
        vc.VideoProcessorSetStreamOutputRate(_vp!, 0, VideoProcessorOutputRate.Normal, false, null);
        vc.VideoProcessorSetOutputBackgroundColor(_vp!, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        // Saída em RGB completo (0–255), como a tela espera. Sem isso algumas placas comprimem a faixa duas vezes
        // e a imagem fica "lavada" (preto vira cinza escuro, branco vira cinza claro).
        vc.VideoProcessorSetOutputColorSpace(_vp!, ColorSpaceFullRgb);
        _vpW = w;
        _vpH = h;
    }

    // D3D11_VIDEO_PROCESSOR_COLOR_SPACE (campos de bits): Usage:1, RGB_Range:1 (0 = 0–255), YCbCr_Matrix:1 (0 = BT.601),
    // YCbCr_xvYCC:1, Nominal_Range:2 (1 = 16–235, 2 = 0–255).
    private static readonly VideoProcessorColorSpace ColorSpaceFullRgb = MakeColorSpace(0u);
    private static readonly VideoProcessorColorSpace ColorSpaceVideo601 = MakeColorSpace(1u << 4);

    private static VideoProcessorColorSpace MakeColorSpace(uint bits)
    {
        var cs = default(VideoProcessorColorSpace);
        *(uint*)&cs = bits;
        return cs;
    }

    private ID3D11VideoProcessorInputView MakeInputView(ID3D11Texture2D tex)
    {
        var d = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        };
        _gpu.VideoDevice.CreateVideoProcessorInputView(tex, _vpEnum!, d, out var view).CheckError();
        return view!;
    }

    public bool PresentNv12(ID3D11Texture2D texture, int slice, int width, int height)
    {
        if (Failed || _disposed) return false;
        lock (_lock)
        {
            try
            {
                width &= ~1; height &= ~1;
                EnsureProcessor(width, height);
                if (_nv12 is null || _nv12.Description.Width != width || _nv12.Description.Height != height)
                {
                    _nv12View?.Dispose(); _nv12View = null;
                    _nv12?.Dispose(); _nv12 = null;
                    // O processador de vídeo só lê NV12 de texturas "de decodificador" (ou de destino de desenho):
                    // tenta as combinações que as placas aceitam, da mais comum para a menos.
                    foreach (var bind in new[] { BindFlags.Decoder, BindFlags.RenderTarget | BindFlags.ShaderResource, BindFlags.RenderTarget })
                    {
                        try
                        {
                            var tex = _gpu.Device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                                Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Default, BindFlags = bind,
                            });
                            try { _nv12View = MakeInputView(tex); }
                            catch { tex.Dispose(); continue; }
                            _nv12 = tex;
                            break;
                        }
                        catch { }
                    }
                    if (_nv12 is null) throw new InvalidOperationException("A placa de vídeo não aceita imagem NV12 no processador de vídeo");
                }
                // Cópia dentro da placa de vídeo (o decodificador reaproveita a textura dele logo depois).
                _gpu.Context.CopySubresourceRegion(_nv12, 0, 0, 0, 0, texture, (uint)slice, new Box(0, 0, 0, width, height, 1));
                _nv12View ??= MakeInputView(_nv12); // processador de vídeo recriado (mudou a resolução)
                _source = Source.Nv12;
                _srcW = width;
                _srcH = height;
                Render();
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        }
    }

    private ID3D11Texture2D? _upload;

    public bool PresentNv12Memory(byte* y, int yStride, byte* uv, int uvStride, int width, int height)
    {
        if (Failed || _disposed) return false;
        lock (_lock)
        {
            try
            {
                width &= ~1; height &= ~1;
                if (_upload is null || _upload.Description.Width != width || _upload.Description.Height != height)
                {
                    _upload?.Dispose();
                    _upload = _gpu.Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                        Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Write,
                    });
                }
                var map = _gpu.Context.Map(_upload, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    // NV12 mapeado: plano Y (altura linhas) e logo depois o plano UV (altura/2 linhas), mesmo passo.
                    byte* dst = (byte*)map.DataPointer;
                    long pitch = map.RowPitch;
                    for (int r = 0; r < height; r++)
                        Buffer.MemoryCopy(y + (long)r * yStride, dst + r * pitch, width, width);
                    byte* dstUv = dst + pitch * height;
                    for (int r = 0; r < height / 2; r++)
                        Buffer.MemoryCopy(uv + (long)r * uvStride, dstUv + r * pitch, width, width);
                }
                finally { _gpu.Context.Unmap(_upload, 0); }
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
            return PresentNv12(_upload, 0, width, height);
        }
    }

    /// <summary>Mostra a imagem que está na memória do processador (blocos JPEG, decodificação por software, HDMI).</summary>
    public bool PresentSurface(FrameSurface surface)
    {
        if (Failed || _disposed) return false;
        lock (_lock)
        {
            try
            {
                bool ok = surface.Read((bits, w, h) =>
                {
                    EnsureProcessor(w, h);
                    if (_bgra is null || _bgra.Description.Width != w || _bgra.Description.Height != h)
                    {
                        _bgraView?.Dispose(); _bgraView = null;
                        _bgra?.Dispose();
                        _bgra = _gpu.Device.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                        });
                    }
                    _gpu.Context.UpdateSubresource(_bgra, 0, null, bits, (uint)(w * 4), (uint)(w * 4 * h));
                    _bgraView ??= MakeInputView(_bgra);
                    _source = Source.Bgra;
                    _srcW = w;
                    _srcH = h;
                });
                if (!ok) return false;
                Render();
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        }
    }

    /// <summary>Redesenha a última imagem (textos mudaram, janela redimensionada).</summary>
    public void Redraw()
    {
        if (Failed || _disposed) return;
        lock (_lock)
        {
            try { Render(); }
            catch (Exception ex) { Fail(ex); }
        }
    }

    private void Render()
    {
        if (_disposed) return;
        if (_wantW != _width || _wantH != _height)
        {
            ReleaseTargets();
            _swap.ResizeBuffers(0, (uint)_wantW, (uint)_wantH, Format.Unknown, SwapChainFlags.None).CheckError();
            _width = _wantW;
            _height = _wantH;
            CreateTargets();
        }

        var view = _source switch { Source.Nv12 => _nv12View, Source.Bgra => _bgraView, _ => null };
        var client = new Rectangle(0, 0, _width, _height);
        if (view is not null && _vp is not null)
        {
            var dest = FrameSurface.Fit(client, _srcW, _srcH);
            Dest = dest;
            if (_outView is null)
            {
                var od = new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                };
                _gpu.VideoDevice.CreateVideoProcessorOutputView(_back!, _vpEnum!, od, out _outView).CheckError();
            }
            var vc = _gpu.VideoContext;
            vc.VideoProcessorSetOutputTargetRect(_vp, true, new Vortice.RawRect(0, 0, _width, _height));
            vc.VideoProcessorSetStreamSourceRect(_vp, 0, true, new Vortice.RawRect(0, 0, _srcW, _srcH));
            vc.VideoProcessorSetStreamDestRect(_vp, 0, true, new Vortice.RawRect(dest.Left, dest.Top, dest.Right, dest.Bottom));
            // Espaço de cor da entrada: H.264 do TransLocal é BT.601 em faixa de vídeo (16–235); BGRA é RGB completo.
            vc.VideoProcessorSetStreamColorSpace(_vp, 0, _source == Source.Nv12 ? ColorSpaceVideo601 : ColorSpaceFullRgb);
            var stream = new VideoProcessorStream { Enable = true, InputSurface = view };
            vc.VideoProcessorBlt(_vp, _outView!, 0, 1, new[] { stream }).CheckError();
        }
        else
        {
            Dest = Rectangle.Empty;
        }

        var text = Overlay?.Invoke() ?? default;
        bool drawText = view is null || text.Stats is { Length: > 0 } || text.Notice is { Length: > 0 };
        if (drawText && _rt is not null)
        {
            _rt.BeginDraw();
            if (view is null) _rt.Clear(new Color4(0f, 0f, 0f, 1f));
            if (_font is not null)
            {
                if (view is null && text.Waiting is { Length: > 0 } waiting)
                    DrawCentered(waiting, client, _gray!);
                if (text.Stats is { Length: > 0 } stats) DrawStats(stats);
                if (text.Notice is { Length: > 0 } notice) DrawNotice(notice);
            }
            _rt.EndDraw();
        }

        // Intervalo 0 no modelo flip em janela: a imagem nova substitui a que estava na fila e aparece na próxima
        // composição da tela, sem bloquear esta thread e sem rasgos.
        BeforePresent?.Invoke(_back!);
        var hr = _swap.Present(0, PresentFlags.None);
        if (hr.Failure) throw new SharpGen.Runtime.SharpGenException(hr);
        Interlocked.Increment(ref Presented);
    }

    private void DrawCentered(string s, Rectangle area, ID2D1Brush brush)
    {
        using var layout = _dwrite.CreateTextLayout(s, _font!, area.Width, area.Height);
        layout.TextAlignment = TextAlignment.Center;
        layout.ParagraphAlignment = ParagraphAlignment.Center;
        _rt!.DrawTextLayout(new Vector2(area.X, area.Y), layout, brush);
    }

    private void DrawStats(string s)
    {
        using var layout = _dwrite.CreateTextLayout(s, _font!, Math.Max(100, _width - 32), 400);
        var m = layout.Metrics;
        float pad = _fontPx * 0.6f;
        float h = m.Height + pad, w = m.Width + pad * 2;
        float x = 8, y = _height - h - 16;
        _rt!.FillRectangle(new Rect(x, y, w, h), _statsBg!);
        _rt.DrawTextLayout(new Vector2(x + pad, y + pad / 2), layout, _white!);
    }

    private void DrawNotice(string s)
    {
        float maxW = Math.Max(200, _width - 80);
        using var layout = _dwrite.CreateTextLayout(s, _font!, maxW, 600);
        layout.TextAlignment = TextAlignment.Center;
        layout.WordWrapping = WordWrapping.Wrap;
        var m = layout.Metrics;
        float w = m.Width + 24, h = m.Height + 14;
        float x = (_width - w) / 2, y = 12;
        _rt!.FillRectangle(new Rect(x, y, w, h), _noticeBg!);
        // O layout centraliza dentro de maxW: desloca para a caixa.
        _rt.DrawTextLayout(new Vector2(x + 12 - m.Left, y + 7), layout, _white!);
    }

    private void Fail(Exception ex)
    {
        if (Failed) return;
        Failed = true;
        Log.Warn("Exibição pela placa de vídeo falhou; voltando ao desenho pelo processador", ex);
        try { Lost?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseTargets();
            _font?.Dispose();
            _nv12View?.Dispose(); _bgraView?.Dispose();
            _nv12?.Dispose(); _bgra?.Dispose(); _upload?.Dispose();
            _vp?.Dispose(); _vpEnum?.Dispose();
            _swap.Dispose();
            _dwrite.Dispose();
            _d2d.Dispose();
            _gpu.Dispose();
        }
    }
}
