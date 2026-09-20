using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace PanoramaDownloader.App.Services;

/// <summary>
/// 缩略图加载优先级：网络缓存 → WebView fetch → HttpClient(Cookie+Referer)
/// 使用 SkiaSharp 解码，兼容 WebP 等 WPF 原生不支持的格式。
/// </summary>
public static class ThumbnailImageLoader
{
    private static readonly HttpClient Http = CreateClient();

    public static Func<string, Task<byte[]?>>? WebFetchAsync { get; set; }
    public static Func<string, Task<string?>>? CookieHeaderAsync { get; set; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        return client;
    }

    public static async Task<BitmapImage?> LoadAsync(string? url, string? referer, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // data:image/...;base64,....  直接解码（来自页面 canvas 抓取）
        if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var comma = url.IndexOf(',');
                if (comma < 0)
                {
                    return null;
                }

                var b64 = url[(comma + 1)..];
                var dataBytes = Convert.FromBase64String(b64);
                return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => CreateBitmap(dataBytes));
            }
            catch
            {
                return null;
            }
        }

        var normalized = url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            return null;
        }

        byte[]? bytes = null;

        if (NetworkImageCache.TryGet(normalized, out var cached) && cached is not null)
        {
            bytes = cached;
        }

        if (bytes is null && WebFetchAsync is not null)
        {
            try
            {
                bytes = await WebFetchAsync(normalized).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    NetworkImageCache.Set(normalized, bytes);
                }
            }
            catch
            {
                bytes = null;
            }
        }

        if (bytes is null)
        {
            bytes = await DownloadViaHttpAsync(normalized, referer, cancellationToken).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                NetworkImageCache.Set(normalized, bytes);
            }
        }

        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => CreateBitmap(bytes));
    }

    private static async Task<byte[]?> DownloadViaHttpAsync(string url, string? referer, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Referer", string.IsNullOrWhiteSpace(referer)
                ? "https://www.720yun.com/"
                : referer);
            request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

            if (CookieHeaderAsync is not null)
            {
                try
                {
                    var cookie = await CookieHeaderAsync(url).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(cookie))
                    {
                        request.Headers.TryAddWithoutValidation("Cookie", cookie);
                    }
                }
                catch
                {
                    // ignore cookie errors
                }
            }

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? CreateBitmap(byte[] bytes)
    {
        try
        {
            // 先用 Skia 解码（支持 WebP），再转成 JPEG 给 WPF
            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is not null)
            {
                var maxW = 160;
                SKBitmap working = decoded;
                SKBitmap? scaled = null;
                try
                {
                    if (decoded.Width > maxW)
                    {
                        var h = Math.Max(1, decoded.Height * maxW / decoded.Width);
                        scaled = decoded.Resize(new SKImageInfo(maxW, h), SKSamplingOptions.Default);
                        if (scaled is not null)
                        {
                            working = scaled;
                        }
                    }

                    using var image = SKImage.FromBitmap(working);
                    using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                    return BitmapFromBytes(jpeg.ToArray());
                }
                finally
                {
                    scaled?.Dispose();
                }
            }

            // Skia 解不了时，尝试 WPF 原生
            return BitmapFromBytes(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage BitmapFromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
