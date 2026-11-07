using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TransLocal.UI;

/// <summary>
/// Visual escuro do TransLocal (painéis arredondados, abas em pílula, destaques laranja/roxo), aplicado por cima dos
/// controles comuns do Windows: a lógica das telas não muda, só a aparência.
/// </summary>
internal static class Theme
{
    // Paleta
    public static readonly Color Background = Color.FromArgb(13, 13, 15);
    public static readonly Color Surface = Color.FromArgb(24, 24, 27);
    public static readonly Color Elevated = Color.FromArgb(36, 36, 41);
    public static readonly Color Hover = Color.FromArgb(48, 48, 54);
    public static readonly Color Border = Color.FromArgb(44, 44, 50);
    public static readonly Color Text = Color.FromArgb(242, 242, 244);
    public static readonly Color Muted = Color.FromArgb(150, 150, 160);
    public static readonly Color Accent = Color.FromArgb(255, 106, 26);      // laranja
    public static readonly Color AccentLight = Color.FromArgb(255, 146, 72);
    public static readonly Color Accent2 = Color.FromArgb(124, 92, 255);     // roxo
    public static readonly Color Good = Color.FromArgb(34, 197, 94);
    public static readonly Color Bad = Color.FromArgb(244, 63, 94);
    public static readonly Color Warn = Color.FromArgb(250, 204, 21);

    public static Font Font(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
    public static Font Semibold(float size) => new("Segoe UI Semibold", size);
    public static Font Light(float size) => new("Segoe UI Light", size);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------------- Integração com o modo escuro do Windows ----------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    private static int _appModeSet;

    /// <summary>Menus, barras de rolagem e listas nativas no modo escuro (Windows 10 1903+; sem efeito em versões antigas).</summary>
    public static void EnableDarkApp()
    {
        if (Interlocked.Exchange(ref _appModeSet, 1) != 0) return;
        try { SetPreferredAppMode(2 /* ForceDark */); FlushMenuThemes(); } catch { }
    }

    /// <summary>Barra de título escura e cantos arredondados (Windows 11).</summary>
    public static void DarkTitleBar(Form f)
    {
        void Apply()
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(f.Handle, 33, ref round, 4);
                int caption = ColorTranslator.ToWin32(Background);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4); // cor da barra de título (Windows 11)
            }
            catch { }
        }
        if (f.IsHandleCreated) Apply(); else f.HandleCreated += (_, _) => Apply();
    }

    private static void NativeDark(Control c, string theme)
    {
        void Apply() { try { SetWindowTheme(c.Handle, theme, null); } catch { } }
        if (c.IsHandleCreated) Apply(); else c.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Aplica o visual escuro a uma janela inteira (controles comuns ganham cores e bordas do tema).</summary>
    public static void Apply(Control root)
    {
        EnableDarkApp();
        if (root is Form form)
        {
            form.BackColor = Background;
            form.ForeColor = Text;
            DarkTitleBar(form);
        }
        Style(root);
    }

    private static void Style(Control c)
    {
        switch (c)
        {
            case PillButton or ToggleCheck or Card or PillTabBar or ModernHeader:
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = Elevated;
                b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = Hover;
                b.FlatAppearance.MouseDownBackColor = Border;
                b.UseVisualStyleBackColor = false;
                break;
            case CheckBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.ForeColor = Text;
                break;
            case RadioButton rb:
                rb.FlatStyle = FlatStyle.Flat;
                rb.ForeColor = Text;
                break;
            case ComboBox cmb:
                cmb.FlatStyle = FlatStyle.Flat;
                cmb.BackColor = Elevated;
                cmb.ForeColor = Text;
                NativeDark(cmb, "DarkMode_CFD");
                break;
            case TextBox tb:
                tb.BorderStyle = BorderStyle.FixedSingle;
                tb.BackColor = Elevated;
                tb.ForeColor = Text;
                NativeDark(tb, "DarkMode_CFD");
                break;
            case NumericUpDown nud:
                nud.BorderStyle = BorderStyle.FixedSingle;
                nud.BackColor = Elevated;
                nud.ForeColor = Text;
                break;
            case ListView lv:
                lv.BackColor = Elevated;
                lv.ForeColor = Text;
                lv.BorderStyle = BorderStyle.None;
                NativeDark(lv, "DarkMode_Explorer");
                if (lv.View == View.Details && !lv.OwnerDraw)
                {
                    lv.OwnerDraw = true;
                    lv.DrawColumnHeader += (_, e) =>
                    {
                        using var bg = new SolidBrush(Surface);
                        e.Graphics.FillRectangle(bg, e.Bounds);
                        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Semibold(9F), new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                            Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    };
                    lv.DrawItem += (_, e) => e.DrawDefault = true;
                    lv.DrawSubItem += (_, e) => e.DrawDefault = true;
                    // A última coluna ocupa o resto da largura (sem a sobra branca do cabeçalho).
                    void Fill()
                    {
                        if (lv.Columns.Count == 0) return;
                        int used = 0;
                        for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
                        lv.Columns[^1].Width = Math.Max(80, lv.ClientSize.Width - used - 1);
                    }
                    lv.Resize += (_, _) => Fill();
                    lv.HandleCreated += (_, _) => Fill();
                    Fill();
                }
                break;
            case ListBox lb:
                lb.BackColor = Elevated;
                lb.ForeColor = Text;
                lb.BorderStyle = BorderStyle.None;
                NativeDark(lb, "DarkMode_Explorer");
                break;
            case LinkLabel ll:
                ll.LinkColor = AccentLight;
                ll.ActiveLinkColor = Accent;
                ll.VisitedLinkColor = AccentLight;
                break;
            case Label l:
                if (IsLegacyColor(l.ForeColor)) l.ForeColor = Text;
                break;
            case TrackBar tr:
                tr.BackColor = c.Parent?.BackColor ?? Background;
                break;
            case ScrollableControl sc when sc.AutoScroll:
                NativeDark(sc, "DarkMode_Explorer");
                break;
        }
        foreach (Control child in c.Controls) Style(child);
        // Controles criados depois (listas que se preenchem, avisos) também recebem o tema.
        c.ControlAdded -= OnControlAdded;
        c.ControlAdded += OnControlAdded;
    }

    private static void OnControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control is { } ctl) Style(ctl);
    }

    private static bool IsLegacyColor(Color c) => c.ToArgb() == SystemColors.ControlText.ToArgb() || c.ToArgb() == Color.Black.ToArgb();

    /// <summary>Cores para menus da área de notificação.</summary>
    public sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Elevated;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }

    public sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = true; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Muted;
            base.OnRenderArrow(e);
        }
    }
}
