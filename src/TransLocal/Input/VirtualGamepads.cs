using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace TransLocal.Input;

/// <summary>
/// No computador que transmite: cria controles virtuais de Xbox 360 (driver ViGEmBus) que repetem os
/// controles USB do computador que exibe. Os jogos os enxergam como controles comuns (XInput).
/// </summary>
internal sealed class VirtualGamepads : IDisposable
{
    public const string StatusOk = "ok";
    public const string StatusNoDriver = "sem-driver";
    public const string StatusDisabled = "desligado";

    private readonly object _sync = new();
    private readonly IXbox360Controller?[] _pads = new IXbox360Controller?[XInput.MaxControllers];
    private ViGEmClient? _client;
    private bool _disposed;

    /// <summary>O jogo pediu vibração: (slot, motor grande, motor pequeno).</summary>
    public event Action<int, byte, byte>? Rumble;

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

    public void Apply(GamepadState s)
    {
        lock (_sync)
        {
            if (_disposed || s.Slot >= _pads.Length) return;
            var pad = _pads[s.Slot];

            if (!s.Connected)
            {
                if (pad is not null)
                {
                    try { pad.Disconnect(); } catch { }
                    _pads[s.Slot] = null;
                    Log.Info($"Controle virtual {s.Slot + 1} desconectado");
                }
                return;
            }

            if (pad is null)
            {
                try
                {
                    pad = CreatePad(s.Slot);
                }
                catch (Exception first)
                {
                    // Falha transitória do driver já observada: tenta mais uma vez. O cliente só é recriado
                    // se nenhum outro controle estiver ativo (fechá-lo removeria todos os controles dele).
                    Log.Warn($"Controle virtual {s.Slot + 1}: nova tentativa de conexão", first);
                    if (_pads.All(p => p is null))
                    {
                        try { _client?.Dispose(); } catch { }
                        _client = null;
                    }
                    Thread.Sleep(50);
                    pad = CreatePad(s.Slot);
                }
                _pads[s.Slot] = pad;
                Log.Info($"Controle virtual {s.Slot + 1} (Xbox 360) conectado");
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
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < _pads.Length; i++)
            {
                if (_pads[i] is null) continue;
                try { _pads[i]!.Disconnect(); } catch { }
                _pads[i] = null;
            }
            try { _client?.Dispose(); } catch { }
            _client = null;
        }
    }
}
