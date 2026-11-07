using TransLocal.UI;

namespace TransLocal;

internal static class Program
{
    private const string SingleInstanceMutex = @"Local\TransLocal.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // Manipulador “mudo” de links (ex.: ms-gamebar): sai imediatamente, sem janela.
        if (args.Length > 0 && args[0].Equals("--noop", StringComparison.OrdinalIgnoreCase)) return 0;

        Log.Init();
        ApplicationConfiguration.Initialize();

        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
        }
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        // ---- Modos usados pelo instalador ----
        if (Has("--set-autostart"))
        {
            SystemIntegration.SetAutoStart(!string.Equals(Arg("--set-autostart"), "off", StringComparison.OrdinalIgnoreCase));
            return 0;
        }
        if (Has("--cleanup"))
        {
            SystemIntegration.RemoveAutoStartIfOurs();
            SystemIntegration.RestoreGameBarPrompt();
            return 0;
        }
        if (Has("--quit"))
        {
            // Usado pelo instalador: pede para a instância em execução fechar normalmente.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(TrayApp.QuitEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        if (Has("--fix-gamebar-prompt"))
        {
            SystemIntegration.SuppressGameBarPromptIfNeeded();
            return 0;
        }
        if (Has("--restore-gamebar-prompt"))
        {
            SystemIntegration.RestoreGameBarPrompt();
            return 0;
        }

        // ---- Serviço do TransLocal (programas de administrador e confirmações do UAC) ----
        if (Has("--service")) return Input.TransLocalService.RunAsService();
        if (Has("--system-agent"))
        {
            Log.InitAt(Input.TransLocalService.ServiceLogDirectory, "agente.log");
            return Input.ElevatedInput.RunHelper(Arg("--system-agent") ?? "?");
        }
        if (Has("--install-service")) return Input.ServiceSetup.Install();
        if (Has("--uninstall-service")) return Input.ServiceSetup.Uninstall();

        // ---- Controle de programas de administrador (tarefa agendada, versões 1.4.x) ----
        if (Has("--elevated-input"))
        {
            // Auxiliar elevado: registro próprio (dois processos no mesmo arquivo se atrapalham).
            Log.Init("TransLocal-administrador.log");
            return Input.ElevatedInput.RunHelper();
        }
        if (Has("--register-elevated-input")) return Input.ElevatedInput.Register(Arg("--register-elevated-input"), Arg("--with-test-target"));
        if (Has("--unregister-elevated-input")) return Input.ElevatedInput.Unregister();
#if DEBUG
        if (Has("--elevated-target")) return DevTools.ElevatedTarget(Arg("--elevated-target")!);
        if (Has("--pipe-test"))
        {
            Log.Echo = Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            return DevTools.PipeTest();
        }
        if (Has("--elevated-test"))
        {
            Log.Echo = Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            return DevTools.ElevatedTest();
        }
#endif

        // ---- Diagnóstico ----
        if (Has("--bench") || Has("--selftest") || Has("--ui-screenshot") || Has("--gamepad-test") || Has("--update-test") || Has("--audio-test") || Has("--codec-test") || Has("--gpu-test") || Has("--dxva-probe") || Has("--stream-test") || Has("--d3d11va-verify") || Has("--capture-test"))
        {
            Log.Echo = Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            try
            {
                if (Has("--bench")) return DevTools.Bench();
                if (Has("--update-test")) return DevTools.UpdateTest(Arg("--update-test") ?? Path.Combine(Path.GetTempPath(), "TransLocal-UpdateTest"));
                if (Has("--gamepad-test")) return DevTools.GamepadTest();
                if (Has("--audio-test")) return DevTools.AudioTest();
                if (Has("--codec-test")) return DevTools.CodecTest();
                if (Has("--gpu-test")) return DevTools.GpuTest();
                if (Has("--dxva-probe")) return DxvaProbe.Run();
                if (Has("--stream-test")) return DevTools.StreamTest();
                if (Has("--d3d11va-verify")) return DevTools.D3D11VaVerify();
                if (Has("--capture-test")) return DevTools.CaptureTest();
                if (Has("--selftest")) return DevTools.SelfTest(Arg("--selftest") ?? Log.DataDirectory);
                return DevTools.UiScreenshots(Arg("--ui-screenshot") ?? Log.DataDirectory);
            }
            catch (Exception ex)
            {
                Log.Error("Diagnóstico falhou", ex);
                return 2;
            }
        }

        // ---- Aplicativo normal ----
        using var mutex = new Mutex(true, SingleInstanceMutex, out bool firstInstance);
        if (!firstInstance)
        {
            // Já está aberto: só traz a janela para a frente.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(TrayApp.ShowEventName);
                ev.Set();
            }
            catch { }
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("Erro não tratado na interface", e.Exception);
            MessageBox.Show(e.Exception.Message, "TransLocal", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Erro fatal", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn("Tarefa com erro não observado", e.Exception);
            e.SetObserved();
        };

        using var app = new TrayApp(startMinimized: Has("--minimized"), justUpdated: Has("--updated"));
        Application.Run(app);
        return 0;
    }
}
