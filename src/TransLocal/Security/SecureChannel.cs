using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TransLocal.Security;

/// <summary>Para que serve a conexão (definido por quem conecta).</summary>
internal enum ChannelPurpose
{
    /// <summary>Quem conecta vai enviar a sua tela.</summary>
    Stream,

    /// <summary>Quem conecta pede a tela de quem aceita (acesso remoto).</summary>
    Pull,

    /// <summary>Celular que aprova pedidos de acesso.</summary>
    Approver,
}

/// <summary>Aparelho do outro lado, já autenticado.</summary>
internal sealed record PeerInfo(string Name, string Fingerprint, string Kind, bool NewlyPaired)
{
    public string ShortId => DeviceIdentity.FormatShortId(Fingerprint);
}

/// <summary>Pedido de confirmação de pareamento mostrado ao usuário.</summary>
internal sealed record PairingPrompt(string PeerName, string PeerShortId, string PeerKind, string Code, bool Incoming);

internal interface ITrustStore
{
    bool IsTrusted(string fingerprint);
    void Trust(string name, string fingerprint, string kind);
}

/// <summary>Falha de autenticação/pareamento, com mensagem para o usuário.</summary>
internal sealed class AuthException(string message) : Exception(message);

internal sealed class SecureChannelOptions
{
    public required DeviceIdentity Identity { get; init; }
    public required ITrustStore Trust { get; init; }
    public required string LocalName { get; init; }
    public string LocalKind { get; init; } = "pc";

    /// <summary>Mostra o código de pareamento e espera a decisão do usuário (bloqueante). null = sem pareamento.</summary>
    public Func<PairingPrompt, bool>? ConfirmPairing { get; init; }
}

internal sealed class AuthChallenge
{
    public string App { get; set; } = Proto.AppId;
    public int ProtocolVersion { get; set; } = Proto.Version;
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "pc";
    public string PublicKey { get; set; } = "";
    public string Nonce { get; set; } = "";
}

internal sealed class AuthResponse
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "pc";
    public string PublicKey { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Signature { get; set; } = "";
}

internal sealed class AuthResult
{
    public bool TrustsYou { get; set; }
    public bool PairingAllowed { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Conexão protegida entre aparelhos TransLocal:
///  1. TLS 1.2/1.3 (criptografia e integridade de tudo que trafega);
///  2. quem aceita a conexão envia um desafio; quem conecta o assina com a chave da sua identidade
///     (o desafio inclui as duas chaves e a finalidade, então não pode ser reaproveitado);
///  3. cada lado confere se o outro está pareado (impressão digital da chave);
///  4. se não estiver, pareamento: código de 6 dígitos por “compromisso e revelação” — quem conecta se
///     compromete com um número aleatório antes de ver o do outro, então um intermediário não consegue
///     forjar códigos iguais nas duas telas. O usuário confirma o mesmo código nos dois aparelhos.
/// </summary>
internal static class SecureChannel
{
    private static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(15);
    private const int HandshakeReadTimeoutMs = 20_000;
    private const int PairingReadTimeoutMs = 150_000;

    private static readonly SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    // ------------------------------------------------------------------ quem conecta

    /// <summary>Protege uma conexão já aberta (lado que conectou). Bloqueante; use fora da thread da interface.</summary>
    public static (Connection Conn, PeerInfo Peer) Connect(TcpClient client, ChannelPurpose purpose, SecureChannelOptions o)
    {
        Connection.PrepareSocket(client);
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        X509Certificate2? serverCert = null;
        try
        {
            using var cts = new CancellationTokenSource(TlsTimeout);
            ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "translocal",
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                // O certificado é autoassinado: a confiança vem da chave pareada, conferida logo abaixo.
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    serverCert = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    return true;
                },
            }, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
        {
            ssl.Dispose();
            throw new AuthException("Não foi possível estabelecer conexão segura. O outro computador pode estar com uma versão antiga do TransLocal (atualize os dois) — " + ex.Message);
        }

        var conn = new Connection(client, ssl) { ReceiveTimeoutMs = HandshakeReadTimeoutMs };
        try
        {
            var ch = conn.ReceiveJson<AuthChallenge>(MsgType.AuthChallenge);
            if (ch.App != Proto.AppId || ch.ProtocolVersion != Proto.Version)
                throw new AuthException("Versão do TransLocal incompatível. Atualize os dois computadores.");
            byte[] serverKey = Convert.FromBase64String(ch.PublicKey);
            byte[] tlsKey = serverCert!.PublicKey.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(serverKey, tlsKey))
                throw new AuthException("A chave do outro computador não confere com a conexão segura.");
            string serverFp = DeviceIdentity.FingerprintOf(serverKey);
            byte[] nonce = Convert.FromBase64String(ch.Nonce);

            conn.SendJson(MsgType.AuthResponse, new AuthResponse
            {
                Name = o.LocalName,
                Kind = o.LocalKind,
                PublicKey = Convert.ToBase64String(o.Identity.PublicKey),
                Purpose = purpose.ToString(),
                Signature = Convert.ToBase64String(o.Identity.Sign(Transcript(serverKey, o.Identity.PublicKey, nonce, purpose))),
            });

            var res = conn.ReceiveJson<AuthResult>(MsgType.AuthResult);
            if (res.Error is not null) throw new AuthException(res.Error);

            bool weTrust = serverFp == o.Identity.Fingerprint || o.Trust.IsTrusted(serverFp);
            bool newlyPaired = false;
            if (weTrust && res.TrustsYou)
            {
                conn.Send(MsgType.AuthDone);
            }
            else
            {
                if (o.ConfirmPairing is null || !res.PairingAllowed)
                    throw new AuthException(purpose == ChannelPurpose.Pull
                        ? $"Este aparelho não está pareado com {ch.Name}. Faça o pareamento em casa (na mesma rede) antes de acessar de fora."
                        : $"{ch.Name} não aceita pareamento agora.");

                byte[] myNonce = RandomNumberGenerator.GetBytes(32);
                conn.Send(MsgType.PairCommit, SHA256.HashData(myNonce));
                conn.ReceiveTimeoutMs = PairingReadTimeoutMs;
                byte[] theirNonce;
                using (var m = conn.Expect(MsgType.PairNonce)) theirNonce = m.Span.ToArray();
                if (theirNonce.Length != 32) throw new AuthException("Pareamento inválido.");
                conn.Send(MsgType.PairReveal, myNonce);

                string code = Sas(o.Identity.PublicKey, serverKey, myNonce, theirNonce);
                bool ok = o.ConfirmPairing(new PairingPrompt(ch.Name, DeviceIdentity.FormatShortId(serverFp), ch.Kind, code, Incoming: false));
                conn.Send(MsgType.PairDecision, new[] { (byte)(ok ? 1 : 0) });
                bool theirs;
                using (var m = conn.Expect(MsgType.PairDecision)) theirs = m.Length == 1 && m.Span[0] == 1;
                if (!ok) throw new AuthException("Pareamento cancelado.");
                if (!theirs) throw new AuthException($"{ch.Name} recusou o pareamento.");
                o.Trust.Trust(ch.Name, serverFp, ch.Kind);
                newlyPaired = true;
                Log.Info($"Pareado com {ch.Name} ({DeviceIdentity.FormatShortId(serverFp)})");
            }

            conn.MaxMessageBytes = Proto.MaxMessageBytes;
            conn.ReceiveTimeoutMs = 15_000;
            return (conn, new PeerInfo(ch.Name, serverFp, ch.Kind, newlyPaired));
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ quem aceita

    /// <summary>Protege uma conexão recebida. Bloqueante (use numa thread própria).</summary>
    /// <param name="pairingAllowed">Se aceita parear um aparelho desconhecido para essa finalidade.</param>
    public static (Connection Conn, PeerInfo Peer, ChannelPurpose Purpose) Accept(TcpClient client, SecureChannelOptions o,
        Func<ChannelPurpose, bool> pairingAllowed)
    {
        Connection.PrepareSocket(client);
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            using var cts = new CancellationTokenSource(TlsTimeout);
            ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = o.Identity.Certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            ssl.Dispose();
            throw;
        }

        var conn = new Connection(client, ssl) { ReceiveTimeoutMs = HandshakeReadTimeoutMs };
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            conn.SendJson(MsgType.AuthChallenge, new AuthChallenge
            {
                Name = o.LocalName,
                Kind = o.LocalKind,
                PublicKey = Convert.ToBase64String(o.Identity.PublicKey),
                Nonce = Convert.ToBase64String(nonce),
            });

            var resp = conn.ReceiveJson<AuthResponse>(MsgType.AuthResponse);
            if (!Enum.TryParse<ChannelPurpose>(resp.Purpose, out var purpose))
                throw new AuthException("Finalidade desconhecida.");
            byte[] clientKey = Convert.FromBase64String(resp.PublicKey);
            byte[] signature = Convert.FromBase64String(resp.Signature);
            if (!DeviceIdentity.Verify(clientKey, Transcript(o.Identity.PublicKey, clientKey, nonce, purpose), signature))
            {
                conn.SendJson(MsgType.AuthResult, new AuthResult { Error = "Assinatura inválida." });
                throw new AuthException("Assinatura inválida.");
            }
            string clientFp = DeviceIdentity.FingerprintOf(clientKey);
            string name = Sanitize(resp.Name);
            string kind = resp.Kind is "phone" ? "phone" : "pc";
            bool weTrust = clientFp == o.Identity.Fingerprint || o.Trust.IsTrusted(clientFp);
            bool canPair = o.ConfirmPairing is not null && pairingAllowed(purpose);
            conn.SendJson(MsgType.AuthResult, new AuthResult { TrustsYou = weTrust, PairingAllowed = canPair });

            bool newlyPaired = false;
            using (var next = conn.Receive())
            {
                if (next.Type == MsgType.AuthDone)
                {
                    if (!weTrust) throw new AuthException($"{name} não está pareado.");
                }
                else if (next.Type == MsgType.PairCommit && canPair && next.Length == 32)
                {
                    byte[] commit = next.Span.ToArray();
                    byte[] myNonce = RandomNumberGenerator.GetBytes(32);
                    conn.ReceiveTimeoutMs = PairingReadTimeoutMs;
                    conn.Send(MsgType.PairNonce, myNonce);
                    byte[] theirNonce;
                    using (var m = conn.Expect(MsgType.PairReveal)) theirNonce = m.Span.ToArray();
                    if (theirNonce.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(theirNonce), commit))
                        throw new AuthException("Pareamento inválido (compromisso não confere).");

                    string code = Sas(clientKey, o.Identity.PublicKey, theirNonce, myNonce);
                    bool ok = o.ConfirmPairing!(new PairingPrompt(name, DeviceIdentity.FormatShortId(clientFp), kind, code, Incoming: true));
                    conn.Send(MsgType.PairDecision, new[] { (byte)(ok ? 1 : 0) });
                    bool theirs;
                    using (var m = conn.Expect(MsgType.PairDecision)) theirs = m.Length == 1 && m.Span[0] == 1;
                    if (!ok || !theirs) throw new AuthException("Pareamento não concluído.");
                    o.Trust.Trust(name, clientFp, kind);
                    newlyPaired = true;
                    Log.Info($"Pareado com {name} ({DeviceIdentity.FormatShortId(clientFp)})");
                }
                else
                {
                    throw new AuthException($"{name} não está pareado.");
                }
            }

            conn.MaxMessageBytes = Proto.MaxMessageBytes;
            conn.ReceiveTimeoutMs = 15_000;
            return (conn, new PeerInfo(name, clientFp, kind, newlyPaired), purpose);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ auxiliares

    /// <summary>O que quem conecta assina: liga a assinatura às duas chaves, ao desafio e à finalidade.</summary>
    private static byte[] Transcript(byte[] serverKey, byte[] clientKey, byte[] nonce, ChannelPurpose purpose)
    {
        using var ms = new MemoryStream();
        void Put(byte[] b)
        {
            ms.Write(BitConverter.GetBytes(b.Length));
            ms.Write(b);
        }
        Put(Encoding.ASCII.GetBytes("TransLocal-auth-v2"));
        Put(serverKey);
        Put(clientKey);
        Put(nonce);
        Put(Encoding.ASCII.GetBytes(purpose.ToString()));
        return ms.ToArray();
    }

    /// <summary>Código de comparação de 6 dígitos (Short Authentication String).</summary>
    public static string Sas(byte[] clientKey, byte[] serverKey, byte[] clientNonce, byte[] serverNonce)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("TransLocal-SAS-v2"));
        ms.Write(clientKey);
        ms.Write(serverKey);
        ms.Write(clientNonce);
        ms.Write(serverNonce);
        byte[] h = SHA256.HashData(ms.ToArray());
        uint v = BitConverter.ToUInt32(h, 0) % 1_000_000;
        return $"{v / 1000:000} {v % 1000:000}";
    }

    private static string Sanitize(string? name)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) s = "Aparelho";
        return s.Length > 40 ? s[..40] : s;
    }
}
