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

    /// <summary>Transmissão completa pela interface de loopback: descoberta, vídeo, recusa por ocupado e encerramento.</summary>
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

        using var server = new ReceiverServer(() => "Receptor-Teste", () => false, port, discoveryPort);
        var started = new TaskCompletionSource<ReceiverSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SessionStarted += s => started.TrySetResult(s);
        server.SessionEnded += (_, reason) => ended.TrySetResult(reason);
        server.Start();

        var found = await DiscoveryScanner.ScanAsync(discoveryPort, TimeSpan.FromSeconds(1.5), includeLoopback: true, CancellationToken.None);
        foreach (var r in found) Log.Info($"SELFTEST descoberto: {r.Name} {r.Endpoint} via {r.Via}");
        Check(found.Any(r => r.Name == "Receptor-Teste"), "descoberta UDP encontra o receptor");

        var opt = new SenderOptions { Name = "Transmissor-Teste", Fps = 30, Quality = 75, Audio = true };
        var sender = await SenderSession.ConnectAsync("127.0.0.1", port, opt, CancellationToken.None);
        sender.StatsUpdated += t => Log.Info("SELFTEST [transmissor] " + t);
        var session = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.StatsUpdated += t => Log.Info("SELFTEST [receptor] " + t);
        Check(sender.ReceiverName == "Receptor-Teste", "handshake (nome do receptor)");

        // Segundo transmissor enquanto o primeiro está ativo: deve ser recusado.
        try
        {
            using var second = await SenderSession.ConnectAsync("127.0.0.1", port, opt, CancellationToken.None);
            Check(false, "segundo transmissor deveria ser recusado");
        }
        catch (InvalidOperationException ex)
        {
            Check(true, $"segundo transmissor recusado (“{ex.Message}”)");
        }

        await Task.Delay(3500);
        long frames = Interlocked.Read(ref session.FramesReceived);
        Check(frames >= 1, $"quadros recebidos: {frames}");
        Check(session.Surface.Width == Screen.PrimaryScreen!.Bounds.Width, $"resolução recebida {session.Surface.Width}×{session.Surface.Height}");
        var png = Path.Combine(outDir, "selftest-recebido.png");
        session.Surface.SavePng(png);
        Log.Info($"SELFTEST imagem recebida salva em {png}");

        // Encerramento pelo receptor: o transmissor deve ser avisado.
        var senderEnded = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Ended += (_, reason) => senderEnded.TrySetResult(reason);
        session.Close();
        var reasonAtSender = await senderEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(reasonAtSender is not null, $"transmissor avisado do fim (“{reasonAtSender}”)");
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Surface.Dispose();

        // Controles de jogo: formato da mensagem e leitura do XInput deste computador.
        var padIn = new Input.GamepadState(2, true, 0x1F3F, 10, 250, -32768, 32767, 1234, -4321);
        Check(Input.GamepadCodec.TryDecode(Input.GamepadCodec.Encode(padIn), out var padOut) && padOut == padIn, "controle de jogo: codificação ida e volta");
        Log.Info($"SELFTEST controles XInput conectados neste computador: {Input.XInput.CountConnected()}");
        bool driver = Input.VirtualGamepads.IsDriverInstalled(out var driverError);
        Log.Info($"SELFTEST driver ViGEmBus neste computador: {(driver ? "instalado" : "ausente (" + driverError + ")")}");

        // Nova sessão com controle permitido, encerrada pelo transmissor.
        var started2 = new TaskCompletionSource<ReceiverSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended2 = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SessionStarted += s => started2.TrySetResult(s);
        server.SessionEnded += (_, reason) => ended2.TrySetResult(reason);
        var optControl = new SenderOptions { Name = "Transmissor-Teste", Fps = 30, Quality = 75, Audio = false, AllowControl = true };
        var sender2 = await SenderSession.ConnectAsync("127.0.0.1", port, optControl, CancellationToken.None);
        var session2 = await started2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string expected = driver ? Input.VirtualGamepads.StatusOk : Input.VirtualGamepads.StatusNoDriver;
        Check(sender2.GamepadStatus == expected, $"transmissor anuncia controles de jogo (“{sender2.GamepadStatus}”)");
        // Transmissão vinda do próprio computador: controle bloqueado (evita ciclo de mouse e de controles virtuais).
        Check(session2.FromThisComputer && !session2.ControlAllowed && session2.GamepadStatus == Input.VirtualGamepads.StatusDisabled,
            "controle bloqueado quando a transmissão vem do próprio computador");
        session2.SendGamepad(Input.GamepadCodec.Encode(padIn with { Slot = 0 })); // deve ser ignorado
        await Task.Delay(800);
        Check(sender2.IsRunning && Input.XInput.CountConnected() == 0, "nenhum controle virtual criado; transmissão segue ativa");
        sender2.Stop();
        var reasonAtReceiver = await ended2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(reasonAtReceiver is not null, $"receptor avisado do fim (“{reasonAtReceiver}”)");
        session2.Surface.Dispose();

        server.Stop();
        Log.Info(failures == 0 ? "SELFTEST concluído: tudo certo" : $"SELFTEST concluído com {failures} falha(s)");
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

    /// <summary>Salva imagens de cada aba da janela principal (para revisão visual).</summary>
    public static int UiScreenshots(string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var app = new TrayApp(startMinimized: true);
        // Fora da área visível e sem ativar: não atrapalha quem estiver usando o computador.
        var form = new MainForm(app)
        {
            NoActivate = true,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000),
            ShowInTaskbar = false,
        };
        form.Show();
        foreach (var tab in new[] { "receive", "send", "hdmi", "settings" })
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
            new Size(surface.Width, surface.Height)) { NoActivate = true, ShowInTaskbar = false };
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
