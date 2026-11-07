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

        public DeviceIdentity LoadIdentity()
        {
            var saved = Get("identity");
            if (saved is not null) return DeviceIdentity.FromPkcs8(Convert.FromBase64String(saved));
            var (identity, pkcs8) = DeviceIdentity.CreateExportable();
            Put("identity", Convert.ToBase64String(pkcs8));
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
        public string? TotpSecret { get => Get("totp"); set => Put("totp", value); }

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
