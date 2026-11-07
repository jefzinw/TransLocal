using System.Diagnostics;
using System.Runtime.InteropServices;
using TransLocal.Streaming;
using TransLocal.UI;
using TransLocal.Video;

namespace TransLocal;

/// <summary>Modos de diagnóstico: --bench, --selftest e --ui-screenshot.</summary>
internal static class DevTools
{
    /// <summary>Mede captura, codificação e decodificação no monitor principal.</summary>
    public static int Bench()
    {
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        Log.Info($"BENCH monitor {screen.DeviceName} {screen.Bounds} • {Environment.ProcessorCount} núcleos");

        foreach (var make in new Func<ScreenCapturer>[] { () => new GdiCapturer(screen.Bounds), () => ScreenCapturer.Create(screen) })
        {
            using var c = make();
            var ms = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                long t = Stopwatch.GetTimestamp();
                c.Capture(true);
                ms.Add(Ms(t));
                Thread.Sleep(10);
            }
            Log.Info($"BENCH captura {c.Method}: média {ms.Skip(5).Average():0.0} ms, máx {ms.Skip(5).Max():0.0} ms");
        }

        // Primeiras capturas logo após criar o capturador: precisam ter imagem (não preta).
        using var cap = ScreenCapturer.Create(screen);
        for (int i = 0; i < 4; i++)
        {
            long t = Stopwatch.GetTimestamp();
            bool ok = cap.Capture(false);
            double ms = Ms(t);
            Log.Info($"BENCH {cap.Method} captura nº {i + 1}: retorno={ok}, {ms:0.0} ms, pixels não pretos={NonBlackFraction(cap):P1}");
        }
        var enc = new TileEncoder(cap.Width, cap.Height);

        // Redução de resolução (transmissão leve) + codificação do quadro reduzido.
        foreach (var (mw, mh) in new[] { (1600, 900), (1366, 768), (1280, 720), (854, 480) })
        {
            var size = FrameScaler.Fit(new Size(cap.Width, cap.Height), mw, mh);
            var scaler = new FrameScaler(cap.Width, cap.Height, size.Width, size.Height);
            var scaleMs = new List<double>();
            for (int i = 0; i < 15; i++)
            {
                long t = Stopwatch.GetTimestamp();
                scaler.Scale(cap);
                scaleMs.Add(Ms(t));
            }
            var senc = new TileEncoder(size.Width, size.Height);
            long te = Stopwatch.GetTimestamp();
            using var sf = senc.Encode(scaler.Bits, 55, 1);
            double encMs = Ms(te);
            Log.Info($"BENCH redução para {size.Width}×{size.Height}: {scaleMs.Skip(3).Average():0.0} ms • quadro inteiro q55 {sf.Length / 1024} KiB em {encMs:0.0} ms");
        }

        // Detecção de mudanças com imagem controlada: mesma imagem => 0 faixas; um bloco alterado => 1 faixa.
        unsafe
        {
            int n = cap.Width * cap.Height * 4;
            var img = GC.AllocateUninitializedArray<byte>(n, pinned: true);
            new ReadOnlySpan<byte>((void*)cap.Bits, n).CopyTo(img);
            var p = Marshal.UnsafeAddrOfPinnedArrayElement(img, 0);
            using (enc.Encode(p, 75, 1)) { }
            using (var same = enc.Encode(p, 75, 2))
            {
                for (int y = 300; y < 310; y++)
                    for (int x = 500 * 4; x < 510 * 4; x++) img[y * cap.Width * 4 + x] ^= 0x55;
                using var one = enc.Encode(p, 75, 3);
                bool ok = same.TileCount == 0 && one.TileCount == 1;
                Log.Info($"BENCH detecção de mudanças: sem mudança={same.TileCount} faixas, 10×10 px alterados={one.TileCount} faixa ({one.Length} bytes) — {(ok ? "OK" : "FALHOU")}");
            }
        }

        foreach (var q in new[] { 55, 75, 85 })
        {
            var encMs = new List<double>();
            int bytes = 0;
            for (int i = 0; i < 12; i++)
            {
                enc.ForceFullFrame();
                long t = Stopwatch.GetTimestamp();
                using var f = enc.Encode(cap.Bits, q, i);
                encMs.Add(Ms(t));
                bytes = f.Length;
            }
            Log.Info($"BENCH quadro inteiro q{q}: média {encMs.Skip(2).Average():0.0} ms, {bytes / 1024} KiB " +
                     $"(≈{bytes * 8 * 30 / 1_000_000.0:0} Mbit/s a 30 quadros/s)");
        }

        // Tela parada: só o que muda (cursor/relógio) é codificado.
        enc.ForceFullFrame();
        using (enc.Encode(cap.Bits, 75, 0)) { }
        var incMs = new List<double>();
        int tiles = 0, incBytes = 0;
        for (int i = 0; i < 30; i++)
        {
            cap.Capture(true);
            long t = Stopwatch.GetTimestamp();
            using var f = enc.Encode(cap.Bits, 75, i);
            incMs.Add(Ms(t));
            tiles += f.TileCount;
            incBytes += f.Length;
        }
        Log.Info($"BENCH incremental (tela parada): média {incMs.Average():0.00} ms, {tiles} faixas e {incBytes / 1024} KiB em 30 quadros");

        // Decodificação de um quadro inteiro.
        enc.ForceFullFrame();
        using var full = enc.Encode(cap.Bits, 75, 1);
        using var surface = new FrameSurface();
        var decMs = new List<double>();
        for (int i = 0; i < 12; i++)
        {
            long t = Stopwatch.GetTimestamp();
            TileDecoder.Decode(full.Buffer, full.Length, surface);
            decMs.Add(Ms(t));
        }
        Log.Info($"BENCH decodificação quadro inteiro: média {decMs.Skip(2).Average():0.0} ms");
        return 0;
    }

    /// <summary>Lista de pareados só em memória (os testes não mexem nas configurações reais).</summary>
    private sealed class MemoryTrust : Security.ITrustStore
    {
        private readonly HashSet<string> _set = new();
        public bool IsTrusted(string fingerprint) { lock (_set) return _set.Contains(fingerprint); }
        public void Trust(string name, string fingerprint, string kind) { lock (_set) _set.Add(fingerprint); }
        public int Count { get { lock (_set) return _set.Count; } }
    }

    /// <summary>
    /// Teste completo pela interface local: segurança (TLS, pareamento, recusa de não pareados, código de
    /// autorização, aprovação pelo celular, bloqueio após falhas), vídeo, modo leve e encerramentos.
    /// </summary>
    public static int SelfTest(string outDir) => SelfTestAsync(outDir).GetAwaiter().GetResult();

    private static async Task<int> SelfTestAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        const int port = 52030, discoveryPort = 52031;
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"SELFTEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }

        // Aparelhos do teste: “PC” (chave persistida do Windows, como no app), “Notebook”, “Celular” e “Invasor”.
        using var pcId = Security.DeviceIdentity.LoadOrCreate();
        using var nbId = Security.DeviceIdentity.CreateEphemeral();
        using var phoneId = Security.DeviceIdentity.CreateEphemeral();
        using var evilId = Security.DeviceIdentity.CreateEphemeral();
        var pcTrust = new MemoryTrust();
        var nbTrust = new MemoryTrust();
        var phoneTrust = new MemoryTrust();
        string? pcCode = null, nbCode = null;
        bool pcAcceptsPairing = true;
        Security.SecureChannelOptions PcChannel() => new()
        {
            Identity = pcId, Trust = pcTrust, LocalName = "PC-Teste",
            ConfirmPairing = p => { pcCode = p.Code; return pcAcceptsPairing; },
        };
        Security.SecureChannelOptions NbChannel(bool allowPairing = true) => new()
        {
            Identity = nbId, Trust = nbTrust, LocalName = "Notebook-Teste",
            ConfirmPairing = allowPairing ? p => { nbCode = p.Code; return true; } : null,
        };

        var prefs = new ReceiverPrefs(0, 0, false);
        var totp = new Security.Totp(Security.Totp.NewSecret());
        var hub = new ApprovalHub { StatusProvider = () => new ApproverStatusMessage { PcName = "PC-Teste", RemoteAccessEnabled = true, TotpSecret = totp.Base32Secret } };
        using var server = new ReceiverServer(PcChannel, () => false, () => prefs, port: port, discoveryPort: discoveryPort);
        var started = new TaskCompletionSource<ReceiverSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SessionStarted += s => started.TrySetResult(s);
        server.SessionEnded += (_, reason) => ended.TrySetResult(reason);
        SenderSession? pulledSender = null;
        server.ApproverHandler = (conn, peer) => hub.Serve(conn, peer);
        server.PullHandler = (conn, peer) =>
        {
            var req = conn.ReceiveJson<StreamRequestMessage>(MsgType.StreamRequest);
            bool ok;
            string? error = null;
            if (!string.IsNullOrEmpty(req.Code)) ok = totp.Verify(req.Code, out error);
            else ok = hub.Request(peer, TimeSpan.FromSeconds(10), null).Approved;
            conn.SendJson(MsgType.StreamDecision, new StreamDecisionMessage { Approved = ok, Reason = error ?? "negado" });
            if (!ok) return false;
            pulledSender = SenderSession.StartOnConnection(conn, new SenderOptions { Name = "PC-Teste", Fps = 30, Quality = 60, Audio = false });
            return true;
        };
        server.Start();

        // --- Descoberta
        var found = await DiscoveryScanner.ScanAsync(discoveryPort, TimeSpan.FromSeconds(1.5), includeLoopback: true, CancellationToken.None);
        Check(found.Any(r => r.Name == "PC-Teste" && r.Id == pcId.ShortId && r.ProtocolVersion == Proto.Version),
            "descoberta UDP encontra o receptor (com ID da chave e versão do protocolo)");

        // --- 1ª conexão: aparelhos desconhecidos → pareamento com código igual nas duas telas
        var opt = new SenderOptions { Name = "Notebook-Teste", Fps = 30, Quality = 75, Audio = true };
        var sender = await SenderSession.ConnectAsync("127.0.0.1", port, opt, NbChannel(), CancellationToken.None);
        sender.StatsUpdated += t => Log.Info("SELFTEST [transmissor] " + t);
        var session = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(pcCode is not null && pcCode == nbCode, $"pareamento: o mesmo código nas duas telas ({pcCode} / {nbCode})");
        Check(pcTrust.IsTrusted(nbId.Fingerprint) && nbTrust.IsTrusted(pcId.Fingerprint), "pareamento salvo nos dois lados");
        Check(sender.ReceiverName == "PC-Teste" && session.SenderName == "Notebook-Teste", "nomes autenticados nos dois lados");

        // --- Segunda conexão já pareada (sem pedir código) enquanto ocupado: recusada por estar exibindo
        pcCode = nbCode = null;
        try
        {
            using var second = await SenderSession.ConnectAsync("127.0.0.1", port, opt, NbChannel(), CancellationToken.None);
            Check(false, "segundo transmissor deveria ser recusado");
        }
        catch (InvalidOperationException ex)
        {
            Check(pcCode is null && nbCode is null, $"reconexão de pareado sem novo código; ocupado recusado (“{ex.Message}”)");
        }

        await Task.Delay(3000);
        long frames = Interlocked.Read(ref session.FramesReceived);
        Check(frames >= 1, $"vídeo pela conexão criptografada: {frames} quadro(s) (tela parada só envia o que muda)");
        session.Surface.SavePng(Path.Combine(outDir, "selftest-recebido.png"));

        // --- Diagnóstico ao vivo e avisos
        var diagLines = session.Diag?.Recent(60) ?? Array.Empty<string>();
        Check(diagLines.Any(l => l.StartsWith("[Notebook-Teste]") && l.Contains("[ESTADO]") && l.Contains("captura:")),
            "diagnóstico ao vivo: o estado do transmissor chega ao receptor");
        Check(session.Diag?.FilePath is { } diagFile && File.Exists(diagFile) && new FileInfo(diagFile).Length > 0,
            $"diagnóstico salvo por sessão ({Path.GetFileName(session.Diag?.FilePath)})");
        var noticeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Notice += t => noticeTcs.TrySetResult(t);
        sender.SendNotice("Aviso de teste");
        string? noticeText = null;
        try { noticeText = await noticeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        Check(noticeText == "Aviso de teste", "aviso do transmissor aparece no receptor");
        Check(DiagFiles.Sanitize("de 192.168.1.43 em " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) == "de x.x.x.x em %USERPROFILE%",
            "relatório para o GitHub remove IP e pasta do usuário");
        Check(ConversionSelfTest(out var convDetail), "conversão de quadros 10 bits / HDR / RGBA para a transmissão" + convDetail);
        Check(ReedSolomonSelfTest(out var rsDetail), "correção de erros Reed-Solomon recupera pacotes perdidos" + rsDetail);
        foreach (double loss in new[] { 0.0, 0.05, 0.15 })
        {
            bool ok = UdpSelfTest(loss, out var udpDetail);
            Check(ok, $"vídeo por UDP cifrado{(loss > 0 ? $" com {loss:P0} de perda simulada" : "")}: {udpDetail}");
        }
        Check(SecureFrameSelfTest(out var sfDetail), "quadro da tela segura pela memória compartilhada (escrita do agente, leitura do TransLocal)" + sfDetail);
        var lan = NetInfo.GetLocalAddresses().FirstOrDefault(a => !a.Kind.StartsWith("Tailscale") && !a.Kind.StartsWith("ZeroTier"));
        Check(!NetInfo.IsSameLocalNetwork(System.Net.IPAddress.Parse("100.101.102.103")) &&
              !NetInfo.IsSameLocalNetwork(System.Net.IPAddress.Parse("8.8.8.8")) &&
              NetInfo.IsSameLocalNetwork(System.Net.IPAddress.Loopback) &&
              (lan is null || NetInfo.IsSameLocalNetwork(lan.Address)),
            "pareamento e descoberta só na rede local (Tailscale e internet recusados; rede de casa aceita)");
        string desk = Input.DesktopFollower.InputDesktopName();
        Check(desk.Equals("Default", StringComparison.OrdinalIgnoreCase), $"área de trabalho de entrada identificada (“{desk}”)");

        var senderEnded = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Ended += (_, reason) => senderEnded.TrySetResult(reason);
        session.Close();
        var reasonAtSender = await senderEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(reasonAtSender is not null, $"transmissor avisado do fim (“{reasonAtSender}”)");
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Surface.Dispose();

        // --- Pareamento recusado no PC: nada é salvo
        var evilTrust = new MemoryTrust();
        pcAcceptsPairing = false;
        try
        {
            await SenderSession.ConnectAsync("127.0.0.1", port, opt, new Security.SecureChannelOptions
            {
                Identity = evilId, Trust = evilTrust, LocalName = "Notebook-Teste", ConfirmPairing = _ => true,
            }, CancellationToken.None);
            Check(false, "pareamento recusado deveria impedir a conexão");
        }
        catch (Security.AuthException ex)
        {
            Check(!pcTrust.IsTrusted(evilId.Fingerprint), $"pareamento recusado no PC bloqueia o aparelho (“{ex.Message}”)");
        }
        pcAcceptsPairing = true;

        // --- Invasor não pareado pedindo a tela (acesso remoto): recusado sem chance de parear
        try
        {
            await RemotePull.RequestAsync("127.0.0.1", port, new Security.SecureChannelOptions
            {
                Identity = evilId, Trust = evilTrust, LocalName = "Invasor", ConfirmPairing = _ => true,
            }, null, _ => { }, CancellationToken.None);
            Check(false, "invasor não pareado deveria ser recusado");
        }
        catch (Security.AuthException ex)
        {
            Check(true, $"acesso remoto de aparelho não pareado recusado (“{ex.Message}”)");
        }

        // --- Acesso remoto com código de autorização errado e depois certo
        try
        {
            await RemotePull.RequestAsync("127.0.0.1", port, NbChannel(false), "000000", _ => { }, CancellationToken.None);
            Check(false, "código errado deveria ser recusado");
        }
        catch (Security.AuthException ex)
        {
            Check(true, $"código de autorização errado recusado (“{ex.Message}”)");
        }
        using var nbViewer = new ReceiverServer(() => NbChannel(false), () => false, () => new ReceiverPrefs(1366, 768, true));
        var pulledStarted = new TaskCompletionSource<ReceiverSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        nbViewer.SessionStarted += s => pulledStarted.TrySetResult(s);
        string usedCode = totp.Current();
        var (pullConn, pullPeer) = await RemotePull.RequestAsync("127.0.0.1", port, NbChannel(false), usedCode, _ => { }, CancellationToken.None);
        var pullTask = Task.Run(() => { try { nbViewer.ServeIncomingStream(pullConn, pullPeer, requestedByUs: true); } finally { pullConn.Dispose(); } });
        var pulled = await pulledStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(2000);
        Check(Interlocked.Read(ref pulled.FramesReceived) >= 1 && pulled.Surface.Width == 1366 && pulledSender?.Limits.Light == true,
            $"acesso remoto com código: tela do PC recebida no notebook em {pulled.Surface.Width}×{pulled.Surface.Height} (modo leve)");
        try
        {
            // O mesmo código de novo (pedir o "atual" outra vez poderia trazer o da janela seguinte, que é válido).
            await RemotePull.RequestAsync("127.0.0.1", port, NbChannel(false), usedCode, _ => { }, CancellationToken.None);
            Check(false, "código já usado deveria ser recusado");
        }
        catch (Security.AuthException)
        {
            Check(true, "código de autorização não pode ser reutilizado");
        }
        pulled.Close();
        await pullTask.WaitAsync(TimeSpan.FromSeconds(5));
        pulled.Surface.Dispose();
        pulledSender?.Stop();

        // --- Celular aprovador: pareia, recebe o pedido e aprova
        var phoneClient = new System.Net.Sockets.TcpClient();
        await phoneClient.ConnectAsync(System.Net.IPAddress.Loopback, port);
        pcCode = null;
        string? phoneCode = null;
        var (phoneConn, phonePeer) = await Task.Run(() => Security.SecureChannel.Connect(phoneClient, Security.ChannelPurpose.Approver,
            new Security.SecureChannelOptions
            {
                Identity = phoneId, Trust = phoneTrust, LocalName = "Celular-Teste", LocalKind = "phone",
                ConfirmPairing = p => { phoneCode = p.Code; return true; },
            }));
        var status = phoneConn.ReceiveJson<ApproverStatusMessage>(MsgType.ApproverStatus);
        Check(phoneCode is not null && phoneCode == pcCode && status.TotpSecret == totp.Base32Secret,
            $"celular pareado como aprovador (código {phoneCode}) e recebe o segredo do código de autorização");
        string? requestSeen = null;
        var phoneLoop = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    using var m = phoneConn.Receive();
                    if (m.Type != MsgType.ApprovalRequest) continue;
                    var r = m.ReadJson<ApprovalRequestMessage>();
                    if (r.Cancelled) continue;
                    requestSeen = r.DeviceName;
                    phoneConn.SendJson(MsgType.ApprovalResponse, new ApprovalResponseMessage { Id = r.Id, Approved = true });
                }
            }
            catch { }
        });
        pulledStarted = new TaskCompletionSource<ReceiverSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (pc2, peer2) = await RemotePull.RequestAsync("127.0.0.1", port, NbChannel(false), null, _ => { }, CancellationToken.None);
        var pullTask2 = Task.Run(() => { try { nbViewer.ServeIncomingStream(pc2, peer2, requestedByUs: true); } finally { pc2.Dispose(); } });
        var pulled2 = await pulledStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(requestSeen == "Notebook-Teste", $"pedido de acesso aprovado pelo celular (pedido de “{requestSeen}”)");
        pulled2.Close();
        await pullTask2.WaitAsync(TimeSpan.FromSeconds(5));
        pulled2.Surface.Dispose();
        pulledSender?.Stop();
        phoneConn.Dispose();

        // --- Controles de jogo e entrada
        var padIn = new Input.GamepadState(2, true, 0x1F3F, 10, 250, -32768, 32767, 1234, -4321);
        Check(Input.GamepadCodec.TryDecode(Input.GamepadCodec.Encode(padIn), out var padOut) && padOut == padIn, "controle de jogo: codificação ida e volta");
        Check(Input.InputCodec.EncodeRelativeMove(-5, 7).Length == 5 && Input.InputCodec.EncodeButtonOnly(MouseButtons.Left, true)[0] == Input.InputCodec.ButtonOnly,
            "mensagens de mouse relativo e botão sem posição");
        var sdl = Input.ControllerService.Instance;
        Check(sdl.Available, $"SDL3 carregada para controles USB/Bluetooth{(sdl.Available ? "" : " — " + sdl.Error)}");

        // --- Cliente antigo (sem TLS) e bloqueio após falhas repetidas
        for (int i = 0; i < 5; i++)
        {
            using var old = new System.Net.Sockets.TcpClient();
            await old.ConnectAsync(System.Net.IPAddress.Loopback, port);
            var s = old.GetStream();
            s.Write(new byte[] { (byte)MsgType.Hello, 2, 0, 0, 0, (byte)'{', (byte)'}' });
            try { s.ReadTimeout = 3000; s.ReadByte(); } catch { }
        }
        await Task.Delay(300);
        bool blocked;
        try
        {
            await RemotePull.RequestAsync("127.0.0.1", port, NbChannel(false), totp.Current(), _ => { }, CancellationToken.None);
            blocked = false;
        }
        catch (Exception ex)
        {
            blocked = ex is Security.AuthException or IOException;
        }
        Check(blocked, "endereço bloqueado depois de 5 conexões inválidas (inclusive do aparelho pareado)");

        server.Stop();
        Input.ControllerService.Shutdown();
        Log.Info(failures == 0 ? "SELFTEST concluído: tudo certo" : $"SELFTEST concluído com {failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Escreve um quadro como o agente faria e lê do lado do TransLocal; nomes que não são do TransLocal são recusados.
    /// </summary>
    private static unsafe bool SecureFrameSelfTest(out string detail)
    {
        detail = "";
        const int w = 64, h = 32;
        using var reader = new Input.SecureFrameBuffer(w, h);
        if (Input.SecureFrameBuffer.OpenForWriting(@"Local\OutroPrograma.Quadro") is not null) { detail = " — aceitou nome estranho"; return false; }
        using var writer = Input.SecureFrameBuffer.OpenForWriting(reader.Name);
        if (writer is null) { detail = " — agente não abriu a área"; return false; }
        var src = new uint[w * h];
        for (int i = 0; i < src.Length; i++) src[i] = 0xFF000000u | (uint)i;
        var dst = new uint[w * h];
        fixed (uint* s = src, d = dst)
        {
            if (reader.TryRead((byte*)d, out _)) { detail = " — leu quadro antes de existir"; return false; }
            writer.Write((IntPtr)s, w, h, secureDesktop: true);
            if (!reader.TryRead((byte*)d, out bool secure) || !secure) { detail = " — quadro não lido"; return false; }
            if (reader.TryRead((byte*)d, out _)) { detail = " — leu o mesmo quadro duas vezes"; return false; }
            writer.Write((IntPtr)s, w + 1, h, secureDesktop: true);
            if (reader.TryRead((byte*)d, out _)) { detail = " — aceitou tamanho errado"; return false; }
        }
        for (int i = 0; i < src.Length; i++) if (src[i] != dst[i]) { detail = $" — pixel {i} diferente"; return false; }
        return true;
    }

    /// <summary>
    /// Converte pixels conhecidos de cada formato que o DuplicateOutput1 pode entregar.
    /// </summary>
    private static unsafe bool ConversionSelfTest(out string detail)
    {
        var results = new List<string>();
        bool ok = true;
        void One(string name, Vortice.DXGI.Format fmt, ReadOnlySpan<byte> src, int pitch, uint expected)
        {
            uint got = 0;
            fixed (byte* s = src) DxgiCapturer.Convert(s, pitch, fmt, (byte*)&got, 1, 1);
            if (got != expected) { ok = false; results.Add($"{name}: {got:X8} ≠ {expected:X8}"); }
        }
        // R10G10B10A2: R=1023 G=512 B=0 → BGRA FF FF80 00
        uint r10 = 1023u | (512u << 10) | (0u << 20) | (3u << 30);
        One("R10G10B10A2", Vortice.DXGI.Format.R10G10B10A2_UNorm, BitConverter.GetBytes(r10), 4, 0xFFFF8000);
        // R8G8B8A8: R=FF G=10 B=20 → BGRA (A FF, R FF, G 10, B 20)
        One("R8G8B8A8", Vortice.DXGI.Format.R8G8B8A8_UNorm, new byte[] { 0xFF, 0x10, 0x20, 0xFF }, 4, 0xFFFF1020);
        // FP16 linear: R=1.0 G=0.5 B=0 → sRGB 255, 188, 0
        var half = new byte[8];
        BitConverter.TryWriteBytes(half.AsSpan(0), (ushort)0x3C00);
        BitConverter.TryWriteBytes(half.AsSpan(2), (ushort)0x3800);
        BitConverter.TryWriteBytes(half.AsSpan(4), (ushort)0x0000);
        BitConverter.TryWriteBytes(half.AsSpan(6), (ushort)0x3C00);
        One("R16G16B16A16F", Vortice.DXGI.Format.R16G16B16A16_Float, half, 8, 0xFFFFBC00);
        detail = ok ? "" : " — " + string.Join("; ", results);
        return ok;
    }

    /// <summary>
    /// --codec-test: 180 quadros 1366×768 com movimento (tela atual + faixa e quadro andando, como num jogo),
    /// codificados em H.264 e decodificados de volta. Mede tempo, taxa a 60 q/s, fidelidade e compara com JPEG.
    /// </summary>
    /// <summary>
    /// Transmissão de ponta a ponta como no uso real (--stream-test): transmissor e receptor nesta máquina, imagem
    /// sintética em movimento (não captura a tela), codificador da placa, UDP com correção, janela de exibição de
    /// verdade (fora da tela, sem foco) decodificando pela placa. Mede quadros/s exibidos e o atraso.
    /// </summary>
    /// <summary>
    /// Captura real pela placa (--capture-test): o PC transmite a própria tela (DXGI → cursor → redução/conversão →
    /// codificador, tudo na placa) e o receptor decodifica na memória. A imagem recebida é comparada numericamente com
    /// uma foto da tela tirada no mesmo instante — nenhuma imagem é salva nem mostrada.
    /// </summary>
    public static unsafe int CaptureTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"CAPTURETEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }
        const int port = 52044, discoveryPort = 52045;
        using var pcId = Security.DeviceIdentity.CreateEphemeral();
        using var nbId = Security.DeviceIdentity.CreateEphemeral();
        var prefs = new ReceiverPrefs(0, 0, false);
        using var server = new ReceiverServer(() => new Security.SecureChannelOptions { Identity = nbId, Trust = new MemoryTrust(), LocalName = "Notebook-Teste", ConfirmPairing = _ => true },
            () => false, () => prefs, port: port, discoveryPort: discoveryPort);
        ReceiverSession? session = null;
        server.SessionStarted += s => session = s;
        server.Start();
        var screen = Screen.PrimaryScreen!;
        var opt = new SenderOptions { Name = "PC-Teste", Fps = 30, Quality = 92, Audio = false, Resolution = "original", MonitorDeviceName = screen.DeviceName };
        string lastStats = "";
        using var sender = SenderSession.ConnectAsync("127.0.0.1", port, opt,
            new Security.SecureChannelOptions { Identity = pcId, Trust = new MemoryTrust(), LocalName = "PC-Teste", ConfirmPairing = _ => true }, CancellationToken.None)
            .GetAwaiter().GetResult();
        sender.StatsUpdated += t => { lastStats = t; Log.Info("CAPTURETEST [PC] " + t); };
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 5000) Thread.Sleep(50);
        Check(session is not null && Interlocked.Read(ref session.FramesReceived) > 0, $"imagem da tela recebida ({session?.FramesReceived ?? 0} quadro(s))");
        Check(lastStats.Contains("toda na placa"), "captura → cursor → redução/conversão → codificação toda na placa de vídeo");
        if (session is not null && session.Surface.Width == screen.Bounds.Width)
        {
            // Foto da tela agora (a área de trabalho costuma estar parada) e comparação com o que chegou.
            using var bmp = new Bitmap(screen.Bounds.Width, screen.Bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(screen.Bounds.Location, Point.Empty, bmp.Size);
            var data = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            double mse = 0;
            int n = 0;
            try
            {
                for (int y = 0; y < bmp.Height; y += 3)
                {
                    uint* row = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                    for (int x = 0; x < bmp.Width; x += 3)
                    {
                        int a = (int)((row[x] >> 8) & 0xFF), b = (int)((session.Surface.PixelAt(x, y) >> 8) & 0xFF);
                        mse += (a - b) * (a - b);
                        n++;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            double psnr = mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (mse / n));
            Check(psnr > 28, $"imagem recebida igual à tela: {psnr:0.0} dB (cores e posição pela placa; partes que mudaram no instante baixam a nota)");
        }
        else Check(false, $"tamanho recebido {session?.Surface.Width}×{session?.Surface.Height} diferente da tela {screen.Bounds.Width}×{screen.Bounds.Height}");
        sender.Dispose();
        Thread.Sleep(300);
        Log.Info(failures == 0 ? "CAPTURETEST concluído: tudo certo" : $"CAPTURETEST concluído com {failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    public static int StreamTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"STREAMTEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }
        Environment.SetEnvironmentVariable("TRANSLOCAL_TEST_PATTERN", "1");
        const int port = 52040, discoveryPort = 52041;
        using var pcId = Security.DeviceIdentity.CreateEphemeral();
        using var nbId = Security.DeviceIdentity.CreateEphemeral();
        var pcTrust = new MemoryTrust();
        var nbTrust = new MemoryTrust();
        var prefs = new ReceiverPrefs(1366, 768, true); // notebook pedindo transmissão leve, como o do usuário
        using var server = new ReceiverServer(() => new Security.SecureChannelOptions { Identity = nbId, Trust = nbTrust, LocalName = "Notebook-Teste", ConfirmPairing = _ => true },
            () => false, () => prefs, port: port, discoveryPort: discoveryPort);
        ReceiverSession? session = null;
        server.SessionStarted += s => session = s;
        server.Start();
        var opt = new SenderOptions { Name = "PC-Teste", Fps = 60, Quality = 92, Audio = false, Resolution = "auto" };
        var stats = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var sender = SenderSession.ConnectAsync("127.0.0.1", port, opt,
            new Security.SecureChannelOptions { Identity = pcId, Trust = pcTrust, LocalName = "PC-Teste", ConfirmPairing = _ => true }, CancellationToken.None)
            .GetAwaiter().GetResult();
        sender.StatsUpdated += t => { stats.Enqueue(t); Log.Info("STREAMTEST [PC] " + t); };
        var wait = Stopwatch.StartNew();
        while (session is null && wait.ElapsedMilliseconds < 10_000) { Application.DoEvents(); Thread.Sleep(10); }
        if (session is null) { Check(false, "o receptor não abriu a sessão"); return 1; }
        string receiverStats = "";
        session.StatsUpdated += t => { receiverStats = t; Log.Info("STREAMTEST [notebook] " + t); };

        var hidden = SystemInformation.VirtualScreen;
        var viewer = new ViewerForm("TESTE", session.Surface, null, Screen.PrimaryScreen!, fullscreen: false, showStats: true, new Size(1366, 768))
        { NoActivate = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        viewer.Location = new Point(hidden.Right + 200, hidden.Bottom + 200);
        viewer.ClientSize = new Size(1366, 808);
        session.FrameReady += viewer.NotifyFrame;
        session.StatsUpdated += viewer.SetStats;
        viewer.Show();
        bool gpu = session.Gpu is { } dev && viewer.UseGpu(dev, s => session.GpuSink = s);
        Check(gpu, "janela exibindo pela placa de vídeo");

        // Aquecimento (quadro-chave, troca para a decodificação pela placa), depois 8 s de medição.
        var warm = Stopwatch.StartNew();
        while (warm.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(5); }
        long f0 = Interlocked.Read(ref session.FramesReceived);
        var measure = Stopwatch.StartNew();
        while (measure.ElapsedMilliseconds < 8000) { Application.DoEvents(); Thread.Sleep(5); }
        double fps = (Interlocked.Read(ref session.FramesReceived) - f0) / measure.Elapsed.TotalSeconds;
        Check(session.CodecLabel.Contains("placa de vídeo"), $"decodificação: {session.CodecLabel}");
        Check(fps >= 55, $"{fps:0.0} quadros/s exibidos no receptor (meta 60)");
        var last = stats.ToArray().Skip(3).ToArray();
        string latency = last.Length > 0 ? last[^1] : "";
        Check(latency.Contains("UDP"), $"transporte: {(latency.Contains("UDP") ? "UDP com correção de erros" : "TCP")}");
        Log.Info($"STREAMTEST resumo do PC: {latency}");
        Log.Info($"STREAMTEST resumo do notebook: {receiverStats}");
        viewer.Close();
        viewer.Dispose();
        sender.Dispose();
        Thread.Sleep(300);
        Environment.SetEnvironmentVariable("TRANSLOCAL_TEST_PATTERN", null);
        Log.Info(failures == 0 ? "STREAMTEST concluído: tudo certo" : $"STREAMTEST concluído com {failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Apaga pedaços ao acaso (até o limite da paridade) e confere que os dados voltam iguais.</summary>
    private static bool ReedSolomonSelfTest(out string detail)
    {
        var rng = new Random(7);
        int cases = 0;
        foreach (var (k, m) in new[] { (1, 1), (5, 1), (10, 3), (48, 10), (48, 24) })
        {
            for (int rep = 0; rep < 20; rep++)
            {
                int size = 64;
                var shards = new byte[k + m][];
                for (int i = 0; i < k + m; i++) { shards[i] = new byte[size]; if (i < k) rng.NextBytes(shards[i]); }
                var original = shards.Take(k).Select(x => (byte[])x.Clone()).ToArray();
                Streaming.ReedSolomon.Encode(shards, k, m, size);
                var present = Enumerable.Repeat(true, k + m).ToArray();
                foreach (int idx in Enumerable.Range(0, k + m).OrderBy(_ => rng.Next()).Take(m)) { present[idx] = false; Array.Clear(shards[idx]); }
                if (!Streaming.ReedSolomon.Recover(shards, present, k, m, size)) { detail = $" (falhou em k={k}, m={m})"; return false; }
                for (int i = 0; i < k; i++)
                    if (!shards[i].AsSpan().SequenceEqual(original[i])) { detail = $" (dados errados em k={k}, m={m})"; return false; }
                cases++;
            }
        }
        detail = $" ({cases} casos, até 24 de 72 pedaços apagados)";
        return true;
    }

    /// <summary>Transmissão UDP de verdade (loopback): quadros de vários tamanhos, cifrados, com perda simulada.</summary>
    private static bool UdpSelfTest(double loss, out string detail)
    {
        Environment.SetEnvironmentVariable("TRANSLOCAL_UDP_LOSS", loss.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var sender = new Streaming.UdpVideoSender(System.Net.IPAddress.Loopback) { Fec = loss > 0.1 ? 0.5 : loss > 0 ? 0.3 : 0.1 };
        Environment.SetEnvironmentVariable("TRANSLOCAL_UDP_LOSS", null);
        using var receiver = new Streaming.UdpVideoReceiver(sender.Key, sender.Session, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sender.Port));
        var got = new System.Collections.Concurrent.ConcurrentDictionary<int, byte[]>();
        receiver.FrameReady += f => got[f.Id] = f.Data.AsSpan(0, f.Length).ToArray();
        var ready = Stopwatch.StartNew();
        while (!sender.Active && ready.ElapsedMilliseconds < 3000) Thread.Sleep(10);
        if (!sender.Active) { detail = "o receptor não se apresentou por UDP"; return false; }
        var rng = new Random(11);
        var frames = new Dictionary<int, byte[]>();
        for (int id = 1; id <= 200; id++)
        {
            var data = new byte[id % 50 == 1 ? 120_000 : rng.Next(500, 25_000)]; // quadro-chave grande de vez em quando
            rng.NextBytes(data);
            frames[id] = data;
            sender.SendFrame(id, id % 50 == 1, 1366, 768, data);
            Thread.Sleep(1);
        }
        Thread.Sleep(300);
        int intact = frames.Count(f => got.TryGetValue(f.Key, out var g) && g.AsSpan().SequenceEqual(f.Value));
        int wrong = frames.Count(f => got.TryGetValue(f.Key, out var g) && !g.AsSpan().SequenceEqual(f.Value));
        long recovered = Interlocked.Read(ref receiver.FramesRecovered);
        detail = $"{intact}/200 quadros chegaram inteiros ({recovered} reconstruídos pela paridade), {wrong} corrompidos";
        return wrong == 0 && intact >= (loss == 0 ? 200 : loss < 0.1 ? 194 : 180) && (loss == 0 || recovered > 0);
    }

    /// <summary>
    /// Caminho da placa de vídeo de ponta a ponta: codifica, decodifica (DXVA) e mostra numa janela de verdade pela
    /// cadeia de troca, sem a imagem passar pelo processador; depois fotografa a tela (composição do Windows) e
    /// compara com o original. Também confere os textos por cima, o caminho BGRA e o redimensionamento.
    /// </summary>
    /// <summary>
    /// Compara, quadro a quadro, a decodificação direta (D3D11VA) com a do Windows no mesmo fluxo (--d3d11va-verify).
    /// Registra os detalhes do primeiro quadro que diverge.
    /// </summary>
    public static unsafe int D3D11VaVerify()
    {
        const int w = 960, h = 540;
        int frames = int.TryParse(Environment.GetEnvironmentVariable("TRANSLOCAL_VERIFY_FRAMES"), out var nf) ? nf : 150;
        using var gpu = GpuDevice.TryCreate();
        if (gpu is null) return 1;
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(w, h), ShowInTaskbar = false };
        _ = form.Handle;
        using var presenter = GpuPresenter.TryCreate(gpu, form.Handle, w, h)!;
        uint[]? shown = null;
        presenter.BeforePresent = back =>
        {
            var d = back.Description;
            using var staging = gpu.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
            {
                Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Staging,
                CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read,
            });
            gpu.Context.CopyResource(staging, back);
            var map = gpu.Context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var img = new uint[w * h];
                for (int y = 0; y < h; y++)
                    fixed (uint* dst = &img[y * w]) Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst, w * 4L, w * 4L);
                shown = img;
            }
            finally { gpu.Context.Unmap(staging, 0); }
        };
        bool high = Environment.GetEnvironmentVariable("TRANSLOCAL_H264_PROFILE") == "100";
        using var enc = H264Encoder.TryCreate(w, h, 60, 6_000_000, high: high);
        using var reference = H264Decoder.TryCreate(hardware: false);
        using var d3d = D3D11H264Decoder.TryCreate(gpu);
        using var surface = new FrameSurface();
        if (enc is null || reference is null || d3d is null) { Log.Info("VERIFY sem codificador/decodificadores"); return 1; }
        Log.Info($"VERIFY codificador {enc.Name}, perfil {enc.Profile}");
        var rng = new Random(3);
        var src = new uint[w * h];
        int bad = 0, compared = 0;
        double worst = 99;
        fixed (uint* f = src)
        {
            for (int i = 0; i < frames; i++)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int xs = x + i * 7;
                        f[y * w + x] = 0xFF000000u | (uint)(((xs * 255 / w) & 0xFF) << 16) | (uint)((y * 255 / h) << 8) | (uint)((((xs >> 3) ^ (y >> 3)) * 37) & 0xFF);
                    }
                var data = enc.Encode((IntPtr)f, w * 4, i == 0, out _);
                if (data is null) { Thread.Sleep(2); data = enc.Poll(out _); }
                if (data is null) continue;
                bool okRef = reference.Decode(data, w, h, surface, out _);
                shown = null;
                bool okHw = d3d.Decode(data, w, h, presenter, out bool err);
                if (!okRef || !okHw || shown is null) { Log.Info($"VERIFY quadro {i}: ref={okRef} d3d={okHw} erro={err} {d3d.Unsupported} • {d3d.LastInfo}"); continue; }
                double mse = 0;
                int n = 0;
                for (int y = 0; y < h; y += 2)
                    for (int x = 0; x < w; x += 2)
                    {
                        int a = (int)((surface.PixelAt(x, y) >> 8) & 0xFF), b = (int)((shown[y * w + x] >> 8) & 0xFF);
                        mse += (a - b) * (a - b);
                        n++;
                    }
                double psnr = mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (mse / n));
                compared++;
                worst = Math.Min(worst, psnr);
                if (psnr < 38)
                {
                    bad++;
                    if (bad <= 6) Log.Info($"VERIFY quadro {i}: {psnr:0.0} dB • {d3d.LastInfo}");
                }
                else if (i < 4 || i % 40 == 0) Log.Info($"VERIFY quadro {i}: {psnr:0.0} dB (ok) • {d3d.LastInfo}");
            }
        }
        Log.Info($"VERIFY concluído: {compared} comparados, {bad} divergentes, pior {worst:0.0} dB");
        return bad == 0 ? 0 : 1;
    }

    public static unsafe int GpuTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"GPUTEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }
        const int w = 960, h = 540, fps = 60, frames = 120;
        // Janela fora da área visível: o teste não aparece por cima de nada (pode haver um jogo transmitindo).
        var hidden = SystemInformation.VirtualScreen;
        var baseFrame = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                baseFrame[y * w + x] = 0xFF000000u | (uint)((x * 255 / w) << 16) | (uint)((y * 255 / h) << 8) | (uint)(((x ^ y) >> 2) & 0xFF);

        using var gpu = GpuDevice.TryCreate();
        Check(gpu is not null, $"dispositivo de vídeo: {gpu?.AdapterName}");
        if (gpu is null) return 1;
        {
            var h264Vld = new Guid("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
            uint count = gpu.VideoDevice.VideoDecoderProfileCount;
            bool has = false;
            for (uint i = 0; i < count; i++)
                if (gpu.VideoDevice.GetVideoDecoderProfile(i) == h264Vld) has = true;
            bool nv12 = false;
            try { nv12 = gpu.VideoDevice.CheckVideoDecoderFormat(h264Vld, Vortice.DXGI.Format.NV12); } catch { }
            Log.Info($"GPUTEST decodificação pela placa: {count} perfis, H.264 VLD={has}, saída NV12={nv12}");
            foreach (uint fl in new[] { MF.ENUM_HARDWARE | MF.ENUM_ASYNC | MF.ENUM_SORT, MF.ENUM_HARDWARE | MF.ENUM_SYNC | MF.ENUM_ASYNC | MF.ENUM_SORT, MF.ENUM_SYNC | MF.ENUM_SORT })
            {
                var list = MF.Enum(Vortice.MediaFoundation.TransformCategoryGuids.VideoDecoder, fl, MF.H264, MF.NV12);
                Log.Info($"GPUTEST decodificadores H.264 (flags 0x{fl:X}): {string.Join(", ", list.Select(MF.NameOf))}");
                foreach (var a in list) a.Dispose();
            }
        }
        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
            Bounds = new Rectangle(hidden.Right + 200, hidden.Bottom + 200, w, h), BackColor = Color.Black, Text = "TransLocal teste de vídeo",
        };
        _ = form.Handle; // só cria a janela: nunca aparece nem tira o foco de outro programa
        Application.DoEvents();
        using var presenter = GpuPresenter.TryCreate(gpu, form.Handle, form.ClientSize.Width, form.ClientSize.Height);
        Check(presenter is not null, "cadeia de troca DXGI (modelo flip) na janela");
        if (presenter is null) return 1;
        presenter.SetFont("Segoe UI", 15);
        string? stats = null, notice = null;
        presenter.Overlay = () => new OverlayText(stats, notice, "Aguardando imagem…");
        presenter.Redraw();

        int bitrate = H264Encoder.BitrateFor(w, h, fps, 75);
        using var enc = H264Encoder.TryCreate(w, h, fps, bitrate);
        using var dec = H264Decoder.TryCreate(hardware: true, gpu, w, h);
        Check(enc is not null && dec is not null, $"codificador {enc?.Name} • decodificador {dec?.Name} ({(dec?.Hardware == true ? "placa de vídeo" : "processador")}) no mesmo dispositivo");
        if (enc is null || dec is null) return 1;
        Native.timeBeginPeriod(1);
        using var surface = new FrameSurface();
        var frame = new uint[w * h];
        var stream = new List<byte[]>();
        int decoded = 0, onGpu = 0, encoded = 0;
        long decTicks = 0, maxTicks = 0;
        var sw = new Stopwatch();
        fixed (uint* f = frame)
        {
            void Feed(byte[]? data)
            {
                if (data is null) return;
                encoded++;
                stream.Add(data);
                sw.Restart();
                bool ok = dec.Decode(data, w, h, surface, presenter, out bool err);
                long t = sw.ElapsedTicks;
                decTicks += t;
                maxTicks = Math.Max(maxTicks, t);
                if (ok) decoded++;
                if (ok && dec.LastOnGpu) onGpu++;
                if (err) Log.Info("GPUTEST erro de decodificação");
            }
            for (int i = 0; i < frames; i++)
            {
                int shift = (i * 7) % w;
                for (int y = 0; y < h; y++)
                    fixed (uint* src = &baseFrame[y * w])
                    {
                        Buffer.MemoryCopy(src + shift, f + (long)y * w, (w - shift) * 4L, (w - shift) * 4L);
                        Buffer.MemoryCopy(src, f + (long)y * w + (w - shift), shift * 4L, shift * 4L);
                    }
                Feed(enc.Encode((IntPtr)f, w * 4, i == 0, out _));
                if (enc.Pending) { Thread.Sleep(1); Feed(enc.Poll(out _)); }
                if (i % 10 == 0) Application.DoEvents();
            }
            for (int k = 0; k < 50 && enc.Pending; k++) { Thread.Sleep(2); Feed(enc.Poll(out _)); }
        }
        double decMs = decTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, encoded);
        double maxMs = maxTicks * 1000.0 / Stopwatch.Frequency;
        Check(decoded >= encoded * 0.9 && encoded >= frames * 0.9, $"{decoded}/{encoded} quadros decodificados e exibidos");
        Check(onGpu >= decoded * 0.9, $"{onGpu}/{decoded} quadros exibidos pela placa de vídeo (decodificação: {(dec.Hardware ? "placa de vídeo" : "processador — o Windows não liberou a placa")})");
        Log.Info($"GPUTEST decodificação + exibição: {decMs:0.00} ms por quadro em média, pico {maxMs:0.0} ms");

        // Comparação com o caminho antigo: conversão de cor no processador + redimensionamento GDI (HALFTONE) para uma
        // tela de notebook 1366×768 — o que a 1.6 fazia a cada quadro.
        foreach (bool fast in new[] { false, true })
        using (var oldDec = H264Decoder.TryCreate(hardware: true, null, w, h))
        using (var oldSurface = new FrameSurface { FastScaling = fast })
        using (var target = new Bitmap(1366, 768, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
        using (var g = Graphics.FromImage(target))
        {
            long oldTicks = 0;
            foreach (var data in stream)
            {
                sw.Restart();
                if (oldDec!.Decode(data, w, h, oldSurface, out _))
                {
                    var hdc = g.GetHdc();
                    try { oldSurface.Paint(hdc, new Rectangle(0, 0, 1366, 768)); }
                    finally { g.ReleaseHdc(hdc); }
                }
                oldTicks += sw.ElapsedTicks;
            }
            double oldMs = oldTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, stream.Count);
            Log.Info($"GPUTEST caminho antigo{(fast ? " (modo leve)" : "")} (cor no processador + GDI para 1366×768): {oldMs:0.00} ms por quadro × novo {decMs:0.00} ms");
        }

        // A imagem pronta para a tela é copiada da placa de vídeo logo antes de cada apresentação.
        uint[]? shown = null;
        presenter.BeforePresent = back =>
        {
            var d = back.Description;
            var gpuDev = presenter.Device;
            using var staging = gpuDev.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
            {
                Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Staging,
                CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read,
            });
            gpuDev.Context.CopyResource(staging, back);
            var map = gpuDev.Context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int bw = (int)d.Width, bh = (int)d.Height;
                var img = new uint[bw * bh];
                for (int y = 0; y < bh; y++)
                    fixed (uint* dst = &img[y * bw])
                        Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst, bw * 4L, bw * 4L);
                shown = img;
            }
            finally { gpuDev.Context.Unmap(staging, 0); }
        };

        double Compare(uint[] reference, string png)
        {
            presenter.Redraw();
            var img = shown ?? new uint[w * h];
            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var wr = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            fixed (uint* src = img) Buffer.MemoryCopy(src, (void*)wr.Scan0, (long)w * h * 4, Math.Min((long)w * h, img.Length) * 4);
            bmp.UnlockBits(wr);
            bmp.Save(Path.Combine(Path.GetTempPath(), png));
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            double mse = 0;
            int n = 0;
            try
            {
                for (int y = 0; y < h; y += 3)
                {
                    uint* row = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                    for (int x = 0; x < w; x += 3)
                    {
                        uint pa = reference[y * w + x], pb = row[x];
                        for (int c = 8; c < 16; c += 8) // verde ≈ luminância (a cor fica em meia resolução no H.264, como no teste do codec)
                        {
                            int d = (int)((pa >> c) & 0xFF) - (int)((pb >> c) & 0xFF);
                            mse += d * d;
                        }
                        n += 1;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (mse / n));
        }

        double psnr = Compare(frame, "translocal-gputest.png");
        if (shown is { } sh)
        {
            // Diagnóstico: diferença média de brilho e melhor deslocamento (atraso de quadro).
            double sumA = 0, sumB = 0; int cnt = 0;
            for (int y = 8; y < h - 8; y += 4)
                for (int x = 8; x < w - 8; x += 4)
                {
                    sumA += (frame[y * w + x] >> 8) & 0xFF; sumB += (sh[y * w + x] >> 8) & 0xFF; cnt++;
                }
            int best = 0; double bestErr = double.MaxValue;
            for (int dx = -70; dx <= 70; dx++)
            {
                double e = 0;
                for (int y = 8; y < h - 8; y += 6)
                    for (int x = 80; x < w - 80; x += 6)
                    {
                        int a = (int)((frame[y * w + x + dx] >> 8) & 0xFF), b = (int)((sh[y * w + x] >> 8) & 0xFF);
                        e += (a - b) * (a - b);
                    }
                if (e < bestErr) { bestErr = e; best = dx; }
            }
            double mseBest = bestErr / Math.Max(1, ((h - 16 + 5) / 6) * ((w - 160 + 5) / 6));
            double psnrAligned = mseBest <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mseBest);
            Log.Info($"GPUTEST brilho médio original {sumA / cnt:0.0} × na tela {sumB / cnt:0.0}");
            psnr = Math.Max(psnr, psnrAligned);
        }
        Check(psnr > 30, $"imagem na tela igual à original: {psnr:0.0} dB (cores e posição pelo processador de vídeo da placa)");

        // Decodificação direta pela placa (D3D11VA, sem a Media Foundation): o mesmo fluxo, quadro a quadro.
        {
            double AlignedPsnr()
            {
                presenter.Redraw();
                var sh2 = shown!;
                double bestErr = double.MaxValue;
                int samples = 0;
                for (int dx = -140; dx <= 140; dx++)
                {
                    double e = 0;
                    int cnt2 = 0;
                    for (int y = 8; y < h - 8; y += 6)
                        for (int x = 150; x < w - 150; x += 6)
                        {
                            int a = (int)((frame[y * w + x + dx] >> 8) & 0xFF), b = (int)((sh2[y * w + x] >> 8) & 0xFF);
                            e += (a - b) * (a - b);
                            cnt2++;
                        }
                    if (e < bestErr) { bestErr = e; samples = cnt2; }
                }
                double mse2 = bestErr / Math.Max(1, samples);
                return mse2 <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse2);
            }
            using var d3d = D3D11H264Decoder.TryCreate(gpu);
            Check(d3d is not null, $"decodificação direta pela placa disponível: {d3d?.Name}");
            if (d3d is not null)
            {
                int ok = 0, errors = 0;
                long ticks = 0, peak = 0;
                foreach (var data in stream)
                {
                    sw.Restart();
                    if (d3d.Decode(data, w, h, presenter, out bool err)) ok++;
                    long t = sw.ElapsedTicks;
                    ticks += t;
                    peak = Math.Max(peak, t);
                    if (err) errors++;
                    if (d3d.Unsupported is not null) break;
                }
                double ms = ticks * 1000.0 / Stopwatch.Frequency / Math.Max(1, stream.Count);
                Check(d3d.Unsupported is null && ok == stream.Count && errors == 0,
                    $"D3D11VA: {ok}/{stream.Count} quadros decodificados pela placa e exibidos, {ms:0.00} ms por quadro (pico {peak * 1000.0 / Stopwatch.Frequency:0.0} ms)" +
                    (d3d.Unsupported is null ? "" : $" — sem suporte: {d3d.Unsupported}"));
                double p2 = AlignedPsnr();
                Compare(frame, "translocal-gputest-d3d11va.png");
                Check(p2 > 30, $"D3D11VA: imagem igual à original: {p2:0.0} dB");
            }
        }

        // Caminho BGRA (blocos JPEG, decodificação por software, HDMI) pela mesma exibição.
        surface.EnsureSize(w, h);
        fixed (uint* b = baseFrame)
        {
            var bmp = new Bitmap(w, h, w * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, (IntPtr)b);
            surface.CopyFromBitmap(bmp, 0, 0);
            bmp.Dispose();
        }
        sw.Restart();
        bool bgraOk = presenter.PresentSurface(surface);
        double bgraMs = sw.Elapsed.TotalMilliseconds;
        double psnrBgra = Compare(baseFrame, "translocal-gputest-bgra.png");
        Check(bgraOk && psnrBgra > 35, $"caminho BGRA: {psnrBgra:0.0} dB, {bgraMs:0.0} ms para enviar e mostrar");

        // Textos por cima (Direct2D) e redimensionamento.
        stats = "Teste • 60 quadros/s • 5,7 Mbit/s • H.264 → tela direto pela placa de vídeo • atraso da imagem 18 ms";
        notice = "Aviso de teste: o jogo está como administrador no PC.";
        presenter.Redraw();
        Compare(baseFrame, "translocal-gputest-textos.png");
        form.ClientSize = new Size(w * 3 / 4, h * 3 / 4);
        presenter.Resize(form.ClientSize.Width, form.ClientSize.Height);
        presenter.Redraw();
        Application.DoEvents();
        Check(!presenter.Failed && presenter.Dest.Width == w * 3 / 4, $"redimensionada para {form.ClientSize.Width}×{form.ClientSize.Height}: imagem em {presenter.Dest}");
        Check(!presenter.Failed, $"{presenter.Presented} apresentações sem erro");

        // A janela de exibição de verdade (fora da tela, sem tirar o foco): troca para a placa de vídeo, recebe
        // quadros pela thread de rede, mostra estatísticas e aviso, muda de tamanho e fecha.
        {
            IGpuFrameSink? sink = null;
            var viewer = new ViewerForm("NOTEBOOK-TESTE", surface, _ => { }, Screen.PrimaryScreen!, fullscreen: false, showStats: true,
                new Size(w, h)) { NoActivate = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
            viewer.Location = new Point(hidden.Right + 200, hidden.Bottom + 200);
            viewer.Show();
            Application.DoEvents();
            bool attached = viewer.UseGpu(gpu, s => sink = s);
            Check(attached && viewer.GpuActive && sink is not null, "janela de exibição usando a placa de vídeo");
            long before = presenter.Presented;
            var t = new Thread(() =>
            {
                for (int i = 0; i < 30; i++) { viewer.NotifyFrame(); Thread.Sleep(5); } // como a thread de rede faz
            });
            t.Start();
            while (t.IsAlive) { Application.DoEvents(); Thread.Sleep(5); }
            viewer.SetStats("NOTEBOOK-TESTE • 960×540 • 60 quadros/s • atraso da imagem 18 ms");
            viewer.ShowNotice("Aviso de teste");
            viewer.Size = new Size(800, 500);
            for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(10); }
            Check(viewer.GpuActive, "janela continua na placa de vídeo depois de quadros, textos e redimensionamento");
            viewer.Close();
            viewer.Dispose();
            Application.DoEvents();
            Check(sink is null, "ao fechar, a janela solta a placa de vídeo (o decodificador volta ao caminho comum)");
        }
        Native.timeEndPeriod(1);
        Log.Info(failures == 0 ? "GPUTEST concluído: tudo certo" : $"GPUTEST concluído com {failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    private static string Nals(byte[] d)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i + 3 < d.Length; i++)
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
            {
                int t = d[i + 3] & 0x1F;
                // first_mb_in_slice (Exp-Golomb) dos slices: 1 bit "1" no começo = 0
                string extra = (t == 1 || t == 5) ? ((d[i + 4] & 0x80) != 0 ? "(início)" : "(meio)") : "";
                sb.Append(t).Append(extra).Append(' ');
                i += 3;
            }
        return sb.ToString();
    }

    public static unsafe int CodecTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"CODECTEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }
        const int w = 1366, h = 768, fps = 60, frames = 180;
        var screen = Screen.PrimaryScreen!;
        using var cap = ScreenCapturer.Create(screen);
        Thread.Sleep(100);
        cap.Capture(drawCursor: false);
        var scaler = new FrameScaler(cap.Width, cap.Height, w, h);
        scaler.Scale(cap);
        var baseFrame = new uint[w * h];
        fixed (uint* bf = baseFrame) Buffer.MemoryCopy((void*)scaler.Bits, bf, (long)w * h * 4, (long)w * h * 4);

        int bitrate = H264Encoder.BitrateFor(w, h, fps, 75);
        using var enc = H264Encoder.TryCreate(w, h, fps, bitrate);
        Check(enc is not null, enc is null ? "codificador H.264 disponível" : $"codificador H.264: {enc.Name} ({(enc.Hardware ? "placa de vídeo" : "software")}), {bitrate / 1e6:0.0} Mbit/s alvo");
        if (enc is null) return 1;
        Native.timeBeginPeriod(1); // como no laço de vídeo da transmissão
        using var dec = H264Decoder.TryCreate(hardware: true);
        Check(dec is not null, $"decodificador H.264: {dec?.Name} ({(dec?.Hardware == true ? "placa de vídeo" : "processador")})");
        if (dec is null) return 1;
        using var decSw = H264Decoder.TryCreate(hardware: false);
        using var surfaceSw = new FrameSurface();
        long decSwTicks = 0;
        int decodedSw = 0;

        using var surface = new FrameSurface();
        var tiles = new TileEncoder(w, h);
        var frame = new uint[w * h];
        long encTicks = 0, decTicks = 0, bytes = 0, jpegBytes = 0;
        int decoded = 0, encodedFrames = 0, keyFrames = 0;
        var sw = new Stopwatch();
        fixed (uint* f = frame)
        {
            for (int i = 0; i < frames; i++)
            {
                // Movimento: a imagem desliza e um quadrado colorido anda — muda quase tudo a cada quadro.
                int shift = (i * 7) % w;
                for (int y = 0; y < h; y++)
                {
                    uint* row = f + (long)y * w;
                    fixed (uint* src = &baseFrame[y * w])
                    {
                        Buffer.MemoryCopy(src + shift, row, (w - shift) * 4L, (w - shift) * 4L);
                        Buffer.MemoryCopy(src, row + (w - shift), shift * 4L, shift * 4L);
                    }
                }
                int bx = (i * 11) % (w - 200), by = (i * 5) % (h - 200);
                for (int y = by; y < by + 200; y++)
                    for (int x = bx; x < bx + 200; x++)
                        f[(long)y * w + x] = 0xFF000000u | (uint)((x * 3 + i * 5) & 0xFF) << 16 | (uint)((y * 2) & 0xFF) << 8 | (uint)(i * 4 & 0xFF);

                sw.Restart();
                var data = enc.Encode((IntPtr)f, w * 4, i == 0, out bool key);
                if (data is null) { Thread.Sleep(2); data = enc.Poll(out key); }
                encTicks += sw.ElapsedTicks;
                if (data is null) continue;
                encodedFrames++;
                if (key) keyFrames++;
                bytes += data.Length;
                sw.Restart();
                if (dec.Decode(data, w, h, surface, out bool err)) decoded++;
                else if (Environment.GetEnvironmentVariable("TRANSLOCAL_CODEC_TRACE") == "1") Log.Info($"CODECTRACE quadro {i}: {data.Length} bytes, chave={key}, sem saída, NAL {Nals(data)}");
                if (Environment.GetEnvironmentVariable("TRANSLOCAL_CODEC_TRACE") == "1" && i % 30 == 1) Log.Info($"CODECTRACE quadro {i} (normal): {data.Length} bytes, NAL {Nals(data)}");
                decTicks += sw.ElapsedTicks;
                if (err) Log.Info($"CODECTEST erro de decodificação no quadro {i}");
                if (decSw is not null)
                {
                    sw.Restart();
                    if (decSw.Decode(data, w, h, surfaceSw, out _)) decodedSw++;
                    decSwTicks += sw.ElapsedTicks;
                }

                using var jf = tiles.Encode((IntPtr)f, 75, i + 1);
                jpegBytes += jf.Span.Length;
            }
        }
        // Recolhe o último quadro que ficou no codificador da placa de vídeo (saída assíncrona).
        for (int k = 0; k < 50 && enc.Pending; k++)
        {
            Thread.Sleep(2);
            var last = enc.Poll(out _);
            if (last is not null) { encodedFrames++; bytes += last.Length; if (dec.Decode(last, w, h, surface, out _)) decoded++; }
        }
        double encMs = encTicks * 1000.0 / Stopwatch.Frequency / frames;
        double decMs = decoded > 0 ? decTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, encodedFrames) : 0;
        double mbps = bytes * 8.0 / frames * fps / 1e6;
        double jpegMbps = jpegBytes * 8.0 / frames * fps / 1e6;
        Check(encodedFrames >= frames * 0.9, $"{encodedFrames}/{frames} quadros codificados ({keyFrames} quadro(s)-chave), {encMs:0.0} ms por quadro no PC");
        Check(decoded >= encodedFrames * 0.9, $"{decoded}/{encodedFrames} quadros decodificados ({(dec.Hardware ? "placa de vídeo" : "processador")}), {decMs:0.0} ms por quadro (inclui conversão para a tela)");
        if (decSw is not null)
            Check(decodedSw >= encodedFrames * 0.9, $"{decodedSw}/{encodedFrames} quadros decodificados (processador, reserva), {decSwTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, encodedFrames):0.0} ms por quadro");
        {
            // Preenchimento (NAL 12) no fim do quadro: removido no envio e, para transmissores antigos, ignorado no receptor.
            byte[] withFiller = { 0, 0, 0, 1, 0x09, 0xF0, 0, 0, 1, 0x41, 0x9A, 0x22, 0, 0, 0, 1, 0x0C, 0xFF, 0xFF, 0x80 };
            var stripped = AnnexB.StripFiller(withFiller)!;
            var trimmed = AnnexB.TrimTrailingFiller(withFiller);
            Check(stripped.Length == 12 && trimmed.Length == 12 && stripped.AsSpan().SequenceEqual(trimmed) && stripped[^1] == 0x22,
                "preenchimento do codificador removido (sem o quadro ficar preso no decodificador até o próximo)");
        }
        Check(mbps < jpegMbps, $"rede a 60 quadros/s: H.264 {mbps:0.0} Mbit/s × JPEG por blocos {jpegMbps:0.0} Mbit/s ({jpegMbps / Math.Max(0.1, mbps):0.0}× menos)");

        // Fidelidade do último quadro decodificado (PSNR de luminância aproximada).
        double mse = 0;
        int n = 0;
        surface.SavePng(Path.Combine(Path.GetTempPath(), "translocal-codectest.png"));
        fixed (uint* f = frame)
        {
            for (int y = 0; y < h; y += 3)
                for (int x = 0; x < w; x += 3)
                {
                    uint pa = f[(long)y * w + x], pb = surface.PixelAt(x, y);
                    int ga = (int)((pa >> 8) & 0xFF), gb = (int)((pb >> 8) & 0xFF);
                    mse += (ga - gb) * (ga - gb);
                    n++;
                }
        }
        double psnr = n > 0 && mse > 0 ? 10 * Math.Log10(255.0 * 255.0 / (mse / n)) : 99;
        Check(psnr > 28, $"fidelidade do último quadro: {psnr:0.0} dB (acima de 28 dB = boa)");
        Log.Info(failures == 0 ? "CODECTEST concluído: tudo certo" : $"CODECTEST concluído com {failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Toca silêncio pelo AudioPlayer e troca de saída (cada dispositivo ligado e de volta ao padrão),
    /// conferindo que o som acompanha a escolha sem parar. Não altera as configurações salvas.
    /// </summary>
    public static int AudioTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"AUDIOTEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }

        Audio.AudioOutput.Initialize(null);
        var outputs = Audio.AudioOutput.List();
        Log.Info($"AUDIOTEST saídas: {string.Join(" | ", outputs.Select(o => o.Name))}; padrão: {Audio.AudioOutput.DefaultName()}");
        Check(outputs.Count > 0, $"{outputs.Count} saída(s) de áudio ligada(s)");
        if (outputs.Count == 0) return 1;

        var format = new NAudio.Wave.WaveFormat(48000, 16, 2);
        var silence = new byte[format.AverageBytesPerSecond / 50]; // 20 ms
        using var player = new Audio.AudioPlayer(format);
        using var cts = new CancellationTokenSource();
        var feeder = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                player.Add(silence);
                await Task.Delay(20);
            }
        });

        string? WaitFor(string? expected, int ms = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (player.DeviceName == expected) return expected;
                Thread.Sleep(50);
            }
            return player.DeviceName;
        }

        string? def = Audio.AudioOutput.DefaultName();
        Check(WaitFor(def) == def, $"começa no padrão do Windows ({player.DeviceName})");
        foreach (var (id, name) in outputs)
        {
            Audio.AudioOutput.Preferred = id;
            var got = WaitFor(name);
            Check(got == name, $"trocou para \"{name}\" (tocando em \"{got}\")");
        }
        Audio.AudioOutput.Preferred = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}";
        Check(WaitFor(def) == def, $"escolha desligada volta ao padrão ({player.DeviceName})");
        Audio.AudioOutput.Preferred = null;
        Check(WaitFor(def) == def, $"volta ao padrão do Windows ({player.DeviceName})");

        cts.Cancel();
        try { feeder.Wait(1000); } catch { }
        Log.Info(failures == 0 ? "AUDIOTEST concluído: tudo certo" : $"AUDIOTEST concluído com {failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Cria um controle virtual (ViGEmBus), aplica estados e confere lendo de volta pelo XInput deste computador.
    /// </summary>
    public static int GamepadTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"PADTEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }

        bool driver = Input.VirtualGamepads.IsDriverInstalled(out var err);
        Check(driver, $"driver ViGEmBus {(driver ? "presente" : "ausente: " + err)}");
        if (!driver) return 1;

        int before = Input.XInput.CountConnected();
        var slotsBefore = Enumerable.Range(0, 4).Where(i => Input.XInput.TryGetState(i, out _, out _)).ToHashSet();
        Log.Info($"PADTEST controles XInput antes: {before}");

        using var pads = new Input.VirtualGamepads();
        var state = new Input.GamepadState(0, true, 0x1000 | 0x0001, 0, 200, 12000, -8000, 0, 0); // A + Cima, RT, analógico esquerdo
        try
        {
            pads.Apply(state);
            Check(true, "controle virtual criado e conectado");
        }
        catch (Exception ex)
        {
            Log.Error("PADTEST falha ao conectar", ex);
            return 1;
        }

        int newSlot = -1;
        for (int i = 0; i < 40 && newSlot < 0; i++)
        {
            Thread.Sleep(50);
            newSlot = Enumerable.Range(0, 4).FirstOrDefault(s => !slotsBefore.Contains(s) && Input.XInput.TryGetState(s, out _, out _), -1);
        }
        Check(newSlot >= 0, $"controle virtual visível no XInput (slot {newSlot + 1})");
        if (newSlot >= 0)
        {
            pads.Apply(state with { Buttons = 0x1000 | 0x0001 });
            Thread.Sleep(100);
            Input.XInput.TryGetState(newSlot, out var read, out _);
            Check(read.Buttons == state.Buttons && read.RightTrigger == 200 && read.LeftX == 12000 && read.LeftY == -8000,
                $"estado lido de volta: botões=0x{read.Buttons:X4} RT={read.RightTrigger} LX={read.LeftX} LY={read.LeftY}");
        }

        pads.Apply(state with { Connected = false });
        Thread.Sleep(500);
        Check(Input.XInput.CountConnected() == before, "controle virtual removido");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Testa a atualização sem aplicá-la de verdade: consulta o GitHub, baixa e confere o arquivo e troca o
    /// executável de uma cópia numa pasta de teste (não reabre nada nem mexe na cópia instalada).
    /// </summary>
    public static int UpdateTest(string dir)
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"UPDTEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }

        Log.Info($"UPDTEST versão atual {Updater.Current} • cópia instalada: {Updater.IsInstalled}");
        var portable = Updater.CheckAsync(CancellationToken.None, installed: false).GetAwaiter().GetResult();
        var setup = Updater.CheckAsync(CancellationToken.None, installed: true).GetAwaiter().GetResult();
        if (portable is null || setup is null)
        {
            Log.Info("UPDTEST nenhuma versão mais nova no GitHub (esta já é a mais recente).");
            return portable is null && setup is null ? 0 : 1;
        }
        Check(true, $"GitHub: versão {portable.Version} ({portable.Tag})");
        Log.Info($"UPDTEST portátil: {portable.AssetName} {portable.Size} bytes, SHA-256 do GitHub: {portable.Sha256 ?? "(não informado)"}");
        Log.Info($"UPDTEST instalador: {setup.AssetName} {setup.Size} bytes, SHA-256 do GitHub: {setup.Sha256 ?? "(não informado)"}");

        int lastLogged = -25;
        var progress = new Progress<int>(p =>
        {
            if (p - lastLogged < 25) return;
            lastLogged = p;
            Log.Info($"UPDTEST baixando… {p}%");
        });
        string file = Updater.DownloadAsync(portable, progress, CancellationToken.None).GetAwaiter().GetResult();
        Check(new FileInfo(file).Length == portable.Size, $"download completo e conferido ({file})");

        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "TransLocal.exe");
        File.Copy(SystemIntegration.ExePath, target, overwrite: true);
        bool replaced = Updater.ReplaceExecutable(target, file);
        string Hash(string p)
        {
            using var fs = File.OpenRead(p);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
        }
        Check(replaced && Hash(target) == Hash(file) && File.Exists(Path.Combine(dir, "TransLocal.old.exe")),
            "troca do executável na pasta de teste (antigo renomeado, novo no lugar)");
        string? newVersion = FileVersionInfo.GetVersionInfo(target).ProductVersion;
        Check(Updater.ParseVersion(newVersion) == portable.Version, $"versão do executável novo: {newVersion}");

        try { Directory.Delete(dir, recursive: true); } catch { }
        try { Directory.Delete(Updater.DownloadDirectory, recursive: true); } catch { }
        Log.Info(failures == 0 ? "UPDTEST concluído: tudo certo" : $"UPDTEST concluído com {failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Salva imagens de cada aba da janela principal (para revisão visual).</summary>
    public static int UiScreenshots(string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var app = new TrayApp(startMinimized: true, autoUpdate: false);
        // Fora da área visível e sem ativar: não atrapalha quem estiver usando o computador.
        var form = new MainForm(app)
        {
            NoActivate = true,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000),
            ShowInTaskbar = false,
        };
        form.Show();
        foreach (var tab in new[] { "receive", "send", "remote", "hdmi", "settings" })
        {
            form.SelectTab(tab);
            for (int i = 0; i < 40; i++)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            var path = Path.Combine(outDir, $"ui-{tab}.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log.Info($"UI salva: {path}");
        }
        form.Dispose();

        // Janela de exibição (em janela, com estatísticas), alimentada por uma captura real.
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        using var surface = new FrameSurface();
        using (var cap = ScreenCapturer.Create(screen))
        {
            cap.Capture(true);
            using var frame = new Bitmap(cap.Width, cap.Height, cap.Stride, System.Drawing.Imaging.PixelFormat.Format32bppRgb, cap.Bits);
            surface.EnsureSize(cap.Width, cap.Height);
            surface.CopyFromBitmap(frame, 0, 0);
            surface.MarkUpdated();
        }
        var viewer = new ViewerForm("NOTEBOOK-TESTE", surface, _ => { }, screen, fullscreen: false, showStats: true,
            new Size(surface.Width, surface.Height), audio: true) { NoActivate = true, ShowInTaskbar = false };
        viewer.Location = new Point(-30000, -30000);
        viewer.Show();
        viewer.SetStats("NOTEBOOK-TESTE • 1920×1080 • 30 quadros/s • 41,2 Mbit/s • decodificação 4 ms");
        var demo = new[]
        {
            "[NOTEBOOK-TESTE] 00:00:13.183 [ESTADO] captura: DXGI DuplicateOutput1 R10G10B10A2_UNorm • primeiro plano: Cyberpunk2077.exe (administrador) (tela cheia) • cursor preso (relativo) • controle: comum (auxiliar ausente)",
            "[NOTEBOOK-TESTE] 00:00:13.190 [AVISO] Aviso ao receptor: Cyberpunk2077.exe está aberto como administrador no PC…",
        };
        viewer.AttachDiagnostics(() => demo, () => 1);
        viewer.ShowNotice("Cyberpunk2077.exe está aberto como administrador no PC: o Windows bloqueia mouse e teclado vindos de outros programas. No PC, aba Transmitir, ative “Controlar programas abertos como administrador”.");
        for (int i = 0; i < 30; i++)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
        using (var bmp = new Bitmap(viewer.Width, viewer.Height))
        {
            viewer.DrawToBitmap(bmp, new Rectangle(Point.Empty, viewer.Size));
            var path = Path.Combine(outDir, "ui-viewer.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log.Info($"UI salva: {path}");
        }
        viewer.CloseFromCode();
        app.Exit();
        return 0;
    }

#if DEBUG
    /// <summary>
    /// Testa o pipe do controle de administrador sem privilégios (TRANSLOCAL_PIPE_TEST=1, só em Debug):
    /// recusa de programa estranho, conexão do auxiliar, envio de comandos, recusa de servidor falso e saída.
    /// </summary>
    public static int PipeTest()
    {
        Environment.SetEnvironmentVariable("TRANSLOCAL_PIPE_TEST", "1"); // herdado pelos processos filhos
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"PIPETEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }
        string pipe = Input.ElevatedInput.PipeName;
        Process Ps(string script) => Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"{script}\"") { UseShellExecute = false, CreateNoWindow = true })!;

        // 1) Programa estranho conecta no pipe do TransLocal: é desconectado sem receber nada.
        Input.ElevatedInputBridge.Enable();
        var bridge = Input.ElevatedInputBridge.Current!;
        using (var foreign = Ps($"$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '{pipe}', 'In'); $p.Connect(5000); $b = $p.ReadByte(); exit ($b + 10)"))
        {
            foreign.WaitForExit(15_000);
            Check(foreign.HasExited && foreign.ExitCode == 9 && !bridge.IsConnected,
                $"programa estranho que conecta ao pipe é recusado e não recebe nada (saída {(foreign.HasExited ? foreign.ExitCode : -1)})");
        }

        // 2) O auxiliar (mesmo executável) conecta e recebe comandos.
        using var helper = Process.Start(new ProcessStartInfo(Input.ElevatedInput.ExePath, "--elevated-input") { UseShellExecute = false, CreateNoWindow = true })!;
        var sw = Stopwatch.StartNew();
        while (!bridge.IsConnected && sw.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(100);
        Check(bridge.IsConnected, $"auxiliar conectado em {sw.Elapsed.TotalSeconds:0.0} s");
        bool sent = bridge.TrySendInput(Input.InputCodec.EncodeRelativeMove(0, 0)) && bridge.TrySendInput(Input.InputCodec.EncodeReleaseAll());
        bridge.SetMonitor(Screen.PrimaryScreen!.Bounds);
        Check(sent, "comandos de entrada enviados ao auxiliar (movimento nulo e soltar tudo)");

        // 3) Desligar a opção fecha o auxiliar.
        Input.ElevatedInputBridge.Disable(stopHelper: true);
        helper.WaitForExit(5000);
        Check(helper.HasExited && helper.ExitCode == 0, "auxiliar encerra quando a opção é desligada");

        // 4) Servidor falso com o nome do pipe tenta mandar "encerrar": o auxiliar não obedece.
        using (var fake = Ps($"$s = New-Object System.IO.Pipes.NamedPipeServerStream('{pipe}', 'Out'); $s.WaitForConnection(); try {{ $s.Write([byte[]](1,4), 0, 2); $s.Flush() }} catch {{}}; Start-Sleep 4; exit 0"))
        {
            Thread.Sleep(1500);
            using var helper2 = Process.Start(new ProcessStartInfo(Input.ElevatedInput.ExePath, "--elevated-input") { UseShellExecute = false, CreateNoWindow = true })!;
            Thread.Sleep(3500);
            Check(!helper2.HasExited, "auxiliar ignora comandos de um pipe criado por outro programa");
            try { helper2.Kill(); } catch { }
            fake.WaitForExit(10_000);
        }

        Log.Info(failures == 0 ? "PIPETEST concluído: tudo certo" : $"PIPETEST concluído com {failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    private static Rectangle TargetRect(Rectangle s) => new(s.X + s.Width / 2 - 230, s.Y + s.Height / 2 - 100, 460, 200);

    /// <summary>Janela de teste aberta como administrador: grava se recebeu clique e o texto digitado.</summary>
    public static int ElevatedTarget(string resultFile)
    {
        bool elevated = ProcessInfo.CurrentIsElevated();
        var screen = Screen.PrimaryScreen!.Bounds;
        using var form = new Form
        {
            Text = "TransLocal — teste de controle de administrador",
            TopMost = true,
            StartPosition = FormStartPosition.Manual,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            ShowInTaskbar = false,
            Bounds = TargetRect(screen),
        };
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, Font = new Font("Segoe UI", 16) };
        form.Controls.Add(box);
        bool clicked = false;
        box.MouseDown += (_, _) => clicked = true;
        var sw = Stopwatch.StartNew();
        var timer = new System.Windows.Forms.Timer { Interval = 150 };
        timer.Tick += (_, _) =>
        {
            try { File.WriteAllText(resultFile, $"{elevated}|{clicked}|{box.Text}"); } catch { }
            if (box.Text.Contains("fim", StringComparison.OrdinalIgnoreCase) || sw.Elapsed > TimeSpan.FromSeconds(90)) form.Close();
        };
        form.Shown += (_, _) => timer.Start();
        Application.Run(form);
        try { File.WriteAllText(resultFile, $"{elevated}|{clicked}|{box.Text}|fechada"); } catch { }
        return 0;
    }

    [DllImport("user32.dll")] private static extern short VkKeyScanW(char ch);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);

    /// <summary>
    /// Teste real do controle de programas de administrador (pede UMA confirmação do UAC):
    /// cria a tarefa, abre uma janela de administrador, confirma que a injeção comum é bloqueada e que
    /// o auxiliar (iniciado pela tarefa, sem novo UAC) consegue clicar e digitar nela. Depois remove tudo.
    /// </summary>
    public static int ElevatedTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Log.Info($"ELEVTEST {(ok ? "OK  " : "FALHOU")} {what}");
            if (!ok) failures++;
        }
        string file = Path.Combine(Path.GetTempPath(), $"translocal-elevtest-{Guid.NewGuid():N}.txt");
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var screen = Screen.PrimaryScreen!.Bounds;
        var rect = TargetRect(screen);
        string Read() { try { return File.ReadAllText(file); } catch { return ""; } }

        Process? reg;
        try
        {
            reg = Process.Start(new ProcessStartInfo(Input.ElevatedInput.ExePath,
                $"--register-elevated-input {sid} --with-test-target \"{file}\"") { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Info($"ELEVTEST FALHOU confirmação do Windows recusada ({ex.Message})");
            return 1;
        }
        var sw = Stopwatch.StartNew();
        while (!Read().StartsWith("True|") && sw.Elapsed < TimeSpan.FromSeconds(90)) Thread.Sleep(200);
        Check(Read().StartsWith("True|"), "janela de teste aberta como administrador");
        Check(Input.ElevatedInput.TaskInstalled(), "tarefa “TransLocal Controle Elevado” registrada");

        byte[] Click(MouseButtons b, bool down)
        {
            var c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2 + 15);
            ushort nx = (ushort)((c.X - screen.X) * 65535L / Math.Max(1, screen.Width - 1));
            ushort ny = (ushort)((c.Y - screen.Y) * 65535L / Math.Max(1, screen.Height - 1));
            return Input.InputCodec.EncodeButton(b, down, nx, ny);
        }

        // 1) Sem o auxiliar: o Windows descarta o clique (UIPI).
        var local = new Input.InputInjector(screen);
        local.Apply(Click(MouseButtons.Left, true));
        local.Apply(Click(MouseButtons.Left, false));
        Thread.Sleep(800);
        Check(Read().StartsWith("True|False|"), "sem o auxiliar, o clique numa janela de administrador é bloqueado (comportamento do Windows)");

        // 2) Com o auxiliar, iniciado pela tarefa sem novo UAC.
        Input.ElevatedInputBridge.Enable();
        var bridge = Input.ElevatedInputBridge.Current!;
        sw.Restart();
        while (!bridge.IsConnected && sw.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(200);
        Check(bridge.IsConnected, $"auxiliar iniciado pela tarefa (sem novo UAC) e conectado em {sw.Elapsed.TotalSeconds:0.0} s");
        if (bridge.IsConnected)
        {
            bridge.SetMonitor(screen);
            bridge.TrySendInput(Click(MouseButtons.Left, true));
            bridge.TrySendInput(Click(MouseButtons.Left, false));
            Thread.Sleep(600);
            var fg = ProcessInfo.Foreground();
            Check(Read().StartsWith("True|True|"), $"com o auxiliar, o clique chega à janela de administrador (primeiro plano: {fg})");
            if (fg.Elevated && Read().StartsWith("True|True|"))
            {
                void Type(string text)
                {
                    foreach (char ch in text)
                    {
                        ushort vk = (ushort)(VkKeyScanW(ch) & 0xFF);
                        ushort scan = (ushort)MapVirtualKeyW(vk, 0);
                        bridge.TrySendInput(Input.InputCodec.EncodeKey(vk, scan, false, false));
                        bridge.TrySendInput(Input.InputCodec.EncodeKey(vk, scan, false, true));
                    }
                }
                Type("tl14");
                Thread.Sleep(800);
                Check(Read().Contains("tl14", StringComparison.OrdinalIgnoreCase), $"teclado chega à janela de administrador (“{Read().Split('|').Last()}”)");
                Type("fim");
            }
            else
            {
                Log.Info("ELEVTEST (a janela de teste não ficou em primeiro plano; digitação não testada para não escrever em outra janela)");
            }
        }
        Input.ElevatedInputBridge.Disable(stopHelper: true);

        reg?.WaitForExit(120_000);
        Thread.Sleep(500);
        Check(!Input.ElevatedInput.TaskInstalled(), "tarefa de teste removida no fim");
        try { File.Delete(file); } catch { }
        Log.Info(failures == 0 ? "ELEVTEST concluído: tudo certo" : $"ELEVTEST concluído com {failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }
#endif

    private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    private static unsafe double NonBlackFraction(ScreenCapturer c)
    {
        var px = new ReadOnlySpan<uint>((void*)c.Bits, c.Width * c.Height);
        long nonBlack = 0;
        for (int i = 0; i < px.Length; i += 7) if ((px[i] & 0x00FFFFFF) != 0) nonBlack++;
        return nonBlack / (px.Length / 7.0);
    }
}
