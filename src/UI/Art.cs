using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WarpSwitch;

/// The animated vortex behind the power button. Everything expensive (gradient sky, nebula glow, stars,
/// the spiral arms) is painted once per mood into bitmaps; a frame only rotates two bitmaps and moves
/// a few dozen particles, and nothing runs at all while the window is hidden.
sealed class VortexArt : IDisposable
{
    public enum Mood { Idle, Busy, On, Error }

    readonly Theme t;
    readonly float s;
    readonly Random rnd = new(1123);
    readonly Particle[] particles = new Particle[34];
    Size client;
    PointF c;
    float rOrb, rArms, aBack, aFront, time;
    Bitmap sky, back, front;
    bool dirty = true;

    public Mood Current { get; private set; } = Mood.Idle;
    public float Spin { get; private set; }

    struct Particle { public float R, Th, Speed, Size, Hue; }

    public VortexArt(Theme t, float scale)
    {
        this.t = t;
        s = scale;
    }

    public void Layout(Size size, PointF center, float orbRadius, float armRadius)
    {
        client = size;
        c = center;
        rOrb = orbRadius;
        rArms = armRadius;
        for (int i = 0; i < particles.Length; i++) particles[i] = Spawn(true);
        dirty = true;
    }

    public bool SetMood(Mood m)
    {
        if (m == Current) return false;
        Current = m;
        dirty = true;
        return true;
    }

    /// Degrees per second for the back arms, the front filaments.
    (float Back, float Front) Speeds => Current switch
    {
        Mood.Busy => (70f, 140f),
        Mood.On => (9f, 17f),
        Mood.Idle => (3f, 5f),
        _ => (0f, 0f),
    };

    /// <paramref name="full"/> = rotate the art too (off when animations are disabled: only the busy ring turns).
    public void Step(float dt, bool full)
    {
        time += dt;
        Spin = (Spin + dt * 320f) % 360f;
        if (!full) return;
        var (b, f) = Speeds;
        aBack = (aBack + b * dt) % 360f;
        aFront = (aFront + f * dt) % 360f;
        if (Current is not (Mood.On or Mood.Busy)) return;
        float pace = Current == Mood.Busy ? 2.2f : 1f;
        for (int i = 0; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            float u = (p.R - rOrb) / (rArms - rOrb);
            p.R -= p.Speed * s * dt * pace * (1f + (1f - u) * 1.6f);
            p.Th += dt * pace * (0.5f + 1.7f * (1f - u) * (1f - u));
            if (p.R < rOrb * 1.04f) p = Spawn(false);
        }
    }

    Particle Spawn(bool anywhere) => new()
    {
        R = anywhere ? rOrb + (float)rnd.NextDouble() * (rArms - rOrb) : rArms * (0.9f + 0.1f * (float)rnd.NextDouble()),
        Th = (float)(rnd.NextDouble() * Math.PI * 2),
        Speed = 9f + 16f * (float)rnd.NextDouble(),
        Size = (1.1f + 1.6f * (float)rnd.NextDouble()),
        Hue = (float)rnd.NextDouble(),
    };

    // ---------------------------------------------------------------- palette per mood

    (Color Inner, Color Outer, Color Glow, Color Spark) Palette => Current switch
    {
        Mood.On => (t.Amber, t.Accent2, t.Accent, Color.FromArgb(255, 255, 214, 170)),
        Mood.Busy => (t.Amber, t.Accent, t.Amber, Color.FromArgb(255, 255, 226, 180)),
        Mood.Error => (Draw.Mix(t.Bad, t.Idle, 0.4f), Draw.Mix(t.Idle, t.Bg, 0.3f), t.Bad, Color.FromArgb(255, 255, 190, 200)),
        _ => (t.Accent3, t.Idle, t.Idle, Color.FromArgb(255, 210, 200, 255)),
    };

    float Intensity => Current switch { Mood.On => 1f, Mood.Busy => 0.85f, Mood.Error => 0.45f, _ => 0.55f };

    void Rebuild()
    {
        dirty = false;
        sky?.Dispose(); back?.Dispose(); front?.Dispose();
        sky = PaintSky();
        var pal = Palette;
        float k = Intensity;
        back = PaintArms(3, 2.7f, 10f, 0.5f * k, 0.3f * k, pal.Inner, pal.Outer, 0.9f, 70);
        front = PaintArms(5, 3.7f, 1.7f, 0.8f * k, 0.22f * k, Draw.Mix(pal.Inner, Color.White, 0.25f), pal.Outer, 1.6f, 40);
    }

    Bitmap PaintSky()
    {
        var bmp = new Bitmap(Math.Max(1, client.Width), Math.Max(1, client.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var all = new Rectangle(Point.Empty, client);
        using (var sky = new LinearGradientBrush(all, t.BgTop, t.BgBottom, 90f)) g.FillRectangle(sky, all);

        var pal = Palette;
        float k = Intensity;
        // Nebula: a big glow behind the vortex plus two faint off-center clouds.
        Glow(g, c.X, c.Y, rArms * 1.35f, Draw.Alpha(pal.Glow, (int)(78 * k)));
        Glow(g, c.X, c.Y, rOrb * 1.9f, Draw.Alpha(pal.Glow, (int)(60 * k)));
        Glow(g, client.Width * 0.92f, client.Height * 0.06f, rArms * 0.9f, Draw.Alpha(t.Accent3, 34));
        Glow(g, client.Width * 0.05f, client.Height * 0.55f, rArms * 0.8f, Draw.Alpha(t.Accent2, 18));

        // Stars (same sky every time: fixed seed).
        var r = new Random(42);
        for (int i = 0; i < 90; i++)
        {
            float x = (float)r.NextDouble() * client.Width, y = (float)r.NextDouble() * client.Height;
            float d = (0.6f + (float)r.NextDouble() * 1.1f) * s;
            int a = 25 + r.Next(110);
            using var b = new SolidBrush(Color.FromArgb(a, r.Next(2) == 0 ? Color.White : Color.FromArgb(200, 190, 255)));
            g.FillEllipse(b, x, y, d, d);
        }
        return bmp;
    }

    static void Glow(Graphics g, float x, float y, float radius, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(x - radius, y - radius, 2 * radius, 2 * radius);
        using var b = new PathGradientBrush(path)
        {
            CenterColor = color,
            SurroundColors = new[] { Color.FromArgb(0, color) },
            FocusScales = new PointF(0.05f, 0.05f),
        };
        g.FillPath(b, path);
    }

    /// Spiral arms painted like brush strokes: hundreds of soft, translucent dabs along a logarithmic spiral,
    /// thick in the middle and thin at both ends, fading towards the rim, plus a sprinkle of sparkles.
    Bitmap PaintArms(int count, float sweep, float width, float core, float glow, Color inner, Color outer, float twist, int sparkles)
    {
        int d = (int)Math.Ceiling(rArms * 2) + 6;
        var bmp = new Bitmap(d, d, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = d / 2f, cy = d / 2f, r0 = rOrb * 0.92f, r1 = rArms;
        int steps = (int)(240 * s);
        var r = new Random(count * 31 + 7);
        var pts = new PointF[steps];
        for (int arm = 0; arm < count; arm++)
        {
            double th0 = arm * 2 * Math.PI / count + r.NextDouble() * 0.25;
            float length = 0;
            for (int i = 0; i < steps; i++)
            {
                float u = i / (float)steps;
                float rad = r0 * (float)Math.Pow(r1 / r0, u);
                double th = th0 + sweep * Math.Pow(u, 1f / twist);
                pts[i] = new PointF(cx + (float)Math.Cos(th) * rad, cy + (float)Math.Sin(th) * rad);
                if (i > 0) length += Distance(pts[i], pts[i - 1]);
            }
            float spacing = length / steps;
            for (int i = 0; i < steps; i++)
            {
                float u = i / (float)steps;
                float taper = (float)Math.Pow(Math.Sin(Math.PI * Math.Min(1.0, u * 1.08)), 0.75);
                float fade = (float)Math.Pow(1 - u, 1.25) * Math.Min(1f, u * 7f);
                float w = Math.Max(0.6f, width * s * taper);
                var col = Draw.Mix(inner, outer, (float)Math.Pow(u, 0.8));
                // Overlapping dabs build up: pick each dab's alpha so the stroke ends up at the target opacity.
                Dab(g, pts[i], w * 2.4f, glow * fade, spacing, col);
                Dab(g, pts[i], w / 2, core * fade, spacing, col);
            }
        }
        var spark = Palette.Spark;
        for (int i = 0; i < sparkles; i++)
        {
            float u = (float)Math.Pow(r.NextDouble(), 0.8);
            int arm = r.Next(count);
            double th = arm * 2 * Math.PI / count + sweep * Math.Pow(u, 1f / twist) + (r.NextDouble() - 0.5) * 0.35;
            float rad = r0 * (float)Math.Pow(r1 / r0, u) + (float)(r.NextDouble() - 0.5) * 10 * s;
            float x = cx + (float)Math.Cos(th) * rad, y = cy + (float)Math.Sin(th) * rad;
            float dd = (0.7f + (float)r.NextDouble() * 1.3f) * s;
            int a = (int)((60 + r.Next(170)) * Math.Min(1f, core * 1.25f) * (1 - u * 0.8f));
            using var b = new SolidBrush(Color.FromArgb(Math.Min(255, a), spark));
            g.FillEllipse(b, x - dd / 2, y - dd / 2, dd, dd);
        }
        return bmp;
    }

    static float Distance(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static void Dab(Graphics g, PointF p, float radius, float opacity, float spacing, Color col)
    {
        if (opacity <= 0.005f) return;
        float overlaps = Math.Max(1f, 2 * radius / Math.Max(0.1f, spacing));
        int a = (int)Math.Round(255 * (1 - Math.Pow(1 - Math.Min(0.95f, opacity), 1 / overlaps)));
        if (a <= 0) return;
        using var b = new SolidBrush(Color.FromArgb(a, col));
        g.FillEllipse(b, p.X - radius, p.Y - radius, 2 * radius, 2 * radius);
    }

    // ---------------------------------------------------------------- painting

    /// Unscaled copy of the static sky for the area being repainted.
    public void PaintSky(Graphics g, Rectangle clip)
    {
        if (dirty) Rebuild();
        var r = Rectangle.Intersect(clip, new Rectangle(Point.Empty, client));
        if (r.Width <= 0 || r.Height <= 0) return;
        var mode = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(sky, r, r, GraphicsUnit.Pixel);
        g.CompositingMode = mode;
    }

    public void PaintVortex(Graphics g)
    {
        if (dirty) Rebuild();
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        Rotated(g, back, aBack);
        Rotated(g, front, aFront);
        g.Restore(state);

        if (Current is Mood.On or Mood.Busy)
        {
            var pal = Palette;
            foreach (var p in particles)
            {
                float u = (p.R - rOrb) / (rArms - rOrb);
                float a = Math.Min(1f, (1f - u) * 3.5f) * Math.Min(1f, u * 6f);
                if (a <= 0.02f) continue;
                float x = c.X + (float)Math.Cos(p.Th) * p.R, y = c.Y + (float)Math.Sin(p.Th) * p.R;
                float d = p.Size * s * (0.7f + 0.6f * (1f - u));
                var col = Draw.Mix(pal.Spark, pal.Outer, p.Hue * u);
                using var b = new SolidBrush(Color.FromArgb((int)(220 * a), col));
                g.FillEllipse(b, x - d / 2, y - d / 2, d, d);
            }
        }
    }

    void Rotated(Graphics g, Bitmap bmp, float angle)
    {
        g.TranslateTransform(c.X, c.Y);
        g.RotateTransform(angle);
        g.DrawImage(bmp, -bmp.Width / 2f, -bmp.Height / 2f, bmp.Width, bmp.Height);
        g.ResetTransform();
    }

    /// The power button: dark glass when off (with a comet ring while busy), a glowing gradient orb when on.
    public void PaintOrb(Graphics g, bool hover)
    {
        float r = rOrb, cx = c.X, cy = c.Y;
        var body = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
        if (Current == Mood.On)
        {
            float breathe = (float)(Math.Sin(time * 1.9) * 0.5 + 0.5);
            for (int i = 3; i >= 1; i--)
            {
                float gr = r + s * (6 * i + 3 * breathe);
                using var halo = new SolidBrush(Color.FromArgb(i == 1 ? 60 : i == 2 ? 34 : 18, t.Accent2));
                g.FillEllipse(halo, cx - gr, cy - gr, 2 * gr, 2 * gr);
            }
            using (var fill = new LinearGradientBrush(body, hover ? Draw.Mix(t.Amber, Color.White, 0.2f) : t.Amber, t.Accent2, 55f))
            {
                fill.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { hover ? Draw.Mix(t.Amber, Color.White, 0.2f) : t.Amber, t.Accent, t.Accent2, Draw.Mix(t.Accent2, t.Accent3, 0.55f) },
                    Positions = new[] { 0f, 0.38f, 0.75f, 1f },
                };
                g.FillEllipse(fill, body);
            }
            Gloss(g, body, 75);
            using (var rim = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, s))) g.DrawEllipse(rim, body);
            Draw.PowerGlyph(g, cx, cy, r * 0.36f, Color.White, 5.5f * s);
            return;
        }

        using (var path = new GraphicsPath())
        {
            path.AddEllipse(body);
            using var fill = new PathGradientBrush(path)
            {
                CenterColor = hover ? Color.FromArgb(255, 0x2E, 0x26, 0x56) : Color.FromArgb(255, 0x24, 0x1D, 0x45),
                SurroundColors = new[] { Color.FromArgb(255, 0x11, 0x0E, 0x20) },
                CenterPoint = new PointF(cx - r * 0.25f, cy - r * 0.3f),
            };
            g.FillPath(fill, path);
        }
        Gloss(g, body, 26);
        Color ringA = Current == Mood.Error ? t.Bad : Current == Mood.Busy ? Draw.Mix(t.Amber, t.Idle, 0.6f) : t.Accent3;
        Color ringB = Current == Mood.Error ? Draw.Mix(t.Bad, t.Idle, 0.6f) : t.Idle;
        using (var ringBrush = new LinearGradientBrush(body, Draw.Alpha(ringA, hover ? 255 : 210), Draw.Alpha(ringB, hover ? 220 : 150), 45f))
        using (var ring = new Pen(ringBrush, 2.2f * s))
            g.DrawEllipse(ring, body);

        if (Current == Mood.Busy)
        {
            var arc = RectangleF.Inflate(body, 5 * s, 5 * s);
            const int tail = 26;
            for (int k = tail - 1; k >= 0; k--)
            {
                float f = 1f - k / (float)tail;
                using var pen = new Pen(Color.FromArgb((int)(255 * f * f), Draw.Mix(t.Accent, t.Amber, f)), (1.5f + 2.5f * f) * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(pen, arc, Spin - k * 5.5f, 6.5f);
            }
        }

        Color glyph = Current switch
        {
            Mood.Busy => t.Amber,
            Mood.Error => t.Bad,
            _ => hover ? t.Text : t.Sub,
        };
        Draw.PowerGlyph(g, cx, cy, r * 0.36f, glyph, 5f * s);
    }

    /// Soft highlight across the top of a sphere.
    static void Gloss(Graphics g, RectangleF body, int alpha)
    {
        var hl = new RectangleF(body.X + body.Width * 0.16f, body.Y + body.Height * 0.06f, body.Width * 0.68f, body.Height * 0.46f);
        using var path = new GraphicsPath();
        path.AddEllipse(hl);
        using var b = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(alpha, 255, 255, 255),
            SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) },
            CenterPoint = new PointF(hl.X + hl.Width / 2, hl.Y + hl.Height * 0.3f),
        };
        g.FillPath(b, path);
    }

    public void Dispose()
    {
        sky?.Dispose(); back?.Dispose(); front?.Dispose();
    }
}
