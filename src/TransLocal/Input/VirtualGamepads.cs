using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace TransLocal.Input;

/// <summary>
/// No computador que transmite: cria controles virtuais de Xbox 360 (driver ViGEmBus) que repetem os
/// controles do computador que exibe. Os jogos os enxergam como controles comuns (XInput).
/// Tudo roda numa thread própria: conectar um controle pode demorar (ou falhar enquanto um jogo abre)
/// e isso não pode atrasar a leitura da rede.
/// </summary>
internal sealed class VirtualGamepads : IDisposable
{
    public const string StatusOk = "ok";
    public const string StatusNoDriver = "sem-driver";
    public const string StatusDisabled = "desligado";

    private const int MaxAttempts = 6;

    private readonly object _sync = new();
    private readonly GamepadState?[] _pending = new GamepadState?[XInput.MaxControllers];
    private readonly IXbox360Controller?[] _pads = new IXbox360Controller?[XInput.MaxControllers];
    private readonly int[] _attempts = new int[XInput.MaxControllers];
    private readonly long[] _retryAt = new long[XInput.MaxControllers];
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private ViGEmClient? _client;
    private volatile bool _disposed;

    /// <summary>O jogo pediu vibração: (slot, motor grande, motor pequeno).</summary>
    public event Action<int, byte, byte>? Rumble;

    public VirtualGamepads()
    {
        _worker = new Thread(Work) { IsBackground = true, Name = "TransLocal controles virtuais", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    /// <summary>Verifica se o driver ViGEmBus está instalado e funcionando.</summary>
    public static bool IsDriverInstalled(out string? error)
    {
        try
        {
            using var client = new ViGEmClient();
            error = null;
            return true;
        }
        catch (VigemBusNotFoundException)
        {
            error = "O driver ViGEmBus não está instalado.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Recebe o estado mais recente de um controle (só o último importa).</summary>
    public void Apply(GamepadState s)
    {
        if (_disposed || s.Slot >= _pending.Length) return;
        lock (_sync) _pending[s.Slot] = s;
        _signal.Set();
    }

    private void Work()
    {
        while (!_disposed)
        {
            _signal.WaitOne(100);
            for (int slot = 0; slot < _pending.Length && !_disposed; slot++)
            {
                GamepadState? s;
                lock (_sync)
                {
                    s = _pending[slot];
                    if (s is null) continue;
                    // Aguardando nova tentativa de conexão: mantém o estado pendente.
                    if (_pads[slot] is null && s.Value.Connected && Environment.TickCount64 < _retryAt[slot]) continue;
                    _pending[slot] = null;
                }
                try
                {
                    ApplyNow(s.Value);
                }
                catch (Exception ex)
                {
                    HandleFailure(slot, s.Value, ex);
                }
            }
        }
    }

    private void ApplyNow(GamepadState s)
    {
        var pad = _pads[s.Slot];
        if (!s.Connected)
        {
            _attempts[s.Slot] = 0;
            if (pad is null) return;
            try { pad.Disconnect(); } catch { }
            _pads[s.Slot] = null;
            Log.Info($"Controle virtual {s.Slot + 1} desconectado");
            return;
        }

        if (pad is null)
        {
            pad = CreatePad(s.Slot);
            _pads[s.Slot] = pad;
            Log.Info($"Controle virtual {s.Slot + 1} (Xbox 360) conectado" + (_attempts[s.Slot] > 0 ? $" na tentativa {_attempts[s.Slot] + 1}" : ""));
            _attempts[s.Slot] = 0;
        }

        // O relatório do Xbox 360 usa os mesmos bits de botões do XInput.
        pad.SetButtonsFull(s.Buttons);
        pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.LeftTrigger);
        pad.SetSliderValue(Xbox360Slider.RightTrigger, s.RightTrigger);
        pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.LeftX);
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, s.LeftY);
        pad.SetAxisValue(Xbox360Axis.RightThumbX, s.RightX);
        pad.SetAxisValue(Xbox360Axis.RightThumbY, s.RightY);
        pad.SubmitReport();
    }

    private void HandleFailure(int slot, GamepadState s, Exception ex)
    {
        // Um controle que falhou ao conectar pode ter ficado “meio plugado”: descarta e tenta de novo.
        if (_pads[slot] is { } broken)
        {
            try { broken.Disconnect(); } catch { }
            _pads[slot] = null;
        }
        _attempts[slot]++;
        if (_attempts[slot] >= MaxAttempts)
        {
            Log.Error($"Controle virtual {slot + 1}: desistindo após {MaxAttempts} tentativas", ex);
            _attempts[slot] = 0;
            _retryAt[slot] = Environment.TickCount64 + 10_000; // tenta de novo daqui a 10 s, se o controle ainda estiver em uso
        }
        else
        {
            int delay = 250 << (_attempts[slot] - 1); // 250 ms, 500 ms, 1 s, 2 s…
            _retryAt[slot] = Environment.TickCount64 + delay;
            Log.Warn($"Controle virtual {slot + 1}: falha ao conectar (tentativa {_attempts[slot]}); nova tentativa em {delay} ms", ex);
            // Recria o cliente do driver se nenhum outro controle estiver ativo (fechá-lo removeria todos).
            if (_pads.All(p => p is null))
            {
                try { _client?.Dispose(); } catch { }
                _client = null;
            }
        }
        lock (_sync) _pending[slot] ??= s; // mantém o estado para a próxima tentativa
    }

    private IXbox360Controller CreatePad(int slot)
    {
        // Evita a janela “ms-gamebar” que o Windows abre ao conectar um controle de Xbox sem a Game Bar.
        SystemIntegration.SuppressGameBarPromptIfNeeded();
        _client ??= new ViGEmClient();
        var pad = _client.CreateXbox360Controller();
        pad.AutoSubmitReport = false;
        pad.FeedbackReceived += (_, e) => Rumble?.Invoke(slot, e.LargeMotor, e.SmallMotor);
        pad.Connect();
        return pad;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _signal.Set();
        _worker.Join(2000);
        for (int i = 0; i < _pads.Length; i++)
        {
            if (_pads[i] is null) continue;
            try { _pads[i]!.Disconnect(); } catch { }
            _pads[i] = null;
        }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _signal.Dispose();
    }
}
