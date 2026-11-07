using System.Security.Cryptography;
using System.Text;

namespace TransLocal.Security;

/// <summary>
/// Código de autorização que muda a cada 30 s (TOTP, RFC 6238 — o mesmo dos apps autenticadores).
/// O celular (app TransLocal ou qualquer autenticador) mostra o código; o computador o confere.
/// </summary>
internal sealed class Totp
{
    private const int Digits = 6;
    private const int StepSeconds = 30;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(10);

    private readonly byte[] _secret;
    private readonly object _sync = new();
    private long _lastUsedCounter = -1;
    private int _failures;
    private DateTime _lockedUntilUtc;

    public Totp(byte[] secret) => _secret = secret;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>A partir do segredo em Base32 (como o celular o recebe do computador).</summary>
    public static Totp FromBase32(string secret) => new(Base32Decode(secret));

    /// <summary>Segundos até o código atual mudar.</summary>
    public static int SecondsLeft => StepSeconds - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % StepSeconds);

    private static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (char ch in text.ToUpperInvariant().Where(c => c != '=' && !char.IsWhiteSpace(c)))
        {
            int v = alphabet.IndexOf(ch);
            if (v < 0) throw new FormatException("Segredo Base32 inválido.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }

    public string Base32Secret => Base32Encode(_secret);

    /// <summary>Endereço para cadastrar em apps autenticadores (QR code).</summary>
    public string OtpAuthUri(string account) =>
        $"otpauth://totp/TransLocal:{Uri.EscapeDataString(account)}?secret={Base32Secret}&issuer=TransLocal&digits={Digits}&period={StepSeconds}";

    public string Current() => Compute(_secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds);

    /// <summary>Confere o código (aceita o intervalo anterior/seguinte), impede reuso e bloqueia após 5 erros.</summary>
    public bool Verify(string? code, out string? error)
    {
        error = null;
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        lock (_sync)
        {
            if (DateTime.UtcNow < _lockedUntilUtc)
            {
                error = $"Muitos códigos errados. Tente de novo depois das {_lockedUntilUtc.ToLocalTime():HH:mm}.";
                return false;
            }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
            if (code.Length == Digits)
            {
                for (long c = now - 1; c <= now + 1; c++)
                {
                    if (c <= _lastUsedCounter) continue; // código já usado
                    if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(_secret, c)), Encoding.ASCII.GetBytes(code)))
                    {
                        _lastUsedCounter = c;
                        _failures = 0;
                        return true;
                    }
                }
            }
            _failures++;
            if (_failures >= MaxFailures)
            {
                _lockedUntilUtc = DateTime.UtcNow + LockoutTime;
                _failures = 0;
                Log.Warn("Código de autorização: muitas tentativas erradas; bloqueado por 10 minutos");
            }
            error = "Código de autorização incorreto ou vencido.";
            return false;
        }
    }

    private static string Compute(byte[] secret, long counter)
    {
        Span<byte> msg = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(msg, counter);
        byte[] hash = HMACSHA1.HashData(secret, msg);
        int offset = hash[^1] & 0x0F;
        int bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
