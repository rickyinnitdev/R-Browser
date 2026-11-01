using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace R1.UI;

public sealed class ToastForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2600 };
    private string _text = "";
    private readonly Font _font = Theme.Ui(9.2f);

    public ToastForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 28, 37);
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _timer.Tick += (_, _) => { _timer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;
            cp.ExStyle |= 0x00000080;
            return cp;
        }
    }

    public void ShowMessage(IWin32Window owner, string text, int bottomOffset = 24)
    {
        _text = text;
        using var g = CreateGraphics();
        var size = g.MeasureString(text, _font);
        int w = (int)Math.Clamp(size.Width + 40, 160, 460);
        int h = 44;

        var form = owner as Form;
        if (form != null)
        {
            var screen = Screen.FromControl(form).WorkingArea;
            int x = form.Left + form.Width - w - 24;
            int y = form.Top + form.Height - h - bottomOffset;
            x = Math.Clamp(x, screen.Left + 8, screen.Right - w - 8);
            y = Math.Clamp(y, screen.Top + 8, screen.Bottom - h - 8);
            SetBounds(x, y, w, h);
        }
        if (!Visible) Show();
        BringToFront();
        Invalidate();
        _timer.Stop();
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var r = new RectangleF(0, 0, Width, Height);
        Theme.FillRound(g, r, 12, Color.FromArgb(30, 35, 46));
        Theme.StrokeRound(g, r, 12, Color.FromArgb(56, 255, 255, 255));
        using (var b = new SolidBrush(Theme.Text))
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
            g.DrawString(_text, _font, b, new RectangleF(18, 0, Width - 36, Height), sf);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
