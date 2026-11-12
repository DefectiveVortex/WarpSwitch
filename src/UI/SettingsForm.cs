using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WarpSwitch;

/// Everything that isn't the power button or the hotspot switch.
sealed class SettingsForm : Form
{
    readonly Settings settings;
    readonly Theme t;
    readonly Tunnel tunnel;
    readonly Action saved;
    readonly float s;
    readonly int contentWidth;
    readonly FlowLayoutPanel body;
    readonly CheckBox cbStartup, cbConnectOnLaunch, cbNotifications, cbUpdates, cbAutoConnect, cbKill, cbDoh;
    readonly ComboBox cmbAnim, cmbMtu;
    readonly ListBox lbTrusted;
    readonly TextBox tbBypass;
    readonly Label lbBypassError, lbAdvancedNote;
    bool startupWas;
    static readonly int[] MtuChoices = { 0, 1280, 1350, 1420 };

    public SettingsForm(Settings settings, Theme theme, Icon icon, Tunnel tunnel, Action saved)
    {
        this.settings = settings;
        this.tunnel = tunnel;
        this.saved = saved;
        t = theme;
        s = DeviceDpi / 96f;
        Text = Program.Name + " settings";
        Icon = icon;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9f);
        BackColor = t.Bg;
        ForeColor = t.Text;
        ClientSize = new Size(P(470), P(660));
        MinimumSize = new Size(P(420), P(360));
        contentWidth = P(470 - 2 * 22) - SystemInformation.VerticalScrollBarWidth;

        body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(P(22), P(8), P(10), P(16)), BackColor = t.Bg,
        };
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = P(50),
            Padding = new Padding(P(14), P(10), P(14), P(10)), BackColor = t.BgTop,
        };
        var save = MakeButton("Save", (o, e) => Save(), primary: true);
        bar.Controls.Add(save);
        bar.Controls.Add(MakeButton("Cancel", (o, e) => Close()));
        AcceptButton = save;
        Controls.Add(body);
        Controls.Add(bar);

        Section("General", first: true);
        cbStartup = Check("Start with Windows (in the tray, no UAC prompt)", false);
        cbStartup.Enabled = false;
        if (!Autostart.Allowed) Hint("Available in the installed version (WarpSwitch-Setup.exe from the releases page), so the sign-in task can't be hijacked.");
        cbConnectOnLaunch = Check("Connect when the app opens", settings.ConnectOnLaunch);
        cbNotifications = Check("Notify me when WARP drops, comes back, or an update is out", settings.Notifications);
        cbUpdates = Check("Check for updates automatically", settings.CheckUpdates);
        cmbAnim = Choice("Animated vortex", new[] { "Follow Windows (" + (Native.ClientAreaAnimation() ? "on" : "off") + ")", "On", "Off" },
            settings.AnimationsOverride == null ? 0 : settings.AnimationsOverride.Value ? 1 : 2);

        Section("Networks");
        cbAutoConnect = Check("Connect by itself on networks I don't trust", settings.AutoConnectUntrusted);
        Hint("WARP turns on when you join a network that isn't in the list below. If you turn it off yourself, it stays off on that network until you move to another one.");
        Caption("Trusted networks (WARP stays off here)");
        lbTrusted = new ListBox
        {
            Width = contentWidth, Height = P(84), BackColor = t.Card, ForeColor = t.Text, BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false, Margin = new Padding(0, P(2), 0, P(4)),
        };
        lbTrusted.Items.AddRange(settings.TrustedNetworks.Cast<object>().ToArray());
        body.Controls.Add(lbTrusted);
        var row = Row();
        row.Controls.Add(MakeButton("Trust the current network", async (o, e) => await TrustCurrent((Button)o)));
        row.Controls.Add(MakeButton("Remove", (o, e) => { if (lbTrusted.SelectedIndex >= 0) lbTrusted.Items.RemoveAt(lbTrusted.SelectedIndex); }));

        Section("Protection");
        cbKill = Check("Kill switch: block the internet whenever WARP is down", settings.KillSwitch);
        Hint("Starts once WARP has connected. While it reconnects, switches routes or moves to a new network, nothing leaves the PC outside WARP; your local network still works. Disconnect to lift the block (for example, to sign in to a Wi-Fi login page).");
        cbDoh = Check("Encrypt DNS lookups outside the tunnel (DNS over HTTPS)", settings.EncryptDns);
        if (SecureDns.Supported) Hint("Uses Windows' own DNS over HTTPS for Cloudflare's 1.1.1.1, so even the moments the tunnel is down don't send readable lookups across the network.");
        else { cbDoh.Enabled = false; cbDoh.Checked = false; Hint("Needs Windows 11."); }

        Section("Skip WARP for these (split tunnel)");
        Hint("One per line: an IPv4 address, a range such as 203.0.113.0/24, or a host name such as printer.example.com. They go straight out your normal connection. Your local network always does.");
        tbBypass = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Width = contentWidth, Height = P(84),
            BackColor = t.Card, ForeColor = t.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f),
            Text = string.Join(Environment.NewLine, settings.Bypass), Margin = new Padding(0, P(4), 0, P(2)),
        };
        body.Controls.Add(tbBypass);
        lbBypassError = Hint("");
        lbBypassError.ForeColor = t.Bad;
        lbBypassError.Visible = false;

        Section("Advanced");
        cmbMtu = Choice("Tunnel MTU (TCP routes)", new[] { "Automatic (1280)", "1280", "1350", "1420" }, Math.Max(0, Array.IndexOf(MtuChoices, settings.Mtu)));
        Hint("Takes effect on the next connect. WARP's helper only officially supports 1280; change it only if a network needs it.");
        var adv = Row();
        adv.Controls.Add(MakeButton("Forget learned routes", (o, e) => { PathMemory.Clear(); Note("Learned routes forgotten: every network gets scouted from scratch next time."); }));
        adv.Controls.Add(MakeButton("Reset WARP registration…", async (o, e) => await ResetRegistration()));
        lbAdvancedNote = Hint("");
        lbAdvancedNote.Visible = false;

        Task.Run(Autostart.IsEnabled).ContinueWith(task =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() =>
            {
                startupWas = !task.IsFaulted && task.Result;
                cbStartup.Checked = startupWas;
                cbStartup.Enabled = Autostart.Allowed || startupWas;   // a portable copy can still turn an old task off
            }));
        });
    }

    int P(float v) => (int)Math.Round(v * s);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleTitleBar(Handle, t.Dark, t.BgTop);
    }

    // ---------------------------------------------------------------- building blocks

    void Section(string title, bool first = false)
    {
        body.Controls.Add(new Label
        {
            Text = title.ToUpperInvariant(), AutoSize = true, ForeColor = t.Amber, Font = new Font("Segoe UI Semibold", 8.5f),
            Margin = new Padding(0, P(first ? 6 : 18), 0, P(4)),
        });
    }

    CheckBox Check(string text, bool on)
    {
        var c = new CheckBox
        {
            Text = text, Checked = on, AutoSize = true, MaximumSize = new Size(contentWidth, 0), ForeColor = t.Text,
            FlatStyle = FlatStyle.Flat, Margin = new Padding(0, P(4), 0, P(2)), Cursor = Cursors.Hand,
        };
        c.FlatAppearance.BorderColor = t.Ring;
        c.FlatAppearance.CheckedBackColor = t.Hover;
        body.Controls.Add(c);
        return c;
    }

    Label Hint(string text)
    {
        var l = new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(contentWidth - P(20), 0), ForeColor = t.Faint,
            Margin = new Padding(P(20), 0, 0, P(4)),
        };
        body.Controls.Add(l);
        return l;
    }

    void Caption(string text) => body.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = t.Sub, Margin = new Padding(0, P(8), 0, 0) });

    ComboBox Choice(string label, string[] items, int selected)
    {
        var row = Row();
        row.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = t.Text, Margin = new Padding(0, P(7), P(10), 0) });
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = t.Card, ForeColor = t.Text,
            Width = P(170), Margin = new Padding(0, P(3), 0, 0),
        };
        c.Items.AddRange(items);
        c.SelectedIndex = Math.Min(Math.Max(0, selected), items.Length - 1);
        row.Controls.Add(c);
        return c;
    }

    FlowLayoutPanel Row()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(contentWidth, 0), Margin = new Padding(0, P(4), 0, P(2)), BackColor = t.Bg };
        body.Controls.Add(row);
        return row;
    }

    Button MakeButton(string text, EventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Draw.Mix(t.Accent, t.Accent2, 0.5f) : t.Card, ForeColor = primary ? Color.White : t.Text,
            Margin = new Padding(0, 0, P(8), 0), Padding = new Padding(P(6), P(2), P(6), P(2)),
        };
        b.FlatAppearance.BorderColor = primary ? t.Accent2 : t.Border;
        b.Click += onClick;
        return b;
    }

    void Note(string text)
    {
        lbAdvancedNote.Text = text;
        lbAdvancedNote.ForeColor = t.Good;
        lbAdvancedNote.Visible = true;
    }

    // ---------------------------------------------------------------- actions

    async Task TrustCurrent(Button b)
    {
        b.Enabled = false;
        string name = await Task.Run(Tunnel.CurrentNetwork);
        b.Enabled = true;
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "This PC isn't on a network right now.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!lbTrusted.Items.Cast<string>().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) lbTrusted.Items.Add(name);
        lbTrusted.SelectedItem = name;
    }

    async Task ResetRegistration()
    {
        var answer = MessageBox.Show(this,
            "Set this PC's WARP registration aside and make a new one on the next connect?\n\n" +
            "Only needed if connecting keeps failing with registration or key errors. The old registration is kept as config.json.bak.",
            Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        if (tunnel.State != TunnelState.Off) await tunnel.DisconnectAsync();
        try
        {
            await Task.Run(Setup.ResetRegistration);
            Note("Done. A fresh WARP registration will be made the next time you connect.");
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't reset it: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    void Save()
    {
        var lines = tbBypass.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var bad = lines.Where(l => !l.StartsWith("#") && Net.ParseBypass(l, out _) == null).ToList();
        if (bad.Count > 0)
        {
            lbBypassError.Text = "Not an IPv4 address, range or host name: " + string.Join(", ", bad.Take(3)) + (bad.Count > 3 ? "…" : "");
            lbBypassError.Visible = true;
            body.ScrollControlIntoView(tbBypass);
            tbBypass.Focus();
            return;
        }

        if (cbStartup.Enabled && cbStartup.Checked != startupWas)
        {
            try { Autostart.Set(cbStartup.Checked); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex is UserError ? ex.Message : "Couldn't change Start with Windows: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        settings.ConnectOnLaunch = cbConnectOnLaunch.Checked;
        settings.Notifications = cbNotifications.Checked;
        settings.CheckUpdates = cbUpdates.Checked;
        settings.AnimationsOverride = cmbAnim.SelectedIndex switch { 1 => true, 2 => false, _ => null };
        settings.AutoConnectUntrusted = cbAutoConnect.Checked;
        settings.TrustedNetworks = lbTrusted.Items.Cast<string>().ToList();
        settings.KillSwitch = cbKill.Checked;
        settings.EncryptDns = cbDoh.Enabled && cbDoh.Checked;
        settings.Bypass = lines;
        settings.Mtu = MtuChoices[Math.Max(0, cmbMtu.SelectedIndex)];
        settings.Save();
        saved();
        Close();
    }
}
