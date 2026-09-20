using System.Collections.Concurrent;
using System.IO;

namespace PanoramaDownloader.App.Services;

/// <summary>
/// 缓存 WebView2 已下载的图片字节，供左侧缩略图直接使用。
/// </summary>
public static class NetworkImageCache
{
    private static readonly ConcurrentDictionary<string, byte[]> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Set(string url, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(url) || bytes.Length == 0)
        {
            return;
        }

        if (bytes.Length > 2 * 1024 * 1024)
        {
            return;
        }

        foreach (var key in KeysFor(url))
        {
            Cache[key] = bytes;
        }
    }

    public static bool TryGet(string? url, out byte[]? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        foreach (var key in KeysFor(url))
        {
            if (Cache.TryGetValue(key, out var found))
            {
                bytes = found;
                return true;
            }
        }

        return false;
    }

    public static void Clear() => Cache.Clear();

    public static int Count => Cache.Count;

    private static List<string> KeysFor(string url)
    {
        var keys = new List<string>();
        var normalized = Normalize(url);
        keys.Add(normalized);

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            keys.Add(uri.GetLeftPart(UriPartial.Path));
            var name = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                keys.Add(name);
            }
        }

        return keys;
    }

    private static string Normalize(string url)
    {
        url = url.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        return url;
    }
}
