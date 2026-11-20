using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace WarpSwitch;

/// A small dialog for typing: one entry (a network name, an address to skip WARP for) or a whole list
/// (the split tunnel, one per line). <c>validate</c> returns why the text can't be saved, or null.
sealed class EditForm : Form
{
    readonly Theme t;
    readonly Fonts fonts;
    readonly float s;
    readonly string hint;
    readonly Rectangle rHint;
    readonly Panel host;
    readonly TextBox box;
    readonly Label error;
    readonly Func<string[], string> validate;
    readonly Font mono;

    /// The non-empty, trimmed lines that were saved.
    public string[] Lines { get; private set; } = new string[0];

    public EditForm(string title, string hint, string text, bool multiline, Theme theme, Icon icon, Func<string[], string> validate)
    {
        t = theme;
        this.hint = hint;
        this.validate = validate;
        s = Native.DisplayScale();
        fonts = new Fonts(s);
        Text = title;
        Icon = icon;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        BackColor = t.Bg;
        ForeColor = t.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        int w = P(multiline ? 440 : 400), pad = P(20);
        int hintH = TextRenderer.MeasureText(hint ?? "", fonts.Small, new Size(w - 2 * pad, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        rHint = new Rectangle(pad, P(18), w - 2 * pad, hintH);
        int y = rHint.Bottom + P(12);
        int boxH = multiline ? P(170) : P(40);

        mono = new Font("Consolas", 9.5f * 4f / 3f * s, GraphicsUnit.Pixel);
        box = new TextBox
        {
            Multiline = multiline, AcceptsReturn = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            BorderStyle = BorderStyle.None, BackColor = t.Surface, ForeColor = t.Text, Dock = DockStyle.Fill,
            Font = multiline ? mono : fonts.Body, Text = text ?? "",
        };
        host = new Panel
        {
            Bounds = new Rectangle(pad, y, w - 2 * pad, boxH), BackColor = t.Bg,
            Padding = multiline ? new Padding(P(12), P(10), P(4), P(10)) : new Padding(P(12), (boxH - fonts.Body.Height) / 2, P(12), 0),
        };
        host.Paint += PaintHost;
        host.Controls.Add(box);
        box.HandleCreated += (o, e) => Native.DarkScrollBars(box.Handle);
        box.GotFocus += (o, e) => host.Invalidate();
        box.LostFocus += (o, e) => host.Invalidate();
        box.TextChanged += (o, e) => error.Visible = false;
        Controls.Add(host);
        y = host.Bottom + P(6);

        error = new Label
        {
            Bounds = new Rectangle(pad, y, w - 2 * pad, P(30)), ForeColor = t.Bad, BackColor = t.Bg, Font = fonts.Small, Visible = false,
        };
        Controls.Add(error);
        y = error.Bottom + P(2);

        var save = new PillButton("Save", t, primary: true, fonts.CardTitle) { Bounds = new Rectangle(w - pad - P(104), y, P(104), P(36)) };
        var cancel = new PillButton("Cancel", t, primary: false, fonts.CardTitle) { Bounds = new Rectangle(save.Left - P(10 + 96), y, P(96), P(36)), DialogResult = DialogResult.Cancel };
        save.Click += (o, e) => Save();
        Controls.Add(save);
        Controls.Add(cancel);
        if (!multiline) AcceptButton = save;
        CancelButton = cancel;
        ClientSize = new Size(w, save.Bottom + pad);
        ActiveControl = box;
        if (!multiline) box.SelectAll();
        else { box.SelectionStart = box.TextLength; }
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
        // In the list, Enter starts a new line; Ctrl+Enter saves.
        if (e.KeyCode == Keys.Enter && e.Control) { Save(); e.Handled = e.SuppressKeyPress = true; }
    }

    void PaintHost(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, host.Width - 1, host.Height - 1);
        using var path = Draw.Round(r, P(12));
        using (var b = new SolidBrush(t.Surface)) g.FillPath(b, path);
        using var pen = new Pen(box.Focused ? Draw.Alpha(t.Accent, 170) : Color.FromArgb(26, 255, 255, 255), box.Focused ? 1.5f * s : 1f);
        g.DrawPath(pen, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Draw.Text(e.Graphics, hint, fonts.Small, rHint, t.Sub, TextFormatFlags.WordBreak);
    }

    void Save()
    {
        var lines = box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        string why = validate?.Invoke(lines);
        if (why != null)
        {
            error.Text = why;
            error.Visible = true;
            box.Focus();
            return;
        }
        Lines = lines;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { fonts.Dispose(); mono.Dispose(); }
        base.Dispose(disposing);
    }
}

/// A rounded button that matches the main window (the stock flat button is square and hard-edged).
sealed class PillButton : Control, IButtonControl
{
    readonly Theme t;
    readonly bool primary;
    bool hot;

    public DialogResult DialogResult { get; set; }

    public PillButton(string text, Theme theme, bool primary, Font font)
    {
        t = theme;
        this.primary = primary;
        Text = text;
        Font = font;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    public void NotifyDefault(bool value) { }

    public void PerformClick() { if (CanSelect || Visible) OnClick(EventArgs.Empty); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (DialogResult != DialogResult.None && FindForm() is Form f) f.DialogResult = DialogResult;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space) PerformClick();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? t.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(1, 1, Width - 2, Height - 2);
        Draw.Pill(g, r, Text, Font, t, primary, hot, Enabled);
        if (Focused && ShowFocusCues)
        {
            using var path = Draw.Round(r, r.Height / 2f);
            using var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 1.5f);
            g.DrawPath(pen, path);
        }
    }
}
