using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TransLocal.Streaming;

/// <summary>Programa em primeiro plano no computador que transmite (para diagnóstico e avisos).</summary>
internal readonly record struct ForegroundApp(int Pid, string Name, bool Elevated, bool Fullscreen)
{
    public override string ToString() =>
        Pid == 0 ? "nenhum" : $"{Name}{(Elevated ? " (administrador)" : "")}{(Fullscreen ? " (tela cheia)" : "")}";
}

internal static partial class ProcessInfo
{
    private static readonly ConcurrentDictionary<int, (string Name, bool Elevated, long Seen)> Cache = new();

    /// <summary>Janela em primeiro plano agora: processo, se roda como administrador e se ocupa o monitor inteiro.</summary>
    public static ForegroundApp Foreground()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return default;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return default;
            var (name, elevated) = Describe((int)pid);
            bool fullscreen = false;
            if (GetWindowRect(hwnd, out var r))
            {
                var s = Screen.FromHandle(hwnd).Bounds;
                fullscreen = r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom
                             && !IsShellWindow(hwnd);
            }
            return new ForegroundApp((int)pid, name, elevated, fullscreen);
        }
        catch
        {
            return default;
        }
    }

    private static bool IsShellWindow(IntPtr hwnd) => hwnd == GetShellWindow() || hwnd == GetDesktopWindow();

    /// <summary>Nome do executável e se o processo está elevado (sem acesso = tratado como elevado).</summary>
    public static (string Name, bool Elevated) Describe(int pid)
    {
        long now = Environment.TickCount64;
        if (Cache.TryGetValue(pid, out var c) && now - c.Seen < 30_000) return (c.Name, c.Elevated);
        string name = "?";
        bool elevated = false;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            // Processos protegidos/de sistema: não dá nem para consultar; com certeza acima do nosso nível.
            elevated = true;
            try { name = Process.GetProcessById(pid).ProcessName + ".exe"; } catch { }
        }
        else
        {
            try
            {
                var buf = new StringBuilder(1024);
                int len = buf.Capacity;
                if (QueryFullProcessImageName(h, 0, buf, ref len)) name = Path.GetFileName(buf.ToString());
                elevated = IsElevated(h);
            }
            finally { CloseHandle(h); }
        }
        if (Cache.Count > 256) Cache.Clear();
        Cache[pid] = (name, elevated, now);
        return (name, elevated);
    }

    /// <summary>Caminho completo do executável de um processo (null se não der para consultar).</summary>
    public static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new StringBuilder(1024);
            int len = buf.Capacity;
            return QueryFullProcessImageName(h, 0, buf, ref len) ? buf.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static bool IsProcessElevated(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        try { return IsElevated(h); }
        finally { CloseHandle(h); }
    }

    public static bool CurrentIsElevated()
    {
        using var p = Process.GetCurrentProcess();
        return IsElevated(p.Handle);
    }

    private static bool IsElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return false;
        try
        {
            return GetTokenInformation(token, TokenElevation, out int value, 4, out _) && value != 0;
        }
        finally { CloseHandle(token); }
    }

    public static int SessionOf(int pid) => ProcessIdToSessionId((uint)pid, out uint s) ? (int)s : -1;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int length, out int returned);
}

/// <summary>
/// Registro de diagnóstico de uma transmissão, compartilhado em tempo real com o outro computador.
/// Leve: só enfileira na hora; uma vez por segundo junta até 40 linhas numa mensagem pequena
/// (repetições viram "×N") e grava o arquivo da sessão. Nada é enviado para fora dos dois aparelhos.
/// </summary>
internal sealed class DiagRelay : IDisposable
{
    private const int MaxQueue = 400;
    private const int MaxLinesPerFlush = 40;
    private const int MaxBytesPerMessage = 8 * 1024;

    private readonly Action<byte[]>? _send;
    private readonly string _peer;
    private readonly ConcurrentQueue<string> _outgoing = new();
    private readonly ConcurrentQueue<string> _file = new();
    private readonly System.Threading.Timer _timer;
    private readonly string? _filePath;
    private readonly List<string> _recent = new();
    private readonly object _recentLock = new();
    private int _queued;
    private int _dropped;
    private string? _lastLine;
    private int _repeat;
    private int _disposed;

    /// <summary>Configuração "compartilhar diagnóstico": quando false, as linhas só ficam no arquivo local.</summary>
    public static volatile bool ShareEnabled = true;

    /// <summary>Avisos e erros (dos dois lados) desde o início da sessão.</summary>
    public int ProblemCount;

    /// <summary>Linhas novas (local ou do outro lado) — para a janela de exibição. Thread do timer.</summary>
    public event Action? Updated;

    public string? FilePath => _filePath;

    /// <param name="send">Envia uma mensagem Diag ao outro computador; null = só grava o arquivo.</param>
    public DiagRelay(string peer, string role, Action<byte[]>? send)
    {
        _peer = SafeName(peer);
        _send = send;
        try
        {
            var dir = DiagFiles.Directory;
            System.IO.Directory.CreateDirectory(dir);
            DiagFiles.Prune(30);
            _filePath = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{role}_{_peer}.log");
            File.WriteAllText(_filePath,
                $"TransLocal {Updater.Current} • {Environment.OSVersion.VersionString} • {Environment.MachineName} ({role}) ↔ {_peer}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            _filePath = null;
        }
        Log.Written += OnLocal;
        _timer = new System.Threading.Timer(_ => Flush(), null, 1000, 1000);
    }

    private static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return sb.Length == 0 ? "outro" : sb.ToString(0, Math.Min(sb.Length, 40));
    }

    private void OnLocal(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        if (level != "INFO") Interlocked.Increment(ref ProblemCount);
        Enqueue(line, toPeer: true, local: true);
    }

    /// <summary>Estado periódico (só no arquivo e para o outro lado; não polui o registro principal).</summary>
    public void Snapshot(string text) => Enqueue($"{DateTime.Now:HH:mm:ss.fff} [ESTADO] {text}", toPeer: true, local: true);

    /// <summary>Linhas que chegaram do outro computador.</summary>
    public void Received(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return;
        string text = Encoding.UTF8.GetString(payload[..Math.Min(payload.Length, MaxBytesPerMessage)]);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length > 600) line = line[..600] + "…";
            if (line.Contains("[AVISO]") || line.Contains("[ERRO]")) Interlocked.Increment(ref ProblemCount);
            string tagged = $"[{_peer}] {line}";
            if (!line.Contains("[ESTADO]")) Log.Relayed($"{DateTime.Now:yyyy-MM-dd} {tagged}");
            Enqueue(tagged, toPeer: false, local: false);
        }
    }

    private void Enqueue(string line, bool toPeer, bool local)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Increment(ref _queued) > MaxQueue)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _file.Enqueue(line);
        if (toPeer && _send is not null) _outgoing.Enqueue(line);
        else Interlocked.Decrement(ref _queued);
        lock (_recentLock)
        {
            _recent.Add(line);
            if (_recent.Count > 60) _recent.RemoveRange(0, _recent.Count - 60);
        }
    }

    /// <summary>Últimas linhas (as mais novas no fim).</summary>
    public string[] Recent(int count)
    {
        lock (_recentLock) return _recent.Skip(Math.Max(0, _recent.Count - count)).ToArray();
    }

    private void Flush()
    {
        bool any = false;
        try
        {
            // Arquivo da sessão (uma escrita por segundo).
            if (!_file.IsEmpty && _filePath is not null)
            {
                var sb = new StringBuilder();
                while (_file.TryDequeue(out var l)) sb.Append(l).Append(Environment.NewLine);
                File.AppendAllText(_filePath, sb.ToString(), Encoding.UTF8);
                any = true;
            }
            else
            {
                while (_file.TryDequeue(out _)) { }
            }

            if (_send is null || _outgoing.IsEmpty) return;
            var msg = new StringBuilder();
            int n = 0;
            while (n < MaxLinesPerFlush && msg.Length < MaxBytesPerMessage - 700 && _outgoing.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _queued);
                // Repetições seguidas (ex.: a mesma falha a cada quadro) viram uma linha com contagem.
                string body = line.Length > 13 ? line[13..] : line;
                if (body == _lastLine) { _repeat++; continue; }
                if (_repeat > 0) msg.Append($"            (repetiu ×{_repeat})\n");
                _repeat = 0;
                _lastLine = body;
                msg.Append(line.Length > 600 ? line[..600] + "…" : line).Append('\n');
                n++;
            }
            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0) msg.Append($"{DateTime.Now:HH:mm:ss.fff} [AVISO] {dropped} linha(s) de diagnóstico descartadas (muitas de uma vez)\n");
            if (msg.Length > 0) _send(Encoding.UTF8.GetBytes(msg.ToString()));
            any = true;
        }
        catch
        {
            // Diagnóstico nunca derruba a transmissão.
        }
        finally
        {
            if (any) try { Updated?.Invoke(); } catch { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Log.Written -= OnLocal;
        _timer.Dispose();
        try { Flush(); } catch { }
    }
}

/// <summary>Arquivos de diagnóstico das sessões e relatório para o GitHub.</summary>
internal static class DiagFiles
{
    public static string Directory => Path.Combine(Log.DataDirectory, "diagnostico");

    public static void Prune(int keep)
    {
        try
        {
            var files = new DirectoryInfo(Directory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in files.Skip(keep)) f.Delete();
        }
        catch { }
    }

    public static FileInfo? Latest()
    {
        try
        {
            return new DirectoryInfo(Directory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        }
        catch { return null; }
    }

    private static readonly Regex Ipv4 = new(@"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b", RegexOptions.Compiled);

    /// <summary>Remove dados pessoais óbvios (endereços IP, pasta do usuário) antes de publicar.</summary>
    public static string Sanitize(string text)
    {
        text = Ipv4.Replace(text, m => m.Value.StartsWith("0.") ? m.Value : "x.x.x.x");
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = text.Replace(Environment.UserName, "<usuário>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>
    /// Endereço de "novo problema" no GitHub já preenchido com o resumo da última sessão
    /// (avisos/erros, sem IPs nem nome de usuário). Nada é enviado sozinho: a pessoa revisa e envia.
    /// </summary>
    public static string IssueUrl(string repo)
    {
        var latest = Latest();
        var body = new StringBuilder();
        body.AppendLine("**O que aconteceu?**").AppendLine().AppendLine("(descreva aqui: jogo/programa, tela cheia ou janela, o que esperava)").AppendLine();
        body.AppendLine($"**Versão:** TransLocal {Updater.Current} • {Environment.OSVersion.VersionString}").AppendLine();
        if (latest is not null)
        {
            string[] lines;
            try { lines = File.ReadAllLines(latest.FullName); } catch { lines = Array.Empty<string>(); }
            var header = lines.FirstOrDefault() ?? "";
            var problems = lines.Skip(1)
                .Where(l => l.Contains("[AVISO]") || l.Contains("[ERRO]") || l.Contains("[ESTADO]"))
                .Select(l => l.Length > 300 ? l[..300] + "…" : l)
                .ToList();
            // Os estados são muitos: mantém só os últimos.
            var keep = problems.Where(l => !l.Contains("[ESTADO]")).TakeLast(40)
                .Concat(problems.Where(l => l.Contains("[ESTADO]")).TakeLast(5)).ToList();
            body.AppendLine($"**Última sessão:** `{latest.Name}`").AppendLine("```").AppendLine(header);
            foreach (var l in keep) body.AppendLine(l);
            body.AppendLine("```");
        }
        string text = Sanitize(body.ToString());
        if (text.Length > 6000) text = text[..6000] + "\n…";
        return $"https://github.com/{repo}/issues/new?title={Uri.EscapeDataString("Problema: ")}&body={Uri.EscapeDataString(text)}";
    }
}
