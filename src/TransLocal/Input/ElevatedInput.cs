using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using TransLocal.Streaming;

namespace TransLocal.Input;

/// <summary>
/// Controle de programas abertos como administrador (Gerenciador de Tarefas, jogos marcados com
/// "Executar como administrador").
///
/// O Windows não deixa um programa comum enviar mouse/teclado para janelas de administrador (UIPI).
/// Um auxiliar mínimo roda elevado (tarefa agendada criada uma vez, com confirmação do UAC) e só faz
/// uma coisa: aplicar os comandos de entrada que o TransLocal desta sessão manda por um pipe local.
/// A parte de rede, vídeo e decodificação continua sem privilégios.
///
/// Proteções: o pipe é criado pelo TransLocal comum, só para o usuário atual e sem acesso pela rede;
/// cada lado confere que o outro é o mesmo executável, na mesma sessão do Windows; o auxiliar só aceita
/// rodar a partir da pasta de instalação (protegida contra escrita por programas comuns).
/// </summary>
internal static class ElevatedInput
{
    public const string TaskName = "TransLocal Controle Elevado";
    public const byte KindInput = 1, KindMonitor = 2, KindPing = 3, KindQuit = 4;

    public static string PipeName => $"TransLocal.ElevatedInput.{CurrentSession}";

    private static int CurrentSession
    {
        get { using var p = Process.GetCurrentProcess(); return p.SessionId; }
    }

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>Só na compilação de desenvolvimento: testa o pipe sem privilégio de administrador.</summary>
    internal static bool UnelevatedTest =>
#if DEBUG
        Environment.GetEnvironmentVariable("TRANSLOCAL_PIPE_TEST") == "1";
#else
        false;
#endif

    /// <summary>Executável numa pasta que só administradores alteram (Arquivos de Programas).</summary>
    public static bool TrustedLocation
    {
        get
        {
#if DEBUG
            // Compilação de desenvolvimento (nunca publicada): permite testar a partir da pasta do projeto.
            return true;
#else
            string exe = Path.GetFullPath(ExePath);
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(root)) continue;
                root = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                if (exe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
#endif
        }
    }

    internal static bool SameExecutable(int pid)
    {
        var path = ProcessInfo.ImagePath(pid);
        return path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Tarefa agendada ----------------

    private static dynamic Scheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new NotSupportedException("Agendador de Tarefas indisponível.");
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        return svc;
    }

    /// <summary>A tarefa existe e aponta para este executável.</summary>
    public static bool TaskInstalled()
    {
        try
        {
            dynamic svc = Scheduler();
            dynamic task = svc.GetFolder("\\").GetTask(TaskName);
            string xml = task.Xml;
            return xml.Contains(System.Security.SecurityElement.Escape(ExePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Inicia o auxiliar pela tarefa (não pede confirmação: já foi autorizada ao criar).</summary>
    public static bool StartHelper()
    {
        try
        {
            dynamic svc = Scheduler();
            dynamic task = svc.GetFolder("\\").GetTask(TaskName);
            task.Run(null);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível iniciar o controle elevado", ex);
            return false;
        }
    }

    /// <summary>
    /// Executado já elevado (--register-elevated-input SID): cria a tarefa que abre o auxiliar no logon
    /// deste usuário, com privilégio máximo, e deixa o usuário iniciá-la sem novo UAC.
    /// </summary>
    public static int Register(string? sidText, string? testTargetFile = null)
    {
        try
        {
            if (!ProcessInfo.CurrentIsElevated()) { Log.Warn("Registro do controle elevado sem privilégio de administrador"); return 2; }
            if (!TrustedLocation) { Log.Warn("Controle elevado exige o TransLocal instalado em Arquivos de Programas"); return 3; }
            var sid = new SecurityIdentifier(sidText ?? WindowsIdentity.GetCurrent().User!.Value);
            string esc(string s) => System.Security.SecurityElement.Escape(s)!;
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Author>TransLocal</Author>
                    <Description>Permite que o TransLocal controle programas abertos como administrador (Gerenciador de Tarefas, jogos). Só recebe comandos do próprio TransLocal neste computador.</Description>
                  </RegistrationInfo>
                  <Triggers>
                    <LogonTrigger><Enabled>true</Enabled><UserId>{sid.Value}</UserId></LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{sid.Value}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>HighestAvailable</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>true</AllowHardTerminate>
                    <StartWhenAvailable>false</StartWhenAvailable>
                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>true</Hidden>
                    <RunOnlyIfIdle>false</RunOnlyIfIdle>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>4</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec><Command>{esc(ExePath)}</Command><Arguments>--elevated-input</Arguments></Exec>
                  </Actions>
                </Task>
                """;
            // Administradores e o sistema: tudo; o usuário: ler e executar (iniciar sem novo UAC).
            string sddl = $"D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;{sid.Value})";
            dynamic svc = Scheduler();
            dynamic folder = svc.GetFolder("\\");
            folder.RegisterTask(TaskName, xml, 6 /* criar ou atualizar */, null, null, 3 /* token interativo */, sddl);
            Log.Info($"Controle elevado registrado para {sid.Value}");
#if DEBUG
            if (testTargetFile is not null)
            {
                // Autoteste: abre a janela-alvo como administrador, espera o teste e desfaz tudo.
                using (var target = Process.Start(new ProcessStartInfo(ExePath, $"--elevated-target \"{testTargetFile}\"") { UseShellExecute = false }))
                    target?.WaitForExit(120_000);
                Unregister();
            }
#endif
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao registrar o controle elevado", ex);
            return 1;
        }
    }

    /// <summary>Executado elevado (--unregister-elevated-input): remove a tarefa e fecha o auxiliar.</summary>
    public static int Unregister()
    {
        try
        {
            dynamic svc = Scheduler();
            try { svc.GetFolder("\\").DeleteTask(TaskName, 0); } catch (COMException) { }
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExePath)))
            {
                using (p)
                {
                    if (p.Id != Environment.ProcessId && SameExecutable(p.Id) && ProcessInfo.IsProcessElevated(p.Id))
                        try { p.Kill(); } catch { }
                }
            }
            Log.Info("Controle elevado removido");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao remover o controle elevado", ex);
            return 1;
        }
    }

    /// <summary>Roda este executável como administrador (mostra o UAC) e espera terminar.</summary>
    public static bool RunElevated(string arguments, out string? error)
    {
        error = null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ExePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) { error = "não foi possível iniciar"; return false; }
            if (!p.WaitForExit(60_000)) { error = "demorou demais"; return false; }
            if (p.ExitCode == 0) return true;
            error = p.ExitCode switch
            {
                3 => "o TransLocal precisa estar instalado (pasta Arquivos de Programas) para esta opção",
                _ => $"código {p.ExitCode}; veja o registro",
            };
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "a confirmação do Windows foi cancelada";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------------- Auxiliar elevado ----------------

    /// <summary>--elevated-input: conecta ao TransLocal desta sessão e aplica os comandos de entrada.</summary>
    public static int RunHelper()
    {
        if (!ProcessInfo.CurrentIsElevated() && !UnelevatedTest)
        {
            Log.Warn("Controle elevado: este processo não está elevado; encerrando");
            return 2;
        }
        if (!TrustedLocation)
        {
            Log.Warn($"Controle elevado: executável fora de Arquivos de Programas ({ExePath}); encerrando");
            return 3;
        }
        using var mutex = new Mutex(true, @"Local\TransLocal.ElevatedInput", out bool first);
        if (!first) return 0;

        Log.Info($"Controle elevado ativo (sessão {CurrentSession})");
        var injector = new InputInjector(Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080));
        int session = CurrentSession;
        var buffer = new byte[256];
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.In, PipeOptions.None);
                try { pipe.Connect(2000); }
                catch (TimeoutException) { continue; }

                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) ||
                    ProcessInfo.SessionOf((int)serverPid) != session || !SameExecutable((int)serverPid))
                {
                    Log.Warn($"Controle elevado: pipe criado por outro programa (pid {serverPid}); ignorado");
                    Thread.Sleep(5000);
                    continue;
                }
                Log.Info($"Controle elevado ligado ao TransLocal (pid {serverPid})");
                try
                {
                    while (true)
                    {
                        int len = pipe.ReadByte();
                        if (len <= 0) break;
                        if (!ReadExactly(pipe, buffer, len)) break;
                        var msg = buffer.AsSpan(0, len);
                        switch (msg[0])
                        {
                            case KindInput when len > 1:
                                injector.Apply(msg[1..]);
                                break;
                            case KindMonitor when len >= 17:
                                injector.SetMonitor(new Rectangle(
                                    BitConverter.ToInt32(msg[1..]), BitConverter.ToInt32(msg[5..]),
                                    BitConverter.ToInt32(msg[9..]), BitConverter.ToInt32(msg[13..])));
                                break;
                            case KindQuit:
                                injector.ReleaseAll();
                                Log.Info("Controle elevado encerrado a pedido do TransLocal");
                                return 0;
                        }
                    }
                }
                finally
                {
                    injector.ReleaseAll();
                }
                Log.Info("Controle elevado: TransLocal desconectou; aguardando");
            }
            catch (IOException) { Thread.Sleep(1000); }
            catch (Exception ex)
            {
                Log.Warn("Controle elevado: erro", ex);
                Thread.Sleep(2000);
            }
        }
    }

    private static bool ReadExactly(Stream s, byte[] buf, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = s.Read(buf, got, len - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}

/// <summary>
/// Lado comum: serve o pipe para o auxiliar elevado e repassa os comandos de entrada a ele.
/// Se o auxiliar não estiver ligado, quem chama usa a injeção local (sem alcançar janelas de administrador).
/// </summary>
internal sealed class ElevatedInputBridge : IDisposable
{
    private readonly object _write = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private NamedPipeServerStream? _pipe;
    private volatile bool _connected;
    private Rectangle _monitor;
    private long _lastStartAttempt;

    /// <summary>Instância em uso (null quando a opção está desligada).</summary>
    public static ElevatedInputBridge? Current { get; private set; }

    public bool IsConnected => _connected;

    /// <summary>Ligou ou desligou o auxiliar (thread de fundo).</summary>
    public event Action<bool>? ConnectionChanged;

    private ElevatedInputBridge()
    {
        _thread = new Thread(Serve) { IsBackground = true, Name = "TransLocal controle elevado" };
        _thread.Start();
    }

    public static void Enable()
    {
        if (Current is not null) return;
        Current = new ElevatedInputBridge();
    }

    public static void Disable(bool stopHelper)
    {
        var c = Current;
        Current = null;
        if (c is null) return;
        if (stopHelper) c.Send(ElevatedInput.KindQuit, ReadOnlySpan<byte>.Empty);
        c.Dispose();
    }

    private void Serve()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(ElevatedInput.PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.WriteThrough);

                // O auxiliar inicia no logon; se não veio (acabou de ser ativado, atualização), pede à tarefa.
                var wait = pipe.WaitForConnectionAsync(ct);
                if (!wait.Wait(3000, ct))
                {
                    MaybeStartHelper();
                    wait.Wait(ct);
                }

                if (!ElevatedInput.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ||
                    ProcessInfo.SessionOf((int)pid) != ProcessInfo.SessionOf(Environment.ProcessId) ||
                    !ElevatedInput.SameExecutable((int)pid) || (!ProcessInfo.IsProcessElevated((int)pid) && !ElevatedInput.UnelevatedTest))
                {
                    Log.Warn($"Controle elevado: conexão recusada (pid {pid} não é o auxiliar do TransLocal)");
                    pipe.Dispose();
                    Thread.Sleep(1000);
                    continue;
                }

                lock (_write) _pipe = pipe;
                _connected = true;
                Log.Info($"Controle elevado conectado (auxiliar pid {pid}): programas de administrador podem ser controlados");
                ConnectionChanged?.Invoke(true);
                if (!_monitor.IsEmpty) SetMonitor(_monitor);

                // Mantém vivo; um envio que falha indica que o auxiliar saiu.
                while (!ct.IsCancellationRequested && _connected)
                {
                    if (ct.WaitHandle.WaitOne(2000)) break;
                    if (!Send(ElevatedInput.KindPing, ReadOnlySpan<byte>.Empty)) break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { break; }
            catch (IOException ex)
            {
                // Outro programa criou o pipe antes (nome ocupado) ou o auxiliar caiu.
                Log.Warn("Controle elevado: pipe indisponível", ex);
                if (ct.WaitHandle.WaitOne(5000)) break;
            }
            catch (Exception ex)
            {
                Log.Warn("Controle elevado: erro", ex);
                if (ct.WaitHandle.WaitOne(5000)) break;
            }
            finally
            {
                bool was = _connected;
                _connected = false;
                lock (_write) _pipe = null;
                try { pipe?.Dispose(); } catch { }
                if (was)
                {
                    Log.Info("Controle elevado desconectado");
                    ConnectionChanged?.Invoke(false);
                }
            }
        }
    }

    private void MaybeStartHelper()
    {
        long now = Environment.TickCount64;
        if (now - _lastStartAttempt < 30_000) return;
        _lastStartAttempt = now;
        if (ElevatedInput.TaskInstalled()) ElevatedInput.StartHelper();
    }

    /// <summary>Envia um comando de entrada ao auxiliar. False se ele não está ligado (use a injeção local).</summary>
    public bool TrySendInput(ReadOnlySpan<byte> payload) => _connected && Send(ElevatedInput.KindInput, payload);

    public void SetMonitor(Rectangle bounds)
    {
        _monitor = bounds;
        if (!_connected) return;
        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b, bounds.X);
        BitConverter.TryWriteBytes(b[4..], bounds.Y);
        BitConverter.TryWriteBytes(b[8..], bounds.Width);
        BitConverter.TryWriteBytes(b[12..], bounds.Height);
        Send(ElevatedInput.KindMonitor, b);
    }

    private bool Send(byte kind, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 200) return false;
        Span<byte> frame = stackalloc byte[2 + payload.Length];
        frame[0] = (byte)(1 + payload.Length);
        frame[1] = kind;
        payload.CopyTo(frame[2..]);
        lock (_write)
        {
            if (_pipe is null) return false;
            try
            {
                _pipe.Write(frame);
                return true;
            }
            catch
            {
                _connected = false;
                return false;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_write)
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }
        _connected = false;
    }
}
