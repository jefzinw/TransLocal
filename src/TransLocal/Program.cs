using TransLocal.UI;

namespace TransLocal;

internal static class Program
{
    private const string SingleInstanceMutex = @"Local\TransLocal.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
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
            SystemIntegration.SetAutoStart(false);
            return 0;
        }

        // ---- Diagnóstico ----
        if (Has("--bench") || Has("--selftest") || Has("--ui-screenshot"))
        {
            Log.Echo = Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            try
            {
                if (Has("--bench")) return DevTools.Bench();
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

        using var app = new TrayApp(startMinimized: Has("--minimized"));
        Application.Run(app);
        return 0;
    }
}
