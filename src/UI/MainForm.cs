using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace WarpSwitch;

/// The whole app: one power button for system-wide WARP, one switch for the Mobile Hotspot, set inside
/// an animated vortex. Everything is painted by hand (no controls); the animation only runs while the
/// window is on screen (slower when it isn't focused), so the app sits at ~0% CPU in the tray.
sealed class MainForm : Form
{
    const int W = 360, H = 684;
    const int DropNoticeSeconds = 20;

    readonly Settings settings;
    readonly Tunnel tunnel;
    readonly Hotspot hotspot = new();
    readonly Updater updater = new();
    readonly Fonts fonts = new();
    readonly Theme T = Theme.Current();
    readonly VortexArt art;
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip trayMenu = new(), appMenu = new();
    readonly ToolStripMenuItem miConnect, miHotspot, miCheckUpdates;
    readonly Timer anim = new() { Interval = 33 };
    readonly Timer poll = new() { Interval = 3000 };
    readonly Timer clock = new() { Interval = 1000 };
    readonly Timer updateTimer = new() { Interval = 60 * 60 * 1000 };
    readonly Stopwatch frameClock = Stopwatch.StartNew();
    readonly float S;
    readonly bool preview;
    Icon icoOff, icoBusy, icoOn;

    bool allowShow, exiting, ready, revealPass, hotspotWasViaWarp, dropNotified, userStopped, updateNotified;
    int hotspotPolling;
    float toggleT;
    int rebinds;
    DateTime lastRebind = DateTime.MinValue, copiedUntil = DateTime.MinValue, droppedAt = DateTime.MinValue, lastUpdateCheck = DateTime.MinValue;
    TunnelState lastState;
    string hover = "", declinedNetwork;
    LogForm logForm;
    SettingsForm settingsForm;
    QrForm qrForm;
    (long In, long Out) usage;

    // Layout, in device pixels.
    Rectangle rStage, rMenu, rUpdate, rBrand, rTitle, rDetail, rInfo, rHot, rHotHead, rToggle, rPassValue, rQrLink, rPassLink, rFooter;
    PointF center;
    float orbRadius;

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
        S = DeviceDpi / 96f;
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
        LayoutTargets();

        miConnect = new ToolStripMenuItem("Connect", null, (s, e) => TogglePower());
        miHotspot = new ToolStripMenuItem("Mobile hotspot", null, (s, e) => ToggleHotspot());
        trayMenu.Items.AddRange(new ToolStripItem[]
        {
            miConnect, miHotspot, new ToolStripSeparator(),
            new ToolStripMenuItem("Open " + Program.Name, null, (s, e) => ShowFromTray()),
            new ToolStripMenuItem("Settings…", null, (s, e) => { ShowFromTray(); ShowSettings(); }),
            new ToolStripMenuItem("Quit", null, (s, e) => ExitApp()),
        });
        trayMenu.Opening += (s, e) => UpdateMenus();

        miCheckUpdates = new ToolStripMenuItem("Check for updates", null, (s, e) => CheckForUpdates(manual: true));
        appMenu.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("Settings…", null, (s, e) => ShowSettings()),
            new ToolStripMenuItem("Show log", null, (s, e) => ShowLog()),
            new ToolStripMenuItem("Open app folder", null, (s, e) => OpenFolder()),
            miCheckUpdates,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit " + Program.Name, null, (s, e) => ExitApp()),
            new ToolStripSeparator(),
            new ToolStripMenuItem(Program.Name + " " + Program.Version + "  ·  made by Vortex") { Enabled = false },
        });
        appMenu.Opening += (s, e) => UpdateMenus();

        trayMenu.Renderer = new MenuRenderer(T);
        appMenu.Renderer = new MenuRenderer(T);
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
        clock.Tick += (s, e) => { usage = tunnel.Usage(); Invalidate(rInfo); };
        updateTimer.Tick += (s, e) => CheckForUpdates(manual: false);

        CreateHandle();
        if (preview) return;

        tray.Visible = true;
        Native.AllowMessage(Handle, Program.ShowMessage);
        Native.AllowTaskbarCreated();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Program.EmergencyCleanup = tunnel.ShutdownNow;
        poll.Start();
        updateTimer.Start();
        OnTunnelChanged();
        BeginInvoke(new Action(InitAsync));
    }

    int P(float v) => (int)Math.Round(v * S);
    Rectangle R(float x, float y, float w, float h) => new(P(x), P(y), P(w), P(h));
    void Ui(Action a) { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(a); } catch (InvalidOperationException) { } }

    void LayoutTargets()
    {
        rBrand = R(22, 12, 240, 34);
        rMenu = R(W - 24 - 30, 14, 30, 30);
        rUpdate = R(W - 24 - 30 - 8 - 100, 18, 100, 22);
        center = new PointF(P(W / 2f), P(190));
        orbRadius = P(58);
        float arms = P(134);
        int reach = (int)arms + P(4);
        rStage = new Rectangle((int)center.X - reach, (int)center.Y - reach, 2 * reach, 2 * reach);
        rTitle = R(20, 328, W - 40, 34);
        rDetail = R(28, 363, W - 56, 40);
        rInfo = R(20, 408, W - 40, 124);
        rHot = R(20, 542, W - 40, 104);
        rToggle = new Rectangle(rHot.Right - P(16 + 44), rHot.Y + P(22), P(44), P(24));
        rHotHead = new Rectangle(rHot.X, rHot.Y, rHot.Width, P(64));
        rPassValue = new Rectangle(rHot.X + P(92), rHot.Y + P(72), P(124), P(22));
        rPassLink = new Rectangle(rHot.Right - P(16 + 44), rHot.Y + P(72), P(44), P(22));
        rQrLink = new Rectangle(rPassLink.X - P(8 + 30), rHot.Y + P(72), P(30), P(22));
        rFooter = R(20, 650, W - 40, 32);
        art.Layout(ClientSize, center, orbRadius, arms);
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
        Native.StyleTitleBar(Handle, true, T.BgTop);
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
        if (!Visible) { clock.Stop(); anim.Stop(); return; }
        clock.Enabled = tunnel.State == TunnelState.Connected;
        StartAnim();
        PollHotspot();
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); StartAnim(); }
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
        settingsForm?.Close();
        qrForm?.Close();
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
            trayMenu.Dispose(); appMenu.Dispose();
            anim.Dispose(); poll.Dispose(); clock.Dispose(); updateTimer.Dispose();
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
        Invalidate(rHot);
    }

    void OnUpdaterChanged()
    {
        Invalidate(new Rectangle(0, 0, ClientSize.Width, P(50)));
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
        // WinRT and adapter enumeration can take a moment: keep them off the UI thread so the vortex doesn't stutter.
        Task.Run(() => hotspot.Refresh(tun)).ContinueWith(_ => Ui(() =>
        {
            hotspotPolling = 0;
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

    // ---------------------------------------------------------------- auto-connect on untrusted networks

    void OnIdleNetwork(string network)
    {
        if (network != declinedNetwork) declinedNetwork = null;
        MaybeAutoConnect(network);
    }

    async void MaybeAutoConnect(string network)
    {
        if (!ready || !settings.AutoConnectUntrusted || tunnel.State != TunnelState.Off || string.IsNullOrEmpty(network)) return;
        if (settings.IsTrusted(network) || network == declinedNetwork) return;
        tunnel.Log.Add("· connecting by itself: " + network + " isn't a trusted network");
        await tunnel.ConnectAsync();
    }

    // ---------------------------------------------------------------- actions

    async void TogglePower()
    {
        if (!ready) return;
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
        if (!ready || hotspot.Busy || !hotspot.Available) return;
        string tun = tunnel.State == TunnelState.Connected ? tunnel.TunId : null;
        bool turnOn = !hotspot.On;
        lastRebind = DateTime.UtcNow;
        await hotspot.SetAsync(turnOn, tun);
        hotspotWasViaWarp = turnOn && tun != null && hotspot.On;
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

    void ShowSettings()
    {
        if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
        settingsForm = new SettingsForm(settings, T, Icon, tunnel, ApplySettings);
        settingsForm.Show(this);
    }

    /// Called by the settings window after it saved.
    void ApplySettings()
    {
        StartAnim();
        Invalidate();
        updateTimer.Enabled = settings.CheckUpdates;
        _ = tunnel.ApplySettingsAsync();
        if (tunnel.State == TunnelState.Off) MaybeAutoConnect(Tunnel.CurrentNetwork());
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

    string HitTest(Point p)
    {
        float dx = p.X - center.X, dy = p.Y - center.Y;
        if (dx * dx + dy * dy <= orbRadius * orbRadius) return "power";
        if (rMenu.Contains(p)) return "menu";
        if (UpdatePillVisible && rUpdate.Contains(p)) return "update";
        if (rToggle.Contains(p) || rHotHead.Contains(p)) return "hotspot";
        bool hasPass = hotspot.Ssid != null && !string.IsNullOrEmpty(hotspot.Passphrase);
        if (hotspot.Ssid != null && rPassLink.Contains(p)) return "reveal";
        if (hasPass && rQrLink.Contains(p)) return "qr";
        if (hasPass && rPassValue.Contains(p)) return "copy";
        return "";
    }

    bool Clickable(string target) => target switch
    {
        "power" => ready && tunnel.State != TunnelState.Stopping,
        "hotspot" => ready && hotspot.Available && !hotspot.Busy,
        "update" => !updater.Busy,
        "" => false,
        _ => true,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string h = HitTest(e.Location);
        if (h == hover) return;
        hover = h;
        Cursor = Clickable(h) ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover.Length == 0) return;
        hover = "";
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        switch (HitTest(e.Location))
        {
            case "power": TogglePower(); break;
            case "menu": appMenu.Show(this, new Point(rMenu.Right, rMenu.Bottom + P(4)), ToolStripDropDownDirection.BelowLeft); break;
            case "update": if (!updater.Busy) ConfirmUpdate(); break;
            case "hotspot": ToggleHotspot(); break;
            case "reveal": revealPass = !revealPass; Invalidate(rHot); break;
            case "qr": ShowQr(); break;
            case "copy":
                try { Clipboard.SetText(hotspot.Passphrase); } catch { }
                copiedUntil = DateTime.UtcNow.AddSeconds(1.6);
                Invalidate(rHot);
                Task.Delay(1700).ContinueWith(_ => Ui(() => Invalidate(rHot)));
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { TogglePower(); e.Handled = true; }
        else if (e.KeyCode == Keys.H) { ToggleHotspot(); e.Handled = true; }
        else if (e.KeyCode == Keys.Oemcomma && e.Control) { ShowSettings(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) Close();
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
    /// a slow 15 fps drift when idle and focused, nothing otherwise.
    void Animate()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) { anim.Stop(); return; }
        float dt = Math.Min(0.1f, (float)frameClock.Elapsed.TotalSeconds);
        frameClock.Restart();
        bool focused = ActiveForm == this;
        var mood = art.Current;
        bool artMoves = settings.Animations && (mood is VortexArt.Mood.On or VortexArt.Mood.Busy || (mood == VortexArt.Mood.Idle && focused));
        int interval = 0;
        if (artMoves || tunnel.Busy)
        {
            art.Step(dt, artMoves);
            Invalidate(rStage);
            interval = tunnel.Busy ? 33 : mood == VortexArt.Mood.On ? (focused ? 40 : 80) : 66;
        }
        float target = hotspot.On ? 1f : 0f;
        if (toggleT != target)
        {
            toggleT += (target - toggleT) * 0.28f;
            if (Math.Abs(target - toggleT) < 0.005f) toggleT = target;
            Invalidate(rToggle);
            interval = 15;
        }
        if (interval == 0) { anim.Stop(); return; }
        if (anim.Interval != interval) anim.Interval = interval;
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
        if (clip.Y < P(50)) PaintHeader(g);
        if (clip.IntersectsWith(rTitle) || clip.IntersectsWith(rDetail)) PaintStatus(g);
        if (clip.IntersectsWith(rInfo)) PaintInfo(g);
        if (clip.IntersectsWith(rHot)) PaintHotspot(g);
        if (clip.IntersectsWith(rFooter)) PaintFooter(g);
    }

    void PaintHeader(Graphics g)
    {
        const string word = "WARP";
        int ww = TextRenderer.MeasureText(g, word, fonts.Brand, Size.Empty, TextFormatFlags.NoPadding).Width;
        Draw.GradientText(g, word, fonts.Brand, new RectangleF(rBrand.X, rBrand.Y, ww + P(8), rBrand.Height), T.Amber, T.Accent2, center: false);
        Draw.Text(g, "SWITCH", fonts.BrandThin, new Rectangle(rBrand.X + ww + P(6), rBrand.Y, P(120), rBrand.Height), T.Sub, TextFormatFlags.VerticalCenter);
        if (hover == "menu") using (var b = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillEllipse(b, rMenu);
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
                : st == TunnelState.Off ? "Tap the orb to send every app through WARP."
                : "";
        if (st == TunnelState.Connected && tunnel.KillSwitchWarning != null) detail = tunnel.KillSwitchWarning;
        // TextBoxControl: a line that doesn't fully fit is dropped (with "…") instead of spilling onto the card below.
        Draw.Text(g, detail, fonts.Body, rDetail, tunnel.DetailIsError ? T.Bad : T.Sub,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
    }

    void PaintInfo(Graphics g)
    {
        bool on = tunnel.State == TunnelState.Connected;
        Draw.Glass(g, rInfo, P(16), T, on ? Color.FromArgb(18, T.Accent) : default);
        var tr = on ? tunnel.Trace : null;
        bool session = on || (tunnel.Busy && tunnel.Since != DateTime.MinValue);
        PaintStatRow(g, rInfo.Y + P(10), new[] { "PUBLIC IP", "EXIT", "UPTIME" }, new[]
        {
            tr != null ? tr.Ip : "—",
            tr != null ? tr.Colo + " · " + tr.Loc : "—",
            on && tunnel.Since != DateTime.MinValue ? Uptime(DateTime.Now - tunnel.Since) : "—",
        });
        PaintStatRow(g, rInfo.Y + P(52), new[] { "DOWNLOADED", "UPLOADED", "PING" }, new[]
        {
            session && usage.In > 0 ? Bytes(usage.In) : "—",
            session && usage.Out > 0 ? Bytes(usage.Out) : "—",
            on && tunnel.PingMs >= 0 ? tunnel.PingMs + " ms" : "—",
        });
        using (var sep = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
            g.DrawLine(sep, rInfo.X + P(16), rInfo.Y + P(98), rInfo.Right - P(16), rInfo.Y + P(98));

        // Which way through the network we're using right now.
        var route = tunnel.Route;
        string line;
        Color dotColor;
        if (on && route != null)
        {
            line = tunnel.Network + "  →  " + route.Short + (tunnel.OnBackupServer ? " · backup server" : "")
                + (tunnel.Reroutes > 0 ? " · rerouted " + tunnel.Reroutes + "×" : "");
            dotColor = T.Good;
        }
        else if (tunnel.Busy && route != null)
        {
            line = (tunnel.Network ?? "") + "  →  trying " + route.Short + (tunnel.OnBackupServer ? " · backup server" : "");
            dotColor = T.Amber;
        }
        else if (tunnel.Busy)
        {
            line = "Scouting routes…";
            dotColor = T.Amber;
        }
        else
        {
            line = "Finds the best route on every network by itself";
            dotColor = T.Faint;
        }
        float dd = P(6);
        var row = new Rectangle(rInfo.X + P(30), rInfo.Y + P(101), rInfo.Width - P(46), P(20));
        using (var b = new SolidBrush(dotColor)) g.FillEllipse(b, rInfo.X + P(17), row.Y + row.Height / 2f - dd / 2, dd, dd);
        Draw.Text(g, line, fonts.Small, row, on ? T.Sub : T.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    void PaintStatRow(Graphics g, int y, string[] labels, string[] values)
    {
        int[] widths = { P(130), P(96), rInfo.Width - P(226) };
        int x = rInfo.X;
        using var sep = new Pen(Color.FromArgb(26, 255, 255, 255), 1f);
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) g.DrawLine(sep, x, y + P(3), x, y + P(35));
            Draw.Text(g, labels[i], fonts.Label, new Rectangle(x + P(4), y, widths[i] - P(8), P(14)), T.Faint, TextFormatFlags.HorizontalCenter);
            Draw.Text(g, values[i], fonts.Value, new Rectangle(x + P(4), y + P(15), widths[i] - P(8), P(22)), values[i] == "—" ? T.Faint : T.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            x += widths[i];
        }
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

    void PaintHotspot(Graphics g)
    {
        bool on = hotspot.On, busy = hotspot.Busy || hotspot.Switching;
        bool warpUp = tunnel.State == TunnelState.Connected;
        Draw.Glass(g, rHot, P(16), T, on && hotspot.ViaWarp ? Color.FromArgb(16, T.Accent2) : default);

        var icon = new RectangleF(rHot.X + P(16), rHot.Y + P(16), P(34), P(34));
        if (on)
            using (var b = new LinearGradientBrush(icon, Draw.Alpha(T.Accent, 90), Draw.Alpha(T.Accent2, 90), 45f)) g.FillEllipse(b, icon);
        else
            using (var b = new SolidBrush(Color.FromArgb(20, 255, 255, 255))) g.FillEllipse(b, icon);
        PaintHotspotGlyph(g, icon, on ? Color.White : T.Sub);

        int tx = rHot.X + P(62);
        const string title = "Mobile hotspot";
        Draw.Text(g, title, fonts.CardTitle, new Rectangle(tx, rHot.Y + P(14), rToggle.X - tx - P(8), P(22)), T.Text, TextFormatFlags.VerticalCenter);
        if (on && hotspot.ViaWarp)
        {
            int tw = TextRenderer.MeasureText(g, title, fonts.CardTitle, Size.Empty, TextFormatFlags.NoPadding).Width;
            var pill = new Rectangle(tx + tw + P(8), rHot.Y + P(17), P(44), P(17));
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
        else if (on) sub = (hotspot.Ssid ?? "Hotspot") + " · " + Devices(hotspot.Clients) + (hotspot.ViaWarp ? "" : warpUp ? " · not via WARP yet" : " · normal internet");
        else sub = warpUp ? "Off. Devices that join will use WARP" : "Off. Shares this PC's internet";
        Draw.Text(g, sub, fonts.Small, new Rectangle(tx, rHot.Y + P(37), rToggle.X - tx - P(6), P(18)), subColor, TextFormatFlags.EndEllipsis);

        PaintToggle(g, rToggle, toggleT, ready && hotspot.Available && !busy);

        using (var pen = new Pen(Color.FromArgb(26, 255, 255, 255), 1f)) g.DrawLine(pen, rHot.X + P(16), rHot.Y + P(64), rHot.Right - P(16), rHot.Y + P(64));
        var row = new Rectangle(rHot.X + P(16), rHot.Y + P(72), rHot.Width - P(32), P(22));
        if (hotspot.Ssid == null)
        {
            if (ready) Draw.Text(g, "A name and password are created when you turn it on.", fonts.Small, row, T.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
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

    void PaintHotspotGlyph(Graphics g, RectangleF box, Color c)
    {
        float cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2 + P(5);
        using var pen = new Pen(c, P(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (float rad in new float[] { P(6f), P(11f) })
            g.DrawArc(pen, cx - rad, cy - rad, 2 * rad, 2 * rad, 225, 90);
        float d = P(4f);
        using var dot = new SolidBrush(c);
        g.FillEllipse(dot, cx - d / 2, cy - d / 2, d, d);
    }

    void PaintToggle(Graphics g, Rectangle r, float t, bool enabled)
    {
        using (var path = Draw.Round(r, r.Height / 2f))
        {
            using (var off = new SolidBrush(enabled ? T.Track : Draw.Mix(T.Track, T.Bg, 0.45f))) g.FillPath(off, path);
            if (t > 0.01f)
                using (var onBrush = new LinearGradientBrush(r, Draw.Alpha(T.Accent, (int)(255 * t)), Draw.Alpha(T.Accent2, (int)(255 * t)), 0f))
                    g.FillPath(onBrush, path);
        }
        float d = r.Height - P(6), x = r.X + P(3) + t * (r.Width - d - P(6));
        using (var shadow = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillEllipse(shadow, x, r.Y + P(4), d, d);
        using var knob = new SolidBrush(enabled ? Color.White : Draw.Mix(Color.White, T.Track, 0.4f));
        g.FillEllipse(knob, x, r.Y + P(3), d, d);
    }

    /// The artist's signature, bottom right, with a little flourish underneath.
    void PaintFooter(Graphics g)
    {
        Draw.Text(g, Program.Name.ToUpperInvariant() + "  " + Program.Version, fonts.Tiny, new Rectangle(rFooter.X + P(2), rFooter.Y, P(160), rFooter.Height), T.Faint, TextFormatFlags.VerticalCenter);
        var sig = new RectangleF(rFooter.Right - P(110), rFooter.Y - P(2), P(108), rFooter.Height - P(4));
        Draw.GradientText(g, "Vortex", fonts.Signature, sig, T.Amber, T.Accent3);
        using var ink = new LinearGradientBrush(new RectangleF(sig.X, sig.Bottom - P(6), sig.Width, P(4)), Draw.Alpha(T.Accent, 0), Draw.Alpha(T.Accent3, 170), 0f);
        using var pen = new Pen(ink, Math.Max(1f, P(1.2f))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawBezier(pen, sig.X + P(8), sig.Bottom - P(1), sig.X + P(40), sig.Bottom + P(3), sig.Right - P(30), sig.Bottom - P(6), sig.Right - P(4), sig.Bottom);
    }

    // ---------------------------------------------------------------- previews (tests and README screenshots)

    /// Renders the window in a made-up state without touching the network. Used by the test project.
    internal Bitmap RenderPreview(Action<Tunnel, Hotspot, Updater> arrange, (long In, long Out) data)
    {
        ready = true;
        arrange(tunnel, hotspot, updater);
        usage = data;
        var mood = tunnel.State == TunnelState.Connected ? VortexArt.Mood.On : tunnel.Busy ? VortexArt.Mood.Busy : tunnel.DetailIsError ? VortexArt.Mood.Error : VortexArt.Mood.Idle;
        art.SetMood(mood);
        art.Step(0.6f, true);
        toggleT = hotspot.On ? 1f : 0f;
        var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var g = Graphics.FromImage(bmp)) OnPaint(new PaintEventArgs(g, new Rectangle(Point.Empty, ClientSize)));
        return bmp;
    }
}
