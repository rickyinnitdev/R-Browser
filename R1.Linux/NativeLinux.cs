using System.Runtime.InteropServices;

namespace R1.Linux;

internal static class NativeLinux
{
    [DllImport("libc.so.6", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    /// <summary>
    /// Sheds unused heap pages back to the Linux kernel immediately (glibc malloc_trim).
    /// Analogous to Win32 EmptyWorkingSet, keeping idle RAM footprint minimal.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(1, GCCollectionMode.Optimized);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                MallocTrim(0);
            }
        }
        catch { }
    }

    /// <summary>
    /// Reads resident set size (RSS) in megabytes from /proc/self/statm.
    /// </summary>
    public static long GetRssMemoryMb()
    {
        try
        {
            if (!File.Exists("/proc/self/statm")) return 0;
            var parts = File.ReadAllText("/proc/self/statm").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && long.TryParse(parts[1], out var pages))
            {
                return (pages * Environment.SystemPageSize) / (1024 * 1024);
            }
        }
        catch { }
        return 0;
    }
}
