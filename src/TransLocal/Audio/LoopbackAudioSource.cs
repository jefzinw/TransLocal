using System.Buffers;
using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

#pragma warning disable CS0618 // WasapiLoopbackCapture continua funcional no NAudio 3.3.0

namespace TransLocal.Audio;

/// <summary>
/// Captura o som que está tocando neste computador (loopback WASAPI) e entrega PCM 16 bits,
/// no máximo 2 canais, sempre no formato anunciado ao receptor.
/// Se a saída padrão muda ou é reconfigurada (jogo trocando o modo da tela HDMI, fone ligado),
/// reabre a captura sozinha e converte a taxa/canais da nova saída para o formato original.
/// </summary>
internal sealed class LoopbackAudioSource : IDisposable
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly object _gate = new();
    private readonly System.Threading.Timer _reopenTimer;
    private WasapiLoopbackCapture? _capture;
    private string? _deviceId;
    private bool _isFloat;
    private int _bytesPerSample;
    private int _inChannels;
    private int _inRate;
    private double _resamplePos;      // posição fracionária entre o último quadro e o próximo
    private short[] _prevFrame = new short[2];
    private bool _started;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Recebe blocos PCM 16 bits little-endian intercalados.</summary>
    public event Action<ReadOnlySpan<byte>>? Pcm16Available;

    public LoopbackAudioSource()
    {
        var first = OpenCapture() ?? throw new InvalidOperationException("Sem saída de áudio para capturar.");
        SampleRate = _inRate;
        Channels = Math.Min(2, _inChannels);
        _capture = first;
        _reopenTimer = new System.Threading.Timer(_ => Reopen("saída de áudio mudou"));
        AudioOutput.EnsureWatching();
        AudioOutput.Changed += OnOutputsChanged;
        Log.Info($"Áudio do sistema: {first.WaveFormat} -> PCM16 {SampleRate} Hz {Channels} canal(is)");
    }

    private WasapiLoopbackCapture? OpenCapture()
    {
        using var en = new MMDeviceEnumerator();
        if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
        var device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var capture = new WasapiLoopbackCapture(device);
        var wf = capture.WaveFormat;
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
                       || (wf is WaveFormatExtensible ext && ext.SubFormat == SubtypeIeeeFloat);
        int bps = wf.BitsPerSample / 8;
        if (bps is not (2 or 3 or 4) || (isFloat && bps != 4))
        {
            capture.Dispose();
            throw new NotSupportedException($"Formato de áudio não suportado: {wf}");
        }
        _isFloat = isFloat;
        _bytesPerSample = bps;
        _inChannels = wf.Channels;
        _inRate = wf.SampleRate;
        _deviceId = device.ID;
        _resamplePos = 0;
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (s, e) =>
        {
            if (e.Exception is null || _disposed || !ReferenceEquals(s, _capture)) return;
            Log.Warn("Captura de áudio parou; reabrindo", e.Exception);
            try { _reopenTimer.Change(500, Timeout.Infinite); } catch (ObjectDisposedException) { }
        };
        return capture;
    }

    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            _capture?.StartRecording();
        }
    }

    private void OnOutputsChanged()
    {
        try { _reopenTimer.Change(600, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Reopen(string why)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                // Mesma saída e ainda capturando: nada a fazer (o aviso foi de outro dispositivo).
                if (_capture is not null && _capture.CaptureState == CaptureState.Capturing)
                {
                    using var en = new MMDeviceEnumerator();
                    if (en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    {
                        using var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (def.ID == _deviceId) return;
                    }
                }
                var old = _capture;
                _capture = null;
                try { old?.StopRecording(); } catch { }
                try { old?.Dispose(); } catch { }
                _capture = OpenCapture();
                if (_capture is null)
                {
                    Log.Warn("Sem saída de áudio padrão; o som volta quando houver uma");
                    return;
                }
                if (_started) _capture.StartRecording();
                Log.Info($"Captura de áudio reaberta ({why}): {_capture.WaveFormat}" +
                         (_inRate != SampleRate || Math.Min(2, _inChannels) != Channels ? $" -> convertido para {SampleRate} Hz {Channels} canal(is)" : ""));
            }
            catch (Exception ex)
            {
                Log.Warn("Não foi possível reabrir a captura de áudio; nova tentativa em 3 s", ex);
                try { _reopenTimer.Change(3000, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var handler = Pcm16Available;
        if (handler is null || e.BytesRecorded <= 0 || !ReferenceEquals(sender, _capture)) return;

        var src = e.Buffer.AsSpan(0, e.BytesRecorded);
        int inFrame = _bytesPerSample * _inChannels;
        int frames = src.Length / inFrame;
        if (frames == 0) return;
        // Saída: até frames * (taxa de saída / taxa de entrada) + 2 quadros.
        int maxOut = (int)((long)frames * SampleRate / Math.Max(1, _inRate)) + 2;
        int outBytesMax = maxOut * Channels * 2;
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(outBytesMax, 1));
        try
        {
            var dst = rented.AsSpan(0, outBytesMax);
            int o = 0;
            Span<short> cur = stackalloc short[2];
            if (_inRate == SampleRate)
            {
                for (int f = 0; f < frames; f++)
                {
                    ReadFrame(src.Slice(f * inFrame, inFrame), cur);
                    for (int c = 0; c < Channels; c++) { BinaryPrimitives.WriteInt16LittleEndian(dst[o..], cur[c]); o += 2; }
                }
            }
            else
            {
                // Conversão linear de taxa (ex.: TV HDMI 48 kHz -> fone Bluetooth 44,1 kHz): suficiente para voz e jogo.
                double step = (double)_inRate / SampleRate;
                for (int f = 0; f < frames; f++)
                {
                    ReadFrame(src.Slice(f * inFrame, inFrame), cur);
                    while (_resamplePos < 1.0)
                    {
                        if (o + Channels * 2 > dst.Length) break;
                        for (int c = 0; c < Channels; c++)
                        {
                            int v = (int)(_prevFrame[c] + (cur[c] - _prevFrame[c]) * _resamplePos);
                            BinaryPrimitives.WriteInt16LittleEndian(dst[o..], (short)v);
                            o += 2;
                        }
                        _resamplePos += step;
                    }
                    _resamplePos -= 1.0;
                    _prevFrame[0] = cur[0];
                    _prevFrame[1] = cur[1];
                }
            }
            if (o > 0) handler(dst[..o]);
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

    /// <summary>Lê um quadro e devolve até 2 canais (mono vira estéreo se o formato original era estéreo).</summary>
    private void ReadFrame(ReadOnlySpan<byte> frame, Span<short> outCh)
    {
        short a = ReadSample(frame[.._bytesPerSample]);
        short b = _inChannels >= 2 ? ReadSample(frame.Slice(_bytesPerSample, _bytesPerSample)) : a;
        if (Channels == 1 && _inChannels >= 2) a = (short)((a + b) / 2);
        outCh[0] = a;
        outCh[1] = b;
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            AudioOutput.Changed -= OnOutputsChanged;
            try { _reopenTimer.Dispose(); } catch { }
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
            _capture = null;
        }
    }
}
