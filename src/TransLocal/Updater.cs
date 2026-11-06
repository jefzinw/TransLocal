using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TransLocal;

/// <summary>Atualização disponível no GitHub.</summary>
internal sealed record UpdateInfo(Version Version, string Tag, string AssetName, Uri DownloadUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// Atualização automática a partir das Releases do GitHub: consulta a mais recente, baixa o instalador (cópia
/// instalada) ou o executável único (cópia portátil), confere tamanho e SHA-256 e aplica.
/// </summary>
internal static class Updater
{
    public const string Repository = "jefzinw/TransLocal";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";

    // A ordem importa: o cliente HTTP usa a versão atual no cabeçalho User-Agent.
    public static Version Current { get; } = ParseVersion(SystemIntegration.Version) ?? new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    public static string DownloadDirectory => Path.Combine(Path.GetTempPath(), "TransLocal-Update");

    private static string ExeDirectory => Path.GetDirectoryName(SystemIntegration.ExePath)!;

    /// <summary>Instalado pelo instalador (há um desinstalador ao lado do executável).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(ExeDirectory, "unins000.exe"));

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TransLocal", Current.ToString()));
        return c;
    }

    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Retorna a atualização se houver uma versão mais nova que a atual; null caso contrário.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct, bool installed)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub respondeu {(int)resp.StatusCode} ({resp.ReasonPhrase})");

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version is null || version <= Current) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            bool wanted = installed
                ? name.StartsWith("TransLocal-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                : name.Equals("TransLocal.exe", StringComparison.OrdinalIgnoreCase);
            if (!wanted) continue;

            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Endereço de download inesperado: " + url);
            string? sha = null;
            if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String &&
                d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = digest[7..].ToLowerInvariant();
            return new UpdateInfo(version, tag, name, url, asset.GetProperty("size").GetInt64(), sha,
                root.GetProperty("html_url").GetString() ?? ReleasesPage);
        }
        Log.Warn($"Versão {version} encontrada, mas sem o arquivo de {(installed ? "instalação" : "executável")}");
        return null;
    }

    /// <summary>Baixa e confere (tamanho e SHA-256). Retorna o caminho do arquivo.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(DownloadDirectory);
        string path = Path.Combine(DownloadDirectory, info.AssetName);
        string part = path + ".part";

        using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long total = 0;
            int lastPct = -1, n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                total += n;
                int pct = info.Size > 0 ? (int)(total * 100 / info.Size) : 0;
                if (pct != lastPct) progress?.Report(lastPct = pct);
            }
        }

        var fi = new FileInfo(part);
        if (fi.Length != info.Size)
        {
            fi.Delete();
            throw new InvalidDataException($"Download incompleto ({fi.Length} de {info.Size} bytes).");
        }
        string hash;
        await using (var fs = File.OpenRead(part))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (info.Sha256 is not null && hash != info.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException("O arquivo baixado não confere com o publicado (SHA-256 diferente).");
        }
        File.Move(part, path, overwrite: true);
        Log.Info($"Atualização {info.Version} baixada: {path} ({info.Size} bytes, SHA-256 {hash}{(info.Sha256 is null ? ", sem referência do GitHub" : ", confere")})");
        return path;
    }

    /// <summary>
    /// Executa o instalador em modo silencioso. Ele pede permissão de administrador, fecha esta instância
    /// (comando --quit) e reabre o TransLocal ao terminar. Retorna o processo do instalador.
    /// </summary>
    public static Process? StartInstaller(string setupPath) =>
        Process.Start(new ProcessStartInfo(setupPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1")
        {
            UseShellExecute = true,
        });

    /// <summary>
    /// Cópia portátil: renomeia o executável em uso (o Windows permite) e coloca o novo no lugar.
    /// Retorna false (e desfaz) se não houver permissão de escrita na pasta.
    /// </summary>
    public static bool ReplaceExecutable(string targetExe, string newExe)
    {
        string old = Path.Combine(Path.GetDirectoryName(targetExe)!, "TransLocal.old.exe");
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(targetExe, old);
            File.Copy(newExe, targetExe);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível substituir o executável", ex);
            try
            {
                if (!File.Exists(targetExe) && File.Exists(old)) File.Move(old, targetExe);
            }
            catch { }
            return false;
        }
    }

    public static void RelaunchUpdated() =>
        Process.Start(new ProcessStartInfo(SystemIntegration.ExePath, "--updated --minimized") { UseShellExecute = false });

    /// <summary>Remove restos de atualizações anteriores (executável antigo e downloads).</summary>
    public static void Cleanup()
    {
        try
        {
            string old = Path.Combine(ExeDirectory, "TransLocal.old.exe");
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }
        try
        {
            if (Directory.Exists(DownloadDirectory)) Directory.Delete(DownloadDirectory, recursive: true);
        }
        catch { }
    }
}
