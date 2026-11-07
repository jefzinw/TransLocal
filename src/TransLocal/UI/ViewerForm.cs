using System.ComponentModel;
using System.Runtime.InteropServices;
using TransLocal.Input;
using TransLocal.Video;

namespace TransLocal.UI;

/// <summary>Área que desenha a imagem recebida e (opcionalmente) captura mouse/teclado.</summary>
internal sealed class SurfaceView : Control
{
    private static readonly Cursor BlankCursor = CreateBlankCursor();
    private readonly FrameSurface _surface;
    private Rectangle _dest;
    private int _invalidatePending;
    private volatile bool _controlEnabled;
    private ushort _lastNx = ushort.MaxValue, _lastNy = ushort.MaxValue;

    public SurfaceView(FrameSurface surface)
    {
        _surface = surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.Selectable, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, false);
        TabStop = true;
        BackColor = Color.Black;
    }

    /// <summary>Envia eventos de entrada ao transmissor (null = sem controle disponível).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<byte[]>? SendInput { get; set; }

    /// <summary>Atalhos locais (não enviados ao outro computador).</summary>
    public event Action<string>? LocalCommand;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Overlay { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ControlEnabled
    {
        get => _controlEnabled;
        set
        {
            bool was = _controlEnabled;
            _controlEnabled = value && SendInput is not null;
            Cursor = _controlEnabled ? BlankCursor : Cursors.Default;
            if (was && !_controlEnabled) SendInput?.Invoke(InputCodec.EncodeReleaseAll());
            UpdateRelativeMode();
        }
    }

    // ---------------- Mouse relativo (jogos que prendem o cursor) ----------------

    private bool _remoteCaptured;
    private bool _forceAbsolute;
    private bool _relativeOn;

    /// <summary>O outro computador avisou que um jogo prendeu/escondeu o cursor.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RemoteCursorCaptured
    {
        get => _remoteCaptured;
        set
        {
            _remoteCaptured = value;
            UpdateRelativeMode();
        }
    }

    /// <summary>Modo relativo ativo agora (para a barra da janela).</summary>
    public bool RelativeActive => _relativeOn;

    public event Action? RelativeModeChanged;

    /// <summary>Ctrl+Alt+Shift+M: força o mouse absoluto mesmo com o cursor preso.</summary>
    public void ToggleForceAbsolute()
    {
        _forceAbsolute = !_forceAbsolute;
        UpdateRelativeMode();
    }

    private void UpdateRelativeMode()
    {
        if (!IsHandleCreated) return;
        bool want = _controlEnabled && _remoteCaptured && !_forceAbsolute && ContainsFocus;
        if (want == _relativeOn)
        {
            if (want) ClipToView();
            return;
        }
        _relativeOn = want;
        var rid = new[]
        {
            new Native.RAWINPUTDEVICE
            {
                usUsagePage = 0x01, // Generic Desktop
                usUsage = 0x02,     // Mouse
                dwFlags = want ? 0u : Native.RIDEV_REMOVE,
                hwndTarget = want ? Handle : IntPtr.Zero,
            },
        };
        Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
        if (want) ClipToView(); else Native.ClipCursorNone(IntPtr.Zero);
        RelativeModeChanged?.Invoke();
    }

    /// <summary>Mantém o cursor local dentro da imagem enquanto o modo relativo está ativo.</summary>
    private void ClipToView()
    {
        var r = RectangleToScreen(ClientRectangle);
        var rc = new Native.RECT(r.Left, r.Top, r.Right, r.Bottom);
        Native.ClipCursor(ref rc);
    }

    private readonly byte[] _rawBuffer = new byte[64];

    private void HandleRawInput(IntPtr lParam)
    {
        uint size = (uint)_rawBuffer.Length;
        uint header = (uint)(8 + 2 * IntPtr.Size);
        if (Native.GetRawInputData(lParam, Native.RID_INPUT, _rawBuffer, ref size, header) == uint.MaxValue) return;
        if (BitConverter.ToUInt32(_rawBuffer, 0) != Native.RIM_TYPEMOUSE) return;
        int mouse = (int)header;                       // RAWMOUSE logo após o cabeçalho
        ushort flags = BitConverter.ToUInt16(_rawBuffer, mouse);
        if ((flags & 0x01) != 0) return;               // MOUSE_MOVE_ABSOLUTE (tablets, área de trabalho remota)
        int dx = BitConverter.ToInt32(_rawBuffer, mouse + 12);
        int dy = BitConverter.ToInt32(_rawBuffer, mouse + 16);
        if (dx != 0 || dy != 0) SendInput?.Invoke(InputCodec.EncodeRelativeMove(dx, dy));
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        UpdateRelativeMode();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        // Teclas seguradas na hora de trocar de janela ficariam presas no outro computador.
        if (_controlEnabled) SendInput?.Invoke(InputCodec.EncodeReleaseAll());
        UpdateRelativeMode();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_relativeOn) Native.ClipCursorNone(IntPtr.Zero);
        base.OnHandleDestroyed(e);
    }

    public void NotifyFrame()
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (Interlocked.Exchange(ref _invalidatePending, 1) != 0) return;
        try
        {
            BeginInvoke(() =>
            {
                Volatile.Write(ref _invalidatePending, 0);
                Invalidate();
            });
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _invalidatePending, 0);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var hdc = e.Graphics.GetHdc();
        try
        {
            _dest = _surface.Paint(hdc, ClientRectangle);
        }
        finally
        {
            e.Graphics.ReleaseHdc(hdc);
        }

        if (!_surface.HasImage)
        {
            TextRenderer.DrawText(e.Graphics, "Aguardando imagem…", Font, ClientRectangle, Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (Overlay is { Length: > 0 } text)
        {
            var size = TextRenderer.MeasureText(e.Graphics, text, Font);
            var box = new Rectangle(8, Height - size.Height - 16, size.Width + 16, size.Height + 8);
            using var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
            e.Graphics.FillRectangle(bg, box);
            TextRenderer.DrawText(e.Graphics, text, Font, new Point(box.X + 8, box.Y + 4), Color.White);
        }
    }

    // ---------------- Mouse ----------------

    private bool Map(Point p, out ushort nx, out ushort ny)
    {
        nx = ny = 0;
        var d = _dest;
        if (d.Width <= 1 || d.Height <= 1) return false;
        double fx = Math.Clamp((p.X - d.X) / (double)(d.Width - 1), 0, 1);
        double fy = Math.Clamp((p.Y - d.Y) / (double)(d.Height - 1), 0, 1);
        nx = (ushort)Math.Round(fx * 65535);
        ny = (ushort)Math.Round(fy * 65535);
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // No modo relativo o movimento vai pelo Raw Input (WM_INPUT), não por posição.
        if (!_controlEnabled || _relativeOn || !Map(e.Location, out var nx, out var ny)) return;
        if (nx == _lastNx && ny == _lastNy) return;
        _lastNx = nx;
        _lastNy = ny;
        SendInput?.Invoke(InputCodec.EncodeMove(nx, ny));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Focused) Focus();
        if (!_controlEnabled) return;
        if (_relativeOn) SendInput?.Invoke(InputCodec.EncodeButtonOnly(e.Button, true));
        else if (Map(e.Location, out var nx, out var ny)) SendInput?.Invoke(InputCodec.EncodeButton(e.Button, true, nx, ny));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_controlEnabled) return;
        if (_relativeOn) SendInput?.Invoke(InputCodec.EncodeButtonOnly(e.Button, false));
        else if (Map(e.Location, out var nx, out var ny)) SendInput?.Invoke(InputCodec.EncodeButton(e.Button, false, nx, ny));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_controlEnabled) SendInput?.Invoke(InputCodec.EncodeWheel(e.Delta, false));
    }

    // ---------------- Teclado ----------------

    protected override bool IsInputKey(Keys keyData) => _controlEnabled || base.IsInputKey(keyData);

    public override bool PreProcessMessage(ref Message msg)
    {
        // Com controle ligado, as teclas vão direto para o WndProc (sem Tab/Alt/atalhos do WinForms).
        if (_controlEnabled && msg.Msg is Native.WM_KEYDOWN or Native.WM_KEYUP or Native.WM_SYSKEYDOWN or Native.WM_SYSKEYUP)
            return false;
        return base.PreProcessMessage(ref msg);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_INPUT:
                if (_relativeOn && _controlEnabled) HandleRawInput(m.LParam);
                break;
            case Native.WM_KEYDOWN:
            case Native.WM_SYSKEYDOWN:
            case Native.WM_KEYUP:
            case Native.WM_SYSKEYUP:
            {
                int vk = (int)(long)m.WParam;
                long lp = (long)m.LParam;
                bool up = m.Msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                if (!up && HandleLocalShortcut((Keys)vk)) return;
                if (_controlEnabled)
                {
                    int scan = (int)((lp >> 16) & 0xFF);
                    bool ext = (lp & (1 << 24)) != 0;
                    SendInput?.Invoke(InputCodec.EncodeKey(vk, scan, ext, up));
                    return; // não deixa o Windows tratar (Alt+F4, menu do sistema…)
                }
                break;
            }
            case Native.WM_CHAR:
            case Native.WM_SYSCHAR:
                if (_controlEnabled) return;
                break;
            case Native.WM_MOUSEHWHEEL:
                if (_controlEnabled)
                {
                    int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                    SendInput?.Invoke(InputCodec.EncodeWheel(delta, true));
                    return;
                }
                break;
        }
        base.WndProc(ref m);
    }

    private bool HandleLocalShortcut(Keys key)
    {
        var mods = ModifierKeys;
        bool cas = (mods & (Keys.Control | Keys.Alt | Keys.Shift)) == (Keys.Control | Keys.Alt | Keys.Shift);
        string? cmd = null;
        if (cas)
        {
            cmd = key switch
            {
                Keys.F => "fullscreen",
                Keys.Q => "close",
                Keys.C => "control",
                Keys.S => "stats",
                Keys.M => "mouse-mode",
                _ => null,
            };
        }
        else if (!_controlEnabled)
        {
            cmd = key switch
            {
                Keys.F11 => "fullscreen",
                Keys.Escape => "exit-fullscreen",
                _ => null,
            };
        }
        if (cmd is null) return false;
        LocalCommand?.Invoke(cmd);
        return true;
    }

    private static Cursor CreateBlankCursor()
    {
        try
        {
            var and = Enumerable.Repeat((byte)0xFF, 32 * 4).ToArray();
            var xor = new byte[32 * 4];
            var h = Native.CreateCursor(Native.GetModuleHandle(null), 0, 0, 32, 32, and, xor);
            if (h != IntPtr.Zero) return new Cursor(h);
        }
        catch { }
        return Cursors.Cross;
    }
}

/// <summary>Janela que exibe uma transmissão recebida ou a entrada HDMI.</summary>
internal sealed class ViewerForm : Form
{
    private readonly SurfaceView _view;
    private readonly Panel _bar;
    private readonly Label _barTitle;
    private readonly Label _gamepadLabel;
    private readonly Button _btnFull;
    private readonly Button _btnControl;
    private readonly Button _btnStats;
    private readonly System.Windows.Forms.Timer _barTimer;
    private readonly string _title;
    private readonly bool _controlAvailable;
    private bool _fullscreen;
    private bool _showStats;
    private bool _closingFromCode;
    private volatile bool _windowActive;
    private Rectangle _windowedBounds;
    private string? _lastStats;

    /// <summary>Verdadeiro quando o controle está ligado e esta janela está em primeiro plano
    /// (só então os controles de jogo são enviados). Lido por outra thread.</summary>
    public bool InputActive => _view.ControlEnabled && _windowActive;

    /// <summary>O usuário fechou a janela (não chamado quando fechada por código).</summary>
    public event Action? UserClosed;

    /// <summary>Mostrar sem tirar o foco de outra janela (usado pelo modo --ui-screenshot).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool NoActivate { get; set; }

    protected override bool ShowWithoutActivation => NoActivate;

    public ViewerForm(string title, FrameSurface surface, Action<byte[]>? sendInput, Screen target, bool fullscreen, bool showStats,
        Size initialImageSize, bool startWithControl = false)
    {
        _title = title;
        _controlAvailable = sendInput is not null;
        _showStats = showStats;

        Text = $"{title} — TransLocal";
        Icon = SystemIntegration.AppIcon;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = false;
        MinimumSize = new Size(320, 200);

        _view = new SurfaceView(surface) { Dock = DockStyle.Fill, SendInput = sendInput };
        _view.LocalCommand += OnLocalCommand;
        _view.RelativeModeChanged += UpdateTitleForMouseMode;

        _bar = new Panel { Height = 40, BackColor = Color.FromArgb(32, 32, 32), Dock = DockStyle.Top, Padding = new Padding(6, 4, 6, 4) };
        _barTitle = new Label
        {
            Text = title,
            ForeColor = Color.White,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Padding = new Padding(4, 0, 0, 0),
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        _gamepadLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(200, 220, 255),
            Margin = new Padding(4, 8, 8, 0),
            Visible = false,
        };
        _btnControl = BarButton(_controlAvailable ? "Controlar: desligado" : "Sem controle (o outro PC não permitiu)", (_, _) => ToggleControl());
        _btnControl.Enabled = _controlAvailable;
        _btnStats = BarButton("Estatísticas", (_, _) => ToggleStats());
        _btnFull = BarButton("Tela cheia", (_, _) => ToggleFullscreen());
        var btnClose = BarButton("Fechar", (_, _) => Close());
        flow.Controls.AddRange(new Control[] { _gamepadLabel, _btnControl, _btnStats, _btnFull, btnClose });
        _bar.Controls.Add(_barTitle);
        _bar.Controls.Add(flow);

        Controls.Add(_view);
        Controls.Add(_bar);

        // Janela inicial: 75% da área útil do monitor, na proporção da imagem.
        var work = target.WorkingArea;
        var img = initialImageSize.Width > 0 && initialImageSize.Height > 0 ? initialImageSize : new Size(16, 9);
        var fit = FrameSurface.Fit(new Rectangle(0, 0, work.Width * 3 / 4, work.Height * 3 / 4 - _bar.Height), img.Width, img.Height);
        var size = SizeFromClientSize(new Size(fit.Width, fit.Height + _bar.Height));
        _windowedBounds = new Rectangle(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2, size.Width, size.Height);
        Bounds = _windowedBounds;

        _barTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _barTimer.Tick += (_, _) => UpdateBarVisibility();

        if (fullscreen) SetFullscreen(true, target);
        UpdateStatsButton();
        if (startWithControl && _controlAvailable) ToggleControl();
    }

    /// <summary>Texto sobre os controles de jogo na barra (null esconde).</summary>
    public void SetGamepadInfo(string? text)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (_gamepadLabel.Text == (text ?? "") && _gamepadLabel.Visible == (text is not null)) return;
                _gamepadLabel.Text = text ?? "";
                _gamepadLabel.Visible = text is not null;
            });
        }
        catch (InvalidOperationException) { }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _windowActive = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _windowActive = false;
    }

    private Button BarButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(52, 52, 52),
            Margin = new Padding(4, 2, 0, 2),
            Padding = new Padding(6, 0, 6, 0),
            TabStop = false,
            Height = 30,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
        b.Click += (s, e) =>
        {
            onClick(s, e);
            _view.Focus();
        };
        return b;
    }

    public void NotifyFrame() => _view.NotifyFrame();

    public void SetStats(string text)
    {
        _lastStats = text;
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _view.Overlay = _showStats ? _lastStats : null;
                if (_showStats) _view.Invalidate();
            });
        }
        catch (InvalidOperationException) { }
    }

    public void CloseFromCode()
    {
        _closingFromCode = true;
        if (!IsDisposed) Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!NoActivate)
        {
            Activate();
            _view.Focus();
        }
        _barTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _barTimer.Stop();
        _barTimer.Dispose();
        base.OnFormClosed(e);
        if (!_closingFromCode) UserClosed?.Invoke();
    }

    private void OnLocalCommand(string cmd)
    {
        switch (cmd)
        {
            case "fullscreen": ToggleFullscreen(); break;
            case "exit-fullscreen": if (_fullscreen) SetFullscreen(false, null); break;
            case "close": Close(); break;
            case "control": ToggleControl(); break;
            case "stats": ToggleStats(); break;
            case "mouse-mode": _view.ToggleForceAbsolute(); break;
        }
    }

    /// <summary>O outro computador avisou se um jogo prendeu o cursor (mouse relativo).</summary>
    public void SetRemoteCursorCaptured(bool captured)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => _view.RemoteCursorCaptured = captured); } catch (InvalidOperationException) { }
    }

    private void UpdateTitleForMouseMode()
    {
        if (!_view.ControlEnabled) return;
        _barTitle.Text = _view.RelativeActive
            ? $"{_title} — mouse de jogo (relativo) • Ctrl+Alt+Shift+M alterna • Ctrl+Alt+Shift+C solta"
            : $"{_title} — controlando (Ctrl+Alt+Shift+C para soltar)";
    }

    private void ToggleControl()
    {
        if (!_controlAvailable) return;
        _view.ControlEnabled = !_view.ControlEnabled;
        _btnControl.Text = _view.ControlEnabled ? "Controlar: ligado" : "Controlar: desligado";
        _barTitle.Text = _view.ControlEnabled
            ? $"{_title} — controlando (Ctrl+Alt+Shift+C para soltar)"
            : _title;
    }

    private void ToggleStats()
    {
        _showStats = !_showStats;
        _view.Overlay = _showStats ? _lastStats : null;
        UpdateStatsButton();
        _view.Invalidate();
    }

    private void UpdateStatsButton() => _btnStats.Text = _showStats ? "Estatísticas: sim" : "Estatísticas: não";

    private void ToggleFullscreen() => SetFullscreen(!_fullscreen, null);

    private void SetFullscreen(bool on, Screen? target)
    {
        if (on == _fullscreen && target is null) return;
        SuspendLayout();
        if (on)
        {
            if (!_fullscreen && WindowState == FormWindowState.Normal) _windowedBounds = Bounds;
            var screen = target ?? Screen.FromControl(this);
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen.Bounds;
            _bar.Dock = DockStyle.None;
            _bar.Visible = false;
            _bar.SetBounds(0, 0, ClientSize.Width, _bar.Height);
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _btnFull.Text = "Sair da tela cheia";
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _windowedBounds;
            _bar.Anchor = AnchorStyles.None;
            _bar.Dock = DockStyle.Top;
            _bar.Visible = true;
            _btnFull.Text = "Tela cheia";
        }
        _fullscreen = on;
        // Em janela: a imagem (Fill) precisa estar à frente da barra (Top) para o encaixe correto.
        // Em tela cheia: a barra fica por cima da imagem.
        if (on) _bar.BringToFront(); else _view.BringToFront();
        ResumeLayout(true);
        _view.Invalidate();
    }

    private void UpdateBarVisibility()
    {
        if (!_fullscreen || !Visible) return;
        var p = PointToClient(Cursor.Position);
        bool inside = ClientRectangle.Contains(p);
        if (!_bar.Visible && inside && p.Y <= 3 && ContainsFocus)
        {
            _bar.SetBounds(0, 0, ClientSize.Width, _bar.Height);
            _bar.Visible = true;
            _bar.BringToFront();
        }
        else if (_bar.Visible && (!inside || p.Y > _bar.Height + 40))
        {
            _bar.Visible = false;
            _view.Invalidate(new Rectangle(0, 0, ClientSize.Width, _bar.Height));
        }
    }
}
