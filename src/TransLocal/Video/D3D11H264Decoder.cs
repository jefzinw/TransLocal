using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TransLocal.Video;

/// <summary>Leitor de bits de uma unidade H.264 (RBSP: sem os bytes de prevenção 0x03).</summary>
internal sealed class H264Bits
{
    private readonly byte[] _d;
    private readonly int _bits;
    private int _pos;

    public H264Bits(ReadOnlySpan<byte> nalPayload)
    {
        var d = new byte[nalPayload.Length];
        int n = 0, zeros = 0;
        foreach (byte b in nalPayload)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            zeros = b == 0 ? zeros + 1 : 0;
            d[n++] = b;
        }
        _d = d;
        _bits = n * 8;
    }

    public int Position => _pos;

    public uint U(int n)
    {
        uint v = 0;
        for (int i = 0; i < n; i++)
        {
            if (_pos >= _bits) throw new InvalidDataException("fim dos dados H.264");
            v = (v << 1) | (uint)((_d[_pos >> 3] >> (7 - (_pos & 7))) & 1);
            _pos++;
        }
        return v;
    }

    public bool Flag() => U(1) != 0;

    public uint Ue()
    {
        int zeros = 0;
        while (U(1) == 0) { if (++zeros > 31) throw new InvalidDataException("Exp-Golomb inválido"); }
        return zeros == 0 ? 0 : (uint)((1L << zeros) - 1 + U(zeros));
    }

    public int Se()
    {
        uint k = Ue();
        return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2);
    }

    /// <summary>Há dados além do bit de parada final (more_rbsp_data).</summary>
    public bool MoreRbspData()
    {
        int last = _bits - 1;
        while (last >= 0 && ((_d[last >> 3] >> (7 - (last & 7))) & 1) == 0) last--;
        return _pos < last;
    }
}

internal sealed class H264Sps
{
    public int Id, Profile, Level, ChromaFormat = 1, BitDepthLuma = 8, BitDepthChroma = 8;
    public int Log2MaxFrameNum, PocType, Log2MaxPocLsb, MaxRefFrames;
    public bool DeltaPicOrderAlwaysZero, FrameMbsOnly, Direct8x8, Gaps;
    public int WidthMbs, HeightMapUnits;
    public int HeightMbs => (FrameMbsOnly ? 1 : 2) * HeightMapUnits;

    public static H264Sps Parse(ReadOnlySpan<byte> payload, out string? unsupported)
    {
        unsupported = null;
        var r = new H264Bits(payload);
        var s = new H264Sps { Profile = (int)r.U(8) };
        r.U(8); // restrições
        s.Level = (int)r.U(8);
        s.Id = (int)r.Ue();
        if (s.Profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            s.ChromaFormat = (int)r.Ue();
            if (s.ChromaFormat == 3) r.U(1);
            s.BitDepthLuma = (int)r.Ue() + 8;
            s.BitDepthChroma = (int)r.Ue() + 8;
            r.U(1);
            if (r.Flag()) unsupported = "matriz de quantização própria";
        }
        s.Log2MaxFrameNum = (int)r.Ue() + 4;
        s.PocType = (int)r.Ue();
        if (s.PocType == 0) s.Log2MaxPocLsb = (int)r.Ue() + 4;
        else if (s.PocType == 1) unsupported ??= "contagem de quadros tipo 1";
        if (s.PocType == 1) return s;
        s.MaxRefFrames = (int)r.Ue();
        s.Gaps = r.Flag();
        s.WidthMbs = (int)r.Ue() + 1;
        s.HeightMapUnits = (int)r.Ue() + 1;
        s.FrameMbsOnly = r.Flag();
        if (!s.FrameMbsOnly) unsupported ??= "vídeo entrelaçado";
        if (!s.FrameMbsOnly) r.U(1);
        s.Direct8x8 = r.Flag();
        if (s.ChromaFormat != 1 || s.BitDepthLuma != 8 || s.BitDepthChroma != 8) unsupported ??= "formato de cor diferente de 4:2:0 8 bits";
        // Limites do padrão (e de qualquer placa): dados fora disso são inválidos — nunca viram tamanhos de textura.
        if (s.Id > 31 || s.PocType > 2 || s.Log2MaxFrameNum > 16 || s.Log2MaxPocLsb > 16 || s.MaxRefFrames > 16 ||
            s.WidthMbs > 512 || s.HeightMapUnits > 288)
            throw new InvalidDataException("SPS fora dos limites do H.264");
        return s;
    }
}

internal sealed class H264Pps
{
    public int Id, SpsId, NumRefIdxL0, NumRefIdxL1, WeightedBipredIdc, PicInitQp, PicInitQs, ChromaQpOffset, SecondChromaQpOffset;
    public bool Cabac, BottomFieldPicOrderPresent, WeightedPred, DeblockingControl, ConstrainedIntra, RedundantPicCnt, Transform8x8;

    public static H264Pps Parse(ReadOnlySpan<byte> payload, out string? unsupported)
    {
        unsupported = null;
        var r = new H264Bits(payload);
        var p = new H264Pps { Id = (int)r.Ue(), SpsId = (int)r.Ue(), Cabac = r.Flag(), BottomFieldPicOrderPresent = r.Flag() };
        if (r.Ue() != 0) { unsupported = "grupos de fatias (FMO)"; return p; }
        p.NumRefIdxL0 = (int)r.Ue() + 1;
        p.NumRefIdxL1 = (int)r.Ue() + 1;
        if (p.Id > 255 || p.SpsId > 31 || p.NumRefIdxL0 > 32 || p.NumRefIdxL1 > 32) throw new InvalidDataException("PPS fora dos limites do H.264");
        p.WeightedPred = r.Flag();
        p.WeightedBipredIdc = (int)r.U(2);
        p.PicInitQp = r.Se() + 26;
        p.PicInitQs = r.Se() + 26;
        p.ChromaQpOffset = r.Se();
        p.DeblockingControl = r.Flag();
        p.ConstrainedIntra = r.Flag();
        p.RedundantPicCnt = r.Flag();
        p.SecondChromaQpOffset = p.ChromaQpOffset;
        if (r.MoreRbspData())
        {
            p.Transform8x8 = r.Flag();
            if (r.Flag()) unsupported = "matriz de quantização própria";
            p.SecondChromaQpOffset = r.Se();
        }
        return p;
    }
}

/// <summary>Cabeçalho de fatia (só o que a decodificação pela placa precisa).</summary>
internal sealed class H264Slice
{
    public int FirstMb, Type, PpsId, FrameNum, PocLsb, DeltaPocBottom, NalRefIdc;
    public bool Idr;
    public bool Intra => Type % 5 is 2 or 4;

    /// <summary>MMCO 1 (diferenças) e 5 (limpa tudo); outros = sem suporte.</summary>
    public List<(int Op, int Diff)>? Mmco;

    public static H264Slice Parse(ReadOnlySpan<byte> nal, IReadOnlyDictionary<int, H264Sps> spss, IReadOnlyDictionary<int, H264Pps> ppss,
        bool full, out string? unsupported)
    {
        unsupported = null;
        var s = new H264Slice { NalRefIdc = (nal[0] >> 5) & 3, Idr = (nal[0] & 0x1F) == 5 };
        var r = new H264Bits(nal[1..]);
        s.FirstMb = (int)r.Ue();
        s.Type = (int)r.Ue();
        s.PpsId = (int)r.Ue();
        if (!full) return s;
        if (!ppss.TryGetValue(s.PpsId, out var pps) || !spss.TryGetValue(pps.SpsId, out var sps))
            throw new InvalidDataException("fatia sem SPS/PPS (falta quadro-chave)");
        s.FrameNum = (int)r.U(sps.Log2MaxFrameNum);
        if (s.Idr) r.Ue();
        if (sps.PocType == 0)
        {
            s.PocLsb = (int)r.U(sps.Log2MaxPocLsb);
            if (pps.BottomFieldPicOrderPresent) s.DeltaPocBottom = r.Se();
        }
        if (pps.RedundantPicCnt) r.Ue();
        int type = s.Type % 5;
        bool b = type == 1, p = type is 0 or 3;
        if (b) r.U(1);
        if (p || b)
        {
            if (r.Flag())
            {
                r.Ue();
                if (b) r.Ue();
            }
        }
        if (type is not 2 and not 4)
        {
            for (int list = 0; list < (b ? 2 : 1); list++)
            {
                if (!r.Flag()) continue;
                for (int guard = 0; guard < 64; guard++)
                {
                    uint idc = r.Ue();
                    if (idc == 3) break;
                    r.Ue();
                }
            }
        }
        if ((pps.WeightedPred && p) || (pps.WeightedBipredIdc == 1 && b))
        {
            unsupported = "predição ponderada";
            return s;
        }
        if (s.NalRefIdc != 0)
        {
            if (s.Idr)
            {
                r.U(1);
                if (r.Flag()) unsupported = "referência de longo prazo";
            }
            else if (r.Flag())
            {
                s.Mmco = new List<(int, int)>();
                for (int guard = 0; guard < 64; guard++)
                {
                    int op = (int)r.Ue();
                    if (op == 0) break;
                    int diff = 0;
                    if (op is 1 or 3) diff = (int)r.Ue() + 1;
                    if (op is 2) r.Ue();
                    if (op is 3 or 6) r.Ue();
                    if (op is 4) r.Ue();
                    if (op is not 1 and not 5) unsupported = "referência de longo prazo";
                    s.Mmco.Add((op, diff));
                }
            }
        }
        return s;
    }
}

/// <summary>
/// Decodificador H.264 direto pela placa de vídeo (D3D11VA, como o Moonlight faz pelo FFmpeg): o TransLocal lê os
/// cabeçalhos (SPS/PPS/fatias), monta os parâmetros DXVA de cada quadro e cuida dos quadros de referência; a placa
/// decodifica as fatias. Não depende do decodificador da Media Foundation, que em alguns PCs se recusa a usar a
/// placa e decodifica pelo processador — inviável a 60 q/s num notebook simples.
/// Suporta o que os codificadores de streaming geram: 4:2:0 8 bits, quadros progressivos, sem quadros de longo prazo.
/// </summary>
internal sealed unsafe class D3D11H264Decoder : IDisposable
{
    private static readonly Guid H264Vld = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PicParams
    {
        public ushort wFrameWidthInMbsMinus1, wFrameHeightInMbsMinus1;
        public byte CurrPic, num_ref_frames;
        public ushort wBitFields;
        public byte bit_depth_luma_minus8, bit_depth_chroma_minus8;
        public ushort Reserved16Bits;
        public uint StatusReportFeedbackNumber;
        public fixed byte RefFrameList[16];
        public fixed int CurrFieldOrderCnt[2];
        public fixed int FieldOrderCntList[32];
        public sbyte pic_init_qs_minus26, chroma_qp_index_offset, second_chroma_qp_index_offset;
        public byte ContinuationFlag;
        public sbyte pic_init_qp_minus26;
        public byte num_ref_idx_l0_active_minus1, num_ref_idx_l1_active_minus1, Reserved8BitsA;
        public fixed ushort FrameNumList[16];
        public uint UsedForReferenceFlags;
        public ushort NonExistingFrameFlags, frame_num;
        public byte log2_max_frame_num_minus4, pic_order_cnt_type, log2_max_pic_order_cnt_lsb_minus4, delta_pic_order_always_zero_flag,
            direct_8x8_inference_flag, entropy_coding_mode_flag, pic_order_present_flag, num_slice_groups_minus1, slice_group_map_type,
            deblocking_filter_control_present_flag, redundant_pic_cnt_present_flag, Reserved8BitsB;
        public ushort slice_group_change_rate_minus1;
        public fixed byte SliceGroupMap[810];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SliceShort
    {
        public uint BSNALunitDataLocation, SliceBytesInBuffer;
        public ushort wBadSliceChopping;
    }

    private sealed class Ref
    {
        public int Surface, FrameNum, PocTop, PocBottom;
    }

    private readonly GpuDevice _gpu;
    private readonly Dictionary<int, H264Sps> _sps = new();
    private readonly Dictionary<int, H264Pps> _pps = new();
    private readonly List<Ref> _dpb = new();
    private ID3D11VideoDecoder? _decoder;
    private ID3D11Texture2D? _surfaces;
    private ID3D11VideoDecoderOutputView[] _views = Array.Empty<ID3D11VideoDecoderOutputView>();
    private VideoDecoderConfig _config;
    private int _poolSize, _codedW, _codedH, _maxRefs;
    private uint _feedback;
    private int _prevPocMsb, _prevPocLsb, _prevFrameNum, _prevFrameNumOffset;
    private int _lastSurface = -1;

    public string Name { get; }

    /// <summary>Formato da sequência que esta decodificação não cobre: use o decodificador do Windows.</summary>
    public string? Unsupported { get; private set; }

    /// <summary>Resumo do último quadro (testes): tipo, frame_num, POC, referências.</summary>
    public string LastInfo { get; private set; } = "";

    private D3D11H264Decoder(GpuDevice gpu, string name)
    {
        _gpu = gpu.Acquire();
        Name = name;
    }

    /// <summary>A placa decodifica H.264 pelo D3D11 no formato de fatias curtas (verificação rápida, sem criar nada).</summary>
    public static bool IsSupported(GpuDevice gpu)
    {
        if (Environment.GetEnvironmentVariable("TRANSLOCAL_NO_D3D11VA") == "1") return false;
        try
        {
            bool has = false;
            for (uint i = 0; i < gpu.VideoDevice.VideoDecoderProfileCount; i++)
                if (gpu.VideoDevice.GetVideoDecoderProfile(i) == H264Vld) has = true;
            if (!has || !gpu.VideoDevice.CheckVideoDecoderFormat(H264Vld, Format.NV12)) return false;
            var desc = new VideoDecoderDescription { Guid = H264Vld, SampleWidth = 1920, SampleHeight = 1088, OutputFormat = Format.NV12 };
            uint count = gpu.VideoDevice.GetVideoDecoderConfigCount(desc);
            for (uint i = 0; i < count; i++)
            {
                gpu.VideoDevice.GetVideoDecoderConfig(desc, i, out var cfg).CheckError();
                if (cfg.ConfigBitstreamRaw == 2) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Null se a placa não decodifica H.264 pelo D3D11 no formato de fatias curtas.</summary>
    public static D3D11H264Decoder? TryCreate(GpuDevice gpu)
    {
        if (Environment.GetEnvironmentVariable("TRANSLOCAL_NO_D3D11VA") == "1") return null;
        try
        {
            bool has = false;
            for (uint i = 0; i < gpu.VideoDevice.VideoDecoderProfileCount; i++)
                if (gpu.VideoDevice.GetVideoDecoderProfile(i) == H264Vld) has = true;
            if (!has || !gpu.VideoDevice.CheckVideoDecoderFormat(H264Vld, Format.NV12)) return null;
            return new D3D11H264Decoder(gpu, $"D3D11VA ({gpu.AdapterName})");
        }
        catch (Exception ex)
        {
            Log.Info($"H.264: decodificação D3D11 indisponível ({ex.Message})");
            return null;
        }
    }

    private void EnsureDecoder(H264Sps sps)
    {
        int w = sps.WidthMbs * 16, h = sps.HeightMbs * 16;
        int refs = Math.Max(1, sps.MaxRefFrames);
        if (_decoder is not null && w == _codedW && h == _codedH && refs <= _maxRefs) return;
        ReleaseDecoder();
        var desc = new VideoDecoderDescription { Guid = H264Vld, SampleWidth = (uint)w, SampleHeight = (uint)h, OutputFormat = Format.NV12 };
        uint count = _gpu.VideoDevice.GetVideoDecoderConfigCount(desc);
        bool found = false;
        for (uint i = 0; i < count && !found; i++)
        {
            _gpu.VideoDevice.GetVideoDecoderConfig(desc, i, out var cfg).CheckError();
            if (cfg.ConfigBitstreamRaw == 2) { _config = cfg; found = true; }
        }
        if (!found) throw new NotSupportedException("a placa não oferece o formato de fatias curtas");
        _gpu.VideoDevice.CreateVideoDecoder(desc, _config, out _decoder).CheckError();
        _poolSize = Math.Clamp(refs + 3, 4, 32);
        _surfaces = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = (uint)_poolSize, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.Decoder,
        });
        _views = new ID3D11VideoDecoderOutputView[_poolSize];
        for (int i = 0; i < _poolSize; i++)
        {
            var vd = new VideoDecoderOutputViewDescription
            {
                DecodeProfile = H264Vld,
                ViewDimension = VideoDecoderOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoDecoderOutputView { ArraySlice = (uint)i },
            };
            _gpu.VideoDevice.CreateVideoDecoderOutputView(_surfaces, vd, out _views[i]).CheckError();
        }
        _codedW = w;
        _codedH = h;
        _maxRefs = refs;
        _dpb.Clear();
        Log.Info($"H.264 pela placa (D3D11VA): {w}×{h}, {refs} referência(s), {_poolSize} superfícies");
    }

    private void ReleaseDecoder()
    {
        foreach (var v in _views) v?.Dispose();
        _views = Array.Empty<ID3D11VideoDecoderOutputView>();
        _surfaces?.Dispose(); _surfaces = null;
        _decoder?.Dispose(); _decoder = null;
        _dpb.Clear();
    }

    /// <summary>
    /// Decodifica um quadro (Annex B) e o entrega à exibição. False sem imagem; <paramref name="error"/> = é preciso
    /// um quadro-chave (começou no meio, dado inválido). <see cref="Unsupported"/> preenchido = use outro decodificador.
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> annexB, int width, int height, IGpuFrameSink sink, out bool error)
    {
        error = false;
        try
        {
            // Unidades NAL do quadro.
            var slices = new List<(int Start, int Len)>();
            int i = 0;
            while (true)
            {
                int s = NextStart(annexB, i, out int scLen);
                if (s < 0) break;
                int nalStart = s + scLen;
                int next = NextStart(annexB, nalStart, out _);
                int nalEnd = next < 0 ? annexB.Length : next;
                while (nalEnd > nalStart && annexB[nalEnd - 1] == 0) nalEnd--; // zeros finais pertencem ao próximo código
                var nal = annexB[nalStart..nalEnd];
                if (nal.Length > 0)
                {
                    int type = nal[0] & 0x1F;
                    if (type == 7)
                    {
                        var sps = H264Sps.Parse(nal[1..], out var why);
                        if (why is not null) { Unsupported = why; return false; }
                        _sps[sps.Id] = sps;
                    }
                    else if (type == 8)
                    {
                        var pps = H264Pps.Parse(nal[1..], out var why);
                        if (why is not null) { Unsupported = why; return false; }
                        _pps[pps.Id] = pps;
                    }
                    else if (type is 1 or 5) slices.Add((nalStart, nalEnd - nalStart));
                }
                if (next < 0) break;
                i = next;
            }
            if (slices.Count == 0) return false;

            // Um pacote pode trazer mais de um quadro (o codificador entregou dois juntos): cada fatia com
            // first_mb_in_slice = 0 começa um quadro novo. Decodifica todos, na ordem, e mostra o último.
            var pictures = new List<List<(int Start, int Len)>>();
            foreach (var sl in slices)
            {
                var hdr = H264Slice.Parse(annexB.Slice(sl.Start, sl.Len), _sps, _pps, full: false, out _);
                if (hdr.FirstMb == 0 || pictures.Count == 0) pictures.Add(new List<(int, int)>());
                pictures[^1].Add(sl);
            }
            bool shownAny = false;
            for (int pi = 0; pi < pictures.Count; pi++)
            {
                if (!DecodePicture(annexB, pictures[pi], width, height, sink, pi == pictures.Count - 1, out error)) return shownAny && !error && Unsupported is null;
                shownAny = true;
            }
            return shownAny;
        }
        catch (InvalidDataException ex)
        {
            Log.Info($"H.264 (D3D11VA): {ex.Message}");
            error = true;
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Erro da placa ou dado inesperado: deixa este caminho e volta ao decodificador do Windows.
            Log.Warn("H.264 (D3D11VA): falha ao decodificar; usando o decodificador do Windows", ex);
            Unsupported = "falha na decodificação direta";
            error = true;
            return false;
        }
    }

    private bool DecodePicture(ReadOnlySpan<byte> annexB, List<(int Start, int Len)> slices, int width, int height, IGpuFrameSink sink,
        bool present, out bool error)
    {
        error = false;
        var first = H264Slice.Parse(annexB.Slice(slices[0].Start, slices[0].Len), _sps, _pps, full: true, out var unsupported);
        if (unsupported is not null) { Unsupported = unsupported; return false; }
        var ppsCur = _pps[first.PpsId];
        var spsCur = _sps[ppsCur.SpsId];
        bool intra = first.Intra;
        for (int k = 1; k < slices.Count && intra; k++)
            intra &= H264Slice.Parse(annexB.Slice(slices[k].Start, slices[k].Len), _sps, _pps, full: false, out _).Intra;
        if (_decoder is null && !first.Idr) { error = true; return false; } // começou no meio: espera o quadro-chave
        EnsureDecoder(spsCur);
        if (first.Idr) _dpb.Clear();

        // Contagem de ordem (POC).
        int pocTop, pocBottom;
        int maxFrameNum = 1 << spsCur.Log2MaxFrameNum;
        if (spsCur.PocType == 0)
        {
            if (first.Idr) { _prevPocMsb = 0; _prevPocLsb = 0; }
            int maxLsb = 1 << spsCur.Log2MaxPocLsb;
            int msb = _prevPocMsb;
            if (first.PocLsb < _prevPocLsb && _prevPocLsb - first.PocLsb >= maxLsb / 2) msb = _prevPocMsb + maxLsb;
            else if (first.PocLsb > _prevPocLsb && first.PocLsb - _prevPocLsb > maxLsb / 2) msb = _prevPocMsb - maxLsb;
            pocTop = msb + first.PocLsb;
            pocBottom = pocTop + first.DeltaPocBottom;
            if (first.NalRefIdc != 0) { _prevPocMsb = msb; _prevPocLsb = first.PocLsb; }
        }
        else
        {
            int offset = first.Idr ? 0 : _prevFrameNum > first.FrameNum ? _prevFrameNumOffset + maxFrameNum : _prevFrameNumOffset;
            int poc = first.Idr ? 0 : first.NalRefIdc == 0 ? 2 * (offset + first.FrameNum) - 1 : 2 * (offset + first.FrameNum);
            pocTop = pocBottom = poc;
            _prevFrameNumOffset = offset;
        }
        _prevFrameNum = first.FrameNum;

        // Superfície livre (fora das referências e diferente da última mostrada).
        int cur = -1;
        for (int s = 0; s < _poolSize && cur < 0; s++)
        {
            int cand = (_lastSurface + 1 + s) % _poolSize;
            if (cand != _lastSurface && !_dpb.Exists(r => r.Surface == cand)) cur = cand;
        }
        if (cur < 0) { _dpb.RemoveAt(0); cur = (_lastSurface + 1) % _poolSize; }

        // Parâmetros do quadro.
        var pp = default(PicParams);
        pp.wFrameWidthInMbsMinus1 = (ushort)(spsCur.WidthMbs - 1);
        pp.wFrameHeightInMbsMinus1 = (ushort)(spsCur.HeightMbs - 1);
        pp.CurrPic = (byte)cur;
        pp.num_ref_frames = (byte)Math.Max(1, spsCur.MaxRefFrames);
        int bits = (1 << 4)                                  // chroma_format_idc = 1 (4:2:0)
                   | (first.NalRefIdc != 0 ? 1 << 6 : 0)
                   | (ppsCur.ConstrainedIntra ? 1 << 7 : 0)
                   | (ppsCur.WeightedPred ? 1 << 8 : 0)
                   | ((ppsCur.WeightedBipredIdc & 3) << 9)
                   | (1 << 11)                               // MbsConsecutiveFlag (sem FMO)
                   | (spsCur.FrameMbsOnly ? 1 << 12 : 0)
                   | (ppsCur.Transform8x8 ? 1 << 13 : 0)
                   | (spsCur.Level >= 31 ? 1 << 14 : 0)
                   | (intra ? 1 << 15 : 0);
        pp.wBitFields = (ushort)bits;
        pp.Reserved16Bits = 3;
        pp.StatusReportFeedbackNumber = ++_feedback == 0 ? ++_feedback : _feedback;
        pp.CurrFieldOrderCnt[0] = pocTop;
        pp.CurrFieldOrderCnt[1] = pocBottom;
        for (int k = 0; k < 16; k++) pp.RefFrameList[k] = 0xFF;
        for (int k = 0; k < _dpb.Count && k < 16; k++)
        {
            var r = _dpb[k];
            pp.RefFrameList[k] = (byte)r.Surface;
            pp.FieldOrderCntList[k * 2] = r.PocTop;
            pp.FieldOrderCntList[k * 2 + 1] = r.PocBottom;
            pp.FrameNumList[k] = (ushort)r.FrameNum;
            pp.UsedForReferenceFlags |= 3u << (k * 2);
        }
        pp.pic_init_qs_minus26 = (sbyte)(ppsCur.PicInitQs - 26);
        pp.chroma_qp_index_offset = (sbyte)ppsCur.ChromaQpOffset;
        pp.second_chroma_qp_index_offset = (sbyte)ppsCur.SecondChromaQpOffset;
        pp.ContinuationFlag = 1;
        pp.pic_init_qp_minus26 = (sbyte)(ppsCur.PicInitQp - 26);
        pp.num_ref_idx_l0_active_minus1 = (byte)(ppsCur.NumRefIdxL0 - 1);
        pp.num_ref_idx_l1_active_minus1 = (byte)(ppsCur.NumRefIdxL1 - 1);
        pp.frame_num = (ushort)first.FrameNum;
        pp.log2_max_frame_num_minus4 = (byte)(spsCur.Log2MaxFrameNum - 4);
        pp.pic_order_cnt_type = (byte)spsCur.PocType;
        pp.log2_max_pic_order_cnt_lsb_minus4 = (byte)Math.Max(0, spsCur.Log2MaxPocLsb - 4);
        pp.delta_pic_order_always_zero_flag = 0;
        pp.direct_8x8_inference_flag = (byte)(spsCur.Direct8x8 ? 1 : 0);
        pp.entropy_coding_mode_flag = (byte)(ppsCur.Cabac ? 1 : 0);
        pp.pic_order_present_flag = (byte)(ppsCur.BottomFieldPicOrderPresent ? 1 : 0);
        pp.deblocking_filter_control_present_flag = (byte)(ppsCur.DeblockingControl ? 1 : 0);
        pp.redundant_pic_cnt_present_flag = (byte)(ppsCur.RedundantPicCnt ? 1 : 0);

        LastInfo = $"fatias={slices.Count} tipo={first.Type % 5} idr={first.Idr} ref={first.NalRefIdc} frame_num={first.FrameNum} poc={pocTop}/{pocBottom} " +
                   $"poc_tipo={spsCur.PocType} sup={cur} dpb=[{string.Join(",", _dpb.Select(r => $"s{r.Surface}:f{r.FrameNum}:p{r.PocTop}"))}] maxref={spsCur.MaxRefFrames} " +
                   $"l0={ppsCur.NumRefIdxL0} mmco={(first.Mmco is null ? "-" : string.Join("/", first.Mmco))}";
        Submit(annexB, slices, ref pp, cur, spsCur.WidthMbs * spsCur.HeightMbs);

        // Referências: limpa (IDR/MMCO 5), desmarca (MMCO 1) ou janela deslizante; depois inclui o quadro atual.
        if (first.NalRefIdc != 0)
        {
            if (first.Mmco is { Count: > 0 } ops)
            {
                foreach (var (op, diff) in ops)
                {
                    if (op == 5) _dpb.Clear();
                    else if (op == 1)
                    {
                        int picNum = first.FrameNum - diff;
                        _dpb.RemoveAll(r => Wrap(r.FrameNum, first.FrameNum, maxFrameNum) == picNum);
                    }
                }
            }
            else
            {
                while (_dpb.Count >= Math.Max(1, spsCur.MaxRefFrames))
                {
                    int oldest = 0;
                    for (int k = 1; k < _dpb.Count; k++)
                        if (Wrap(_dpb[k].FrameNum, first.FrameNum, maxFrameNum) < Wrap(_dpb[oldest].FrameNum, first.FrameNum, maxFrameNum)) oldest = k;
                    _dpb.RemoveAt(oldest);
                }
            }
            _dpb.Add(new Ref { Surface = cur, FrameNum = first.FrameNum, PocTop = pocTop, PocBottom = pocBottom });
        }
        _lastSurface = cur;

        if (!present) return true;
        int w = Math.Min(width, _codedW), h = Math.Min(height, _codedH);
        if (!sink.PresentNv12(_surfaces!, cur, w, h)) { error = true; return false; }
        return true;
    }

    private static int Wrap(int frameNum, int current, int max) => frameNum > current ? frameNum - max : frameNum;

    private void Submit(ReadOnlySpan<byte> annexB, List<(int Start, int Len)> slices, ref PicParams pp, int surface, int mbCount)
    {
        var vc = _gpu.VideoContext;
        // A placa pode ainda estar usando a superfície (E_PENDING): tenta de novo por alguns milissegundos.
        for (int attempt = 0; ; attempt++)
        {
            var r = vc.DecoderBeginFrame(_decoder!, _views[surface], 0, IntPtr.Zero);
            if (r.Success) break;
            if (r.Code != unchecked((int)0x8000000A) || attempt > 200) r.CheckError();
            Thread.Sleep(0);
        }
        try
        {
            // Parâmetros da imagem.
            GetBuffer(VideoDecoderBufferType.PictureParameters, out uint size, out IntPtr ptr);
            if (size < sizeof(PicParams)) throw new InvalidOperationException("buffer de parâmetros pequeno");
            *(PicParams*)ptr = pp;
            vc.ReleaseDecoderBuffer(_decoder!, VideoDecoderBufferType.PictureParameters).CheckError();

            // Matriz de quantização plana (padrão).
            const int QmSize = 6 * 16 + 2 * 64;
            GetBuffer((VideoDecoderBufferType)4, out size, out ptr);
            new Span<byte>((void*)ptr, QmSize).Fill(16);
            vc.ReleaseDecoderBuffer(_decoder!, (VideoDecoderBufferType)4).CheckError();

            // Fatias: código de início + NAL, completado com zeros até múltiplo de 128 (exigência de várias placas).
            GetBuffer(VideoDecoderBufferType.Bitstream, out uint bsSize, out IntPtr bsPtr);
            var bs = new Span<byte>((void*)bsPtr, (int)bsSize);
            var ctrl = new SliceShort[slices.Count];
            int pos = 0;
            for (int k = 0; k < slices.Count; k++)
            {
                var (start, len) = slices[k];
                if (pos + 3 + len > bs.Length) throw new InvalidDataException("quadro maior que o buffer da placa");
                bs[pos] = 0; bs[pos + 1] = 0; bs[pos + 2] = 1;
                annexB.Slice(start, len).CopyTo(bs[(pos + 3)..]);
                ctrl[k] = new SliceShort { BSNALunitDataLocation = (uint)pos, SliceBytesInBuffer = (uint)(3 + len) };
                pos += 3 + len;
            }
            int padded = Math.Min(bs.Length, (pos + 127) & ~127);
            bs[pos..padded].Clear();
            ctrl[^1].SliceBytesInBuffer += (uint)(padded - pos);
            vc.ReleaseDecoderBuffer(_decoder!, VideoDecoderBufferType.Bitstream).CheckError();

            GetBuffer(VideoDecoderBufferType.SliceControl, out size, out ptr);
            int ctrlBytes = slices.Count * sizeof(SliceShort);
            if (size < ctrlBytes) throw new InvalidOperationException("buffer de fatias pequeno");
            fixed (SliceShort* c = ctrl) Buffer.MemoryCopy(c, (void*)ptr, size, ctrlBytes);
            vc.ReleaseDecoderBuffer(_decoder!, VideoDecoderBufferType.SliceControl).CheckError();

            var descs = new[]
            {
                new VideoDecoderBufferDescription { BufferType = VideoDecoderBufferType.PictureParameters, DataSize = (uint)sizeof(PicParams) },
                new VideoDecoderBufferDescription { BufferType = (VideoDecoderBufferType)4, DataSize = QmSize },
                new VideoDecoderBufferDescription { BufferType = VideoDecoderBufferType.Bitstream, DataSize = (uint)padded, NumMBsInBuffer = (uint)mbCount },
                new VideoDecoderBufferDescription { BufferType = VideoDecoderBufferType.SliceControl, DataSize = (uint)ctrlBytes, NumMBsInBuffer = (uint)mbCount },
            };
            vc.SubmitDecoderBuffers(_decoder!, (uint)descs.Length, descs).CheckError();
        }
        finally
        {
            vc.DecoderEndFrame(_decoder!).CheckError();
        }
    }

    /// <summary>ID3D11VideoContext::GetDecoderBuffer (8º método da interface; a biblioteca não o expõe).</summary>
    private void GetBuffer(VideoDecoderBufferType type, out uint size, out IntPtr ptr)
    {
        IntPtr ctx = _gpu.VideoContext.NativePointer;
        uint s;
        IntPtr p;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, uint*, IntPtr*, int>)(*(void***)ctx)[7])(ctx, _decoder!.NativePointer, (int)type, &s, &p);
        if (hr < 0 || p == IntPtr.Zero) throw new COMException($"GetDecoderBuffer({type})", hr);
        size = s;
        ptr = p;
    }

    private static int NextStart(ReadOnlySpan<byte> d, int from, out int len)
    {
        for (int i = Math.Max(0, from); i + 2 < d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0) continue;
            if (d[i + 2] == 1) { len = 3; return i; }
            if (i + 3 < d.Length && d[i + 2] == 0 && d[i + 3] == 1) { len = 4; return i; }
        }
        len = 0;
        return -1;
    }

    public void Dispose()
    {
        ReleaseDecoder();
        _gpu.Dispose();
    }
}
