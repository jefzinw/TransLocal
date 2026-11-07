using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TransLocal.Input;

/// <summary>
/// Mensagens de entrada (receptor → transmissor). Coordenadas absolutas normalizadas 0..65535 dentro da imagem.
///  1 mover:          [nx:u16][ny:u16]
///  2 botão:          [botão:u8][desceu:u8][nx:u16][ny:u16]
///  3 roda:           [delta:i16][horizontal:u8]
///  4 tecla:          [vk:u16][scan:u16][flags:u8]  (1 = soltou, 2 = tecla estendida)
///  5 mover relativo: [dx:i16][dy:i16]               (jogos: cursor preso/oculto)
///  6 soltar tudo                                     (janela perdeu o foco / controle desligado)
///  7 botão parado:   [botão:u8][desceu:u8]          (sem mover o cursor; modo relativo)
/// </summary>
internal static class InputCodec
{
    public const byte Move = 1, Button = 2, Wheel = 3, Key = 4, RelativeMove = 5, ReleaseAll = 6, ButtonOnly = 7;

    public static byte[] EncodeMove(ushort nx, ushort ny)
    {
        var b = new byte[5];
        b[0] = Move;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), nx);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), ny);
        return b;
    }

    private static byte ButtonIndex(MouseButtons button) => button switch
    {
        MouseButtons.Left => 0,
        MouseButtons.Right => 1,
        MouseButtons.Middle => 2,
        MouseButtons.XButton1 => 3,
        MouseButtons.XButton2 => 4,
        _ => 255,
    };

    public static byte[] EncodeButton(MouseButtons button, bool down, ushort nx, ushort ny)
    {
        var b = new byte[7];
        b[0] = Button;
        b[1] = ButtonIndex(button);
        b[2] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), nx);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), ny);
        return b;
    }

    public static byte[] EncodeButtonOnly(MouseButtons button, bool down) =>
        new[] { ButtonOnly, ButtonIndex(button), (byte)(down ? 1 : 0) };

    public static byte[] EncodeWheel(int delta, bool horizontal)
    {
        var b = new byte[4];
        b[0] = Wheel;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), (short)Math.Clamp(delta, short.MinValue, short.MaxValue));
        b[3] = (byte)(horizontal ? 1 : 0);
        return b;
    }

    public static byte[] EncodeKey(int vk, int scan, bool extended, bool up)
    {
        var b = new byte[6];
        b[0] = Key;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), (ushort)vk);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(3), (ushort)scan);
        b[5] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
        return b;
    }

    public static byte[] EncodeRelativeMove(int dx, int dy)
    {
        var b = new byte[5];
        b[0] = RelativeMove;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(3), (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        return b;
    }

    public static byte[] EncodeReleaseAll() => new[] { ReleaseAll };
}

/// <summary>Aplica no Windows (SendInput) os comandos de mouse/teclado vindos do receptor.</summary>
internal sealed class InputInjector
{
    private readonly object _sync = new();
    private readonly HashSet<(ushort Vk, ushort Scan, bool Ext)> _keysDown = new();
    private readonly HashSet<byte> _buttonsDown = new();
    private Rectangle _monitor;

    public InputInjector(Rectangle monitorBounds) => _monitor = monitorBounds;

    public void SetMonitor(Rectangle bounds)
    {
        lock (_sync) _monitor = bounds;
    }

    public void Apply(ReadOnlySpan<byte> m)
    {
        if (m.Length < 1) return;
        lock (_sync)
        {
            switch (m[0])
            {
                case InputCodec.Move when m.Length >= 5:
                    MoveTo(BinaryPrimitives.ReadUInt16LittleEndian(m[1..]), BinaryPrimitives.ReadUInt16LittleEndian(m[3..]));
                    break;
                case InputCodec.Button when m.Length >= 7:
                    MoveTo(BinaryPrimitives.ReadUInt16LittleEndian(m[3..]), BinaryPrimitives.ReadUInt16LittleEndian(m[5..]));
                    SendButton(m[1], m[2] != 0);
                    break;
                case InputCodec.ButtonOnly when m.Length >= 3:
                    SendButton(m[1], m[2] != 0);
                    break;
                case InputCodec.RelativeMove when m.Length >= 5:
                    // Movimento relativo: o que os jogos leem quando prendem o cursor (câmera em 1ª/3ª pessoa).
                    SendMouse(BinaryPrimitives.ReadInt16LittleEndian(m[1..]), BinaryPrimitives.ReadInt16LittleEndian(m[3..]), 0, Native.MOUSEEVENTF_MOVE);
                    break;
                case InputCodec.Wheel when m.Length >= 4:
                    SendMouse(0, 0, (uint)(int)BinaryPrimitives.ReadInt16LittleEndian(m[1..]),
                        m[3] != 0 ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL);
                    break;
                case InputCodec.Key when m.Length >= 6:
                    var vk = BinaryPrimitives.ReadUInt16LittleEndian(m[1..]);
                    var scan = BinaryPrimitives.ReadUInt16LittleEndian(m[3..]);
                    bool up = (m[5] & 1) != 0, ext = (m[5] & 2) != 0;
                    SendKey(vk, scan, ext, up);
                    if (up) _keysDown.Remove((vk, scan, ext)); else _keysDown.Add((vk, scan, ext));
                    break;
                case InputCodec.ReleaseAll:
                    ReleaseAllLocked();
                    break;
            }
        }
    }

    /// <summary>Solta teclas e botões que ficaram pressionados (fim da sessão, foco perdido).</summary>
    public void ReleaseAll()
    {
        lock (_sync) ReleaseAllLocked();
    }

    private void ReleaseAllLocked()
    {
        foreach (var k in _keysDown) SendKey(k.Vk, k.Scan, k.Ext, true);
        _keysDown.Clear();
        foreach (var b in _buttonsDown.ToArray()) SendButton(b, false);
        _buttonsDown.Clear();
    }

    private void MoveTo(ushort nx, ushort ny)
    {
        int x = _monitor.X + (int)((long)nx * Math.Max(0, _monitor.Width - 1) / 65535);
        int y = _monitor.Y + (int)((long)ny * Math.Max(0, _monitor.Height - 1) / 65535);
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(2, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
        int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        SendMouse(ax, ay, 0, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK);
    }

    private void SendButton(byte idx, bool down)
    {
        (uint flags, uint data) = idx switch
        {
            0 => (down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP, 0u),
            1 => (down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP, 0u),
            2 => (down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP, 0u),
            3 => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, Native.XBUTTON1),
            4 => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, Native.XBUTTON2),
            _ => (0u, 0u),
        };
        if (flags == 0) return;
        SendMouse(0, 0, data, flags);
        if (down) _buttonsDown.Add(idx); else _buttonsDown.Remove(idx);
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
        };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }

    private static void SendKey(ushort vk, ushort scan, bool extended, bool up)
    {
        // Jogos (DirectInput/Raw Input) leem o código físico da tecla: injeta por scan code quando houver.
        uint flags = (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0) | (up ? Native.KEYEVENTF_KEYUP : 0);
        if (scan != 0) flags |= Native.KEYEVENTF_SCANCODE;
        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = scan != 0 ? (ushort)0 : vk, wScan = scan, dwFlags = flags } },
        };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }
}

/// <summary>Detecta, no computador que transmite, se um jogo prendeu ou escondeu o cursor.</summary>
internal static class CursorCapture
{
    public static bool IsCaptured(Rectangle monitor)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        bool hidden = Native.GetCursorInfo(ref ci) && (ci.flags & Native.CURSOR_SHOWING) == 0;
        bool clipped = false;
        if (Native.GetClipCursor(out var clip))
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            // Preso numa área bem menor que a área de trabalho (jogos prendem o cursor na janela ou num ponto).
            clipped = (clip.Right - clip.Left) * (long)(clip.Bottom - clip.Top) < (long)vw * vh / 2 &&
                      !(clip.Left == vx && clip.Top == vy && clip.Right == vx + vw && clip.Bottom == vy + vh);
        }
        return hidden || clipped;
    }
}
