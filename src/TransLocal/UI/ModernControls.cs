using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TransLocal.UI;

/// <summary>Botão em forma de pílula: principal em degradê laranja, comum escuro com borda fina.</summary>
internal class PillButton : Button
{
    private bool _hover, _down;

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = Theme.Semibold(9.5F);
        ForeColor = Theme.Text;
        MinimumSize = new Size(0, 34);
    }

    [DefaultValue(false)]
    public bool Primary { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + 32 + Padding.Horizontal, Math.Max(34, t.Height + 14));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, r.Height / 2);
        if (Primary && Enabled)
        {
            var c1 = _down ? Theme.Accent : _hover ? Theme.AccentLight : Theme.Accent;
            var c2 = _down ? Theme.Accent : Color.FromArgb(255, 140, 60);
            using var b = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), c1, c2, LinearGradientMode.Vertical);
            g.FillPath(b, path);
        }
        else
        {
            using var b = new SolidBrush(!Enabled ? Theme.Surface : _down ? Theme.Border : _hover ? Theme.Hover : Theme.Elevated);
            g.FillPath(b, path);
            using var pen = new Pen(Focused && ShowFocusCues ? Theme.Accent : Theme.Border);
            g.DrawPath(pen, path);
        }
        var color = !Enabled ? Theme.Muted : Primary ? Color.White : Theme.Text;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Caixa de seleção desenhada como interruptor (liga/desliga), com o texto ao lado.</summary>
internal class ToggleCheck : CheckBox
{
    private const int SwitchW = 38, SwitchH = 22, Gap = 12;
    private bool _hover;

    public ToggleCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        ForeColor = Theme.Text;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int maxText = MaximumSize.Width > 0 ? MaximumSize.Width - SwitchW - Gap : int.MaxValue;
        var t = TextRenderer.MeasureText(Text, Font, new Size(maxText, 0), TextFormatFlags.WordBreak);
        return new Size(SwitchW + Gap + t.Width + 2, Math.Max(SwitchH + 6, t.Height + 6));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int top = Math.Max(0, Math.Min((Height - SwitchH) / 2, 3));
        var r = new RectangleF(0.5f, top + 0.5f, SwitchW - 1, SwitchH - 1);
        using (var path = Theme.Rounded(r, SwitchH / 2f))
        {
            if (Checked && Enabled)
            {
                using var b = new LinearGradientBrush(r, Theme.Accent, Color.FromArgb(255, 140, 60), LinearGradientMode.Horizontal);
                g.FillPath(b, path);
            }
            else
            {
                using var b = new SolidBrush(Checked ? Color.FromArgb(110, Theme.Accent) : _hover && Enabled ? Theme.Hover : Color.FromArgb(58, 58, 66));
                g.FillPath(b, path);
            }
        }
        float knob = SwitchH - 6;
        float kx = Checked ? r.Right - knob - 3 : r.X + 3;
        using (var kb = new SolidBrush(Enabled ? Color.White : Color.FromArgb(150, 150, 150)))
            g.FillEllipse(kb, kx, r.Y + 3, knob, knob);
        var textRect = new Rectangle(SwitchW + Gap, 0, Width - SwitchW - Gap, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(Theme.AccentLight) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, 0, top, SwitchW - 1, SwitchH - 1);
        }
    }
}

/// <summary>Cartão: painel com cantos arredondados, fundo um tom acima e borda fina. Organiza filhos em uma coluna.</summary>
internal class Card : TableLayoutPanel
{
    public Card()
    {
        DoubleBuffered = true;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 16, 20, 16);
        Margin = new Padding(0, 0, 0, 14);
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Rounded(r, 16);
        using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
        using var pen = new Pen(Theme.Border);
        g.DrawPath(pen, path);
    }

    public void AddRow(Control c)
    {
        RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(c, 0, RowCount - 1);
    }
}

/// <summary>Uma "aba" de conteúdo (substitui TabPage).</summary>
internal class PagePanel : Panel
{
    public PagePanel(string text)
    {
        Text = text;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        Visible = false;
    }
}

/// <summary>Mostra uma página por vez (substitui o TabControl; a navegação é a <see cref="PillTabBar"/>).</summary>
internal class PageHost : Panel
{
    private readonly List<PagePanel> _pages = new();
    private PagePanel? _selected;

    public PageHost()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
    }

    public event EventHandler? SelectedIndexChanged;

    public IReadOnlyList<PagePanel> Pages => _pages;

    public void AddRange(params PagePanel[] pages)
    {
        foreach (var p in pages)
        {
            _pages.Add(p);
            Controls.Add(p);
        }
        SelectedTab ??= _pages.FirstOrDefault();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PagePanel? SelectedTab
    {
        get => _selected;
        set
        {
            if (value is null || value == _selected) return;
            SuspendLayout();
            foreach (var p in _pages) p.Visible = p == value;
            _selected = value;
            ResumeLayout(true);
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected is null ? -1 : _pages.IndexOf(_selected);
        set { if (value >= 0 && value < _pages.Count) SelectedTab = _pages[value]; }
    }
}

/// <summary>Barra de navegação em pílulas (a selecionada em branco), como nos painéis modernos.</summary>
internal class PillTabBar : Control
{
    private readonly List<string> _items = new();
    private readonly List<RectangleF> _rects = new();
    private int _selected, _hover = -1;

    public PillTabBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.Semibold(9.25F);
        Height = 40;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public event Action<int>? SelectedChanged;

    public void SetItems(IEnumerable<string> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value == _selected || value < 0 || value >= _items.Count) return;
            _selected = value;
            Invalidate();
        }
    }

    /// <summary>Largura que as pílulas ocupam.</summary>
    public int ContentWidth
    {
        get
        {
            int w = 0;
            foreach (var s in _items) w += TextRenderer.MeasureText(s, Font).Width + 30 + 6;
            return Math.Max(0, w - 6);
        }
    }

    private void Layout2()
    {
        _rects.Clear();
        float x = Math.Max(0, (Width - ContentWidth) / 2f);
        foreach (var s in _items)
        {
            float w = TextRenderer.MeasureText(s, Font).Width + 30;
            _rects.Add(new RectangleF(x, (Height - 34) / 2f, w, 34));
            x += w + 6;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Layout2();
        for (int i = 0; i < _items.Count; i++)
        {
            var r = _rects[i];
            using var path = Theme.Rounded(r, r.Height / 2);
            bool sel = i == _selected;
            using (var b = new SolidBrush(sel ? Color.FromArgb(246, 246, 248) : i == _hover ? Theme.Hover : Color.FromArgb(28, 28, 32)))
                g.FillPath(b, path);
            if (!sel)
            {
                using var pen = new Pen(Theme.Border);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, _items[i], Font, Rectangle.Round(r), sel ? Color.FromArgb(18, 18, 20) : Color.FromArgb(205, 205, 212),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _rects.Count; i++) if (_rects[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h >= 0) Select(h);
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left && _selected > 0) Select(_selected - 1);
        else if (e.KeyCode == Keys.Right && _selected < _items.Count - 1) Select(_selected + 1);
        base.OnKeyDown(e);
    }

    private void Select(int i)
    {
        if (i == _selected) return;
        _selected = i;
        Invalidate();
        SelectedChanged?.Invoke(i);
    }
}

/// <summary>
/// Cabeçalho: logotipo redondo e nome à esquerda, navegação em pílulas no centro e um selo de estado à direita.
/// </summary>
internal class ModernHeader : Panel
{
    public PillTabBar Tabs { get; } = new();
    private string _status = "";
    private Color _statusColor = Theme.Muted;

    public ModernHeader()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Top;
        Height = 72;
        BackColor = Theme.Background;
        Controls.Add(Tabs);
    }

    public void SetStatus(string text, Color color)
    {
        if (text == _status && color == _statusColor) return;
        _status = text;
        _statusColor = color;
        PerformLayout();
        Invalidate();
    }

    private int StatusWidth => string.IsNullOrEmpty(_status) ? 0 : TextRenderer.MeasureText(_status, Theme.Semibold(9F)).Width + 40;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int left = 190, right = Math.Max(StatusWidth + 24, 40);
        int avail = Width - left - right;
        int w = Math.Min(avail, Math.Max(Tabs.ContentWidth + 8, 100));
        int x = Math.Max(left, (Width - w) / 2);
        if (x + w > Width - right) x = Math.Max(left, Width - right - w);
        Tabs.SetBounds(x, (Height - 40) / 2, Math.Max(100, w), 40);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        // Logotipo: círculo branco com o símbolo da transmissão.
        var logo = new RectangleF(22, (Height - 40) / 2f, 40, 40);
        using (var b = new SolidBrush(Color.FromArgb(246, 246, 248))) g.FillEllipse(b, logo);
        using (var pen = new Pen(Color.FromArgb(18, 18, 20), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = logo.X + logo.Width / 2, cy = logo.Y + logo.Height / 2;
            g.DrawRectangle(pen, cx - 10, cy - 8, 20, 13);
            g.DrawLine(pen, cx - 5, cy + 9, cx + 5, cy + 9);
        }
        using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, logo.Right - 13, logo.Y + 2, 10, 10);
        TextRenderer.DrawText(g, "TransLocal", Theme.Semibold(15F), new Point(72, (Height - 30) / 2), Theme.Text);

        if (!string.IsNullOrEmpty(_status))
        {
            var f = Theme.Semibold(9F);
            int w = StatusWidth;
            var r = new RectangleF(Width - w - 22, (Height - 32) / 2f, w, 32);
            using var path = Theme.Rounded(r, 16);
            using (var b = new SolidBrush(Color.FromArgb(28, 28, 32))) g.FillPath(b, path);
            using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            using (var dot = new SolidBrush(_statusColor)) g.FillEllipse(dot, r.X + 14, r.Y + 12, 8, 8);
            TextRenderer.DrawText(g, _status, f, new Rectangle((int)r.X + 28, (int)r.Y, w - 32, (int)r.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }
    }
}
