using Microsoft.Web.WebView2.WinForms;

namespace R1.UI;

internal sealed class BrowserTab : IDisposable
{
    public WebView2 View = null!;
    public Core.AdBlocker? Blocker;
    public string Title = "New tab";
    public string Url = "";
    public bool Loading;
    public Task? Initialization;
    public bool Disposed;
    public bool Suspended;
    public bool Crashed;
    public bool IsPlayingAudio;
    public bool IsMuted;
    public bool Hibernated;
    public DateTime LastActive = DateTime.Now;
    public double Zoom = 1;
    public Image? Icon;

    public bool IsInternal => Url.StartsWith("r1://", StringComparison.OrdinalIgnoreCase) ||
                              Url.StartsWith("https://r1.internal", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Disposed = true;
        try { Blocker = null; View?.Dispose(); } catch { }
        // Icons are shared and owned by the form cache.
        Icon = null;
    }
}
