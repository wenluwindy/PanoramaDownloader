using System.Net.Http;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Core.Downloader;

public sealed class DownloadProgress
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public string? CurrentUrl { get; set; }
    /// <summary>download / probe / stitch / done</summary>
    public string Phase { get; set; } = "download";
    public string? StatusText { get; set; }
    public double Percent => Total <= 0 ? 0 : Math.Min(100, Completed * 100.0 / Total);
}

public sealed class DownloadOptions
{
    public string Referer { get; set; } = "https://www.720yun.com/";
    public string? Cookie { get; set; }
    public int MaxConcurrency { get; set; } = 8;
    public int RetryCount { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 60;
    public IProgress<DownloadProgress>? Progress { get; set; }
    public CancellationToken CancellationToken { get; set; }
}

public sealed class TileDownloader
{
    private static readonly HttpClient SharedHttp = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        return client;
    }

    public async Task<DownloadResult> DownloadAsync(
        IReadOnlyList<TileRequest> tiles,
        string outputDirectory,
        DownloadOptions options)
    {
        Directory.CreateDirectory(outputDirectory);
        var progress = new DownloadProgress
        {
            Total = tiles.Count,
            Phase = "download",
            StatusText = "下载瓦片"
        };
        options.Progress?.Report(progress);

        using var gate = new SemaphoreSlim(Math.Clamp(options.MaxConcurrency, 1, 32));
        var ok = 0;
        var fail = 0;
        var failed = new List<TileRequest>();
        var failedLock = new object();

        var tasks = tiles.Select(async tile =>
        {
            await gate.WaitAsync(options.CancellationToken).ConfigureAwait(false);
            try
            {
                var target = Path.Combine(outputDirectory, tile.RelativePath);
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(target) && new FileInfo(target).Length > 0)
                {
                    Interlocked.Increment(ref ok);
                    Report();
                    return;
                }

                var bytes = await DownloadWithRetryAsync(tile.Url, options).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0)
                {
                    Interlocked.Increment(ref fail);
                    lock (failedLock)
                    {
                        failed.Add(tile);
                    }

                    Report(tile.Url);
                    return;
                }

                await File.WriteAllBytesAsync(target, bytes, options.CancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref ok);
                Report(tile.Url);
            }
            finally
            {
                gate.Release();
            }

            void Report(string? current = null)
            {
                var completed = Volatile.Read(ref ok) + Volatile.Read(ref fail);
                var failedCount = Volatile.Read(ref fail);
                options.Progress?.Report(new DownloadProgress
                {
                    Total = progress.Total,
                    Completed = completed,
                    Failed = failedCount,
                    CurrentUrl = current,
                    Phase = "download",
                    StatusText = "下载瓦片"
                });
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new DownloadResult
        {
            Ok = ok,
            Fail = fail,
            FailedTiles = failed
        };
    }

    private static async Task<byte[]?> DownloadWithRetryAsync(string url, DownloadOptions options)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= options.RetryCount; attempt++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Referer", options.Referer);
                request.Headers.TryAddWithoutValidation("Origin", "https://www.720yun.com");
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
                if (!string.IsNullOrWhiteSpace(options.Cookie))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", options.Cookie);
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

                using var response = await SharedHttp.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    await Task.Delay(200 * (attempt + 1), options.CancellationToken).ConfigureAwait(false);
                    continue;
                }

                return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (options.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 含超时导致的 TaskCanceledException
                last = ex;
                if (attempt < options.RetryCount)
                {
                    await Task.Delay(200 * (attempt + 1), options.CancellationToken).ConfigureAwait(false);
                }
            }
        }

        _ = last;
        return null;
    }

    /// <summary>尝试下载单个瓦片；已存在非空文件视为成功。</summary>
    public async Task<bool> TryDownloadOneAsync(
        TileRequest tile,
        string outputDirectory,
        DownloadOptions options)
    {
        var target = Path.Combine(outputDirectory, tile.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            return true;
        }

        var bytes = await DownloadWithRetryAsync(tile.Url, options).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        await File.WriteAllBytesAsync(target, bytes, options.CancellationToken).ConfigureAwait(false);
        return true;
    }
}
