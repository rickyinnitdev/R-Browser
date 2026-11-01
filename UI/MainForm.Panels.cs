using System.Drawing;
using System.Windows.Forms;

namespace R1.UI;

public sealed class DownloadEntry
{
    public string Name = "";
    public string Path = "";
    public string State = "Starting";
    public long Received;
    public long Total;
    public long Speed;
    public CancellationTokenSource? Cts;
}

public sealed partial class MainForm
{
    private void OpenPanel(SidePanelMode mode)
    {
        if (_side.Visible && _side.Mode == mode)
        {
            _side.Visible = false;
        }
        else
        {
            _side.Mode = mode;
            _side.Visible = true;
            RefreshSidePanel();
        }
        LayoutContent();
        RecalcLayout();
        Invalidate();
    }

    private void ToggleShieldPanel()
    {
        _side.SiteHost = HostOf(Active?.Url ?? "");
        OpenPanel(SidePanelMode.Shields);
    }

    private void ShowFindBar()
    {
        _findBar.Visible = true;
        LayoutContent();
        _findBar.FocusQuery();
        Invalidate();
    }

    private void DeleteSideItem(int index)
    {
        if (index < 0 || index >= _side.Items.Count) return;
        var item = _side.Items[index];
        if (_side.Mode == SidePanelMode.Bookmarks)
        {
            var b = _app.Bookmarks.Items.FirstOrDefault(x => x.Url == item.Url);
            if (b != null) _app.Bookmarks.Remove(b);
        }
        else if (_side.Mode == SidePanelMode.History)
        {
            var h = _app.History.Items.FirstOrDefault(x => x.Url == item.Url);
            if (h != null) _app.History.Items.Remove(h);
            _app.History.Save();
        }
        else if (_side.Mode == SidePanelMode.Downloads)
        {
            var dl = _downloads.FirstOrDefault(d => d.Path == item.Url);
            if (dl != null)
            {
                try { dl.Cts?.Cancel(); } catch { }
                _downloads.Remove(dl);
            }
        }
        _side.DeleteAt(index);
        Invalidate();
    }

    private void RefreshSidePanel()
    {
        if (!_side.Visible) return;
        var tab = Active;

        switch (_side.Mode)
        {
            case SidePanelMode.Bookmarks:
                _side.SetItems("Bookmarks", $"{_app.Bookmarks.Items.Count} saved",
                    _app.Bookmarks.Items.Select(b => new PanelItem
                    {
                        Title = string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title,
                        Sub = b.Url,
                        Url = b.Url
                    }));
                break;

            case SidePanelMode.History:
                _side.SetItems("History", _incognito ? "Not recorded in incognito" : $"{_app.History.Items.Count} entries",
                    _app.History.Items.Take(200).Select(h => new PanelItem
                    {
                        Title = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title,
                        Sub = h.Url,
                        Url = h.Url,
                        Trailing = Relative(h.LastVisit)
                    }));
                break;

            case SidePanelMode.Downloads:
                _side.SetItems("Downloads", _downloads.Count == 0 ? "Nothing downloaded yet" : _downloads.Count + " files",
                    _downloads.Select(d => new PanelItem
                    {
                        Title = d.Name,
                        Sub = d.Speed > 0 && d.State == "Downloading" ? $"{d.State} · {FormatBytes(d.Speed)}/s" : d.State,
                        Url = d.Path,
                        Trailing = d.Total > 0 ? (d.Received * 100 / Math.Max(1, d.Total)) + "%" : (d.Received > 0 ? FormatBytes(d.Received) : ""),
                        Progress = d.Total > 0 ? (double)d.Received / d.Total : (d.State == "Completed" ? 1 : -1)
                    }), keepScroll: true);
                break;

            case SidePanelMode.Shields:
            {
                var host = HostOf(tab?.Url ?? "");
                _side.SiteHost = host;
                int count = tab?.Blocker?.BlockedCount ?? 0;
                var recent = tab?.Blocker?.Recent ?? Array.Empty<string>();
                _side.SetItems("Shields", count == 0 ? "No requests blocked on this tab" : $"{count:N0} requests blocked on this tab",
                    recent.Reverse().Take(120).Select(u => new PanelItem
                    {
                        Title = HostOf(u),
                        Sub = ShortUrl(u)
                    }));
                break;
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1048576) return (bytes / 1024f).ToString("0.0") + " KB";
        if (bytes < 1073741824) return (bytes / 1048576f).ToString("0.0") + " MB";
        return (bytes / 1073741824f).ToString("0.00") + " GB";
    }

    private static string Relative(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m";
        if (d.TotalHours < 24) return (int)d.TotalHours + "h";
        if (d.TotalDays < 7) return (int)d.TotalDays + "d";
        return t.ToString("MMM d");
    }

    private static string ShortUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            var s = u.Host + u.PathAndQuery;
            return s.Length > 90 ? s[..90] + "…" : s;
        }
        catch { return url.Length > 90 ? url[..90] + "…" : url; }
    }

    private void ShowTabMenu(int index, Point screenPt)
    {
        if (index < 0 || index >= _tabs.Count) return;
        var tab = _tabs[index];

        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            BackColor = Color.FromArgb(22, 26, 34),
            ForeColor = Theme.Text,
            Font = _fUi,
            Padding = new Padding(4, 6, 4, 6)
        };

        void Add(string text, string shortcut, Action action)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut, ForeColor = Theme.Text };
            item.Click += (_, _) => action();
            item.Padding = new Padding(6, 3, 6, 3);
            menu.Items.Add(item);
        }
        void Sep() => menu.Items.Add(new ToolStripSeparator());

        Add("Reload", "Ctrl+R", () => tab.View?.CoreWebView2?.Reload());
        Add("Duplicate tab", "", () => _ = NewTabAsync(tab.Url, true));

        bool isMuted = tab.View?.CoreWebView2?.IsMuted ?? false;
        Add(isMuted ? "Unmute tab" : "Mute tab", "", () =>
        {
            var core = tab.View?.CoreWebView2;
            if (core != null) core.IsMuted = !core.IsMuted;
        });

        if (index != _active && !tab.Suspended && tab.View?.CoreWebView2 != null)
        {
            Add("Sleep tab", "", async () =>
            {
                try { tab.Suspended = await tab.View.CoreWebView2.TrySuspendAsync(); Invalidate(); } catch { }
            });
        }

        Sep();
        bool vert = _app.Settings.VerticalTabs;
        Add(vert ? "Turn on top tabs" : "Turn on vertical tabs", "Ctrl+Shift+V", () => ToggleVerticalTabs(!vert));

        if (_recentlyClosed.Count > 0)
        {
            Add("Reopen closed tab", "Ctrl+Shift+T", ReopenClosedTab);
        }

        Sep();
        Add("Close tab", "Ctrl+W", () => CloseTab(index));

        if (_tabs.Count > 1)
        {
            Add("Close other tabs", "", () =>
            {
                var keep = _tabs[index];
                for (int i = _tabs.Count - 1; i >= 0; i--)
                {
                    if (_tabs[i] != keep) CloseTab(i);
                }
            });
        }

        if (index < _tabs.Count - 1)
        {
            Add("Close tabs to the right", "", () =>
            {
                var anchor = _tabs[index];
                int anchorIdx = _tabs.IndexOf(anchor);
                if (anchorIdx < 0) return;
                for (int i = _tabs.Count - 1; i > anchorIdx; i--)
                {
                    CloseTab(i);
                }
            });
        }

        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(screenPt);
    }

    private void ShowMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            BackColor = Color.FromArgb(22, 26, 34),
            ForeColor = Theme.Text,
            Font = _fUi,
            Padding = new Padding(4, 6, 4, 6)
        };

        void Add(string text, Keys shortcut, Action action)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = ShortcutText(shortcut), ForeColor = Theme.Text };
            item.Click += (_, _) => action();
            item.Padding = new Padding(6, 3, 6, 3);
            menu.Items.Add(item);
        }
        void Sep() => menu.Items.Add(new ToolStripSeparator());

        Add("New tab", Keys.Control | Keys.T, () => _ = NewTabAsync(null, true));
        Add("New window", Keys.None, () => BrowserAppContext.RegisterWindow(new MainForm(_app, false, null)));
        Add("New incognito window", Keys.Control | Keys.Shift | Keys.N, () => BrowserAppContext.RegisterWindow(new MainForm(_app, true, null)));
        Sep();
        Add("Bookmarks", Keys.Control | Keys.B, () => OpenPanel(SidePanelMode.Bookmarks));
        Add("History", Keys.Control | Keys.H, () => OpenPanel(SidePanelMode.History));
        Add("Downloads", Keys.Control | Keys.J, () => OpenPanel(SidePanelMode.Downloads));
        Add("Find in page", Keys.Control | Keys.F, ShowFindBar);
        Add("Vertical tabs (sidebar)", Keys.Control | Keys.Shift | Keys.V, () => ToggleVerticalTabs(!_app.Settings.VerticalTabs));
        Sep();
        Add("Adblock shields", Keys.None, ToggleShieldPanel);
        Add("Update filter lists", Keys.None, () => _ = UpdateFiltersAsync());
        Add("Settings", Keys.None, () => Navigate("r1://settings"));
        Sep();
        Add("Zoom in", Keys.Control | Keys.Oemplus, () => SetZoom((Active?.Zoom ?? 1) + 0.1));
        Add("Zoom out", Keys.Control | Keys.OemMinus, () => SetZoom((Active?.Zoom ?? 1) - 0.1));
        Add("Reset zoom", Keys.Control | Keys.D0, () => SetZoom(1.0));
        Sep();
        Add("Developer tools", Keys.F12, ToggleDevTools);
        Add("Exit", Keys.None, Close);

        var pt = PointToScreen(new Point((int)(_rMenu.X * Sc), (int)(_rMenu.Bottom * Sc) + 2));
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(pt);
    }

    private static string ShortcutText(Keys k)
    {
        if (k == Keys.None) return "";
        var parts = new List<string>();
        if ((k & Keys.Control) != 0) parts.Add("Ctrl");
        if ((k & Keys.Shift) != 0) parts.Add("Shift");
        if ((k & Keys.Alt) != 0) parts.Add("Alt");
        var key = k & Keys.KeyCode;
        parts.Add(key switch
        {
            Keys.Oemplus => "+",
            Keys.OemMinus => "−",
            Keys.D0 => "0",
            _ => key.ToString()
        });
        return string.Join("+", parts);
    }
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item?.Selected == true ? Theme.Text : Color.FromArgb(210, 219, 234);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(52, 255, 255, 255), 1f);
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(22, 26, 34);
        public override Color ImageMarginGradientBegin => Color.FromArgb(22, 26, 34);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(22, 26, 34);
        public override Color ImageMarginGradientEnd => Color.FromArgb(22, 26, 34);
        public override Color MenuBorder => Color.FromArgb(52, 255, 255, 255);
        public override Color MenuItemBorder => Color.FromArgb(0, 0, 0, 0);
        public override Color MenuItemSelected => Color.FromArgb(34, 255, 255, 255);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(34, 255, 255, 255);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(34, 255, 255, 255);
        public override Color SeparatorDark => Color.FromArgb(40, 255, 255, 255);
        public override Color SeparatorLight => Color.FromArgb(20, 255, 255, 255);
    }
}
