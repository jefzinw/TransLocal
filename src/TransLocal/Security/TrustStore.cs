using System.Security.Cryptography;
using System.Text;

namespace TransLocal.Security;

/// <summary>Aparelho pareado.</summary>
internal sealed class TrustedDevice
{
    public string Name { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Kind { get; set; } = "pc";
    public DateTime PairedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Último endereço onde foi visto (para pedir a tela de fora de casa).</summary>
    public string? LastAddress { get; set; }

    public string ShortId => DeviceIdentity.FormatShortId(Fingerprint);
}

/// <summary>Lista de aparelhos pareados guardada nas configurações.</summary>
internal sealed class SettingsTrustStore : ITrustStore
{
    private readonly AppSettings _settings;
    private readonly object _sync = new();

    public SettingsTrustStore(AppSettings settings) => _settings = settings;

    public event Action? Changed;

    public bool IsTrusted(string fingerprint)
    {
        lock (_sync) return _settings.TrustedDevices.Any(d => d.Fingerprint == fingerprint);
    }

    public TrustedDevice? Find(string fingerprint)
    {
        lock (_sync) return _settings.TrustedDevices.FirstOrDefault(d => d.Fingerprint == fingerprint);
    }

    public IReadOnlyList<TrustedDevice> All()
    {
        lock (_sync) return _settings.TrustedDevices.ToList();
    }

    public void Trust(string name, string fingerprint, string kind)
    {
        lock (_sync)
        {
            _settings.TrustedDevices.RemoveAll(d => d.Fingerprint == fingerprint);
            _settings.TrustedDevices.Add(new TrustedDevice
            {
                Name = name,
                Fingerprint = fingerprint,
                Kind = kind,
                PairedUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
            });
            _settings.Save();
        }
        Changed?.Invoke();
    }

    public void Touch(string fingerprint, string name, string? address)
    {
        lock (_sync)
        {
            var d = _settings.TrustedDevices.FirstOrDefault(x => x.Fingerprint == fingerprint);
            if (d is null) return;
            d.Name = name;
            d.LastSeenUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(address)) d.LastAddress = address;
            _settings.Save();
        }
    }

    public void Remove(string fingerprint)
    {
        lock (_sync)
        {
            _settings.TrustedDevices.RemoveAll(d => d.Fingerprint == fingerprint);
            _settings.Save();
        }
        Log.Info($"Pareamento removido: {DeviceIdentity.FormatShortId(fingerprint)}");
        Changed?.Invoke();
    }
}

/// <summary>Protege segredos locais com a DPAPI do Windows (só este usuário neste computador consegue ler).</summary>
internal static class LocalSecret
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("TransLocal-secret-v1");

    public static string Protect(byte[] secret) =>
        Convert.ToBase64String(ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser));

    public static byte[]? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;
        try
        {
            return ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível ler um segredo protegido", ex);
            return null;
        }
    }
}
