using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Xunit;

namespace WarpSwitch.Tests;

/// Paints the real windows in made-up states. Always checks they render; with WARPSWITCH_RENDER_DIR set it also
/// saves PNGs (that's how the README screenshots are made).
public class RenderTests
{
    static readonly string OutDir = Environment.GetEnvironmentVariable("WARPSWITCH_RENDER_DIR");

    static void OnSta(Action a)
    {
        Exception error = null;
        var t = new Thread(() => { try { a(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception("render failed", error);
    }

    static void Save(Bitmap bmp, string name)
    {
        Assert.True(bmp.Width > 300 && bmp.Height > 500);
        if (string.IsNullOrEmpty(OutDir)) return;
        Directory.CreateDirectory(OutDir);
        bmp.Save(Path.Combine(OutDir, name + ".png"), ImageFormat.Png);
    }

    static void Main(string name, Action<Tunnel, Hotspot, Updater> arrange, (long, long) usage = default) => OnSta(() =>
    {
        using var form = new MainForm(startHidden: true, preview: true);
        using var bmp = form.RenderPreview(arrange, usage);
        Save(bmp, name);
    });

    static readonly TraceInfo Trace = new() { Ip = "104.28.222.171", Colo = "DEL", Loc = "IN", Warp = true };

    [Fact]
    public void Off() => Main("off", (t, h, u) => h.Preview(false, false, 0, "VORTEX-PC 4821", "k7m2p9xq4r"));

    [Fact]
    public void Connected() => Main("connected", (t, h, u) =>
    {
        t.Preview(TunnelState.Connected, trace: Trace, route: new WarpPath { Ip = "162.159.198.2", Port = 8443, Sni = "www.cloudflare.com" },
            network: "Student", uptime: TimeSpan.FromMinutes(83), ping: 38, reroutes: 1);
        h.Preview(true, true, 2, "VORTEX-PC 4821", "k7m2p9xq4r");
        u.Preview(new Version(2, 2, 0));
    }, (1_288_490_189L, 214_748_365L));

    [Fact]
    public void Connecting() => Main("connecting", (t, h, u) =>
    {
        t.Preview(TunnelState.Reconnecting, "Route blocked. Switching to TCP 4443…", route: new WarpPath { Ip = "162.159.198.189", Port = 4443, Sni = "www.cloudflare.com" }, network: "Student");
        h.Preview(false, false, 0, null, null);
    });

    [Fact]
    public void GaveUp() => Main("error", (t, h, u) =>
    {
        t.Preview(TunnelState.Off, "Couldn't get WARP through this network right now: it blocked or cut all 8 routes tried. Your normal internet is back. Try again in a minute.", error: true);
        h.Preview(false, false, 0, "VORTEX-PC 4821", "k7m2p9xq4r");
    });

    /// Shows the window off-screen (child controls only paint once it really exists) and captures its client area.
    static Bitmap ClientShot(Form form, Size? clientSize = null)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-6000, 0);
        form.Show();
        if (clientSize != null) form.ClientSize = clientSize.Value;
        Application.DoEvents();
        Thread.Sleep(300);
        Application.DoEvents();
        using var full = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(full, new Rectangle(Point.Empty, form.Size));
        var origin = form.PointToScreen(Point.Empty);
        return full.Clone(new Rectangle(new Point(origin.X - form.Left, origin.Y - form.Top), form.ClientSize), full.PixelFormat);
    }

    static void SaveShot(Bitmap bmp, string name)
    {
        Assert.True(bmp.Width > 200 && bmp.Height > 200);
        if (string.IsNullOrEmpty(OutDir)) return;
        Directory.CreateDirectory(OutDir);
        bmp.Save(Path.Combine(OutDir, name + ".png"), ImageFormat.Png);
    }

    [Fact]
    public void QrWindow() => OnSta(() =>
    {
        using var form = new QrForm("VORTEX-PC 4821", "k7m2p9xq4r", Theme.Current(), null);
        using var bmp = ClientShot(form);
        SaveShot(bmp, "qr");
    });

    [Fact]
    public void SettingsWindow() => OnSta(() =>
    {
        var settings = Settings.Load(Path.Combine(Path.GetTempPath(), "warpswitch-render-" + Guid.NewGuid().ToString("N") + ".ini"));
        settings.TrustedNetworks.Add("HOME 1_5GHz");
        settings.Bypass.Add("printer.example.com");
        settings.Bypass.Add("203.0.113.0/24");
        settings.AutoConnectUntrusted = true;
        using var form = new SettingsForm(settings, Theme.Current(), null, new Tunnel(settings), () => { });
        using var bmp = ClientShot(form);
        SaveShot(bmp, "settings");
    });
}
