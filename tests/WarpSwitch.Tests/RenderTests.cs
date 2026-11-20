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

    static void Main(string name, Action<Tunnel, Hotspot, Updater> arrange, (long, long) usage = default, Action<Settings> configure = null, string hover = "") => OnSta(() =>
    {
        using var form = new MainForm(startHidden: true, preview: true);
        using var bmp = form.RenderPreview(arrange, usage, configure, hover);
        Save(bmp, name);
    });

    /// What a set-up copy looks like: saved networks, a few split-tunnel entries, the kill switch on.
    static void Configured(Settings s)
    {
        s.AutoConnectNetworks.AddRange(new[] { "Student", "Library Guest", "Airport Free Wi-Fi", "HOME 1_5GHz" });
        s.Bypass.AddRange(new[] { "printer.example.com", "203.0.113.0/24", "10.20.0.5", "intranet.example.edu", "198.51.100.7" });
        s.KillSwitch = true;
        s.ConnectOnLaunch = true;
    }

    static readonly TraceInfo Trace = new() { Ip = "104.28.222.171", Colo = "DEL", Loc = "IN", Warp = true };

    [Fact]
    public void Off() => Main("off", (t, h, u) =>
    {
        t.Preview(TunnelState.Off, network: "Coffee Corner");
        h.Preview(false, false, 0, "VORTEX-PC 4821", "k7m2p9xq4r");
    });

    [Fact]
    public void Connected() => Main("connected", (t, h, u) =>
    {
        t.Preview(TunnelState.Connected, trace: Trace, route: new WarpPath { Ip = "162.159.198.2", Port = 8443, Sni = "www.cloudflare.com" },
            network: "Student", uptime: TimeSpan.FromMinutes(83), ping: 38, reroutes: 1);
        h.Preview(true, true, 2, "VORTEX-PC 4821", "k7m2p9xq4r");
        u.Preview(new Version(2, 2, 0));
    }, (1_288_490_189L, 214_748_365L), Configured, hover: "sw:kill");

    /// More entries than fit: the network list scrolls and the split tunnel ends in "+N more".
    [Fact]
    public void Crowded() => Main("crowded", (t, h, u) =>
    {
        t.Preview(TunnelState.Off, network: "Library Guest");
        h.Preview(false, false, 0, null, null);
    }, default, s =>
    {
        Configured(s);
        s.AutoConnectNetworks.AddRange(new[] { "Office 5G", "Hotel Lobby" });
        for (int i = 1; i <= 12; i++) s.Bypass.Add("host" + i + ".example.com");
        s.AnimationsOverride = false;
        s.Mtu = 1420;
    });

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

    /// The settings live on the main screen now: clicking them (through the same hit-testing the mouse uses)
    /// changes and saves the setting.
    [Fact]
    public void SettingsChangeWhenClicked() => OnSta(() =>
    {
        using var form = new MainForm(startHidden: true, preview: true);
        using (form.RenderPreview((t, h, u) => t.Preview(TunnelState.Off, network: "Coffee Corner"), default, Configured)) { }
        var s = form.PreviewSettings;

        Assert.True(form.PreviewClick("sw:kill"));
        Assert.False(s.KillSwitch);
        Assert.True(form.PreviewClick("sw:auto"));
        Assert.False(s.AutoConnect);
        Assert.True(form.PreviewClick("seg:mtu:2"));
        Assert.Equal(1420, s.Mtu);
        Assert.True(form.PreviewClick("seg:anim:2"));
        Assert.False(s.AnimationsOverride);

        // The star saves the network the PC is on, and a second click forgets it.
        Assert.False(s.IsAutoConnectNetwork("Coffee Corner"));
        Assert.True(form.PreviewClick("star"));
        Assert.True(s.IsAutoConnectNetwork("Coffee Corner"));
        Assert.True(form.PreviewClick("star"));
        Assert.False(s.IsAutoConnectNetwork("Coffee Corner"));

        // × on a saved network and on a split-tunnel chip.
        Assert.True(form.PreviewClick("netx:0"));
        Assert.Equal(new[] { "Library Guest", "Airport Free Wi-Fi", "HOME 1_5GHz" }, s.AutoConnectNetworks);
        Assert.True(form.PreviewClick("chipx:1"));
        Assert.DoesNotContain("203.0.113.0/24", s.Bypass);
        Assert.Equal(4, s.Bypass.Count);

        // All of it was written to disk.
        var saved = Settings.Load(GetPath(s));
        Assert.False(saved.KillSwitch);
        Assert.Equal(1420, saved.Mtu);
        Assert.Equal(s.AutoConnectNetworks, saved.AutoConnectNetworks);
        Assert.Equal(s.Bypass, saved.Bypass);
    });

    static string GetPath(Settings s) => (string)typeof(Settings).GetField("path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(s);

    [Fact]
    public void SplitTunnelEditor() => OnSta(() =>
    {
        using var form = new EditForm("Skip WARP for these", "One per line: an IPv4 address, a range such as 203.0.113.0/24, or a host name such as printer.example.com.",
            "printer.example.com\r\n203.0.113.0/24\r\n10.20.0.5", true, Theme.Current(), null, _ => null);
        using var bmp = ClientShot(form);
        SaveShot(bmp, "edit");
    });
}
