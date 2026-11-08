using Android.Content;
using TransLocal.Security;
using TransLocal.Streaming;

namespace TransLocal
{
    /// <summary>Registro (versão Android do Log do app Windows, usado pelo código compartilhado).</summary>
    internal static class Log
    {
        private const string Tag = "TransLocal";
        public static void Info(string message) => global::Android.Util.Log.Info(Tag, message);
        public static void Warn(string message, Exception? ex = null) =>
            global::Android.Util.Log.Warn(Tag, ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");
        public static void Error(string message, Exception? ex = null) =>
            global::Android.Util.Log.Error(Tag, ex is null ? message : $"{message} :: {ex}");
    }
}

namespace TransLocal.Mobile
{
    /// <summary>
    /// Dados do app no armazenamento privado do Android (só este app lê): identidade do celular,
    /// computador pareado e segredo do código de autorização.
    /// </summary>
    internal sealed class PhoneStore : ITrustStore
    {
        private readonly ISharedPreferences _prefs;

        public PhoneStore(Context context) =>
            _prefs = context.GetSharedPreferences("translocal", FileCreationMode.Private)!;

        private string? Get(string key) => _prefs.GetString(key, null);

        private void Put(string key, string? value)
        {
            var e = _prefs.Edit()!;
            if (value is null) e.Remove(key); else e.PutString(key, value);
            e.Commit();
        }

        // ---- Segredos (chave do aparelho e código de autorização) cifrados pelo Android Keystore ----
        // A chave AES fica no hardware seguro do celular e nunca sai dele: mesmo com root ou cópia do armazenamento,
        // os segredos não são legíveis. Valores antigos em texto puro são cifrados na primeira leitura.
        private const string Sealed = "ks1:";
        private const string KeyAlias = "translocal-store";

        private static Javax.Crypto.ISecretKey StoreKey()
        {
            var ks = Java.Security.KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);
            if (ks.GetEntry(KeyAlias, null) is Java.Security.KeyStore.SecretKeyEntry entry) return entry.SecretKey!;
            var gen = Javax.Crypto.KeyGenerator.GetInstance(global::Android.Security.Keystore.KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
            gen.Init(new global::Android.Security.Keystore.KeyGenParameterSpec.Builder(KeyAlias,
                    global::Android.Security.Keystore.KeyStorePurpose.Encrypt | global::Android.Security.Keystore.KeyStorePurpose.Decrypt)
                .SetBlockModes(global::Android.Security.Keystore.KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(global::Android.Security.Keystore.KeyProperties.EncryptionPaddingNone)
                .SetKeySize(256)
                .Build());
            return gen.GenerateKey()!;
        }

        private static string Seal(string plain)
        {
            var c = Javax.Crypto.Cipher.GetInstance("AES/GCM/NoPadding")!;
            c.Init(Javax.Crypto.CipherMode.EncryptMode, StoreKey());
            byte[] ct = c.DoFinal(System.Text.Encoding.UTF8.GetBytes(plain))!;
            return Sealed + Convert.ToBase64String(c.GetIV()!) + ":" + Convert.ToBase64String(ct);
        }

        private static string Open(string stored)
        {
            var parts = stored[Sealed.Length..].Split(':');
            var c = Javax.Crypto.Cipher.GetInstance("AES/GCM/NoPadding")!;
            c.Init(Javax.Crypto.CipherMode.DecryptMode, StoreKey(), new Javax.Crypto.Spec.GCMParameterSpec(128, Convert.FromBase64String(parts[0])));
            return System.Text.Encoding.UTF8.GetString(c.DoFinal(Convert.FromBase64String(parts[1]))!);
        }

        private string? GetSecret(string key)
        {
            var v = Get(key);
            if (v is null) return null;
            if (v.StartsWith(Sealed, StringComparison.Ordinal))
            {
                try { return Open(v); }
                catch (Exception ex)
                {
                    // Chave do Keystore apagada (ex.: credenciais do aparelho limpas): o segredo não pode mais ser lido.
                    Log.Warn($"Segredo “{key}” ilegível (chave do Keystore perdida); será necessário parear de novo", ex);
                    return null;
                }
            }
            PutSecret(key, v); // migração: valor antigo em texto puro passa a ser guardado cifrado
            return v;
        }

        private void PutSecret(string key, string? value)
        {
            if (value is null) { Put(key, null); return; }
            try { Put(key, Seal(value)); }
            catch (Exception ex)
            {
                // Aparelho sem Keystore utilizável: continua funcionando no armazenamento privado do app.
                Log.Warn("Keystore indisponível; segredo guardado só no armazenamento privado do app", ex);
                Put(key, value);
            }
        }

        public DeviceIdentity LoadIdentity()
        {
            var saved = GetSecret("identity");
            if (saved is not null) return DeviceIdentity.FromPkcs8(Convert.FromBase64String(saved));
            var (identity, pkcs8) = DeviceIdentity.CreateExportable();
            PutSecret("identity", Convert.ToBase64String(pkcs8));
            return identity;
        }

        public string DeviceName
        {
            get => Get("deviceName") ?? ((global::Android.OS.Build.Manufacturer ?? "") + " " + (global::Android.OS.Build.Model ?? "Celular")).Trim();
            set => Put("deviceName", value);
        }

        public string? PcName { get => Get("pcName"); set => Put("pcName", value); }
        public string? PcFingerprint { get => Get("pcFp"); set => Put("pcFp", value); }
        public string? PcAddress { get => Get("pcAddress"); set => Put("pcAddress", value); }
        public string? TotpSecret { get => GetSecret("totp"); set => PutSecret("totp", value); }

        public bool HasPc => PcFingerprint is not null && PcAddress is not null;

        // Confiança: o celular confia só no computador pareado.
        public bool IsTrusted(string fingerprint) => fingerprint == PcFingerprint;

        public void Trust(string name, string fingerprint, string kind)
        {
            PcFingerprint = fingerprint;
            PcName = name;
        }

        public void Forget()
        {
            PcFingerprint = null;
            PcName = null;
            PcAddress = null;
            TotpSecret = null;
        }

        public (string Host, int Port) Endpoint()
        {
            string a = PcAddress ?? "";
            int colon = a.LastIndexOf(':');
            if (colon > 0 && a.IndexOf(':') == colon && int.TryParse(a[(colon + 1)..], out var p)) return (a[..colon], p);
            return (a, Proto.VideoPort);
        }
    }

    /// <summary>Estado em memória compartilhado entre o serviço e a tela.</summary>
    internal static class PhoneState
    {
        private static readonly object Sync = new();
        private static readonly List<ApprovalRequestMessage> PendingList = new();
        private static Connection? _current;

        public static string Status { get; private set; } = "Iniciando…";
        public static bool Connected { get; private set; }
        public static ApproverStatusMessage? LastStatus { get; set; }

        /// <summary>Algo mudou (disparado de qualquer thread).</summary>
        public static event Action? Changed;

        /// <summary>Acorda o serviço para tentar conectar já (ex.: app aberto, endereço alterado).</summary>
        public static readonly AutoResetEvent Kick = new(false);

        public static IReadOnlyList<ApprovalRequestMessage> Pending
        {
            get { lock (Sync) return PendingList.ToList(); }
        }

        public static void SetStatus(string text, bool connected)
        {
            Status = text;
            Connected = connected;
            Changed?.Invoke();
        }

        public static void SetConnection(Connection? conn)
        {
            lock (Sync)
            {
                _current = conn;
                if (conn is null) PendingList.Clear();
            }
            Changed?.Invoke();
        }

        public static void AddPending(ApprovalRequestMessage r)
        {
            lock (Sync)
            {
                PendingList.RemoveAll(p => p.Id == r.Id);
                PendingList.Add(r);
            }
            Changed?.Invoke();
        }

        public static void RemovePending(string id)
        {
            lock (Sync) PendingList.RemoveAll(p => p.Id == id);
            Changed?.Invoke();
        }

        /// <summary>Envia a resposta ao computador. Retorna false se não há conexão.</summary>
        public static bool Respond(string id, bool approved)
        {
            Connection? conn;
            lock (Sync) conn = _current;
            if (conn is null) return false;
            try
            {
                conn.SendJson(MsgType.ApprovalResponse, new ApprovalResponseMessage { Id = id, Approved = approved });
                RemovePending(id);
                Log.Info($"Pedido {id} {(approved ? "aprovado" : "negado")}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Falha ao responder o pedido", ex);
                return false;
            }
        }
    }
}
