using System.Text.Json;

namespace PanoramaDownloader.Core.Storage;

public sealed class ExportHistoryItem
{
    public string SourceUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SceneName { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string ExportMode { get; set; } = string.Empty;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "全景图下载");

    public int MaxConcurrency { get; set; } = 8;
    public int TimeoutSeconds { get; set; } = 60;
    public int RetryCount { get; set; } = 3;
    public string DefaultExportMode { get; set; } = "Both"; // Tiles | Equirect | Both
    public int JpegQuality { get; set; } = 92;
    /// <summary>等距柱状整图最大宽度（像素），默认 4096。</summary>
    public int MaxEquirectWidth { get; set; } = 4096;
    public bool OpenFolderAfterExport { get; set; } = true;
    public bool DisclaimerAccepted { get; set; }
    public List<string> RecentUrls { get; set; } = [];
    public List<ExportHistoryItem> RecentExports { get; set; } = [];
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SettingsStore(string? rootDirectory = null)
    {
        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PanoramaDownloader");
        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, "settings.json");
        UserDataDirectory = Path.Combine(root, "WebView2UserData");
        Directory.CreateDirectory(UserDataDirectory);
    }

    public string UserDataDirectory { get; }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return Clamp(new AppSettings());
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return Clamp(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
        }
        catch
        {
            return Clamp(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        Clamp(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }

    public void AddRecentUrl(AppSettings settings, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        settings.RecentUrls.RemoveAll(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase));
        settings.RecentUrls.Insert(0, url.Trim());
        if (settings.RecentUrls.Count > 20)
        {
            settings.RecentUrls = settings.RecentUrls.Take(20).ToList();
        }

        Save(settings);
    }

    public void AddRecentExport(AppSettings settings, ExportHistoryItem item)
    {
        settings.RecentExports.RemoveAll(e =>
            string.Equals(e.OutputDirectory, item.OutputDirectory, StringComparison.OrdinalIgnoreCase));
        settings.RecentExports.Insert(0, item);
        if (settings.RecentExports.Count > 30)
        {
            settings.RecentExports = settings.RecentExports.Take(30).ToList();
        }

        Save(settings);
    }

    public static AppSettings Clamp(AppSettings settings)
    {
        settings.MaxConcurrency = Math.Clamp(settings.MaxConcurrency, 2, 16);
        settings.TimeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 10, 300);
        settings.RetryCount = Math.Clamp(settings.RetryCount, 0, 10);
        settings.JpegQuality = Math.Clamp(settings.JpegQuality, 60, 100);
        settings.MaxEquirectWidth = Math.Clamp(settings.MaxEquirectWidth, 1024, 16384);
        if (string.IsNullOrWhiteSpace(settings.DownloadDirectory))
        {
            settings.DownloadDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "全景图下载");
        }

        if (settings.DefaultExportMode is not ("Tiles" or "Equirect" or "Both"))
        {
            settings.DefaultExportMode = "Both";
        }

        return settings;
    }
}
