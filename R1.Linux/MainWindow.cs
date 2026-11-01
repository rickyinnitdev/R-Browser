using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Gtk;
using WebKit;
using R1.Core;

namespace R1.Linux;

internal sealed class MainWindow
{
    private readonly ApplicationWindow _window;
    public ApplicationWindow Window => _window;

    private readonly Application _appInstance;
    private readonly AppState _app;
    private readonly bool _isIncognito;
    private readonly List<BrowserTab> _tabs = new();
    private BrowserTab? _activeTab;

    // UI Widgets
    private readonly Box _rootBox;
    private readonly Box _tabBarBox;
    private readonly Box _navBarBox;
    private readonly Button _backButton;
    private readonly Button _forwardButton;
    private readonly Button _reloadButton;
    private readonly Entry _urlEntry;
    private readonly Button _shieldButton;
    private readonly Label _shieldLabel;
    private readonly Button _newTabButton;
    private readonly Box _webContainer;

    // Background timer for tab lifecycle
    private GLib.SourceFunc? _idleTimer;

    public MainWindow(Application app, AppState appState, bool incognito = false, string? startUrl = null)
    {
        _appInstance = app;
        _app = appState;
        _isIncognito = incognito;

        _window = ApplicationWindow.New(app);
        _window.SetTitle(_isIncognito ? "R Browser (Incognito)" : "R Browser");
        _window.SetDefaultSize(1200, 780);

        // Apply dark-glass theme styling via CSS
        ApplyDarkThemeCss();

        _rootBox = Box.New(Orientation.Vertical, 0);
        _window.SetChild(_rootBox);

        // 1. Top Tab Strip
        _tabBarBox = Box.New(Orientation.Horizontal, 4);
        _tabBarBox.AddCssClass("r1-tabbar");

        _newTabButton = Button.NewWithLabel("+");
        _newTabButton.AddCssClass("r1-tab-btn");
        _newTabButton.OnClicked += (s, e) => AddTab("r1://newtab", true);

        _rootBox.Append(_tabBarBox);

        // 2. Navigation Bar (Address Pill + Controls)
        _navBarBox = Box.New(Orientation.Horizontal, 6);
        _navBarBox.AddCssClass("r1-navbar");

        _backButton = Button.NewWithLabel("‹");
        _backButton.AddCssClass("r1-nav-btn");
        _backButton.OnClicked += (s, e) => { if (_activeTab?.View?.CanGoBack() == true) _activeTab.View.GoBack(); };

        _forwardButton = Button.NewWithLabel("›");
        _forwardButton.AddCssClass("r1-nav-btn");
        _forwardButton.OnClicked += (s, e) => { if (_activeTab?.View?.CanGoForward() == true) _activeTab.View.GoForward(); };

        _reloadButton = Button.NewWithLabel("↻");
        _reloadButton.AddCssClass("r1-nav-btn");
        _reloadButton.OnClicked += (s, e) => _activeTab?.View?.Reload();

        _urlEntry = Entry.New();
        _urlEntry.Hexpand = true;
        _urlEntry.AddCssClass("r1-url-entry");
        _urlEntry.PlaceholderText = "Search the web or enter an address";
        _urlEntry.OnActivate += (s, e) => NavigateToInput(_urlEntry.GetText());

        _shieldButton = Button.New();
        _shieldButton.AddCssClass("r1-shield-btn");
        _shieldLabel = Label.New("🛡 0");
        _shieldButton.SetChild(_shieldLabel);
        _shieldButton.OnClicked += (s, e) => ShowShieldDetails();

        _navBarBox.Append(_backButton);
        _navBarBox.Append(_forwardButton);
        _navBarBox.Append(_reloadButton);
        _navBarBox.Append(_urlEntry);
        _navBarBox.Append(_shieldButton);

        _rootBox.Append(_navBarBox);

        // 3. Central Web Content Container
        _webContainer = Box.New(Orientation.Vertical, 0);
        _webContainer.Vexpand = true;
        _webContainer.Hexpand = true;
        _rootBox.Append(_webContainer);

        // Setup Keyboard Shortcuts via EventControllerKey
        SetupKeyShortcuts();

        // Setup Idle Tab Suspension Timer (every 15s)
        SetupIdleTimer();

        // Initial Tab
        AddTab(startUrl ?? _app.Settings.HomePage, true);
    }

    public void Present() => _window.Present();
    public void Close() => _window.Close();

    private void ApplyDarkThemeCss()
    {
        var css = """
window {
    background-color: #121214;
    color: #e4e4e7;
}

.r1-tabbar {
    background-color: #18181b;
    padding: 6px 8px 0px 8px;
    border-bottom: 1px solid #27272a;
}

.r1-tab-btn {
    background-color: transparent;
    color: #a1a1aa;
    border: none;
    border-radius: 6px 6px 0 0;
    padding: 6px 12px;
    font-size: 13px;
    font-weight: 500;
}

.r1-tab-btn:hover {
    background-color: #27272a;
    color: #f4f4f5;
}

.r1-tab-btn.active {
    background-color: #27272a;
    color: #ffffff;
    border-bottom: 2px solid #6366f1;
}

.r1-tab-close {
    background: transparent;
    border: none;
    color: #71717a;
    margin-left: 6px;
    padding: 0 4px;
    font-size: 11px;
}

.r1-tab-close:hover {
    color: #ef4444;
}

.r1-navbar {
    background-color: #18181b;
    padding: 6px 10px;
    border-bottom: 1px solid #27272a;
}

.r1-nav-btn {
    background: transparent;
    color: #a1a1aa;
    border: none;
    border-radius: 6px;
    font-size: 16px;
    padding: 4px 10px;
}

.r1-nav-btn:hover {
    background-color: #27272a;
    color: #ffffff;
}

.r1-url-entry {
    background-color: #27272a;
    color: #f4f4f5;
    border: 1px solid #3f3f46;
    border-radius: 8px;
    padding: 5px 12px;
    font-size: 13px;
}

.r1-url-entry:focus {
    border-color: #6366f1;
    background-color: #202023;
}

.r1-shield-btn {
    background-color: #27272a;
    border: 1px solid #3f3f46;
    border-radius: 6px;
    color: #34d399;
    padding: 4px 8px;
    font-size: 12px;
}

.r1-shield-btn:hover {
    background-color: #3f3f46;
}
""";

        var provider = CssProvider.New();
        provider.LoadFromData(css, -1);
        Gtk.StyleContext.AddProviderForDisplay(
            Gdk.Display.GetDefault()!,
            provider,
            800 // GTK_STYLE_PROVIDER_PRIORITY_USER
        );
    }

    public void AddTab(string url, bool makeActive = true)
    {
        var tab = new BrowserTab();

        // 1. Create WebKit WebView
        var webView = WebView.New();
        webView.Vexpand = true;
        webView.Hexpand = true;
        tab.View = webView;

        // 2. Setup AdBlocker & Injected Scripts
        var blocker = new LinuxAdBlocker(_app, webView, OnInternalCommand);
        tab.Blocker = blocker;
        blocker.Attach();
        blocker.Changed += () =>
        {
            if (_activeTab == tab)
            {
                GLib.Functions.IdleAdd(0, () =>
                {
                    _shieldLabel.SetText($"🛡 {blocker.BlockedCount}");
                    return false;
                });
            }
        };

        // 3. WebKit Signals
        webView.OnLoadChanged += (sender, args) =>
        {
            var loadEvent = args.LoadEvent;
            if (loadEvent == LoadEvent.Started)
            {
                tab.Loading = true;
                tab.Url = webView.GetUri() ?? tab.Url;
                if (_activeTab == tab) _urlEntry.SetText(tab.Url);
            }
            else if (loadEvent == LoadEvent.Committed)
            {
                var uri = webView.GetUri() ?? "";
                tab.Url = uri;
                if (Uri.TryCreate(uri, UriKind.Absolute, out var u))
                {
                    blocker.DocumentHost = u.Host;
                    blocker.RefreshInjectedScript();
                }
            }
            else if (loadEvent == LoadEvent.Finished)
            {
                tab.Loading = false;
                tab.Touch();
                tab.Title = webView.GetTitle() ?? "Untitled";
                tab.TitleLabel.SetText(TruncateTitle(tab.Title));

                if (!_isIncognito && !string.IsNullOrWhiteSpace(tab.Url) && !tab.IsInternal)
                {
                    _app.History.Add(tab.Title, tab.Url);
                }
            }
        };

        // 4. Create Tab Header Item in Top Bar
        var tabBox = Box.New(Orientation.Horizontal, 4);
        tabBox.AddCssClass("r1-tab-btn");
        tab.TabHeaderBox = tabBox;

        var titleLabel = Label.New("New tab");
        titleLabel.SetEllipsize(Pango.EllipsizeMode.End);
        titleLabel.SetMaxWidthChars(20);
        tab.TitleLabel = titleLabel;

        var closeBtn = Button.NewWithLabel("✕");
        closeBtn.AddCssClass("r1-tab-close");
        closeBtn.OnClicked += (s, e) => CloseTab(tab);
        tab.CloseButton = closeBtn;

        tabBox.Append(titleLabel);
        tabBox.Append(closeBtn);

        // Click tab to switch
        var clickGesture = GestureClick.New();
        clickGesture.OnPressed += (gesture, args) => SwitchToTab(tab);
        tabBox.AddController(clickGesture);

        _tabs.Add(tab);

        // Insert before the '+' button in the tabbar
        _tabBarBox.Remove(_newTabButton);
        _tabBarBox.Append(tabBox);
        _tabBarBox.Append(_newTabButton);

        // Load target URL
        NavigateTab(tab, url);

        if (makeActive || _activeTab == null)
        {
            SwitchToTab(tab);
        }
    }

    public void SwitchToTab(BrowserTab tab)
    {
        if (_activeTab == tab) return;

        // Unhighlight old tab
        if (_activeTab != null)
        {
            _activeTab.TabHeaderBox.RemoveCssClass("active");
            if (_activeTab.View != null)
            {
                _webContainer.Remove(_activeTab.View);
            }
        }

        _activeTab = tab;
        tab.Touch();
        tab.TabHeaderBox.AddCssClass("active");

        if (tab.View != null)
        {
            _webContainer.Append(tab.View);
            tab.View.GrabFocus();
        }

        _urlEntry.SetText(tab.Url);
        _shieldLabel.SetText($"🛡 {tab.Blocker?.BlockedCount ?? 0}");
    }

    public void CloseTab(BrowserTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;

        _tabs.Remove(tab);
        _tabBarBox.Remove(tab.TabHeaderBox);

        if (_activeTab == tab)
        {
            if (tab.View != null) _webContainer.Remove(tab.View);

            if (_tabs.Count > 0)
            {
                int nextIndex = Math.Clamp(index, 0, _tabs.Count - 1);
                SwitchToTab(_tabs[nextIndex]);
            }
            else
            {
                _activeTab = null;
                Close(); // Close window if last tab closed
            }
        }

        tab.Dispose();
        NativeLinux.TrimWorkingSet();
    }

    private void NavigateToInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || _activeTab == null) return;
        input = input.Trim();

        string targetUrl;
        if (input.StartsWith("r1://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            targetUrl = input;
        }
        else if (input.Contains('.') && !input.Contains(' '))
        {
            targetUrl = "https://" + input;
        }
        else
        {
            // Search query fallback
            targetUrl = string.Format(_app.Settings.SearchTemplate, Uri.EscapeDataString(input));
        }

        NavigateTab(_activeTab, targetUrl);
    }

    private void NavigateTab(BrowserTab tab, string url)
    {
        tab.Url = url;
        tab.Touch();

        if (url.Equals("r1://newtab", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(_app.AssetsDir, "newtab.html");
            if (File.Exists(path))
            {
                var html = File.ReadAllText(path);
                tab.View?.LoadHtml(html, "file://" + Path.GetFullPath(_app.AssetsDir) + "/");
                return;
            }
        }
        else if (url.Equals("r1://settings", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(_app.AssetsDir, "settings.html");
            if (File.Exists(path))
            {
                var html = File.ReadAllText(path);
                tab.View?.LoadHtml(html, "file://" + Path.GetFullPath(_app.AssetsDir) + "/");
                return;
            }
        }

        tab.View?.LoadUri(url);
    }

    private void SetupKeyShortcuts()
    {
        var keyController = EventControllerKey.New();
        keyController.OnKeyPressed += (controller, args) =>
        {
            var keyval = args.Keyval;
            var state = args.State;
            bool ctrl = (state & Gdk.ModifierType.ControlMask) != 0;
            bool shift = (state & Gdk.ModifierType.ShiftMask) != 0;

            if (ctrl && !shift && (keyval == Gdk.Constants.KEY_t || keyval == Gdk.Constants.KEY_T))
            {
                AddTab("r1://newtab", true);
                return true;
            }
            if (ctrl && !shift && (keyval == Gdk.Constants.KEY_w || keyval == Gdk.Constants.KEY_W))
            {
                if (_activeTab != null) CloseTab(_activeTab);
                return true;
            }
            if (ctrl && !shift && (keyval == Gdk.Constants.KEY_l || keyval == Gdk.Constants.KEY_L))
            {
                _urlEntry.GrabFocus();
                _urlEntry.SelectRegion(0, -1);
                return true;
            }
            if (ctrl && !shift && (keyval == Gdk.Constants.KEY_r || keyval == Gdk.Constants.KEY_R))
            {
                _activeTab?.View?.Reload();
                return true;
            }
            if (keyval == Gdk.Constants.KEY_F5)
            {
                _activeTab?.View?.Reload();
                return true;
            }
            if (ctrl && shift && (keyval == Gdk.Constants.KEY_n || keyval == Gdk.Constants.KEY_N))
            {
                var incogWin = new MainWindow(_appInstance, _app, incognito: true, startUrl: "r1://newtab");
                incogWin.Present();
                return true;
            }

            return false;
        };
        _window.AddController(keyController);
    }

    private void SetupIdleTimer()
    {
        _idleTimer = () =>
        {
            if (!_app.Settings.SleepBackgroundTabs) return true;

            var now = DateTime.Now;
            bool trimmed = false;

            foreach (var tab in _tabs)
            {
                if (tab == _activeTab) continue;

                // Suspend inactive background tabs after configured duration
                if (!tab.Suspended && (now - tab.LastActive).TotalSeconds > _app.Settings.SleepAfterSeconds)
                {
                    tab.Suspended = true;
                    trimmed = true;
                }
            }

            if (trimmed)
            {
                NativeLinux.TrimWorkingSet();
            }

            return true;
        };

        GLib.Functions.TimeoutAdd(0, 15000, _idleTimer);
    }

    private void OnInternalCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("r1", out var act)) return;

            var action = act.GetString();
            if (action == "search" && root.TryGetProperty("q", out var q))
            {
                NavigateToInput(q.GetString() ?? "");
            }
            else if (action == "openUrl" && root.TryGetProperty("url", out var u))
            {
                NavigateToInput(u.GetString() ?? "");
            }
            else if (action == "openSettings")
            {
                NavigateToInput("r1://settings");
            }
        }
        catch { }
    }

    private void ShowShieldDetails()
    {
        var count = _activeTab?.Blocker?.BlockedCount ?? 0;
        var bytes = (_activeTab?.Blocker?.BlockedBytes ?? 0) / 1024;
        var mem = NativeLinux.GetRssMemoryMb();

        var dialog = Gtk.Window.New();
        dialog.SetTitle("R Browser Protection Shields");
        dialog.SetTransientFor(_window);
        dialog.SetModal(true);
        dialog.SetDefaultSize(320, 180);

        var box = Box.New(Orientation.Vertical, 12);
        box.SetMarginTop(20);
        box.SetMarginBottom(20);
        box.SetMarginStart(20);
        box.SetMarginEnd(20);

        var text = Label.New($"Blocked ads & trackers: {count}\nSaved bandwidth: ~{bytes} KB\nProcess RAM (RSS): {mem} MB");
        var okBtn = Button.NewWithLabel("OK");
        okBtn.OnClicked += (s, e) => dialog.Close();

        box.Append(text);
        box.Append(okBtn);
        dialog.SetChild(box);
        dialog.Present();
    }

    private static string TruncateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "New tab";
        return title.Length > 20 ? title.Substring(0, 18) + "…" : title;
    }
}
