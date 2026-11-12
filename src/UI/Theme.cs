using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace WarpSwitch;

/// "Vortex night": one deliberate dark look (the app has its own identity rather than following the
/// Windows light/dark setting). Opaque colors are for real controls (menus, the log window);
/// the glass colors are for painting over the animated background.
sealed class Theme
{
    public readonly bool Dark = true;
    public Color Bg, BgTop, BgBottom, Card, Border, Text, Sub, Faint, Ring, Track, Hover;
    public Color GlassFill, GlassEdge, GlassShine;
    public Color Accent, Accent2, Accent3, Amber, Idle, Good, Bad;

    static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    public static Theme Current() => new()
    {
        Bg = Hex(0x0C0A16), BgTop = Hex(0x161129), BgBottom = Hex(0x07060C),
        Card = Hex(0x17142A), Border = Hex(0x2A2545), Text = Hex(0xF3F0FF), Sub = Hex(0xA9A3C9), Faint = Hex(0x6F6994),
        Ring = Hex(0x3A3366), Track = Hex(0x3A355C), Hover = Hex(0x231E3D),
        GlassFill = Color.FromArgb(16, 255, 255, 255), GlassEdge = Color.FromArgb(30, 255, 255, 255), GlassShine = Color.FromArgb(40, 255, 255, 255),
        Accent = Hex(0xFF8A1F), Accent2 = Hex(0xFF3D7F), Accent3 = Hex(0x8B5CF6), Amber = Hex(0xFFB347),
        Idle = Hex(0x5B4FB0), Good = Hex(0x3DDC97), Bad = Hex(0xFF6B81),
    };
}

/// Point sizes; GDI scales them with the display DPI by itself.
sealed class Fonts : IDisposable
{
    public readonly Font Brand = Pick(11f, FontStyle.Regular, "Bahnschrift SemiBold", "Segoe UI Semibold");
    public readonly Font BrandThin = Pick(11f, FontStyle.Regular, "Bahnschrift Light", "Segoe UI Light");
    public readonly Font Title = Pick(19f, FontStyle.Regular, "Bahnschrift SemiBold", "Segoe UI Semibold");
    public readonly Font Body = new("Segoe UI", 9.75f);
    public readonly Font Small = new("Segoe UI", 8.75f);
    public readonly Font Label = Pick(7.5f, FontStyle.Regular, "Bahnschrift SemiBold", "Segoe UI Semibold");
    public readonly Font Value = new("Segoe UI Semibold", 10f);
    public readonly Font CardTitle = new("Segoe UI Semibold", 10.5f);
    public readonly Font Signature = Pick(15f, FontStyle.Regular, "Segoe Script", "Gabriola", "Segoe UI Italic");
    public readonly Font Tiny = Pick(7.5f, FontStyle.Regular, "Bahnschrift Light", "Segoe UI");

    static Font Pick(float size, FontStyle style, params string[] families)
    {
        foreach (var name in families)
        {
            var f = new Font(name, size, style);
            if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        return new Font("Segoe UI", size, style);
    }

    public void Dispose()
    {
        foreach (var f in new[] { Brand, BrandThin, Title, Body, Small, Label, Value, CardTitle, Signature, Tiny }) f.Dispose();
    }
}

static class Draw
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// Frosted-glass card: faint fill, hairline edge, and a soft highlight along the top.
    public static void Glass(Graphics g, Rectangle r, float radius, Theme t, Color tint = default)
    {
        using var path = Round(r, radius);
        using (var fill = new LinearGradientBrush(r, t.GlassFill, Color.FromArgb(6, 255, 255, 255), 90f)) g.FillPath(fill, path);
        if (tint.A > 0)
            using (var wash = new LinearGradientBrush(r, tint, Color.FromArgb(0, tint), 0f)) g.FillPath(wash, path);
        using (var edge = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), t.GlassShine, Color.FromArgb(10, 255, 255, 255), 90f))
        using (var pen = new Pen(edge, 1f))
            g.DrawPath(pen, path);
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0f, Math.Min(1f, t));
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static Color Alpha(Color c, int a) => Color.FromArgb(Math.Max(0, Math.Min(255, a)), c);

    public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags = TextFormatFlags.Default) =>
        TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

    /// Text filled with a gradient (headline / signature). Centered in <paramref name="r"/> unless left-aligned.
    public static void GradientText(Graphics g, string s, Font f, RectangleF r, Color a, Color b, bool center = true, float angle = 0f)
    {
        if (string.IsNullOrEmpty(s)) return;
        using var path = new GraphicsPath();
        float em = g.DpiY * f.SizeInPoints / 72f;
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.Alignment = center ? StringAlignment.Center : StringAlignment.Near;
        fmt.LineAlignment = StringAlignment.Center;
        fmt.FormatFlags |= StringFormatFlags.NoWrap;
        path.AddString(s, f.FontFamily, (int)f.Style, em, r, fmt);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0) return;
        bounds.Inflate(2, 2);
        using var brush = new LinearGradientBrush(bounds, a, b, angle);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
        g.SmoothingMode = old;
    }

    public static void PowerGlyph(Graphics g, float cx, float cy, float r, Color c, float width)
    {
        using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, cx - r, cy - r + r * 0.12f, 2 * r, 2 * r, -50, 280);
        g.DrawLine(pen, cx, cy - r - r * 0.22f, cx, cy - r * 0.08f);
    }

    /// The Vortex mark: three arms curling into the middle.
    public static void Swirl(Graphics g, float cx, float cy, float radius, Color c, float width, float turn = 0f)
    {
        using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        for (int arm = 0; arm < 3; arm++)
        {
            var pts = new PointF[10];
            for (int i = 0; i < pts.Length; i++)
            {
                float t = i / (float)(pts.Length - 1);
                float rr = radius * (0.22f + 0.78f * t);
                double th = turn * Math.PI / 180 + arm * 2 * Math.PI / 3 + 2.3 * t;
                pts[i] = new PointF(cx + (float)(Math.Cos(th) * rr), cy + (float)(Math.Sin(th) * rr));
            }
            g.DrawCurve(pen, pts, 0.5f);
        }
    }
}

static class Icons
{
    /// Tray icon: dim swirl = off, amber swirl = connecting, gradient disc + white swirl = connected.
    public static Icon Make(int size, Color fill, Color fill2, Color swirl)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float inset = size * 0.04f;
            var r = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
            if (fill.A > 0)
                using (var b = new LinearGradientBrush(r, fill, fill2.A > 0 ? fill2 : fill, 45f))
                    g.FillEllipse(b, r);
            float rad = size * (fill.A > 0 ? 0.33f : 0.42f);
            Draw.Swirl(g, size / 2f, size / 2f, rad, swirl, Math.Max(1.3f, size * (fill.A > 0 ? 0.09f : 0.11f)));
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { Native.DestroyIcon(h); }
    }
}

/// Context menus that match the window.
sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    readonly Theme t;

    public MenuRenderer(Theme t) : base(new Palette(t))
    {
        this.t = t;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? t.Text : t.Faint;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        float s = r.Height;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(t.Accent, Math.Max(1.6f, s * 0.13f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(r.X + s * 0.2f, r.Y + s * 0.52f),
            new PointF(r.X + s * 0.42f, r.Y + s * 0.74f),
            new PointF(r.X + s * 0.82f, r.Y + s * 0.28f),
        });
    }

    sealed class Palette : ProfessionalColorTable
    {
        readonly Theme t;
        public Palette(Theme t) { this.t = t; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => t.Card;
        public override Color ImageMarginGradientBegin => t.Card;
        public override Color ImageMarginGradientMiddle => t.Card;
        public override Color ImageMarginGradientEnd => t.Card;
        public override Color MenuBorder => t.Border;
        public override Color MenuItemBorder => t.Hover;
        public override Color MenuItemSelected => t.Hover;
        public override Color MenuItemSelectedGradientBegin => t.Hover;
        public override Color MenuItemSelectedGradientEnd => t.Hover;
        public override Color SeparatorDark => t.Border;
        public override Color SeparatorLight => t.Card;
        public override Color CheckBackground => t.Card;
        public override Color CheckSelectedBackground => t.Hover;
        public override Color CheckPressedBackground => t.Hover;
    }
}
