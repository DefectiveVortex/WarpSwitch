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
    public Color Surface, SurfaceHover, Chip, Line;
    public Color GlassFill, GlassEdge, GlassShine;
    public Color Accent, Accent2, Accent3, Amber, Idle, Good, Bad;

    static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    public static Theme Current() => new()
    {
        Bg = Hex(0x0C0A16), BgTop = Hex(0x161129), BgBottom = Hex(0x07060C),
        Card = Hex(0x17142A), Border = Hex(0x2A2545), Text = Hex(0xF3F0FF), Sub = Hex(0xA9A3C9), Faint = Hex(0x6F6994),
        Ring = Hex(0x3A3366), Track = Hex(0x332E52), Hover = Hex(0x231E3D),
        // The settings side: soft raised cards on the night background, no hard outlines.
        Surface = Hex(0x15122A), SurfaceHover = Hex(0x1D1937), Chip = Hex(0x221D3D), Line = Color.FromArgb(14, 255, 255, 255),
        GlassFill = Color.FromArgb(16, 255, 255, 255), GlassEdge = Color.FromArgb(30, 255, 255, 255), GlassShine = Color.FromArgb(40, 255, 255, 255),
        Accent = Hex(0xFF8A1F), Accent2 = Hex(0xFF3D7F), Accent3 = Hex(0x8B5CF6), Amber = Hex(0xFFB347),
        Idle = Hex(0x5B4FB0), Good = Hex(0x3DDC97), Bad = Hex(0xFF6B81),
    };
}

/// Fonts in pixels at the window's own scale, so a window that's shrunk to fit a small screen shrinks its text
/// with it. Sizes are given in points at 96 dpi (pt × 4/3 = px).
sealed class Fonts : IDisposable
{
    public readonly Font Brand, BrandThin, Title, Body, Small, Label, Value, CardTitle, Signature, Tiny, Button;

    public Fonts(float scale = 1f)
    {
        Font Px(float pt, FontStyle style, params string[] families) => Pick(pt * 4f / 3f * scale, style, families);
        Brand = Px(11f, FontStyle.Regular, "Bahnschrift SemiBold", "Segoe UI Semibold");
        BrandThin = Px(11f, FontStyle.Regular, "Bahnschrift Light", "Segoe UI Light");
        Title = Px(20f, FontStyle.Regular, "Bahnschrift SemiBold", "Segoe UI Semibold");
        Body = Px(9.75f, FontStyle.Regular, "Segoe UI");
        Small = Px(8.75f, FontStyle.Regular, "Segoe UI");
        Label = Px(7.5f, FontStyle.Regular, "Bahnschrift SemiBold", "Segoe UI Semibold");
        Value = Px(10.5f, FontStyle.Regular, "Segoe UI Semibold");
        CardTitle = Px(10.5f, FontStyle.Regular, "Segoe UI Semibold");
        Signature = Px(13f, FontStyle.Regular, "Segoe Script", "Gabriola", "Segoe UI Italic");
        Tiny = Px(7.5f, FontStyle.Regular, "Bahnschrift Light", "Segoe UI");
        Button = Px(11f, FontStyle.Regular, "Segoe UI Semibold");
    }

    static Font Pick(float px, FontStyle style, params string[] families)
    {
        foreach (var name in families)
        {
            var f = new Font(name, px, style, GraphicsUnit.Pixel);
            if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        return new Font("Segoe UI", px, style, GraphicsUnit.Pixel);
    }

    public void Dispose()
    {
        foreach (var f in new[] { Brand, BrandThin, Title, Body, Small, Label, Value, CardTitle, Signature, Tiny, Button }) f.Dispose();
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

    /// Soft raised card: an opaque fill a shade above the background and a barely-there top edge.
    public static void Card(Graphics g, Rectangle r, float radius, Theme t, Color? fill = null)
    {
        using var path = Round(r, radius);
        using (var b = new SolidBrush(fill ?? t.Surface)) g.FillPath(b, path);
        using var edge = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(22, 255, 255, 255), Color.FromArgb(4, 255, 255, 255), 90f);
        using var pen = new Pen(edge, 1f);
        g.DrawPath(pen, path);
    }

    /// The on/off switch. <paramref name="t"/> runs 0 (off) → 1 (on) while it animates; the knob slides with a
    /// little stretch mid-way and the track fades into the sunset gradient.
    public static void Switch(Graphics g, Rectangle r, float t, bool enabled, bool hot, Theme th, float scale)
    {
        t = Math.Max(0f, Math.Min(1f, t));
        using (var path = Round(r, r.Height / 2f))
        {
            Color off = enabled ? (hot ? Mix(th.Track, Color.White, 0.08f) : th.Track) : Mix(th.Track, th.Bg, 0.5f);
            using (var b = new SolidBrush(off)) g.FillPath(b, path);
            if (t > 0.01f)
            {
                int a = (int)(255 * t * (enabled ? 1f : 0.45f));
                using var on = new LinearGradientBrush(r, Alpha(th.Accent, a), Alpha(th.Accent2, a), 0f);
                g.FillPath(on, path);
            }
        }
        float pad = 3 * scale, d = r.Height - 2 * pad;
        float stretch = (float)Math.Sin(Math.PI * t) * d * 0.28f;
        float x = r.X + pad + t * (r.Width - d - 2 * pad) - stretch * t;
        var knob = new RectangleF(x, r.Y + pad, d + stretch, d);
        using (var shadow = Round(new RectangleF(knob.X, knob.Y + scale, knob.Width, knob.Height), d / 2))
        using (var sb = new SolidBrush(Color.FromArgb(enabled ? 60 : 25, 0, 0, 0)))
            g.FillPath(sb, shadow);
        using var kp = Round(knob, d / 2);
        using var kb = new SolidBrush(enabled ? Color.White : Mix(Color.White, th.Track, 0.55f));
        g.FillPath(kb, kp);
    }

    /// Rounded pill button: the sunset gradient for the main action, a soft tint otherwise.
    public static void Pill(Graphics g, Rectangle r, string text, Font f, Theme t, bool primary, bool hot, bool enabled, float radius = -1)
    {
        using var path = Round(r, radius < 0 ? r.Height / 2f : radius);
        if (primary && enabled)
        {
            using var b = new LinearGradientBrush(r, hot ? Mix(t.Accent, Color.White, 0.12f) : t.Accent, hot ? Mix(t.Accent2, Color.White, 0.12f) : t.Accent2, 0f);
            g.FillPath(b, path);
        }
        else
        {
            using var b = new SolidBrush(Color.FromArgb(!enabled ? 10 : hot ? 34 : 22, 255, 255, 255));
            g.FillPath(b, path);
        }
        Text(g, text, f, r, primary && enabled ? Color.White : enabled ? t.Text : t.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    public static void Wifi(Graphics g, RectangleF box, Color c, float scale, float weight = 1.8f)
    {
        float cx = box.X + box.Width / 2, cy = box.Y + box.Height * 0.78f;
        using var pen = new Pen(c, weight * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (float f in new[] { 0.3f, 0.58f })
        {
            float rad = box.Height * f;
            g.DrawArc(pen, cx - rad, cy - rad, 2 * rad, 2 * rad, 225, 90);
        }
        float d = weight * 1.9f * scale;
        using var dot = new SolidBrush(c);
        g.FillEllipse(dot, cx - d / 2, cy - d / 2, d, d);
    }

    public static void Cross(Graphics g, RectangleF box, Color c, float width)
    {
        using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, box.Left, box.Top, box.Right, box.Bottom);
        g.DrawLine(pen, box.Left, box.Bottom, box.Right, box.Top);
    }

    public static void Plus(Graphics g, float cx, float cy, float half, Color c, float width)
    {
        using var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - half, cy, cx + half, cy);
        g.DrawLine(pen, cx, cy - half, cx, cy + half);
    }

    /// Five-point star, filled or outlined ("auto-connect here").
    public static void Star(Graphics g, float cx, float cy, float r, Color c, bool filled, float width)
    {
        var pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / 5;
            float rr = i % 2 == 0 ? r : r * 0.45f;
            pts[i] = new PointF(cx + (float)Math.Cos(a) * rr, cy + (float)Math.Sin(a) * rr);
        }
        if (filled) using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        using var pen = new Pen(c, width) { LineJoin = LineJoin.Round };
        g.DrawPolygon(pen, pts);
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
        float em = f.Unit == GraphicsUnit.Pixel ? f.Size : g.DpiY * f.SizeInPoints / 72f;
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.Alignment = center ? StringAlignment.Center : StringAlignment.Near;
        fmt.LineAlignment = StringAlignment.Center;
        fmt.FormatFlags |= StringFormatFlags.NoWrap;
        // GenericTypographic lays out only whole lines; a script font's line is taller than its box, so it drew nothing.
        fmt.FormatFlags &= ~StringFormatFlags.LineLimit;
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

    /// Rounded highlight inset from the edges instead of a hard full-width bar.
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Draw.Round(r, 6);
        using var b = new SolidBrush(t.Hover);
        e.Graphics.FillPath(b, path);
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
