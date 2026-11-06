using System.Diagnostics;
using FlashCap;
using TransLocal.Audio;

namespace TransLocal.Video;

internal sealed class HdmiDeviceItem
{
    public HdmiDeviceItem(CaptureDeviceDescriptor descriptor) => Descriptor = descriptor;
    public CaptureDeviceDescriptor Descriptor { get; }
    public string Key => $"{Descriptor.DeviceType}|{Descriptor.Name}";
    public override string ToString() => Descriptor.Name;
}

internal sealed class HdmiFormatItem
{
    public HdmiFormatItem(VideoCharacteristics c) => Characteristics = c;
    public VideoCharacteristics Characteristics { get; }
    public double Fps => Characteristics.FramesPerSecond.Denominator == 0
        ? 0
        : Characteristics.FramesPerSecond.Numerator / (double)Characteristics.FramesPerSecond.Denominator;
    public string Key => $"{Characteristics.Width}x{Characteristics.Height}@{Fps:0.##}:{Characteristics.PixelFormat}";
    public override string ToString()
    {
        string fmt = Characteristics.PixelFormat switch
        {
            PixelFormats.JPEG => "MJPEG",
            PixelFormats.YUYV => "YUY2 (sem compressão)",
            PixelFormats.UYVY => "UYVY (sem compressão)",
            PixelFormats.NV12 => "NV12 (sem compressão)",
            var p => p.ToString(),
        };
        return $"{Characteristics.Width}×{Characteristics.Height} @ {Fps:0.##} fps — {fmt}";
    }
}

/// <summary>Exibe a entrada de uma placa de captura HDMI (dispositivo de vídeo UVC/DirectShow).</summary>
internal sealed class HdmiCaptureSession : IAsyncDisposable
{
    private CaptureDevice? _device;
    private AudioPassthrough? _audio;
    private int _statFrames;
    private long _statLastTs = Stopwatch.GetTimestamp();
    private System.Threading.Timer? _statsTimer;
    private volatile bool _stopping;

    public FrameSurface Surface { get; } = new();
    public string DeviceName { get; private set; } = "";
    public event Action? FrameReady;
    public event Action<string>? StatsUpdated;

    public static List<HdmiDeviceItem> ListDevices()
    {
        var result = new List<HdmiDeviceItem>();
        try
        {
            var all = new CaptureDevices().EnumerateDescriptors()
                .Where(d => d.Characteristics.Any(c => c.PixelFormat != PixelFormats.Unknown))
                .ToList();
            // O mesmo aparelho pode aparecer via DirectShow e Video for Windows: prefere DirectShow.
            foreach (var d in all.OrderBy(d => d.DeviceType == DeviceTypes.DirectShow ? 0 : 1))
            {
                if (result.Any(r => r.Descriptor.Name == d.Name)) continue;
                result.Add(new HdmiDeviceItem(d));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Falha ao listar dispositivos de captura", ex);
        }
        return result;
    }

    public static List<HdmiFormatItem> ListFormats(HdmiDeviceItem device) =>
        device.Descriptor.Characteristics
            .Where(c => c.PixelFormat != PixelFormats.Unknown)
            .Select(c => new HdmiFormatItem(c))
            .GroupBy(f => f.Key).Select(g => g.First())
            .OrderByDescending(f => f.Characteristics.Width * f.Characteristics.Height)
            .ThenByDescending(f => f.Fps)
            .ThenBy(f => f.Characteristics.PixelFormat == PixelFormats.JPEG ? 1 : 0)
            .ToList();

    /// <summary>Melhor formato padrão: até 1920×1080, maior taxa de quadros, sem compressão no empate.</summary>
    public static HdmiFormatItem? PickBest(IReadOnlyList<HdmiFormatItem> formats)
    {
        var upTo1080 = formats.Where(f => f.Characteristics.Width * f.Characteristics.Height <= 1920 * 1080).ToList();
        var pool = upTo1080.Count > 0 ? upTo1080 : formats.ToList();
        return pool
            .OrderByDescending(f => f.Characteristics.Width * f.Characteristics.Height)
            .ThenByDescending(f => Math.Round(Math.Min(f.Fps, 60)))
            .ThenBy(f => f.Characteristics.PixelFormat == PixelFormats.JPEG ? 1 : 0)
            .FirstOrDefault();
    }

    /// <summary>Tenta adivinhar a entrada de áudio que pertence à placa de captura.</summary>
    public static string? GuessAudioInput(IEnumerable<(string Id, string Name)> inputs, string? videoDeviceName)
    {
        string[] hints = { "usb video", "capture", "captura", "hdmi", "digital audio interface", "ms2109", "ms2130", "cam link", "elgato", "avermedia", "ugreen", "video" };
        var list = inputs.ToList();
        if (!string.IsNullOrEmpty(videoDeviceName))
        {
            var byName = list.FirstOrDefault(i => i.Name.Contains(videoDeviceName, StringComparison.OrdinalIgnoreCase));
            if (byName.Id is not null) return byName.Id;
        }
        foreach (var h in hints)
        {
            var hit = list.FirstOrDefault(i => i.Name.Contains(h, StringComparison.OrdinalIgnoreCase));
            if (hit.Id is not null) return hit.Id;
        }
        return null;
    }

    public async Task StartAsync(HdmiDeviceItem device, HdmiFormatItem format, string? audioInputId)
    {
        DeviceName = device.Descriptor.Name;
        Log.Info($"Abrindo captura HDMI: {device.Descriptor.Name} ({device.Descriptor.DeviceType}) {format}");
        _device = await device.Descriptor.OpenAsync(
            format.Characteristics,
            TranscodeFormats.Auto,
            isScattering: false,
            maxQueuingFrames: 1,
            OnPixelBuffer);
        await _device.StartAsync();
        if (!string.IsNullOrEmpty(audioInputId)) _audio = AudioPassthrough.TryOpen(audioInputId);
        _statsTimer = new System.Threading.Timer(_ => ReportStats(), null, 1000, 1000);
    }

    private void OnPixelBuffer(PixelBufferScope scope)
    {
        if (_stopping) return;
        try
        {
            var seg = scope.Buffer.ReferImage();
            if (seg.Array is null || seg.Count == 0) return;
            if (!Surface.TryCopyFromBmpFile(seg.AsSpan()))
            {
                using var ms = new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false);
                using var img = (Bitmap)Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
                Surface.EnsureSize(img.Width, img.Height);
                Surface.CopyFromBitmap(img, 0, 0);
                Surface.MarkUpdated();
            }
            Interlocked.Increment(ref _statFrames);
            FrameReady?.Invoke();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Warn("Quadro da placa de captura ignorado", ex);
        }
    }

    private void ReportStats()
    {
        long now = Stopwatch.GetTimestamp();
        double secs = Math.Max(0.001, (now - Interlocked.Exchange(ref _statLastTs, now)) / (double)Stopwatch.Frequency);
        int frames = Interlocked.Exchange(ref _statFrames, 0);
        StatsUpdated?.Invoke($"HDMI • {DeviceName} • {Surface.Width}×{Surface.Height} • {frames / secs:0} quadros/s");
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _statsTimer?.Dispose();
        _audio?.Dispose();
        if (_device is not null)
        {
            try { await _device.StopAsync(); } catch (Exception ex) { Log.Warn("Falha ao parar captura", ex); }
            try { await _device.DisposeAsync(); } catch { }
        }
        Surface.Dispose();
        Log.Info("Captura HDMI fechada");
    }
}
