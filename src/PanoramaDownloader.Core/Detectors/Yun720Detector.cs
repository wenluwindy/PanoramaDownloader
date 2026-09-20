using System.Text.Json;
using System.Text.RegularExpressions;
using PanoramaDownloader.Core.Models;
using PanoramaDownloader.Core.Services;

namespace PanoramaDownloader.Core.Detectors;

/// <summary>
/// 720 云检测器：网络捕获 + API/HTML 配置解析 → <see cref="PanoramaManifest"/>。
/// 支持立方体多分辨率与平面矩阵瓦片 URL。
/// </summary>
public sealed partial class Yun720Detector : IPanoramaDetector
{
    public string Name => "yun720";

    public bool CanHandle(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("720yun", StringComparison.OrdinalIgnoreCase);
    }

    public DetectionResult Detect(string sourceUrl, IReadOnlyList<CapturedNetworkEntry> entries, string? pageHtml)
    {
        if (!CanHandle(sourceUrl))
        {
            return new DetectionResult
            {
                Success = false,
                Message = "当前仅支持 720 云作品链接（域名需包含 720yun）。"
            };
        }

        // 原则：能看到全景资源就直接用；只有作品本身无权访问时才要求登录。
        var hasPanoramaSignal = HasUsablePanoramaSignal(entries, pageHtml);
        if (!hasPanoramaSignal && RequiresLogin(sourceUrl, entries))
        {
            return new DetectionResult
            {
                Success = false,
                NeedLogin = true,
                Message = "该作品似乎需要登录后才能查看。若浏览器里也打不开，请点击「登录 720 云」后再分析。"
            };
        }

        var manifest = new PanoramaManifest
        {
            SourceUrl = sourceUrl,
            Detector = Name,
            Title = ExtractTitle(pageHtml) ?? "720 云作品",
            HeadersHint =
            {
                ["Referer"] = "https://www.720yun.com/"
            },
            DetectedAt = DateTimeOffset.Now
        };

        var scenes = ExtractScenesFromEntries(entries);
        if (scenes.Count == 0)
        {
            scenes.AddRange(ExtractScenesFromHtml(pageHtml));
        }

        if (scenes.Count == 0)
        {
            var workId = ExtractWorkId(sourceUrl);
            scenes.Add(new PanoramaScene
            {
                Id = workId ?? "scene_unknown",
                Name = manifest.Title,
                ThumbnailUrl = FindLikelyThumbnail(entries, pageHtml)
            });
        }

        manifest.Scenes = scenes;
        Yun720ConfigExtractor.EnrichManifest(manifest, entries, pageHtml);
        SceneThumbnailEnricher.ApplyPageMeta(manifest, pageMetaJson: null, entries);
        Yun720TileInferencer.EnrichScenes(manifest, entries);
        PruneNoiseScenes(manifest);

        var tileHintCount = entries.Count(e => Yun720TileInferencer.TryParseTileUrl(e.Url) is not null);
        var thumbCount = manifest.Scenes.Count(s => !string.IsNullOrWhiteSpace(s.ThumbnailUrl));
        var withTiles = manifest.Scenes.Count(s => !string.IsNullOrWhiteSpace(s.TileBaseUrl));
        var projection = manifest.Scenes.FirstOrDefault(s => s.Levels.Count > 0)?.Projection ?? "unknown";
        var gridHint = manifest.Scenes
            .Where(s => s.Levels.Count > 0)
            .Select(s => s.Levels.OrderByDescending(l => l.Level).First())
            .Select(l => $"l{l.Level} {l.Rows}×{l.Cols}")
            .FirstOrDefault();
        var message = withTiles > 0
            ? $"已识别 {manifest.Scenes.Count} 个场景（缩略图 {thumbCount}，瓦片 {withTiles}，{projection}{(gridHint is null ? "" : "，" + gridHint)}），瓦片请求 {tileHintCount} 条。"
            : tileHintCount > 0
                ? $"已识别 {manifest.Scenes.Count} 个场景（缩略图 {thumbCount}），捕获到约 {tileHintCount} 条疑似瓦片请求但未能归一化结构。"
                : $"已识别 {manifest.Scenes.Count} 个场景（缩略图 {thumbCount}）。尚未捕获到瓦片请求，请旋转/放大全景画面后再分析。";

        return new DetectionResult
        {
            Success = true,
            Manifest = manifest,
            Message = message
        };
    }

    /// <summary>
    /// 在 Detect 之后用页面脚本元数据再次补全缩略图/场景。
    /// </summary>
    public static void EnrichWithPageMeta(PanoramaManifest manifest, string? pageMetaJson, IReadOnlyList<CapturedNetworkEntry> entries)
    {
        SceneThumbnailEnricher.ApplyPageMeta(manifest, pageMetaJson, entries);
        // 页面元数据里的 path 可能刚补上，再跑一遍配置合并与瓦片推断
        Yun720ConfigExtractor.EnrichManifest(manifest, entries, pageHtml: null);
        Yun720TileInferencer.EnrichScenes(manifest, entries);
        PruneNoiseScenes(manifest);
    }

    /// <summary>
    /// 去掉明显噪声场景（无缩略图、无瓦片、名字像随机 id 且数量过多时保留有瓦片的）。
    /// </summary>
    private static void PruneNoiseScenes(PanoramaManifest manifest)
    {
        if (manifest.Scenes.Count <= 1)
        {
            return;
        }

        var useful = manifest.Scenes
            .Where(s =>
                !string.IsNullOrWhiteSpace(s.TileBaseUrl) ||
                !string.IsNullOrWhiteSpace(s.ThumbnailUrl) ||
                s.Levels.Count > 0 ||
                (!string.IsNullOrWhiteSpace(s.Name) && s.Name != s.Id))
            .ToList();

        if (useful.Count > 0 && useful.Count < manifest.Scenes.Count)
        {
            manifest.Scenes = useful;
        }
    }

    /// <summary>
    /// 已捕获到可用来源：瓦片、缩略图、成功的作品/场景 JSON，或页面标题。
    /// </summary>
    private static bool HasUsablePanoramaSignal(IReadOnlyList<CapturedNetworkEntry> entries, string? pageHtml)
    {
        if (entries.Any(e =>
                (IsLikelyTileUrl(e.Url) || IsLikelyThumbnailUrl(e.Url) || IsLikelyPanoramaAssetUrl(e.Url)) &&
                e.StatusCode is null or >= 200 and < 400))
        {
            return true;
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.BodyText) || !LooksLikeJson(entry.BodyText))
            {
                continue;
            }

            if (entry.StatusCode is >= 400)
            {
                continue;
            }

            if (IsLikelyWorkOrSceneApi(entry.Url) ||
                entry.BodyText.Contains("scene", StringComparison.OrdinalIgnoreCase) ||
                entry.BodyText.Contains("pano", StringComparison.OrdinalIgnoreCase) ||
                entry.BodyText.Contains("tile", StringComparison.OrdinalIgnoreCase))
            {
                // 排除明确失败的业务码
                if (IsAuthFailureBody(entry.BodyText))
                {
                    continue;
                }

                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(ExtractTitle(pageHtml)) &&
            !string.IsNullOrWhiteSpace(pageHtml) &&
            (pageHtml.Contains("pano", StringComparison.OrdinalIgnoreCase) ||
             pageHtml.Contains("krpano", StringComparison.OrdinalIgnoreCase) ||
             pageHtml.Contains("720yun", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return FindLikelyThumbnail(entries, pageHtml) is not null;
    }

    /// <summary>
    /// 仅在「没有可用全景信号」且「作品访问被明确拒绝 / 落在登录 URL」时要求登录。
    /// 页面顶栏的「请登录」、用户中心 401 等噪声一律忽略。
    /// </summary>
    private static bool RequiresLogin(string sourceUrl, IReadOnlyList<CapturedNetworkEntry> entries)
    {
        if (IsLoginUrl(sourceUrl))
        {
            return true;
        }

        return entries.Any(IsWorkAccessDenied);
    }

    private static bool IsWorkAccessDenied(CapturedNetworkEntry entry)
    {
        if (!IsLikelyWorkOrSceneApi(entry.Url) && !IsLikelyTileUrl(entry.Url))
        {
            return false;
        }

        if (entry.StatusCode is 401 or 403)
        {
            return true;
        }

        return !string.IsNullOrEmpty(entry.BodyText) && IsAuthFailureBody(entry.BodyText);
    }

    private static bool IsAuthFailureBody(string body)
    {
        return body.Contains("\"code\":401", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("\"code\": 401", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("\"code\":403", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("\"code\": 403", StringComparison.OrdinalIgnoreCase) ||
               (body.Contains("未登录", StringComparison.Ordinal) &&
                (body.Contains("work", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("pano", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("scene", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("无权", StringComparison.Ordinal) ||
                 body.Contains("权限", StringComparison.Ordinal)));
    }

    private static bool IsLoginUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("passport.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyWorkOrSceneApi(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var lower = url.ToLowerInvariant();
        // 排除用户/账号类接口，避免“未登录用户中心”误伤公开作品
        if (lower.Contains("/user") || lower.Contains("/member") || lower.Contains("/account") ||
            lower.Contains("/auth") || lower.Contains("/passport") || lower.Contains("/login"))
        {
            return false;
        }

        return lower.Contains("/work") ||
               lower.Contains("/pano") ||
               lower.Contains("/scene") ||
               lower.Contains("/tourpano") ||
               lower.Contains("/tour") ||
               lower.Contains("/product");
    }

    private static bool IsLikelyPanoramaAssetUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var lower = url.ToLowerInvariant();
        return lower.Contains(".jpg") || lower.Contains(".jpeg") || lower.Contains(".png") || lower.Contains(".webp")
               ? lower.Contains("pano") || lower.Contains("cube") || lower.Contains("/l/") || lower.Contains("/f/") ||
                 lower.Contains("multires") || lower.Contains("sphere")
               : false;
    }

    private static List<PanoramaScene> ExtractScenesFromEntries(IReadOnlyList<CapturedNetworkEntry> entries)
    {
        var scenes = new List<PanoramaScene>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.BodyText))
            {
                continue;
            }

            if (!LooksLikeJson(entry.BodyText))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(entry.BodyText);
                CollectScenesFromJson(doc.RootElement, scenes, seen);
            }
            catch (JsonException)
            {
                // ignore non-json bodies
            }
        }

        return scenes;
    }

    private static void CollectScenesFromJson(JsonElement element, List<PanoramaScene> scenes, HashSet<string> seen)
        => CollectScenesFromJson(element, scenes, seen, inScenesArray: false, depth: 0);

    private static void CollectScenesFromJson(
        JsonElement element,
        List<PanoramaScene> scenes,
        HashSet<string> seen,
        bool inScenesArray,
        int depth)
    {
        if (depth > 12)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var id = TryGetString(element, "id", "sceneId", "sid", "panoId", "partnerId");
                var name = TryGetString(element, "name", "sceneName", "title", "panoName");
                var thumb = TryGetString(element, "thumb", "thumbnail", "thumbUrl", "cover", "coverUrl", "picUrl", "img", "image")
                            ?? TryGetNestedUrl(element, "thumb", "thumbnail", "cover", "image");
                var path = TryGetString(element, "path", "imgPath", "tilePath", "panoPath", "resourcePath");

                var looksLikeScene = inScenesArray ||
                                     !string.IsNullOrWhiteSpace(thumb) ||
                                     (!string.IsNullOrWhiteSpace(path) &&
                                      path.Contains("imgs", StringComparison.OrdinalIgnoreCase)) ||
                                     (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id) &&
                                      (element.TryGetProperty("thumb", out _) ||
                                       element.TryGetProperty("thumbUrl", out _) ||
                                       element.TryGetProperty("cover", out _) ||
                                       element.TryGetProperty("path", out _) ||
                                       element.TryGetProperty("imgPath", out _)));

                if (looksLikeScene && (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(name)))
                {
                    var sceneId = string.IsNullOrWhiteSpace(id) ? $"scene_{scenes.Count + 1}" : id!;
                    if (seen.Add(sceneId))
                    {
                        scenes.Add(new PanoramaScene
                        {
                            Id = sceneId,
                            Name = string.IsNullOrWhiteSpace(name) ? sceneId : name!,
                            ThumbnailUrl = NormalizeUrl(thumb),
                            TileBaseUrl = Yun720ConfigExtractor.NormalizeImgsBase(path)
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(thumb) || !string.IsNullOrWhiteSpace(path))
                    {
                        var existing = scenes.FirstOrDefault(s => s.Id.Equals(sceneId, StringComparison.OrdinalIgnoreCase));
                        if (existing is not null)
                        {
                            if (string.IsNullOrWhiteSpace(existing.ThumbnailUrl))
                            {
                                existing.ThumbnailUrl = NormalizeUrl(thumb);
                            }

                            if (string.IsNullOrWhiteSpace(existing.TileBaseUrl))
                            {
                                existing.TileBaseUrl = Yun720ConfigExtractor.NormalizeImgsBase(path);
                            }
                        }
                    }
                }

                foreach (var prop in element.EnumerateObject())
                {
                    var childInScenes = inScenesArray ||
                                        prop.Name.Contains("scene", StringComparison.OrdinalIgnoreCase) ||
                                        prop.Name.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                                        prop.Name.Equals("panos", StringComparison.OrdinalIgnoreCase) ||
                                        prop.Name.Equals("panoList", StringComparison.OrdinalIgnoreCase) ||
                                        prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase);
                    CollectScenesFromJson(prop.Value, scenes, seen, childInScenes, depth + 1);
                }

                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectScenesFromJson(item, scenes, seen, inScenesArray, depth + 1);
                }

                break;
        }
    }

    private static List<PanoramaScene> ExtractScenesFromHtml(string? pageHtml)
    {
        var scenes = new List<PanoramaScene>();
        if (string.IsNullOrWhiteSpace(pageHtml))
        {
            return scenes;
        }

        var thumb = FindLikelyThumbnail([], pageHtml);
        var title = ExtractTitle(pageHtml);
        if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(thumb))
        {
            scenes.Add(new PanoramaScene
            {
                Id = "scene_html",
                Name = title ?? "场景",
                ThumbnailUrl = thumb
            });
        }

        return scenes;
    }

    private static string? ExtractTitle(string? pageHtml)
    {
        if (string.IsNullOrWhiteSpace(pageHtml))
        {
            return null;
        }

        var match = TitleRegex().Match(pageHtml);
        if (!match.Success)
        {
            return null;
        }

        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
    }

    private static string? ExtractWorkId(string sourceUrl)
    {
        var match = WorkIdRegex().Match(sourceUrl);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? FindLikelyThumbnail(IReadOnlyList<CapturedNetworkEntry> entries, string? pageHtml)
    {
        foreach (var entry in entries)
        {
            if (IsLikelyThumbnailUrl(entry.Url))
            {
                return entry.Url;
            }
        }

        if (string.IsNullOrWhiteSpace(pageHtml))
        {
            return null;
        }

        var og = OgImageRegex().Match(pageHtml);
        if (!og.Success)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(og.Groups[1].Value) ? og.Groups[2].Value : og.Groups[1].Value;
    }

    private static bool IsLikelyTileUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("/tile", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("tiles", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(url, @"/[lfrbud]/[lfrbud\d/_-]+\.(jpg|jpeg|png|webp)", RegexOptions.IgnoreCase);
    }

    private static bool IsLikelyThumbnailUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var lower = url.ToLowerInvariant();
        return (lower.Contains("thumb") || lower.Contains("cover") || lower.Contains("preview")) &&
               (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".png") || lower.EndsWith(".webp") || lower.Contains(".jpg?"));
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        return trimmed.StartsWith("{") || trimmed.StartsWith("[");
    }

    private static string? TryGetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }

        foreach (var prop in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    return prop.Value.GetString();
                }
            }
        }

        return null;
    }

    private static string? TryGetNestedUrl(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = TryGetString(prop, "url", "src", "thumbUrl", "link");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
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

    [GeneratedRegex(@"<title>\s*(.*?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"720yun\.com/(?:t|vr)/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkIdRegex();

    [GeneratedRegex(@"property=[""']og:image[""']\s+content=[""']([^""']+)[""']|content=[""']([^""']+)[""']\s+property=[""']og:image[""']", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageRegex();
}
