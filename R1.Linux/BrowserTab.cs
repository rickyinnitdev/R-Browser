using System;
using Gtk;
using WebKit;
using R1.Core;

namespace R1.Linux;

internal sealed class BrowserTab : IDisposable
{
    public WebView? View;
    public LinuxAdBlocker? Blocker;
    public string Title = "New tab";
    public string Url = "";
    public bool Loading;
    public bool Suspended;
    public bool Hibernated;
    public bool IsPlayingAudio;
    public bool IsMuted;
    public DateTime LastActive = DateTime.Now;
    public double Zoom = 1.0;
    public Box TabHeaderBox = null!;
    public Label TitleLabel = null!;
    public Button CloseButton = null!;

    public bool IsInternal => Url.StartsWith("r1://", StringComparison.OrdinalIgnoreCase) ||
                              Url.StartsWith("https://r1.internal", StringComparison.OrdinalIgnoreCase);

    public void Touch() => LastActive = DateTime.Now;

    public void Dispose()
    {
        try { Blocker?.Dispose(); } catch { }
        Blocker = null;
        try { View?.Dispose(); } catch { }
        View = null;
    }
}
