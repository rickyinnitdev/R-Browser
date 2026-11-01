using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using R1.Core;

namespace R1.UI;

internal enum HitKind
{
    None, Tab, TabClose, TabAudio, NewTab, Min, Max, Close,
    Back, Fwd, Reload, Address, Star, Shield, Menu, Sidebar, ToggleLayout
}

public sealed partial class MainForm : Form
{
    private const float TabStripH = 32f;
    private const float ToolbarH = 32f;
    private const float ChromeH = TabStripH + ToolbarH;

    private readonly AppState _app;
    private readonly ShortcutFilter _shortcutFilter;
    private readonly bool _incognito;
    private readonly string? _startUrl;
    private readonly string _profileDir;

    private readonly List<BrowserTab> _tabs = new();
    private int _active = -1;

    private CoreWebView2Environment? _env;
    private Task<CoreWebView2Environment>? _envTask;

    private Panel _contentHost = null!, _webHost = null!;
    private TabSidebar _tabSidebar = null!;
    private SidePanel _side = null!;
    private FindBar _findBar = null!;
    private TextBox _addressBox = null!;
    private SuggestPopup? _suggest;
    private ToastForm? _toast;
    private bool _editing;

    private RectangleF _rNewTab, _rBack, _rFwd, _rReload, _rAddr, _rStar, _rShield, _rMenu;
    private RectangleF _rMin, _rMax, _rClose, _rSidebar, _rToggleLayout;
    private readonly List<(RectangleF Rect, int Index)> _tabRects = new();
    private readonly List<(RectangleF Rect, int Index)> _tabCloseRects = new();
    private readonly List<(RectangleF Rect, int Index)> _tabAudioRects = new();
    private float _tabW = 160, _tabGap = 5, _tabScroll;
    private HitKind _hot;
    private int _hotTab = -1;
    private int _dragTab = -1;
    private PointF _dragStartPoint;
    private bool _isDraggingTab;

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _sleepTick = new() { Interval = 10_000 };
    private readonly Font _fTab, _fUi, _fSmall, _fTiny, _fUrl, _fTitle;
    private readonly List<DownloadEntry> _downloads = new();
    private readonly Dictionary<string, Image?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _iconLoading = new(StringComparer.OrdinalIgnoreCase);
    private float _spin = 0;
    private bool _chromeHidden;
    private DateTime _lastCloseClick = DateTime.MinValue;

    public MainForm(AppState app, bool incognito, string? startUrl)
    {
        _app = app;
        _incognito = incognito;
        _startUrl = startUrl;
        _profileDir = incognito
            ? Path.Combine(Path.GetTempPath(), "R1-incognito-" + Guid.NewGuid().ToString("N")[..8])
            : app.UserDataDir;

        Text = incognito ? "R Browser — Incognito" : "R Browser";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 840);
        MinimumSize = new Size(760, 500);
        BackColor = Theme.Bg0;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        KeyPreview = true;
        AllowDrop = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _fTab = Theme.Ui(9f);
        _fUi = Theme.Ui(10f);
        _fSmall = Theme.Ui(8.6f);
        _fTiny = Theme.Ui(7.4f, FontStyle.Bold);
        _fUrl = Theme.Ui(9f);
        _fTitle = Theme.Display(11f, FontStyle.Bold);

        BuildSurface();
        RecalcLayout();

        _tick.Tick += (_, _) =>
        {
            bool dirty = false;
            if (_tabs.Any(t => t.Loading))
            {
                _spin = (_spin + 26) % 360;
                dirty = true;
            }
            if (dirty)
            {
                if (_app.Settings.VerticalTabs)
                {
                    Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ToolbarH * Sc)));
                    _tabSidebar?.Invalidate();
                }
                else
                {
                    Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ChromeH * Sc)));
                }
            }
        };
        _tick.Start();

        _sleepTick.Tick += async (_, _) => await SleepIdleTabsAsync();
        _sleepTick.Start();

        _shortcutFilter = new ShortcutFilter(this);
        Application.AddMessageFilter(_shortcutFilter);

        _app.EngineLoaded += () =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(async () =>
                {
                    foreach (var tab in _tabs)
                    {
                        if (tab.Blocker != null && !tab.Disposed)
                        {
                            await tab.Blocker.RefreshCssAsync();
                        }
                    }
                    Invalidate(Rectangle.Ceiling(_rShield));
                });
            }
            catch { }
        };

        Deactivate += (_, _) => { if (_editing && !_addressBox.Focused) CancelEdit(); };
    }

    private sealed class ShortcutFilter : IMessageFilter
    {
        private readonly MainForm _owner;
        public ShortcutFilter(MainForm owner) => _owner = owner;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x0100 && m.Msg != 0x0104) return false;
            if (_owner.IsDisposed || !_owner.ContainsFocus) return false;
            if (Form.ActiveForm != _owner) return false;

            var key = (Keys)(int)m.WParam;
            bool ctrl = (Control.ModifierKeys & Keys.Control) != 0;
            bool shift = (Control.ModifierKeys & Keys.Shift) != 0;
            bool alt = (Control.ModifierKeys & Keys.Alt) != 0;
            if (!ctrl && !alt && key is not (Keys.F5 or Keys.F12 or Keys.F11 or Keys.Escape)) return false;
            return _owner.HandleShortcut(key, ctrl, shift, alt);
        }
    }

    private float Sc => DeviceDpi / 96f;

    private BrowserTab? Active => _active >= 0 && _active < _tabs.Count ? _tabs[_active] : null;

    private void BuildSurface()
    {
        _contentHost = new Panel { BackColor = Color.FromArgb(8, 9, 12), Dock = DockStyle.None };
        _webHost = new Panel { BackColor = Color.FromArgb(8, 9, 12) };
        _contentHost.Controls.Add(_webHost);

        _tabSidebar = new TabSidebar(_app)
        {
            Visible = _app.Settings.VerticalTabs,
            Collapsed = _app.Settings.VerticalTabsCollapsed,
            Width = (int)((_app.Settings.VerticalTabsCollapsed ? 48 : 210) * Sc)
        };
        _tabSidebar.NewTabRequested += () => _ = NewTabAsync(null, true);
        _tabSidebar.TabActivated += ti => ActivateTab(ti);
        _tabSidebar.TabClosed += ti => CloseTab(ti);
        _tabSidebar.TabAudioToggled += ti =>
        {
            if (ti >= 0 && ti < _tabs.Count)
            {
                var core = _tabs[ti].View?.CoreWebView2;
                if (core != null) core.IsMuted = !core.IsMuted;
            }
        };
        _tabSidebar.TabMenuRequested += (ti, pt) => ShowTabMenu(ti, pt);
        _tabSidebar.TabReordered += (from, to) => ReorderTabs(from, to);
        _tabSidebar.CollapseToggled += () =>
        {
            _app.Settings.VerticalTabsCollapsed = !_app.Settings.VerticalTabsCollapsed;
            _app.SaveSettings();
            _tabSidebar.Collapsed = _app.Settings.VerticalTabsCollapsed;
            _tabSidebar.Width = (int)((_app.Settings.VerticalTabsCollapsed ? 48 : 210) * Sc);
            LayoutContent();
            Invalidate();
        };
        _tabSidebar.LayoutSwitchRequested += () => ToggleVerticalTabs(false);
        _contentHost.Controls.Add(_tabSidebar);

        _side = new SidePanel(_app) { Visible = false };
        _side.CloseRequested += () => { _side.Visible = false; LayoutContent(); RecalcLayout(); Invalidate(); };
        _side.OpenUrl += url => { Navigate(NormalizeInput(url)); };
        _side.OpenUrlInNewTab += url =>
        {
            _ = NewTabAsync(NormalizeInput(url), false);
            ShowToast("Opened in background tab");
        };
        _side.DeleteRequested += DeleteSideItem;
        _side.ToggleSite += () =>
        {
            var tab = Active;
            if (tab == null) return;
            var host = HostOf(tab.Url);
            if (string.IsNullOrEmpty(host)) return;
            if (_app.Settings.WhitelistActive(host)) _app.Settings.Whitelist.Remove(host);
            else _app.Settings.Whitelist.Add(host);
            _app.SaveSettings();
            ShowToast(_app.Settings.WhitelistActive(host) ? "Ads allowed on " + host : "Shields up on " + host);
            tab.View?.CoreWebView2?.Reload();
            RefreshSidePanel();
            Invalidate();
        };
        _contentHost.Controls.Add(_side);

        _findBar = new FindBar { Visible = false };
        _findBar.CloseRequested += () => { _findBar.Visible = false; LayoutContent(); RecalcLayout(); Invalidate(); FocusWeb(); };
        _findBar.QueryChanged += q => FindInPage(q, forward: true, first: true);
        _findBar.NextRequested += () => FindInPage(_findBar.Query, forward: true, first: false);
        _findBar.PrevRequested += () => FindInPage(_findBar.Query, forward: false, first: false);
        _contentHost.Controls.Add(_findBar);

        Controls.Add(_contentHost);

        _addressBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(23, 27, 35),
            ForeColor = Theme.Text,
            Font = _fUrl,
            Visible = false,
            HideSelection = false,
            AutoSize = false
        };
        _addressBox.KeyDown += AddressBox_KeyDown;
        _addressBox.TextChanged += (_, _) => UpdateSuggestions();
        _addressBox.LostFocus += (_, _) =>
        {
            if (_editing && !_addressBox.Focused && (_suggest == null || !_suggest.HasFocus))
                CancelEdit();
        };
        Controls.Add(_addressBox);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.EnableDarkChrome(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Environment.GetEnvironmentVariable("R1_OFFSCREEN") == "1")
        {
            // Test hook: keep the window visible but away from the desktop so automation
            // never steals focus or receives stray keystrokes.
            Location = new Point(-32000, 0);
            Size = new Size(1280, 840);
        }
        _ = InitAsync();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateActiveMemoryTarget();
        RecalcLayout();
        LayoutContent();
        if (_editing) PositionAddressBox();
        Invalidate();
    }

    private void LayoutContent()
    {
        if (_contentHost == null || _webHost == null || _side == null || _findBar == null) return;
        bool vert = _app.Settings.VerticalTabs;
        int top = _chromeHidden ? 0 : (int)((vert ? ToolbarH : ChromeH) * Sc);
        int w = ClientSize.Width;
        int h = Math.Max(0, ClientSize.Height - top);
        int tabSidebarW = (vert && _tabSidebar != null && _tabSidebar.Visible) ? _tabSidebar.Width : 0;
        int sideW = _side.Visible ? (int)(384 * Sc) : 0;
        int findH = _findBar.Visible ? (int)(46 * Sc) : 0;

        _contentHost.SetBounds(0, top, w, h);

        bool sideOnLeft = _app.Settings.SidePanelLeft;
        int webLeft = tabSidebarW + (sideOnLeft ? sideW : 0);
        int webW = Math.Max(0, w - tabSidebarW - sideW);

        if (vert && _tabSidebar != null)
        {
            _tabSidebar.SetBounds(0, 0, tabSidebarW, h);
            _tabSidebar.BringToFront();
        }

        _webHost.SetBounds(webLeft, 0, webW, Math.Max(0, h - findH));
        _findBar.SetBounds(webLeft, h - findH, webW, findH);

        if (sideOnLeft)
            _side.SetBounds(tabSidebarW, 0, sideW, h);
        else
            _side.SetBounds(w - sideW, 0, sideW, h);
    }

    private void ReorderTabs(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count || to < 0 || to >= _tabs.Count || from == to) return;
        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);
        if (_active == from) _active = to;
        else if (_active > from && _active <= to) _active--;
        else if (_active < from && _active >= to) _active++;
        RecalcLayout();
        _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
        Invalidate();
    }

    private void RecalcLayout()
    {
        float w = ClientSize.Width / Sc;
        bool vert = _app.Settings.VerticalTabs;

        if (vert)
        {
            float lightY = (ToolbarH - 10f) / 2f;
            _rMin = new RectangleF(w - 66, lightY, 10, 10);
            _rMax = new RectangleF(w - 46, lightY, 10, 10);
            _rClose = new RectangleF(w - 26, lightY, 10, 10);

            _rBack = new RectangleF(8, 4, 26, 26);
            _rFwd = new RectangleF(36, 4, 26, 26);
            _rReload = new RectangleF(64, 4, 26, 26);

            _rMenu = new RectangleF(w - 96, 4, 26, 26);
            _rToggleLayout = new RectangleF(w - 124, 4, 26, 26);
            _rSidebar = new RectangleF(w - 152, 4, 26, 26);
            _rShield = new RectangleF(w - 180, 4, 26, 26);
            _rStar = new RectangleF(w - 208, 4, 26, 26);

            float addrLeft = 96;
            float addrRight = w - 214;
            _rAddr = new RectangleF(addrLeft, 4, Math.Max(120, addrRight - addrLeft), 26);

            _rNewTab = RectangleF.Empty;
            _tabRects.Clear();
            _tabCloseRects.Clear();
            _tabAudioRects.Clear();
        }
        else
        {
            float lightY = (TabStripH - 10f) / 2f;
            _rMin = new RectangleF(w - 66, lightY, 10, 10);
            _rMax = new RectangleF(w - 46, lightY, 10, 10);
            _rClose = new RectangleF(w - 26, lightY, 10, 10);

            _rBack = new RectangleF(8, 4, 26, 24);
            _rFwd = new RectangleF(36, 4, 26, 24);
            _rSidebar = new RectangleF(70, 4, 30, 24);
            _rToggleLayout = new RectangleF(104, 4, 26, 24);

            float startX = 136;
            float rightEdge = w - 106;
            float avail = Math.Max(120, rightEdge - startX);
            int n = _tabs.Count;
            _tabGap = 4;
            if (n > 0)
            {
                float maxW = 184;
                float fit = (avail - _tabGap * (n - 1)) / n;
                _tabW = Math.Clamp(fit, 106, maxW);
            }

            float totalTabW = n > 0 ? n * (_tabW + _tabGap) - _tabGap : 0;
            _tabScroll = Math.Clamp(_tabScroll, 0, Math.Max(0, totalTabW - avail));

            _tabRects.Clear();
            _tabCloseRects.Clear();
            _tabAudioRects.Clear();
            float x = startX - _tabScroll;
            for (int i = 0; i < n; i++)
            {
                var r = new RectangleF(x, 4, _tabW, 25);
                _tabRects.Add((r, i));
                _tabCloseRects.Add((new RectangleF(r.Right - 26, r.Y + 5, 16, 16), i));
                var t = _tabs[i];
                if (t.IsPlayingAudio || t.IsMuted)
                {
                    _tabAudioRects.Add((new RectangleF(r.Right - 44, r.Y + 5, 16, 16), i));
                }
                x += _tabW + _tabGap;
            }

            float lastRight = n == 0 ? startX : startX - _tabScroll + n * (_tabW + _tabGap) - _tabGap;
            float newTabX = Math.Min(lastRight + 5, w - 106);
            _rNewTab = new RectangleF(Math.Max(startX, newTabX), 4, 24, 24);

            _rReload = new RectangleF(8, 35, 26, 26);
            _rStar = new RectangleF(w - 92, 35, 26, 26);
            _rShield = new RectangleF(w - 62, 35, 26, 26);
            _rMenu = new RectangleF(w - 32, 35, 26, 26);
            _rAddr = new RectangleF(40, 35, Math.Max(120, w - 138), 26);
        }
    }

    // ---------------- painting ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var state = g.Save();
        try
        {
        g.ScaleTransform(Sc, Sc);

        float w = ClientSize.Width / Sc;
        float h = ClientSize.Height / Sc;

        if (!_chromeHidden)
        {
            bool vert = _app.Settings.VerticalTabs;
            float topBarH = vert ? ToolbarH : ChromeH;
            PaintBackdrop(g, w, h, topBarH);
            if (!vert)
            {
                PaintTabs(g, w);
            }
            PaintToolbar(g, w);
            PaintTrafficLights(g);
            PaintSidebarButton(g);
            PaintToggleLayoutButton(g);
        }

        }
        finally { g.Restore(state); }
    }

    private void PaintBackdrop(Graphics g, float w, float h, float barH)
    {
        using (var lg = new LinearGradientBrush(new RectangleF(0, 0, w, barH),
                   Color.FromArgb(24, 26, 32), Color.FromArgb(14, 15, 19), 90f))
            g.FillRectangle(lg, 0, 0, w, barH);

        Theme.Bloom(g, new RectangleF(-w * 0.15f, -190, w * 0.75f, 300), Color.FromArgb(12, 160, 176, 210));
        Theme.Bloom(g, new RectangleF(w * 0.55f, -210, w * 0.6f, 290), Color.FromArgb(7, 140, 180, 170));

        using (var b = new SolidBrush(Theme.Bg0))
            g.FillRectangle(b, 0, barH, w, Math.Max(0, h - barH));

        using (var pen = new Pen(Color.FromArgb(24, 255, 255, 255), 1f))
            g.DrawLine(pen, 0, barH - 0.5f, w, barH - 0.5f);
    }

    private void PaintToggleLayoutButton(Graphics g)
    {
        bool vert = _app.Settings.VerticalTabs;
        if (_hot == HitKind.ToggleLayout)
            Theme.FillRound(g, RectangleF.Inflate(_rToggleLayout, 1, 1), 7, Color.FromArgb(24, 255, 255, 255));
        Icons.Draw(g, vert ? IconKind.LayoutHorizontal : IconKind.LayoutVertical,
            IconBox(_rToggleLayout, 16), _hot == HitKind.ToggleLayout ? Theme.Text : Theme.Muted, 1.4f);
    }

    private void PaintTabs(Graphics g, float w)
    {
        var tabClip = g.Save();
        g.SetClip(new RectangleF(110, 0, Math.Max(0, w - 212), TabStripH));
        for (int i = 0; i < _tabRects.Count; i++)
        {
            var (r, index) = _tabRects[i];
            if (r.Right < 112 || r.X > w - 106) continue;
            var tab = _tabs[index];
            bool active = index == _active;
            bool hover = _hot == HitKind.Tab && _hotTab == index;

            if (active)
            {
                Theme.FillRound(g, r, 8, Color.FromArgb(22, 255, 255, 255));
                Theme.StrokeRound(g, r, 8, Color.FromArgb(22, 255, 255, 255));
            }
            else if (hover)
            {
                Theme.FillRound(g, r, 8, Color.FromArgb(16, 255, 255, 255));
            }

            var iconRect = new RectangleF(r.X + 9, r.Y + 6, 13, 13);
            if (tab.Loading)
                PaintSpinner(g, RectangleF.Inflate(iconRect, -1.5f, -1.5f), active ? Theme.Accent : Theme.Muted, _spin);
            else if (!TryPaintTabIcon(g, tab, iconRect))
            {
                Icons.Draw(g, tab.IsInternal ? IconKind.Spark : IconKind.Globe, iconRect,
                    active ? Theme.Text : Theme.Muted, 1.3f);
            }

            bool showAudio = tab.IsPlayingAudio || tab.IsMuted;
            float textRight = (active || hover) ? r.Right - 30 : r.Right - 12;
            if (showAudio) textRight -= 18;
            var titleRect = new RectangleF(r.X + 32, r.Y + 1, Math.Max(10, textRight - (r.X + 32)), r.Height - 2);
            Color titleColor = active ? Theme.Text : tab.Hibernated ? Theme.Dim : Theme.Muted;
            DrawText(g, tab.Title, _fTab, titleRect, titleColor, StringAlignment.Near);

            if (showAudio)
            {
                var ar = new RectangleF((active || hover) ? r.Right - 44 : r.Right - 22, r.Y + 6, 13, 13);
                if (_hot == HitKind.TabAudio && _hotTab == index)
                {
                    Theme.FillRound(g, RectangleF.Inflate(ar, 3, 3), 6, Color.FromArgb(48, 255, 255, 255));
                }
                Icons.Draw(g, tab.IsMuted ? IconKind.VolumeMute : IconKind.Volume, ar,
                    tab.IsMuted ? Theme.Danger : Theme.Aqua, 1.4f);
            }

            if (active || hover)
            {
                var cr = new RectangleF(r.Right - 24, r.Y + 6, 13, 13);
                if (_hot == HitKind.TabClose && _hotTab == index)
                {
                    Theme.FillRound(g, RectangleF.Inflate(cr, 3, 3), 6, Color.FromArgb(48, 255, 255, 255));
                }
                Icons.Draw(g, IconKind.Close, cr, active || hover ? Theme.Text : Theme.Muted, 1.4f);
            }
        }

        g.Restore(tabClip);
        // new tab button
        if (_hot == HitKind.NewTab) Theme.FillRound(g, RectangleF.Inflate(_rNewTab, 2, 2), 7, Color.FromArgb(20, 255, 255, 255));
        Icons.Draw(g, IconKind.Plus, IconBox(_rNewTab, 14), _hot == HitKind.NewTab ? Theme.Text : Theme.Muted, 1.6f);
    }

    private bool TryPaintTabIcon(Graphics g, BrowserTab tab, RectangleF rect)
    {
        var image = tab.Icon;
        if (image == null) return false;
        try { g.DrawImage(image, rect); return true; }
        catch (ArgumentException) { }
        catch (ExternalException) { }
        // A stale/invalid favicon must never escape OnPaint: WinForms replaces the
        // entire control with its red-X error surface after a paint exception.
        foreach (var t in _tabs) if (ReferenceEquals(t.Icon, image)) t.Icon = null;
        foreach (var key in _icons.Where(p => ReferenceEquals(p.Value, image)).Select(p => p.Key).ToArray())
            _icons.Remove(key);
        Log.Write("Discarded invalid favicon while painting");
        return false;
    }

    private void PaintToolbar(Graphics g, float w)
    {
        var tab = Active;

        PaintNavButton(g, _rBack, IconKind.Back, tab?.View?.CanGoBack == true, HitKind.Back);
        PaintNavButton(g, _rFwd, IconKind.Forward, tab?.View?.CanGoForward == true, HitKind.Fwd);

        if (tab?.Loading == true)
            PaintSpinner(g, IconBox(_rReload, 15), _hot == HitKind.Reload ? Theme.Text : Theme.Muted, _spin);
        else
            PaintNavButton(g, _rReload, IconKind.Reload, tab != null, HitKind.Reload);

        // address pill
        bool hot = _hot == HitKind.Address || _editing;
        Theme.FillRound(g, _rAddr, 10, _editing ? Color.FromArgb(84, 6, 8, 12) : Color.FromArgb(56, 6, 8, 12));
        Theme.StrokeRound(g, _rAddr, 10, _editing ? Color.FromArgb(120, Theme.Accent) : Color.FromArgb(20, 255, 255, 255),
            _editing ? 1.4f : 1f);
        if (hot && !_editing) Theme.StrokeRound(g, _rAddr, 10, Color.FromArgb(34, 255, 255, 255), 1f);

        var iconRect = new RectangleF(_rAddr.X + 10, _rAddr.Y + 7, 12, 12);
        bool secure = tab != null && tab.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (tab?.IsInternal == true) Icons.Draw(g, IconKind.Spark, iconRect, Theme.Aqua, 1.4f);
        else if (secure) Icons.Draw(g, IconKind.Lock, iconRect, Theme.Muted, 1.5f);
        else Icons.Draw(g, IconKind.Globe, iconRect, Theme.Muted, 1.4f);

        if (!_editing)
        {
            var textRect = new RectangleF(_rAddr.X + 29, _rAddr.Y, _rAddr.Width - 46, _rAddr.Height);
            var display = tab == null ? "" : DisplayUrl(tab.Url);
            if (string.IsNullOrEmpty(display)) display = "Search or enter address";
            int schemeEnd = display.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0)
            {
                using var typo = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
                var scheme = display[..(schemeEnd + 3)];
                float schemeW = g.MeasureString(scheme, _fUrl, PointF.Empty, typo).Width;
                DrawText(g, scheme, _fUrl, textRect, Theme.Dim, StringAlignment.Near);
                var restRect = new RectangleF(textRect.X + schemeW, textRect.Y, Math.Max(0, textRect.Width - schemeW), textRect.Height);
                DrawText(g, display[(schemeEnd + 3)..], _fUrl, restRect, tab == null ? Theme.Dim : Theme.Text, StringAlignment.Near);
            }
            else
            {
                DrawText(g, display, _fUrl, textRect, string.IsNullOrEmpty(tab?.Url) ? Theme.Dim : Theme.Text, StringAlignment.Near);
            }
        }

        // bookmark star
        bool marked = tab != null && _app.Bookmarks.Contains(RealUrl(tab.Url));
        PaintIconButton(g, _rStar, marked ? IconKind.StarFilled : IconKind.Star, marked ? Theme.Accent : Theme.Muted, HitKind.Star);

        // shield
        bool whitelisted = _app.Settings.WhitelistActive(HostOf(tab?.Url ?? ""));
        var shieldColor = !_app.Settings.AdblockEnabled ? Theme.Dim : whitelisted ? Theme.Dim : Theme.Aqua;
        PaintIconButton(g, _rShield, IconKind.ShieldFilled, shieldColor, HitKind.Shield);
        int blocked = tab?.Blocker?.BlockedCount ?? 0;
        if (blocked > 0)
        {
            var badgeRect = new RectangleF(_rShield.Right - 10, _rShield.Y, 13, 13);
            Theme.FillRound(g, badgeRect, 7.5f, Color.FromArgb(230, 36, 44, 60));
            Theme.StrokeRound(g, badgeRect, 7.5f, Color.FromArgb(90, Theme.Aqua));
            DrawText(g, blocked > 99 ? "99+" : blocked.ToString(), _fTiny, badgeRect, Theme.Aqua, StringAlignment.Center);
        }

        PaintIconButton(g, _rMenu, IconKind.Menu, Theme.Muted, HitKind.Menu);
    }

    private static RectangleF IconBox(RectangleF r, float size) =>
        new(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);

    private void PaintNavButton(Graphics g, RectangleF r, IconKind icon, bool enabled, HitKind kind)
    {
        if (kind is HitKind.Back or HitKind.Fwd)
        {
            Theme.FillRound(g, r, 7, Color.FromArgb(_hot == kind && enabled ? 24 : 12, 255, 255, 255));
            Theme.StrokeRound(g, r, 7, Color.FromArgb(18, 255, 255, 255));
        }
        else if (enabled && _hot == kind)
            Theme.FillRound(g, r, 8, Color.FromArgb(22, 255, 255, 255));
        Icons.Draw(g, icon, IconBox(r, 16), enabled ? Theme.Text : Color.FromArgb(62, 68, 82), 1.8f);
    }

    private void PaintIconButton(Graphics g, RectangleF r, IconKind icon, Color color, HitKind kind)
    {
        if (_hot == kind) Theme.FillRound(g, RectangleF.Inflate(r, 1, 1), 8, Color.FromArgb(24, 255, 255, 255));
        Icons.Draw(g, icon, IconBox(r, 16), color, 1.5f);
    }

    private void PaintSidebarButton(Graphics g)
    {
        bool selected = _side.Visible;
        Theme.FillRound(g, _rSidebar, 7, Color.FromArgb(_hot == HitKind.Sidebar || selected ? 26 : 12, 255, 255, 255));
        Theme.StrokeRound(g, _rSidebar, 7, Color.FromArgb(18, 255, 255, 255));
        var box = new RectangleF(_rSidebar.X + 7, _rSidebar.Y + 6, 16, 12);
        using var pen = new Pen(selected ? Theme.Text : Theme.Muted, 1.3f);
        using var outline = Theme.Round(box, 2);
        g.DrawPath(pen, outline);
        g.DrawLine(pen, box.X + 7, box.Y, box.X + 7, box.Bottom);
        for (int i = 0; i < 3; i++)
            g.DrawLine(pen, box.X + 2.5f, box.Y + 4 + i * 3, box.X + 4.5f, box.Y + 4 + i * 3);
    }

    private void PaintTrafficLights(Graphics g)
    {
        foreach (var (r, kind, color) in new[] {
            (_rClose, HitKind.Close, Color.FromArgb(255, 95, 87)),
            (_rMin, HitKind.Min, Color.FromArgb(254, 188, 46)),
            (_rMax, HitKind.Max, Color.FromArgb(40, 200, 64)) })
        {
            using var fill = new SolidBrush(_hot == kind ? Theme.Mix(color, Color.White, 0.12f) : color);
            g.FillEllipse(fill, r);
            if (_hot == kind)
                Icons.Draw(g, kind == HitKind.Close ? IconKind.Close : kind == HitKind.Min ? IconKind.Minimize : IconKind.Maximize,
                    RectangleF.Inflate(r, -2, -2), Theme.Bg0, 2f);
        }
    }

    private void PaintSpinner(Graphics g, RectangleF r, Color color, float angle)
    {
        using var pen = new Pen(color, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, r, angle, 110);
        g.DrawArc(pen, r, angle + 180, 110);
    }

    private void DrawText(Graphics g, string text, Font font, RectangleF rect, Color color, StringAlignment align)
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

    // ---------------- input ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = new PointF(e.X / Sc, e.Y / Sc);

        if (_dragTab >= 0 && e.Button == MouseButtons.Left && !_app.Settings.VerticalTabs)
        {
            if (!_isDraggingTab && Math.Abs(p.X - _dragStartPoint.X) > 6)
            {
                _isDraggingTab = true;
            }
            if (_isDraggingTab)
            {
                HitAt(p, out int target);
                if (target >= 0 && target != _dragTab)
                {
                    ReorderTabs(_dragTab, target);
                    _dragTab = target;
                }
                return;
            }
        }

        float topH = _app.Settings.VerticalTabs ? ToolbarH : ChromeH;
        if (e.Y < topH * Sc && !_chromeHidden)
        {
            var kind = HitAt(p, out int ti);
            if (kind != _hot || ti != _hotTab)
            {
                _hot = kind;
                _hotTab = ti;
                Cursor = kind is HitKind.None or HitKind.Tab ? Cursors.Default : Cursors.Hand;
                Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(topH * Sc)));
            }
        }
        else if (_hot != HitKind.None)
        {
            _hot = HitKind.None;
            _hotTab = -1;
            Cursor = Cursors.Default;
            Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(topH * Sc)));
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragTab = -1;
        _isDraggingTab = false;
    }

    private HitKind HitAt(PointF p, out int tabIndex)
    {
        tabIndex = -1;
        if (_rClose.Contains(p)) return HitKind.Close;
        if (_rMax.Contains(p)) return HitKind.Max;
        if (_rMin.Contains(p)) return HitKind.Min;
        if (_rToggleLayout.Contains(p)) return HitKind.ToggleLayout;
        if (_rSidebar.Contains(p)) return HitKind.Sidebar;

        bool vert = _app.Settings.VerticalTabs;
        if (vert)
        {
            if (_rBack.Contains(p)) return HitKind.Back;
            if (_rFwd.Contains(p)) return HitKind.Fwd;
            if (_rReload.Contains(p)) return HitKind.Reload;
            if (_rAddr.Contains(p)) return HitKind.Address;
            if (_rStar.Contains(p)) return HitKind.Star;
            if (_rShield.Contains(p)) return HitKind.Shield;
            if (_rMenu.Contains(p)) return HitKind.Menu;
            return HitKind.None;
        }

        if (_rNewTab.Contains(p)) return HitKind.NewTab;
        if (_rBack.Contains(p)) return HitKind.Back;
        if (_rFwd.Contains(p)) return HitKind.Fwd;

        if (p.Y < TabStripH)
        {
            if (p.X < 136 || p.X > ClientSize.Width / Sc - 106) return HitKind.None;
            for (int i = 0; i < _tabCloseRects.Count; i++)
            {
                var (cr, index) = _tabCloseRects[i];
                if (_tabs.Count > index && (_active == index || _hotTab == index) && cr.Contains(p))
                { tabIndex = index; return HitKind.TabClose; }
            }
            for (int i = 0; i < _tabAudioRects.Count; i++)
            {
                var (ar, index) = _tabAudioRects[i];
                if (_tabs.Count > index)
                {
                    bool showClose = (_active == index || _hotTab == index);
                    var effectiveAr = showClose ? ar : new RectangleF(ar.X + 22, ar.Y, ar.Width, ar.Height);
                    if (effectiveAr.Contains(p))
                    {
                        tabIndex = index;
                        return HitKind.TabAudio;
                    }
                }
            }
            for (int i = 0; i < _tabRects.Count; i++)
            {
                var (r, index) = _tabRects[i];
                if (r.Contains(p)) { tabIndex = index; return HitKind.Tab; }
            }
            return HitKind.None;
        }

        if (_rReload.Contains(p)) return HitKind.Reload;
        if (_rAddr.Contains(p)) return HitKind.Address;
        if (_rStar.Contains(p)) return HitKind.Star;
        if (_rShield.Contains(p)) return HitKind.Shield;
        if (_rMenu.Contains(p)) return HitKind.Menu;
        return HitKind.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var p = new PointF(e.X / Sc, e.Y / Sc);
        if (_editing) CommitAddress();
        if (_suggest != null) HideSuggestions();

        if (e.Button == MouseButtons.Middle)
        {
            var mk = HitAt(p, out int mti);
            if ((mk == HitKind.Tab || mk == HitKind.TabClose || mk == HitKind.TabAudio) && mti >= 0) CloseTab(mti);
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            var rk = HitAt(p, out int rti);
            if ((rk == HitKind.Tab || rk == HitKind.TabClose || rk == HitKind.TabAudio) && rti >= 0)
            {
                ShowTabMenu(rti, PointToScreen(e.Location));
                return;
            }
        }
        if (e.Button != MouseButtons.Left) return;

        var kind = HitAt(p, out int ti);
        switch (kind)
        {
            case HitKind.Close: Close(); return;
            case HitKind.Max: WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; return;
            case HitKind.Min: WindowState = FormWindowState.Minimized; return;
            case HitKind.ToggleLayout: ToggleVerticalTabs(!_app.Settings.VerticalTabs); return;
            case HitKind.Sidebar: OpenPanel(_side.Visible ? _side.Mode : SidePanelMode.Bookmarks); return;
            case HitKind.NewTab: _ = NewTabAsync(null, true); return;
            case HitKind.TabClose: CloseTab(ti); return;
            case HitKind.TabAudio:
                if (ti >= 0 && ti < _tabs.Count)
                {
                    var core = _tabs[ti].View?.CoreWebView2;
                    if (core != null) core.IsMuted = !core.IsMuted;
                }
                return;
            case HitKind.Tab:
                _dragTab = ti;
                _dragStartPoint = p;
                _isDraggingTab = false;
                ActivateTab(ti);
                return;
            case HitKind.Back: if (Active?.View?.CanGoBack == true) Active.View.GoBack(); return;
            case HitKind.Fwd: if (Active?.View?.CanGoForward == true) Active.View.GoForward(); return;
            case HitKind.Reload:
                var t = Active;
                if (t == null) return;
                if (t.Loading) t.View?.CoreWebView2?.Stop(); else t.View?.CoreWebView2?.Reload();
                return;
            case HitKind.Address: BeginEdit(); return;
            case HitKind.Star: ToggleBookmark(); return;
            case HitKind.Shield: ToggleShieldPanel(); return;
            case HitKind.Menu: ShowMenu(); return;
        }

        float dragH = _app.Settings.VerticalTabs ? ToolbarH : TabStripH;
        if (p.Y < dragH && e.Clicks == 2)
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            return;
        }
        if (p.Y < dragH)
        {
            Native.DragWindow(this);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_NCHITTEST = 0x84;

        if (m.Msg == WM_GETMINMAXINFO)
        {
            try
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
                var screen = Screen.FromHandle(Handle);
                var work = screen.WorkingArea;
                var bounds = screen.Bounds;
                mmi.ptMaxPosition.x = Math.Abs(work.Left - bounds.Left);
                mmi.ptMaxPosition.y = Math.Abs(work.Top - bounds.Top);
                mmi.ptMaxSize.x = work.Width;
                mmi.ptMaxSize.y = work.Height;
                Marshal.StructureToPtr(mmi, m.LParam, true);
            }
            catch { }
            return;
        }

        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || WindowState == FormWindowState.Maximized) return;

        int screenX = (short)(m.LParam.ToInt64() & 0xFFFF);
        int screenY = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
        var pt = PointToClient(new Point(screenX, screenY));
        int edge = (int)(4 * Sc);
        bool left = pt.X <= edge, right = pt.X >= ClientSize.Width - edge;
        bool top = pt.Y <= edge, bottom = pt.Y >= ClientSize.Height - edge;

        if (left && top) m.Result = (IntPtr)13;       // HTTOPLEFT
        else if (right && top) m.Result = (IntPtr)14; // HTTOPRIGHT
        else if (left && bottom) m.Result = (IntPtr)16;
        else if (right && bottom) m.Result = (IntPtr)17;
        else if (left) m.Result = (IntPtr)10;
        else if (right) m.Result = (IntPtr)11;
        else if (top) m.Result = (IntPtr)12;
        else if (bottom) m.Result = (IntPtr)15;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RecalcLayout();
        LayoutContent();
        Invalidate();
    }

    // ---------------- address bar ----------------

    private void BeginEdit()
    {
        var tab = Active;
        if (tab == null || _chromeHidden) return;
        _editing = true;
        _addressBox.Text = DisplayUrl(tab.Url);
        _addressBox.Visible = true;
        _addressBox.BringToFront();
        PositionAddressBox();
        _addressBox.Focus();
        _addressBox.SelectAll();
        Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)((_app.Settings.VerticalTabs ? ToolbarH : ChromeH) * Sc)));
    }

    private void PositionAddressBox()
    {
        int x = (int)((_rAddr.X + 32) * Sc);
        int y = (int)((_rAddr.Y + 4) * Sc);
        int wid = (int)((_rAddr.Width - 46) * Sc);
        _addressBox.SetBounds(x, y, Math.Max(50, wid), (int)(18 * Sc));
    }

    private void CancelEdit()
    {
        _editing = false;
        _addressBox.Visible = false;
        HideSuggestions();
        Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)((_app.Settings.VerticalTabs ? ToolbarH : ChromeH) * Sc)));
    }

    private void CommitAddress()
    {
        var text = _addressBox.Text;
        _editing = false;
        _addressBox.Visible = false;
        HideSuggestions();
        Navigate(NormalizeInput(text));
        Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)((_app.Settings.VerticalTabs ? ToolbarH : ChromeH) * Sc)));
    }

    private void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            var chosen = _suggest?.SelectedValue;
            if (!string.IsNullOrEmpty(chosen))
            {
                _editing = false;
                _addressBox.Visible = false;
                HideSuggestions();
                Navigate(chosen);
                Invalidate();
            }
            else CommitAddress();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            CancelEdit();
            FocusWeb();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Down && _suggest != null)
        {
            _suggest.MoveSelection(1);
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Up && _suggest != null)
        {
            _suggest.MoveSelection(-1);
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Tab)
        {
            if (_suggest?.SelectedValue is { Length: > 0 } v)
            {
                _addressBox.Text = v;
                _addressBox.SelectionStart = _addressBox.TextLength;
            }
            e.SuppressKeyPress = true;
        }
    }

    private void UpdateSuggestions()
    {
        if (!_editing) return;
        var q = _addressBox.Text.Trim();
        if (q.Length < 2)
        {
            HideSuggestions();
            return;
        }
        var items = new List<(string, string, string)>();
        foreach (var h in _app.History.Search(q, 6))
            items.Add((string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title, h.Url, h.Url));
        if (items.Count == 0 && (q.Contains('.') || q.StartsWith("http")))
            items.Add((q, "Open address", NormalizeInput(q)));
        if (items.Count == 0)
            items.Add(("Search for “" + q + "”", _app.Settings.SearchTemplate.Contains("duckduckgo") ? "DuckDuckGo" : "Search",
                string.Format(_app.Settings.SearchTemplate, Uri.EscapeDataString(q))));

        var loc = PointToScreen(new Point((int)(_rAddr.X * Sc), (int)(_rAddr.Bottom * Sc) + (int)(6 * Sc)));
        if (_suggest == null)
        {
            _suggest = new SuggestPopup();
            _suggest.ItemChosen += (value) =>
            {
                _editing = false;
                _addressBox.Visible = false;
                HideSuggestions();
                Navigate(value);
                Invalidate();
            };
        }
        _suggest.ShowItems(items, loc, (int)(_rAddr.Width * Sc));
    }

    private void HideSuggestions()
    {
        _suggest?.HideAll();
    }

    private void FocusWeb()
    {
        var tab = Active;
        if (tab == null || tab.View == null) return;
        try { tab.View.Focus(); } catch { }
    }

    // ---------------- helpers ----------------

    private static string DisplayUrl(string url)
    {
        if (url.StartsWith("https://r1.internal/newtab.html", StringComparison.OrdinalIgnoreCase)) return "r1://newtab";
        if (url.StartsWith("https://r1.internal/settings.html", StringComparison.OrdinalIgnoreCase)) return "r1://settings";
        return url;
    }

    private static string RealUrl(string url)
    {
        if (url.StartsWith("r1://newtab", StringComparison.OrdinalIgnoreCase)) return "https://r1.internal/newtab.html";
        if (url.StartsWith("r1://settings", StringComparison.OrdinalIgnoreCase)) return "https://r1.internal/settings.html";
        return url;
    }

    private string NormalizeInput(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return "r1://newtab";
        if (text.StartsWith("r1://", StringComparison.OrdinalIgnoreCase)) return text;
        if (text.Contains("://", StringComparison.Ordinal)) return text;
        if (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return "http://" + text;
        if (text.Contains('.') && !text.Contains(' ') && text.Length < 256) return "https://" + text;
        return string.Format(_app.Settings.SearchTemplate, Uri.EscapeDataString(text));
    }

    private static string HostOf(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.StartsWith("r1://", StringComparison.OrdinalIgnoreCase)) return "r1.internal";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
    }

    private void ShowToast(string text)
    {
        _toast ??= new ToastForm();
        int bottomOffset = (_findBar?.Visible == true) ? (int)(46 * Sc) + 24 : 24;
        _toast.ShowMessage(this, text, bottomOffset);
    }
}
