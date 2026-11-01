using System;
using Gtk;
using R1.Core;

namespace R1.Linux;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool incognito = false;
        string? startUrl = null;

        foreach (var arg in args)
        {
            if (arg.Equals("--incognito", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-incognito", StringComparison.OrdinalIgnoreCase))
            {
                incognito = true;
            }
            else if (!arg.StartsWith("-") && startUrl == null)
            {
                startUrl = arg;
            }
        }

        // Initialize R1 Core application state & filter engine
        var appState = new AppState();
        appState.StartEngineLoad();

        // Initialize GTK 4 Application
        var app = Application.New("org.r1.browser", Gio.ApplicationFlags.FlagsNone);

        app.OnActivate += (sender, eventArgs) =>
        {
            var window = new MainWindow(app, appState, incognito, startUrl);
            window.Present();
        };

        return app.RunWithSynchronizationContext(null);
    }
}
