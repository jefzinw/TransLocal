using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TransLocal.Security;

/// <summary>
/// Identidade criptográfica deste aparelho: chave ECDSA P-256 guardada pelo Windows (CNG, não exportável,
/// no perfil do usuário). A “impressão digital” é o SHA-256 da chave pública; é ela que os aparelhos
/// pareados guardam e conferem a cada conexão.
/// </summary>
internal sealed class DeviceIdentity : IDisposable
{
    private const string KeyName = "TransLocal Device Identity v2";

    private readonly bool _persisted;
    private X509Certificate2? _certificate;

    public ECDsa Key { get; }

    /// <summary>Certificado autoassinado com a chave da identidade (TLS de quem aceita conexões). Criado sob demanda.</summary>
    public X509Certificate2 Certificate => _certificate ??= CreateCertificate();

    /// <summary>Chave pública (SubjectPublicKeyInfo, DER).</summary>
    public byte[] PublicKey { get; }

    public string Fingerprint { get; }
    public string ShortId => FormatShortId(Fingerprint);

    private DeviceIdentity(ECDsa key, bool persisted)
    {
        Key = key;
        _persisted = persisted;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        Fingerprint = FingerprintOf(PublicKey);
    }

    private X509Certificate2 CreateCertificate()
    {
        var req = new CertificateRequest("CN=TransLocal " + ShortId, Key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, critical: false));
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // Chave persistida (CNG) já serve ao TLS do Windows; uma chave efêmera precisa ser reimportada.
        return _persisted ? cert : X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    /// <summary>Identidade a partir de uma chave guardada pelo próprio app (celular).</summary>
    public static DeviceIdentity FromPkcs8(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new DeviceIdentity(key, persisted: false);
    }

    /// <summary>Nova chave exportável (celular guarda no armazenamento privado do app).</summary>
    public static (DeviceIdentity Identity, byte[] Pkcs8) CreateExportable()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (new DeviceIdentity(key, persisted: false), key.ExportPkcs8PrivateKey());
    }

    /// <summary>Carrega (ou cria na primeira vez) a identidade deste aparelho.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static DeviceIdentity LoadOrCreate()
    {
        CngKey key;
        if (CngKey.Exists(KeyName))
        {
            key = CngKey.Open(KeyName);
        }
        else
        {
            key = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.AllUsages,
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            });
            Log.Info("Identidade deste aparelho criada");
        }
        return new DeviceIdentity(new ECDsaCng(key), persisted: true);
    }

    /// <summary>Identidade temporária (testes).</summary>
    public static DeviceIdentity CreateEphemeral() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), persisted: false);

    public byte[] Sign(byte[] data) => Key.SignData(data, HashAlgorithmName.SHA256);

    public static bool Verify(byte[] publicKeySpki, byte[] data, byte[] signature)
    {
        try
        {
            using var k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            return k.KeySize == 256 && k.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static string FingerprintOf(byte[] publicKeySpki) => Convert.ToHexString(SHA256.HashData(publicKeySpki)).ToLowerInvariant();

    /// <summary>Forma curta e legível da impressão digital (ex.: 4F2A-91C0).</summary>
    public static string FormatShortId(string fingerprint) =>
        fingerprint.Length >= 8 ? $"{fingerprint[..4]}-{fingerprint[4..8]}".ToUpperInvariant() : fingerprint.ToUpperInvariant();

    public void Dispose()
    {
        _certificate?.Dispose();
        Key.Dispose();
    }
}
