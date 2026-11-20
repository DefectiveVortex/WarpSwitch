using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace WarpSwitch;

/// Live view of the recent usque output and app events, for troubleshooting. New lines are appended; it only
/// follows them to the bottom while you're at the bottom, so you can scroll back and select text meanwhile.
sealed class LogForm : Form
{
    const int EM_GETFIRSTVISIBLELINE = 0xCE, EM_LINESCROLL = 0xB6, WM_SETREDRAW = 0x0B;
    const int MaxLinesShown = 3000;

    readonly LogBuffer log;
    readonly TextBox box;
    readonly Timer timer = new() { Interval = 1000 };
    readonly Theme theme;
    readonly Font mono = new("Consolas", 9f);
    long seen;

    public LogForm(LogBuffer log, Theme theme, Icon icon)
    {
        this.log = log;
        this.theme = theme;
        float s = Native.DisplayScale();
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
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = mono,
            BackColor = theme.Card, ForeColor = theme.Text, MaxLength = 0,
        };
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = (int)(44 * s),
            Padding = new Padding((int)(8 * s)), BackColor = theme.Bg,
        };
        bar.Controls.Add(MakeButton("Copy all", (o, e) => { try { Clipboard.SetText(box.TextLength > 0 ? box.Text : " "); } catch { } }));
        bar.Controls.Add(MakeButton("Open app folder", (o, e) => { try { Process.Start("explorer.exe", "\"" + Setup.Dir + "\""); } catch { } }));
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

    int FirstVisibleLine() => (int)Native.SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);

    bool AtBottom()
    {
        int visible = Math.Max(1, box.ClientSize.Height / Math.Max(1, box.Font.Height));
        int total = box.GetLineFromCharIndex(box.TextLength) + 1;
        return FirstVisibleLine() + visible >= total - 1;
    }

    void Pull()
    {
        var lines = log.Since(seen, out seen, out bool reset);
        if (lines.Length == 0 && !reset) return;
        bool follow = box.SelectionLength == 0 && AtBottom();
        if (reset || box.GetLineFromCharIndex(box.TextLength) > MaxLinesShown)
        {
            box.Text = string.Join(Environment.NewLine, lines);
            if (follow) { box.SelectionStart = box.TextLength; box.ScrollToCaret(); }
            return;
        }
        string text = (box.TextLength > 0 ? Environment.NewLine : "") + string.Join(Environment.NewLine, lines);
        if (follow)
        {
            box.AppendText(text);
            return;
        }
        // Keep the reader's place and selection while lines arrive underneath.
        int first = FirstVisibleLine(), selStart = box.SelectionStart, selLen = box.SelectionLength;
        Native.SendMessage(box.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            box.AppendText(text);
            box.Select(selStart, selLen);
            Native.SendMessage(box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(first - FirstVisibleLine()));
        }
        finally
        {
            Native.SendMessage(box.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            box.Invalidate();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleTitleBar(Handle, theme.Dark, theme.Bg);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        mono.Dispose();
        base.OnFormClosed(e);
    }
}
