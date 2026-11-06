using System.Collections.Concurrent;
using System.Text;
using SDL;
using static SDL.SDL3;

namespace TransLocal.Input;

/// <summary>Controle conectado a este computador.</summary>
/// <param name="Mapped">false = joystick que a SDL não reconhece como controle (precisa do assistente).</param>
internal sealed record ControllerInfo(uint Id, string Name, string Kind, bool Wireless, bool Mapped, string Guid)
{
    public string Describe() =>
        $"{Name}{(Wireless ? " (sem fio/Bluetooth)" : "")}{(Mapped ? "" : " — não reconhecido")}";
}

internal sealed class ControllerSnapshot
{
    public static readonly ControllerSnapshot Empty = new(Array.Empty<ControllerInfo>(), new Dictionary<uint, GamepadState>());

    public ControllerSnapshot(IReadOnlyList<ControllerInfo> controllers, IReadOnlyDictionary<uint, GamepadState> states)
    {
        Controllers = controllers;
        States = states;
    }

    public IReadOnlyList<ControllerInfo> Controllers { get; }

    /// <summary>Estado (layout Xbox/XInput) de cada controle reconhecido, por id.</summary>
    public IReadOnlyDictionary<uint, GamepadState> States { get; }
}

/// <summary>Estado bruto de um joystick (para o assistente de configuração).</summary>
internal sealed record RawJoystickState(uint Id, bool[] Buttons, short[] Axes, byte[] Hats);

/// <summary>
/// Controles de jogo deste computador via SDL3: Xbox, PlayStation 4/5, Switch Pro e centenas de outros,
/// por USB ou Bluetooth, todos traduzidos para o layout do Xbox, com vibração.
/// Toda chamada à SDL acontece numa única thread dedicada.
/// </summary>
internal sealed unsafe class ControllerService
{
    private static readonly object InstanceLock = new();
    private static ControllerService? _instance;

    /// <summary>Mapeamentos salvos (assistente) a aplicar quando a SDL iniciar.</summary>
    public static Func<IEnumerable<string>> InitialMappings { get; set; } = Array.Empty<string>;

    public static ControllerService Instance
    {
        get
        {
            lock (InstanceLock) return _instance ??= new ControllerService();
        }
    }

    /// <summary>Encerra a SDL (ao sair do aplicativo).</summary>
    public static void Shutdown()
    {
        lock (InstanceLock)
        {
            _instance?.Stop();
            _instance = null;
        }
    }

    private readonly ConcurrentQueue<Action> _actions = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<uint, IntPtr> _pads = new();
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile ControllerSnapshot _snapshot = ControllerSnapshot.Empty;
    private volatile RawJoystickState? _raw;
    private IReadOnlyList<ControllerInfo> _infos = Array.Empty<ControllerInfo>();
    private IntPtr _rawJoystick;
    private uint _rawId;
    private int _activeUsers;

    public bool Available { get; private set; }
    public string? Error { get; private set; }
    public ControllerSnapshot Snapshot => _snapshot;
    public RawJoystickState? Raw => _raw;

    private ControllerService()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "TransLocal SDL", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(5000);
    }

    /// <summary>Enquanto o objeto devolvido não for descartado, os controles são lidos a cada 4 ms.</summary>
    public IDisposable BeginActive()
    {
        Interlocked.Increment(ref _activeUsers);
        return new Releaser(() => Interlocked.Decrement(ref _activeUsers));
    }

    public void Rumble(uint id, byte large, byte small) => _actions.Enqueue(() =>
    {
        if (!_pads.TryGetValue(id, out var gp)) return;
        uint duration = large != 0 || small != 0 ? 60_000u : 0u;
        SDL_RumbleGamepad((SDL_Gamepad*)gp, (ushort)(large * 257), (ushort)(small * 257), duration);
    });

    public void AddMapping(string mapping) => _actions.Enqueue(() =>
    {
        int r = SDL_AddGamepadMapping(mapping);
        if (r < 0) Log.Warn("Mapeamento de controle recusado pela SDL: " + SDL_GetError());
        else Log.Info("Mapeamento de controle aplicado");
    });

    /// <summary>Passa a publicar o estado bruto do joystick (assistente). id 0 = parar.</summary>
    public void WatchRaw(uint id) => _actions.Enqueue(() =>
    {
        if (_rawJoystick != IntPtr.Zero)
        {
            SDL_CloseJoystick((SDL_Joystick*)_rawJoystick);
            _rawJoystick = IntPtr.Zero;
        }
        _raw = null;
        _rawId = id;
        if (id != 0)
        {
            var js = SDL_OpenJoystick((SDL_JoystickID)id);
            if (js == null) Log.Warn("Não foi possível abrir o joystick: " + SDL_GetError());
            _rawJoystick = (IntPtr)js;
        }
    });

    private void Stop()
    {
        _running = false;
        _thread.Join(2000);
    }

    private void Loop()
    {
        try
        {
            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            if (!SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD))
            {
                Error = SDL_GetError();
                Log.Warn("SDL não iniciou: " + Error);
                return;
            }
            foreach (var m in InitialMappings())
            {
                if (SDL_AddGamepadMapping(m) < 0) Log.Warn("Mapeamento salvo recusado: " + SDL_GetError());
            }
            Available = true;
            Log.Info($"Controles de jogo: SDL {SDL_GetVersion() / 1000000}.{SDL_GetVersion() / 1000 % 1000}.{SDL_GetVersion() % 1000} pronta");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warn("SDL indisponível (usando só XInput)", ex);
            return;
        }
        finally
        {
            _ready.Set();
        }

        long lastScan = 0;
        try
        {
            while (_running)
            {
                while (_actions.TryDequeue(out var action))
                {
                    try { action(); } catch (Exception ex) { Log.Warn("Ação de controle falhou", ex); }
                }

                // Processa conexões/desconexões e atualiza os estados; a fila de eventos não é usada.
                SDL_PumpEvents();
                SDL_FlushEvents(SDL_EventType.SDL_EVENT_FIRST, SDL_EventType.SDL_EVENT_LAST);

                long now = Environment.TickCount64;
                if (now - lastScan >= 250)
                {
                    Rescan();
                    lastScan = now;
                }
                Publish();
                Thread.Sleep(Volatile.Read(ref _activeUsers) > 0 || _rawJoystick != IntPtr.Zero ? 4 : 50);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Leitura de controles (SDL) interrompida", ex);
        }
        finally
        {
            foreach (var gp in _pads.Values) SDL_CloseGamepad((SDL_Gamepad*)gp);
            _pads.Clear();
            if (_rawJoystick != IntPtr.Zero) SDL_CloseJoystick((SDL_Joystick*)_rawJoystick);
            SDL_Quit();
        }
    }

    private void Rescan()
    {
        var infos = new List<ControllerInfo>();
        var present = new HashSet<uint>();

        int count;
        SDL_JoystickID* ids = SDL_GetGamepads(&count);
        if (ids != null)
        {
            for (int i = 0; i < count; i++)
            {
                uint id = (uint)ids[i];
                present.Add(id);
                if (!_pads.ContainsKey(id))
                {
                    var gp = SDL_OpenGamepad(ids[i]);
                    if (gp == null) continue;
                    _pads[id] = (IntPtr)gp;
                    Log.Info($"Controle conectado: {SDL_GetGamepadName(gp)} ({SDL_GetGamepadType(gp)}, {SDL_GetGamepadConnectionState(gp)})");
                }
            }
            SDL_free(ids);
        }

        foreach (var id in _pads.Keys.ToList())
        {
            var gp = (SDL_Gamepad*)_pads[id];
            if (present.Contains(id) && SDL_GamepadConnected(gp)) continue;
            Log.Info($"Controle desconectado: {SDL_GetGamepadName(gp)}");
            SDL_CloseGamepad(gp);
            _pads.Remove(id);
        }

        foreach (var (id, ptr) in _pads)
        {
            var gp = (SDL_Gamepad*)ptr;
            infos.Add(new ControllerInfo(id, SDL_GetGamepadName(gp) ?? "Controle", KindOf(SDL_GetGamepadType(gp)),
                SDL_GetGamepadConnectionState(gp) == SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRELESS,
                true, GuidString(SDL_GetGamepadGUIDForID((SDL_JoystickID)id))));
        }

        // Joysticks que a SDL não sabe mapear para o layout de controle.
        SDL_JoystickID* js = SDL_GetJoysticks(&count);
        if (js != null)
        {
            for (int i = 0; i < count; i++)
            {
                if (SDL_IsGamepad(js[i])) continue;
                uint id = (uint)js[i];
                infos.Add(new ControllerInfo(id, SDL_GetJoystickNameForID(js[i]) ?? "Joystick", "Genérico", false, false,
                    GuidString(SDL_GetJoystickGUIDForID(js[i]))));
            }
            SDL_free(js);
        }

        _infos = infos;
    }

    private void Publish()
    {
        var states = new Dictionary<uint, GamepadState>(_pads.Count);
        foreach (var (id, ptr) in _pads)
        {
            var gp = (SDL_Gamepad*)ptr;
            ushort buttons = 0;
            foreach (var (sdl, bit) in ButtonMap)
                if (SDL_GetGamepadButton(gp, sdl)) buttons |= bit;
            states[id] = new GamepadState(0, true, buttons,
                Trigger(SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER)),
                Trigger(SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)),
                SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX),
                InvertY(SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY)),
                SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX),
                InvertY(SDL_GetGamepadAxis(gp, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY)));
        }
        _snapshot = new ControllerSnapshot(_infos, states);

        if (_rawJoystick != IntPtr.Zero)
        {
            var j = (SDL_Joystick*)_rawJoystick;
            var b = new bool[Math.Max(0, SDL_GetNumJoystickButtons(j))];
            var a = new short[Math.Max(0, SDL_GetNumJoystickAxes(j))];
            var h = new byte[Math.Max(0, SDL_GetNumJoystickHats(j))];
            for (int i = 0; i < b.Length; i++) b[i] = SDL_GetJoystickButton(j, i);
            for (int i = 0; i < a.Length; i++) a[i] = SDL_GetJoystickAxis(j, i);
            for (int i = 0; i < h.Length; i++) h[i] = SDL_GetJoystickHat(j, i);
            _raw = new RawJoystickState(_rawId, b, a, h);
        }
    }

    // Botões SDL → bits do XInput/Xbox 360. Posicional: “sul” é sempre o botão de baixo (A no Xbox, X no PlayStation).
    private static readonly (SDL_GamepadButton Sdl, ushort Bit)[] ButtonMap =
    {
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, 0x0001),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, 0x0002),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, 0x0004),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, 0x0008),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, 0x0010),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, 0x0020),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_TOUCHPAD, 0x0020), // touchpad do PlayStation = View/Back
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, 0x0040),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, 0x0080),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, 0x0100),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, 0x0200),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE, 0x0400),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, 0x1000),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, 0x2000),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, 0x4000),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, 0x8000),
    };

    private static byte Trigger(short v) => (byte)Math.Clamp(v * 255 / 32767, 0, 255);

    // SDL: positivo = para baixo. XInput: positivo = para cima.
    private static short InvertY(short v) => (short)Math.Clamp(-(int)v, short.MinValue, short.MaxValue);

    private static string KindOf(SDL_GamepadType type)
    {
        var s = type.ToString().ToUpperInvariant();
        if (s.Contains("XBOX")) return "Xbox";
        if (s.Contains("PS3") || s.Contains("PS4") || s.Contains("PS5")) return "PlayStation";
        if (s.Contains("SWITCH") || s.Contains("JOYCON") || s.Contains("NINTENDO")) return "Nintendo";
        return "Genérico";
    }

    private static string GuidString(SDL_GUID guid)
    {
        byte* buf = stackalloc byte[33];
        SDL_GUIDToString(guid, buf, 33);
        return Encoding.ASCII.GetString(buf, 32);
    }

    private sealed class Releaser : IDisposable
    {
        private Action? _release;
        public Releaser(Action release) => _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
