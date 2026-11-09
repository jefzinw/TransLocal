using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TransLocal.Input;

namespace TransLocal.UI;

/// <summary>
/// Trava de atalhos: com a janela de exibição em primeiro plano e o controle ligado, TODAS as teclas — inclusive
/// Alt+Tab, a tecla Windows, Alt+Esc, Ctrl+Esc e Alt+F4 — vão para o outro computador, e este não as usa.
/// Usa um gancho de teclado de baixo nível (como Parsec/Moonlight no modo imersivo). Ctrl+Alt+Del continua local
/// (o Windows não deixa nenhum programa capturar). Ctrl+Alt+Shift+&lt;tecla&gt; continua sendo atalho do TransLocal.
/// </summary>
internal sealed class KeyboardLock : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _proc; // mantido vivo enquanto o gancho existir
    private readonly IntPtr _hook;
    private readonly Func<IntPtr> _window;
    private readonly Func<bool> _canSend;
    private readonly Action<byte[]> _send;
    private readonly Action<Keys> _localShortcut;
    private readonly BlockingCollection<byte[]> _queue = new();
    private readonly Thread _sender;
    private bool _ctrl, _alt, _shift;
    private volatile bool _enabled;

    /// <param name="window">Janela que precisa estar em primeiro plano.</param>
    /// <param name="canSend">Controle ligado (as teclas podem ir para o outro computador).</param>
    /// <param name="send">Envia um evento de entrada já codificado.</param>
    /// <param name="localShortcut">Ctrl+Alt+Shift+&lt;tecla&gt; pressionado com a trava valendo (atalho do TransLocal).</param>
    public KeyboardLock(Func<IntPtr> window, Func<bool> canSend, Action<byte[]> send, Action<Keys> localShortcut)
    {
        _localShortcut = localShortcut;
        _window = window;
        _canSend = canSend;
        _send = send;
        _proc = Hook;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn($"Trava de atalhos indisponível (erro {Marshal.GetLastWin32Error()})");
        // O gancho precisa responder rápido (o Windows o desliga se demorar): o envio pela rede fica noutra thread.
        _sender = new Thread(() =>
        {
            foreach (var m in _queue.GetConsumingEnumerable())
            {
                try { _send(m); } catch { }
            }
        }) { IsBackground = true, Name = "TransLocal trava de teclado" };
        _sender.Start();
    }

    /// <summary>Trava ligada pelo usuário (só age com a janela em primeiro plano e o controle ligado).</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public bool Available => _hook != IntPtr.Zero;

    /// <summary>A trava está valendo agora.</summary>
    public bool Active => _enabled && _hook != IntPtr.Zero && _canSend() && GetForegroundWindow() == _window();

    private IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((k.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        bool up = (k.flags & LLKHF_UP) != 0;
        switch (k.vkCode)
        {
            case 0xA2 or 0xA3 or 0x11: _ctrl = !up; break;
            case 0xA4 or 0xA5 or 0x12: _alt = !up; break;
            case 0xA0 or 0xA1 or 0x10: _shift = !up; break;
        }
        if (!Active) return CallNextHookEx(_hook, nCode, wParam, lParam);
        bool isModifier = k.vkCode is >= 0xA0 and <= 0xA5 or 0x10 or 0x11 or 0x12;
        // Ctrl+Alt+Shift+<tecla>: atalho do TransLocal (tratado aqui; não vai para o outro computador). A janela não
        // veria os modificadores, que a trava engoliu.
        if (_ctrl && _alt && _shift && !isModifier)
        {
            if (!up) _localShortcut((Keys)k.vkCode);
            return 1;
        }
        bool ext = (k.flags & LLKHF_EXTENDED) != 0;
        _queue.Add(InputCodec.EncodeKey((int)k.vkCode, (int)k.scanCode, ext, up));
        return 1; // este computador não vê a tecla (nada de Menu Iniciar, Alt+Tab local etc.)
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _queue.CompleteAdding();
    }
}
