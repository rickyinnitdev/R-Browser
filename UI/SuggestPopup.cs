using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace R1.UI;

public sealed class SuggestPopup : Form
{
    private readonly List<(string Title, string Sub, string Value)> _items = new();
    private readonly Font _fTitle, _fSub;
    private int _selected = -1;
    private readonly int _rowH = 46;

    public event Action<string>? ItemChosen;

    public string? SelectedValue => _selected >= 0 && _selected < _items.Count ? _items[_selected].Value : null;
    public bool HasFocus => false;

    public SuggestPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(17, 20, 27);
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _fTitle = Theme.Ui(9.6f);
        _fSub = Theme.Ui(8.2f);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    public void ShowItems(IReadOnlyList<(string Title, string Sub, string Value)> items, Point screenLocation, int width)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = -1;
        int targetW = Math.Max(220, width);
        int h = Math.Max(1, _items.Count) * _rowH + 12;

        var screen = Screen.FromPoint(screenLocation).WorkingArea;
        int x = Math.Clamp(screenLocation.X, screen.Left, Math.Max(screen.Left, screen.Right - targetW));
        int y = screenLocation.Y;
        if (y + h > screen.Bottom)
        {
            y = Math.Max(screen.Top, screen.Bottom - h);
        }

        SetBounds(x, y, targetW, h);
        if (!Visible) Show();
        BringToFront();
        Invalidate();
    }

    public void HideAll()
    {
        if (Visible) Hide();
        _items.Clear();
        _selected = -1;
    }

    public void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        if (_selected == -1)
        {
            _selected = delta > 0 ? 0 : _items.Count - 1;
        }
        else if (_selected == 0 && delta < 0)
        {
            _selected = -1;
        }
        else
        {
            _selected = (_selected + delta + _items.Count) % _items.Count;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var rect = new RectangleF(0, 0, Width, Height);
        Theme.FillRound(g, rect, 12, Color.FromArgb(22, 26, 34));
        Theme.StrokeRound(g, rect, 12, Color.FromArgb(44, 255, 255, 255));
        using (var pen = new Pen(Color.FromArgb(60, Theme.Accent), 1f))
        {
            using var p = Theme.Round(rect, 12);
            g.DrawPath(pen, p);
        }

        for (int i = 0; i < _items.Count; i++)
        {
            var r = new RectangleF(6, 6 + i * _rowH, Width - 12, _rowH - 2);
            if (i == _selected) Theme.FillRound(g, r, 8, Color.FromArgb(30, 255, 255, 255));

            using var b1 = new SolidBrush(Theme.Text);
            using var b2 = new SolidBrush(Theme.Muted);
            using var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(_items[i].Title, _fTitle, b1, new RectangleF(r.X + 12, r.Y + 6, r.Width - 24, 18), sf);
            g.DrawString(_items[i].Sub, _fSub, b2, new RectangleF(r.X + 12, r.Y + 23, r.Width - 24, 16), sf);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Y < 6) return;
        int idx = (e.Y - 6) / _rowH;
        if (idx >= 0 && idx < _items.Count)
        {
            var value = _items[idx].Value;
            HideAll();
            ItemChosen?.Invoke(value);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Y < 6) return;
        int idx = (e.Y - 6) / _rowH;
        if (idx != _selected && idx >= 0 && idx < _items.Count)
        {
            _selected = idx;
            Invalidate();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HideAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fTitle.Dispose();
            _fSub.Dispose();
        }
        base.Dispose(disposing);
    }
}
