using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace TransLocal;

internal static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "TransLocal";
    public const string FirewallRuleName = "TransLocal";

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0";

    public static Icon AppIcon { get; } = LoadIcon();

    private static Icon LoadIcon()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TransLocal.ico");
            if (s is not null) return new Icon(s);
        }
        catch { }
        return SystemIcons.Application;
    }

    // ---------------- Iniciar com o Windows ----------------

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string;
        }
        catch { return false; }
    }

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k.SetValue(RunValue, $"\"{ExePath}\" --minimized");
            else k.DeleteValue(RunValue, false);
        }
        catch (Exception ex)
        {
            Log.Warn("Falha ao alterar início automático", ex);
        }
    }

    /// <summary>Remove o início automático só se ele aponta para este executável (outra cópia pode estar instalada).</summary>
    public static void RemoveAutoStartIfOurs()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(RunValue) is string cmd && cmd.Contains(ExePath, StringComparison.OrdinalIgnoreCase))
                k.DeleteValue(RunValue, false);
        }
        catch (Exception ex)
        {
            Log.Warn("Falha ao remover início automático", ex);
        }
    }

    /// <summary>Atualiza o caminho do início automático se o programa foi movido.</summary>
    public static void RefreshAutoStartPath()
    {
        if (IsAutoStartEnabled()) SetAutoStart(true);
    }

    // ---------------- Firewall ----------------

    /// <summary>
    /// Verifica se existe a regra de entrada do TransLocal para este executável.
    /// Ligações diretas por cabo costumam ser classificadas como “rede pública”, onde o Windows
    /// bloqueia conexões de entrada por padrão; por isso a regra vale para todos os perfis.
    /// </summary>
    public static bool HasFirewallRule()
    {
        try
        {
            // O netsh escreve na página de código OEM (850 em português); necessário para caminhos com acentos.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRuleName}\" verbose")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && output.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível consultar o firewall", ex);
            return false;
        }
    }

    /// <summary>
    /// De onde o TransLocal aceita conexões: a rede local (inclui cabo direto 169.254.x.x) e o Tailscale.
    /// A internet aberta fica bloqueada pelo próprio Windows, mesmo que alguém encaminhe a porta no roteador.
    /// </summary>
    public const string FirewallRemoteIps = "localsubnet,100.64.0.0/10,fd7a:115c:a1e0::/48";

    /// <summary>Cria a regra no firewall (pede permissão de administrador). Retorna true se deu certo.</summary>
    public static bool AddFirewallRule()
    {
        string exe = ExePath;
        string args = "/c " +
                      $"netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\" >nul 2>&1 & " +
                      $"netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any " +
                      $"remoteip={FirewallRemoteIps} " +
                      "description=\"Permite receber transmissões de tela do TransLocal (rede local e Tailscale)\"";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(30_000);
            bool ok = HasFirewallRule();
            Log.Info($"Regra de firewall criada: {ok}");
            return ok;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Usuário cancelou a elevação para o firewall");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("Falha ao criar regra de firewall", ex);
            return false;
        }
    }

    // ---------------- Controles de jogo (driver ViGEmBus) ----------------

    public const string GamepadDriverUrl = "https://github.com/nefarius/ViGEmBus/releases/latest";

    /// <summary>
    /// Instala o driver ViGEmBus pelo winget (o Windows pede permissão de administrador).
    /// Sem winget, abre a página de download. Retorna true se o driver ficou disponível.
    /// </summary>
    public static async Task<bool> InstallGamepadDriverAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("winget",
                "install --id ViGEm.ViGEmBus -e --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = true,
            };
            using var p = Process.Start(psi);
            if (p is null) throw new Win32Exception();
            await p.WaitForExitAsync();
            Log.Info($"winget ViGEmBus terminou com código {p.ExitCode}");
        }
        catch (Win32Exception ex)
        {
            Log.Warn("winget indisponível; abrindo a página do ViGEmBus", ex);
            OpenUrl(GamepadDriverUrl);
            return false;
        }
        return await Task.Run(() => Input.VirtualGamepads.IsDriverInstalled(out _));
    }

    // ---------------- Aviso “ms-gamebar” ----------------

    private static readonly string[] GameBarProtocols = { "ms-gamebar", "ms-gamebarservices", "ms-gamingoverlay" };
    private const string NoopMarker = "TransLocalNoop";
    private static int _gameBarChecked;

    /// <summary>
    /// Quando um controle de Xbox (inclusive virtual) é conectado, o Windows abre o link “ms-gamebar:”.
    /// Sem a Xbox Game Bar instalada, isso mostra a janela “Obter um aplicativo para abrir este link”,
    /// que rouba o foco do jogo. Se não houver Game Bar nem outro programa associado, registra para o
    /// usuário atual um manipulador que não faz nada (systray.exe). Reversível com <see cref="RestoreGameBarPrompt"/>.
    /// </summary>
    public static void SuppressGameBarPromptIfNeeded()
    {
        if (Interlocked.Exchange(ref _gameBarChecked, 1) != 0) return;
        try
        {
            if (IsGameBarInstalled()) return;
            string handler = $"\"{Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\systray.exe")}\"";
            if (!File.Exists(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\systray.exe")))
                handler = $"\"{ExePath}\" --noop";

            foreach (var proto in GameBarProtocols)
            {
                if (HasProtocolCommand(proto)) continue; // outro programa já cuida deste link
                using var existing = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{proto}");
                bool existed = existing is not null;
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{proto}");
                key.SetValue(NoopMarker, existed ? "added" : "created");
                if (key.GetValue("URL Protocol") is null) key.SetValue("URL Protocol", "");
                key.SetValue("NoOpenWith", "");
                using var cmd = key.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", handler);
                Log.Info($"Aviso “{proto}:” desativado (Game Bar não instalada)");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível desativar o aviso ms-gamebar", ex);
        }
    }

    /// <summary>Desfaz o que <see cref="SuppressGameBarPromptIfNeeded"/> criou.</summary>
    public static void RestoreGameBarPrompt()
    {
        foreach (var proto in GameBarProtocols)
        {
            try
            {
                string path = $@"Software\Classes\{proto}";
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
                if (key?.GetValue(NoopMarker) is not string how) continue;
                if (how == "created")
                {
                    key.Dispose();
                    Registry.CurrentUser.DeleteSubKeyTree(path, false);
                }
                else
                {
                    key.DeleteSubKeyTree("shell", false);
                    key.DeleteValue("NoOpenWith", false);
                    key.DeleteValue(NoopMarker, false);
                }
                Log.Info($"Aviso “{proto}:” restaurado ao padrão do Windows");
            }
            catch (Exception ex)
            {
                Log.Warn($"Falha ao restaurar {proto}", ex);
            }
        }
    }

    /// <summary>Se a Game Bar foi instalada depois, remove o manipulador mudo para não atrapalhá-la.</summary>
    public static void RestoreGameBarPromptIfGameBarInstalled()
    {
        try
        {
            if (IsGameBarInstalled()) RestoreGameBarPrompt();
        }
        catch { }
    }

    private static bool IsGameBarInstalled()
    {
        // Pacotes AppX instalados para o usuário aparecem neste repositório do registro.
        using var repo = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        return repo?.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.XboxGamingOverlay_", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool HasProtocolCommand(string proto)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{proto}\shell\open\command");
        if (key?.GetValue("") is not string cmd || string.IsNullOrWhiteSpace(cmd)) return false;
        using var mine = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{proto}");
        return mine?.GetValue(NoopMarker) is null; // o nosso próprio manipulador não conta
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
