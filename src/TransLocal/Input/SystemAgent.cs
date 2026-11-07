using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;

namespace TransLocal.Input;

/// <summary>
/// Serviço do TransLocal (opcional, como o serviço do Parsec). Roda como SYSTEM na sessão 0 e só faz uma
/// coisa: manter o agente do TransLocal rodando como SYSTEM dentro da sessão do usuário conectado.
/// Só um processo SYSTEM na sessão do usuário alcança a área de trabalho segura do Windows
/// (confirmações do UAC, Ctrl+Alt+Del) e qualquer janela de administrador.
/// O serviço não escuta rede nem pipes; quem conversa com o agente é o TransLocal da sessão,
/// por um pipe local verificado dos dois lados (ver ElevatedInput).
/// </summary>
internal sealed class TransLocalService : ServiceBase
{
    public const string Name = "TransLocalService";
    public const string DisplayNameText = "TransLocal (controle de telas de administrador)";

    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _kick = new(false);
    private Thread? _thread;
    private Process? _agent;
    private uint _agentSession = uint.MaxValue;

    public TransLocalService()
    {
        ServiceName = Name;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        Log.Info($"Serviço iniciado ({ElevatedInput.ExePath})");
        if (!ElevatedInput.TrustedLocation)
        {
            Log.Warn("Serviço: executável fora de Arquivos de Programas; o agente não será iniciado");
            return;
        }
        _thread = new Thread(Monitor) { IsBackground = true, Name = "TransLocal serviço" };
        _thread.Start();
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        Log.Info($"Serviço: sessão {change.SessionId} {change.Reason}");
        _kick.Set();
    }

    protected override void OnStop() => Shutdown();
    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _stop.Set();
        _thread?.Join(3000);
        KillAgent();
        Log.Info("Serviço parado");
    }

    /// <summary>A cada 2 s (ou ao trocar de sessão): agente vivo na sessão do console com usuário conectado.</summary>
    private void Monitor()
    {
        var handles = new WaitHandle[] { _stop, _kick };
        while (true)
        {
            try
            {
                uint session = AgentLauncher.WTSGetActiveConsoleSessionId();
                bool hasUser = session != uint.MaxValue && AgentLauncher.SessionUserSid(session) is not null;
                bool alive = _agent is { HasExited: false };
                if (!hasUser)
                {
                    if (alive) KillAgent();
                }
                else if (!alive || _agentSession != session)
                {
                    KillAgent();
                    _agent = AgentLauncher.StartInSession(session);
                    _agentSession = session;
                    if (_agent is not null) Log.Info($"Agente iniciado na sessão {session} (pid {_agent.Id})");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Serviço: falha ao manter o agente", ex);
            }
            if (WaitHandle.WaitAny(handles, 2000) == 0) return;
        }
    }

    private void KillAgent()
    {
        var a = _agent;
        _agent = null;
        _agentSession = uint.MaxValue;
        if (a is null) return;
        try { if (!a.HasExited) a.Kill(); } catch { }
        a.Dispose();
    }

    /// <summary>--service: chamado pelo Gerenciador de Serviços.</summary>
    public static int RunAsService()
    {
        Log.InitAt(ServiceLogDirectory, "servico.log");
        ServiceBase.Run(new TransLocalService());
        return 0;
    }

    /// <summary>Registros do serviço e do agente (legíveis pelos usuários, gravados só pelo sistema).</summary>
    public static string ServiceLogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TransLocal");
}

/// <summary>Cria o agente como SYSTEM dentro da sessão do usuário (CreateProcessAsUser com o token do serviço).</summary>
internal static class AgentLauncher
{
    /// <summary>SID do usuário conectado na sessão (null se ninguém entrou ainda).</summary>
    public static string? SessionUserSid(uint session)
    {
        if (!WTSQueryUserToken(session, out var userToken)) return null;
        try
        {
            using var id = new WindowsIdentity(userToken);
            return id.User?.Value;
        }
        finally { CloseHandle(userToken); }
    }

    public static Process? StartInSession(uint session)
    {
        string? userSid = SessionUserSid(session);
        if (userSid is null) return null;
        IntPtr own = IntPtr.Zero, dup = IntPtr.Zero, sd = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ALL_ACCESS, out own)) throw new Win32Exception();
            if (!DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out dup))
                throw new Win32Exception();
            if (!SetTokenInformation(dup, 12 /* TokenSessionId */, ref session, 4)) throw new Win32Exception();

            // O usuário da sessão pode CONSULTAR o processo do agente (o TransLocal confere o executável
            // antes de conversar com ele), mas não pode abri-lo para escrever ou injetar código.
            string sddl = $"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x101000;;;{userSid})";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, IntPtr.Zero)) throw new Win32Exception();
            var pa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder($"\"{ElevatedInput.ExePath}\" --system-agent {userSid}");
            if (!CreateProcessAsUserW(dup, null, cmd, ref pa, IntPtr.Zero, false, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero, Path.GetDirectoryName(ElevatedInput.ExePath), ref si, out var pi))
                throw new Win32Exception();
            CloseHandle(pi.hThread);
            var p = Process.GetProcessById(pi.dwProcessId);
            CloseHandle(pi.hProcess);
            return p;
        }
        catch (Exception ex)
        {
            Log.Warn($"Não foi possível iniciar o agente na sessão {session}", ex);
            return null;
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
            if (dup != IntPtr.Zero) CloseHandle(dup);
            if (own != IntPtr.Zero) CloseHandle(own);
        }
    }

    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] internal static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impersonation, int type, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int cls, ref uint value, int length);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? app, StringBuilder cmd, ref SECURITY_ATTRIBUTES pa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out IntPtr sd, IntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}

/// <summary>
/// Faz a thread atual seguir a área de trabalho que recebe a entrada (Default ↔ Winlogon do UAC).
/// Só funciona como SYSTEM e em threads sem janelas.
/// </summary>
internal sealed class DesktopFollower : IDisposable
{
    private IntPtr _handle;
    public string Current { get; private set; } = "";

    /// <summary>True se a thread está na área de trabalho de entrada agora; Changed indica troca.</summary>
    public bool Follow(out bool changed)
    {
        changed = false;
        IntPtr h = OpenInputDesktop(0, false, GENERIC_ALL);
        if (h == IntPtr.Zero) return false;
        string name = NameOf(h);
        if (name == Current && _handle != IntPtr.Zero)
        {
            CloseDesktop(h);
            return true;
        }
        if (!SetThreadDesktop(h))
        {
            CloseDesktop(h);
            return false;
        }
        if (_handle != IntPtr.Zero) CloseDesktop(_handle);
        _handle = h;
        changed = Current.Length > 0;
        Current = name;
        return true;
    }

    /// <summary>Nome da área de trabalho que recebe a entrada agora ("Default", "Winlogon"…), sem trocar.</summary>
    public static string InputDesktopName()
    {
        IntPtr h = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (h == IntPtr.Zero) return "?";
        try { return NameOf(h); }
        finally { CloseDesktop(h); }
    }

    private static string NameOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return GetUserObjectInformationW(h, 2 /* UOI_NAME */, sb, sb.Capacity * 2, out _) ? sb.ToString() : "?";
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseDesktop(_handle);
        _handle = IntPtr.Zero;
    }

    private const uint GENERIC_ALL = 0x10000000;
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr obj, int index, StringBuilder info, int length, out int needed);
}

/// <summary>
/// No agente SYSTEM: enquanto o TransLocal pedir (o DXGI dele recebe "acesso negado" na tela segura do UAC),
/// captura a área de trabalho de entrada a ~15 quadros/s e escreve na área compartilhada que ele criou.
/// Thread própria, sem janelas (precisa trocar de área de trabalho).
/// </summary>
internal sealed class SecureDesktopCapture
{
    private readonly AutoResetEvent _wake = new(false);
    private volatile string? _name;
    private Rectangle _bounds;
    private long _lastRequest;

    public SecureDesktopCapture()
    {
        new Thread(Loop) { IsBackground = true, Name = "TransLocal captura segura" }.Start();
    }

    public void Request(string mappingName, Rectangle bounds)
    {
        _bounds = bounds;
        _name = mappingName;
        Volatile.Write(ref _lastRequest, Environment.TickCount64);
        _wake.Set();
    }

    private void Loop()
    {
        using var follower = new DesktopFollower();
        Video.ScreenCapturer? capturer = null;
        SecureFrameBuffer? buffer = null;
        string? bufferName = null;
        bool active = false;
        while (true)
        {
            try
            {
                if (Environment.TickCount64 - Volatile.Read(ref _lastRequest) > 2000)
                {
                    if (active) Log.Info("Agente: captura da tela segura parada");
                    active = false;
                    capturer?.Dispose(); capturer = null;
                    buffer?.Dispose(); buffer = null; bufferName = null;
                    _wake.WaitOne(1000);
                    continue;
                }
                if (!follower.Follow(out bool changed)) { Thread.Sleep(200); continue; }
                if (changed || !active)
                {
                    capturer?.Dispose();
                    capturer = null;
                    Log.Info($"Agente: capturando a área de trabalho “{follower.Current}” para a transmissão");
                }
                active = true;
                string? name = _name;
                if (name is null) { Thread.Sleep(100); continue; }
                if (buffer is null || bufferName != name)
                {
                    buffer?.Dispose();
                    buffer = SecureFrameBuffer.OpenForWriting(name);
                    bufferName = name;
                    if (buffer is null) { Thread.Sleep(500); continue; }
                }
                if (capturer is null)
                {
                    var b = _bounds;
                    var screen = Screen.AllScreens.FirstOrDefault(s => s.Bounds == b) ?? Screen.PrimaryScreen!;
                    capturer = Video.ScreenCapturer.Create(screen);
                }
                if (capturer.Capture(drawCursor: true))
                    buffer.Write(capturer.Bits, capturer.Width, capturer.Height, !string.Equals(follower.Current, "Default", StringComparison.OrdinalIgnoreCase));
                Thread.Sleep(66);
            }
            catch (Exception ex)
            {
                Log.Warn("Agente: falha na captura da tela segura", ex);
                capturer?.Dispose();
                capturer = null;
                Thread.Sleep(1000);
            }
        }
    }
}

/// <summary>
/// Quadro da área de trabalho segura em memória compartilhada. Quem cria é o TransLocal da sessão
/// (nome aleatório, acesso padrão: o usuário e o sistema); o agente SYSTEM só abre e escreve.
/// Cabeçalho de 64 bytes: [seq:int32 (ímpar = escrevendo)][largura][altura][mesa segura:int32]; depois BGRA.
/// </summary>
internal sealed unsafe class SecureFrameBuffer : IDisposable
{
    private const int Header = 64;
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _ptr;
    private int _lastSeq;

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Lado do TransLocal: cria a área com nome aleatório.</summary>
    public SecureFrameBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Name = $@"Local\TransLocal.SecureFrame.{Guid.NewGuid():N}";
        _file = MemoryMappedFile.CreateNew(Name, Header + (long)width * height * 4);
        _view = _file.CreateViewAccessor();
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _ptr = p + _view.PointerOffset;
    }

    private SecureFrameBuffer(string name, MemoryMappedFile file)
    {
        Name = name;
        _file = file;
        _view = _file.CreateViewAccessor();
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _ptr = p + _view.PointerOffset;
        Width = Height = 0;
    }

    /// <summary>Lado do agente: abre a área que o TransLocal criou (só nomes do próprio TransLocal).</summary>
    public static SecureFrameBuffer? OpenForWriting(string name)
    {
        if (!name.StartsWith(@"Local\TransLocal.SecureFrame.", StringComparison.Ordinal) || name.Length > 80) return null;
        try { return new SecureFrameBuffer(name, MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite)); }
        catch { return null; }
    }

    public long Capacity => _view.Capacity - Header;

    /// <summary>Agente: escreve um quadro BGRA (top-down, largura×4 por linha).</summary>
    public void Write(IntPtr bgra, int width, int height, bool secureDesktop)
    {
        long bytes = (long)width * height * 4;
        if (bytes > Capacity) return;
        int* h = (int*)_ptr;
        int seq = h[0];
        Volatile.Write(ref h[0], seq | 1);
        h[1] = width;
        h[2] = height;
        h[3] = secureDesktop ? 1 : 0;
        Buffer.MemoryCopy((void*)bgra, _ptr + Header, Capacity, bytes);
        Volatile.Write(ref h[0], (seq | 1) + 1);
    }

    /// <summary>TransLocal: copia o quadro novo, se houver e tiver o tamanho esperado.</summary>
    public bool TryRead(byte* dst, out bool secureDesktop)
    {
        secureDesktop = false;
        int* h = (int*)_ptr;
        int seq = Volatile.Read(ref h[0]);
        if (seq == 0 || (seq & 1) != 0 || seq == _lastSeq) return false;
        if (h[1] != Width || h[2] != Height) return false;
        secureDesktop = h[3] != 0;
        long bytes = (long)Width * Height * 4;
        Buffer.MemoryCopy(_ptr + Header, dst, bytes, bytes);
        if (Volatile.Read(ref h[0]) != seq) return false; // reescrito durante a cópia
        _lastSeq = seq;
        return true;
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>Instala/remove o serviço (executado como administrador, com confirmação do UAC).</summary>
internal static class ServiceSetup
{
    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(TransLocalService.Name);
            _ = sc.Status;
            return true;
        }
        catch { return false; }
    }

    public static bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(TransLocalService.Name);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    /// <summary>--install-service: cria (ou atualiza) e inicia o serviço; remove a tarefa antiga do auxiliar.</summary>
    public static int Install()
    {
        try
        {
            if (!Streaming.ProcessInfo.CurrentIsElevated()) { Log.Warn("Instalação do serviço sem privilégio de administrador"); return 2; }
            if (!ElevatedInput.TrustedLocation) { Log.Warn("O serviço exige o TransLocal instalado em Arquivos de Programas"); return 3; }
            // O agente do serviço substitui o auxiliar da tarefa agendada (versões 1.4.x): remove antes de iniciar.
            ElevatedInput.RemoveTaskOnly();
            string bin = $"\"{ElevatedInput.ExePath}\" --service";
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) throw new Win32Exception();
            try
            {
                IntPtr svc = OpenServiceW(scm, TransLocalService.Name, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero)
                {
                    svc = CreateServiceW(scm, TransLocalService.Name, TransLocalService.DisplayNameText, SERVICE_ALL_ACCESS,
                        SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, bin, null, IntPtr.Zero, null, null, null);
                    if (svc == IntPtr.Zero) throw new Win32Exception();
                }
                else if (!ChangeServiceConfigW(svc, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, bin,
                             null, IntPtr.Zero, null, null, null, TransLocalService.DisplayNameText))
                {
                    throw new Win32Exception();
                }
                try
                {
                    var desc = new SERVICE_DESCRIPTION
                    {
                        lpDescription = "Permite que o TransLocal controle programas de administrador e as confirmações do Windows (UAC) " +
                                        "durante uma transmissão. Só recebe comandos do próprio TransLocal neste computador.",
                    };
                    ChangeServiceConfig2W(svc, 1 /* SERVICE_CONFIG_DESCRIPTION */, ref desc);
                    StartServiceW(svc, 0, IntPtr.Zero); // já rodando: ERROR_SERVICE_ALREADY_RUNNING, sem problema
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }

            Log.Info("Serviço do TransLocal instalado e iniciado");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao instalar o serviço", ex);
            return 1;
        }
    }

    /// <summary>--uninstall-service: para e remove o serviço (e a tarefa antiga, se houver).</summary>
    public static int Uninstall()
    {
        try
        {
            try
            {
                using var sc = new ServiceController(TransLocalService.Name);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
            }
            catch { }
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm != IntPtr.Zero)
            {
                IntPtr svc = OpenServiceW(scm, TransLocalService.Name, DELETE);
                if (svc != IntPtr.Zero)
                {
                    DeleteService(svc);
                    CloseServiceHandle(svc);
                }
                CloseServiceHandle(scm);
            }
            ElevatedInput.Unregister();
            Log.Info("Serviço do TransLocal removido");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao remover o serviço", ex);
            return 1;
        }
    }

    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F, SERVICE_ALL_ACCESS = 0xF01FF, DELETE = 0x10000;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_AUTO_START = 2, SERVICE_ERROR_NORMAL = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_DESCRIPTION { public string lpDescription; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(IntPtr scm, string name, string display, uint access, uint type, uint start, uint error,
        string bin, string? group, IntPtr tag, string? deps, string? user, string? password);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfigW(IntPtr svc, uint type, uint start, uint error, string bin, string? group, IntPtr tag,
        string? deps, string? user, string? password, string display);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2W(IntPtr svc, uint level, ref SERVICE_DESCRIPTION info);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool StartServiceW(IntPtr svc, uint argc, IntPtr argv);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(IntPtr svc);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr h);
}
