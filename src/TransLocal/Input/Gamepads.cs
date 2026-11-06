using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TransLocal.Input;

/// <summary>Estado de um controle no formato XInput (o mesmo usado pelo controle virtual de Xbox 360).</summary>
internal readonly record struct GamepadState(byte Slot, bool Connected, ushort Buttons, byte LeftTrigger, byte RightTrigger,
    short LeftX, short LeftY, short RightX, short RightY)
{
    public GamepadState Neutral() => this with { Buttons = 0, LeftTrigger = 0, RightTrigger = 0, LeftX = 0, LeftY = 0, RightX = 0, RightY = 0 };
}

/// <summary>
/// Mensagens de controle de jogo.
///  Gamepad (receptor → transmissor): [slot:u8][conectado:u8][botões:u16][LT:u8][RT:u8][LX:i16][LY:i16][RX:i16][RY:i16]
///  Vibração (transmissor → receptor): [slot:u8][motor grande:u8][motor pequeno:u8]
/// </summary>
internal static class GamepadCodec
{
    public const int StateSize = 16;

    public static byte[] Encode(GamepadState s)
    {
        var b = new byte[StateSize];
        b[0] = s.Slot;
        b[1] = (byte)(s.Connected ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), s.Buttons);
        b[4] = s.LeftTrigger;
        b[5] = s.RightTrigger;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(6), s.LeftX);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(8), s.LeftY);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(10), s.RightX);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(12), s.RightY);
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> b, out GamepadState s)
    {
        s = default;
        if (b.Length < 14 || b[0] > 3) return false;
        s = new GamepadState(b[0], b[1] != 0,
            BinaryPrimitives.ReadUInt16LittleEndian(b[2..]), b[4], b[5],
            BinaryPrimitives.ReadInt16LittleEndian(b[6..]), BinaryPrimitives.ReadInt16LittleEndian(b[8..]),
            BinaryPrimitives.ReadInt16LittleEndian(b[10..]), BinaryPrimitives.ReadInt16LittleEndian(b[12..]));
        return true;
    }

    public static byte[] EncodeRumble(int slot, byte large, byte small) => new[] { (byte)slot, large, small };
}

/// <summary>Acesso aos controles XInput (Xbox 360/One/Series e compatíveis) deste computador.</summary>
internal static class XInput
{
    public const int MaxControllers = 4;
    private const int ERROR_SUCCESS = 0;
    private static bool _exUnavailable;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
        public uint Reserved; // a versão estendida pode escrever 4 bytes a mais
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);
    // Ordinal 100 = XInputGetStateEx: igual ao XInputGetState, mas também informa o botão Guia (Xbox).
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] private static extern int XInputGetStateEx(int dwUserIndex, out XINPUT_STATE pState);
    [DllImport("xinput1_4.dll")] private static extern int XInputSetState(int dwUserIndex, ref XINPUT_VIBRATION pVibration);

    public static bool TryGetState(int slot, out GamepadState state, out uint packet)
    {
        state = default;
        packet = 0;
        XINPUT_STATE s;
        int r;
        try
        {
            if (!_exUnavailable)
            {
                try { r = XInputGetStateEx(slot, out s); }
                catch (EntryPointNotFoundException)
                {
                    _exUnavailable = true;
                    r = XInputGetState(slot, out s);
                }
            }
            else
            {
                r = XInputGetState(slot, out s);
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        if (r != ERROR_SUCCESS) return false;
        var g = s.Gamepad;
        packet = s.dwPacketNumber;
        state = new GamepadState((byte)slot, true, g.wButtons, g.bLeftTrigger, g.bRightTrigger, g.sThumbLX, g.sThumbLY, g.sThumbRX, g.sThumbRY);
        return true;
    }

    public static int CountConnected()
    {
        int n = 0;
        for (int i = 0; i < MaxControllers; i++)
            if (TryGetState(i, out _, out _)) n++;
        return n;
    }

    public static void SetVibration(int slot, byte large, byte small)
    {
        var v = new XINPUT_VIBRATION { wLeftMotorSpeed = (ushort)(large * 257), wRightMotorSpeed = (ushort)(small * 257) };
        try { XInputSetState(slot, ref v); } catch { }
    }
}

/// <summary>
/// No computador que exibe: lê os controles USB a cada 4 ms e envia as mudanças ao transmissor
/// enquanto <paramref name="isActive"/> for verdadeiro (janela de exibição em primeiro plano, controle ligado).
/// Quando deixa de estar ativo, envia o controle em repouso para nada ficar “preso”.
/// </summary>
internal sealed class GamepadForwarder : IDisposable
{
    private readonly Action<byte[]> _send;
    private readonly Func<bool> _isActive;
    private readonly bool _canSend;
    private readonly GamepadState?[] _lastSent = new GamepadState?[XInput.MaxControllers];
    private readonly bool[] _rumbling = new bool[XInput.MaxControllers];
    private volatile bool _running = true;
    private readonly Thread _thread;

    /// <summary>Quantos controles estão conectados neste computador agora.</summary>
    public int ConnectedCount { get; private set; }

    /// <param name="canSend">false quando o outro computador não aceita controles (só conta os conectados).</param>
    public GamepadForwarder(Action<byte[]> send, Func<bool> isActive, bool canSend)
    {
        _send = send;
        _isActive = isActive;
        _canSend = canSend;
        _thread = new Thread(Loop) { IsBackground = true, Name = "TransLocal controles", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        Native.timeBeginPeriod(1);
        var present = new bool[XInput.MaxControllers];
        try
        {
            long tick = 0;
            while (_running)
            {
                bool active = _canSend && _isActive();
                for (int i = 0; i < XInput.MaxControllers; i++)
                {
                    // Consultar um slot vazio é lento no XInput: slots vazios só a cada ~0,5 s.
                    if (!present[i] && tick % 125 != 0) continue;

                    present[i] = XInput.TryGetState(i, out var state, out _);
                    if (!_canSend) continue;

                    var last = _lastSent[i];
                    if (!present[i])
                    {
                        // Controle desconectado aqui: desconecta o controle virtual lá.
                        if (last is { } l) Send(l.Neutral() with { Connected = false });
                        _lastSent[i] = null;
                        continue;
                    }

                    // Fora de foco (ou controle desligado): mantém o controle virtual, mas em repouso.
                    var desired = active ? state : state.Neutral();
                    if (last is null)
                    {
                        // O controle virtual só é criado quando o controle é usado com a exibição em foco.
                        if (!active) continue;
                    }
                    else if (last.Value == desired)
                    {
                        continue;
                    }
                    Send(desired);
                    _lastSent[i] = desired;
                }
                ConnectedCount = present.Count(p => p);
                tick++;
                Thread.Sleep(4);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Leitura de controles interrompida", ex);
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    private void Send(GamepadState s)
    {
        try { _send(GamepadCodec.Encode(s)); } catch { }
    }

    /// <summary>Vibração pedida pelo jogo no outro computador.</summary>
    public void Rumble(int slot, byte large, byte small)
    {
        if (slot is < 0 or >= XInput.MaxControllers) return;
        _rumbling[slot] = large != 0 || small != 0;
        XInput.SetVibration(slot, large, small);
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(500);
        for (int i = 0; i < XInput.MaxControllers; i++)
        {
            if (_rumbling[i]) XInput.SetVibration(i, 0, 0);
        }
    }
}
