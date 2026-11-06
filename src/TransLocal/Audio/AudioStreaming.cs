using System.Buffers;
using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// NAudio 3 marca WasapiCapture/WasapiLoopbackCapture como obsoletos em favor do WasapiRecorderBuilder;
// continuam funcionais na versão fixada no projeto (3.3.0).
#pragma warning disable CS0618

namespace TransLocal.Audio;

/// <summary>
/// Captura o som que está tocando neste computador (loopback WASAPI) e entrega PCM 16 bits,
/// no máximo 2 canais, na taxa de amostragem do dispositivo.
/// </summary>
internal sealed class LoopbackAudioSource : IDisposable
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly WasapiLoopbackCapture _capture;
    private readonly bool _isFloat;
    private readonly int _bytesPerSample;
    private readonly int _inChannels;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Recebe blocos PCM 16 bits little-endian intercalados.</summary>
    public event Action<ReadOnlySpan<byte>>? Pcm16Available;

    public LoopbackAudioSource()
    {
        _capture = new WasapiLoopbackCapture();
        var wf = _capture.WaveFormat;
        _isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
                   || (wf is WaveFormatExtensible ext && ext.SubFormat == SubtypeIeeeFloat);
        _bytesPerSample = wf.BitsPerSample / 8;
        _inChannels = wf.Channels;
        SampleRate = wf.SampleRate;
        Channels = Math.Min(2, wf.Channels);
        if (_bytesPerSample is not (2 or 3 or 4) || (_isFloat && _bytesPerSample != 4))
            throw new NotSupportedException($"Formato de áudio não suportado: {wf}");
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Log.Warn("Captura de áudio parou", e.Exception);
        };
        Log.Info($"Áudio do sistema: {wf} -> PCM16 {SampleRate} Hz {Channels} canal(is)");
    }

    public void Start() => _capture.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var handler = Pcm16Available;
        if (handler is null || e.BytesRecorded <= 0) return;

        var src = e.Buffer.AsSpan(0, e.BytesRecorded);
        int inFrame = _bytesPerSample * _inChannels;
        int frames = src.Length / inFrame;
        int outBytes = frames * Channels * 2;
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(outBytes, 1));
        try
        {
            var dst = rented.AsSpan(0, outBytes);
            int o = 0;
            for (int f = 0; f < frames; f++)
            {
                int baseOff = f * inFrame;
                for (int c = 0; c < Channels; c++)
                {
                    short s = ReadSample(src.Slice(baseOff + c * _bytesPerSample, _bytesPerSample));
                    BinaryPrimitives.WriteInt16LittleEndian(dst[o..], s);
                    o += 2;
                }
            }
            handler(dst);
        }
        catch (Exception ex)
        {
            Log.Warn("Falha ao processar áudio", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private short ReadSample(ReadOnlySpan<byte> s)
    {
        if (_isFloat)
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(s);
            if (float.IsNaN(v)) return 0;
            return (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);
        }
        return _bytesPerSample switch
        {
            2 => BinaryPrimitives.ReadInt16LittleEndian(s),
            3 => (short)((s[1]) | (s[2] << 8)),
            _ => (short)(BinaryPrimitives.ReadInt32LittleEndian(s) >> 16),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
    }
}

/// <summary>Toca áudio recebido com latência baixa, descartando o atraso acumulado.</summary>
internal sealed class AudioPlayer : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly IWavePlayer _output;
    private readonly int _maxBufferedBytes;
    private bool _disposed;

    public AudioPlayer(WaveFormat format, int maxDelayMs = 200)
    {
        _buffer = new BufferedWaveProvider(format, TimeSpan.FromSeconds(2))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _maxBufferedBytes = format.AverageBytesPerSecond * maxDelayMs / 1000;

        IWavePlayer? output = null;
        try
        {
            var wasapi = new WasapiOut(AudioClientShareMode.Shared, true, 40);
            output = wasapi;
            wasapi.Init(_buffer);
        }
        catch (Exception ex)
        {
            Log.Warn("WASAPI indisponível para reprodução; usando WaveOut", ex);
            try { output?.Dispose(); } catch { }
            var waveOut = new WaveOutEvent { BufferMilliseconds = 30, NumberOfBuffers = 3 };
            output = waveOut;
            waveOut.Init(_buffer);
        }
        _output = output;
        _output.Play();
    }

    public void Add(ReadOnlySpan<byte> data)
    {
        if (_disposed) return;
        if (_buffer.BufferedBytes > _maxBufferedBytes) _buffer.ClearBuffer();
        _buffer.AddSamples(data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output.Stop(); } catch { }
        try { _output.Dispose(); } catch { }
    }
}

/// <summary>Repassa o áudio de uma entrada (ex.: placa de captura HDMI) para os alto-falantes.</summary>
internal sealed class AudioPassthrough : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly AudioPlayer _player;
    private bool _disposed;

    public AudioPassthrough(MMDevice inputDevice)
    {
        _capture = new WasapiCapture(inputDevice, true, 20);
        _player = new AudioPlayer(_capture.WaveFormat, 150);
        _capture.DataAvailable += (_, e) =>
        {
            try { _player.Add(e.Buffer.AsSpan(0, e.BytesRecorded)); } catch { }
        };
        _capture.StartRecording();
        Log.Info($"Repasse de áudio: {inputDevice.FriendlyName} ({_capture.WaveFormat})");
    }

    public static List<(string Id, string Name)> ListInputs()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d) list.Add((d.ID, d.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Falha ao listar entradas de áudio", ex);
        }
        return list;
    }

    public static AudioPassthrough? TryOpen(string deviceId)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDevice(deviceId);
            return new AudioPassthrough(dev);
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível abrir a entrada de áudio da placa de captura", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        _player.Dispose();
    }
}
