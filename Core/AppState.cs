using System.Text.Json;
using System.Text.Json.Serialization;

namespace R1.Core;

public sealed class Settings
{
    public string SearchTemplate { get; set; } = "https://duckduckgo.com/?q={0}";
    public string HomePage { get; set; } = "r1://newtab";
    public bool AdblockEnabled { get; set; } = true;
    public bool CosmeticEnabled { get; set; } = true;
    public bool AntiAdblockEnabled { get; set; } = true;
    public bool RemoveCookieBanners { get; set; } = true;
    public bool SleepBackgroundTabs { get; set; } = true;
    public int SleepAfterSeconds { get; set; } = 45;
    public int RendererLimit { get; set; } = 3;
    public bool HardwareAcceleration { get; set; }
    public bool SecureDns { get; set; } = true;
    public bool DownloadAcceleration { get; set; } = true;
    public int DownloadConnections { get; set; } = 6;
    public bool RestoreSession { get; set; } = true;
    public bool VerticalTabs { get; set; } = false;
    public bool VerticalTabsCollapsed { get; set; } = false;
    public bool SidePanelLeft { get; set; } = false;
    public List<string> Whitelist { get; set; } = new();
    public List<string> SessionUrls { get; set; } = new();
    public List<string> FilterUrls { get; set; } = new()
    {
        "https://easylist.to/easylist/easylist.txt",
        "https://easylist.to/easylist/easyprivacy.txt",
        "https://raw.githubusercontent.com/AdguardTeam/AdguardFilters/master/AnnoyancesFilter/Popups/sections/antiadblock.txt",
        "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/quick-fixes.txt",
        "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&mimetype=plaintext"
    };
    public string FiltersUpdated { get; set; } = "";

    public bool WhitelistActive(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        foreach (var w in Whitelist)
            if (host.Equals(w, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + w, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

public sealed class Bookmark
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
}

public sealed class HistoryEntry
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime LastVisit { get; set; } = DateTime.Now;
    public int Visits { get; set; } = 1;
}

public sealed class BookmarkStore
{
    private readonly string _path;
    private readonly object _lock = new();
    public List<Bookmark> Items { get; private set; } = new();

    public BookmarkStore(string dir)
    {
        _path = Path.Combine(dir, "bookmarks.json");
        try
        {
            if (File.Exists(_path))
                Items = JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(_path)) ?? new();
        }
        catch { Items = new(); }
    }

    public bool Contains(string url)
    {
        lock (_lock)
        {
            return Items.Any(b => b.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Toggle(string title, string url)
    {
        lock (_lock)
        {
            var existing = Items.FirstOrDefault(b => b.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (existing != null) Items.Remove(existing);
            else Items.Insert(0, new Bookmark { Title = title, Url = url });
            Save();
        }
    }

    public void Remove(Bookmark b)
    {
        lock (_lock)
        {
            Items.Remove(b);
            Save();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            AppState.SafeWriteAllText(_path, JsonSerializer.Serialize(Items, JsonOpts));
        }
    }

    internal static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}

public sealed class HistoryStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private DateTime _lastSave = DateTime.MinValue;
    public List<HistoryEntry> Items { get; private set; } = new();

    public HistoryStore(string dir)
    {
        _path = Path.Combine(dir, "history.json");
        try
        {
            if (File.Exists(_path))
                Items = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch { Items = new(); }
    }

    public void Add(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("r1://")) return;
        lock (_lock)
        {
            var e = Items.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (e != null)
            {
                e.LastVisit = DateTime.Now;
                e.Visits++;
                if (!string.IsNullOrWhiteSpace(title)) e.Title = title;
                Items.Remove(e);
                Items.Insert(0, e);
            }
            else
            {
                Items.Insert(0, new HistoryEntry { Title = title, Url = url });
            }
            if (Items.Count > 4000) Items.RemoveRange(4000, Items.Count - 4000);
            if ((DateTime.Now - _lastSave).TotalSeconds > 5)
            {
                _lastSave = DateTime.Now;
                Save();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Items.Clear();
            Save();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            AppState.SafeWriteAllText(_path, JsonSerializer.Serialize(Items, BookmarkStore.JsonOpts));
        }
    }

    public List<HistoryEntry> Search(string q, int max = 30)
    {
        lock (_lock)
        {
            return Items
                .Where(i => i.Url.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(max)
                .ToList();
        }
    }
}

public sealed class AppState
{
    public Settings Settings { get; private set; } = new();
    public BookmarkStore Bookmarks { get; private set; } = null!;
    public HistoryStore History { get; private set; } = null!;
    public FilterEngine Engine { get; } = new();
    public bool EngineReady { get; private set; }
    public event Action? EngineLoaded;
    public string DataDir { get; }
    public string AssetsDir { get; }
    public string UserDataDir { get; }
    public string FiltersPath { get; }

    internal static void SafeWriteAllText(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public AppState()
    {
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "R1");
        Directory.CreateDirectory(DataDir);
        AssetsDir = Path.Combine(AppContext.BaseDirectory, "assets");
        UserDataDir = Path.Combine(DataDir, "WebView2");
        FiltersPath = Path.Combine(DataDir, "filters.cache.txt");

        LoadSettings();
        Bookmarks = new BookmarkStore(DataDir);
        History = new HistoryStore(DataDir);
    }

    private void LoadSettings()
    {
        var path = Path.Combine(DataDir, "settings.json");
        try
        {
            if (File.Exists(path))
                Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch { Settings = new Settings(); }
    }

    public void SaveSettings()
    {
        SafeWriteAllText(Path.Combine(DataDir, "settings.json"), JsonSerializer.Serialize(Settings, BookmarkStore.JsonOpts));
    }

    public void StartEngineLoad()
    {
        Task.Run(() =>
        {
            try
            {
                string text;
                if (File.Exists(FiltersPath) && new FileInfo(FiltersPath).Length > 100_000)
                {
                    text = File.ReadAllText(FiltersPath);
                }
                else
                {
                    var asm = typeof(AppState).Assembly;
                    var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("filters.dat", StringComparison.OrdinalIgnoreCase)) ?? "R1.assets.filters.dat";
                    using var stream = asm.GetManifestResourceStream(resName);
                    if (stream == null) return;
                    using var gz = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
                    using var reader = new StreamReader(gz);
                    text = reader.ReadToEnd();
                }
                Engine.Load(text);
                EngineReady = true;
                Log.Write($"engine network={Engine.NetworkRules} cosmetic={Engine.CosmeticRules} generic={Engine.GenericCosmeticCount}");
                EngineLoaded?.Invoke();
            }
            catch { }
        });
    }

    public async Task<bool> UpdateFiltersAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 RBrowser/1.0");
        var parts = new List<string>();
        foreach (var url in Settings.FilterUrls)
        {
            try
            {
                var txt = await http.GetStringAsync(url);
                parts.Add(txt);
            }
            catch { }
        }
        if (parts.Count == 0) return false;
        var merged = FilterEngine.Prune(parts);
        Engine.Load(merged);
        EngineReady = true;
        SafeWriteAllText(FiltersPath, merged);
        Settings.FiltersUpdated = DateTime.Now.ToString("yyyy-MM-dd");
        SaveSettings();
        EngineLoaded?.Invoke();
        return true;
    }
}
