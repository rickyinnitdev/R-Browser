using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using R1.Core;

namespace R1.UI;

internal sealed class TabSidebar : Control
{
    private readonly AppState _app;
    private IReadOnlyList<BrowserTab> _tabs = Array.Empty<BrowserTab>();
    private int _active = -1;
    private bool _collapsed;
    private float _scroll;
    private float _spin;

    private readonly Font _fTitle, _fTab, _fSmall, _fTiny;
    private readonly ToolTip _toolTip;
    private string _lastToolTipText = "";

    // Hit-test state
    private int _hoverTab = -1;
    private int _hoverClose = -1;
    private int _hoverAudio = -1;
    private bool _hoverNewTab;
    private bool _hoverCollapse;
    private bool _hoverLayout;

    // Drag to reorder
    private int _dragTab = -1;
    private Point _dragStart;
    private bool _isDragging;

    // Hit areas
    private RectangleF _rCollapse, _rLayout, _rNewTab;
    private readonly List<(RectangleF Rect, int Index)> _tabRects = new();
    private readonly List<(RectangleF Rect, int Index)> _closeRects = new();
    private readonly List<(RectangleF Rect, int Index)> _audioRects = new();

    public event Action? NewTabRequested;
    public event Action<int>? TabActivated;
    public event Action<int>? TabClosed;
    public event Action<int>? TabAudioToggled;
    public event Action<int, Point>? TabMenuRequested;
    public event Action<int, int>? TabReordered;
    public event Action? CollapseToggled;
    public event Action? LayoutSwitchRequested;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            _hoverTab = -1;
            Invalidate();
        }
    }

    public TabSidebar(AppState app)
    {
        _app = app;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(13, 15, 20);

        _fTitle = Theme.Display(10f, FontStyle.Bold);
        _fTab = Theme.Ui(9.2f);
        _fSmall = Theme.Ui(8.2f);
        _fTiny = Theme.Ui(7.2f, FontStyle.Bold);

        _toolTip = new ToolTip
        {
            InitialDelay = 350,
            ReshowDelay = 100,
            AutoPopDelay = 5000,
            ShowAlways = true
        };
    }

    public void UpdateTabs(IReadOnlyList<BrowserTab> tabs, int activeIndex, float spin)
    {
        _tabs = tabs;
        _active = activeIndex;
        _spin = spin;
        ClampScroll();
        Invalidate();
    }

    public void EnsureVisible(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        float itemH = 34f + 3f;
        float itemY = index * itemH;
        float viewH = AvailableListHeight();
        if (itemY < _scroll)
        {
            _scroll = itemY;
            ClampScroll();
            Invalidate();
        }
        else if (itemY + 34f > _scroll + viewH)
        {
            _scroll = (itemY + 34f) - viewH;
            ClampScroll();
            Invalidate();
        }
    }

    private float Sc => DeviceDpi / 96f;
    private float HeaderH => 36f;
    private float NewTabH => 32f;
    private float ListTop => HeaderH + NewTabH + 8f;

    private float AvailableListHeight()
    {
        float totalH = Height / Sc;
        return Math.Max(0, totalH - ListTop);
    }

    private void ClampScroll()
    {
        float totalListH = _tabs.Count * (34f + 3f);
        float viewH = AvailableListHeight();
        float maxScroll = Math.Max(0, totalListH - viewH);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var st = g.Save();
        try
        {
            g.ScaleTransform(Sc, Sc);
            float w = Width / Sc;
            float h = Height / Sc;

            // Background gradient
            using (var lg = new LinearGradientBrush(new RectangleF(0, 0, w, h),
                       Color.FromArgb(18, 20, 27), Color.FromArgb(11, 13, 17), 90f))
            {
                g.FillRectangle(lg, 0, 0, w, h);
            }

            // Right border divider
            using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255), 1f))
                g.DrawLine(pen, w - 0.5f, 0, w - 0.5f, h);

            PaintHeader(g, w);
            PaintNewTabButton(g, w);
            PaintTabList(g, w, h);
        }
        finally
        {
            g.Restore(st);
        }
    }

    private void PaintHeader(Graphics g, float w)
    {
        if (!_collapsed)
        {
            // Title & Count badge
            var titleRect = new RectangleF(12, 10, w - 74, 18);
            DrawText(g, "TABS", _fTitle, titleRect, Theme.Muted, StringAlignment.Near);

            if (_tabs.Count > 0)
            {
                var countText = _tabs.Count.ToString();
                var countRect = new RectangleF(48, 10, 22, 16);
                Theme.FillRound(g, countRect, 6, Color.FromArgb(24, 255, 255, 255));
                DrawText(g, countText, _fTiny, countRect, Theme.Text, StringAlignment.Center);
            }

            // Top tabs layout switch button
            _rLayout = new RectangleF(w - 56, 7, 24, 24);
            if (_hoverLayout) Theme.FillRound(g, _rLayout, 6, Color.FromArgb(26, 255, 255, 255));
            Icons.Draw(g, IconKind.LayoutHorizontal, RectangleF.Inflate(_rLayout, -4, -4),
                _hoverLayout ? Theme.Text : Theme.Muted, 1.4f);

            // Collapse button
            _rCollapse = new RectangleF(w - 28, 7, 24, 24);
            if (_hoverCollapse) Theme.FillRound(g, _rCollapse, 6, Color.FromArgb(26, 255, 255, 255));
            Icons.Draw(g, IconKind.CollapseLeft, RectangleF.Inflate(_rCollapse, -4, -4),
                _hoverCollapse ? Theme.Text : Theme.Muted, 1.4f);
        }
        else
        {
            // Expand button
            _rCollapse = new RectangleF((w - 24) / 2, 7, 24, 24);
            if (_hoverCollapse) Theme.FillRound(g, _rCollapse, 6, Color.FromArgb(26, 255, 255, 255));
            Icons.Draw(g, IconKind.ExpandRight, RectangleF.Inflate(_rCollapse, -4, -4),
                _hoverCollapse ? Theme.Text : Theme.Muted, 1.4f);

            _rLayout = RectangleF.Empty;
        }
    }

    private void PaintNewTabButton(Graphics g, float w)
    {
        if (!_collapsed)
        {
            _rNewTab = new RectangleF(10, HeaderH + 2, w - 20, NewTabH);
            Color bg = _hoverNewTab ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(16, 255, 255, 255);
            Theme.FillRound(g, _rNewTab, 8, bg);
            Theme.StrokeRound(g, _rNewTab, 8, Color.FromArgb(22, 255, 255, 255));

            var iconBox = new RectangleF(_rNewTab.X + 8, _rNewTab.Y + 8, 16, 16);
            Icons.Draw(g, IconKind.Plus, iconBox, _hoverNewTab ? Theme.Text : Theme.Muted, 1.6f);

            var textRect = new RectangleF(_rNewTab.X + 28, _rNewTab.Y, _rNewTab.Width - 36, _rNewTab.Height);
            DrawText(g, "New tab", _fTab, textRect, _hoverNewTab ? Theme.Text : Theme.Muted, StringAlignment.Near);
        }
        else
        {
            _rNewTab = new RectangleF((w - 28) / 2, HeaderH + 2, 28, 28);
            Color bg = _hoverNewTab ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(16, 255, 255, 255);
            Theme.FillRound(g, _rNewTab, 7, bg);
            Theme.StrokeRound(g, _rNewTab, 7, Color.FromArgb(22, 255, 255, 255));

            Icons.Draw(g, IconKind.Plus, RectangleF.Inflate(_rNewTab, -6, -6),
                _hoverNewTab ? Theme.Text : Theme.Muted, 1.6f);
        }
    }

    private void PaintTabList(Graphics g, float w, float h)
    {
        _tabRects.Clear();
        _closeRects.Clear();
        _audioRects.Clear();

        float viewH = AvailableListHeight();
        if (viewH <= 0) return;

        var clipState = g.Save();
        g.SetClip(new RectangleF(0, ListTop, w, viewH));

        float startY = ListTop - _scroll;
        float itemH = 34f;
        float itemGap = 3f;

        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            float y = startY + i * (itemH + itemGap);
            var r = _collapsed
                ? new RectangleF(4, y, w - 8, itemH)
                : new RectangleF(8, y, w - 16, itemH);

            _tabRects.Add((r, i));

            // Skip offscreen
            if (r.Bottom < ListTop || r.Y > h) continue;

            bool active = i == _active;
            bool hover = i == _hoverTab;

            if (active)
            {
                Theme.FillRound(g, r, 8, Color.FromArgb(30, 255, 255, 255));
                Theme.StrokeRound(g, r, 8, Color.FromArgb(44, Theme.Accent));
            }
            else if (hover)
            {
                Theme.FillRound(g, r, 8, Color.FromArgb(18, 255, 255, 255));
            }

            // Favicon / Loading spinner
            var iconRect = _collapsed
                ? new RectangleF(r.X + (r.Width - 16) / 2, r.Y + 9, 16, 16)
                : new RectangleF(r.X + 8, r.Y + 9, 16, 16);

            if (tab.Loading)
            {
                PaintSpinner(g, RectangleF.Inflate(iconRect, -1.5f, -1.5f), active ? Theme.Accent : Theme.Muted, _spin);
            }
            else if (!TryPaintTabIcon(g, tab, iconRect))
            {
                Icons.Draw(g, tab.IsInternal ? IconKind.Spark : IconKind.Globe, iconRect,
                    active ? Theme.Text : Theme.Muted, 1.3f);
            }

            if (!_collapsed)
            {
                bool showAudio = tab.IsPlayingAudio || tab.IsMuted;
                float rightMargin = (active || hover) ? 26f : 8f;
                if (showAudio) rightMargin += 22f;

                // Title
                var titleRect = new RectangleF(r.X + 30, r.Y + 1, Math.Max(10, r.Width - 30 - rightMargin), r.Height - 2);
                Color titleColor = active ? Theme.Text : tab.Hibernated ? Theme.Dim : Theme.Muted;
                DrawText(g, tab.Title, _fTab, titleRect, titleColor, StringAlignment.Near);

                // Audio indicator / mute button
                if (showAudio)
                {
                    var ar = new RectangleF(r.Right - ((active || hover) ? 46f : 24f), r.Y + 9, 16, 16);
                    _audioRects.Add((ar, i));
                    if (_hoverAudio == i)
                    {
                        Theme.FillRound(g, RectangleF.Inflate(ar, 2, 2), 5, Color.FromArgb(40, 255, 255, 255));
                    }
                    Icons.Draw(g, tab.IsMuted ? IconKind.VolumeMute : IconKind.Volume, ar,
                        tab.IsMuted ? Theme.Danger : Theme.Aqua, 1.4f);
                }

                // Close button
                if (active || hover)
                {
                    var cr = new RectangleF(r.Right - 22, r.Y + 9, 16, 16);
                    _closeRects.Add((cr, i));
                    if (_hoverClose == i)
                    {
                        Theme.FillRound(g, RectangleF.Inflate(cr, 2, 2), 5, Color.FromArgb(48, 255, 255, 255));
                    }
                    Icons.Draw(g, IconKind.Close, cr, _hoverClose == i ? Theme.Text : Theme.Muted, 1.4f);
                }
            }
            else
            {
                // Collapsed mode audio indicator badge
                if (tab.IsPlayingAudio || tab.IsMuted)
                {
                    var badge = new RectangleF(iconRect.Right - 5, iconRect.Bottom - 5, 6, 6);
                    using var b = new SolidBrush(tab.IsMuted ? Theme.Danger : Theme.Aqua);
                    g.FillEllipse(b, badge);
                }
            }
        }

        g.Restore(clipState);

        // Scrollbar
        float totalListH = _tabs.Count * (itemH + itemGap);
        if (totalListH > viewH && viewH > 0)
        {
            float barH = Math.Max(26, viewH * viewH / totalListH);
            float maxScroll = totalListH - viewH;
            float t = maxScroll <= 0 ? 0 : _scroll / maxScroll;
            float barY = ListTop + t * (viewH - barH);
            Theme.FillRound(g, new RectangleF(w - 5, barY, 3, barH), 2, Color.FromArgb(50, 255, 255, 255));
        }
    }

    private static bool TryPaintTabIcon(Graphics g, BrowserTab tab, RectangleF rect)
    {
        var image = tab.Icon;
        if (image == null) return false;
        try { g.DrawImage(image, rect); return true; }
        catch { return false; }
    }

    private static void PaintSpinner(Graphics g, RectangleF r, Color color, float angle)
    {
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, r, angle, 110);
        g.DrawArc(pen, r, angle + 180, 110);
    }

    private static void DrawText(Graphics g, string text, Font font, RectangleF rect, Color color, StringAlignment align)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 2) return;
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = align,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(text, font, brush, rect, sf);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = new PointF(e.X / Sc, e.Y / Sc);

        // Handle live dragging to reorder
        if (_dragTab >= 0 && e.Button == MouseButtons.Left)
        {
            if (!_isDragging && Math.Abs(e.Y - _dragStart.Y) > 5)
            {
                _isDragging = true;
                Cursor = Cursors.SizeNS;
            }
            if (_isDragging)
            {
                int target = TabIndexAt(p);
                if (target >= 0 && target != _dragTab)
                {
                    TabReordered?.Invoke(_dragTab, target);
                    _dragTab = target;
                    Invalidate();
                }
                return;
            }
        }

        bool hotCollapse = _rCollapse.Contains(p);
        bool hotLayout = _rLayout.Contains(p);
        bool hotNewTab = _rNewTab.Contains(p);

        int hotTab = TabIndexAt(p);
        int hotClose = CloseIndexAt(p);
        int hotAudio = AudioIndexAt(p);

        if (hotCollapse != _hoverCollapse || hotLayout != _hoverLayout || hotNewTab != _hoverNewTab ||
            hotTab != _hoverTab || hotClose != _hoverClose || hotAudio != _hoverAudio)
        {
            _hoverCollapse = hotCollapse;
            _hoverLayout = hotLayout;
            _hoverNewTab = hotNewTab;
            _hoverTab = hotTab;
            _hoverClose = hotClose;
            _hoverAudio = hotAudio;

            Cursor = (hotCollapse || hotLayout || hotNewTab || hotClose >= 0 || hotAudio >= 0)
                ? Cursors.Hand
                : hotTab >= 0 ? Cursors.Default : Cursors.Default;

            // Collapsed tooltip
            if (_collapsed && hotTab >= 0 && hotTab < _tabs.Count)
            {
                var tab = _tabs[hotTab];
                var tt = tab.Title;
                if (tt != _lastToolTipText)
                {
                    _lastToolTipText = tt;
                    _toolTip.SetToolTip(this, tt);
                }
            }
            else if (_lastToolTipText != "")
            {
                _lastToolTipText = "";
                _toolTip.SetToolTip(this, null);
            }

            Invalidate();
        }
    }

    private int TabIndexAt(PointF p)
    {
        for (int i = 0; i < _tabRects.Count; i++)
        {
            if (_tabRects[i].Rect.Contains(p)) return _tabRects[i].Index;
        }
        return -1;
    }

    private int CloseIndexAt(PointF p)
    {
        for (int i = 0; i < _closeRects.Count; i++)
        {
            if (_closeRects[i].Rect.Contains(p)) return _closeRects[i].Index;
        }
        return -1;
    }

    private int AudioIndexAt(PointF p)
    {
        for (int i = 0; i < _audioRects.Count; i++)
        {
            if (_audioRects[i].Rect.Contains(p)) return _audioRects[i].Index;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var p = new PointF(e.X / Sc, e.Y / Sc);

        if (e.Button == MouseButtons.Middle)
        {
            int ti = TabIndexAt(p);
            if (ti >= 0) TabClosed?.Invoke(ti);
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            int ti = TabIndexAt(p);
            if (ti >= 0)
            {
                TabMenuRequested?.Invoke(ti, PointToScreen(e.Location));
                return;
            }
        }

        if (e.Button != MouseButtons.Left) return;

        if (_rCollapse.Contains(p))
        {
            CollapseToggled?.Invoke();
            return;
        }
        if (_rLayout.Contains(p))
        {
            LayoutSwitchRequested?.Invoke();
            return;
        }
        if (_rNewTab.Contains(p))
        {
            NewTabRequested?.Invoke();
            return;
        }

        int ci = CloseIndexAt(p);
        if (ci >= 0)
        {
            TabClosed?.Invoke(ci);
            return;
        }

        int ai = AudioIndexAt(p);
        if (ai >= 0)
        {
            TabAudioToggled?.Invoke(ai);
            return;
        }

        int tabIdx = TabIndexAt(p);
        if (tabIdx >= 0)
        {
            _dragTab = tabIdx;
            _dragStart = e.Location;
            _isDragging = false;
            TabActivated?.Invoke(tabIdx);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragTab = -1;
        _isDragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float viewH = AvailableListHeight();
        float totalListH = _tabs.Count * (34f + 3f);
        if (totalListH > viewH)
        {
            _scroll = Math.Clamp(_scroll - e.Delta / 2.5f, 0, totalListH - viewH);
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTab = -1;
        _hoverClose = -1;
        _hoverAudio = -1;
        _hoverNewTab = false;
        _hoverCollapse = false;
        _hoverLayout = false;
        _lastToolTipText = "";
        _toolTip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fTitle.Dispose();
            _fTab.Dispose();
            _fSmall.Dispose();
            _fTiny.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
