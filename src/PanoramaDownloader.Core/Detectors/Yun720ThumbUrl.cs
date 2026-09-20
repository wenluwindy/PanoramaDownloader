using System.Text.RegularExpressions;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Core.Detectors;

/// <summary>
/// 从 720 云场景菜单缩略图 URL 推导瓦片根路径与资源 ID。
/// 例：https://thumb-t.720static.com/resource/prod/.../93539831/imgs/thumb.jpg?...
///  → https://ssl-panoimg….720static.com/resource/prod/.../93539831/imgs/
/// </summary>
public static partial class Yun720ThumbUrl
{
    public static string? ExtractResourceId(string? thumbOrTileUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbOrTileUrl))
        {
            return null;
        }

        var m = ResourceIdRegex().Match(StripQuery(thumbOrTileUrl));
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? ExtractResourceImgsPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var m = ResourcePathRegex().Match(StripQuery(url));
        return m.Success ? m.Groups["path"].Value.TrimEnd('/') + "/" : null;
    }

    public static string? DeriveTileBase(
        string? thumbUrl,
        IReadOnlyList<CapturedNetworkEntry>? entries = null)
    {
        if (string.IsNullOrWhiteSpace(thumbUrl))
        {
            return null;
        }

        thumbUrl = thumbUrl.Trim().Trim('"', '\'');
        if (thumbUrl.StartsWith("//", StringComparison.Ordinal))
        {
            thumbUrl = "https:" + thumbUrl;
        }

        var path = ExtractResourceImgsPath(thumbUrl);
        if (string.IsNullOrWhiteSpace(path))
        {
            return Yun720ConfigExtractor.NormalizeImgsBase(thumbUrl);
        }

        // 优先复用已捕获瓦片请求的 CDN 主机
        if (entries is { Count: > 0 })
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Url))
                {
                    continue;
                }

                if (!entry.Url.Contains(path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                    !entry.Url.Contains(path, StringComparison.OrdinalIgnoreCase))
                {
                    // 也按资源 ID 匹配
                    var rid = ExtractResourceId(thumbUrl);
                    if (rid is null || !entry.Url.Contains("/" + rid + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                var parsed = Yun720TileInferencer.TryParseTileUrl(entry.Url);
                if (parsed is not null)
                {
                    return parsed.BaseUrl;
                }

                var fromEntry = Yun720ConfigExtractor.NormalizeImgsBase(entry.Url);
                if (!string.IsNullOrWhiteSpace(fromEntry))
                {
                    return fromEntry;
                }
            }

            // 任意已观测到的 panoimg 主机 + 当前资源 path
            foreach (var entry in entries)
            {
                if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var u))
                {
                    continue;
                }

                if (u.Host.Contains("panoimg", StringComparison.OrdinalIgnoreCase))
                {
                    return $"https://{u.Host}{path}";
                }
            }
        }

        if (!Uri.TryCreate(thumbUrl, UriKind.Absolute, out var thumbUri))
        {
            return null;
        }

        var host = RewriteThumbHostToPanoHost(thumbUri.Host);
        return $"https://{host}{path}";
    }

    private static string RewriteThumbHostToPanoHost(string host)
    {
        if (host.Contains("panoimg", StringComparison.OrdinalIgnoreCase))
        {
            return host;
        }

        // thumb-t.720static.com / thumb.720static.com → ssl-panoimg.720static.com
        if (host.Contains("thumb", StringComparison.OrdinalIgnoreCase))
        {
            return "ssl-panoimg.720static.com";
        }

        return host;
    }

    private static string StripQuery(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q >= 0 ? url[..q] : url;
    }

    // .../93539831/imgs/thumb.jpg
    [GeneratedRegex(@"/([A-Za-z0-9_-]+)/imgs/(?:thumb\.[A-Za-z0-9]+)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ResourceIdRegex();

    [GeneratedRegex(@"(?<path>/resource/[^?\s""']+/imgs)/?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ResourcePathRegex();
}
