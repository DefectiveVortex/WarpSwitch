using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace WarpSwitch;

/// Live view of the recent usque output and app events, for troubleshooting.
sealed class LogForm : Form
{
    readonly LogBuffer log;
    readonly TextBox box;
    readonly Timer timer = new() { Interval = 1000 };
    readonly Theme theme;
    string shown = "";

    public LogForm(LogBuffer log, Theme theme, Icon icon)
    {
        this.log = log;
        this.theme = theme;
        float s = DeviceDpi / 96f;
        Text = Program.Name + " log";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size((int)(680 * s), (int)(420 * s));
        MinimumSize = new Size((int)(420 * s), (int)(240 * s));
        BackColor = theme.Bg;

        box = new TextBox
        {
            Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f),
            BackColor = theme.Card, ForeColor = theme.Text,
        };
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = (int)(44 * s),
            Padding = new Padding((int)(8 * s)), BackColor = theme.Bg,
        };
        bar.Controls.Add(MakeButton("Copy all", (o, e) => Clipboard.SetText(box.TextLength > 0 ? box.Text : " ")));
        bar.Controls.Add(MakeButton("Open app folder", (o, e) => Process.Start("explorer.exe", Setup.Dir)));
        Controls.Add(box);
        Controls.Add(bar);

        timer.Tick += (o, e) => Pull();
        timer.Start();
        Pull();
    }

    Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = theme.Card, ForeColor = theme.Text, Margin = new Padding(6, 0, 0, 0),
        };
        b.FlatAppearance.BorderColor = theme.Border;
        b.Click += onClick;
        return b;
    }

    void Pull()
    {
        string text = log.Text();
        if (text == shown) return;
        shown = text;
        box.Text = text;
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleTitleBar(Handle, theme.Dark, theme.Bg);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        base.OnFormClosed(e);
    }
}
