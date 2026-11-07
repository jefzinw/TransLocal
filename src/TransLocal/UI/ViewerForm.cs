using System.ComponentModel;
using System.Runtime.InteropServices;
using TransLocal.Audio;
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

    // ---------------- Exibição pela placa de vídeo ----------------

    private volatile GpuPresenter? _gpu;
    private Action<IGpuFrameSink?>? _setSink;

    /// <summary>Exibição pela placa de vídeo ativa (senão, desenho por GDI).</summary>
    public bool GpuActive => _gpu is { Failed: false };

    /// <summary>
    /// Passa a exibir pela placa de vídeo (cadeia de troca DXGI nesta janela). <paramref name="setSink"/> recebe a
    /// exibição para o decodificador entregar quadros direto a ela (e null quando ela deixa de existir).
    /// </summary>
    public bool AttachGpu(GpuDevice device, Action<IGpuFrameSink?> setSink)
    {
        if (!IsHandleCreated || IsDisposed || _gpu is not null) return false;
        var p = GpuPresenter.TryCreate(device, Handle, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        if (p is null) return false;
        p.SetFont(Font.Name, Font.SizeInPoints * DeviceDpi / 72f);
        p.Overlay = () => new OverlayText(Overlay, Notice, _surface.HasImage || p.HasImage ? null : "Aguardando imagem…");
        p.Lost += () =>
        {
            try { BeginInvoke(DetachGpu); } catch { }
        };
        _gpu = p;
        _setSink = setSink;
        if (_surface.HasImage) p.PresentSurface(_surface); else p.Redraw();
        setSink(p);
        return true;
    }

    /// <summary>Volta ao desenho por GDI (a placa de vídeo falhou ou a janela vai fechar).</summary>
    private void DetachGpu()
    {
        var p = _gpu;
        if (p is null) return;
        _setSink?.Invoke(null);
        _gpu = null;
        p.Dispose();
        if (!IsDisposed) Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DetachGpu();
        base.Dispose(disposing);
    }

    public void NotifyFrame()
    {
        if (!IsHandleCreated || IsDisposed) return;
        // Pela placa de vídeo: mostra já, nesta thread (sem esperar a vez na fila de mensagens da janela).
        if (_gpu is { Failed: false } gpu && gpu.PresentSurface(_surface)) return;
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
        _gpu?.Resize(ClientSize.Width, ClientSize.Height);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_gpu is { Failed: false } gpu)
        {
            // A imagem e os textos são desenhados pela placa de vídeo; GDI aqui ficaria por baixo da cadeia de troca.
            gpu.Resize(ClientSize.Width, ClientSize.Height);
            gpu.Redraw();
            _dest = gpu.Dest;
            return;
        }
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
            using var bg = new SolidBrush(Color.FromArgb(215, 18, 18, 21));
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(box, 12)) e.Graphics.FillPath(bg, path);
            TextRenderer.DrawText(e.Graphics, text, Font, new Point(box.X + 8, box.Y + 4), Color.White);
        }
        if (Notice is { Length: > 0 } notice)
        {
            int maxW = Math.Max(200, Width - 80);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter;
            var size = TextRenderer.MeasureText(e.Graphics, notice, Font, new Size(maxW, 0), flags);
            var box = new Rectangle((Width - size.Width) / 2 - 12, 12, size.Width + 24, size.Height + 14);
            using var bg = new SolidBrush(Color.FromArgb(225, Theme.Accent));
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(box, 14)) e.Graphics.FillPath(bg, path);
            TextRenderer.DrawText(e.Graphics, notice, Font, new Rectangle(box.X + 12, box.Y + 7, size.Width, size.Height), Color.White, flags);
        }
    }

    /// <summary>Aviso em destaque no topo da imagem (null esconde).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Notice { get; set; }

    // ---------------- Mouse ----------------

    private bool Map(Point p, out ushort nx, out ushort ny)
    {
        nx = ny = 0;
        var d = _gpu is { Failed: false, HasImage: true } gpu ? gpu.Dest : _dest;
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
                Keys.A => "audio-next",
                Keys.D => "diag",
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
    private readonly Button? _btnAudio;
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
        Size initialImageSize, bool startWithControl = false, bool audio = false)
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

        _bar = new Panel { Height = 46, BackColor = Theme.Background, Dock = DockStyle.Top, Padding = new Padding(10, 4, 10, 4) };
        Theme.DarkTitleBar(this);
        _barTitle = new Label
        {
            Text = title,
            Font = Theme.Semibold(11F),
            ForeColor = Theme.Text,
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
        flow.Controls.Add(_gamepadLabel);
        if (audio)
        {
            // O clique do BarButton devolve o foco à imagem; o menu abre logo depois.
            _btnAudio = BarButton("Som", (_, _) => BeginInvoke(ShowAudioMenu));
            flow.Controls.Add(_btnAudio);
            AudioOutput.Changed += OnAudioOutputsChanged;
            HandleCreated += (_, _) => UpdateAudioButton();
        }
        _btnDiag = BarButton("Diagnóstico", (_, _) => ToggleDiag());
        _btnDiag.Visible = false;
        flow.Controls.AddRange(new Control[] { _btnControl, _btnStats, _btnDiag, _btnFull, btnClose });
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
        var b = new PillButton
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(6, 3, 0, 3),
            TabStop = false,
            Font = Theme.Semibold(9F),
            MinimumSize = new Size(0, 32),
        };
        b.Click += (s, e) =>
        {
            onClick(s, e);
            _view.Focus();
        };
        return b;
    }

    public void NotifyFrame() => _view.NotifyFrame();

    /// <summary>Exibe pela placa de vídeo (quadros H.264 vão direto do decodificador para a tela). False = segue por GDI.</summary>
    public bool UseGpu(GpuDevice gpu, Action<IGpuFrameSink?> setSink) => _view.AttachGpu(gpu, setSink);

    /// <summary>A imagem está sendo exibida pela placa de vídeo.</summary>
    public bool GpuActive => _view.GpuActive;

    public void SetStats(string text)
    {
        _lastStats = text;
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (_showStats || _showDiag) RefreshOverlay();
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
        AudioOutput.Changed -= OnAudioOutputsChanged;
        _noticeTimer?.Dispose();
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
            case "audio-next": if (_btnAudio is not null) NextAudioOutput(); break;
            case "diag": ToggleDiag(); break;
        }
    }

    // ---------- Avisos e diagnóstico ----------

    private System.Windows.Forms.Timer? _noticeTimer;
    private Button? _btnDiag;
    private bool _showDiag;
    private Func<string[]>? _diagLines;
    private Func<int>? _diagProblems;

    /// <summary>Liga o painel de diagnóstico da sessão (linhas dos dois computadores).</summary>
    public void AttachDiagnostics(Func<string[]> lines, Func<int> problems)
    {
        _diagLines = lines;
        _diagProblems = problems;
        if (_btnDiag is not null) _btnDiag.Visible = true;
        UpdateDiagButton();
    }

    /// <summary>Linhas novas chegaram (qualquer thread).</summary>
    public void DiagnosticsUpdated()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                UpdateDiagButton();
                if (_showDiag) RefreshOverlay();
            });
        }
        catch (InvalidOperationException) { }
    }

    private void UpdateDiagButton()
    {
        if (_btnDiag is null || _diagProblems is null) return;
        int n = _diagProblems();
        string text = n > 0 ? $"Diagnóstico ⚠ {n}" : "Diagnóstico";
        if (_btnDiag.Text != text) _btnDiag.Text = text;
        _btnDiag.ForeColor = n > 0 ? Color.FromArgb(255, 200, 120) : Color.White;
    }

    private void ToggleDiag()
    {
        if (_diagLines is null) return;
        _showDiag = !_showDiag;
        RefreshOverlay();
    }

    private void RefreshOverlay()
    {
        var parts = new List<string>();
        if (_showStats && _lastStats is not null) parts.Add(_lastStats);
        if (_showDiag && _diagLines is not null)
        {
            var lines = _diagLines();
            parts.Add("Diagnóstico desta transmissão (Ctrl+Alt+Shift+D fecha) — salvo em %LOCALAPPDATA%\\TransLocal\\diagnostico");
            if (lines.Length == 0) parts.Add("  nada registrado ainda");
            foreach (var l in lines) parts.Add("  " + (l.Length > 160 ? l[..160] + "…" : l));
        }
        _view.Overlay = parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
        _view.Invalidate();
    }

    /// <summary>Mostra um aviso do outro computador no topo da imagem por 15 s (qualquer thread).</summary>
    public void ShowNotice(string text)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _view.Notice = text;
                _view.Invalidate();
                _noticeTimer?.Dispose();
                _noticeTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
                _noticeTimer.Tick += (_, _) =>
                {
                    _noticeTimer?.Stop();
                    _view.Notice = null;
                    _view.Invalidate();
                };
                _noticeTimer.Start();
            });
        }
        catch (InvalidOperationException) { }
    }

    // ---------- Saída de som ----------

    private static string Short(string? name, int max = 28) =>
        string.IsNullOrEmpty(name) ? "nenhuma" : name.Length <= max ? name : name[..(max - 1)] + "…";

    private void OnAudioOutputsChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(UpdateAudioButton); } catch (InvalidOperationException) { }
    }

    private void UpdateAudioButton()
    {
        if (_btnAudio is null || IsDisposed) return;
        string? current = null;
        string? preferred = AudioOutput.Preferred;
        if (preferred is not null) current = AudioOutput.List().FirstOrDefault(o => o.Id == preferred).Name;
        bool missing = preferred is not null && current is null;
        current ??= AudioOutput.DefaultName();
        _btnAudio.Text = $"Som: {Short(current)}{(missing ? " (escolhido desligado)" : "")} ▾";
    }

    private void ShowAudioMenu()
    {
        if (_btnAudio is null) return;
        var menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false, Renderer = new Theme.MenuRenderer() };
        string? preferred = AudioOutput.Preferred;
        var def = new ToolStripMenuItem($"Padrão do Windows ({Short(AudioOutput.DefaultName(), 40)}) — muda sozinho ao ligar um fone")
        {
            Checked = preferred is null,
        };
        def.Click += (_, _) => { AudioOutput.Preferred = null; UpdateAudioButton(); };
        menu.Items.Add(def);
        menu.Items.Add(new ToolStripSeparator());
        var outputs = AudioOutput.List();
        foreach (var (id, name) in outputs)
        {
            var item = new ToolStripMenuItem(name) { Checked = id == preferred };
            item.Click += (_, _) => { AudioOutput.Preferred = id; UpdateAudioButton(); };
            menu.Items.Add(item);
        }
        if (outputs.Count == 0) menu.Items.Add(new ToolStripMenuItem("Nenhuma saída de som ligada") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        var hint = new ToolStripMenuItem("Ctrl+Alt+Shift+A troca para a próxima saída") { Enabled = false };
        menu.Items.Add(hint);
        menu.Closed += (_, _) => BeginInvoke(() => { menu.Dispose(); _view.Focus(); });
        menu.Show(_btnAudio, new Point(0, _btnAudio.Height));
    }

    /// <summary>Atalho: Padrão → cada saída ligada → Padrão…</summary>
    private void NextAudioOutput()
    {
        var outputs = AudioOutput.List();
        string? preferred = AudioOutput.Preferred;
        int idx = preferred is null ? -1 : outputs.FindIndex(o => o.Id == preferred);
        string? next = idx + 1 < outputs.Count ? outputs[idx + 1].Id : null;
        AudioOutput.Preferred = next;
        UpdateAudioButton();
        string label = next is null ? $"Padrão do Windows ({AudioOutput.DefaultName() ?? "nenhuma"})" : outputs[idx + 1].Name;
        _barTitle.Text = $"{_title} — som em: {label}";
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
        UpdateStatsButton();
        RefreshOverlay();
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
