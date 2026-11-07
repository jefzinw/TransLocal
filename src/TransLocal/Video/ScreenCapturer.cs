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
    public bool Capture(bool drawCursor) => Capture(drawCursor, 0);

    /// <summary>
    /// Como <see cref="Capture(bool)"/>, mas espera até <paramref name="waitMs"/> por um quadro novo da tela (DXGI):
    /// a captura acompanha o ritmo do jogo em vez de um relógio próprio — menos atraso e sem trancos.
    /// </summary>
    public bool Capture(bool drawCursor, int waitMs)
    {
        WaitMs = Math.Max(0, waitMs);
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

    /// <summary>Espera máxima por um quadro novo na captura atual (ms).</summary>
    protected int WaitMs { get; private set; }

    /// <summary>A captura espera o próximo quadro da tela sozinha (não precisa de pausa entre capturas).</summary>
    public virtual bool WaitsForFrames => false;

    /// <summary>A última chamada a Capture produziu uma imagem diferente da anterior.</summary>
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
