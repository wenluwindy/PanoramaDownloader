using PanoramaDownloader.Core.Downloader;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Tests;

public class TileDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_SkipsExistingNonEmptyFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pano-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var relative = Path.Combine("l1", "f", "01_01.jpg");
            var full = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, [1, 2, 3, 4]);

            var downloader = new TileDownloader();
            var result = await downloader.DownloadAsync(
                [
                    new TileRequest
                    {
                        Url = "http://127.0.0.1:1/missing.jpg",
                        RelativePath = relative,
                        Face = "f",
                        Level = 1,
                        Row = 1,
                        Col = 1
                    }
                ],
                root,
                new DownloadOptions
                {
                    RetryCount = 0,
                    TimeoutSeconds = 2,
                    MaxConcurrency = 2
                });

            Assert.Equal(1, result.Ok);
            Assert.Equal(0, result.Fail);
            Assert.Empty(result.FailedTiles);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task DownloadAsync_RecordsFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "pano-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var downloader = new TileDownloader();
            var result = await downloader.DownloadAsync(
                [
                    new TileRequest
                    {
                        Url = "http://127.0.0.1:1/nope.jpg",
                        RelativePath = Path.Combine("l1", "f", "01_01.jpg"),
                        Face = "f",
                        Level = 1,
                        Row = 1,
                        Col = 1
                    }
                ],
                root,
                new DownloadOptions
                {
                    RetryCount = 0,
                    TimeoutSeconds = 2,
                    MaxConcurrency = 1
                });

            Assert.Equal(0, result.Ok);
            Assert.Equal(1, result.Fail);
            Assert.Single(result.FailedTiles);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
