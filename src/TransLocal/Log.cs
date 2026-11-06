using System.Text;

namespace TransLocal;

internal static class Log
{
    private static readonly object Sync = new();
    private static string? _path;

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TransLocal");

    public static string? FilePath => _path;

    /// <summary>Quando true, também escreve no console (modos --bench/--selftest).</summary>
    public static bool Echo { get; set; }

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            _path = Path.Combine(DataDirectory, "TransLocal.log");
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                var old = _path + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(_path, old);
            }
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? ex = null) =>
        Write("AVISO", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    public static void Error(string message, Exception? ex = null) =>
        Write("ERRO", ex is null ? message : $"{message} :: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (Echo)
        {
            try { Console.WriteLine(line); } catch { }
        }
        if (_path is null) return;
        lock (Sync)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }
}
