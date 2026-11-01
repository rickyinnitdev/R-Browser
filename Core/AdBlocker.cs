using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace R1.Core;

/// <summary>Per-tab ad blocker: request interception + cosmetic/anti-adblock injection.</summary>
public sealed class AdBlocker
{
    private readonly AppState _app;
    private readonly CoreWebView2 _core;
    private readonly List<string> _recent = new();
    private string? _scriptId;
    private string? _registeredScript;
    private readonly SemaphoreSlim _scriptGate = new(1, 1);
    private DateTime _lastPopup = DateTime.MinValue;

    public string DocumentHost { get; set; } = "";
    public int BlockedCount { get; private set; }
    public long BlockedBytes { get; private set; }
    public IReadOnlyList<string> Recent => _recent;
    public event Action? Changed;

    public AdBlocker(AppState app, CoreWebView2 core)
    {
        _app = app;
        _core = core;
    }

    public async Task AttachAsync()
    {
        _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _core.WebResourceRequested += OnRequest;
        _core.WebMessageReceived += OnMessage;
        var script = InjectedScripts.Build(_app.Engine, _app.Settings, DocumentHost);
        Log.Write($"Attach host={DocumentHost} scriptChars={script.Length}");
        try { await UpdateScriptAsync(script); } catch (Exception ex) { Log.Write("Attach script failed: " + ex.Message); }
    }

    /// <summary>Push fresh domain CSS into the live document (settings changes, engine late-load).</summary>
    public async Task RefreshCssAsync()
    {
        try { await UpdateScriptAsync(InjectedScripts.Build(_app.Engine, _app.Settings, DocumentHost)); }
        catch { return; }
        var enabled = _app.Settings.AdblockEnabled && !_app.Settings.WhitelistActive(DocumentHost);
        var css = enabled && _app.Settings.CosmeticEnabled
            ? _app.Engine.CosmeticCss(DocumentHost, _app.Settings.RemoveCookieBanners)
            : "";
        var json = JsonSerializer.Serialize(css);
        Log.Write($"RefreshCss host={DocumentHost} cssChars={css.Length}");
        try { await _core.ExecuteScriptAsync($"window.__r1configure && window.__r1configure({enabled.ToString().ToLowerInvariant()}, {(_app.Settings.AntiAdblockEnabled && enabled).ToString().ToLowerInvariant()}); window.__r1apply && window.__r1apply({json})"); } catch { }
    }

    private async Task UpdateScriptAsync(string script)
    {
        await _scriptGate.WaitAsync();
        try
        {
            if (_registeredScript == script) return;
            var next = await _core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            if (_scriptId != null) _core.RemoveScriptToExecuteOnDocumentCreated(_scriptId);
            _scriptId = next;
            _registeredScript = script;
        }
        finally { _scriptGate.Release(); }
    }

    private void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!_app.Settings.AdblockEnabled) return;
        if (!_app.Engine.IsLoaded) return;

        var raw = e.Request.Uri;
        if (raw.Length < 10) return;
        if (!raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        if (_app.Settings.WhitelistActive(DocumentHost)) return;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return;
        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0) return;

        var verdict = _app.Engine.Check(host, uri.PathAndQuery, DocumentHost, MapType(e.ResourceContext));
        if (verdict != Verdict.Block) return;

        try
        {
            e.Response = BuildBlockedResponse(_core, MapType(e.ResourceContext));
        }
        catch { return; }

        BlockedCount++;
        BlockedBytes += Math.Min(raw.Length * 4L, 262144L);
        _recent.Add(raw);
        if (_recent.Count > 100) _recent.RemoveAt(0);
        Log.Write($"block#{BlockedCount} {MapType(e.ResourceContext)} {raw}");
        if (BlockedCount == 1 || BlockedCount % 4 == 0) Changed?.Invoke();
    }

    // Blocked requests answer 403 Forbidden instead of 204 No Content. Chromium's
    // renderer crashes (STATUS_ACCESS_VIOLATION) on pages where a blocked script
    // receives a successful 2xx response; an error status is handled cleanly.
    private static CoreWebView2WebResourceResponse BuildBlockedResponse(CoreWebView2 core, ResType type)
    {
        return core.Environment.CreateWebResourceResponse(
            Stream.Null, 403, "Forbidden",
            "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("r1", out var kind)) return;
            if (kind.GetString() != "cos") return;

            var host = root.TryGetProperty("host", out var h) ? (h.GetString() ?? "").ToLowerInvariant() : "";
            var css = _app.Settings.AdblockEnabled && !_app.Settings.WhitelistActive(DocumentHost) && _app.Settings.CosmeticEnabled
                ? _app.Engine.CosmeticCss(host, _app.Settings.RemoveCookieBanners)
                : "";
            var payload = "{\"r1\":\"css\",\"css\":" + JsonSerializer.Serialize(css) + "}";
            Log.Write($"cos host={host} cssChars={css.Length}");
            try { _core.PostWebMessageAsJson(payload); } catch { }
        }
        catch { }
    }

    public bool ShouldBlockPopup(string raw, bool userInitiated)
    {
        bool valid = Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || uri.Scheme == "http");
        bool protectedSite = _app.Settings.AdblockEnabled && !_app.Settings.WhitelistActive(DocumentHost);
        bool blocked = !valid || !userInitiated;
        if (!blocked && protectedSite)
        {
            blocked = _app.Engine.Check(uri!.Host, uri.PathAndQuery, DocumentHost, ResType.Document) == Verdict.Block ||
                (DateTime.UtcNow - _lastPopup).TotalMilliseconds < 1000;
        }
        if (!blocked) { _lastPopup = DateTime.UtcNow; return false; }
        BlockedCount++;
        _recent.Add("Popup: " + raw);
        if (_recent.Count > 100) _recent.RemoveAt(0);
        Changed?.Invoke();
        return true;
    }

    private static ResType MapType(CoreWebView2WebResourceContext ctx) => ctx switch
    {
        CoreWebView2WebResourceContext.Document => ResType.Document,
        CoreWebView2WebResourceContext.Stylesheet => ResType.Stylesheet,
        CoreWebView2WebResourceContext.Image => ResType.Image,
        CoreWebView2WebResourceContext.Media => ResType.Media,
        CoreWebView2WebResourceContext.Font => ResType.Font,
        CoreWebView2WebResourceContext.Script => ResType.Script,
        CoreWebView2WebResourceContext.XmlHttpRequest => ResType.Xhr,
        CoreWebView2WebResourceContext.Fetch => ResType.Xhr,
        CoreWebView2WebResourceContext.EventSource => ResType.Xhr,
        CoreWebView2WebResourceContext.Ping => ResType.Ping,
        CoreWebView2WebResourceContext.Websocket => ResType.Websocket,
        _ => ResType.Other
    };
}
