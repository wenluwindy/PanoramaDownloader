using System.Text.Json;
using System.Text.RegularExpressions;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Core.Detectors;

/// <summary>
/// 从 720 云作品/场景 API JSON 与页面 HTML 中提取场景元数据与瓦片根路径。
/// </summary>
public static partial class Yun720ConfigExtractor
{
    public static void EnrichManifest(
        PanoramaManifest manifest,
        IReadOnlyList<CapturedNetworkEntry> entries,
        string? pageHtml)
    {
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sceneHints = new List<SceneHint>();

        foreach (var entry in entries)
        {
            CollectImgsBases(entry.Url, bases);
            if (!string.IsNullOrWhiteSpace(entry.BodyText))
            {
                CollectImgsBasesFromText(entry.BodyText, bases);
                if (LooksLikeJson(entry.BodyText))
                {
                    TryParseJsonBody(entry.BodyText, sceneHints, bases);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(pageHtml))
        {
            CollectImgsBasesFromText(pageHtml, bases);
            ExtractEmbeddedJsonChunks(pageHtml, sceneHints, bases);
            var title = ExtractOgTitle(pageHtml);
            if (!string.IsNullOrWhiteSpace(title) &&
                (string.IsNullOrWhiteSpace(manifest.Title) || manifest.Title == "720 云作品"))
            {
                manifest.Title = title!;
            }
        }

        MergeSceneHints(manifest, sceneHints);

        // 尚无瓦片根路径时，用捕获到的 /imgs/ 前缀补到缺省场景
        if (bases.Count > 0)
        {
            ApplyTileBases(manifest, bases.ToList());
        }
    }

    private static void MergeSceneHints(PanoramaManifest manifest, List<SceneHint> hints)
    {
        if (hints.Count == 0)
        {
            return;
        }

        // 优先保留带缩略图 / 瓦片路径的高质量提示
        var ranked = hints
            .GroupBy(h => h.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(Score).First())
            .OrderByDescending(Score)
            .ToList();

        var byId = manifest.Scenes.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var hint in ranked)
        {
            if (byId.TryGetValue(hint.Id, out var existing))
            {
                if (!string.IsNullOrWhiteSpace(hint.Name) &&
                    (string.IsNullOrWhiteSpace(existing.Name) || existing.Name == existing.Id))
                {
                    existing.Name = hint.Name!;
                }

                if (string.IsNullOrWhiteSpace(existing.ThumbnailUrl) &&
                    !string.IsNullOrWhiteSpace(hint.ThumbnailUrl))
                {
                    existing.ThumbnailUrl = hint.ThumbnailUrl;
                }

                if (string.IsNullOrWhiteSpace(existing.TileBaseUrl) &&
                    !string.IsNullOrWhiteSpace(hint.TileBaseUrl))
                {
                    existing.TileBaseUrl = hint.TileBaseUrl;
                }

                continue;
            }

            // 避免把 API 噪声对象当成场景：至少要有名字或缩略图或瓦片路径
            if (string.IsNullOrWhiteSpace(hint.Name) &&
                string.IsNullOrWhiteSpace(hint.ThumbnailUrl) &&
                string.IsNullOrWhiteSpace(hint.TileBaseUrl))
            {
                continue;
            }

            var scene = new PanoramaScene
            {
                Id = hint.Id,
                Name = string.IsNullOrWhiteSpace(hint.Name) ? hint.Id : hint.Name!,
                ThumbnailUrl = hint.ThumbnailUrl,
                TileBaseUrl = hint.TileBaseUrl
            };
            manifest.Scenes.Add(scene);
            byId[scene.Id] = scene;
        }
    }

    private static int Score(SceneHint h)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(h.ThumbnailUrl)) score += 3;
        if (!string.IsNullOrWhiteSpace(h.TileBaseUrl)) score += 5;
        if (!string.IsNullOrWhiteSpace(h.Name) && h.Name != h.Id) score += 2;
        if (h.FromScenesArray) score += 4;
        return score;
    }

    private static void ApplyTileBases(PanoramaManifest manifest, List<string> bases)
    {
        if (manifest.Scenes.Count == 0 || bases.Count == 0)
        {
            return;
        }

        var normalized = bases
            .Select(b => b.Trim().TrimEnd('/') + "/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 单场景：可把唯一/主 base 填上
        if (manifest.Scenes.Count == 1)
        {
            var scene = manifest.Scenes[0];
            if (string.IsNullOrWhiteSpace(scene.TileBaseUrl))
            {
                scene.TileBaseUrl = normalized[0];
            }

            return;
        }

        // 多场景：只按路径包含场景 id / 资源目录精确匹配，绝不把同一个 base 广播给所有场景
        var unused = new List<string>(normalized);
        foreach (var scene in manifest.Scenes)
        {
            if (!string.IsNullOrWhiteSpace(scene.TileBaseUrl))
            {
                unused.RemoveAll(b =>
                    string.Equals(b.TrimEnd('/'), scene.TileBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                continue;
            }

            var hit = unused.FirstOrDefault(b =>
                (!string.IsNullOrWhiteSpace(scene.Id) &&
                 b.Contains(scene.Id, StringComparison.OrdinalIgnoreCase)));
            if (hit is not null)
            {
                scene.TileBaseUrl = hit;
                unused.Remove(hit);
            }
        }

        // 不再「按序填入」——错误绑定会导致导出永远是第一个全景
    }

    private static void TryParseJsonBody(string body, List<SceneHint> hints, HashSet<string> bases)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            WalkJson(doc.RootElement, hints, bases, inScenesArray: false, depth: 0);
        }
        catch (JsonException)
        {
            // ignore
        }
    }

    private static void WalkJson(
        JsonElement element,
        List<SceneHint> hints,
        HashSet<string> bases,
        bool inScenesArray,
        int depth)
    {
        if (depth > 14)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                TryAddSceneHint(element, hints, bases, inScenesArray);

                foreach (var prop in element.EnumerateObject())
                {
                    var name = prop.Name;
                    if (IsPathProperty(name) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        CollectImgsBases(prop.Value.GetString(), bases);
                        CollectImgsBasesFromText(prop.Value.GetString() ?? "", bases);
                    }

                    var childInScenes = inScenesArray ||
                                        name.Equals("scenes", StringComparison.OrdinalIgnoreCase) ||
                                        name.Equals("panos", StringComparison.OrdinalIgnoreCase) ||
                                        name.Equals("sceneList", StringComparison.OrdinalIgnoreCase) ||
                                        name.Equals("panoList", StringComparison.OrdinalIgnoreCase);

                    WalkJson(prop.Value, hints, bases, childInScenes, depth + 1);
                }

                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkJson(item, hints, bases, inScenesArray, depth + 1);
                }

                break;
            case JsonValueKind.String:
                CollectImgsBases(element.GetString(), bases);
                CollectImgsBasesFromText(element.GetString() ?? "", bases);
                break;
        }
    }

    private static void TryAddSceneHint(
        JsonElement element,
        List<SceneHint> hints,
        HashSet<string> bases,
        bool inScenesArray)
    {
        var id = TryGetString(element, "id", "sceneId", "sid", "panoId", "partnerId", "pid");
        var name = TryGetString(element, "name", "sceneName", "title", "panoName");
        var thumb = TryGetString(element, "thumb", "thumbnail", "thumbUrl", "cover", "coverUrl", "picUrl", "img", "image", "snapshot");
        var path = TryGetString(element, "path", "imgPath", "tilePath", "panoPath", "resourcePath", "basePath", "imgs", "url");

        string? tileBase = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            CollectImgsBases(path, bases);
            CollectImgsBasesFromText(path, bases);
            tileBase = NormalizeImgsBase(path);
        }

        // 噪声过滤：不在 scenes 数组里时，必须同时有 id + (name 或 thumb 或 tile)
        if (!inScenesArray)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(thumb) &&
                string.IsNullOrWhiteSpace(tileBase))
            {
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var sceneId = string.IsNullOrWhiteSpace(id)
            ? $"scene_{hints.Count + 1}"
            : id!;

        hints.Add(new SceneHint
        {
            Id = sceneId,
            Name = name,
            ThumbnailUrl = NormalizeUrl(thumb),
            TileBaseUrl = tileBase,
            FromScenesArray = inScenesArray
        });
    }

    private static bool IsPathProperty(string name) =>
        name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("imgs", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("src", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cdn", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("resource", StringComparison.OrdinalIgnoreCase);

    private static void ExtractEmbeddedJsonChunks(string html, List<SceneHint> hints, HashSet<string> bases)
    {
        foreach (Match m in JsonObjectRegex().Matches(html))
        {
            var chunk = m.Value;
            if (chunk.Length < 40 || chunk.Length > 500_000)
            {
                continue;
            }

            if (!(chunk.Contains("scene", StringComparison.OrdinalIgnoreCase) ||
                  chunk.Contains("imgs", StringComparison.OrdinalIgnoreCase) ||
                  chunk.Contains("pano", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            TryParseJsonBody(chunk, hints, bases);
        }
    }

    private static void CollectImgsBases(string? url, HashSet<string> bases)
    {
        var normalized = NormalizeImgsBase(url);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            bases.Add(normalized!);
        }
    }

    private static void CollectImgsBasesFromText(string text, HashSet<string> bases)
    {
        foreach (Match m in ImgsBaseRegex().Matches(text))
        {
            bases.Add(m.Groups["base"].Value.TrimEnd('/') + "/");
        }
    }

    /// <summary>
    /// 将 resource/.../imgs 或完整瓦片 URL 归一化为以 /imgs/ 结尾的根路径。
    /// </summary>
    public static string? NormalizeImgsBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim().Trim('"', '\'');
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        // 已是完整 imgs 根
        var m = ImgsBaseRegex().Match(value);
        if (m.Success)
        {
            return m.Groups["base"].Value.TrimEnd('/') + "/";
        }

        // 相对路径：resource/.../imgs 或 .../imgs/
        if (value.Contains("/imgs", StringComparison.OrdinalIgnoreCase))
        {
            var idx = value.IndexOf("/imgs", StringComparison.OrdinalIgnoreCase);
            var prefix = value[..(idx + 5)];
            if (!prefix.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // 相对路径无法单独用，留给调用方拼 CDN；仍返回带 imgs 的相对形态无意义
                return null;
            }

            return prefix.TrimEnd('/') + "/";
        }

        return null;
    }

    private static string? ExtractOgTitle(string html)
    {
        var m = OgTitleRegex().Match(html);
        if (!m.Success)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[2].Value : m.Groups[1].Value;
        return System.Net.WebUtility.HtmlDecode(title).Trim();
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                {
                    return prop.GetString();
                }

                if (prop.ValueKind is JsonValueKind.Number)
                {
                    return prop.ToString();
                }
            }
        }

        foreach (var prop in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ValueKind == JsonValueKind.Number
                            ? prop.Value.ToString()
                            : null;
                }
            }
        }

        return null;
    }

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim().Trim('"', '\'');
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        return url;
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.StartsWith("{") || trimmed.StartsWith("[");
    }

    // https://ssl-panoimg6.720static.com/resource/prod/.../521330/imgs/
    [GeneratedRegex(
        @"(?<base>https?://[^\s""'<>]+?/imgs)(?:/|(?=[""'\s<>?]|$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ImgsBaseRegex();

    [GeneratedRegex(
        @"\{[^{}]{20,}\}",
        RegexOptions.Compiled)]
    private static partial Regex JsonObjectRegex();

    [GeneratedRegex(
        @"property=[""']og:title[""']\s+content=[""']([^""']+)[""']|content=[""']([^""']+)[""']\s+property=[""']og:title[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex OgTitleRegex();

    private sealed class SceneHint
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? ThumbnailUrl { get; set; }
        public string? TileBaseUrl { get; set; }
        public bool FromScenesArray { get; set; }
    }
}
