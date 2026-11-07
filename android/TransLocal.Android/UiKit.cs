using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace TransLocal.Mobile;

/// <summary>Visual escuro do app (o mesmo do TransLocal no Windows): cartões arredondados, pílulas, laranja e roxo.</summary>
internal static class Ui
{
    public static readonly Color Background = Color.Rgb(13, 13, 15);
    public static readonly Color Surface = Color.Rgb(24, 24, 27);
    public static readonly Color Elevated = Color.Rgb(36, 36, 41);
    public static readonly Color Border = Color.Rgb(44, 44, 50);
    public static readonly Color Text = Color.Rgb(242, 242, 244);
    public static readonly Color Muted = Color.Rgb(150, 150, 160);
    public static readonly Color Accent = Color.Rgb(255, 106, 26);
    public static readonly Color AccentLight = Color.Rgb(255, 146, 72);
    public static readonly Color Accent2 = Color.Rgb(124, 92, 255);
    public static readonly Color Good = Color.Rgb(34, 197, 94);
    public static readonly Color Bad = Color.Rgb(244, 63, 94);

    public static int Dp(Context c, float v) => (int)(v * c.Resources!.DisplayMetrics!.Density + 0.5f);

    public static GradientDrawable Rounded(Context c, Color fill, float radiusDp, Color? stroke = null)
    {
        var d = new GradientDrawable();
        d.SetShape(ShapeType.Rectangle);
        d.SetCornerRadius(Dp(c, radiusDp));
        d.SetColor(fill);
        if (stroke is { } s) d.SetStroke(Dp(c, 1), s);
        return d;
    }

    public static GradientDrawable Gradient(Context c, float radiusDp, params Color[] colors)
    {
        var d = new GradientDrawable(GradientDrawable.Orientation.LeftRight!, colors.Select(x => (int)x.ToArgb()).ToArray());
        d.SetCornerRadius(Dp(c, radiusDp));
        return d;
    }

    public static TextView Label(Context c, string text, float sp, Color color, bool bold = false, bool light = false)
    {
        var t = new TextView(c) { Text = text };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(color);
        if (bold) t.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        else if (light) t.SetTypeface(Typeface.Create("sans-serif-light", TypefaceStyle.Normal), TypefaceStyle.Normal);
        t.SetLineSpacing(Dp(c, 2), 1f);
        return t;
    }

    /// <summary>Cartão: fundo um tom acima, cantos de 22 dp e borda fina.</summary>
    public static LinearLayout Card(Context c, bool highlight = false)
    {
        var card = new LinearLayout(c) { Orientation = Orientation.Vertical };
        card.Background = highlight
            ? Rounded(c, Color.Rgb(34, 24, 20), 22, Color.Argb(170, 255, 106, 26))
            : Rounded(c, Surface, 22, Border);
        card.SetPadding(Dp(c, 18), Dp(c, 16), Dp(c, 18), Dp(c, 18));
        card.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(c, 14),
        };
        return card;
    }

    /// <summary>Botão em pílula: principal em degradê laranja, comum escuro com borda.</summary>
    public static Button Pill(Context c, string text, Action onClick, bool primary = false, bool fill = true)
    {
        var b = new Button(c) { Text = text };
        b.SetAllCaps(false);
        b.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
        b.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        b.SetTextColor(primary ? Color.White : Text);
        b.Background = primary ? Gradient(c, 999, Accent, AccentLight) : Rounded(c, Elevated, 999, Border);
        b.StateListAnimator = null;
        b.SetPadding(Dp(c, 20), Dp(c, 12), Dp(c, 20), Dp(c, 12));
        b.Click += (_, _) => onClick();
        b.LayoutParameters = new LinearLayout.LayoutParams(fill ? ViewGroup.LayoutParams.MatchParent : ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(c, 10) };
        return b;
    }

    /// <summary>Selo de estado (bolinha colorida + texto) numa pílula escura.</summary>
    public static LinearLayout Chip(Context c, out View dot, out TextView text)
    {
        var chip = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        chip.SetGravity(GravityFlags.CenterVertical);
        chip.Background = Rounded(c, Color.Rgb(28, 28, 32), 999, Border);
        chip.SetPadding(Dp(c, 12), Dp(c, 7), Dp(c, 14), Dp(c, 7));
        dot = new View(c) { Background = Oval(Muted) };
        chip.AddView(dot, new LinearLayout.LayoutParams(Dp(c, 8), Dp(c, 8)) { RightMargin = Dp(c, 8) });
        text = Label(c, "", 12.5f, Text, bold: true);
        text.SetSingleLine(true);
        text.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        chip.AddView(text);
        return chip;
    }

    public static GradientDrawable Oval(Color color)
    {
        var d = new GradientDrawable();
        d.SetShape(ShapeType.Oval);
        d.SetColor(color);
        return d;
    }

    /// <summary>Quadradinho de informação (título pequeno + valor), como os blocos inferiores do painel.</summary>
    public static LinearLayout Tile(Context c, string title, out TextView value, Color accent)
    {
        var tile = new LinearLayout(c) { Orientation = Orientation.Vertical };
        tile.Background = Rounded(c, Color.Rgb(30, 30, 34), 16, Border);
        tile.SetPadding(Dp(c, 12), Dp(c, 10), Dp(c, 12), Dp(c, 12));
        var head = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        head.AddView(new View(c) { Background = Oval(accent) }, new LinearLayout.LayoutParams(Dp(c, 7), Dp(c, 7)) { RightMargin = Dp(c, 6) });
        head.AddView(Label(c, title, 11.5f, Muted));
        tile.AddView(head);
        value = Label(c, "—", 14.5f, Text, bold: true);
        value.SetPadding(0, Dp(c, 6), 0, 0);
        tile.AddView(value);
        var bar = new View(c) { Background = Rounded(c, accent, 999) };
        tile.AddView(bar, new LinearLayout.LayoutParams(Dp(c, 34), Dp(c, 3)) { TopMargin = Dp(c, 8) });
        return tile;
    }
}

/// <summary>
/// Anel do código de autorização: arco em degradê laranja → roxo que esvazia conforme o código perto de mudar,
/// com o código grande no centro (como o anel de porcentagem do painel de referência).
/// </summary>
internal sealed class CodeRing : View
{
    private readonly Paint _track = new(PaintFlags.AntiAlias);
    private readonly Paint _arc = new(PaintFlags.AntiAlias);
    private readonly Paint _code = new(PaintFlags.AntiAlias);
    private readonly Paint _sub = new(PaintFlags.AntiAlias);

    public string Code { get; set; } = "— — —";
    public string Caption { get; set; } = "";
    public float Fraction { get; set; }

    public CodeRing(Context c) : base(c)
    {
        float stroke = Ui.Dp(c, 12);
        _track.SetStyle(Paint.Style.Stroke);
        _track.StrokeWidth = stroke;
        _track.Color = Color.Rgb(38, 38, 44);
        _track.StrokeCap = Paint.Cap.Round;
        _arc.SetStyle(Paint.Style.Stroke);
        _arc.StrokeWidth = stroke;
        _arc.StrokeCap = Paint.Cap.Round;
        _code.Color = Ui.Text;
        _code.TextAlign = Paint.Align.Center;
        _code.TextSize = Ui.Dp(c, 34);
        _code.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        _code.LetterSpacing = 0.06f;
        _sub.Color = Ui.Muted;
        _sub.TextAlign = Paint.Align.Center;
        _sub.TextSize = Ui.Dp(c, 13);
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        int size = Math.Min(w, Ui.Dp(Context!, 230));
        SetMeasuredDimension(w, size);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float size = Math.Min(Width, Height);
        float pad = _track.StrokeWidth / 2 + Ui.Dp(Context!, 4);
        float cx = Width / 2f, cy = Height / 2f;
        var r = new RectF(cx - size / 2 + pad, cy - size / 2 + pad, cx + size / 2 - pad, cy + size / 2 - pad);
        canvas.DrawArc(r, 0, 360, false, _track);
        _arc.SetShader(new SweepGradient(cx, cy, new int[] { Ui.Accent2.ToArgb(), Ui.Accent.ToArgb(), Ui.AccentLight.ToArgb(), Ui.Accent2.ToArgb() }, null));
        float sweep = Math.Clamp(Fraction, 0.02f, 1f) * 360f;
        canvas.Save();
        canvas.Rotate(-90, cx, cy);
        canvas.DrawArc(r, 0, sweep, false, _arc);
        canvas.Restore();
        canvas.DrawText(Code, cx, cy + _code.TextSize * 0.32f, _code);
        if (Caption.Length > 0) canvas.DrawText(Caption, cx, cy + _code.TextSize * 0.32f + Ui.Dp(Context!, 26), _sub);
    }
}
