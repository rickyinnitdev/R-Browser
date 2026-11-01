using System;
using System.Text.Json;
using WebKit;
using R1.Core;

namespace R1.Linux;

/// <summary>
/// WebKitGTK ad blocking & privacy engine adapter for Linux.
/// Connects R1.Core filter lists, cosmetic sheets, and anti-adblock protection to WebKit.
/// </summary>
internal sealed class LinuxAdBlocker : IDisposable
{
    private readonly AppState _app;
    private readonly WebView _view;
    private readonly Action<string>? _onInternalCommand;
    private readonly List<string> _recent = new();
    private UserScript? _activeUserScript;

    public string DocumentHost { get; set; } = "";
    public int BlockedCount { get; private set; }
    public long BlockedBytes { get; private set; }
    public IReadOnlyList<string> Recent => _recent;
    public event Action? Changed;

    public LinuxAdBlocker(AppState app, WebView view, Action<string>? onInternalCommand = null)
    {
        _app = app;
        _view = view;
        _onInternalCommand = onInternalCommand;
    }

    public void Attach()
    {
        var manager = _view.GetUserContentManager();

        // Register message handler for R1 internal communication & cosmetic requests
        try
        {
            manager.RegisterScriptMessageHandler("r1", null);
        }
        catch { }

        // Inject initial script (cosmetic rules + anti-adblock + window.chrome shim)
        RefreshInjectedScript();
    }

    public void RefreshInjectedScript()
    {
        try
        {
            var manager = _view.GetUserContentManager();
            if (_activeUserScript != null)
            {
                manager.RemoveAllScripts();
            }

            var baseScript = InjectedScripts.Build(_app.Engine, _app.Settings, DocumentHost);

            // WebKit postMessage shim so newtab.html / settings.html work identically
            const string bridgeShim = """
(function() {
  if (!window.chrome) window.chrome = {};
  if (!window.chrome.webview) {
    window.chrome.webview = {
      postMessage: function(msg) {
        try {
          if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.r1) {
            window.webkit.messageHandlers.r1.postMessage(typeof msg === 'string' ? msg : JSON.stringify(msg));
          }
        } catch (e) {}
      }
    };
  }
})();
""";

            var fullScript = bridgeShim + "\n" + baseScript;

            _activeUserScript = UserScript.New(
                source: fullScript,
                injectedFrames: UserContentInjectedFrames.AllFrames,
                injectionTime: UserScriptInjectionTime.Start,
                allowList: null,
                blockList: null
            );

            manager.AddScript(_activeUserScript);
        }
        catch (Exception ex)
        {
            Log.Write("LinuxAdBlocker.RefreshInjectedScript failed: " + ex.Message);
        }
    }

    public void HandleScriptMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("r1", out var kindProp)) return;
            var kind = kindProp.GetString();

            if (kind == "cos")
            {
                // Dynamic cosmetic CSS request from frame
                var host = root.TryGetProperty("host", out var h) ? (h.GetString() ?? "").ToLowerInvariant() : "";
                var enabled = _app.Settings.AdblockEnabled && !_app.Settings.WhitelistActive(DocumentHost) && _app.Settings.CosmeticEnabled;
                var css = enabled ? _app.Engine.CosmeticCss(host, _app.Settings.RemoveCookieBanners) : "";
                var payload = JsonSerializer.Serialize(css);

                var js = $"window.__r1apply && window.__r1apply({payload});";
                _ = _view.EvaluateJavascriptAsync(js);
            }
            else if (kind == "search" || kind == "openUrl" || kind == "saveSetting")
            {
                _onInternalCommand?.Invoke(json);
            }
        }
        catch { }
    }

    public bool ShouldBlockUrl(string rawUrl, ResType type)
    {
        if (!_app.Settings.AdblockEnabled) return false;
        if (!_app.Engine.IsLoaded) return false;
        if (rawUrl.Length < 10 || !rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        if (_app.Settings.WhitelistActive(DocumentHost)) return false;

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0) return false;

        var verdict = _app.Engine.Check(host, uri.PathAndQuery, DocumentHost, type);
        if (verdict == Verdict.Block)
        {
            BlockedCount++;
            BlockedBytes += Math.Min(rawUrl.Length * 4L, 262144L);
            _recent.Add(rawUrl);
            if (_recent.Count > 100) _recent.RemoveAt(0);
            Log.Write($"[Linux block#{BlockedCount}] {type} {rawUrl}");
            Changed?.Invoke();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            var manager = _view.GetUserContentManager();
            manager.RemoveAllScripts();
            manager.UnregisterScriptMessageHandler("r1", null);
        }
        catch { }
    }
}
