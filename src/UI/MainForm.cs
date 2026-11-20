using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace WarpSwitch;

/// The whole app on one screen, in two halves. On the right, a rounded night-sky panel: the animated vortex
/// around the power orb, what the connection is doing, and a dock with the network and a big connect button.
/// On the left, every setting as soft cards with animated switches, applied the moment they're flipped.
/// Everything is painted by hand (no controls); the animation only runs while the window is on screen
/// (slower when it isn't focused), so the app sits at ~0% CPU in the tray.
sealed class MainForm : Form
{
    const int W = 960, H = 600;
    const int DropNoticeSeconds = 20;
    const int NetRowHeight = 32;
    static readonly int[] MtuChoices = { 0, 1350, 1420 };

    readonly Settings settings;
    readonly Tunnel tunnel;
    readonly Hotspot hotspot = new();
    readonly Updater updater = new();
    readonly Fonts fonts;
    readonly Theme T = Theme.Current();
    readonly VortexArt art;
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip trayMenu = new(), appMenu = new(), addMenu = new();
    readonly ToolStripMenuItem miConnect, miHotspot, miCheckUpdates;
    readonly ToolTip tip = new() { OwnerDraw = true, UseAnimation = false, UseFading = false };
    readonly Timer anim = new() { Interval = 33 };
    readonly Timer poll = new() { Interval = 3000 };
    readonly Timer clock = new() { Interval = 1000 };
    readonly Timer updateTimer = new() { Interval = 60 * 60 * 1000 };
    readonly Timer tipTimer = new() { Interval = 550 };
    readonly Stopwatch frameClock = Stopwatch.StartNew();
    readonly float S;
    readonly bool preview, dohSupported, autostartAllowed;
    Icon icoOff, icoBusy, icoOn;

    bool allowShow, exiting, ready, revealPass, hotspotWasViaWarp, dropNotified, userStopped, updateNotified;
    bool startupOn, startupBusy, keyboardFocus;
    int hotspotPolling, netScroll;
    int rebinds;
    DateTime lastRebind = DateTime.MinValue, copiedUntil = DateTime.MinValue, droppedAt = DateTime.MinValue, lastUpdateCheck = DateTime.MinValue;
    DateTime forgotUntil = DateTime.MinValue, resetUntil = DateTime.MinValue;
    TunnelState lastState;
    string hover = "", focus, declinedNetwork, currentNetwork, tipText;
    LogForm logForm;
    QrForm qrForm;
    EditForm editForm;
    (long In, long Out) usage;

    // ---------------------------------------------------------------- the settings widgets

    /// An animated on/off switch on a row. T runs 0 → 1 as it slides on.
    sealed class Switch
    {
        public string Id, Label, Sub;
        public Func<string> Tip;
        public Rectangle Row, Knob;
        public Func<bool> Get, Enabled = () => true;
        public Action Flip;
        public float T;
    }

    /// A row of options with a highlight that slides to the chosen one. T is the highlight's position, in options.
    sealed class Segments
    {
        public string Id, Label;
        public Func<string> Tip;
        public string[] Options;
        public Rectangle Row, Box;
        public Func<int> Get;
        public Action<int> Set;
        public float T;

        public Rectangle Option(int i)
        {
            int w = Box.Width / Options.Length;
            return new Rectangle(Box.X + i * w, Box.Y, i == Options.Length - 1 ? Box.Width - i * w : w, Box.Height);
        }
    }

    /// A split-tunnel entry (or the "+ Add" / "+N more" chip at the end).
    sealed class Chip
    {
        public Rectangle Box;
        public string Text, Kind;   // "entry", "add", "more"
        public int Index;
        public Rectangle X => new(Box.Right - Box.Height + 2, Box.Y, Box.Height - 4, Box.Height);
    }

    readonly struct Hit
    {
        public readonly Rectangle Box;
        public readonly string Id, Tip;
        public Hit(Rectangle box, string id, string tip = null) { Box = box; Id = id; Tip = tip; }
    }

    readonly List<Switch> switches = new();
    readonly List<Segments> segments = new();
    readonly Switch swHotspot, swKill, swDoh, swAuto, swStartup, swLaunch, swNotify, swUpdates;
    readonly Segments segAnim, segMtu;
    List<Chip> chipCache;
    string chipKey;

    // Layout, in device pixels.
    Rectangle rHero, rStage, rSig, rTitle, rDetail, rStats, rDock, rDockRow, rStar, rConnect;
    Rectangle rHeader, rBrand, rMenu, rUpdate;
    Rectangle cHot, cProt, cAuto, cApp, cSplit, cAdv;
    Rectangle rHotHead, rHotName, rPassValue, rQrLink, rPassLink;
    Rectangle rAutoSub, rNetList, rNetAdd, rSplitEdit, rChips, rForget, rReset;
    PointF center;
    float orbRadius, heroRadius;

    readonly Program.Command firstCommand;
    readonly bool justUpdated;

    public MainForm(bool startHidden, Program.Command command = Program.Command.Show, bool updated = false, bool preview = false)
    {
        this.preview = preview;
        allowShow = !startHidden;
        firstCommand = command;
        justUpdated = updated;
        settings = preview ? Settings.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "warpswitch-preview-" + Guid.NewGuid().ToString("N") + ".ini")) : Settings.Load();
        tunnel = new Tunnel(settings);
        dohSupported = SecureDns.Supported;
        autostartAllowed = preview || Autostart.Allowed;   // previews show the installed app
        S = FitScale(Native.DisplayScale());
        fonts = new Fonts(S);
        art = new VortexArt(T, S);
        Text = Program.Name;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(P(W), P(H));
        KeyPreview = true;
        BackColor = T.Bg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        swHotspot = AddSwitch(new Switch { Id = "hotspot", Get = () => hotspot.On, Enabled = () => ready && hotspot.Available && !hotspot.Busy, Flip = ToggleHotspot });
        swKill = AddSwitch(new Switch
        {
            Id = "kill", Label = "Kill switch", Sub = "Nothing leaks if WARP drops",
            Get = () => settings.KillSwitch, Flip = () => { settings.KillSwitch = !settings.KillSwitch; SaveSettings(live: true); },
            Tip = () => "Starts once WARP has connected. While it reconnects, switches routes or moves to a new network, nothing leaves the PC outside WARP; your local network still works. Disconnect to lift the block (for example, to sign in to a Wi-Fi login page).",
        });
        swDoh = AddSwitch(new Switch
        {
            Id = "doh", Label = "Encrypted DNS", Sub = dohSupported ? "DNS over HTTPS outside WARP" : "Needs Windows 11",
            Get = () => dohSupported && settings.EncryptDns, Enabled = () => dohSupported,
            Flip = () => { settings.EncryptDns = !settings.EncryptDns; SaveSettings(live: true); },
            Tip = () => dohSupported
                ? "Uses Windows' own DNS over HTTPS for Cloudflare's 1.1.1.1, so even the moments the tunnel is down don't send readable lookups across the network."
                : "Uses Windows 11's DNS over HTTPS, which Windows 10 doesn't have.",
        });
        swAuto = AddSwitch(new Switch
        {
            Id = "auto", Label = "Auto-connect",
            Get = () => settings.AutoConnect,
            Flip = () => { settings.AutoConnect = !settings.AutoConnect; SaveSettings(); AutoConnectHere(); },
            Tip = () => "WARP turns on by itself when you join a saved network. If you turn it off yourself, it stays off on that network until you move to another one.",
        });
        swStartup = AddSwitch(new Switch
        {
            Id = "startup", Label = "Start with Windows",
            Get = () => startupOn, Enabled = () => !startupBusy && (autostartAllowed || startupOn), Flip = FlipStartup,
            Tip = () => autostartAllowed
                ? "Starts in the tray when you sign in, without a UAC prompt."
                : "Available in the installed version (WarpSwitch-Setup.exe from the releases page), so the sign-in task can't be hijacked.",
        });
        swLaunch = AddSwitch(new Switch
        {
            Id = "launch", Label = "Connect when the app opens",
            Get = () => settings.ConnectOnLaunch, Flip = () => { settings.ConnectOnLaunch = !settings.ConnectOnLaunch; SaveSettings(); },
            Tip = () => "Connects as soon as WARP Switch starts, including when it starts with Windows.",
        });
        swNotify = AddSwitch(new Switch
        {
            Id = "notify", Label = "Notifications",
            Get = () => settings.Notifications, Flip = () => { settings.Notifications = !settings.Notifications; SaveSettings(); },
            Tip = () => "When WARP drops for more than a few seconds, comes back, gives up, or an update is out.",
        });
        swUpdates = AddSwitch(new Switch
        {
            Id = "updates", Label = "Check for updates",
            Get = () => settings.CheckUpdates, Flip = () => { settings.CheckUpdates = !settings.CheckUpdates; SaveSettings(); },
            Tip = () => "Looks for a new version on GitHub about twice a day. You can always check from the ⋯ menu.",
        });
        segAnim = AddSegments(new Segments
        {
            Id = "anim", Label = "Animations", Options = new[] { "Auto", "On", "Off" },
            Get = () => settings.AnimationsOverride == null ? 0 : settings.AnimationsOverride.Value ? 1 : 2,
            Set = i => { settings.AnimationsOverride = i switch { 1 => true, 2 => false, _ => (bool?)null }; SaveSettings(); },
            Tip = () => "Auto follows Windows' “Animation effects” setting (now " + (Native.ClientAreaAnimation() ? "on" : "off") + ").",
        });
        segMtu = AddSegments(new Segments
        {
            Id = "mtu", Label = "Tunnel MTU", Options = new[] { "Auto", "1350", "1420" },
            Get = () => Math.Max(0, Array.IndexOf(MtuChoices, settings.Mtu)),
            Set = i => { settings.Mtu = MtuChoices[i]; SaveSettings(); },
            Tip = () => "Takes effect on the next connect. WARP's helper only officially supports 1280 (Auto); change it only if a network needs it.",
        });
        LayoutTargets();
        SnapWidgets();

        miConnect = new ToolStripMenuItem("Connect", null, (s, e) => TogglePower());
        miHotspot = new ToolStripMenuItem("Mobile hotspot", null, (s, e) => ToggleHotspot());
        trayMenu.Items.AddRange(new ToolStripItem[]
        {
            miConnect, miHotspot, new ToolStripSeparator(),
            new ToolStripMenuItem("Open " + Program.Name, null, (s, e) => ShowFromTray()),
            new ToolStripMenuItem("Quit", null, (s, e) => ExitApp()),
        });
        trayMenu.Opening += (s, e) => UpdateMenus();

        miCheckUpdates = new ToolStripMenuItem("Check for updates", null, (s, e) => CheckForUpdates(manual: true));
        appMenu.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("Show log", null, (s, e) => ShowLog()),
            new ToolStripMenuItem("Open app folder", null, (s, e) => OpenFolder()),
            miCheckUpdates,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit " + Program.Name, null, (s, e) => ExitApp()),
            new ToolStripSeparator(),
            new ToolStripMenuItem(Program.Name + " " + Program.Version + "  ·  made by Vortex") { Enabled = false },
        });
        appMenu.Opening += (s, e) => UpdateMenus();

        foreach (var menu in new[] { trayMenu, appMenu, addMenu })
        {
            var m = menu;
            m.Renderer = new MenuRenderer(T);
            m.HandleCreated += (s, e) => Native.RoundCorners(m.Handle);
        }
        tip.Popup += (s, e) =>
        {
            var size = TextRenderer.MeasureText(tipText ?? "", fonts.Small, new Size(P(280), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            e.ToolTipSize = new Size(size.Width + P(22) + 2, size.Height + P(14));
        };
        tip.Draw += (s, e) =>
        {
            using (var b = new SolidBrush(T.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var pen = new Pen(T.Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, fonts.Small, new Rectangle(P(11), P(7), e.Bounds.Width - P(22), e.Bounds.Height - P(14)), T.Sub,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        };
        tipTimer.Tick += (s, e) => ShowTip();

        MakeTrayIcons();
        tray.ContextMenuStrip = trayMenu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        tray.BalloonTipClicked += (s, e) => ShowFromTray();

        hotspot.Log = tunnel.Log.Add;
        tunnel.Changed += () => Ui(OnTunnelChanged);
        tunnel.IdleNetworkChanged += name => Ui(() => OnIdleNetwork(name));
        hotspot.Changed += () => Ui(OnHotspotChanged);
        updater.Changed += () => Ui(OnUpdaterChanged);
        anim.Tick += (s, e) => Animate();
        poll.Tick += (s, e) => PollHotspot();
        clock.Tick += (s, e) => { usage = tunnel.Usage(); Invalidate(rStats); };
        updateTimer.Tick += (s, e) => CheckForUpdates(manual: false);

        CreateHandle();
        if (preview) return;

        tunnel.Log.Add($"· window {ClientSize.Width}×{ClientSize.Height} at scale {S:0.##} (display {Native.DisplayScale() * 100:0} %)");
        tray.Visible = true;
        Native.AllowMessage(Handle, Program.ShowMessage);
        Native.AllowTaskbarCreated();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Program.EmergencyCleanup = tunnel.ShutdownNow;
        poll.Start();
        updateTimer.Enabled = settings.CheckUpdates;
        Task.Run(Autostart.IsEnabled).ContinueWith(task => Ui(() =>
        {
            startupOn = !task.IsFaulted && task.Result;
            StartAnim();
            Invalidate(cApp);
        }));
        OnTunnelChanged();
        BeginInvoke(new Action(InitAsync));
    }

    int P(float v) => (int)Math.Round(v * S);
    Rectangle R(float x, float y, float w, float h) => new(P(x), P(y), P(w), P(h));
    void Ui(Action a) { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(a); } catch (InvalidOperationException) { } }

    Switch AddSwitch(Switch s) { switches.Add(s); return s; }
    Segments AddSegments(Segments s) { segments.Add(s); return s; }

    /// The display's scale, or less if the window wouldn't fit the screen at it (a small laptop at 150 %):
    /// everything, text included, is drawn at this scale.
    static float FitScale(float dpiScale)
    {
        try
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int chromeH = SystemInformation.CaptionHeight + 2 * SystemInformation.FrameBorderSize.Height + 16;
            int chromeW = 2 * SystemInformation.FrameBorderSize.Width + 16;
            float fit = Math.Min((area.Height - chromeH) / (float)H, (area.Width - chromeW) / (float)W);
            return Math.Max(0.6f, Math.Min(dpiScale, fit));
        }
        catch { return dpiScale; }
    }

    void LayoutTargets()
    {
        // Right: the hero panel.
        rHero = R(588, 12, 360, 576);
        heroRadius = P(24);
        center = new PointF(rHero.X + rHero.Width / 2f, rHero.Y + P(146));
        orbRadius = P(48);
        float arms = P(112);
        int reach = (int)arms + P(6);
        rStage = new Rectangle((int)center.X - reach, (int)center.Y - reach, 2 * reach, 2 * reach);
        rSig = new Rectangle(rHero.Right - P(96), rHero.Y + P(8), P(84), P(30));
        rTitle = new Rectangle(rHero.X + P(16), rHero.Y + P(270), rHero.Width - P(32), P(36));
        rDetail = new Rectangle(rHero.X + P(28), rHero.Y + P(306), rHero.Width - P(56), P(38));
        rStats = new Rectangle(rHero.X + P(22), rHero.Y + P(352), rHero.Width - P(44), P(86));
        rDock = new Rectangle(rHero.X + P(12), rHero.Bottom - P(12 + 118), rHero.Width - P(24), P(118));
        rDockRow = new Rectangle(rDock.X + P(14), rDock.Y + P(12), rDock.Width - P(28), P(40));
        rStar = new Rectangle(rDockRow.Right - P(34), rDockRow.Y + P(3), P(34), P(34));
        rConnect = new Rectangle(rDock.X + P(12), rDock.Bottom - P(12 + 48), rDock.Width - P(24), P(48));

        // Left: header, then two columns of cards.
        rHeader = new Rectangle(0, 0, rHero.X, P(54));
        rBrand = R(24, 14, 240, 32);
        rMenu = R(576 - 20 - 32, 14, 32, 32);
        rUpdate = new Rectangle(rMenu.X - P(10 + 104), P(19), P(104), P(22));
        cHot = R(20, 60, 272, 136);
        cProt = R(20, 208, 272, 136);
        cAuto = R(20, 356, 272, 232);
        cApp = R(304, 60, 272, 244);
        cSplit = R(304, 316, 272, 148);
        cAdv = R(304, 476, 272, 112);

        Rectangle Knob(Rectangle row) => new(row.Right - P(16 + 42), row.Y + (row.Height - P(24)) / 2, P(42), P(24));

        rHotHead = new Rectangle(cHot.X, cHot.Y, cHot.Width, P(68));
        swHotspot.Row = rHotHead;
        swHotspot.Knob = new Rectangle(cHot.Right - P(16 + 42), cHot.Y + P(22), P(42), P(24));
        rHotName = new Rectangle(cHot.X + P(16), cHot.Y + P(76), cHot.Width - P(32), P(24));
        int py = cHot.Y + P(102);
        rPassValue = new Rectangle(cHot.X + P(92), py, P(100), P(24));
        rPassLink = new Rectangle(cHot.Right - P(16 + 40), py, P(40), P(24));
        rQrLink = new Rectangle(rPassLink.X - P(8 + 26), py, P(26), P(24));

        int y = cProt.Y + P(42);
        foreach (var sw in new[] { swKill, swDoh })
        {
            sw.Row = new Rectangle(cProt.X, y, cProt.Width, P(44));
            sw.Knob = Knob(sw.Row);
            y += P(44);
        }

        swAuto.Row = new Rectangle(cAuto.X, cAuto.Y + P(4), cAuto.Width, P(42));
        swAuto.Knob = Knob(swAuto.Row);
        rAutoSub = new Rectangle(cAuto.X + P(16), cAuto.Y + P(44), cAuto.Width - P(32), P(32));
        rNetList = new Rectangle(cAuto.X + P(10), cAuto.Y + P(80), cAuto.Width - P(20), P(3 * NetRowHeight));
        rNetAdd = new Rectangle(cAuto.X + P(16), cAuto.Bottom - P(14 + 34), cAuto.Width - P(32), P(34));

        y = cApp.Y + P(42);
        foreach (var sw in new[] { swStartup, swLaunch, swNotify, swUpdates })
        {
            sw.Row = new Rectangle(cApp.X, y, cApp.Width, P(38));
            sw.Knob = Knob(sw.Row);
            y += P(38);
        }
        segAnim.Row = new Rectangle(cApp.X, y, cApp.Width, P(38));
        segAnim.Box = new Rectangle(cApp.Right - P(16 + 138), y + P(6), P(138), P(26));

        rSplitEdit = new Rectangle(cSplit.Right - P(16 + 36), cSplit.Y + P(12), P(36), P(24));
        rChips = new Rectangle(cSplit.X + P(16), cSplit.Y + P(46), cSplit.Width - P(32), P(86));

        segMtu.Row = new Rectangle(cAdv.X, cAdv.Y + P(38), cAdv.Width, P(32));
        segMtu.Box = new Rectangle(cAdv.Right - P(16 + 138), segMtu.Row.Y + P(3), P(138), P(26));
        int bw = (cAdv.Width - P(32 + 8)) / 2;
        rForget = new Rectangle(cAdv.X + P(16), cAdv.Bottom - P(12 + 28), bw, P(28));
        rReset = new Rectangle(rForget.Right + P(8), rForget.Y, cAdv.Right - P(16) - rForget.Right - P(8), P(28));

        chipCache = null;
        art.Layout(ClientSize, rHero, heroRadius, center, orbRadius, arms);
    }

    // ---------------------------------------------------------------- lifecycle

    async void InitAsync()
    {
        bool resume = false;
        string network = null;
        try
        {
            resume = await Task.Run(() =>
            {
                Setup.EnsureBinaries();
                Setup.MigrateOldConfig();
                try { if (Autostart.RepointIfNeeded()) tunnel.Log.Add("· the sign-in task now starts this copy (" + Application.ExecutablePath + ")"); } catch { }
                bool wasRunning = tunnel.RecoverOnStartup();
                network = Tunnel.CurrentNetwork();
                return wasRunning;
            });
        }
        catch (Exception ex) { tunnel.ReportError(ex); }
        ready = true;
        currentNetwork = network;
        PollHotspot();
        UpdateMenus();
        Invalidate();
        if (justUpdated) Notify(Program.Name + " " + Program.Version, "Updated. " + (firstCommand == Program.Command.Connect ? "Reconnecting to WARP…" : "You're on the latest version."), force: true);
        // WARP was already on (old .bat buttons, or a previous run): take it over seamlessly.
        if (resume || settings.ConnectOnLaunch || firstCommand == Program.Command.Connect) await tunnel.ConnectAsync();
        else MaybeAutoConnect(network);
        if (firstCommand == Program.Command.HotspotOn) Run(Program.Command.HotspotOn);
        if (settings.CheckUpdates) { await Task.Delay(TimeSpan.FromSeconds(20)); CheckForUpdates(manual: false); }
    }

    /// Requests from a second launch (WARP Switch.exe --connect etc., or the installer's Quit).
    async void Run(Program.Command command)
    {
        switch (command)
        {
            case Program.Command.Show: ShowFromTray(); break;
            case Program.Command.Connect: if (ready) await tunnel.ConnectAsync(); break;
            case Program.Command.Disconnect: userStopped = true; await tunnel.DisconnectAsync(); break;
            case Program.Command.HotspotOn:
            case Program.Command.HotspotOff:
                if (!ready || hotspot.Busy) break;
                bool on = command == Program.Command.HotspotOn;
                if (on == hotspot.On && !(on && tunnel.State == TunnelState.Connected && !hotspot.ViaWarp)) break;
                lastRebind = DateTime.UtcNow;
                string tun = tunnel.State == TunnelState.Connected ? tunnel.TunId : null;
                await hotspot.SetAsync(on, tun);
                hotspotWasViaWarp = on && tun != null && hotspot.On;
                break;
            case Program.Command.Quit: ExitApp(); break;
        }
    }

    void MakeTrayIcons()
    {
        int size = SystemInformation.SmallIconSize.Width;
        icoOff = Icons.Make(size, Color.Empty, Color.Empty, Color.FromArgb(0xB4, 0xAC, 0xDA));
        icoBusy = Icons.Make(size, Color.Empty, Color.Empty, T.Amber);
        icoOn = Icons.Make(size, T.Accent, T.Accent2, Color.White);
        UpdateTrayIcon();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleTitleBar(Handle, true, T.Bg);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void WndProc(ref Message m)
    {
        if (Program.ShowMessage != 0 && m.Msg == Program.ShowMessage) { Run((Program.Command)(int)m.WParam); return; }
        base.WndProc(ref m);
    }

    void ShowFromTray()
    {
        allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { clock.Stop(); anim.Stop(); HideTip(); return; }
        clock.Enabled = tunnel.State == TunnelState.Connected;
        StartAnim();
        PollHotspot();
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); StartAnim(); }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); HideTip(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); StartAnim(); }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) tunnel.Suspend();
        else if (e.Mode == PowerModes.Resume) tunnel.Resume();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;   // the X button tucks the app into the tray
            Hide();
            if (!settings.TrayHintShown)
            {
                settings.TrayHintShown = true;
                settings.Save();
                tray.ShowBalloonTip(4000, Program.Name + " is still running", "It lives in the tray now. Right-click the icon to quit.", ToolTipIcon.None);
            }
            return;
        }
        if (!exiting && !preview) tunnel.ShutdownNow();   // Windows is shutting down or signing out
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    async void ExitApp()
    {
        if (exiting) return;
        exiting = true;
        allowShow = false;
        Hide();
        logForm?.Close();
        qrForm?.Close();
        editForm?.Close();
        try
        {
            bool reshare = hotspot.On && (hotspot.ViaWarp || hotspotWasViaWarp);
            await tunnel.DisconnectAsync();
            for (int i = 0; i < 100 && tunnel.State != TunnelState.Off; i++) await Task.Delay(100);
            if (reshare) await hotspot.SetAsync(true, null);   // leave the hotspot working on normal internet
        }
        catch { }
        tray.Visible = false;
        tunnel.Log.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!preview) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            tray.Dispose();
            trayMenu.Dispose(); appMenu.Dispose(); addMenu.Dispose();
            tip.Dispose();
            anim.Dispose(); poll.Dispose(); clock.Dispose(); updateTimer.Dispose(); tipTimer.Dispose();
            fonts.Dispose();
            art.Dispose();
            icoOff?.Dispose(); icoBusy?.Dispose(); icoOn?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- state

    void OnTunnelChanged()
    {
        var st = tunnel.State;
        var mood = st == TunnelState.Connected ? VortexArt.Mood.On
            : tunnel.Busy ? VortexArt.Mood.Busy
            : tunnel.DetailIsError ? VortexArt.Mood.Error
            : VortexArt.Mood.Idle;
        art.SetMood(mood);
        StartAnim();
        clock.Enabled = st == TunnelState.Connected && Visible;
        if (st == TunnelState.Connected) usage = tunnel.Usage();
        if (st != TunnelState.Off && !string.IsNullOrEmpty(tunnel.Network)) currentNetwork = tunnel.Network;
        UpdateTrayIcon();
        UpdateMenus();

        if (st == TunnelState.Connected && lastState != TunnelState.Connected)
        {
            rebinds = 0;
            lastRebind = DateTime.MinValue;
            PollHotspot();
            if (dropNotified) Notify("WARP is back", "All apps are going through WARP again.");
            dropNotified = false;
            droppedAt = DateTime.MinValue;
            userStopped = false;
        }
        if (st == TunnelState.Reconnecting && lastState == TunnelState.Connected) droppedAt = DateTime.UtcNow;
        if (st == TunnelState.Off && lastState != TunnelState.Off)
        {
            droppedAt = DateTime.MinValue;
            if (tunnel.DetailIsError && !userStopped && !exiting)
            {
                Notify("WARP stopped", tunnel.Detail, error: true);
                declinedNetwork = tunnel.Network;   // don't auto-connect straight back into the same failure
            }
            if (hotspotWasViaWarp && !exiting)
            {
                // WARP went away underneath the hotspot: keep it running on the normal connection.
                hotspotWasViaWarp = false;
                _ = hotspot.SetAsync(true, null);
            }
            userStopped = false;
        }
        lastState = st;
        Invalidate();
    }

    void OnHotspotChanged()
    {
        StartAnim();
        UpdateMenus();
        UpdateTrayIcon();
        Invalidate(cHot);
    }

    void OnUpdaterChanged()
    {
        Invalidate(rHeader);
        UpdateMenus();
        var v = updater.Available;
        if (v != null && !updater.Busy && !updateNotified && settings.SkippedVersion != v.ToString(3) && !Visible)
        {
            updateNotified = true;
            Notify("Update available", Program.Name + " " + v.ToString(3) + " is out. Open the app to install it.");
        }
    }

    void PollHotspot()
    {
        poll.Interval = Visible ? 3000 : 8000;
        CheckDropNotice();
        if (!ready || hotspot.Busy || Interlocked.Exchange(ref hotspotPolling, 1) == 1) return;
        string tun = tunnel.State == TunnelState.Connected ? tunnel.TunId : null;
        // While WARP is off the dock shows which network the PC is on; keep it current while it's on screen.
        bool wantNetwork = tunnel.State == TunnelState.Off && Visible;
        string network = null;
        // WinRT and adapter enumeration can take a moment: keep them off the UI thread so the vortex doesn't stutter.
        Task.Run(() =>
        {
            hotspot.Refresh(tun);
            if (wantNetwork) network = Tunnel.CurrentNetwork();
        }).ContinueWith(_ => Ui(() =>
        {
            hotspotPolling = 0;
            if (wantNetwork && tunnel.State == TunnelState.Off && network != currentNetwork)
            {
                currentNetwork = network;
                Invalidate(rDock);
                Invalidate(cAuto);
            }
            AfterHotspotPoll(tun);
        }));
    }

    void AfterHotspotPoll(string tun)
    {
        if (tun == null || tunnel.State != TunnelState.Connected || tunnel.TunId != tun) return;
        if (!hotspot.On) { hotspotWasViaWarp = false; return; }
        if (hotspot.ViaWarp) { hotspotWasViaWarp = true; return; }
        // On, but sharing the raw Wi-Fi (e.g. switched on from Windows Settings, or the tunnel just moved
        // to another route and got a fresh adapter): move it onto WARP.
        if (rebinds < 2 && (DateTime.UtcNow - lastRebind).TotalSeconds > 20)
        {
            rebinds++;
            lastRebind = DateTime.UtcNow;
            _ = hotspot.SetAsync(true, tun);
        }
    }

    // ---------------------------------------------------------------- notifications

    void Notify(string title, string text, bool error = false, bool force = false)
    {
        if (preview || (!settings.Notifications && !force)) return;
        // Only when the window isn't in front of the user: otherwise they can see it already.
        if (!force && Visible && ActiveForm == this) return;
        tray.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// A drop that fixes itself within a few seconds isn't worth a notification; one that lasts is.
    void CheckDropNotice()
    {
        if (droppedAt == DateTime.MinValue || dropNotified || tunnel.State == TunnelState.Connected) return;
        if ((DateTime.UtcNow - droppedAt).TotalSeconds < DropNoticeSeconds) return;
        dropNotified = true;
        Notify("WARP dropped", settings.KillSwitch
            ? "Reconnecting. The kill switch is holding your internet until WARP is back."
            : "Reconnecting. Apps aren't going through WARP until it's back.", error: true);
    }

    // ---------------------------------------------------------------- auto-connect on saved networks

    void OnIdleNetwork(string network)
    {
        if (network != declinedNetwork) declinedNetwork = null;
        currentNetwork = network;
        Invalidate(rDock);
        Invalidate(cAuto);
        MaybeAutoConnect(network);
    }

    async void MaybeAutoConnect(string network)
    {
        if (preview || !ready || !settings.AutoConnect || tunnel.State != TunnelState.Off || string.IsNullOrEmpty(network)) return;
        if (!settings.IsAutoConnectNetwork(network) || network == declinedNetwork) return;
        tunnel.Log.Add("· connecting by itself: " + network + " is a saved network");
        await tunnel.ConnectAsync();
    }

    /// After the saved list or the switch changed: connect now if the PC is already on a saved network.
    void AutoConnectHere()
    {
        if (preview || !ready || tunnel.State != TunnelState.Off) return;
        Task.Run(Tunnel.CurrentNetwork).ContinueWith(t => Ui(() =>
        {
            if (t.IsFaulted) return;
            currentNetwork = t.Result;
            Invalidate(rDock);
            MaybeAutoConnect(t.Result);
        }));
    }

    /// The network the dock is about (and the star saves).
    string DockNetwork => tunnel.State != TunnelState.Off && !string.IsNullOrEmpty(tunnel.Network) ? tunnel.Network : currentNetwork;

    void ToggleStar()
    {
        string net = DockNetwork;
        if (string.IsNullOrEmpty(net)) return;
        bool save = !settings.IsAutoConnectNetwork(net);
        settings.SetAutoConnectNetwork(net, save);
        ClampNetScroll();
        SaveSettings();
        if (save) MaybeAutoConnect(net);
    }

    void AddNetwork(string name)
    {
        if (!settings.SetAutoConnectNetwork(name, true)) return;
        // Scroll so the new one shows.
        netScroll = NetScrollMax;
        SaveSettings();
        AutoConnectHere();
    }

    void RemoveNetwork(int index)
    {
        if (index < 0 || index >= settings.AutoConnectNetworks.Count) return;
        settings.AutoConnectNetworks.RemoveAt(index);
        ClampNetScroll();
        SaveSettings();
    }

    int NetScrollMax => Math.Max(0, settings.AutoConnectNetworks.Count * P(NetRowHeight) - rNetList.Height);

    void ClampNetScroll() => netScroll = Math.Max(0, Math.Min(netScroll, NetScrollMax));

    Rectangle NetRow(int i) => new(rNetList.X, rNetList.Y + i * P(NetRowHeight) - netScroll, rNetList.Width, P(NetRowHeight));

    Rectangle NetRemove(Rectangle row) => new(row.Right - P(8 + 24), row.Y + (row.Height - P(24)) / 2, P(24), P(24));

    async void ShowAddNetworkMenu()
    {
        if (preview) return;
        string here = null;
        List<string> known = new();
        try { (here, known) = await Task.Run(() => (Tunnel.CurrentNetwork(), Net.KnownWifiNetworks())); } catch { }
        if (IsDisposed) return;
        if (tunnel.State == TunnelState.Off) currentNetwork = here;
        static string Menu(string s) => s.Replace("&", "&&");
        addMenu.Items.Clear();
        if (!string.IsNullOrEmpty(here))
        {
            bool saved = settings.IsAutoConnectNetwork(here);
            addMenu.Items.Add(new ToolStripMenuItem((saved ? "Already saved: " : "This network: ") + Menu(here), null, (s, e) => AddNetwork(here)) { Enabled = !saved });
            addMenu.Items.Add(new ToolStripSeparator());
        }
        var others = known.Where(n => !settings.IsAutoConnectNetwork(n) && !string.Equals(n, here, StringComparison.OrdinalIgnoreCase)).Take(15).ToList();
        if (others.Count > 0)
        {
            addMenu.Items.Add(new ToolStripMenuItem("Wi-Fi this PC remembers") { Enabled = false });
            foreach (var n in others)
            {
                string name = n;
                addMenu.Items.Add(new ToolStripMenuItem("    " + Menu(name), null, (s, e) => AddNetwork(name)));
            }
            addMenu.Items.Add(new ToolStripSeparator());
        }
        addMenu.Items.Add(new ToolStripMenuItem("Type a name…", null, (s, e) => TypeNetwork()));
        addMenu.Show(this, new Point(rNetAdd.X, rNetAdd.Y - P(4)), ToolStripDropDownDirection.AboveRight);
    }

    void TypeNetwork()
    {
        var lines = Edit("Auto-connect on a network",
            "The network's name as Windows shows it: the Wi-Fi name, or for a cable, the name under Settings → Network & internet → Ethernet.",
            "", multiline: false, l => l.Length == 0 ? "Type the network's name." : null);
        if (lines != null && lines.Length > 0) AddNetwork(lines[0]);
    }

    // ---------------------------------------------------------------- split tunnel

    static string CheckBypass(string[] lines)
    {
        var bad = lines.Where(l => !l.StartsWith("#") && Net.ParseBypass(l, out _) == null).ToList();
        return bad.Count == 0 ? null : "Not an IPv4 address, range or host name: " + string.Join(", ", bad.Take(3)) + (bad.Count > 3 ? "…" : "");
    }

    void EditBypass()
    {
        var lines = Edit("Skip WARP for these",
            "One per line: an IPv4 address, a range such as 203.0.113.0/24, or a host name such as printer.example.com. They go straight out your normal connection. Your local network always does. Ctrl+Enter saves.",
            string.Join(Environment.NewLine, settings.Bypass), multiline: true, CheckBypass);
        if (lines == null) return;
        settings.Bypass = lines.ToList();
        SaveSettings(live: true);
    }

    void AddBypass()
    {
        var lines = Edit("Skip WARP for…",
            "An IPv4 address, a range such as 203.0.113.0/24, or a host name such as printer.example.com. It goes straight out your normal connection.",
            "", multiline: false, l => l.Length == 0 ? "Type an address, range or host name." : CheckBypass(l));
        if (lines == null) return;
        foreach (var l in lines)
            if (!settings.Bypass.Any(b => string.Equals(b, l, StringComparison.OrdinalIgnoreCase))) settings.Bypass.Add(l);
        SaveSettings(live: true);
    }

    void RemoveBypass(int index)
    {
        if (index < 0 || index >= settings.Bypass.Count) return;
        settings.Bypass.RemoveAt(index);
        SaveSettings(live: true);
    }

    /// Opens the typing dialog; the saved lines, or null if it was cancelled.
    string[] Edit(string title, string hint, string text, bool multiline, Func<string[], string> validate)
    {
        if (editForm != null && !editForm.IsDisposed) { editForm.Activate(); return null; }
        HideTip();
        using var f = new EditForm(title, hint, text, multiline, T, Icon, validate);
        editForm = f;
        try { return f.ShowDialog(this) == DialogResult.OK ? f.Lines : null; }
        finally { editForm = null; }
    }

    /// Chips for the split-tunnel entries, wrapped onto up to three lines, then "+ Add" (or "+N more" when
    /// they don't all fit, which opens the full list).
    List<Chip> Chips()
    {
        string key = string.Join("\n", settings.Bypass);
        if (chipCache != null && key == chipKey) return chipCache;
        var placed = new List<Chip>();
        int h = P(26), gap = P(6), left = rChips.X, right = rChips.Right, top = rChips.Y, maxLines = 3;
        int Measure(string s) => TextRenderer.MeasureText(s, fonts.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        int addW = Measure("Add") + P(12 + 14 + 6 + 12);
        int n = settings.Bypass.Count;
        if (n == 0)
        {
            placed.Add(new Chip { Kind = "add", Text = "Add", Box = new Rectangle(left, top + P(44), addW, h) });
            return Cache(placed);
        }
        int x = left, line = 0;
        bool Place(Chip c, int w)
        {
            w = Math.Min(w, right - left);
            if (x + w > right && x > left) { line++; x = left; }
            if (line >= maxLines) return false;
            c.Box = new Rectangle(x, top + line * (h + gap), w, h);
            x += w + gap;
            placed.Add(c);
            return true;
        }
        int shown = 0;
        for (int i = 0; i < n; i++)
        {
            if (!Place(new Chip { Kind = "entry", Text = settings.Bypass[i], Index = i }, Measure(settings.Bypass[i]) + P(12 + 24))) break;
            shown++;
        }
        if (shown == n && Place(new Chip { Kind = "add", Text = "Add" }, addW)) return Cache(placed);
        // Not everything fits: make room on the last line for "+N more".
        while (true)
        {
            string label = "+" + (n - shown) + " more";
            int w = Measure(label) + P(24);
            var last = placed.LastOrDefault();
            if (last == null) { placed.Add(new Chip { Kind = "more", Text = label, Box = new Rectangle(left, top, w, h) }); break; }
            int lastLine = (last.Box.Y - top) / (h + gap);
            if (last.Box.Right + gap + w <= right) { placed.Add(new Chip { Kind = "more", Text = label, Box = new Rectangle(last.Box.Right + gap, last.Box.Y, w, h) }); break; }
            if (lastLine < maxLines - 1) { placed.Add(new Chip { Kind = "more", Text = label, Box = new Rectangle(left, last.Box.Y + h + gap, w, h) }); break; }
            placed.RemoveAt(placed.Count - 1);
            shown--;
        }
        return Cache(placed);

        List<Chip> Cache(List<Chip> list)
        {
            chipCache = list;
            chipKey = key;
            return list;
        }
    }

    // ---------------------------------------------------------------- actions

    async void TogglePower()
    {
        if (!ready || preview) return;
        switch (tunnel.State)
        {
            case TunnelState.Off:
                await tunnel.ConnectAsync();
                break;
            case TunnelState.Starting:
            case TunnelState.Connecting:
            case TunnelState.Connected:
            case TunnelState.Reconnecting:
                userStopped = true;
                declinedNetwork = tunnel.Network;   // with auto-connect on, stay off here until the network changes
                await tunnel.DisconnectAsync();
                break;
        }
    }

    async void ToggleHotspot()
    {
        if (!ready || preview || hotspot.Busy || !hotspot.Available) return;
        string tun = tunnel.State == TunnelState.Connected ? tunnel.TunId : null;
        bool turnOn = !hotspot.On;
        lastRebind = DateTime.UtcNow;
        await hotspot.SetAsync(turnOn, tun);
        hotspotWasViaWarp = turnOn && tun != null && hotspot.On;
    }

    async void FlipStartup()
    {
        if (startupBusy || preview || !(autostartAllowed || startupOn)) return;
        bool want = !startupOn;
        startupBusy = true;
        startupOn = want;
        StartAnim();
        try { await Task.Run(() => Autostart.Set(want)); }
        catch (Exception ex)
        {
            startupOn = !want;
            MessageBox.Show(this, ex is UserError ? ex.Message : "Couldn't change Start with Windows: " + ex.Message, Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            startupBusy = false;
            StartAnim();
            Invalidate(cApp);
        }
    }

    /// A setting changed: save it, and push it to a live connection when it acts on one.
    void SaveSettings(bool live = false)
    {
        settings.Save();
        if (live) _ = tunnel.ApplySettingsAsync();
        updateTimer.Enabled = settings.CheckUpdates && !preview;
        StartAnim();
        Invalidate();
    }

    void ForgetRoutes()
    {
        PathMemory.Clear();
        forgotUntil = DateTime.UtcNow.AddSeconds(2.5);
        Invalidate(cAdv);
        Task.Delay(2600).ContinueWith(_ => Ui(() => Invalidate(cAdv)));
    }

    async void ResetRegistration()
    {
        var answer = MessageBox.Show(this,
            "Set this PC's WARP registration aside and make a new one on the next connect?\n\n" +
            "Only needed if connecting keeps failing with registration or key errors. The old registration is kept as config.json.bak.",
            Program.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        if (tunnel.State != TunnelState.Off) { userStopped = true; await tunnel.DisconnectAsync(); }
        try
        {
            await Task.Run(Setup.ResetRegistration);
            resetUntil = DateTime.UtcNow.AddSeconds(2.5);
            Invalidate(cAdv);
            _ = Task.Delay(2600).ContinueWith(_ => Ui(() => Invalidate(cAdv)));
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't reset it: " + ex.Message, Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    void ShowLog()
    {
        if (logForm == null || logForm.IsDisposed)
        {
            logForm = new LogForm(tunnel.Log, T, Icon);
            logForm.Show();
        }
        else logForm.Activate();
    }

    void ShowQr()
    {
        if (hotspot.Ssid == null || string.IsNullOrEmpty(hotspot.Passphrase)) return;
        qrForm?.Close();
        qrForm = new QrForm(hotspot.Ssid, hotspot.Passphrase, T, Icon);
        qrForm.Show(this);
    }

    static void OpenFolder()
    {
        try { Process.Start("explorer.exe", "\"" + Setup.Dir + "\""); } catch { }
    }

    async void CheckForUpdates(bool manual)
    {
        if (preview || updater.Busy) return;
        if (!manual && (!settings.CheckUpdates || (DateTime.UtcNow - lastUpdateCheck).TotalHours < 11.5)) return;
        lastUpdateCheck = DateTime.UtcNow;
        try
        {
            bool newer = await updater.CheckAsync();
            if (manual && !newer)
                MessageBox.Show(this, "You're on the latest version (" + Program.Version + ").", Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (manual && newer) ConfirmUpdate();
        }
        catch (Exception ex)
        {
            tunnel.Log.Add("· update check failed: " + ex.GetBaseException().Message);
            if (manual) MessageBox.Show(this, "Couldn't check for updates: " + ex.GetBaseException().Message, Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    async void ConfirmUpdate()
    {
        var v = updater.Available;
        if (v == null || updater.Busy) return;
        bool connected = tunnel.State is TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting;
        var answer = MessageBox.Show(this,
            "Install " + Program.Name + " " + v.ToString(3) + " now?\n\n" +
            (connected ? "WARP disconnects for a few seconds and reconnects once the update is in." : "It takes a few seconds; the app reopens afterwards.") +
            "\n\nWhat's new: " + Updater.ReleasesUrl,
            Program.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (answer == DialogResult.No)
        {
            settings.SkippedVersion = v.ToString(3);   // stop nagging about this one; the pill stays for later
            settings.Save();
            return;
        }
        if (answer != DialogResult.Yes) return;
        try
        {
            tunnel.Log.Add("· installing update " + v.ToString(3));
            await updater.InstallAsync(reconnect: connected);
            ExitApp();   // the installer waits for this copy to close, then installs and starts the new one
        }
        catch (Exception ex)
        {
            tunnel.Log.Add("· update failed: " + ex.GetBaseException().Message);
            MessageBox.Show(this, "The update didn't install: " + ex.GetBaseException().Message + "\n\nYou can also download it from " + Updater.ReleasesUrl,
                Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void UpdateMenus()
    {
        var st = tunnel.State;
        miConnect.Text = st switch
        {
            TunnelState.Off => "Connect",
            TunnelState.Starting => "Cancel",
            TunnelState.Stopping => "Disconnecting…",
            _ => "Disconnect",
        };
        miConnect.Enabled = ready && st != TunnelState.Stopping;
        miHotspot.Checked = hotspot.On;
        miHotspot.Enabled = ready && hotspot.Available && !hotspot.Busy;
        miCheckUpdates.Enabled = !updater.Busy;
        miCheckUpdates.Text = updater.Available != null ? "Install update " + updater.Available.ToString(3) + "…" : "Check for updates";
    }

    void UpdateTrayIcon()
    {
        tray.Icon = tunnel.State == TunnelState.Connected ? icoOn : tunnel.Busy ? icoBusy : icoOff;
        string text = Program.Name + ": " + Title(tunnel.State) + (hotspot.On ? (hotspot.ViaWarp ? " · hotspot via WARP" : " · hotspot on") : "");
        tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }

    static string Title(TunnelState s) => s switch
    {
        TunnelState.Starting => "Starting…",
        TunnelState.Connecting => "Connecting…",
        TunnelState.Connected => "Connected",
        TunnelState.Reconnecting => "Reconnecting…",
        TunnelState.Stopping => "Disconnecting…",
        _ => "Not connected",
    };

    // ---------------------------------------------------------------- input

    bool UpdatePillVisible => updater.Available != null || updater.Busy;
    bool StarVisible => ready && !string.IsNullOrEmpty(DockNetwork);
    bool HasPassword => hotspot.Ssid != null && !string.IsNullOrEmpty(hotspot.Passphrase);

    /// Everything clickable or hoverable, most specific first. Rebuilt on demand so it never goes stale.
    List<Hit> Hits()
    {
        var h = new List<Hit> { new(rMenu, "menu", "Log, app folder, updates, quit") };
        if (UpdatePillVisible) h.Add(new Hit(rUpdate, "update"));
        h.Add(new Hit(rConnect, "connect"));
        if (StarVisible)
            h.Add(new Hit(rStar, "star", settings.IsAutoConnectNetwork(DockNetwork)
                ? "Saved: WARP connects by itself on this network" + (settings.AutoConnect ? "." : " (auto-connect is paused).") + " Click to forget it."
                : "Save this network: WARP will connect by itself whenever you're on it."));
        if (hotspot.Ssid != null) h.Add(new Hit(rPassLink, "reveal"));
        if (HasPassword) { h.Add(new Hit(rQrLink, "qr", "Join by pointing a phone's camera at a QR code")); h.Add(new Hit(rPassValue, "copy", "Click to copy")); }
        foreach (var sw in switches) h.Add(new Hit(sw.Row, "sw:" + sw.Id, sw.Tip?.Invoke()));
        foreach (var sg in segments)
        {
            string t = sg.Tip?.Invoke();
            for (int i = 0; i < sg.Options.Length; i++) h.Add(new Hit(sg.Option(i), "seg:" + sg.Id + ":" + i, t));
            h.Add(new Hit(sg.Row, "segrow:" + sg.Id, t));
        }
        for (int i = 0; i < settings.AutoConnectNetworks.Count; i++)
        {
            var row = Rectangle.Intersect(NetRow(i), rNetList);
            if (row.Height <= 0) continue;
            var x = Rectangle.Intersect(NetRemove(NetRow(i)), rNetList);
            if (x.Height > 0) h.Add(new Hit(x, "netx:" + i, "Stop auto-connecting on " + settings.AutoConnectNetworks[i]));
            h.Add(new Hit(row, "net:" + i));
        }
        h.Add(new Hit(rNetAdd, "netadd"));
        foreach (var c in Chips())
        {
            if (c.Kind == "entry") { h.Add(new Hit(c.X, "chipx:" + c.Index, "Remove " + c.Text)); h.Add(new Hit(c.Box, "chip:" + c.Index, c.Text)); }
            else h.Add(new Hit(c.Box, c.Kind == "add" ? "chipadd" : "chipmore"));
        }
        h.Add(new Hit(rSplitEdit, "splitedit", "Edit the whole list"));
        h.Add(new Hit(rForget, "forget", "Forget which routes worked on each network. Every network gets scouted from scratch next time."));
        h.Add(new Hit(rReset, "reset", "Make a new WARP registration on the next connect. Only needed if connecting keeps failing with registration or key errors."));
        return h;
    }

    Hit? HitAt(Point p)
    {
        float dx = p.X - center.X, dy = p.Y - center.Y;
        if (dx * dx + dy * dy <= orbRadius * orbRadius)
            return new Hit(new Rectangle((int)(center.X - orbRadius), (int)(center.Y - orbRadius), (int)(2 * orbRadius), (int)(2 * orbRadius)), "power");
        foreach (var hit in Hits()) if (hit.Box.Contains(p)) return hit;
        return null;
    }

    Switch SwitchFor(string id) => id.StartsWith("sw:") ? switches.FirstOrDefault(s => "sw:" + s.Id == id) : null;
    Segments SegmentsFor(string id) => segments.FirstOrDefault(s => id == "seg:" + s.Id || id.StartsWith("seg:" + s.Id + ":"));

    bool Clickable(string target)
    {
        if (string.IsNullOrEmpty(target)) return false;
        if (target is "power" or "connect") return ready && tunnel.State != TunnelState.Stopping;
        if (target == "update") return !updater.Busy;
        if (target.StartsWith("sw:")) return SwitchFor(target)?.Enabled() == true;
        if (target.StartsWith("segrow:") || target.StartsWith("net:") || target.StartsWith("chip:")) return false;
        return true;
    }

    /// Clicking (or pressing Space on) a target.
    void Act(string id)
    {
        if (!Clickable(id)) return;
        switch (id)
        {
            case "power":
            case "connect": TogglePower(); return;
            case "menu": appMenu.Show(this, new Point(rMenu.Right, rMenu.Bottom + P(4)), ToolStripDropDownDirection.BelowLeft); return;
            case "update": ConfirmUpdate(); return;
            case "star": ToggleStar(); return;
            case "reveal": revealPass = !revealPass; Invalidate(cHot); return;
            case "qr": ShowQr(); return;
            case "copy":
                try { Clipboard.SetText(hotspot.Passphrase); } catch { }
                copiedUntil = DateTime.UtcNow.AddSeconds(1.6);
                Invalidate(cHot);
                Task.Delay(1700).ContinueWith(_ => Ui(() => Invalidate(cHot)));
                return;
            case "netadd": ShowAddNetworkMenu(); return;
            case "chipadd": AddBypass(); return;
            case "chipmore":
            case "splitedit": EditBypass(); return;
            case "forget": ForgetRoutes(); return;
            case "reset": ResetRegistration(); return;
        }
        if (SwitchFor(id) is Switch sw) { sw.Flip(); return; }
        if (SegmentsFor(id) is Segments sg)
        {
            // A click picks that option; Space on the focused control moves to the next one.
            int pick = id.Length > ("seg:" + sg.Id + ":").Length && int.TryParse(id.Substring(("seg:" + sg.Id + ":").Length), out int i) ? i : (sg.Get() + 1) % sg.Options.Length;
            if (pick != sg.Get()) sg.Set(pick);
            return;
        }
        if (id.StartsWith("netx:") && int.TryParse(id.Substring(5), out int n)) { RemoveNetwork(n); return; }
        if (id.StartsWith("chipx:") && int.TryParse(id.Substring(6), out int c)) RemoveBypass(c);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitAt(e.Location);
        string h = hit?.Id ?? "";
        if (h == hover) return;
        hover = h;
        Cursor = Clickable(h) ? Cursors.Hand : Cursors.Default;
        HideTip();
        if (hit?.Tip != null) tipTimer.Start();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        HideTip();
        if (hover.Length == 0) return;
        hover = "";
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        HideTip();
        if (keyboardFocus) { keyboardFocus = false; Invalidate(); }
        var hit = HitAt(e.Location);
        if (hit != null) Act(hit.Value.Id);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!rNetList.Contains(e.Location) || NetScrollMax == 0) return;
        netScroll -= Math.Sign(e.Delta) * P(NetRowHeight);
        ClampNetScroll();
        hover = HitAt(e.Location)?.Id ?? "";
        Invalidate(cAuto);
    }

    void ShowTip()
    {
        tipTimer.Stop();
        if (!Visible || ActiveForm != this) return;
        var p = PointToClient(Cursor.Position);
        var hit = HitAt(p);
        if (hit?.Tip == null || hit.Value.Id != hover) return;
        tipText = hit.Value.Tip;
        tip.Show(tipText, this, new Point(hit.Value.Box.X, hit.Value.Box.Bottom + P(6)), 9000);
    }

    void HideTip()
    {
        tipTimer.Stop();
        if (tipText == null) return;
        tipText = null;
        try { tip.Hide(this); } catch { }
    }

    // Keyboard: Space/Enter is the power button, H the hotspot. Tab walks through the settings (Space flips the
    // one with the ring, ←/→ pick an option), Esc lets go of them, a second Esc tucks the window away.
    static readonly string[] FocusOrder =
    {
        "sw:hotspot", "sw:kill", "sw:doh", "sw:auto", "netadd", "sw:startup", "sw:launch", "sw:notify", "sw:updates",
        "seg:anim", "splitedit", "seg:mtu", "forget", "reset", "star", "connect",
    };

    bool Focusable(string id) => id switch
    {
        "star" => StarVisible,
        "seg:anim" or "seg:mtu" => true,
        _ => Clickable(id),
    };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Tab or (Keys.Tab | Keys.Shift))
        {
            var order = FocusOrder.Where(Focusable).ToArray();
            if (order.Length == 0) return true;
            int dir = keyData == Keys.Tab ? 1 : -1;
            int i = keyboardFocus ? Array.IndexOf(order, focus) : -1;
            i = i < 0 ? (dir > 0 ? 0 : order.Length - 1) : (i + dir + order.Length) % order.Length;
            focus = order[i];
            keyboardFocus = true;
            HideTip();
            Invalidate();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            if (keyboardFocus && focus != null) Act(focus);
            else TogglePower();
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Left or Keys.Right && keyboardFocus && focus != null && SegmentsFor(focus) is Segments sg)
        {
            int i = Math.Max(0, Math.Min(sg.Options.Length - 1, sg.Get() + (e.KeyCode == Keys.Right ? 1 : -1)));
            if (i != sg.Get()) sg.Set(i);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.H) { ToggleHotspot(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape)
        {
            if (keyboardFocus) { keyboardFocus = false; Invalidate(); }
            else Close();
        }
    }

    // ---------------------------------------------------------------- animation

    void StartAnim()
    {
        if (!Visible || WindowState == FormWindowState.Minimized || anim.Enabled) return;
        frameClock.Restart();
        anim.Interval = 15;
        anim.Start();
    }

    /// Frame pacing: 30 fps while connecting, 25 fps connected (12 when the window isn't focused),
    /// a slow 15 fps drift when idle and focused, ~60 fps while a switch slides, nothing otherwise.
    void Animate()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) { anim.Stop(); return; }
        float dt = Math.Min(0.1f, (float)frameClock.Elapsed.TotalSeconds);
        frameClock.Restart();
        bool focused = ActiveForm == this;
        var mood = art.Current;
        bool motion = settings.Animations;
        bool artMoves = motion && (mood is VortexArt.Mood.On or VortexArt.Mood.Busy || (mood == VortexArt.Mood.Idle && focused));
        int interval = 0;
        if (artMoves || tunnel.Busy)
        {
            art.Step(dt, artMoves);
            Invalidate(rStage);
            interval = tunnel.Busy ? 33 : mood == VortexArt.Mood.On ? (focused ? 40 : 80) : 66;
        }
        // Switches and option highlights ease towards where they should be (or jump, with animations off).
        float k = motion ? 1f - (float)Math.Exp(-dt * 14f) : 1f;
        foreach (var sw in switches)
            if (Ease(ref sw.T, sw.Get() ? 1f : 0f, k)) { Invalidate(Rectangle.Inflate(sw.Knob, P(4), P(4))); interval = 15; }
        foreach (var sg in segments)
            if (Ease(ref sg.T, sg.Get(), k)) { Invalidate(Rectangle.Inflate(sg.Box, P(2), P(2))); interval = 15; }
        if (interval == 0) { anim.Stop(); return; }
        if (anim.Interval != interval) anim.Interval = interval;
    }

    /// Moves <paramref name="v"/> a step towards <paramref name="target"/>; false once it's there.
    static bool Ease(ref float v, float target, float k)
    {
        if (v == target) return false;
        v += (target - v) * k;
        if (Math.Abs(target - v) < 0.004f) v = target;
        return true;
    }

    void SnapWidgets()
    {
        foreach (var sw in switches) sw.T = sw.Get() ? 1f : 0f;
        foreach (var sg in segments) sg.T = sg.Get();
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        art.PaintSky(g, clip);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        if (clip.IntersectsWith(rStage))
        {
            art.PaintVortex(g);
            art.PaintOrb(g, hover == "power" && Clickable("power"));
        }
        if (clip.IntersectsWith(rSig)) PaintSignature(g);
        if (clip.IntersectsWith(rHeader)) PaintHeader(g);
        if (clip.IntersectsWith(rTitle) || clip.IntersectsWith(rDetail) || clip.IntersectsWith(rStats)) PaintStatus(g);
        if (clip.IntersectsWith(rStats)) PaintStats(g);
        if (clip.IntersectsWith(rDock)) PaintDock(g);
        if (clip.IntersectsWith(cHot)) PaintHotspot(g);
        if (clip.IntersectsWith(cProt)) PaintProtection(g);
        if (clip.IntersectsWith(cAuto)) PaintAutoConnect(g);
        if (clip.IntersectsWith(cApp)) PaintApp(g);
        if (clip.IntersectsWith(cSplit)) PaintSplit(g);
        if (clip.IntersectsWith(cAdv)) PaintAdvanced(g);
    }

    void FillRound(Graphics g, Rectangle r, float radius, Color c)
    {
        using var path = Draw.Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    /// The keyboard ring, around whatever Tab has reached.
    void PaintFocus(Graphics g, string id, Rectangle r, float radius = -1)
    {
        if (!keyboardFocus || focus != id) return;
        using var path = Draw.Round(r, radius < 0 ? Math.Min(P(14), r.Height / 2f) : radius);
        using var pen = new Pen(Draw.Alpha(T.Amber, 200), Math.Max(1.5f, 1.5f * S));
        g.DrawPath(pen, path);
    }

    void PaintCardTitle(Graphics g, Rectangle card, string title) =>
        Draw.Text(g, title, fonts.CardTitle, new Rectangle(card.X + P(16), card.Y + P(13), card.Width - P(32), P(22)), T.Text, TextFormatFlags.VerticalCenter);

    void PaintSwitchRow(Graphics g, Switch sw, bool label = true)
    {
        bool enabled = sw.Enabled(), hot = hover == "sw:" + sw.Id && enabled;
        var inner = Rectangle.Inflate(sw.Row, -P(6), -P(2));
        if (hot) FillRound(g, inner, P(12), T.SurfaceHover);
        if (label)
        {
            int x = sw.Row.X + P(16), w = sw.Knob.X - x - P(10);
            Color c = enabled ? T.Text : T.Faint;
            if (sw.Sub != null)
            {
                int mid = sw.Row.Y + sw.Row.Height / 2;
                Draw.Text(g, sw.Label, fonts.Body, new Rectangle(x, mid - P(19), w, P(20)), c, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                Draw.Text(g, sw.Sub, fonts.Small, new Rectangle(x, mid + P(1), w, P(17)), T.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else Draw.Text(g, sw.Label, fonts.Body, new Rectangle(x, sw.Row.Y, w, sw.Row.Height), c, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        Draw.Switch(g, sw.Knob, sw.T, enabled, hot, T, S);
        PaintFocus(g, "sw:" + sw.Id, inner);
    }

    void PaintSegments(Graphics g, Segments sg)
    {
        Draw.Text(g, sg.Label, fonts.Body, new Rectangle(sg.Row.X + P(16), sg.Row.Y, sg.Box.X - sg.Row.X - P(22), sg.Row.Height), T.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        FillRound(g, sg.Box, sg.Box.Height / 2f, Draw.Mix(T.Bg, T.Surface, 0.4f));
        float w = sg.Box.Width / (float)sg.Options.Length;
        var hl = new RectangleF(sg.Box.X + sg.T * w + P(2), sg.Box.Y + P(2), w - P(4), sg.Box.Height - P(4));
        using (var path = Draw.Round(hl, hl.Height / 2f))
        using (var b = new SolidBrush(T.Ring))
            g.FillPath(b, path);
        int sel = sg.Get();
        for (int i = 0; i < sg.Options.Length; i++)
        {
            bool hot = hover == "seg:" + sg.Id + ":" + i;
            Draw.Text(g, sg.Options[i], fonts.Small, sg.Option(i), i == sel ? Color.White : hot ? T.Text : T.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        PaintFocus(g, "seg:" + sg.Id, Rectangle.Inflate(sg.Box, P(3), P(3)));
    }

    void PaintHeader(Graphics g)
    {
        const string word = "WARP";
        int ww = TextRenderer.MeasureText(g, word, fonts.Brand, Size.Empty, TextFormatFlags.NoPadding).Width;
        Draw.GradientText(g, word, fonts.Brand, new RectangleF(rBrand.X, rBrand.Y, ww + P(8), rBrand.Height), T.Amber, T.Accent2, center: false);
        Draw.Text(g, "SWITCH", fonts.BrandThin, new Rectangle(rBrand.X + ww + P(6), rBrand.Y, P(120), rBrand.Height), T.Sub, TextFormatFlags.VerticalCenter);
        if (hover == "menu") FillRound(g, rMenu, P(10), T.SurfaceHover);
        float cx = rMenu.X + rMenu.Width / 2f, cy = rMenu.Y + rMenu.Height / 2f, d = P(3.6f), gap = P(6);
        using (var dot = new SolidBrush(hover == "menu" ? T.Text : T.Sub))
            for (int i = -1; i <= 1; i++) g.FillEllipse(dot, cx + i * gap - d / 2, cy - d / 2, d, d);
        if (UpdatePillVisible) PaintUpdatePill(g);
    }

    void PaintUpdatePill(Graphics g)
    {
        string label = updater.Busy
            ? (updater.Progress >= 0 && updater.Progress < 100 ? "Updating " + updater.Progress + "%" : "Installing…")
            : "Update " + updater.Available.ToString(3);
        bool hot = hover == "update" && !updater.Busy;
        using (var path = Draw.Round(rUpdate, rUpdate.Height / 2f))
        {
            using (var fill = new LinearGradientBrush(rUpdate, Draw.Alpha(T.Accent, hot ? 90 : 55), Draw.Alpha(T.Accent2, hot ? 90 : 55), 0f)) g.FillPath(fill, path);
            using var edge = new LinearGradientBrush(rUpdate, T.Amber, T.Accent2, 0f);
            using var pen = new Pen(edge, Math.Max(1f, S));
            g.DrawPath(pen, path);
        }
        Draw.Text(g, label, fonts.Label, rUpdate, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// The artist's signature, top right of the night sky, with a little flourish underneath.
    void PaintSignature(Graphics g)
    {
        var sig = new RectangleF(rSig.X, rSig.Y, rSig.Width, rSig.Height - P(4));
        Draw.GradientText(g, "Vortex", fonts.Signature, sig, Draw.Alpha(T.Amber, 210), Draw.Alpha(T.Accent3, 210));
        using var ink = new LinearGradientBrush(new RectangleF(sig.X, sig.Bottom - P(6), sig.Width, P(4)), Draw.Alpha(T.Accent, 0), Draw.Alpha(T.Accent3, 150), 0f);
        using var pen = new Pen(ink, Math.Max(1f, P(1.1f))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawBezier(pen, sig.X + P(8), sig.Bottom - P(1), sig.X + P(32), sig.Bottom + P(3), sig.Right - P(24), sig.Bottom - P(6), sig.Right - P(4), sig.Bottom);
    }

    void PaintStatus(Graphics g)
    {
        var st = tunnel.State;
        var (a, b) = st == TunnelState.Connected ? (T.Amber, T.Accent2)
            : tunnel.Busy ? (T.Amber, T.Accent)
            : tunnel.DetailIsError ? (T.Bad, T.Accent2)
            : (T.Text, Draw.Mix(T.Sub, T.Accent3, 0.4f));
        Draw.GradientText(g, Title(st), fonts.Title, rTitle, a, b);
        string detail = tunnel.Detail;
        if (string.IsNullOrEmpty(detail))
            detail = !ready ? "Getting ready…"
                : st == TunnelState.Connected ? (settings.KillSwitch ? "All apps go through WARP. Kill switch on." : "All apps on this PC are going through WARP.")
                : st == TunnelState.Off ? "Connect to send every app through WARP."
                : "";
        if (st == TunnelState.Connected && tunnel.KillSwitchWarning != null) detail = tunnel.KillSwitchWarning;
        // While off there are no stats to show, so a long message (why it gave up) gets their room too.
        var box = st == TunnelState.Off ? Rectangle.FromLTRB(rDetail.Left, rDetail.Top, rDetail.Right, rStats.Bottom) : rDetail;
        // TextBoxControl: a line that doesn't fully fit is dropped (with "…") instead of spilling onto the stats.
        Draw.Text(g, detail, fonts.Body, box, tunnel.DetailIsError ? T.Bad : T.Sub,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
    }

    /// Value over label, three across and two down, like a dashboard: no boxes, no rules.
    void PaintStats(Graphics g)
    {
        if (tunnel.State == TunnelState.Off) return;   // nothing but dashes; the status message uses the room
        bool on = tunnel.State == TunnelState.Connected;
        var tr = on ? tunnel.Trace : null;
        bool session = on || (tunnel.Busy && tunnel.Since != DateTime.MinValue);
        string[] labels = { "Public IP", "Exit", "Uptime", "Downloaded", "Uploaded", "Ping" };
        string[] values =
        {
            tr != null ? tr.Ip : "—",
            tr != null ? tr.Colo + " · " + tr.Loc : "—",
            on && tunnel.Since != DateTime.MinValue ? Uptime(DateTime.Now - tunnel.Since) : "—",
            session && usage.In > 0 ? Bytes(usage.In) : "—",
            session && usage.Out > 0 ? Bytes(usage.Out) : "—",
            on && tunnel.PingMs >= 0 ? tunnel.PingMs + " ms" : "—",
        };
        int colW = rStats.Width / 3;
        for (int i = 0; i < 6; i++)
        {
            int col = i % 3, x = rStats.X + col * colW, y = rStats.Y + i / 3 * P(44);
            var align = col == 0 ? TextFormatFlags.Left : col == 1 ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Right;
            Draw.Text(g, values[i], fonts.Value, new Rectangle(x, y, colW, P(22)), values[i] == "—" ? T.Faint : T.Text, align | TextFormatFlags.EndEllipsis);
            Draw.Text(g, labels[i], fonts.Small, new Rectangle(x, y + P(22), colW, P(18)), T.Sub, align);
        }
    }

    void PaintDock(Graphics g)
    {
        using (var path = Draw.Round(rDock, P(18)))
        {
            using (var b = new SolidBrush(Color.FromArgb(20, 255, 255, 255))) g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb(18, 255, 255, 255), 1f);
            g.DrawPath(pen, path);
        }
        var st = tunnel.State;
        bool on = st == TunnelState.Connected, busy = tunnel.Busy && st != TunnelState.Stopping;
        string net = DockNetwork;
        bool saved = settings.IsAutoConnectNetwork(net);

        var badge = new Rectangle(rDockRow.X, rDockRow.Y + P(2), P(36), P(36));
        using (var path = Draw.Round(badge, P(11)))
        {
            if (on) using (var b = new LinearGradientBrush(badge, T.Accent, T.Accent2, 45f)) g.FillPath(b, path);
            else using (var b = new SolidBrush(Color.FromArgb(busy ? 34 : 20, 255, 255, 255))) g.FillPath(b, path);
        }
        Draw.Wifi(g, RectangleF.Inflate(badge, -P(9), -P(9)), on ? Color.White : busy ? T.Amber : T.Sub, S);

        string sub;
        Color subColor = T.Sub;
        var route = tunnel.Route;
        if (on && route != null)
            sub = route.Short + (tunnel.OnBackupServer ? " · backup server" : "") + (tunnel.Reroutes > 0 ? " · rerouted " + tunnel.Reroutes + "×" : "");
        else if (busy && route != null) sub = "Trying " + route.Short + (tunnel.OnBackupServer ? " · backup server" : "");
        else if (busy) sub = "Scouting routes…";
        else if (string.IsNullOrEmpty(net)) sub = ready ? "Join a Wi-Fi network or plug in a cable" : "";
        else if (saved) sub = settings.AutoConnect ? "Saved · WARP turns on here by itself" : "Saved · auto-connect is paused";
        else { sub = "Not protected · ☆ to auto-connect here"; subColor = T.Faint; }

        int tx = badge.Right + P(12), tw = rStar.X - tx - P(6);
        Draw.Text(g, string.IsNullOrEmpty(net) ? (ready ? "No network" : "Looking for the network…") : net, fonts.CardTitle,
            new Rectangle(tx, rDockRow.Y, tw, P(21)), T.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Draw.Text(g, sub, fonts.Small, new Rectangle(tx, rDockRow.Y + P(21), tw, P(18)), subColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (StarVisible)
        {
            bool hot = hover == "star";
            if (hot) using (var b = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillEllipse(b, rStar);
            Draw.Star(g, rStar.X + rStar.Width / 2f, rStar.Y + rStar.Height / 2f + P(1), P(9), saved ? T.Amber : hot ? T.Text : T.Sub, saved, Math.Max(1.2f, P(1.4f)));
            PaintFocus(g, "star", rStar, rStar.Height / 2f);
        }

        string label = !ready ? "Connect" : st switch
        {
            TunnelState.Off => "Connect",
            TunnelState.Starting or TunnelState.Connecting => "Cancel",
            TunnelState.Stopping => "Disconnecting…",
            _ => "Disconnect",
        };
        Draw.Pill(g, rConnect, label, fonts.Button, T, primary: st == TunnelState.Off, hot: hover == "connect", enabled: Clickable("connect"));
        PaintFocus(g, "connect", Rectangle.Inflate(rConnect, P(3), P(3)));
    }

    void PaintHotspot(Graphics g)
    {
        bool on = hotspot.On, busy = hotspot.Busy || hotspot.Switching;
        bool warpUp = tunnel.State == TunnelState.Connected;
        Draw.Card(g, cHot, P(18), T);
        bool hot = hover == "sw:hotspot" && Clickable("sw:hotspot");
        var head = Rectangle.Inflate(rHotHead, -P(6), -P(6));
        if (hot) FillRound(g, head, P(14), T.SurfaceHover);

        var icon = new RectangleF(cHot.X + P(16), cHot.Y + P(16), P(36), P(36));
        if (on)
            using (var b = new LinearGradientBrush(icon, T.Accent, T.Accent2, 45f)) g.FillEllipse(b, icon);
        else
            using (var b = new SolidBrush(Color.FromArgb(20, 255, 255, 255))) g.FillEllipse(b, icon);
        Draw.Wifi(g, RectangleF.Inflate(icon, -P(9), -P(9)), on ? Color.White : T.Sub, S);

        int tx = cHot.X + P(64), tw = swHotspot.Knob.X - tx - P(8);
        const string title = "Mobile hotspot";
        Draw.Text(g, title, fonts.CardTitle, new Rectangle(tx, cHot.Y + P(14), tw, P(22)), T.Text, TextFormatFlags.VerticalCenter);
        if (on && hotspot.ViaWarp)
        {
            int ttw = TextRenderer.MeasureText(g, title, fonts.CardTitle, Size.Empty, TextFormatFlags.NoPadding).Width;
            var pill = new Rectangle(tx + ttw + P(8), cHot.Y + P(17), P(44), P(17));
            using (var path = Draw.Round(pill, pill.Height / 2f))
            using (var b = new LinearGradientBrush(pill, T.Accent, T.Accent2, 0f))
                g.FillPath(b, path);
            Draw.Text(g, "WARP", fonts.Label, pill, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        string sub;
        Color subColor = T.Sub;
        if (!ready) sub = "";
        else if (!hotspot.Available) sub = hotspot.Problem ?? "Needs an internet connection";
        else if (busy) sub = "Switching…";
        else if (hotspot.Error != null) { sub = hotspot.Error; subColor = T.Bad; }
        else if (on) sub = Devices(hotspot.Clients) + (hotspot.ViaWarp ? " connected" : warpUp ? " · not via WARP yet" : " · normal internet");
        else sub = warpUp ? "Devices that join will use WARP" : "Shares this PC's internet";
        Draw.Text(g, sub, fonts.Small, new Rectangle(tx, cHot.Y + P(37), tw, P(18)), subColor, TextFormatFlags.EndEllipsis);

        Draw.Switch(g, swHotspot.Knob, swHotspot.T, ready && hotspot.Available && !busy, hot, T, S);
        PaintFocus(g, "sw:hotspot", head);

        using (var pen = new Pen(T.Line, 1f)) g.DrawLine(pen, cHot.X + P(16), cHot.Y + P(70), cHot.Right - P(16), cHot.Y + P(70));
        if (hotspot.Ssid == null)
        {
            if (ready) Draw.Text(g, "Turn it on to get a name and password.", fonts.Small, rHotName, T.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        Draw.Text(g, "Name", fonts.Small, new Rectangle(rHotName.X, rHotName.Y, P(72), rHotName.Height), T.Sub, TextFormatFlags.VerticalCenter);
        Draw.Text(g, hotspot.Ssid, fonts.Body, new Rectangle(rPassValue.X, rHotName.Y, rHotName.Right - rPassValue.X, rHotName.Height), T.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var row = new Rectangle(cHot.X + P(16), rPassValue.Y, cHot.Width - P(32), rPassValue.Height);
        Draw.Text(g, "Password", fonts.Small, new Rectangle(row.X, row.Y, P(72), row.Height), T.Sub, TextFormatFlags.VerticalCenter);
        string secret = hotspot.Passphrase ?? "";
        string shown = revealPass ? secret : new string('•', Math.Max(8, Math.Min(12, secret.Length)));
        Draw.Text(g, shown, fonts.Body, rPassValue, hover == "copy" ? T.Amber : T.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        bool copied = DateTime.UtcNow < copiedUntil;
        if (secret.Length > 0)
            Draw.Text(g, "QR", fonts.Small, rQrLink, hover == "qr" ? T.Amber : T.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        string link = copied ? "Copied" : revealPass ? "Hide" : "Show";
        Draw.Text(g, link, fonts.Small, rPassLink, copied ? T.Good : hover == "reveal" ? T.Amber : T.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    void PaintProtection(Graphics g)
    {
        Draw.Card(g, cProt, P(18), T);
        PaintCardTitle(g, cProt, "Protection");
        PaintSwitchRow(g, swKill);
        PaintSwitchRow(g, swDoh);
    }

    void PaintApp(Graphics g)
    {
        Draw.Card(g, cApp, P(18), T);
        PaintCardTitle(g, cApp, "App");
        foreach (var sw in new[] { swStartup, swLaunch, swNotify, swUpdates }) PaintSwitchRow(g, sw);
        PaintSegments(g, segAnim);
    }

    void PaintAutoConnect(Graphics g)
    {
        Draw.Card(g, cAuto, P(18), T);
        PaintSwitchRow(g, swAuto, label: false);
        Draw.Text(g, "Auto-connect", fonts.CardTitle, new Rectangle(cAuto.X + P(16), swAuto.Row.Y, P(160), swAuto.Row.Height), T.Text, TextFormatFlags.VerticalCenter);
        Draw.Text(g, settings.AutoConnect ? "WARP turns on by itself when you join one of these networks." : "Paused: WARP won't turn on by itself on these networks.",
            fonts.Small, rAutoSub, T.Faint, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);

        var nets = settings.AutoConnectNetworks;
        if (nets.Count == 0)
        {
            var box = Rectangle.Inflate(rNetList, -P(6), -P(4));
            using (var path = Draw.Round(box, P(12)))
            using (var pen = new Pen(Color.FromArgb(34, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash })
                g.DrawPath(pen, path);
            Draw.Text(g, "No saved networks yet.\nAdd one, or click ☆ in the dock.", fonts.Small, Rectangle.Inflate(box, -P(12), 0), T.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
        else
        {
            var state = g.Save();
            g.SetClip(rNetList, CombineMode.Intersect);
            for (int i = 0; i < nets.Count; i++)
            {
                var row = NetRow(i);
                if (!row.IntersectsWith(rNetList)) continue;
                bool here = string.Equals(nets[i], DockNetwork, StringComparison.OrdinalIgnoreCase);
                bool rowHot = hover == "net:" + i || hover == "netx:" + i;
                var inner = Rectangle.Inflate(row, 0, -P(2));
                if (rowHot) FillRound(g, inner, P(10), T.SurfaceHover);
                Draw.Wifi(g, new RectangleF(row.X + P(10), row.Y + (row.Height - P(18)) / 2f, P(18), P(18)), here ? T.Good : T.Sub, S, 1.6f);
                var x = NetRemove(row);
                int nx = row.X + P(38), nw = x.X - nx - P(6);
                string name = nets[i];
                if (here)
                {
                    const string tag = "this network";
                    int tagW = TextRenderer.MeasureText(g, tag, fonts.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
                    int nameW = Math.Min(nw - tagW - P(8), TextRenderer.MeasureText(g, name, fonts.Body, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + P(2));
                    Draw.Text(g, name, fonts.Body, new Rectangle(nx, row.Y, Math.Max(P(20), nameW), row.Height), T.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    Draw.Text(g, tag, fonts.Small, new Rectangle(nx + Math.Max(P(20), nameW) + P(8), row.Y, tagW + P(2), row.Height), T.Good, TextFormatFlags.VerticalCenter);
                }
                else Draw.Text(g, name, fonts.Body, new Rectangle(nx, row.Y, nw, row.Height), T.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                bool xHot = hover == "netx:" + i;
                if (xHot) using (var b = new SolidBrush(Color.FromArgb(24, 255, 255, 255))) g.FillEllipse(b, x);
                Draw.Cross(g, RectangleF.Inflate(x, -P(8), -P(8)), xHot ? T.Text : rowHot ? T.Sub : T.Faint, Math.Max(1.2f, P(1.5f)));
            }
            g.Restore(state);
            if (NetScrollMax > 0)
            {
                // A slim scroll thumb so it's clear there's more.
                float total = nets.Count * P(NetRowHeight), frac = rNetList.Height / total;
                float th = Math.Max(P(18), rNetList.Height * frac), ty = rNetList.Y + (rNetList.Height - th) * (netScroll / (float)NetScrollMax);
                FillRound(g, new Rectangle(cAuto.Right - P(7), (int)ty, P(3), (int)th), P(1.5f), Color.FromArgb(50, 255, 255, 255));
            }
        }

        bool hot = hover == "netadd";
        FillRound(g, rNetAdd, rNetAdd.Height / 2f, Color.FromArgb(hot ? 34 : 18, 255, 255, 255));
        const string add = "Add network";
        int aw = TextRenderer.MeasureText(g, add, fonts.Body, Size.Empty, TextFormatFlags.NoPadding).Width;
        int ax = rNetAdd.X + (rNetAdd.Width - aw - P(18)) / 2;
        Draw.Plus(g, ax + P(5), rNetAdd.Y + rNetAdd.Height / 2f, P(5), T.Amber, Math.Max(1.4f, P(1.8f)));
        Draw.Text(g, add, fonts.Body, new Rectangle(ax + P(18), rNetAdd.Y, aw + P(4), rNetAdd.Height), T.Text, TextFormatFlags.VerticalCenter);
        PaintFocus(g, "netadd", Rectangle.Inflate(rNetAdd, P(3), P(3)));
    }

    void PaintSplit(Graphics g)
    {
        Draw.Card(g, cSplit, P(18), T);
        PaintCardTitle(g, cSplit, "Skip WARP for");
        Draw.Text(g, "Edit", fonts.Small, rSplitEdit, hover == "splitedit" ? T.Amber : T.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        PaintFocus(g, "splitedit", Rectangle.Inflate(rSplitEdit, P(4), 0));
        if (settings.Bypass.Count == 0)
            Draw.Text(g, "Addresses, ranges or host names that go straight out your normal connection.", fonts.Small,
                new Rectangle(rChips.X, rChips.Y, rChips.Width, P(36)), T.Faint, TextFormatFlags.WordBreak);
        foreach (var c in Chips())
        {
            bool hot = hover == (c.Kind == "entry" ? "chip:" + c.Index : c.Kind == "add" ? "chipadd" : "chipmore") || hover == "chipx:" + c.Index && c.Kind == "entry";
            FillRound(g, c.Box, c.Box.Height / 2f, hot ? Draw.Mix(T.Chip, Color.White, 0.07f) : T.Chip);
            switch (c.Kind)
            {
                case "entry":
                    Draw.Text(g, c.Text, fonts.Small, new Rectangle(c.Box.X + P(12), c.Box.Y, c.Box.Width - P(12 + 22), c.Box.Height), T.Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    bool xHot = hover == "chipx:" + c.Index;
                    Draw.Cross(g, RectangleF.Inflate(c.X, -P(7), -P(8.5f)), xHot ? T.Text : T.Faint, Math.Max(1.1f, P(1.3f)));
                    break;
                case "add":
                    Draw.Plus(g, c.Box.X + P(17), c.Box.Y + c.Box.Height / 2f, P(4.5f), T.Amber, Math.Max(1.3f, P(1.6f)));
                    Draw.Text(g, c.Text, fonts.Small, new Rectangle(c.Box.X + P(28), c.Box.Y, c.Box.Width - P(28), c.Box.Height), T.Text, TextFormatFlags.VerticalCenter);
                    break;
                default:
                    Draw.Text(g, c.Text, fonts.Small, c.Box, T.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    break;
            }
        }
    }

    void PaintAdvanced(Graphics g)
    {
        Draw.Card(g, cAdv, P(18), T);
        PaintCardTitle(g, cAdv, "Advanced");
        PaintSegments(g, segMtu);
        bool forgot = DateTime.UtcNow < forgotUntil, reset = DateTime.UtcNow < resetUntil;
        Draw.Pill(g, rForget, forgot ? "Forgotten ✓" : "Forget routes", fonts.Small, T, false, hover == "forget", true);
        Draw.Pill(g, rReset, reset ? "Done ✓" : "Reset registration", fonts.Small, T, false, hover == "reset", true);
        PaintFocus(g, "forget", Rectangle.Inflate(rForget, P(3), P(3)));
        PaintFocus(g, "reset", Rectangle.Inflate(rReset, P(3), P(3)));
    }

    static string Uptime(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";

    internal static string Bytes(long n)
    {
        if (n < 1024 * 1024) return Math.Max(1, (n + 512) / 1024) + " KB";
        double mb = n / (1024.0 * 1024);
        if (mb < 1024) return (mb < 100 ? mb.ToString("0.0") : mb.ToString("0")) + " MB";
        double gb = mb / 1024;
        return (gb < 100 ? gb.ToString("0.00") : gb.ToString("0.0")) + " GB";
    }

    static string Devices(int n) => n == 1 ? "1 device" : n + " devices";

    // ---------------------------------------------------------------- previews (tests and README screenshots)

    /// Renders the window in a made-up state without touching the network. Used by the test project.
    internal Bitmap RenderPreview(Action<Tunnel, Hotspot, Updater> arrange, (long In, long Out) data, Action<Settings> configure = null, string hoverTarget = "")
    {
        ready = true;
        startupOn = true;
        arrange(tunnel, hotspot, updater);
        configure?.Invoke(settings);
        usage = data;
        currentNetwork = tunnel.Network;
        hover = hoverTarget;
        chipCache = null;
        var mood = tunnel.State == TunnelState.Connected ? VortexArt.Mood.On : tunnel.Busy ? VortexArt.Mood.Busy : tunnel.DetailIsError ? VortexArt.Mood.Error : VortexArt.Mood.Idle;
        art.SetMood(mood);
        art.Step(0.6f, true);
        SnapWidgets();
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var g = Graphics.FromImage(bmp)) OnPaint(new PaintEventArgs(g, new Rectangle(Point.Empty, ClientSize)));
        return bmp;
    }

    internal Settings PreviewSettings => settings;

    /// For tests: clicks the middle of the target with this id, the way the mouse would (through hit-testing).
    internal bool PreviewClick(string id)
    {
        foreach (var hit in Hits())
        {
            if (hit.Id != id) continue;
            var p = new Point(hit.Box.X + hit.Box.Width / 2, hit.Box.Y + hit.Box.Height / 2);
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
            return true;
        }
        return false;
    }
}
