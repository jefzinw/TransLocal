using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace TransLocal.Video;

/// <summary>
/// Vídeo H.264 pela Media Foundation do Windows (como Parsec/Moonlight fazem): no PC, codificador da placa de
/// vídeo quando houver (senão o do Windows, por software); no receptor, o decodificador do Windows.
/// Comparado aos blocos JPEG, usa ~5× menos rede e bem menos processador para decodificar — é o que permite
/// 60 quadros/s num notebook simples sem saturar o Wi-Fi.
/// </summary>
internal static unsafe class MF
{
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    // ICodecAPI
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");

    public const int E_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int E_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    public const uint OUTPUT_PROVIDES_SAMPLES = 0x100;

    private static int _started;

    public static void Startup()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) MediaFactory.MFStartup();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OutputData { public uint StreamId; public IntPtr Sample; public uint Status; public IntPtr Events; }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamInfo { public uint Flags, Size, Alignment; }

    // Chamadas diretas pela vtable de IMFTransform (HRESULTs exatos, sem exceções no caminho de cada quadro).
    private static void** V(IMFTransform t) => *(void***)t.NativePointer;

    public static int SetInputType(IMFTransform t, IMFMediaType? mt) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)V(t)[15])(t.NativePointer, 0, mt?.NativePointer ?? IntPtr.Zero, 0);

    public static int SetOutputType(IMFTransform t, IMFMediaType? mt) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)V(t)[16])(t.NativePointer, 0, mt?.NativePointer ?? IntPtr.Zero, 0);

    public static IMFMediaType? GetOutputAvailableType(IMFTransform t, int index)
    {
        IntPtr p;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)V(t)[14])(t.NativePointer, 0, (uint)index, &p);
        return hr >= 0 && p != IntPtr.Zero ? new IMFMediaType(p) : null;
    }

    public static IMFMediaType? GetOutputCurrentType(IMFTransform t)
    {
        IntPtr p;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(t)[18])(t.NativePointer, 0, &p);
        return hr >= 0 && p != IntPtr.Zero ? new IMFMediaType(p) : null;
    }

    public static StreamInfo GetOutputStreamInfo(IMFTransform t)
    {
        StreamInfo si;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, StreamInfo*, int>)V(t)[7])(t.NativePointer, 0, &si);
        return si;
    }

    public static int ProcessMessage(IMFTransform t, uint message, nuint param = 0) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, int>)V(t)[23])(t.NativePointer, message, param);

    public static int ProcessInput(IMFTransform t, IMFSample sample) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)V(t)[24])(t.NativePointer, 0, sample.NativePointer, 0);

    public static int ProcessOutput(IMFTransform t, ref OutputData data)
    {
        uint status;
        fixed (OutputData* d = &data)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, OutputData*, uint*, int>)V(t)[25])(t.NativePointer, 0, 1, d, &status);
    }

    // MFT_MESSAGE_TYPE: FLUSH 0, DRAIN 1, SET_D3D_MANAGER 2 (não 1 — com 1 o codificador era esvaziado e o decodificador nunca recebia a placa).
    public const uint MSG_FLUSH = 0, MSG_SET_D3D_MANAGER = 2, MSG_BEGIN_STREAMING = 0x10000000, MSG_START_OF_STREAM = 0x10000003;

    /// <summary>Codificadores/decodificadores registrados (hardware primeiro quando pedido).</summary>
    public static List<IMFActivate> Enum(Guid category, uint flags, Guid inSub, Guid outSub)
    {
        Startup();
        var list = new List<IMFActivate>();
        MediaFactory.MFTEnumEx(category, flags,
            new RegisterTypeInfo { GuidMajorType = Video, GuidSubtype = inSub },
            new RegisterTypeInfo { GuidMajorType = Video, GuidSubtype = outSub },
            out IntPtr array, out uint count);
        try
        {
            for (int i = 0; i < count; i++) list.Add(new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size)));
        }
        finally
        {
            if (array != IntPtr.Zero) Marshal.FreeCoTaskMem(array);
        }
        return list;
    }

    [DllImport("mfplat.dll")]
    private static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr manager);

    /// <summary>
    /// Gerenciador de dispositivo DXGI ligado ao dispositivo Direct3D com o token correto (a versão sem token da
    /// biblioteca não liga de verdade, e o decodificador cai em silêncio para o processador).
    /// </summary>
    public static IMFDXGIDeviceManager CreateDeviceManager(Vortice.Direct3D11.ID3D11Device device)
    {
        Startup();
        int hr = MFCreateDXGIDeviceManager(out uint token, out IntPtr ptr);
        if (hr < 0 || ptr == IntPtr.Zero) throw new COMException("MFCreateDXGIDeviceManager", hr);
        // IMFDXGIDeviceManager::ResetDevice é o 8º método (IUnknown 0–2, Close 3, GetVideoService 4, Lock 5, Open 6, Reset 7).
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)(*(void***)ptr)[7])(ptr, device.NativePointer, token);
        if (hr < 0)
        {
            Marshal.Release(ptr);
            throw new COMException("ResetDevice", hr);
        }
        return new IMFDXGIDeviceManager(ptr);
    }

    public static string NameOf(IMFActivate a)
    {
        try { return a.GetAllocatedString(FriendlyName) ?? "?"; } catch { return "?"; }
    }

    public static IMFTransform Activate(IMFActivate a)
    {
        a.ActivateObject<IMFTransform>(IID_IMFTransform, out var t);
        return t ?? throw new InvalidOperationException("Ativação sem objeto");
    }

    public static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    public const uint ENUM_SYNC = 0x1, ENUM_ASYNC = 0x2, ENUM_HARDWARE = 0x4, ENUM_SORT = 0x40;
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported([In] ref Guid api);
    [PreserveSig] int IsModifiable([In] ref Guid api);
    [PreserveSig] int GetParameterRange([In] ref Guid api, out object min, out object max, out object step);
    [PreserveSig] int GetParameterValues([In] ref Guid api, out IntPtr values, out uint count);
    [PreserveSig] int GetDefaultValue([In] ref Guid api, out object value);
    [PreserveSig] int GetValue([In] ref Guid api, out object value);
    [PreserveSig] int SetValue([In] ref Guid api, [In] ref object value);
}

/// <summary>Codificador H.264 de baixa latência (um quadro entra, um sai; sem quadros B).</summary>
internal sealed unsafe class H264Encoder : IDisposable
{
    private readonly IMFTransform _mft;
    private readonly ICodecAPI? _codec;
    private readonly IMFMediaEventGenerator? _events;
    private readonly bool _async;
    private readonly bool _providesSamples;
    private readonly int _outSize;
    private readonly int _width, _height, _fps;
    private readonly byte[] _nv12;
    private long _frameIndex;
    private int _needInput;

    public string Name { get; }
    public bool Hardware { get; }
    public int Width => _width;
    public int Height => _height;
    public int Bitrate { get; }

    private H264Encoder(IMFTransform mft, string name, bool hardware, int w, int h, int fps, int bitrate, bool conservative, bool high,
        Vortice.Direct3D11.ID3D11Device? device = null)
    {
        _mft = mft;
        Name = name;
        Hardware = hardware;
        _width = w;
        _height = h;
        _fps = fps;
        Bitrate = bitrate;
        _nv12 = GC.AllocateUninitializedArray<byte>(w * h * 3 / 2, pinned: true);
        CurrentBitrate = bitrate;

        var attrs = mft.Attributes;
        try { _async = attrs.GetUInt32(MF.TransformAsync) != 0; } catch { _async = false; }
        if (_async)
        {
            attrs.Set(MF.AsyncUnlock, 1u);
            _events = mft.QueryInterface<IMFMediaEventGenerator>();
        }
        try { attrs.Set(MF.LowLatency, 1u); } catch { }
        if (device is not null)
        {
            // Entrada direto da placa de vídeo (textura NV12): o codificador lê a imagem sem passar pelo processador.
            _manager = MF.CreateDeviceManager(device);
            int hrm = MF.ProcessMessage(mft, MF.MSG_SET_D3D_MANAGER, (nuint)(ulong)_manager.NativePointer);
            if (hrm < 0) throw new COMException("SET_D3D_MANAGER (codificador)", hrm);
            TakesTextures = true;
        }

        try { _codec = (ICodecAPI)Marshal.GetObjectForIUnknown(mft.NativePointer); } catch { _codec = null; }
        if (!Set(MF.RateControlMode, 4u)) Set(MF.RateControlMode, 0u); // baixa latência VBR; senão CBR
        Set(MF.MeanBitRate, (uint)bitrate);
        Conservative = conservative;
        if (!conservative)
        {
            Set(MF.MaxBitRate, (uint)(bitrate * 2));
            // Buffer de ~3 quadros: absorve cenas de muito movimento sem pixelar (com 1 quadro a imagem desmanchava)
            // e ainda limita o tamanho do quadro-chave, que sai em rajadas curtas pelo UDP.
            Set(MF.BufferSize, (uint)Math.Max(bitrate * 3L / Math.Max(fps, 1), 192_000));
        }
        Set(MF.LowLatency, true);
        Set(MF.BPictureCount, 0u);
        // Quadros-chave só quando o receptor pede (começo, perda de sequência): sem pico periódico.
        Set(MF.GopSize, (uint)(fps * (conservative ? 4 : 120)));
        Set(MF.QualityVsSpeed, uint.TryParse(Environment.GetEnvironmentVariable("TRANSLOCAL_ENC_QVS"), out var qvs) ? qvs : 33u);

        var output = MediaFactory.MFCreateMediaType();
        output.Set(MF.MajorType, MF.Video);
        output.Set(MF.Subtype, MF.H264);
        output.Set(MF.FrameSize, MF.Pack(w, h));
        output.Set(MF.FrameRate, MF.Pack(fps, 1));
        output.Set(MF.PixelAspect, MF.Pack(1, 1));
        output.Set(MF.AvgBitrate, (uint)bitrate);
        output.Set(MF.InterlaceMode, 2u);
        int hr = -1;
        foreach (uint profile in ProfileOrder(high))
        {
            output.Set(MF.Profile, profile);
            hr = MF.SetOutputType(mft, output);
            if (hr >= 0) { Profile = profile; break; }
        }
        output.Dispose();
        if (hr < 0) throw new COMException("Tipo de saída H.264 recusado", hr);

        var input = MediaFactory.MFCreateMediaType();
        input.Set(MF.MajorType, MF.Video);
        input.Set(MF.Subtype, MF.NV12);
        input.Set(MF.FrameSize, MF.Pack(w, h));
        input.Set(MF.FrameRate, MF.Pack(fps, 1));
        input.Set(MF.PixelAspect, MF.Pack(1, 1));
        input.Set(MF.InterlaceMode, 2u);
        hr = MF.SetInputType(mft, input);
        input.Dispose();
        if (hr < 0) throw new COMException("Tipo de entrada NV12 recusado", hr);

        var si = MF.GetOutputStreamInfo(mft);
        _providesSamples = (si.Flags & MF.OUTPUT_PROVIDES_SAMPLES) != 0;
        _outSize = (int)Math.Max(si.Size, (uint)(w * h)); // quadro-chave cabe com folga
        MF.ProcessMessage(mft, MF.MSG_BEGIN_STREAMING);
        MF.ProcessMessage(mft, MF.MSG_START_OF_STREAM);
    }

    private bool Set(Guid api, object value)
    {
        if (_codec is null) return false;
        try { return _codec.SetValue(ref api, ref value) >= 0; } catch { return false; }
    }

    /// <summary>Perfil H.264 em uso (100 High, 77 Main, 66 Baseline).</summary>
    public uint Profile { get; private set; }

    // High (CABAC + transformada 8×8): ~10–15% mais nitidez pela mesma taxa — quando o receptor decodifica pela
    // placa de vídeo. Senão Baseline: sem CABAC, a decodificação pelo processador de um notebook simples fica mais leve.
    private static uint[] ProfileOrder(bool high) => Environment.GetEnvironmentVariable("TRANSLOCAL_H264_PROFILE") switch
    {
        "100" => new[] { 100u, 77u, 66u },
        "77" => new[] { 77u, 66u },
        "66" => new[] { 66u, 77u },
        _ => high ? new[] { 100u, 77u, 66u } : new[] { 66u, 77u },
    };

    /// <summary>Taxa de bits atual (muda com <see cref="SetBitrate"/>).</summary>
    public int CurrentBitrate { get; private set; }

    /// <summary>
    /// Muda a taxa de bits sem reiniciar o codificador (controle adaptativo, como no Xbox Cloud/Parsec).
    /// False se este codificador não aceita mudança durante a transmissão.
    /// </summary>
    public bool SetBitrate(int bitrate)
    {
        if (bitrate == CurrentBitrate) return true;
        bool ok = Set(MF.MeanBitRate, (uint)bitrate);
        if (ok)
        {
            Set(MF.MaxBitRate, (uint)(bitrate * 2));
            CurrentBitrate = bitrate;
        }
        return ok;
    }

    // Horário de captura de cada quadro enviado ao codificador, para saber o atraso de cada saída.
    private readonly Queue<long> _inputTimes = new();

    /// <summary>Horário (Stopwatch) da captura do último quadro devolvido por Encode/Poll.</summary>
    public long LastOutputCaptureTime { get; private set; }

    /// <summary>Taxa de bits recomendada: ~0,07–0,12 bit por pixel por quadro conforme a qualidade escolhida.</summary>
    public static int BitrateFor(int w, int h, int fps, int quality)
    {
        // Em rede de casa, como o Moonlight: o custo cresce menos que proporcional aos quadros/s (a 30 q/s cada quadro
        // muda mais que a 60 e precisa de mais bits). Qualidade 85, 1366×768: ~9 Mbit/s a 30 q/s, ~15 Mbit/s a 60 q/s;
        // máxima, 1080p60: ~35 Mbit/s. Antes, a 30 q/s ficava em ~4 Mbit/s — jogos detalhados pixelavam.
        double k = quality >= 90 ? 0.95 : quality >= 70 ? 0.80 : 0.55;
        double bps = w * (double)h * Math.Pow(Math.Max(1, fps), 0.7) * k;
        return (int)Math.Clamp(bps, 4_000_000, 40_000_000);
    }

    /// <summary>Ajustes da 1.6 (sem buffer de 1 quadro nem limite de pico): reserva se a placa não entregar quadros.</summary>
    public bool Conservative { get; }

    private static readonly bool StallTest = Environment.GetEnvironmentVariable("TRANSLOCAL_ENC_STALL_TEST") == "1";

    /// <summary>Chamadas a Encode seguidas sem nenhuma saída (codificador travado ou recusando os ajustes).</summary>
    public int FramesWithoutOutput { get; private set; }

    /// <summary>
    /// Cria o melhor codificador disponível (placa de vídeo, depois software). Null se nenhum servir.
    /// <paramref name="level"/>: 0 = ajustes de baixa latência; 1 = ajustes conservadores; 2 = só software.
    /// </summary>
    /// <summary>Recebe texturas NV12 da placa de vídeo (<see cref="EncodeTexture"/>).</summary>
    public bool TakesTextures { get; }

    private IMFDXGIDeviceManager? _manager;

    /// <summary>
    /// Codificador da placa de vídeo que lê texturas NV12 do dispositivo informado (caminho todo na placa, como
    /// Sunshine/Parsec/Xbox Cloud). Null se nenhum codificador de hardware aceitar.
    /// </summary>
    public static H264Encoder? TryCreateForTextures(Vortice.Direct3D11.ID3D11Device device, int w, int h, int fps, int bitrate, bool high)
    {
        if (w % 2 != 0 || h % 2 != 0 || w < 64 || h < 64) return null;
        List<IMFActivate> list;
        try { list = MF.Enum(TransformCategoryGuids.VideoEncoder, MF.ENUM_HARDWARE | MF.ENUM_ASYNC | MF.ENUM_SORT, MF.NV12, MF.H264); }
        catch (Exception ex) { Log.Warn("H.264: não foi possível listar codificadores", ex); return null; }
        try
        {
            foreach (var a in list)
            {
                string name = MF.NameOf(a);
                IMFTransform? t = null;
                try
                {
                    t = MF.Activate(a);
                    return new H264Encoder(t, name, true, w, h, fps, bitrate, false, high, device);
                }
                catch (Exception ex)
                {
                    Log.Info($"H.264: codificador “{name}” não aceitou texturas da placa ({ex.Message})");
                    try { t?.Dispose(); } catch { }
                }
            }
            return null;
        }
        finally
        {
            foreach (var a in list) a.Dispose();
        }
    }

    public static H264Encoder? TryCreate(int w, int h, int fps, int bitrate, int level = 0, bool high = false)
    {
        if (w % 2 != 0 || h % 2 != 0 || w < 64 || h < 64) return null;
        if (int.TryParse(Environment.GetEnvironmentVariable("TRANSLOCAL_ENC_LEVEL"), out int forced)) level = Math.Max(level, forced); // testes
        bool softwareOnly = level >= 2 || Environment.GetEnvironmentVariable("TRANSLOCAL_ENC_SOFTWARE") == "1"; // testes com a placa ocupada
        foreach (uint flags in softwareOnly ? new[] { MF.ENUM_SYNC | MF.ENUM_SORT } : new[] { MF.ENUM_HARDWARE | MF.ENUM_ASYNC | MF.ENUM_SORT, MF.ENUM_SYNC | MF.ENUM_SORT })
        {
            List<IMFActivate> list;
            try { list = MF.Enum(TransformCategoryGuids.VideoEncoder, flags, MF.NV12, MF.H264); }
            catch (Exception ex) { Log.Warn("H.264: não foi possível listar codificadores", ex); continue; }
            foreach (var a in list)
            {
                string name = MF.NameOf(a);
                IMFTransform? t = null;
                try
                {
                    t = MF.Activate(a);
                    var enc = new H264Encoder(t, name, (flags & MF.ENUM_HARDWARE) != 0, w, h, fps, bitrate, level == 1, high);
                    foreach (var other in list) other.Dispose();
                    return enc;
                }
                catch (Exception ex)
                {
                    Log.Info($"H.264: codificador “{name}” recusou a configuração ({ex.Message})");
                    try { t?.Dispose(); } catch { }
                }
            }
            foreach (var a in list) a.Dispose();
        }
        return null;
    }

    /// <summary>
    /// Codifica um quadro BGRA (top-down). Devolve o fluxo Annex B do quadro (ou null se o codificador ainda não
    /// entregou nada — codificadores de placa de vídeo podem atrasar um quadro).
    /// </summary>
    public byte[]? Encode(IntPtr bgra, int stride, bool forceKeyFrame, out bool keyFrame, long captureTime = 0)
    {
        keyFrame = false;
        FramesWithoutOutput++;
        // Teste da rede de segurança: simula um codificador que aceita quadros e nunca devolve imagem.
        if (StallTest && !Conservative) return null;
        if (forceKeyFrame) Set(MF.ForceKeyFrame, 1u);

        // Converte direto no buffer do codificador (sem cópia intermediária).
        var buffer = MediaFactory.MFCreateMemoryBuffer(_nv12.Length);
        buffer.Lock(out IntPtr p, out _, out _);
        ColorConvert.BgraToNv12((byte*)bgra, stride, (byte*)p, _width, _height);
        buffer.Unlock();
        buffer.CurrentLength = _nv12.Length;
        return Submit(buffer, out keyFrame, captureTime);
    }

    /// <summary>Codifica uma textura NV12 da placa de vídeo (só com <see cref="TakesTextures"/>).</summary>
    public byte[]? EncodeTexture(Vortice.Direct3D11.ID3D11Texture2D nv12, bool forceKeyFrame, out bool keyFrame, long captureTime)
    {
        keyFrame = false;
        FramesWithoutOutput++;
        if (StallTest && !Conservative) return null;
        if (forceKeyFrame) Set(MF.ForceKeyFrame, 1u);
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID, nv12, 0, false);
        try
        {
            using var b2 = buffer.QueryInterface<IMF2DBuffer>();
            buffer.CurrentLength = (int)b2.ContiguousLength;
        }
        catch { buffer.CurrentLength = _width * _height * 3 / 2; }
        return Submit(buffer, out keyFrame, captureTime);
    }

    private byte[]? Submit(IMFMediaBuffer buffer, out bool keyFrame, long captureTime)
    {
        keyFrame = false;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        long duration = 10_000_000L / _fps;
        sample.SampleTime = _frameIndex++ * duration;
        sample.SampleDuration = duration;

        byte[]? result = null;
        try
        {
            if (_async)
            {
                PumpEvents(ref result, ref keyFrame);
                if (_needInput <= 0)
                {
                    // Codificador ainda ocupado com o quadro anterior: espera um pouco a vez dele.
                    for (int i = 0; i < 8 && _needInput <= 0; i++) { Thread.Sleep(1); PumpEvents(ref result, ref keyFrame); }
                    if (_needInput <= 0) return result; // ocupado: descarta este quadro (o próximo traz a tela atual)
                }
                _needInput--;
                int hr = MF.ProcessInput(_mft, sample);
                if (Trace && _traceCount++ < 40) Log.Info($"H264TRACE ProcessInput 0x{hr:X8} (texturas={TakesTextures})");
                if (hr < 0) throw new COMException("ProcessInput", hr);
                _inputTimes.Enqueue(captureTime);
                if (_inputTimes.Count > 32) _inputTimes.Dequeue();
                _outstanding++;
                // Espera curta (até ~8 ms) pela saída deste quadro; se demorar, Poll() recolhe na próxima volta do laço.
                for (int i = 0; i < 12 && _outstanding > 0; i++)
                {
                    PumpEvents(ref result, ref keyFrame);
                    if (_outstanding > 0) { if (i < 4) Thread.Yield(); else Thread.Sleep(1); }
                }
            }
            else
            {
                int hr = MF.ProcessInput(_mft, sample);
                if (hr == MF.E_NOTACCEPTING) { Drain(ref result, ref keyFrame); hr = MF.ProcessInput(_mft, sample); }
                if (hr < 0) throw new COMException("ProcessInput", hr);
                _inputTimes.Enqueue(captureTime);
                if (_inputTimes.Count > 32) _inputTimes.Dequeue();
                Drain(ref result, ref keyFrame);
            }
        }
        finally
        {
            sample.Dispose();
            buffer.Dispose();
        }
        return AnnexB.StripFiller(result);
    }

    private int _outstanding;

    /// <summary>Há quadro enviado ao codificador da placa de vídeo cuja saída ainda não foi recolhida.</summary>
    public bool Pending => _async && _outstanding > 0;

    /// <summary>Recolhe a saída atrasada do codificador da placa de vídeo, sem esperar.</summary>
    public byte[]? Poll(out bool keyFrame)
    {
        keyFrame = false;
        if (!Pending) return null;
        byte[]? result = null;
        PumpEvents(ref result, ref keyFrame);
        return AnnexB.StripFiller(result);
    }

    private static readonly bool Trace = Environment.GetEnvironmentVariable("TRANSLOCAL_ENC_TRACE") == "1";
    private int _traceCount;

    private void PumpEvents(ref byte[]? result, ref bool key)
    {
        while (true)
        {
            IMFMediaEvent ev;
            try { ev = _events!.GetEvent(1 /* MF_EVENT_FLAG_NO_WAIT */); }
            catch { return; }
            using (ev)
            {
                var type = ev.EventType;
                if (Trace && _traceCount++ < 40) Log.Info($"H264TRACE evento {type} (status 0x{ev.Status.Code:X8})");
                if (type == MediaEventTypes.TransformNeedInput) _needInput++;
                else if (type == MediaEventTypes.TransformHaveOutput && TakeOutput(ref result, ref key)) _outstanding = Math.Max(0, _outstanding - 1);
            }
        }
    }

    private void Drain(ref byte[]? result, ref bool key)
    {
        for (int i = 0; i < 8; i++)
        {
            if (!TakeOutput(ref result, ref key)) return;
        }
    }

    /// <summary>Tira uma saída do codificador e acrescenta ao resultado. False quando não há mais.</summary>
    private bool TakeOutput(ref byte[]? result, ref bool key)
    {
        IMFSample? own = null;
        IMFMediaBuffer? ownBuffer = null;
        var data = new MF.OutputData();
        if (!_providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            ownBuffer = MediaFactory.MFCreateMemoryBuffer(_outSize);
            own.AddBuffer(ownBuffer);
            data.Sample = own.NativePointer;
        }
        try
        {
            int hr = MF.ProcessOutput(_mft, ref data);
            if (hr == MF.E_NEED_MORE_INPUT) return false;
            if (hr < 0) throw new COMException("ProcessOutput", hr);
            var outSample = own ?? (data.Sample != IntPtr.Zero ? new IMFSample(data.Sample) : null);
            if (outSample is null) return false;
            try
            {
                try { key |= outSample.GetUInt32(MF.CleanPoint) != 0; } catch { }
                if (_inputTimes.Count > 0) LastOutputCaptureTime = _inputTimes.Dequeue();
                FramesWithoutOutput = 0;
                using var contiguous = outSample.ConvertToContiguousBuffer();
                contiguous.Lock(out IntPtr p, out _, out int len);
                try
                {
                    int old = result?.Length ?? 0;
                    Array.Resize(ref result, old + len);
                    Marshal.Copy(p, result, old, len);
                }
                finally { contiguous.Unlock(); }
            }
            finally
            {
                if (own is null) outSample.Dispose();
            }
            if (data.Events != IntPtr.Zero) Marshal.Release(data.Events);
            return true;
        }
        finally
        {
            ownBuffer?.Dispose();
            own?.Dispose();
        }
    }

    public void Dispose()
    {
        try { MF.ProcessMessage(_mft, MF.MSG_FLUSH); } catch { }
        if (_codec is not null) try { Marshal.ReleaseComObject(_codec); } catch { }
        _events?.Dispose();
        _mft.Dispose();
        _manager?.Dispose();
    }
}

/// <summary>Decodificador H.264 do Windows (software, baixa latência), escrevendo direto na superfície de exibição.</summary>
internal sealed unsafe class H264Decoder : IDisposable
{
    private readonly IMFTransform _mft;
    private bool _providesSamples;
    private int _outSize;
    private int _codedWidth, _codedHeight, _stride;
    private Vortice.Direct3D11.ID3D11Device? _device;
    private bool _ownsDevice;
    private IMFDXGIDeviceManager? _manager;
    private IGpuFrameSink? _sink;
    private static readonly Guid IID_Texture2D = typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID;

    /// <summary>O último quadro foi entregue direto à placa de vídeo (sem passar pela memória do processador).</summary>
    public bool LastOnGpu { get; private set; }

    public string Name { get; }

    private bool _linked;

    /// <summary>
    /// Decodificando de fato pela placa de vídeo (DXVA): o decodificador do Windows aceitou a placa E entrega as
    /// próprias texturas. Em alguns PCs ele aceita a placa mas continua no processador sem avisar — aí é false.
    /// </summary>
    public bool Hardware => _linked && _providesSamples;

    private H264Decoder(IMFTransform mft, string name, bool hardware, GpuDevice? gpu, int width, int height)
    {
        _mft = mft;
        Name = name;
        try { mft.Attributes.Set(MF.LowLatency, 1u); } catch { }
        if (hardware) _linked = TryEnableDxva(gpu);

        var input = MediaFactory.MFCreateMediaType();
        input.Set(MF.MajorType, MF.Video);
        input.Set(MF.Subtype, MF.H264);
        if (width > 0 && height > 0)
        {
            input.Set(MF.FrameSize, MF.Pack(width, height));
            input.Set(MF.FrameRate, MF.Pack(60, 1));
            input.Set(MF.InterlaceMode, 2u);
        }
        int hr = MF.SetInputType(mft, input);
        input.Dispose();
        if (hr < 0) throw new COMException("Tipo de entrada H.264 recusado", hr);
        if (!NegotiateOutput()) throw new InvalidOperationException("Decodificador sem saída NV12");
        MF.ProcessMessage(mft, MF.MSG_BEGIN_STREAMING);
        MF.ProcessMessage(mft, MF.MSG_START_OF_STREAM);
    }

    /// <summary>Liga a decodificação pela placa de vídeo (antes de definir os formatos). False = segue no processador.</summary>
    private bool TryEnableDxva(GpuDevice? gpu)
    {
        try
        {
            if (gpu is not null)
            {
                // Mesmo dispositivo da exibição: a textura decodificada vai direto para a tela.
                _device = gpu.Device;
                _ownsDevice = false;
                _manager = MF.CreateDeviceManager(_device!);
                try { _mft.Attributes.Set(new Guid("f7db8a2f-4f48-4ee8-ae31-8b6ebe558ae2"), 1u); } catch { } // CODECAPI_AVDecVideoAcceleration_H264
                int hr0 = MF.ProcessMessage(_mft, MF.MSG_SET_D3D_MANAGER, (nuint)(ulong)_manager.NativePointer);
                if (hr0 < 0) throw new COMException("SET_D3D_MANAGER", hr0);
                return true;
            }
            _ownsDevice = true;
            var levels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0, Vortice.Direct3D.FeatureLevel.Level_9_3 };
            Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware,
                Vortice.Direct3D11.DeviceCreationFlags.VideoSupport | Vortice.Direct3D11.DeviceCreationFlags.BgraSupport,
                levels, out _device).CheckError();
            using (var mt = _device!.QueryInterface<Vortice.Direct3D11.ID3D11Multithread>()) mt.SetMultithreadProtected(true);
            _manager = MF.CreateDeviceManager(_device!);
            int hr = MF.ProcessMessage(_mft, MF.MSG_SET_D3D_MANAGER, (nuint)(ulong)_manager.NativePointer);
            if (hr < 0) throw new COMException("SET_D3D_MANAGER", hr);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"H.264: decodificação pela placa de vídeo indisponível ({ex.Message}); usando o processador");
            try { MF.ProcessMessage(_mft, MF.MSG_SET_D3D_MANAGER, 0); } catch { }
            _manager?.Dispose(); _manager = null;
            if (_ownsDevice) _device?.Dispose();
            _device = null;
            return false;
        }
    }

    private bool NegotiateOutput()
    {
        if (_linked)
        {
            // Texturas de saída que a exibição pode ler (como o Firefox faz com este mesmo decodificador).
            try
            {
                using var oa = _mft.GetOutputStreamAttributes(0);
                oa.Set(new Guid("eacf97ad-065c-4408-bee3-fdcbfd128be2"), (uint)(Vortice.Direct3D11.BindFlags.Decoder | Vortice.Direct3D11.BindFlags.ShaderResource));
                oa.Set(new Guid("39dbd44d-2e44-4931-a4c8-352d3dc42115"), 1u);
            }
            catch (Exception ex) { Log.Info($"H.264: atributos de saída D3D11 indisponíveis ({ex.Message})"); }
        }
        for (int i = 0; i < 32; i++)
        {
            using var t = MF.GetOutputAvailableType(_mft, i);
            if (t is null) break;
            Guid sub;
            try { sub = t.GetGUID(MF.Subtype); } catch { continue; }
            if (sub != MF.NV12) continue;
            if (MF.SetOutputType(_mft, t) < 0) continue;
            ReadOutputFormat();
            return true;
        }
        return false;
    }

    private void ReadOutputFormat()
    {
        using var cur = MF.GetOutputCurrentType(_mft);
        if (cur is not null)
        {
            try
            {
                ulong size = cur.GetUInt64(MF.FrameSize);
                _codedWidth = (int)(size >> 32);
                _codedHeight = (int)(size & 0xFFFFFFFF);
            }
            catch { }
            try { _stride = (int)cur.GetUInt32(MF.DefaultStride); } catch { _stride = _codedWidth; }
            if (_stride <= 0) _stride = _codedWidth;
        }
        var si = MF.GetOutputStreamInfo(_mft);
        _providesSamples = (si.Flags & MF.OUTPUT_PROVIDES_SAMPLES) != 0;
        _outSize = (int)Math.Max(si.Size, (uint)(Math.Max(_stride, 16) * Math.Max(_codedHeight, 16) * 3 / 2));
    }

    private static bool? _available;

    /// <summary>O Windows deste computador tem decodificador H.264 (verificado uma vez).</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available is bool b) return b;
            try
            {
                using var d = TryCreate();
                _available = d is not null;
            }
            catch { _available = false; }
            return _available.Value;
        }
    }

    public static H264Decoder? TryCreate(bool hardware = true, GpuDevice? gpu = null, int width = 0, int height = 0)
    {
        List<IMFActivate> list;
        try { list = MF.Enum(TransformCategoryGuids.VideoDecoder, MF.ENUM_SYNC | MF.ENUM_SORT, MF.H264, MF.NV12); }
        catch (Exception ex) { Log.Warn("H.264: não foi possível listar decodificadores", ex); return null; }
        try
        {
            foreach (var a in list)
            {
                string name = MF.NameOf(a);
                IMFTransform? t = null;
                try
                {
                    t = MF.Activate(a);
                    return new H264Decoder(t, name, hardware, gpu, width, height);
                }
                catch (Exception ex)
                {
                    Log.Info($"H.264: decodificador “{name}” indisponível ({ex.Message})");
                    try { t?.Dispose(); } catch { }
                }
            }
            return null;
        }
        finally
        {
            foreach (var a in list) a.Dispose();
        }
    }

    /// <summary>
    /// Decodifica um quadro e escreve em <paramref name="surface"/> (tamanho visível width×height).
    /// False se não saiu imagem (o decodificador ainda precisa de mais dados, ou erro — peça um quadro-chave).
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> annexB, int width, int height, FrameSurface surface, out bool error) =>
        Decode(annexB, width, height, surface, null, out error);

    /// <summary>
    /// Como o outro Decode, mas entrega o quadro a <paramref name="gpu"/> quando ele está na placa de vídeo
    /// (<see cref="LastOnGpu"/>); senão, escreve na superfície.
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> annexB, int width, int height, FrameSurface surface, IGpuFrameSink? gpu, out bool error)
    {
        error = false;
        LastOnGpu = false;
        _sink = gpu is { Failed: false } ? gpu : null;
        // Transmissores 1.6 mandam unidades de preenchimento: o decodificador seguraria o quadro até o próximo.
        annexB = AnnexB.TrimTrailingFiller(annexB);
        var buffer = MediaFactory.MFCreateMemoryBuffer(annexB.Length);
        buffer.Lock(out IntPtr p, out _, out _);
        annexB.CopyTo(new Span<byte>((void*)p, annexB.Length));
        buffer.Unlock();
        buffer.CurrentLength = annexB.Length;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        bool produced = false;
        try
        {
            int hr = MF.ProcessInput(_mft, sample);
            if (hr == MF.E_NOTACCEPTING) { produced |= Drain(width, height, surface); hr = MF.ProcessInput(_mft, sample); }
            if (hr < 0) { error = true; return produced; }
            produced |= Drain(width, height, surface);
        }
        catch (Exception ex)
        {
            Log.Warn("H.264: falha ao decodificar", ex);
            error = true;
        }
        finally
        {
            sample.Dispose();
            buffer.Dispose();
        }
        return produced;
    }

    private bool Drain(int width, int height, FrameSurface surface)
    {
        bool produced = false;
        for (int guard = 0; guard < 8; guard++)
        {
            IMFSample? own = null;
            IMFMediaBuffer? ownBuffer = null;
            var data = new MF.OutputData();
            if (!_providesSamples)
            {
                own = MediaFactory.MFCreateSample();
                // Indo para a exibição pela placa de vídeo: a saída é escrita direto numa textura dela.
                ownBuffer = MediaFactory.MFCreateMemoryBuffer(_outSize);
                own.AddBuffer(ownBuffer);
                data.Sample = own.NativePointer;
            }
            try
            {
                int hr = MF.ProcessOutput(_mft, ref data);
                if (hr == MF.E_NEED_MORE_INPUT) return produced;
                if (hr == MF.E_STREAM_CHANGE)
                {
                    // Primeiro quadro (ou mudança de resolução): renegocia a saída e tenta de novo.
                    if (!NegotiateOutput()) throw new InvalidOperationException("Decodificador sem saída NV12");
                    continue;
                }
                if (hr < 0) throw new COMException("ProcessOutput", hr);
                var outSample = own ?? (data.Sample != IntPtr.Zero ? new IMFSample(data.Sample) : null);
                if (outSample is null) return produced;
                try
                {
                    if (_sink is not null && TryPresentGpu(outSample, width, height))
                    {
                        produced = true;
                        LastOnGpu = true;
                        if (data.Events != IntPtr.Zero) Marshal.Release(data.Events);
                        continue;
                    }
                    using var contiguous = outSample.ConvertToContiguousBuffer();
                    contiguous.Lock(out IntPtr p, out _, out int len);
                    try
                    {
                        int stride = _stride > 0 ? _stride : _codedWidth;
                        int planeHeight = _codedHeight > 0 ? _codedHeight : height;
                        // Alguns decodificadores alinham a altura do plano Y a 16 linhas.
                        if ((long)stride * planeHeight * 3 / 2 > len) planeHeight = (int)(len * 2L / 3 / stride);
                        int w = Math.Min(width, stride), h = Math.Min(height, planeHeight);
                        byte* yp = (byte*)p, uvp = (byte*)p + (long)stride * planeHeight;
                        // Exibição pela placa de vídeo: envia o NV12 como está (cor e tamanho ficam com a placa).
                        if (_sink is not null && _sink.PresentNv12Memory(yp, stride, uvp, stride, w, h))
                        {
                            LastOnGpu = true;
                        }
                        else
                        {
                            surface.WriteNv12(yp, stride, uvp, stride, w, h);
                        }
                        produced = true;
                    }
                    finally { contiguous.Unlock(); }
                }
                finally
                {
                    if (own is null) outSample.Dispose();
                }
                if (data.Events != IntPtr.Zero) Marshal.Release(data.Events);
            }
            finally
            {
                ownBuffer?.Dispose();
                own?.Dispose();
            }
        }
        return produced;
    }

    /// <summary>Entrega a textura de saída (DXVA) à exibição. False = não é textura ou a exibição recusou.</summary>
    private bool TryPresentGpu(IMFSample sample, int width, int height)
    {
        try
        {
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
            if (dxgi is null) return false;
            IntPtr ptr = dxgi.GetResource(IID_Texture2D);
            if (ptr == IntPtr.Zero) return false;
            using var tex = new Vortice.Direct3D11.ID3D11Texture2D(ptr);
            int slice = (int)dxgi.SubresourceIndex;
            var d = tex.Description;
            int w = Math.Min(width, (int)d.Width), h = Math.Min(height, (int)d.Height);
            return _sink!.PresentNv12(tex, slice, w, h);
        }
        catch (Exception ex)
        {
            Log.Warn("H.264: saída na placa de vídeo indisponível; usando o processador", ex);
            _sink = null;
            return false;
        }
    }

    public void Dispose()
    {
        try { MF.ProcessMessage(_mft, MF.MSG_FLUSH); } catch { }
        _mft.Dispose();
        _manager?.Dispose();
        if (_ownsDevice) _device?.Dispose();
    }
}

/// <summary>Conversões BGRA ↔ NV12 (BT.601, faixa limitada) em paralelo por faixas de linhas.</summary>
/// <summary>Ajustes no fluxo H.264 (Annex B).</summary>
internal static class AnnexB
{
    /// <summary>
    /// Remove as unidades de preenchimento (NAL tipo 12) que codificadores como o da AMD acrescentam para manter a
    /// taxa constante. Elas não têm imagem — só gastam rede — e, no fim de um quadro, fazem o decodificador do Windows
    /// esperar o quadro seguinte para soltar este (um "tranco" de ~16 ms a cada poucos quadros).
    /// </summary>
    public static byte[]? StripFiller(byte[]? data)
    {
        if (data is null || data.Length < 5) return data;
        int idx = FindStart(data, 0, out int sc);
        if (idx < 0) return data;
        bool any = false;
        for (int i = idx; i >= 0;)
        {
            int next = FindStart(data, i + 3, out _);
            if ((data[i + StartLen(data, i)] & 0x1F) == 12) { any = true; break; }
            i = next;
        }
        if (!any) return data;
        var output = new List<byte>(data.Length);
        output.AddRange(data.AsSpan(0, idx).ToArray());
        for (int i = idx; i >= 0;)
        {
            int next = FindStart(data, i + 3, out _);
            int end = next < 0 ? data.Length : next;
            if ((data[i + StartLen(data, i)] & 0x1F) != 12) output.AddRange(data.AsSpan(i, end - i).ToArray());
            i = next;
        }
        return output.ToArray();
    }

    /// <summary>Corta preenchimento no fim do quadro sem copiar (receptor, fluxo de versões antigas).</summary>
    public static ReadOnlySpan<byte> TrimTrailingFiller(ReadOnlySpan<byte> data)
    {
        while (true)
        {
            int last = -1;
            for (int i = data.Length - 4; i >= 0; i--)
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) { last = i; break; }
            if (last < 0 || (data[last + 3] & 0x1F) != 12) return data;
            int cut = last > 0 && data[last - 1] == 0 ? last - 1 : last; // código de início de 4 bytes
            if (cut <= 0) return data;
            data = data[..cut];
        }
    }

    // Posição do próximo código de início (00 00 01 ou 00 00 00 01) a partir de "from"; -1 se não houver.
    private static int FindStart(byte[] d, int from, out int len)
    {
        for (int i = Math.Max(0, from); i + 3 < d.Length; i++)
        {
            if (d[i] == 0 && d[i + 1] == 0)
            {
                if (d[i + 2] == 1) { len = 3; return i; }
                if (d[i + 2] == 0 && i + 3 < d.Length && d[i + 3] == 1) { len = 4; return i; }
            }
        }
        len = 0;
        return -1;
    }

    private static int StartLen(byte[] d, int i) => d[i + 2] == 1 ? 3 : 4;
}

internal static unsafe class ColorConvert
{
    public static void BgraToNv12(byte* bgra, int stride, byte* nv12, int w, int h)
    {
        byte* yPlane = nv12;
        byte* uvPlane = nv12 + (long)w * h;
        int pairs = h / 2;
        int bands = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        Parallel.For(0, bands, band =>
        {
            int start = pairs * band / bands, end = pairs * (band + 1) / bands;
            for (int py = start; py < end; py++)
            {
                int y0 = py * 2;
                byte* r0 = bgra + (long)y0 * stride;
                byte* r1 = r0 + stride;
                byte* yo0 = yPlane + (long)y0 * w;
                byte* yo1 = yo0 + w;
                byte* uv = uvPlane + (long)py * w;
                for (int x = 0; x < w; x += 2)
                {
                    int b00 = r0[0], g00 = r0[1], R00 = r0[2];
                    int b01 = r0[4], g01 = r0[5], R01 = r0[6];
                    int b10 = r1[0], g10 = r1[1], R10 = r1[2];
                    int b11 = r1[4], g11 = r1[5], R11 = r1[6];
                    yo0[x] = (byte)(((66 * R00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16);
                    yo0[x + 1] = (byte)(((66 * R01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16);
                    yo1[x] = (byte)(((66 * R10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16);
                    yo1[x + 1] = (byte)(((66 * R11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16);
                    int r = (R00 + R01 + R10 + R11 + 2) >> 2, g = (g00 + g01 + g10 + g11 + 2) >> 2, b = (b00 + b01 + b10 + b11 + 2) >> 2;
                    uv[x] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    uv[x + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                    r0 += 8; r1 += 8;
                }
            }
        });
    }

    private static readonly int[] YTab = BuildY();
    private static readonly int[] RvTab = Build(409), GuTab = Build(100), GvTab = Build(208), BuTab = Build(516);
    private static readonly byte[] Clamp = BuildClamp();

    private static int[] BuildY() { var t = new int[256]; for (int i = 0; i < 256; i++) t[i] = 298 * (i - 16) + 128; return t; }
    private static int[] Build(int k) { var t = new int[256]; for (int i = 0; i < 256; i++) t[i] = k * (i - 128); return t; }
    private static byte[] BuildClamp() { var t = new byte[1024 + 256]; for (int i = 0; i < t.Length; i++) t[i] = (byte)Math.Clamp(i - 512, 0, 255); return t; }

    public static void Nv12ToBgra(byte* yPlane, int yStride, byte* uvPlane, int uvStride, byte* bgra, int bgraStride, int w, int h)
    {
        int pairs = h / 2;
        int bands = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        Parallel.For(0, bands, band =>
        {
            fixed (int* yt = YTab, rv = RvTab, gu = GuTab, gv = GvTab, bu = BuTab)
            fixed (byte* cl = Clamp)
            {
                byte* c = cl + 512;
                int start = pairs * band / bands, end = pairs * (band + 1) / bands;
                for (int py = start; py < end; py++)
                {
                    byte* uv = uvPlane + (long)py * uvStride;
                    for (int line = 0; line < 2; line++)
                    {
                        int y = py * 2 + line;
                        byte* ys = yPlane + (long)y * yStride;
                        uint* o = (uint*)(bgra + (long)y * bgraStride);
                        for (int x = 0; x < w - 1; x += 2)
                        {
                            int u = uv[x], v = uv[x + 1];
                            int rr = rv[v], gg = gu[u] + gv[v], bb = bu[u];
                            int y0 = yt[ys[x]], y1 = yt[ys[x + 1]];
                            o[x] = 0xFF000000u | ((uint)c[(y0 + rr) >> 8] << 16) | ((uint)c[(y0 - gg) >> 8] << 8) | c[(y0 + bb) >> 8];
                            o[x + 1] = 0xFF000000u | ((uint)c[(y1 + rr) >> 8] << 16) | ((uint)c[(y1 - gg) >> 8] << 8) | c[(y1 + bb) >> 8];
                        }
                    }
                }
            }
        });
    }
}
