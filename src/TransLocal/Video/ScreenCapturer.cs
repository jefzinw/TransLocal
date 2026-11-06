using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TransLocal.Video;

/// <summary>
/// Base dos capturadores: mantém o quadro numa DIB 32 bpp (top-down) e desenha o cursor do mouse por cima.
/// Deve ser criado, usado e descartado na mesma thread.
/// </summary>
internal abstract class ScreenCapturer : IDisposable
{
    private readonly IntPtr _screenDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _oldBitmap;
    private readonly Dictionary<IntPtr, (int X, int Y)> _hotspots = new();
    private bool _disposed;

    protected readonly IntPtr MemDc;
    protected IntPtr ScreenDc => _screenDc;

    /// <summary>DC de memória com a imagem capturada (para redimensionar com StretchBlt).</summary>
    public IntPtr DeviceContext => MemDc;

    public Rectangle Bounds { get; }
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
    public int Stride => Bounds.Width * 4;
    public IntPtr Bits { get; }
    public abstract string Method { get; }

    protected ScreenCapturer(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentException("Monitor inválido", nameof(bounds));
        Bounds = bounds;
        _screenDc = Native.GetDC(IntPtr.Zero);
        MemDc = Native.CreateCompatibleDC(_screenDc);
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = bounds.Width,
                biHeight = -bounds.Height,
                biPlanes = 1,
                biBitCount = 32,
            }
        };
        _dib = Native.CreateDIBSection(_screenDc, ref bmi, Native.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(MemDc);
            Native.ReleaseDC(IntPtr.Zero, _screenDc);
            throw new InvalidOperationException("Não foi possível alocar o buffer de captura.");
        }
        Bits = bits;
        _oldBitmap = Native.SelectObject(MemDc, _dib);
    }

    /// <summary>Atualiza <see cref="Bits"/> com a imagem atual do monitor. Retorna false se não há
    /// imagem disponível agora (ex.: tela de bloqueio ou UAC na área de trabalho segura).</summary>
    public bool Capture(bool drawCursor)
    {
        if (!AcquireDesktop(out bool desktopChanged)) return false;
        var cursor = drawCursor ? ReadCursor() : default;
        if (!desktopChanged && _composed && cursor == _lastCursor)
        {
            // Nada mudou: Bits continua com a imagem anterior (com o cursor no mesmo lugar).
            LastCaptureChanged = false;
            return true;
        }
        ComposeDesktop();
        if (cursor.Handle != IntPtr.Zero) DrawCursor(cursor);
        _lastCursor = cursor;
        _composed = true;
        LastCaptureChanged = true;
        Native.GdiFlush();
        return true;
    }

    /// <summary>A última chamada a <see cref="Capture"/> produziu uma imagem diferente da anterior.</summary>
    public bool LastCaptureChanged { get; private set; }

    private bool _composed;
    private CursorState _lastCursor;

    private readonly record struct CursorState(IntPtr Handle, int X, int Y);

    /// <summary>Obtém a imagem da área de trabalho. <paramref name="changed"/> indica se ela mudou.</summary>
    protected abstract bool AcquireDesktop(out bool changed);

    /// <summary>Coloca a imagem da área de trabalho (sem cursor) em <see cref="Bits"/>.</summary>
    protected virtual void ComposeDesktop() { }

    private static CursorState ReadCursor()
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero)
            return default;
        return new CursorState(ci.hCursor, ci.ptScreenPos.X, ci.ptScreenPos.Y);
    }

    private void DrawCursor(CursorState ci)
    {
        if (!_hotspots.TryGetValue(ci.Handle, out var hs))
        {
            hs = (0, 0);
            if (Native.GetIconInfo(ci.Handle, out var ii))
            {
                hs = (ii.xHotspot, ii.yHotspot);
                if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            }
            if (_hotspots.Count > 128) _hotspots.Clear();
            _hotspots[ci.Handle] = hs;
        }

        int x = ci.X - Bounds.X - hs.X;
        int y = ci.Y - Bounds.Y - hs.Y;
        if (x >= Width || y >= Height || x < -256 || y < -256) return;
        Native.DrawIconEx(MemDc, x, y, ci.Handle, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
    }

    protected virtual void DisposeCore() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCore();
        Native.SelectObject(MemDc, _oldBitmap);
        Native.DeleteObject(_dib);
        Native.DeleteDC(MemDc);
        Native.ReleaseDC(IntPtr.Zero, _screenDc);
    }

    /// <summary>Cria o melhor capturador disponível: DXGI Desktop Duplication, ou GDI como reserva.</summary>
    public static ScreenCapturer Create(Screen screen)
    {
        try
        {
            return new DxgiCapturer(screen);
        }
        catch (Exception ex)
        {
            Log.Warn($"Captura DXGI indisponível para {screen.DeviceName}; usando GDI", ex);
            return new GdiCapturer(screen.Bounds);
        }
    }
}

/// <summary>Captura por GDI (BitBlt da área de trabalho composta). Compatível com tudo, porém mais lenta.</summary>
internal sealed class GdiCapturer : ScreenCapturer
{
    public GdiCapturer(Rectangle bounds) : base(bounds) { }

    public override string Method => "GDI";

    // O BitBlt reescreve a imagem inteira (apagando o cursor desenhado antes): sempre "mudou".
    protected override bool AcquireDesktop(out bool changed)
    {
        changed = true;
        return Native.BitBlt(MemDc, 0, 0, Width, Height, ScreenDc, Bounds.X, Bounds.Y, Native.SRCCOPY);
    }
}

/// <summary>
/// Captura por DXGI Desktop Duplication (a mesma API usada pela gravação de tela do Windows):
/// a GPU entrega o quadro já composto, em poucos milissegundos.
/// </summary>
internal sealed unsafe class DxgiCapturer : ScreenCapturer
{
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

    private readonly string _deviceName;
    private readonly byte[] _clean;
    private readonly byte* _cleanPtr;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput1? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _staging;
    private bool _hasFrame;
    private bool _cleanChanged;
    private long _nextRetry;

    public override string Method => "DXGI";

    public DxgiCapturer(Screen screen) : base(screen.Bounds)
    {
        _deviceName = screen.DeviceName;
        _clean = GC.AllocateUninitializedArray<byte>(Width * Height * 4, pinned: true);
        _cleanPtr = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_clean, 0);
        try
        {
            Open();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Open()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var desc = output.Description;
                if (string.Equals(desc.DeviceName, _deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    _adapter = adapter;
                    _output = output.QueryInterface<IDXGIOutput1>();
                    output.Dispose();
                    break;
                }
                output.Dispose();
            }
            if (_adapter is not null) break;
            adapter.Dispose();
        }
        if (_adapter is null || _output is null) throw new NotSupportedException($"Saída DXGI não encontrada para {_deviceName}");

        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11.D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out _device, out _context).CheckError();

        CreateDuplication();
        SeedFromGdi();
    }

    /// <summary>
    /// Logo após criar a duplicação, o DXGI entrega quadros pretos até a tela mudar. Uma captura GDI
    /// única fornece a imagem inicial correta; as atualizações seguintes vêm do DXGI.
    /// </summary>
    private void SeedFromGdi()
    {
        if (!Native.BitBlt(MemDc, 0, 0, Width, Height, ScreenDc, Bounds.X, Bounds.Y, Native.SRCCOPY))
        {
            _hasFrame = false;
            return;
        }
        Native.GdiFlush();
        long bytes = (long)Width * Height * 4;
        Buffer.MemoryCopy((void*)Bits, _cleanPtr, bytes, bytes);
        _hasFrame = true;
        _cleanChanged = true;
    }

    private void CreateDuplication()
    {
        _dup?.Dispose();
        _dup = null;
        _dup = _output!.DuplicateOutput(_device!);
        var d = _dup.Description;
        if (d.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            throw new NotSupportedException("Monitor girado (retrato) — usando captura GDI.");
        if (d.ModeDescription.Width != (uint)Width || d.ModeDescription.Height != (uint)Height)
            throw new NotSupportedException($"Tamanho DXGI {d.ModeDescription.Width}×{d.ModeDescription.Height} diferente do monitor {Width}×{Height}");
        if (d.ModeDescription.Format != Format.B8G8R8A8_UNorm)
            throw new NotSupportedException($"Formato DXGI {d.ModeDescription.Format} não suportado (HDR?)");

        if (_staging is null)
        {
            _staging = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }
    }

    protected override bool AcquireDesktop(out bool changed)
    {
        if (_dup is null)
        {
            // A duplicação se perde em mudanças de modo, UAC, tela de bloqueio… tenta recriar de tempos em tempos.
            if (Environment.TickCount64 >= _nextRetry)
            {
                try
                {
                    CreateDuplication();
                    SeedFromGdi();
                }
                catch (Exception ex)
                {
                    _dup?.Dispose();
                    _dup = null;
                    _nextRetry = Environment.TickCount64 + 500;
                    Log.Warn("Recriando a captura DXGI", ex);
                }
            }
            if (_dup is null)
            {
                changed = TakeCleanChanged();
                return _hasFrame;
            }
        }

        var hr = _dup!.AcquireNextFrame(0, out var info, out var resource);
        if (hr.Success)
        {
            try
            {
                // LastPresentTime == 0: nada novo foi apresentado (só o ponteiro mudou, ou é o quadro
                // inicial ainda vazio); a imagem anterior continua valendo.
                if (info.LastPresentTime != 0)
                {
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    _context!.CopyResource(_staging!, texture);
                    var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        byte* src = (byte*)map.DataPointer;
                        int rowBytes = Width * 4;
                        for (int y = 0; y < Height; y++)
                            Buffer.MemoryCopy(src + (long)y * map.RowPitch, _cleanPtr + (long)y * rowBytes, rowBytes, rowBytes);
                    }
                    finally
                    {
                        _context.Unmap(_staging!, 0);
                    }
                    _hasFrame = true;
                    _cleanChanged = true;
                }
            }
            finally
            {
                resource.Dispose();
                _dup.ReleaseFrame();
            }
        }
        else if (hr.Code != DXGI_ERROR_WAIT_TIMEOUT)
        {
            // DXGI_ERROR_ACCESS_LOST e similares
            Log.Info($"Captura DXGI interrompida (0x{hr.Code:X8}); será recriada");
            _dup.Dispose();
            _dup = null;
            _nextRetry = 0;
        }
        changed = TakeCleanChanged();
        return _hasFrame;
    }

    private bool TakeCleanChanged()
    {
        bool c = _cleanChanged;
        _cleanChanged = false;
        return c;
    }

    /// <summary>Copia a última imagem limpa (sem cursor) para a DIB de saída.</summary>
    protected override void ComposeDesktop()
    {
        long bytes = (long)Width * Height * 4;
        Buffer.MemoryCopy(_cleanPtr, (void*)Bits, bytes, bytes);
    }

    protected override void DisposeCore()
    {
        _staging?.Dispose();
        _dup?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _output?.Dispose();
        _adapter?.Dispose();
    }
}

internal static class Monitors
{
    public static Screen Resolve(string? deviceName)
    {
        var all = Screen.AllScreens;
        return all.FirstOrDefault(s => s.DeviceName == deviceName)
               ?? Screen.PrimaryScreen
               ?? all[0];
    }

    public static string Describe(Screen s, int index) =>
        $"Monitor {index + 1}{(s.Primary ? " (principal)" : "")} — {s.Bounds.Width}×{s.Bounds.Height}";
}
