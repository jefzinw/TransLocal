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

/// <summary>
/// Saída de som deste computador: o padrão do Windows (acompanha a troca, ex.: ao ligar um fone
/// Bluetooth) ou um dispositivo escolhido. Avisa os tocadores quando devem mudar de saída.
/// </summary>
internal static class AudioOutput
{
    private static readonly object Gate = new();
    private static string? _preferred;
    private static MMDeviceEnumerator? _watcher;
    private static MMDeviceNotificationClient? _client;

    /// <summary>Dispositivos de saída ou o padrão mudaram, ou a escolha mudou (chamado em outra thread).</summary>
    public static event Action? Changed;

    /// <summary>A escolha do usuário mudou (para salvar nas configurações).</summary>
    public static event Action<string?>? PreferredChanged;

    /// <summary>ID do dispositivo escolhido; null = padrão do Windows.</summary>
    public static string? Preferred
    {
        get { lock (Gate) return _preferred; }
        set
        {
            if (string.IsNullOrEmpty(value)) value = null;
            lock (Gate)
            {
                if (_preferred == value) return;
                _preferred = value;
            }
            PreferredChanged?.Invoke(value);
            RaiseChanged();
        }
    }

    /// <summary>Define a escolha salva sem avisar (início do programa).</summary>
    public static void Initialize(string? preferred)
    {
        lock (Gate) _preferred = string.IsNullOrEmpty(preferred) ? null : preferred;
        EnsureWatching();
    }

    /// <summary>Saídas ativas: (ID, nome).</summary>
    public static List<(string Id, string Name)> List()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (d) list.Add((d.ID, d.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Falha ao listar saídas de áudio", ex);
        }
        return list;
    }

    /// <summary>Nome da saída padrão do Windows agora (ou null).</summary>
    public static string? DefaultName()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.FriendlyName;
        }
        catch { return null; }
    }

    /// <summary>
    /// Dispositivo em que o som deve tocar agora: o escolhido, se estiver ligado; senão o padrão.
    /// Quem recebe deve descartá-lo.
    /// </summary>
    public static MMDevice? Resolve(MMDeviceEnumerator en)
    {
        string? want = Preferred;
        if (want is not null)
        {
            try
            {
                var d = en.GetDevice(want);
                if (d.State == DeviceState.Active) return d;
                d.Dispose();
            }
            catch { }
        }
        try
        {
            return en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : null;
        }
        catch { return null; }
    }

    public static void EnsureWatching()
    {
        lock (Gate)
        {
            if (_watcher is not null) return;
            try
            {
                // Criado numa thread MTA: os avisos chegam em threads do sistema e não dependem da UI.
                var (en, client) = Task.Run(() =>
                {
                    var e = new MMDeviceEnumerator();
                    // Sem contexto de sincronização: os avisos chegam numa thread do sistema e só agendam a troca.
                    var c = e.CreateNotificationClient(false);
                    c.DefaultDeviceChanged += (_, a) =>
                    {
                        if (a.Flow == DataFlow.Render && a.Role == Role.Multimedia) RaiseChanged();
                    };
                    c.DeviceAdded += (_, _) => RaiseChanged();
                    c.DeviceRemoved += (_, _) => RaiseChanged();
                    c.DeviceStateChanged += (_, _) => RaiseChanged();
                    return (e, c);
                }).GetAwaiter().GetResult();
                _watcher = en;
                _client = client;
            }
            catch (Exception ex)
            {
                Log.Warn("Não foi possível acompanhar as trocas de dispositivo de áudio", ex);
            }
        }
    }

    internal static void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        // Nunca dentro do aviso do Windows: o tratamento recria a saída de áudio.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { h(); } catch (Exception ex) { Log.Warn("Falha ao trocar a saída de áudio", ex); }
        });
    }
}

/// <summary>
/// Toca áudio recebido com latência baixa, descartando o atraso acumulado.
/// Muda de saída sozinho quando o padrão do Windows ou a escolha do usuário mudam.
/// </summary>
internal sealed class AudioPlayer : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly int _maxBufferedBytes;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _reopenTimer;
    private IWavePlayer? _output;
    private MMDevice? _device;
    private string? _deviceId;
    private bool _disposed;

    /// <summary>Nome da saída em uso (ou null).</summary>
    public string? DeviceName { get; private set; }

    public AudioPlayer(WaveFormat format, int maxDelayMs = 200)
    {
        _buffer = new BufferedWaveProvider(format, TimeSpan.FromSeconds(2))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _maxBufferedBytes = format.AverageBytesPerSecond * maxDelayMs / 1000;
        _reopenTimer = new System.Threading.Timer(_ => Reopen(force: false));
        AudioOutput.EnsureWatching();
        Open();
        AudioOutput.Changed += OnOutputsChanged;
    }

    public void Add(ReadOnlySpan<byte> data)
    {
        if (_disposed) return;
        if (_buffer.BufferedBytes > _maxBufferedBytes) _buffer.ClearBuffer();
        _buffer.AddSamples(data);
    }

    // Os avisos chegam em rajadas (ex.: Bluetooth conectando): espera assentar antes de trocar.
    private void OnOutputsChanged()
    {
        try { _reopenTimer.Change(400, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Reopen(bool force)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!force)
            {
                using var en = new MMDeviceEnumerator();
                using var target = AudioOutput.Resolve(en);
                if (target?.ID == _deviceId && _output?.PlaybackState == PlaybackState.Playing) return;
            }
            CloseOutput();
            Open();
        }
    }

    private void Open()
    {
        lock (_gate)
        {
            if (_disposed) return;
            MMDevice? device = null;
            try
            {
                using (var en = new MMDeviceEnumerator()) device = AudioOutput.Resolve(en);
                if (device is null) throw new InvalidOperationException("nenhuma saída de áudio ativa");
                var wasapi = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
                try { wasapi.Init(_buffer); }
                catch { wasapi.Dispose(); throw; }
                wasapi.PlaybackStopped += OnPlaybackStopped;
                _output = wasapi;
                _device = device;
                _deviceId = device.ID;
                DeviceName = device.FriendlyName;
                _buffer.ClearBuffer();
                wasapi.Play();
                Log.Info($"Som tocando em: {DeviceName}");
                return;
            }
            catch (Exception ex)
            {
                device?.Dispose();
                Log.Warn("WASAPI indisponível para reprodução; usando WaveOut", ex);
            }

            try
            {
                // Mapeador de som do Windows: segue o dispositivo padrão.
                var waveOut = new WaveOutEvent { BufferMilliseconds = 30, NumberOfBuffers = 3 };
                waveOut.Init(_buffer);
                _output = waveOut;
                _deviceId = null;
                DeviceName = AudioOutput.DefaultName();
                waveOut.Play();
            }
            catch (Exception ex)
            {
                _output = null;
                DeviceName = null;
                Log.Warn("Sem saída de áudio disponível", ex);
            }
        }
    }

    // O dispositivo sumiu (fone desligado, Bluetooth caiu): procura outra saída.
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed || e.Exception is null) return;
        Log.Warn("A saída de áudio parou", e.Exception);
        try { _reopenTimer.Change(300, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void CloseOutput()
    {
        var output = _output;
        _output = null;
        if (output is WasapiOut w) w.PlaybackStopped -= OnPlaybackStopped;
        try { output?.Stop(); } catch { }
        try { output?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _device = null;
        _deviceId = null;
    }

    public void Dispose()
    {
        AudioOutput.Changed -= OnOutputsChanged;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseOutput();
        }
        try { _reopenTimer.Dispose(); } catch { }
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
