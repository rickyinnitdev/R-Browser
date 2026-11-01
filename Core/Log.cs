using System.Text;

namespace R1.Core;

/// <summary>Opt-in diagnostics: set R1_LOG=1 to append engine events to %LOCALAPPDATA%\R1\r1.log.</summary>
internal static class Log
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("R1_LOG") == "1";
    private static readonly object Gate = new();
    private static string? _path;

    public static void Write(string what)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                _path ??= Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "R1", "r1.log");
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {what}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}
