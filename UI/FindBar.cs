using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace R1.UI;

public sealed class FindBar : Control
{
    private readonly TextBox _box;
    private readonly Font _fSmall;
    private readonly Font _fPlaceholder;
    private string _status = "";
    private RectangleF _rPrev, _rNext, _rClose;
    private int _hot = -1;

    public event Action? CloseRequested, NextRequested, PrevRequested;
    public event Action<string>? QueryChanged;

    public string Query => _box.Text;

    public FindBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(13, 15, 20);
        _fSmall = Theme.Ui(8.4f);
        _fPlaceholder = Theme.Ui(9.8f);

        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(23, 27, 35),
            ForeColor = Theme.Text,
            Font = _fPlaceholder,
            AutoSize = false
        };
        _box.TextChanged += (_, _) =>
        {
            _status = "";
            QueryChanged?.Invoke(_box.Text);
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                CloseRequested?.Invoke();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                if (e.Shift) PrevRequested?.Invoke(); else NextRequested?.Invoke();
                e.SuppressKeyPress = true;
            }
        };
        Controls.Add(_box);
    }

    public void FocusQuery()
    {
        _box.Focus();
        _box.SelectAll();
    }

    public void SetStatus(string s)
    {
        _status = s;
        Invalidate();
    }

    private float Sc => DeviceDpi / 96f;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        float w = Width / Sc;
        _rPrev = new RectangleF(w - 128, 8, 28, 28);
        _rNext = new RectangleF(w - 96, 8, 28, 28);
        _rClose = new RectangleF(w - 60, 8, 28, 28);
        _box.SetBounds((int)(16 * Sc), (int)(13 * Sc), (int)(Math.Max(60, w - 230) * Sc), (int)(18 * Sc));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var st = g.Save();
        g.ScaleTransform(Sc, Sc);
        float w = Width / Sc, h = Height / Sc;

        using (var lg = new LinearGradientBrush(new RectangleF(0, 0, w, h),
                   Color.FromArgb(19, 22, 29), Color.FromArgb(12, 14, 18), 90f))
            g.FillRectangle(lg, 0, 0, w, h);
        using (var pen = new Pen(Color.FromArgb(34, 255, 255, 255), 1f))
            g.DrawLine(pen, 0, 0.5f, w, 0.5f);

        var pill = new RectangleF(10, 6, Math.Max(80, w - 140), 34);
        Theme.FillRound(g, pill, 12, Color.FromArgb(22, 27, 35));
        Theme.StrokeRound(g, pill, 12, Color.FromArgb(34, 255, 255, 255));
        Icons.Draw(g, IconKind.Search, new RectangleF(pill.X + 12, pill.Y + 10, 14, 14), Theme.Muted, 1.4f);
        if (_box.TextLength == 0)
        {
            using var b = new SolidBrush(Theme.Dim);
            using var f = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString("Find in page", _fPlaceholder, b, new RectangleF(pill.X + 36, pill.Y, pill.Width - 44, pill.Height), f);
        }

        DrawButton(g, _rPrev, IconKind.Back, 0);
        DrawButton(g, _rNext, IconKind.Forward, 1);
        DrawButton(g, _rClose, IconKind.Close, 2);

        if (!string.IsNullOrEmpty(_status))
        {
            using var b = new SolidBrush(Theme.Danger);
            using var f = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Far };
            g.DrawString(_status, _fSmall, b, new RectangleF(w - 240, 0, 100, h), f);
        }

        g.Restore(st);
    }

    private void DrawButton(Graphics g, RectangleF r, IconKind icon, int index)
    {
        if (_hot == index) Theme.FillRound(g, r, 8, Color.FromArgb(26, 255, 255, 255));
        Icons.Draw(g, icon, RectangleF.Inflate(r, 8, 8), _hot == index ? Theme.Text : Theme.Muted, 1.6f);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = new PointF(e.X / Sc, e.Y / Sc);
        int hot = _rPrev.Contains(p) ? 0 : _rNext.Contains(p) ? 1 : _rClose.Contains(p) ? 2 : -1;
        if (hot != _hot)
        {
            _hot = hot;
            Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var p = new PointF(e.X / Sc, e.Y / Sc);
        if (_rPrev.Contains(p)) PrevRequested?.Invoke();
        else if (_rNext.Contains(p)) NextRequested?.Invoke();
        else if (_rClose.Contains(p)) CloseRequested?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hot = -1;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fSmall.Dispose();
            _fPlaceholder.Dispose();
            _box.Dispose();
        }
        base.Dispose(disposing);
    }
}
