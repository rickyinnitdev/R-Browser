using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using R1.Core;

namespace R1.UI;

public enum SidePanelMode { None, Bookmarks, History, Downloads, Shields }

public sealed class PanelItem
{
    public string Title = "";
    public string Sub = "";
    public string Url = "";
    public string? Trailing;
    public double Progress = -1;
    public object? Tag;
}

public sealed class SidePanel : Control
{
    private readonly AppState _app;
    private readonly List<PanelItem> _items = new();
    private readonly Font _fTitle, _fItem, _fSub, _fSmall, _fTiny;
    private string _title = "";
    private string _subtitle = "";
    private float _scroll;
    private int _hover = -1;
    private RectangleF _rClose, _rAction;
    private bool _actionHover;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public SidePanelMode Mode { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string SiteHost { get; set; } = "";

    public event Action? CloseRequested;
    public event Action? ToggleSite;
    public event Action<string>? OpenUrl;
    public event Action<string>? OpenUrlInNewTab;
    public event Action<int>? DeleteRequested;

    public SidePanel(AppState app)
    {
        _app = app;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(13, 15, 20);
        _fTitle = Theme.Display(12.5f, FontStyle.Bold);
        _fItem = Theme.Ui(9.6f);
        _fSub = Theme.Ui(8.4f);
        _fSmall = Theme.Ui(8.2f, FontStyle.Bold);
        _fTiny = Theme.Ui(7.4f, FontStyle.Bold);
    }

    public void SetItems(string title, string subtitle, IEnumerable<PanelItem> items, bool keepScroll = false)
    {
        _title = title;
        _subtitle = subtitle;
        _items.Clear();
        _items.AddRange(items);
        if (!keepScroll) _scroll = 0;
        _hover = -1;
        Invalidate();
    }

    public void DeleteAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);
        Invalidate();
    }

    public IReadOnlyList<PanelItem> Items => _items;

    private float Sc => DeviceDpi / 96f;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var st = g.Save();
        g.ScaleTransform(Sc, Sc);
        float w = Width / Sc, h = Height / Sc;

        using (var lg = new LinearGradientBrush(new RectangleF(0, 0, w, h),
                   Color.FromArgb(18, 21, 28), Color.FromArgb(11, 13, 17), 90f))
            g.FillRectangle(lg, 0, 0, w, h);
        using (var pen = new Pen(Color.FromArgb(34, 255, 255, 255), 1f))
            g.DrawLine(pen, 0.5f, 0, 0.5f, h);

        // header
        var titleRect = new RectangleF(20, 16, w - 66, 26);
        DrawText(g, _title, _fTitle, titleRect, Theme.Text, StringAlignment.Near);
        DrawText(g, _subtitle, _fSub, new RectangleF(20, 42, w - 66, 18), Theme.Muted, StringAlignment.Near);

        _rClose = new RectangleF(w - 42, 16, 24, 24);
        if (_hotClose) Theme.FillRound(g, _rClose, 7, Color.FromArgb(30, 255, 255, 255));
        Icons.Draw(g, IconKind.Close, RectangleF.Inflate(_rClose, 5, 5), Theme.Muted, 1.4f);

        float y = 72;

        if (Mode == SidePanelMode.Shields)
        {
            bool allowed = _app.Settings.WhitelistActive(HostOfCurrent());
            _rAction = new RectangleF(20, y, w - 40, 34);
            Theme.FillRound(g, _rAction, 10, _actionHover ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(24, 255, 255, 255));
            Theme.StrokeRound(g, _rAction, 10, Color.FromArgb(_actionHover ? 90 : 46, Theme.Aqua));
            Icons.Draw(g, allowed ? IconKind.Shield : IconKind.ShieldFilled,
                new RectangleF(_rAction.X + 12, _rAction.Y + 9, 16, 16), allowed ? Theme.Dim : Theme.Aqua, 1.4f);
            DrawText(g, allowed ? "Ads allowed on this site" : "Shields are up on this site",
                _fSmall, new RectangleF(_rAction.X + 36, _rAction.Y, _rAction.Width - 48, _rAction.Height),
                allowed ? Theme.Muted : Theme.Text, StringAlignment.Near);
            y += 48;
        }
        else
        {
            _rAction = RectangleF.Empty;
        }

        // list
        var listRect = new RectangleF(0, y, w, Math.Max(0, h - y));
        g.SetClip(listRect);
        float rowH = 62;
        float start = y - _scroll;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var r = new RectangleF(10, start + i * rowH, w - 20, rowH - 6);
            if (r.Bottom < y || r.Y > h) continue;

            if (_hover == i)
                Theme.FillRound(g, r, 10, Color.FromArgb(20, 255, 255, 255));

            float textLeft = r.X + 12;
            float textRight = r.Right - 12;
            if (!string.IsNullOrEmpty(item.Trailing))
            {
                DrawText(g, item.Trailing, _fTiny, new RectangleF(r.Right - 70, r.Y + 8, 58, 14), Theme.Dim, StringAlignment.Far);
                textRight -= 62;
            }

            DrawText(g, item.Title, _fItem, new RectangleF(textLeft, r.Y + 6, textRight - textLeft, 19),
                Theme.Text, StringAlignment.Near);
            DrawText(g, item.Sub, _fSub, new RectangleF(textLeft, r.Y + 25, textRight - textLeft, 17),
                Theme.Muted, StringAlignment.Near);

            if (item.Progress >= 0)
            {
                var track = new RectangleF(textLeft, r.Y + 44, textRight - textLeft, 4);
                Theme.FillRound(g, track, 2, Color.FromArgb(36, 255, 255, 255));
                float pw = (float)Math.Clamp(item.Progress, 0, 1) * track.Width;
                if (pw > 1) Theme.FillRound(g, new RectangleF(track.X, track.Y, pw, track.Height), 2, Theme.Accent);
            }
        }
        g.ResetClip();

        if (_items.Count == 0)
        {
            DrawText(g, Mode == SidePanelMode.Downloads ? "No downloads yet" : "Nothing here yet",
                _fSub, new RectangleF(0, y + 20, w, 24), Theme.Dim, StringAlignment.Center);
        }

        // scrollbar
        float totalH = _items.Count * rowH;
        float viewH = h - y;
        if (totalH > viewH && viewH > 0)
        {
            float barH = Math.Max(30, viewH * viewH / totalH);
            float maxScroll = totalH - viewH;
            float t = maxScroll <= 0 ? 0 : _scroll / maxScroll;
            float barY = y + t * (viewH - barH);
            Theme.FillRound(g, new RectangleF(w - 6, barY, 3, barH), 2, Color.FromArgb(60, 255, 255, 255));
        }

        g.Restore(st);
    }

    private string HostOfCurrent() => SiteHost;

    private bool _hotClose;

    private void DrawText(Graphics g, string text, Font font, RectangleF rect, Color color, StringAlignment align)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 2) return;
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = align,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(text, font, brush, rect, sf);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = new PointF(e.X / Sc, e.Y / Sc);
        bool hotClose = _rClose.Contains(p);
        bool action = Mode == SidePanelMode.Shields && _rAction.Contains(p);
        int idx = IndexAt(p);
        if (idx != _hover || hotClose != _hotClose || action != _actionHover)
        {
            _hover = idx;
            _hotClose = hotClose;
            _actionHover = action;
            Cursor = idx >= 0 || hotClose || action ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private int IndexAt(PointF p)
    {
        float y = 72 + (Mode == SidePanelMode.Shields ? 48 : 0);
        if (p.Y < y) return -1;
        float rowH = 62;
        int i = (int)((p.Y - y + _scroll) / rowH);
        if (i < 0 || i >= _items.Count) return -1;
        return i;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var p = new PointF(e.X / Sc, e.Y / Sc);

        if (_rClose.Contains(p))
        {
            CloseRequested?.Invoke();
            return;
        }
        if (Mode == SidePanelMode.Shields && _rAction.Contains(p))
        {
            ToggleSite?.Invoke();
            return;
        }
        int idx = IndexAt(p);
        if (idx < 0) return;

        if (e.Button == MouseButtons.Middle && !string.IsNullOrEmpty(_items[idx].Url))
        {
            OpenUrlInNewTab?.Invoke(_items[idx].Url);
            return;
        }

        if (e.Button == MouseButtons.Right && (Mode == SidePanelMode.Bookmarks || Mode == SidePanelMode.History || Mode == SidePanelMode.Downloads))
        {
            DeleteRequested?.Invoke(idx);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        var item = _items[idx];
        if (Mode == SidePanelMode.Downloads)
        {
            try
            {
                if (File.Exists(item.Url)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + item.Url + "\"");
            }
            catch { }
            return;
        }
        if (!string.IsNullOrEmpty(item.Url)) OpenUrl?.Invoke(item.Url);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float y = 72 + (Mode == SidePanelMode.Shields ? 48 : 0);
        float viewH = Height / Sc - y;
        float totalH = _items.Count * 62f;
        float max = Math.Max(0, totalH - viewH);
        _scroll = Math.Clamp(_scroll - e.Delta / 3f, 0, max);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _hotClose = false;
        _actionHover = false;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fTitle.Dispose();
            _fItem.Dispose();
            _fSub.Dispose();
            _fSmall.Dispose();
            _fTiny.Dispose();
        }
        base.Dispose(disposing);
    }
}
