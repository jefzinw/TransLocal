using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TransLocal.Video;

/// <summary>
/// Captura por DXGI Desktop Duplication (a mesma API usada pela gravação de tela do Windows):
/// a GPU entrega o quadro já composto, em poucos milissegundos.
/// Usa DuplicateOutput1 (Windows 10 1703+), que funciona com jogos em tela cheia que apresentam em
/// 10 bits ou HDR — a versão antiga perde o acesso sem parar nesses casos.
/// </summary>
internal sealed unsafe class DxgiCapturer : ScreenCapturer
{
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

    // Ordem de preferência; o Windows converte o que o jogo apresenta para um destes.
    private static readonly Format[][] FormatLists =
    {
        new[] { Format.B8G8R8A8_UNorm, Format.R10G10B10A2_UNorm, Format.R16G16B16A16_Float },
        new[] { Format.B8G8R8A8_UNorm, Format.R16G16B16A16_Float },
        new[] { Format.B8G8R8A8_UNorm },
    };

    /// <summary>
    /// IDXGIOutput5::DuplicateOutput1 chamado direto pela vtable (posição 26). A ligação gerada pelo
    /// Vortice 3.8.3 omite o ponteiro de saída e corrompe memória (E_INVALIDARG ou falha de acesso).
    /// </summary>
    private static int DuplicateOutput1(IDXGIOutput5 output, ID3D11Device device, Format[] formats, out IDXGIOutputDuplication? dup)
    {
        dup = null;
        IntPtr result = IntPtr.Zero;
        int hr;
        fixed (Format* f = formats)
        {
            var vtbl = *(void***)output.NativePointer;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, Format*, IntPtr*, int>)vtbl[26];
            hr = fn(output.NativePointer, device.NativePointer, 0, (uint)formats.Length, f, &result);
        }
        if (hr >= 0 && result != IntPtr.Zero) dup = new IDXGIOutputDuplication(result);
        return hr;
    }

    private readonly string _deviceName;
    private readonly byte[] _clean;
    private readonly byte* _cleanPtr;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput1? _output;
    private IDXGIOutput5? _output5;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _staging;
    private Format _stagingFormat;
    private Format _lastFormat;
    private bool _hasFrame;
    private bool _cleanChanged;
    private long _nextRetry;
    private int _failures;
    private long _openedAt;
    private int _quickLosses;
    private bool _gdiActive;
    private bool _gdiBlackLogged;
    private bool _legacyLogged;
    private string _api = "";

    public override string Method => "DXGI";

    /// <summary>Estado para diagnóstico: API, formato, ou o que está usando enquanto o DXGI não volta.</summary>
    public string State => _dup is not null
        ? $"DXGI {_api}{(_lastFormat == Format.Unknown ? "" : " " + _lastFormat)}"
        : _gdiActive ? $"DXGI perdido ({_failures} falhas), usando GDI"
        : Blocked ? $"DXGI perdido ({_failures} falhas), bloqueada (só preto)"
        : $"DXGI perdido ({_failures} falhas), última imagem";

    /// <summary>
    /// O DXGI não volta e a captura GDI só devolve preto: o programa em tela cheia não deixa capturar
    /// (tela cheia exclusiva). A transmissão mostra a última imagem.
    /// </summary>
    public bool Blocked { get; private set; }

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
                    _output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
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
        // Em tela cheia exclusiva o GDI devolve preto: melhor manter a última imagem boa.
        if (_hasFrame && LooksBlack((byte*)Bits)) return;
        long bytes = (long)Width * Height * 4;
        Buffer.MemoryCopy((void*)Bits, _cleanPtr, bytes, bytes);
        _hasFrame = true;
        _cleanChanged = true;
    }

    private void CreateDuplication()
    {
        _dup?.Dispose();
        _dup = null;
        if (_output5 is not null)
        {
            // Algumas listas de formatos são recusadas por drivers/versões do Windows: tenta da mais completa à mínima.
            foreach (var formats in FormatLists)
            {
                int hr = DuplicateOutput1(_output5, _device!, formats, out _dup);
                if (hr >= 0 && _dup is not null)
                {
                    _api = formats.Length == FormatLists[0].Length ? "DuplicateOutput1" : $"DuplicateOutput1 ({formats.Length} formatos)";
                    break;
                }
                _dup = null;
                if (!_legacyLogged) Log.Info($"DuplicateOutput1 com {string.Join(", ", formats)} recusado (0x{hr:X8})");
            }
            if (_dup is null) _legacyLogged = true;
        }
        if (_dup is null)
        {
            _dup = _output!.DuplicateOutput(_device!);
            _api = "DuplicateOutput";
        }
        var d = _dup.Description;
        if (d.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            throw new NotSupportedException("Monitor girado (retrato) — usando captura GDI.");
        if (d.ModeDescription.Width != (uint)Width || d.ModeDescription.Height != (uint)Height)
            throw new NotSupportedException($"Tamanho DXGI {d.ModeDescription.Width}×{d.ModeDescription.Height} diferente do monitor {Width}×{Height}");
        _openedAt = Environment.TickCount64;
    }

    private void EnsureStaging(Format format)
    {
        if (_staging is not null && _stagingFormat == format) return;
        _staging?.Dispose();
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _stagingFormat = format;
        if (format != Format.B8G8R8A8_UNorm) Log.Info($"Captura DXGI recebendo {format} (convertido para a transmissão)");
    }

    protected override bool AcquireDesktop(out bool changed)
    {
        if (_dup is null)
        {
            // A duplicação se perde quando um jogo assume a tela, em mudanças de modo, UAC, tela de bloqueio…
            // Recria com espera crescente: recriar a cada quadro esgota a memória de vídeo e trava o PC.
            if (Environment.TickCount64 >= _nextRetry)
            {
                try
                {
                    if (_device is null)
                    {
                        Open(); // já cria a duplicação e captura a imagem inicial
                    }
                    else
                    {
                        CreateDuplication();
                        SeedFromGdi();
                    }
                }
                catch (Exception ex)
                {
                    _dup?.Dispose();
                    _dup = null;
                    RegisterFailure($"recriação falhou: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (_dup is null)
            {
                // Enquanto o DXGI não volta (jogo em tela cheia), tenta a captura GDI para a imagem não congelar.
                if (_quickLosses >= 2 || _failures >= 4) GdiFallback();
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
                    var format = texture.Description.Format;
                    EnsureStaging(format);
                    _lastFormat = format;
                    _context!.CopyResource(_staging!, texture);
                    var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        CopyConverted((byte*)map.DataPointer, (int)map.RowPitch, format);
                    }
                    finally
                    {
                        _context.Unmap(_staging!, 0);
                    }
                    _hasFrame = true;
                    _cleanChanged = true;
                }
                if (_failures > 0 && Environment.TickCount64 - _openedAt > 2000)
                {
                    Log.Info($"Captura DXGI estável de novo após {_failures} falha(s) ({_api}{(_lastFormat == Format.Unknown ? "" : ", " + _lastFormat)})");
                    _failures = 0;
                    _quickLosses = 0;
                    _gdiActive = false;
                    _gdiBlackLogged = false;
                    Blocked = false;
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
            // DXGI_ERROR_ACCESS_LOST (jogo em tela cheia, troca de modo), DXGI_ERROR_INVALID_CALL…
            _dup.Dispose();
            _dup = null;
            bool quick = Environment.TickCount64 - _openedAt < 2000;
            _quickLosses = quick ? _quickLosses + 1 : 0;
            RegisterFailure($"interrompida (0x{hr.Code:X8}{(quick ? ", logo após recriar" : "")})");
        }
        changed = TakeCleanChanged();
        return _hasFrame;
    }

    /// <summary>Copia o quadro mapeado para a imagem limpa em BGRA 8 bits, convertendo se preciso.</summary>
    private void CopyConverted(byte* src, int rowPitch, Format format)
    {
        int rowBytes = Width * 4;
        switch (format)
        {
            case Format.B8G8R8A8_UNorm:
            case Format.B8G8R8X8_UNorm:
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy(src + (long)y * rowPitch, _cleanPtr + (long)y * rowBytes, rowBytes, rowBytes);
                break;
            default:
                Convert(src, rowPitch, format, _cleanPtr, Width, Height);
                break;
        }
    }

    /// <summary>Converte um quadro de outro formato DXGI para BGRA 8 bits (público para o autoteste).</summary>
    internal static void Convert(byte* src, int rowPitch, Format format, byte* dst, int width, int height)
    {
        int rowBytes = width * 4;
        switch (format)
        {
            case Format.R8G8B8A8_UNorm:
                for (int y = 0; y < height; y++)
                {
                    uint* s = (uint*)(src + (long)y * rowPitch);
                    uint* d = (uint*)(dst + (long)y * rowBytes);
                    for (int x = 0; x < width; x++)
                    {
                        uint v = s[x];
                        d[x] = (v & 0xFF00FF00) | ((v & 0xFF) << 16) | ((v >> 16) & 0xFF);
                    }
                }
                break;
            case Format.R10G10B10A2_UNorm:
                for (int y = 0; y < height; y++)
                {
                    uint* s = (uint*)(src + (long)y * rowPitch);
                    uint* d = (uint*)(dst + (long)y * rowBytes);
                    for (int x = 0; x < width; x++)
                    {
                        uint v = s[x];
                        uint r = (v & 0x3FF) >> 2, g = ((v >> 10) & 0x3FF) >> 2, b = ((v >> 20) & 0x3FF) >> 2;
                        d[x] = 0xFF000000 | (r << 16) | (g << 8) | b;
                    }
                }
                break;
            case Format.R16G16B16A16_Float:
                var lut = HalfToSrgb.Table;
                for (int y = 0; y < height; y++)
                {
                    ushort* s = (ushort*)(src + (long)y * rowPitch);
                    uint* d = (uint*)(dst + (long)y * rowBytes);
                    for (int x = 0; x < width; x++, s += 4)
                        d[x] = 0xFF000000 | ((uint)lut[s[0]] << 16) | ((uint)lut[s[1]] << 8) | lut[s[2]];
                }
                break;
            default:
                throw new NotSupportedException($"Formato DXGI {format} não suportado");
        }
    }

    /// <summary>Captura GDI enquanto o DXGI está perdido; ignora quadros pretos (tela cheia exclusiva).</summary>
    private void GdiFallback()
    {
        if (!Native.BitBlt(MemDc, 0, 0, Width, Height, ScreenDc, Bounds.X, Bounds.Y, Native.SRCCOPY)) return;
        Native.GdiFlush();
        if (LooksBlack((byte*)Bits))
        {
            if (!_gdiBlackLogged)
            {
                _gdiBlackLogged = true;
                Log.Warn("Captura bloqueada: o DXGI não volta e a captura GDI só devolve preto (programa em tela cheia exclusiva). Mantendo a última imagem.");
            }
            Blocked = true;
            return;
        }
        Blocked = false;
        if (!_gdiActive)
        {
            _gdiActive = true;
            Log.Info("Captura DXGI indisponível: usando GDI até ela voltar");
        }
        long bytes = (long)Width * Height * 4;
        Buffer.MemoryCopy((void*)Bits, _cleanPtr, bytes, bytes);
        _hasFrame = true;
        _cleanChanged = true;
    }

    private bool LooksBlack(byte* bgra)
    {
        int stride = Width * 4;
        for (int gy = 1; gy < 16; gy++)
        {
            int y = Height * gy / 16;
            for (int gx = 1; gx < 16; gx++)
            {
                int x = Width * gx / 16;
                byte* p = bgra + (long)y * stride + x * 4;
                if (p[0] > 8 || p[1] > 8 || p[2] > 8) return false;
            }
        }
        return true;
    }

    /// <summary>Agenda a próxima tentativa com espera crescente e, após várias falhas, recria o dispositivo.</summary>
    private void RegisterFailure(string what)
    {
        _failures++;
        int delay = Math.Min(3000, 100 << Math.Min(_failures, 5)); // 200, 400, 800, 1600, 3000 ms…
        _nextRetry = Environment.TickCount64 + delay;
        if (_failures == 1 || _failures % 20 == 0)
            Log.Info($"Captura DXGI {what}; nova tentativa em {delay} ms (falha nº {_failures}, {_api}).");
        if (_failures % 6 == 0)
        {
            // Dispositivo possivelmente perdido (reinício do driver, troca de GPU): recomeça do zero.
            ReleaseDevice();
        }
    }

    private void ReleaseDevice()
    {
        _dup?.Dispose();
        _dup = null;
        _staging?.Dispose();
        _staging = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _output5?.Dispose();
        _output5 = null;
        _output?.Dispose();
        _output = null;
        _adapter?.Dispose();
        _adapter = null;
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

    protected override void DisposeCore() => ReleaseDevice();
}

/// <summary>Meio-float linear (scRGB, área de trabalho HDR) → 8 bits sRGB, por tabela (65 536 entradas).</summary>
internal static class HalfToSrgb
{
    private static byte[]? _table;

    public static byte[] Table => _table ??= Build();

    private static byte[] Build()
    {
        var t = new byte[65536];
        for (int i = 0; i < 65536; i++)
        {
            float v = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            if (float.IsNaN(v) || v <= 0) { t[i] = 0; continue; }
            if (v >= 1) { t[i] = 255; continue; }
            double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            t[i] = (byte)Math.Clamp((int)Math.Round(s * 255), 0, 255);
        }
        return t;
    }
}
