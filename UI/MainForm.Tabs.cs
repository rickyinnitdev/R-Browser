using System.Drawing;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using R1.Core;

namespace R1.UI;

public sealed partial class MainForm
{
    private async Task InitAsync()
    {
        try
        {
            await EnsureEnvironmentAsync();

            var restore = new List<string>();
            if (!_incognito && _app.Settings.RestoreSession)
            {
                foreach (var u in _app.Settings.SessionUrls)
                    if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !u.Contains("r1.internal"))
                        restore.Add(u);
            }

            var first = string.IsNullOrWhiteSpace(_startUrl)
                ? (restore.Count > 0 ? restore[0] : "r1://newtab")
                : NormalizeInput(_startUrl);
            if (!string.IsNullOrWhiteSpace(_startUrl)) restore.Clear();

            await NewTabAsync(first, true);
            for (int i = 1; i < Math.Min(restore.Count, 12); i++)
                await NewTabAsync(restore[i], false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "R Browser could not start its web engine.\r\n\r\n" + ex.Message +
                "\r\n\r\nMake sure the Microsoft Edge WebView2 Runtime is installed.",
                "R Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private Task<CoreWebView2Environment> EnsureEnvironmentAsync()
    {
        if (_env != null) return Task.FromResult(_env);
        _envTask ??= CreateEnvironmentAsync();
        return _envTask;
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        Directory.CreateDirectory(_profileDir);
        int limit = Math.Clamp(_app.Settings.RendererLimit, 1, 12);

        // Software rendering is the default: it keeps the GPU process small (~100 MB
        // less private memory). Users who prefer smoother scrolling can switch it on.
        bool gpu = _app.Settings.HardwareAcceleration;
        var gpuOverride = Environment.GetEnvironmentVariable("R1_GPU");
        if (gpuOverride == "1") gpu = true;
        else if (gpuOverride == "0") gpu = false;

        var args =
            $"--renderer-process-limit={limit} " +
            "--disk-cache-size=33554432 " +
            "--media-cache-size=16777216 " +
            "--disable-speech-api --disable-speech-synthesis-api --disable-print-preview " +
            "--disable-background-networking --disable-component-update --disable-domain-reliability " +
            "--disable-sync --no-first-run --no-default-browser-check --autoplay-policy=user-gesture-required " +
            "--disable-hyperlink-auditing --dns-prefetch-disable --enforce-webrtc-ip-permission-check " +
            "--force-webrtc-ip-handling-policy=disable_non_proxied_udp --no-pings --disable-breakpad " +
            "--disable-features=Translate,MediaRouter,OptimizationHints,InterestFeedContentSuggestions,PrivacySandboxSettings4,BrowsingTopics,InterestGroupStorage,Fledge";
        if (!gpu) args += " --disable-gpu --disable-gpu-compositing";
        if (_app.Settings.SecureDns)
        {
            args += " --enable-features=DnsOverHttps,EncryptedClientHello,ReducedReferrerGranularity,HttpsUpgrades " +
                    "--dns-over-https-mode=secure " +
                    "--dns-over-https-templates=\"https://cloudflare-dns.com/dns-query{?dns} https://dns.quad9.net/dns-query{?dns}\"";
        }
        var options = new CoreWebView2EnvironmentOptions(args, "en-US");
        var dbg = Environment.GetEnvironmentVariable("R1_DEBUG_PORT");
        if (!string.IsNullOrWhiteSpace(dbg))
        {
            // Test hook: expose CDP for automated checks (never set in normal use).
            options.AdditionalBrowserArguments += $" --remote-debugging-port={dbg}";
        }
        var env = await CoreWebView2Environment.CreateAsync(null, _profileDir, options);
        _env = env;
        return env;
    }

    private async Task<BrowserTab> NewTabAsync(string? url, bool activate)
    {
        var tab = await CreateTabAsync(url, activate);
        _tabs.Add(tab);
        RecalcLayout();
        _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
        if (activate) ActivateTab(_tabs.Count - 1);
        else { tab.View.Visible = false; Invalidate(); }
        return tab;
    }

    private async Task<BrowserTab> CreateTabAsync(string? url, bool initialize)
    {
        var tab = new BrowserTab();
        var view = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(8, 9, 12),
            Visible = false
        };
        tab.View = view;
        _webHost.Controls.Add(view);

        tab.Url = url ?? "r1://newtab";
        tab.Title = tab.IsInternal ? "New tab" : HostOf(tab.Url);
        if (initialize) await EnsureTabInitializedAsync(tab);
        return tab;
    }

    private Task EnsureTabInitializedAsync(BrowserTab tab) =>
        tab.Initialization ??= InitializeTabAsync(tab);

    private async Task InitializeTabAsync(BrowserTab tab)
    {
        var env = await EnsureEnvironmentAsync();
        if (tab.Disposed) return;
        await tab.View.EnsureCoreWebView2Async(env);
        if (tab.Disposed) return;
        ConfigureCore(tab);
        tab.Blocker = new AdBlocker(_app, tab.View.CoreWebView2);
        tab.Blocker.DocumentHost = HostOf(tab.Url);
        tab.Blocker.Changed += () =>
        {
            if (tab == Active && !IsDisposed)
            {
                try { BeginInvoke(() => Invalidate(Rectangle.Ceiling(_rShield))); } catch { }
            }
        };
        await tab.Blocker.AttachAsync();
        if (!tab.Disposed) NavigateTab(tab, tab.Url);
    }

    private void ConfigureCore(BrowserTab tab)
    {
        var core = tab.View.CoreWebView2;
        try { core.SetVirtualHostNameToFolderMapping("r1.internal", _app.AssetsDir, CoreWebView2HostResourceAccessKind.Allow); }
        catch { }

        var s = core.Settings;
        s.AreDevToolsEnabled = true;
        s.AreDefaultContextMenusEnabled = true;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = true;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsBuiltInErrorPageEnabled = true;
        s.IsReputationCheckingRequired = false;
        try { core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark; } catch { }

        core.NavigationStarting += (_, e) =>
        {
            tab.Loading = true;
            tab.Crashed = false;
            tab.Url = e.Uri;
            if (tab.Blocker != null) tab.Blocker.DocumentHost = HostOf(e.Uri);
            _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
            if (tab == Active) Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ChromeH * Sc)));
        };

        core.SourceChanged += (_, _) =>
        {
            tab.Url = core.Source;
            if (tab.Blocker != null) tab.Blocker.DocumentHost = HostOf(core.Source);
            _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
            if (tab == Active) Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ChromeH * Sc)));
        };

        core.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            tab.IsPlayingAudio = core.IsDocumentPlayingAudio;
            RecalcLayout();
            _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
            Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ChromeH * Sc)));
        };

        core.IsMutedChanged += (_, _) =>
        {
            tab.IsMuted = core.IsMuted;
            RecalcLayout();
            _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
            Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ChromeH * Sc)));
        };

        core.NavigationCompleted += async (_, e) =>
        {
            tab.Loading = false;
            tab.Url = core.Source;
            if (!string.IsNullOrWhiteSpace(core.DocumentTitle)) tab.Title = core.DocumentTitle;
            if (!_incognito && !tab.IsInternal && tab.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                _app.History.Add(tab.Title, tab.Url);
            LoadIcon(tab);
            _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
            if (tab == Active)
            {
                Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ChromeH * Sc)));
                RefreshSidePanel();
            }
            if (tab.Blocker != null) await tab.Blocker.RefreshCssAsync();
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? HostOf(tab.Url) : core.DocumentTitle;
            _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
            if (tab == Active) Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ChromeH * Sc)));
        };

        core.FaviconChanged += (_, _) => LoadIcon(tab);

        core.HistoryChanged += (_, _) =>
        {
            if (tab == Active) Invalidate(new Rectangle(0, 0, (int)(140 * Sc), (int)(ChromeH * Sc)));
        };

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (tab.Blocker?.ShouldBlockPopup(e.Uri, e.IsUserInitiated) == true) return;
            _ = NewTabAsync(e.Uri, true);
        };

        core.PermissionRequested += (_, e) =>
        {
            e.State = e.PermissionKind is CoreWebView2PermissionKind.ClipboardRead
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };

        core.DownloadStarting += (_, e) => OnDownloadStarting(e);
        tab.View.ZoomFactorChanged += (_, _) =>
        {
            tab.Zoom = tab.View.ZoomFactor;
            if (tab == Active) Invalidate();
        };

        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            SetFullscreen(core.ContainsFullScreenElement);
        };

        core.ProcessFailed += (_, e) =>
        {
            Log.Write($"ProcessFailed {e.ProcessFailedKind} reason={e.Reason} exit={e.ExitCode} url={tab.Url}");
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                tab.Crashed = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited;
                ShowToast(tab.Crashed ? "Tab crashed - press F5 to reload" : "Page is not responding");
            }
        };

                core.WebMessageReceived += (_, e) => OnWebMessage(tab, e);
    }

    private static readonly HttpClient IconHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private const int IconCacheLimit = 64;

    private async void LoadIcon(BrowserTab tab)
    {
        var core = tab.View.CoreWebView2;
        var uri = core?.FaviconUri;
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "https" && parsed.Scheme != "http")) return;
        if (_icons.TryGetValue(uri, out var cached)) { tab.Icon = cached; Invalidate(); return; }
        if (!_iconLoading.Add(uri)) return;
        Bitmap? bmp = null;
        try
        {
            using var response = await IconHttp.GetAsync(parsed, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 128_000) return;
            await using var input = await response.Content.ReadAsStreamAsync();
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer)) > 0)
            {
                if (bytes.Length + count > 128_000) return;
                bytes.Write(buffer, 0, count);
            }
            bytes.Position = 0;
            using var src = Image.FromStream(bytes);
            if (src.Width > 1024 || src.Height > 1024) return;
            bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, 16, 16);
            if (IsDisposed || tab.Disposed || core?.FaviconUri != uri) return;
            if (_icons.Count >= IconCacheLimit)
            {
                var oldest = _icons.First();
                _icons.Remove(oldest.Key);
                foreach (var t in _tabs) if (ReferenceEquals(t.Icon, oldest.Value)) t.Icon = null;
                oldest.Value?.Dispose();
            }
            _icons[uri] = bmp;
            tab.Icon = bmp;
            bmp = null; // cache owns the bitmap
            _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
            Invalidate();
        }
        catch { _icons[uri] = null; }
        finally { bmp?.Dispose(); _iconLoading.Remove(uri); }
    }

    private void Navigate(string input)
    {
        if (Active == null) { _ = NewTabAsync(input, true); return; }
        NavigateTab(Active, input);
    }

    private void NavigateTab(BrowserTab tab, string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        var target = RealUrl(input);
        try
        {
            tab.Url = input;
            if (tab.View?.CoreWebView2 != null) tab.View.CoreWebView2.Navigate(target);
        }
        catch { }
    }

    private readonly List<(string Url, string Title)> _recentlyClosed = new();

    private void ReopenClosedTab()
    {
        while (_recentlyClosed.Count > 0)
        {
            var (url, _) = _recentlyClosed[0];
            _recentlyClosed.RemoveAt(0);
            if (!string.IsNullOrWhiteSpace(url))
            {
                _ = NewTabAsync(url, true);
                ShowToast("Reopened tab");
                return;
            }
        }
        ShowToast("No recently closed tabs");
    }

    private async void ActivateTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        var tab = _tabs[index];
        if (_active == index && tab.View != null && tab.Initialization?.IsCompletedSuccessfully == true)
        {
            FocusWeb();
            return;
        }
        if (_active >= 0 && _active < _tabs.Count)
        {
            var old = _tabs[_active];
            if (old.View != null) old.View.Visible = false;
            old.LastActive = DateTime.Now;
        }
        _active = index;

        if (tab.Hibernated || tab.View == null)
        {
            tab.Hibernated = false;
            var view = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(8, 9, 12),
                Visible = false
            };
            tab.View = view;
            _webHost.Controls.Add(view);
            tab.Initialization = null;
        }

        try { await EnsureTabInitializedAsync(tab); }
        catch (Exception ex) { tab.Initialization = null; ShowToast("Could not load tab: " + ex.Message); return; }
        if (tab.Disposed || Active != tab) return;
        tab.View.Visible = true;
        tab.View.BringToFront();
        UpdateActiveMemoryTarget();
        tab.LastActive = DateTime.Now;
        if (tab.Suspended && tab.View.CoreWebView2 != null)
        {
            try { tab.View.CoreWebView2.Resume(); } catch { }
            tab.Suspended = false;
        }
        RecalcLayout();
        LayoutContent();
        RefreshSidePanel();
        _tabSidebar?.UpdateTabs(_tabs, _active, _spin);
        EnsureTabVisible(_active);
        Invalidate();
        FocusWeb();
    }

    public void ToggleVerticalTabs(bool on)
    {
        _app.Settings.VerticalTabs = on;
        _app.SaveSettings();
        if (_tabSidebar != null)
        {
            _tabSidebar.Visible = on;
            _tabSidebar.Collapsed = _app.Settings.VerticalTabsCollapsed;
            _tabSidebar.Width = (int)((_app.Settings.VerticalTabsCollapsed ? 48 : 210) * Sc);
            _tabSidebar.UpdateTabs(_tabs, _active, _spin);
        }
        RecalcLayout();
        LayoutContent();
        Invalidate();
        ShowToast(on ? "Sidebar tabs enabled" : "Top tabs enabled");
    }

    private void EnsureTabVisible(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        if (!_app.Settings.VerticalTabs && index < _tabRects.Count)
        {
            float startX = 136;
            float avail = Math.Max(120, (ClientSize.Width / Sc - 106) - startX);
            float tabLeft = index * (_tabW + _tabGap);
            float tabRight = tabLeft + _tabW;
            if (tabLeft < _tabScroll) _tabScroll = tabLeft;
            else if (tabRight > _tabScroll + avail) _tabScroll = tabRight - avail;
            RecalcLayout();
        }
        else if (_app.Settings.VerticalTabs && _tabSidebar != null)
        {
            _tabSidebar.EnsureVisible(index);
        }
    }

    private void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        var tab = _tabs[index];
        if (!tab.IsInternal && !string.IsNullOrEmpty(tab.Url) && !tab.Url.Contains("r1.internal"))
        {
            _recentlyClosed.Insert(0, (tab.Url, tab.Title));
            if (_recentlyClosed.Count > 30)
                _recentlyClosed.RemoveAt(_recentlyClosed.Count - 1);
        }

        _tabs.RemoveAt(index);
        try { if (tab.View != null) _webHost.Controls.Remove(tab.View); } catch { }
        try { tab.Dispose(); } catch { }
        Native.TrimWorkingSet();

        if (_tabs.Count == 0)
        {
            _active = -1;
            Close();
            return;
        }

        _tabSidebar?.UpdateTabs(_tabs, _active, _spin);

        int next = _active > index ? _active - 1 : _active == index ? Math.Min(index, _tabs.Count - 1) : _active;
        next = Math.Clamp(next, 0, _tabs.Count - 1);

        foreach (var t in _tabs) if (t.View != null) t.View.Visible = false;
        _active = -1;
        ActivateTab(next);
    }

    private void CycleTab(int delta)
    {
        if (_tabs.Count < 2) return;
        int next = (_active + delta + _tabs.Count) % _tabs.Count;
        ActivateTab(next);
    }

    private void ReloadTab(bool hard)
    {
        var tab = Active;
        if (tab?.View?.CoreWebView2 == null) return;
        if (hard) tab.View.CoreWebView2.Reload();
        else tab.View.CoreWebView2.Navigate(tab.View.CoreWebView2.Source);
    }

    private void SetZoom(double z)
    {
        var tab = Active;
        if (tab?.View?.CoreWebView2 == null) return;
        tab.View.ZoomFactor = Math.Clamp(z, 0.25, 5.0);
    }

    private void ToggleBookmark()
    {
        var tab = Active;
        if (tab == null || string.IsNullOrEmpty(tab.Url) || tab.IsInternal) return;
        var real = RealUrl(tab.Url);
        _app.Bookmarks.Toggle(string.IsNullOrWhiteSpace(tab.Title) ? HostOf(tab.Url) : tab.Title, real);
        ShowToast(_app.Bookmarks.Contains(real) ? "Bookmark added" : "Bookmark removed");
        Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ChromeH * Sc)));
    }

    private void ToggleDevTools()
    {
        var tab = Active;
        if (tab?.View?.CoreWebView2 == null) return;
        if (tab.View.CoreWebView2.Settings.AreDevToolsEnabled) tab.View.CoreWebView2.OpenDevToolsWindow();
    }

    private FormWindowState _preFullscreenState = FormWindowState.Normal;

    private void SetFullscreen(bool on)
    {
        if (_chromeHidden == on) return;
        _chromeHidden = on;
        if (on)
        {
            _preFullscreenState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            WindowState = _preFullscreenState;
        }
        LayoutContent();
        Invalidate();
    }

    private void UpdateActiveMemoryTarget()
    {
        foreach (var tab in _tabs)
        {
            var core = tab.View?.CoreWebView2;
            if (core == null) continue;
            try
            {
                bool isActive = tab == Active;
                core.MemoryUsageTargetLevel = (!isActive || WindowState == FormWindowState.Minimized)
                    ? CoreWebView2MemoryUsageTargetLevel.Low
                    : CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch (NotImplementedException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    private bool _sleepScanRunning;

    private async Task SleepIdleTabsAsync()
    {
        if (_sleepScanRunning || !_app.Settings.SleepBackgroundTabs) return;
        _sleepScanRunning = true;
        try
        {
            // Cap legacy settings so existing profiles also get the new 45-second budget.
            var cutoff = DateTime.Now.AddSeconds(-Math.Clamp(_app.Settings.SleepAfterSeconds, 20, 45));
            var hibernateCutoff = DateTime.Now.AddMinutes(-15);
            foreach (var tab in _tabs.ToArray())
            {
                if (tab.Disposed || tab == Active || tab.Loading || tab.Hibernated) continue;
                var core = tab.View?.CoreWebView2;
                if (core != null && (core.IsDocumentPlayingAudio || core.ContainsFullScreenElement)) continue;

                if (tab.LastActive < hibernateCutoff && !tab.IsInternal && !string.IsNullOrEmpty(tab.Url))
                {
                    try
                    {
                        if (tab.View != null) _webHost.Controls.Remove(tab.View);
                        tab.View?.Dispose();
                        tab.View = null!;
                        tab.Initialization = null;
                        tab.Blocker = null;
                        tab.Suspended = false;
                        tab.Hibernated = true;
                        Native.TrimWorkingSet();
                    }
                    catch { }
                    continue;
                }

                if (tab.Suspended || tab.LastActive > cutoff || core == null) continue;
                try
                {
                    // WebView2 itself declines suspension for other protected workloads.
                    bool suspended = await core.TrySuspendAsync();
                    if (tab.Disposed) continue;
                    if (tab == Active || tab.View?.Visible == true)
                    {
                        if (suspended) core.Resume();
                        tab.Suspended = false;
                    }
                    else tab.Suspended = suspended;
                }
                catch { }
            }
        }
        finally { _sleepScanRunning = false; }
    }

    private async void FindInPage(string query, bool forward, bool first)
    {
        var tab = Active;
        var core = tab?.View?.CoreWebView2;
        if (core == null) return;
        if (string.IsNullOrEmpty(query))
        {
            _findBar.SetStatus("");
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(query);
            var res = await core.ExecuteScriptAsync(
                $"window.find({json}, false, {(forward ? "false" : "true")}, true, false, false, false)");
            _findBar.SetStatus(res == "true" ? "" : "No matches");
        }
        catch { }
    }

    private async void OnDownloadStarting(CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var dl = e.DownloadOperation;
            var name = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrWhiteSpace(name)) name = "download";
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(dir);
            var path = UniquePath(dir, name);
            e.ResultFilePath = path;

            var uri = dl.Uri;
            bool isHttp = !string.IsNullOrEmpty(uri) &&
                          (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

            if (_app.Settings.DownloadAcceleration && isHttp)
            {
                e.Cancel = true;

                var entry = new DownloadEntry
                {
                    Name = name,
                    Path = path,
                    Total = (long)(dl.TotalBytesToReceive ?? 0),
                    State = "Starting",
                    Cts = new CancellationTokenSource()
                };
                _downloads.Insert(0, entry);
                ShowToast($"Accelerating download: {name}");
                if (_side.Visible) RefreshSidePanel();

                string? cookieHeader = null;
                var tab = Active;
                if (tab?.View?.CoreWebView2 != null)
                {
                    try
                    {
                        var cookies = await tab.View.CoreWebView2.CookieManager.GetCookiesAsync(uri);
                        if (cookies.Count > 0)
                            cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                    }
                    catch { }
                }

                _ = Task.Run(async () =>
                {
                    var progress = new Progress<SegmentedDownloader.DownloadProgress>(p =>
                    {
                        entry.Total = p.TotalBytes;
                        entry.Received = p.ReceivedBytes;
                        entry.Speed = p.BytesPerSecond;
                        entry.State = p.State;
                        if (p.IsCompleted)
                        {
                            ShowToast($"Saved {name}");
                        }
                        if (_side.Visible) RefreshSidePanel();
                    });

                    try
                    {
                        await SegmentedDownloader.DownloadAsync(
                            uri,
                            path,
                            _app.Settings.DownloadConnections,
                            cookieHeader,
                            tab?.Url,
                            progress,
                            entry.Cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        entry.State = "Canceled";
                        if (_side.Visible) RefreshSidePanel();
                    }
                    catch (Exception ex)
                    {
                        entry.State = "Failed";
                        ShowToast($"Download failed: {name}");
                        Log.Write($"Download error: {ex.Message}");
                        if (_side.Visible) RefreshSidePanel();
                    }
                });

                return;
            }

            var stdEntry = new DownloadEntry
            {
                Name = name,
                Path = path,
                Total = (long)(dl.TotalBytesToReceive ?? 0),
                State = "Starting"
            };
            _downloads.Insert(0, stdEntry);

            dl.BytesReceivedChanged += (_, _) =>
            {
                stdEntry.Received = dl.BytesReceived;
                if (stdEntry.Total <= 0) stdEntry.Total = (long)(dl.TotalBytesToReceive ?? 0);
                if (_side.Visible) RefreshSidePanel();
            };
            dl.StateChanged += (_, _) =>
            {
                stdEntry.State = dl.State switch
                {
                    CoreWebView2DownloadState.InProgress => "Downloading",
                    CoreWebView2DownloadState.Interrupted => "Failed",
                    _ => "Completed"
                };
                stdEntry.Received = dl.BytesReceived;
                if (dl.State == CoreWebView2DownloadState.Completed)
                    ShowToast("Saved " + name);
                if (_side.Visible) RefreshSidePanel();
            };

            ShowToast("Downloading " + name);
            if (_side.Visible) RefreshSidePanel();
        }
        catch { }
    }

    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; i < 1000; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
        return path;
    }

    private void OnWebMessage(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // Only bundled pages may invoke browser actions or change settings.
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
                source.Scheme != "https" || source.Host != "r1.internal") return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("r1", out var kindProp)) return;
            var kind = kindProp.GetString();

            switch (kind)
            {
                case "search":
                {
                    var q = root.TryGetProperty("q", out var qp) ? (qp.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(q)) return;
                    Navigate(NormalizeInput(q));
                    break;
                }
                case "open":
                {
                    var url = root.TryGetProperty("url", out var up) ? (up.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(url)) return;
                    _ = NewTabAsync(url, true);
                    break;
                }
                case "goto":
                {
                    var url = root.TryGetProperty("url", out var up) ? (up.GetString() ?? "") : "";
                    Navigate(url);
                    break;
                }
                case "home":
                {
                    var payload = BuildHomePayload();
                    try { tab.View?.CoreWebView2?.PostWebMessageAsJson(payload); } catch { }
                    break;
                }
                case "get":
                {
                    var payload = BuildSettingsPayload();
                    try { tab.View?.CoreWebView2?.PostWebMessageAsJson(payload); } catch { }
                    break;
                }
                case "set":
                {
                    var key = root.TryGetProperty("key", out var kp) ? (kp.GetString() ?? "") : "";
                    if (root.TryGetProperty("value", out var vp))
                    {
                        bool b = vp.ValueKind == JsonValueKind.True;
                        if (vp.ValueKind == JsonValueKind.String) ApplySetting(key, vp.GetString() ?? "", b: false);
                        else ApplySetting(key, "", b);
                    }
                    break;
                }
                case "action":
                {
                    var action = root.TryGetProperty("name", out var ap) ? (ap.GetString() ?? "") : "";
                    RunAction(action);
                    break;
                }
            }
        }
        catch { }
    }

    private string BuildHomePayload()
    {
        var sites = new List<object>();
        foreach (var b in _app.Bookmarks.Items.Take(8))
            sites.Add(new { t = b.Title, u = b.Url });
        if (sites.Count < 8)
        {
            foreach (var h in _app.History.Items.Take(16))
            {
                if (sites.Count >= 8) break;
                if (sites.Any(x => x.GetType().GetProperty("u")!.GetValue(x) as string == h.Url)) continue;
                sites.Add(new { t = h.Title, u = h.Url });
            }
        }
        int blocked = _tabs.Sum(t => t.Blocker?.BlockedCount ?? 0);
        return JsonSerializer.Serialize(new
        {
            r1 = "home",
            sites,
            blocked,
            rules = _app.Engine.NetworkRules,
            cosmetic = _app.Engine.CosmeticRules,
            incognito = _incognito
        });
    }

    private string BuildSettingsPayload()
    {
        var s = _app.Settings;
        return JsonSerializer.Serialize(new
        {
            r1 = "settings",
            adblock = s.AdblockEnabled,
            cosmetic = s.CosmeticEnabled,
            antiadblock = s.AntiAdblockEnabled,
            cookies = s.RemoveCookieBanners,
            sleep = s.SleepBackgroundTabs,
            restore = s.RestoreSession,
            gpu = s.HardwareAcceleration,
            securedns = s.SecureDns,
            downloadaccel = s.DownloadAcceleration,
            verticaltabs = s.VerticalTabs,
            sidepanelleft = s.SidePanelLeft,
            search = s.SearchTemplate,
            rules = _app.Engine.NetworkRules,
            cosmeticRules = _app.Engine.CosmeticRules,
            engineReady = _app.EngineReady,
            updated = s.FiltersUpdated,
            whitelist = s.Whitelist
        });
    }

    private void ApplySetting(string key, string text, bool b)
    {
        var s = _app.Settings;
        switch (key)
        {
            case "adblock": s.AdblockEnabled = b; break;
            case "cosmetic": s.CosmeticEnabled = b; break;
            case "antiadblock": s.AntiAdblockEnabled = b; break;
            case "cookies": s.RemoveCookieBanners = b; break;
            case "sleep": s.SleepBackgroundTabs = b; break;
            case "restore": s.RestoreSession = b; break;
            case "gpu": s.HardwareAcceleration = b; break;
            case "securedns": s.SecureDns = b; break;
            case "downloadaccel": s.DownloadAcceleration = b; break;
            case "verticaltabs": s.VerticalTabs = b; ToggleVerticalTabs(b); break;
            case "sidepanelleft": s.SidePanelLeft = b; LayoutContent(); Invalidate(); break;
            case "search": if (text.Contains("{0}")) s.SearchTemplate = text; break;
            default: return;
        }
        _app.SaveSettings();
        if (key is "gpu" or "securedns") ShowToast("Restart R Browser to apply the changes");
        foreach (var t in _tabs)
            if (t.Blocker != null) _ = t.Blocker.RefreshCssAsync();
        Invalidate(new Rectangle(0, 0, ClientSize.Width, (int)(ChromeH * Sc)));
    }

    private void RunAction(string action)
    {
        switch (action)
        {
            case "clearHistory":
                _app.History.Clear();
                ShowToast("Browsing history cleared");
                break;
            case "clearCookies":
                _ = ClearCookiesAsync();
                break;
            case "updateFilters":
                _ = UpdateFiltersAsync();
                break;
            case "openDownloads":
                OpenPanel(SidePanelMode.Downloads);
                break;
            case "openBookmarks":
                OpenPanel(SidePanelMode.Bookmarks);
                break;
            case "openHistory":
                OpenPanel(SidePanelMode.History);
                break;
            case "openFolder":
                try { System.Diagnostics.Process.Start("explorer.exe", _app.DataDir); } catch { }
                break;
        }
    }

    private async Task ClearCookiesAsync()
    {
        try
        {
            foreach (var t in _tabs)
            {
                var cm = t.View?.CoreWebView2?.CookieManager;
                if (cm == null) continue;
                var cookies = await cm.GetCookiesAsync(null);
                foreach (var c in cookies)
                    cm.DeleteCookie(c);
            }
            ShowToast("Cookies cleared for this session");
        }
        catch { }
    }

    private async Task UpdateFiltersAsync()
    {
        ShowToast("Updating filter lists...");
        var ok = await _app.UpdateFiltersAsync();
        ShowToast(ok ? $"Filters updated - {_app.Engine.NetworkRules:N0} rules" : "Filter update failed");
        foreach (var t in _tabs)
            if (t.Blocker != null) _ = t.Blocker.RefreshCssAsync();
        if (_side.Visible) RefreshSidePanel();
    }

    private bool HandleShortcut(Keys key, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && !alt)
        {
            switch (key)
            {
                case Keys.T:
                    if (shift) ReopenClosedTab();
                    else _ = NewTabAsync(null, true);
                    return true;
                case Keys.W: CloseTab(_active); return true;
                case Keys.L:
                case Keys.K:
                case Keys.E: BeginEdit(); return true;
                case Keys.F: ShowFindBar(); return true;
                case Keys.R: ReloadTab(shift); return true;
                case Keys.D: ToggleBookmark(); return true;
                case Keys.B: OpenPanel(SidePanelMode.Bookmarks); return true;
                case Keys.V: if (shift) { ToggleVerticalTabs(!_app.Settings.VerticalTabs); return true; } break;
                case Keys.H: OpenPanel(SidePanelMode.History); return true;
                case Keys.J: OpenPanel(SidePanelMode.Downloads); return true;
                case Keys.M:
                    if (Active?.View?.CoreWebView2 != null)
                        Active.View.CoreWebView2.IsMuted = !Active.View.CoreWebView2.IsMuted;
                    return true;
                case Keys.Tab: CycleTab(shift ? -1 : 1); return true;
                case Keys.D0:
                case Keys.NumPad0: SetZoom(1.0); return true;
                case Keys.Oemplus:
                case Keys.Add: SetZoom((Active?.Zoom ?? 1) + 0.1); return true;
                case Keys.OemMinus:
                case Keys.Subtract: SetZoom((Active?.Zoom ?? 1) - 0.1); return true;
            }
            if (!shift && key >= Keys.D1 && key <= Keys.D8)
            {
                int target = key - Keys.D1;
                if (target < _tabs.Count) ActivateTab(target);
                return true;
            }
            if (!shift && key == Keys.D9 && _tabs.Count > 0)
            {
                ActivateTab(_tabs.Count - 1);
                return true;
            }
            if (shift && key == Keys.N) { BrowserAppContext.RegisterWindow(new MainForm(_app, true, null)); return true; }
            if (shift && key == Keys.Delete) { RunAction("clearHistory"); return true; }
        }
        if (alt && !ctrl)
        {
            if (key == Keys.Left) { if (Active?.View.CanGoBack == true) Active.View.GoBack(); return true; }
            if (key == Keys.Right) { if (Active?.View.CanGoForward == true) Active.View.GoForward(); return true; }
        }
        if (key == Keys.F5) { ReloadTab(true); return true; }
        if (key == Keys.F12) { ToggleDevTools(); return true; }
        if (key == Keys.F11) { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; return true; }
        if (key == Keys.Escape)
        {
            if (_findBar.Visible) { _findBar.Visible = false; LayoutContent(); Invalidate(); return true; }
            if (_chromeHidden) { SetFullscreen(false); return true; }
        }
        return false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0;
        bool shift = (keyData & Keys.Shift) != 0;
        bool alt = (keyData & Keys.Alt) != 0;
        if (HandleShortcut(key, ctrl, shift, alt)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            if (!_incognito)
            {
                _app.Settings.SessionUrls = _tabs
                    .Select(t => t.Url)
                    .Where(u => !string.IsNullOrEmpty(u) && u.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !u.Contains("r1.internal"))
                    .Take(20)
                    .ToList();
                _app.SaveSettings();
                _app.Bookmarks.Save();
                _app.History.Save();
            }
        }
        catch { }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(_shortcutFilter);
        _tick.Stop();
        _sleepTick.Stop();
        foreach (var tab in _tabs) tab.Dispose();
        _tabs.Clear();
        foreach (var image in _icons.Values) image?.Dispose();
        _icons.Clear();
        _tick.Dispose();
        _sleepTick.Dispose();
        _addressBox.Dispose();
        _tabSidebar?.Dispose();
        _fTab.Dispose();
        _fUi.Dispose();
        _fSmall.Dispose();
        _fTiny.Dispose();
        _fUrl.Dispose();
        _fTitle.Dispose();
        _suggest?.Dispose();
        _toast?.Dispose();

        // Clean up incognito profile data so nothing persists on disk.
        if (_incognito)
        {
            try
            {
                if (Directory.Exists(_profileDir))
                    Directory.Delete(_profileDir, recursive: true);
            }
            catch { }
        }

        base.OnFormClosed(e);
    }
}
