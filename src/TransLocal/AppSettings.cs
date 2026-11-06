using System.Text.Json;

namespace TransLocal;

internal sealed class AppSettings
{
    // Geral
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Procura e instala atualizações do GitHub ao abrir (e a cada 12 h).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Última versão cuja instalação automática foi tentada e quando (evita repetir em ciclo).</summary>
    public string? LastUpdateAttempt { get; set; }
    public DateTime LastUpdateAttemptUtc { get; set; }

    // Receptor (este PC mostra a tela de outro)
    public bool ReceiverEnabled { get; set; } = true;
    public bool ViewerFullscreen { get; set; } = true;
    public string? ViewerMonitor { get; set; }
    public bool ReceiverPlayAudio { get; set; } = true;
    public bool ShowStats { get; set; }

    /// <summary>Ao exibir, já começa controlando (mouse, teclado e controles) quando o outro computador permite.</summary>
    public bool ViewerAutoControl { get; set; } = true;

    /// <summary>Pede ao transmissor uma transmissão leve (até 1366×768, 30 quadros/s, mais compressão).</summary>
    public bool ReceiverLight { get; set; }

    /// <summary>Mapeamentos de controles configurados no assistente (formato SDL), um por controle.</summary>
    public List<string> ControllerMappings { get; set; } = new();

    // Transmissor (este PC envia a sua tela)
    public string? SenderMonitor { get; set; }
    public int SenderFps { get; set; } = 30;
    public int SenderQuality { get; set; } = 75;
    public bool SenderAudio { get; set; } = true;
    public bool SenderAllowControl { get; set; }

    /// <summary>"auto" (tela do receptor), "original" ou limite como "1280x720".</summary>
    public string SenderResolution { get; set; } = "auto";
    public string? LastReceiverAddress { get; set; }
    public string? LastReceiverName { get; set; }

    // HDMI (placa de captura)
    public string? HdmiDevice { get; set; }
    public string? HdmiFormat { get; set; }
    public string? HdmiAudioDeviceId { get; set; }
    public bool HdmiFullscreen { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TransLocal", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (s is not null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Falha ao ler configurações; usando padrão", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Falha ao salvar configurações", ex);
        }
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DeviceName)) DeviceName = Environment.MachineName;
        DeviceName = DeviceName.Trim();
        if (DeviceName.Length > 40) DeviceName = DeviceName[..40];
        SenderFps = Math.Clamp(SenderFps, 5, 60);
        SenderQuality = Math.Clamp(SenderQuality, 30, 95);
    }
}
