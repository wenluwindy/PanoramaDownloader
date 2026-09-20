using PanoramaDownloader.Core.Storage;

namespace PanoramaDownloader.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Clamp_LimitsRanges()
    {
        var s = new AppSettings
        {
            MaxConcurrency = 100,
            TimeoutSeconds = 1,
            RetryCount = 99,
            JpegQuality = 10,
            MaxEquirectWidth = 50,
            DefaultExportMode = "Nope",
            DownloadDirectory = ""
        };

        SettingsStore.Clamp(s);

        Assert.Equal(16, s.MaxConcurrency);
        Assert.Equal(10, s.TimeoutSeconds);
        Assert.Equal(10, s.RetryCount);
        Assert.Equal(60, s.JpegQuality);
        Assert.Equal(1024, s.MaxEquirectWidth);
        Assert.Equal("Both", s.DefaultExportMode);
        Assert.False(string.IsNullOrWhiteSpace(s.DownloadDirectory));
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "pano-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            var settings = store.Load();
            settings.MaxConcurrency = 6;
            settings.DownloadDirectory = Path.Combine(root, "downloads");
            settings.DefaultExportMode = "Tiles";
            store.Save(settings);

            var loaded = store.Load();
            Assert.Equal(6, loaded.MaxConcurrency);
            Assert.Equal("Tiles", loaded.DefaultExportMode);
            Assert.Equal(settings.DownloadDirectory, loaded.DownloadDirectory);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
