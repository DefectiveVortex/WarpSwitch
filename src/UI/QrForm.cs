using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WarpSwitch;

/// "Scan to join the hotspot": a phone camera reads the standard WIFI: code and joins without typing.
sealed class QrForm : Form
{
    readonly QrCode code;
    readonly string ssid;
    readonly Theme t;
    readonly Fonts fonts = new();
    readonly float s;

    public QrForm(string ssid, string passphrase, Theme theme, Icon icon)
    {
        this.ssid = ssid;
        t = theme;
        code = QrCode.Encode(Hotspot.JoinCode(ssid, passphrase));
        s = DeviceDpi / 96f;
        Text = "Join " + ssid;
        Icon = icon;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        BackColor = t.Bg;
        ClientSize = new Size(P(300), P(372));
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    int P(float v) => (int)Math.Round(v * s);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleTitleBar(Handle, t.Dark, t.Bg);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new LinearGradientBrush(ClientRectangle, t.BgTop, t.BgBottom, 90f)) g.FillRectangle(bg, ClientRectangle);

        // White card with the quiet zone scanners need around the code.
        var card = new Rectangle(P(30), P(24), P(240), P(240));
        using (var path = Draw.Round(card, P(18)))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, path);
        int quiet = P(16);
        float module = (card.Width - 2f * quiet) / code.Size;
        g.SmoothingMode = SmoothingMode.None;
        using (var ink = new SolidBrush(Color.FromArgb(255, 12, 10, 22)))
            for (int y = 0; y < code.Size; y++)
                for (int x = 0; x < code.Size; x++)
                    if (code[x, y])
                        g.FillRectangle(ink, card.X + quiet + x * module, card.Y + quiet + y * module, module + 0.5f, module + 0.5f);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Draw.Text(g, ssid, fonts.CardTitle, new Rectangle(P(16), P(276), ClientSize.Width - P(32), P(24)), t.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        Draw.Text(g, "Point a phone's camera at the code to join this hotspot without typing the password.", fonts.Small,
            new Rectangle(P(24), P(304), ClientSize.Width - P(48), P(44)), t.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) fonts.Dispose();
        base.Dispose(disposing);
    }
}
