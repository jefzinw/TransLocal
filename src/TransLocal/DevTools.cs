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
        var (pullConn, pullPeer) = await RemotePull.RequestAsync("127.0.0.1", port, NbChannel(false), totp.Current(), _ => { }, CancellationToken.None);
        var pullTask = Task.Run(() => { try { nbViewer.ServeIncomingStream(pullConn, pullPeer, requestedByUs: true); } finally { pullConn.Dispose(); } });
        var pulled = await pulledStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(2000);
        Check(Interlocked.Read(ref pulled.FramesReceived) >= 1 && pulled.Surface.Width == 1366 && pulledSender?.Limits.Light == true,
            $"acesso remoto com código: tela do PC recebida no notebook em {pulled.Surface.Width}×{pulled.Surface.Height} (modo leve)");
        try
        {
            await RemotePull.RequestAsync("127.0.0.1", port, NbChannel(false), totp.Current(), _ => { }, CancellationToken.None);
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

    private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    private static unsafe double NonBlackFraction(ScreenCapturer c)
    {
        var px = new ReadOnlySpan<uint>((void*)c.Bits, c.Width * c.Height);
        long nonBlack = 0;
        for (int i = 0; i < px.Length; i += 7) if ((px[i] & 0x00FFFFFF) != 0) nonBlack++;
        return nonBlack / (px.Length / 7.0);
    }
}
