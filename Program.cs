using System.Drawing;
using System.Windows.Forms;
using R1.Core;
using R1.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(new Font("Segoe UI", 9f));
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("UNHANDLED " + e.ExceptionObject);
        Application.ThreadException += (_, e) => Log.Write("THREAD EXCEPTION " + e.Exception);
        var app = new AppState();
        app.StartEngineLoad();

        bool incognito = args.Any(a => a.Equals("--incognito", StringComparison.OrdinalIgnoreCase));
        string? url = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        Application.Run(new BrowserAppContext(app, incognito, url));
    }
}

internal sealed class BrowserAppContext : ApplicationContext
{
    private static int _openCount;
    private static readonly object _sync = new();

    public BrowserAppContext(AppState app, bool incognito, string? url)
    {
        RegisterWindow(new MainForm(app, incognito, url));
    }

    public static void RegisterWindow(MainForm form)
    {
        lock (_sync) _openCount++;
        form.FormClosed += (_, _) =>
        {
            lock (_sync)
            {
                _openCount--;
                if (_openCount <= 0)
                {
                    Application.ExitThread();
                }
            }
        };
        form.Show();
    }
}
