using System.Runtime.InteropServices;
using TransLocal.Video;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace TransLocal;

/// <summary>
/// Diagnóstico (--dxva-probe): testa combinações de configuração do decodificador H.264 do Windows até achar a que
/// decodifica pela placa de vídeo (amostras de saída em textura D3D11). Usado para descobrir por que ele recusa a placa.
/// </summary>
internal static unsafe class DxvaProbe
{
    public static int Run()
    {
        MF.Startup();
        // Fluxo real: alguns quadros codificados pelo codificador por software (não ocupa a placa).
        Environment.SetEnvironmentVariable("TRANSLOCAL_ENC_SOFTWARE", "1");
        const int w = 640, h = 360;
        var stream = new List<byte[]>();
        using (var enc = H264Encoder.TryCreate(w, h, 30, 2_000_000))
        {
            if (enc is null) { Log.Info("DXVAPROBE sem codificador"); return 1; }
            var img = new uint[w * h];
            fixed (uint* p = img)
            {
                for (int i = 0; i < 12; i++)
                {
                    for (int k = 0; k < img.Length; k++) img[k] = 0xFF000000u | (uint)((k * 7 + i * 13) & 0xFFFFFF);
                    var d = enc.Encode((IntPtr)p, w * 4, i == 0, out _);
                    if (d is { Length: > 0 }) stream.Add(d);
                }
            }
        }
        Environment.SetEnvironmentVariable("TRANSLOCAL_ENC_SOFTWARE", null);
        Log.Info($"DXVAPROBE fluxo de teste: {stream.Count} quadros");

        // Decodificador D3D11 direto (sem Media Foundation): a placa aceita criar uma sessão de decodificação H.264?
        using (var gpu = GpuDevice.TryCreate())
        {
            if (gpu is not null)
            {
                var vld = new Guid("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
                foreach (var (dw, dh) in new[] { (640, 368), (1366, 768), (1920, 1088) })
                {
                    try
                    {
                        var desc = new VideoDecoderDescription { Guid = vld, SampleWidth = (uint)dw, SampleHeight = (uint)dh, OutputFormat = Vortice.DXGI.Format.NV12 };
                        uint configs = gpu.VideoDevice.GetVideoDecoderConfigCount(desc);
                        string cfgs = "";
                        for (uint c = 0; c < configs; c++)
                        {
                            gpu.VideoDevice.GetVideoDecoderConfig(ref desc, c, out var cfg).CheckError();
                            cfgs += $" [{c}: bitstreamRaw={cfg.ConfigBitstreamRaw}]";
                        }
                        string created = "não tentado";
                        if (configs > 0)
                        {
                            gpu.VideoDevice.GetVideoDecoderConfig(ref desc, 0, out var cfg0).CheckError();
                            var r = gpu.VideoDevice.CreateVideoDecoder(desc, cfg0, out var dec);
                            created = r.Success ? "sim" : $"0x{r.Code:X8}";
                            dec?.Dispose();
                        }
                        Log.Info($"DXVAPROBE D3D11 direto {dw}×{dh}: {configs} configuração(ões){cfgs}; decodificador criado: {created}");
                    }
                    catch (Exception ex) { Log.Info($"DXVAPROBE D3D11 direto {dw}×{dh}: erro {ex.Message}"); }
                }
            }
        }

        int hits = 0;
        foreach (bool cocreate in new[] { false, true })
        foreach (bool bgra in new[] { false, true })
        foreach (bool mt in new[] { false, true })
        foreach (bool managerFirst in new[] { true, false })
        foreach (bool frameSize in new[] { false, true })
        foreach (bool lowLatency in new[] { false, true })
        {
            string name = $"cocreate={cocreate} bgra={bgra} multithread={mt} gerenciador-antes={managerFirst} tamanho={frameSize} baixa-latência={lowLatency}";
            try
            {
                var (gpu, flags, outputs) = Try(stream, w, h, cocreate, bgra, mt, managerFirst, frameSize, lowLatency);
                if (gpu) hits++;
                Log.Info($"DXVAPROBE {(gpu ? "PLACA" : "cpu  ")} flags=0x{flags:X} saídas={outputs} • {name}");
            }
            catch (Exception ex)
            {
                Log.Info($"DXVAPROBE erro   {ex.GetType().Name}: {ex.Message} • {name}");
            }
        }
        Log.Info($"DXVAPROBE concluído: {hits} combinação(ões) decodificam pela placa de vídeo");
        return 0;
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);

    private static (bool gpu, uint flags, int outputs) Try(List<byte[]> stream, int w, int h, bool cocreate, bool bgra, bool mt,
        bool managerFirst, bool frameSize, bool lowLatency)
    {
        IMFTransform mft;
        if (cocreate)
        {
            var obj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("62CE7E72-4C71-4d20-B15D-452831A87D9D"))!)!;
            var unk = Marshal.GetIUnknownForObject(obj);
            var iid = MF.IID_IMFTransform;
            Marshal.QueryInterface(unk, in iid, out var t);
            Marshal.Release(unk);
            mft = new IMFTransform(t);
        }
        else
        {
            var list = MF.Enum(TransformCategoryGuids.VideoDecoder, MF.ENUM_SYNC | MF.ENUM_SORT, MF.H264, MF.NV12);
            mft = MF.Activate(list[0]);
            foreach (var a in list) a.Dispose();
        }
        uint devFlags = 0x800 /* VIDEO_SUPPORT */ | (bgra ? 0x20u : 0u);
        int hr = D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, devFlags, IntPtr.Zero, 0, 7, out var devPtr, out _, out var ctxPtr);
        if (hr < 0) throw new COMException("D3D11CreateDevice", hr);
        Marshal.Release(ctxPtr);
        using var device = new ID3D11Device(devPtr);
        if (mt) using (var m = device.QueryInterface<ID3D11Multithread>()) m.SetMultithreadProtected(true);
        IMFDXGIDeviceManager? manager = null;
        try
        {
            if (lowLatency) mft.Attributes.Set(MF.LowLatency, 1u);
            void SetManager()
            {
                manager = MF.CreateDeviceManager(device);
                int r = MF.ProcessMessage(mft, MF.MSG_SET_D3D_MANAGER, (nuint)(ulong)manager.NativePointer);
                if (r < 0) throw new COMException("SET_D3D_MANAGER", r);
            }
            if (managerFirst) SetManager();
            var input = MediaFactory.MFCreateMediaType();
            input.Set(MF.MajorType, MF.Video);
            input.Set(MF.Subtype, MF.H264);
            if (frameSize) input.Set(MF.FrameSize, MF.Pack(w, h));
            hr = MF.SetInputType(mft, input);
            input.Dispose();
            if (hr < 0) throw new COMException("SetInputType", hr);
            SetNv12(mft);
            if (!managerFirst) SetManager();
            uint flags = MF.GetOutputStreamInfo(mft).Flags;
            MF.ProcessMessage(mft, MF.MSG_BEGIN_STREAMING);
            MF.ProcessMessage(mft, MF.MSG_START_OF_STREAM);
            bool gpu = false;
            int outputs = 0;
            foreach (var frame in stream)
            {
                var buffer = MediaFactory.MFCreateMemoryBuffer(frame.Length);
                buffer.Lock(out IntPtr p, out _, out _);
                Marshal.Copy(frame, 0, p, frame.Length);
                buffer.Unlock();
                buffer.CurrentLength = frame.Length;
                var sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(buffer);
                MF.ProcessInput(mft, sample);
                sample.Dispose();
                buffer.Dispose();
                for (int guard = 0; guard < 4; guard++)
                {
                    var si = MF.GetOutputStreamInfo(mft);
                    flags = si.Flags;
                    bool provides = (si.Flags & MF.OUTPUT_PROVIDES_SAMPLES) != 0;
                    IMFSample? own = null;
                    IMFMediaBuffer? ownBuf = null;
                    var data = new MF.OutputData();
                    if (!provides)
                    {
                        own = MediaFactory.MFCreateSample();
                        ownBuf = MediaFactory.MFCreateMemoryBuffer((int)Math.Max(si.Size, (uint)(w * h * 2)));
                        own.AddBuffer(ownBuf);
                        data.Sample = own.NativePointer;
                    }
                    int r = MF.ProcessOutput(mft, ref data);
                    if (r == MF.E_STREAM_CHANGE) { SetNv12(mft); continue; }
                    if (r < 0) { ownBuf?.Dispose(); own?.Dispose(); break; }
                    outputs++;
                    var outSample = own ?? new IMFSample(data.Sample);
                    using (var b = outSample.GetBufferByIndex(0))
                    using (var dx = b.QueryInterfaceOrNull<IMFDXGIBuffer>())
                        if (dx is not null && provides) gpu = true;
                    if (own is null) outSample.Dispose();
                    ownBuf?.Dispose();
                    own?.Dispose();
                    if (data.Events != IntPtr.Zero) Marshal.Release(data.Events);
                }
            }
            return (gpu, flags, outputs);
        }
        finally
        {
            try { MF.ProcessMessage(mft, MF.MSG_FLUSH); } catch { }
            mft.Dispose();
            manager?.Dispose();
        }
    }

    private static void SetNv12(IMFTransform mft)
    {
        for (int i = 0; i < 32; i++)
        {
            using var t = MF.GetOutputAvailableType(mft, i);
            if (t is null) break;
            if (t.GetGUID(MF.Subtype) == MF.NV12 && MF.SetOutputType(mft, t) >= 0) return;
        }
        throw new InvalidOperationException("sem NV12");
    }
}
