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

    /// <summary>Cria a regra no firewall (pede permissão de administrador). Retorna true se deu certo.</summary>
    public static bool AddFirewallRule()
    {
        string exe = ExePath;
        string args = "/c " +
                      $"netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\" >nul 2>&1 & " +
                      $"netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any " +
                      "description=\"Permite receber transmissões de tela do TransLocal\"";
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
