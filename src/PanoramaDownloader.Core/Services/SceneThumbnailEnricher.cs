using System.Text.Json;
using System.Text.RegularExpressions;
using PanoramaDownloader.Core.Detectors;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Core.Services;

/// <summary>
/// 从页面脚本返回的 JSON / 网络捕获中补全场景缩略图。
/// </summary>
public static class SceneThumbnailEnricher
{
    public static void ApplyPageMeta(PanoramaManifest manifest, string? pageMetaJson, IReadOnlyList<CapturedNetworkEntry> entries)
    {
        if (!string.IsNullOrWhiteSpace(pageMetaJson))
        {
            TryMergeScenesFromMeta(manifest, pageMetaJson, entries);
        }

        // 缩略图仍为空时，用网络捕获候选补齐（按序，且不覆盖已有 http 缩略图）
        var candidates = CollectThumbnailCandidates(entries, pageMetaJson);
        var next = 0;
        foreach (var scene in manifest.Scenes)
        {
            if (!string.IsNullOrWhiteSpace(scene.ThumbnailUrl))
            {
                continue;
            }

            if (next < candidates.Count)
            {
                scene.ThumbnailUrl = candidates[next++];
            }
        }

        // 凡有缩略图但无瓦片根路径的，从 thumb URL 推导
        foreach (var scene in manifest.Scenes)
        {
            scene.ThumbnailUrl = NormalizeUrl(scene.ThumbnailUrl) ?? scene.ThumbnailUrl;

            if (!string.IsNullOrWhiteSpace(scene.TileBaseUrl))
            {
                continue;
            }

            var derived = Yun720ThumbUrl.DeriveTileBase(scene.ThumbnailUrl, entries);
            if (!string.IsNullOrWhiteSpace(derived))
            {
                scene.TileBaseUrl = derived;
            }
        }
    }

    private static void TryMergeScenesFromMeta(
        PanoramaManifest manifest,
        string pageMetaJson,
        IReadOnlyList<CapturedNetworkEntry> entries)
    {
        try
        {
            using var doc = JsonDocument.Parse(pageMetaJson);
            if (!doc.RootElement.TryGetProperty("scenes", out var scenesEl) ||
                scenesEl.ValueKind != JsonValueKind.Array)
            {
                ApplyImagesOnly(manifest, doc.RootElement);
                return;
            }

            var source = doc.RootElement.TryGetProperty("source", out var srcEl)
                ? srcEl.GetString() ?? ""
                : "";
            var fromMenu = source.Equals("itemsWp", StringComparison.OrdinalIgnoreCase);

            var existingById = manifest.Scenes.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            var existingByResource = new Dictionary<string, PanoramaScene>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in manifest.Scenes)
            {
                var rid = Yun720ThumbUrl.ExtractResourceId(s.ThumbnailUrl)
                          ?? Yun720ThumbUrl.ExtractResourceId(s.TileBaseUrl)
                          ?? Yun720ThumbUrl.ExtractResourceId(s.Id);
                if (!string.IsNullOrWhiteSpace(rid))
                {
                    existingByResource[rid!] = s;
                }
            }

            var metaScenes = scenesEl.EnumerateArray().ToList();

            // itemsWp 菜单通常比 API 更全：以菜单为准重建列表（保留已匹配场景上的瓦片观测）
            if (fromMenu && metaScenes.Count > 0)
            {
                var rebuilt = new List<PanoramaScene>();
                var used = new HashSet<PanoramaScene>();
                foreach (var item in metaScenes)
                {
                    var built = BuildSceneFromMetaItem(item, entries);
                    var matched = FindExisting(built, existingById, existingByResource);
                    if (matched is not null && used.Add(matched))
                    {
                        MergeInto(matched, built);
                        rebuilt.Add(matched);
                    }
                    else
                    {
                        rebuilt.Add(built);
                    }
                }

                if (rebuilt.Count > 0)
                {
                    manifest.Scenes = rebuilt;
                }

                return;
            }

            foreach (var item in metaScenes)
            {
                var built = BuildSceneFromMetaItem(item, entries);
                var scene = FindExisting(built, existingById, existingByResource);
                if (scene is not null)
                {
                    MergeInto(scene, built);
                }
                else
                {
                    manifest.Scenes.Add(built);
                    existingById[built.Id] = built;
                    var rid = Yun720ThumbUrl.ExtractResourceId(built.ThumbnailUrl)
                              ?? Yun720ThumbUrl.ExtractResourceId(built.TileBaseUrl);
                    if (!string.IsNullOrWhiteSpace(rid))
                    {
                        existingByResource[rid!] = built;
                    }
                }
            }

            ApplyImagesOnly(manifest, doc.RootElement);
        }
        catch (JsonException)
        {
            // ignore bad meta
        }
    }

    private static PanoramaScene BuildSceneFromMetaItem(JsonElement item, IReadOnlyList<CapturedNetworkEntry> entries)
    {
        var thumb = NormalizeUrl(GetString(item, "thumbUrl", "thumb", "cover", "coverUrl", "picUrl"));
        var path = GetString(item, "path", "imgPath", "tilePath", "panoPath", "resourcePath", "imgs", "texturePath");
        var name = GetString(item, "name", "title");
        var id = GetString(item, "id", "partnerId", "sceneId");
        var rid = Yun720ThumbUrl.ExtractResourceId(thumb)
                  ?? Yun720ThumbUrl.ExtractResourceId(path)
                  ?? Yun720ThumbUrl.ExtractResourceId(id);

        if (string.IsNullOrWhiteSpace(id) || id.StartsWith("menu_", StringComparison.OrdinalIgnoreCase))
        {
            id = rid ?? id ?? $"scene_{Guid.NewGuid():N}"[..12];
        }

        var tileBase = Yun720ConfigExtractor.NormalizeImgsBase(path)
                       ?? Yun720ThumbUrl.DeriveTileBase(thumb, entries)
                       ?? Yun720ThumbUrl.DeriveTileBase(path, entries);

        return new PanoramaScene
        {
            Id = id!,
            Name = string.IsNullOrWhiteSpace(name) ? (rid ?? id!) : name!,
            ThumbnailUrl = thumb,
            TileBaseUrl = tileBase
        };
    }

    private static PanoramaScene? FindExisting(
        PanoramaScene built,
        Dictionary<string, PanoramaScene> byId,
        Dictionary<string, PanoramaScene> byResource)
    {
        if (byId.TryGetValue(built.Id, out var byExactId))
        {
            return byExactId;
        }

        var rid = Yun720ThumbUrl.ExtractResourceId(built.ThumbnailUrl)
                  ?? Yun720ThumbUrl.ExtractResourceId(built.TileBaseUrl)
                  ?? Yun720ThumbUrl.ExtractResourceId(built.Id);
        if (!string.IsNullOrWhiteSpace(rid) && byResource.TryGetValue(rid!, out var byRid))
        {
            return byRid;
        }

        return null;
    }

    private static void MergeInto(PanoramaScene target, PanoramaScene source)
    {
        if (!string.IsNullOrWhiteSpace(source.Name) &&
            (string.IsNullOrWhiteSpace(target.Name) || target.Name == target.Id))
        {
            target.Name = source.Name;
        }

        if (!string.IsNullOrWhiteSpace(source.ThumbnailUrl))
        {
            // 菜单里的 http 缩略图优先于占位
            if (string.IsNullOrWhiteSpace(target.ThumbnailUrl) ||
                (!source.ThumbnailUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                 target.ThumbnailUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) ||
                source.ThumbnailUrl.Contains("/imgs/thumb", StringComparison.OrdinalIgnoreCase))
            {
                target.ThumbnailUrl = source.ThumbnailUrl;
            }
        }

        if (string.IsNullOrWhiteSpace(target.TileBaseUrl) && !string.IsNullOrWhiteSpace(source.TileBaseUrl))
        {
            target.TileBaseUrl = source.TileBaseUrl;
        }
    }

    private static void ApplyImagesOnly(PanoramaManifest manifest, JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var dataUrls = new List<string>();
        foreach (var img in images.EnumerateArray())
        {
            var data = GetString(img, "dataUrl");
            if (!string.IsNullOrWhiteSpace(data) &&
                data.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                dataUrls.Add(data);
                continue;
            }

            var src = NormalizeUrl(GetString(img, "src", "url"));
            if (!string.IsNullOrWhiteSpace(src))
            {
                dataUrls.Add(src!);
            }
        }

        for (var i = 0; i < manifest.Scenes.Count && i < dataUrls.Count; i++)
        {
            var scene = manifest.Scenes[i];
            var candidate = dataUrls[i];
            if (candidate.StartsWith("data:image", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(scene.ThumbnailUrl))
            {
                scene.ThumbnailUrl = candidate;
            }
        }
    }

    public static List<string> CollectThumbnailCandidates(IReadOnlyList<CapturedNetworkEntry> entries, string? pageMetaJson)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? url)
        {
            url = NormalizeUrl(url);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (!(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                  url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (seen.Add(url))
            {
                // dataURL 插到前面，优先使用
                if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                {
                    list.Insert(0, url);
                }
                else
                {
                    list.Add(url);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(pageMetaJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(pageMetaJson);
                if (doc.RootElement.TryGetProperty("images", out var images) &&
                    images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var img in images.EnumerateArray())
                    {
                        Add(GetString(img, "dataUrl"));
                        Add(GetString(img, "src", "url"));
                    }
                }

                if (doc.RootElement.TryGetProperty("scenes", out var scenes) &&
                    scenes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var scene in scenes.EnumerateArray())
                    {
                        Add(GetString(scene, "thumbUrl", "thumb", "cover", "coverUrl", "picUrl"));
                    }
                }
            }
            catch (JsonException)
            {
                // ignore
            }
        }

        foreach (var entry in entries)
        {
            if (IsLikelyThumbnailUrl(entry.Url) || IsLikelyCoverImageUrl(entry.Url))
            {
                Add(entry.Url);
            }

            if (string.IsNullOrWhiteSpace(entry.BodyText) || !LooksLikeJson(entry.BodyText))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(entry.BodyText);
                CollectUrlsFromJson(doc.RootElement, Add, 0);
            }
            catch (JsonException)
            {
                // ignore
            }
        }

        return list;
    }

    private static void CollectUrlsFromJson(JsonElement element, Action<string?> add, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var name = prop.Name;
                    if (name.Contains("thumb", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("cover", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("picUrl", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("img", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("image", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("url", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var s = prop.Value.GetString();
                            if (LooksLikeImageUrl(s))
                            {
                                add(s);
                            }
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            add(GetString(prop.Value, "url", "src", "thumbUrl"));
                        }
                    }

                    CollectUrlsFromJson(prop.Value, add, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectUrlsFromJson(item, add, depth + 1);
                }

                break;
            case JsonValueKind.String:
            {
                var s = element.GetString();
                if (LooksLikeImageUrl(s) &&
                    (s!.Contains("thumb", StringComparison.OrdinalIgnoreCase) ||
                     s.Contains("cover", StringComparison.OrdinalIgnoreCase)))
                {
                    add(s);
                }

                break;
            }
        }
    }

    private static bool IsLikelyThumbnailUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var lower = url.ToLowerInvariant();
        return (lower.Contains("thumb") || lower.Contains("cover") || lower.Contains("preview") ||
                lower.Contains("snapshot") || lower.Contains("poster")) &&
               LooksLikeImageUrl(url);
    }

    private static bool IsLikelyCoverImageUrl(string? url)
    {
        if (!LooksLikeImageUrl(url))
        {
            return false;
        }

        var lower = url!.ToLowerInvariant();
        // 720static 常见封面/小图，排除明显瓦片路径
        if (lower.Contains("/tile") || Regex.IsMatch(lower, @"/[lfrbud]/\d+/\d+"))
        {
            return false;
        }

        return lower.Contains("720static") || lower.Contains("720yun") || lower.Contains("qiniu") ||
               lower.Contains("aliyuncs") || lower.Contains("myqcloud");
    }

    private static bool LooksLikeImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var lower = url.ToLowerInvariant();
        return lower.StartsWith("http") &&
               (lower.Contains(".jpg") || lower.Contains(".jpeg") || lower.Contains(".png") ||
                lower.Contains(".webp") || lower.Contains(".gif") || lower.Contains("format/jpg") ||
                lower.Contains("imageView") || lower.Contains("/image"));
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.StartsWith("{") || trimmed.StartsWith("[");
    }

    private static string? GetString(JsonElement element, params string[] names)
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

        // 大小写不敏感兜底
        foreach (var prop in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();
                }
            }
        }

        return null;
    }

    private static string? NormalizeImgsBase(string? value)
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

        var idx = value.IndexOf("/imgs", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value[..(idx + 5)].TrimEnd('/') + "/";
    }

    private static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim().Trim('"', '\'');
        if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        return url;
    }
}
