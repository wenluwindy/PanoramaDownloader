using System.Text.RegularExpressions;
using PanoramaDownloader.Core.Models;

namespace PanoramaDownloader.Core.Detectors;

/// <summary>
/// 从网络捕获推断 720 云瓦片结构。
/// <list type="bullet">
/// <item>立方体多分辨率：{base}{face}/l{level}/{row}/l{level}_{face}_{row}_{col}.jpg</item>
/// <item>平面矩阵多分辨率：{base}l{level}/{row}/l{level}_{row}_{col}.jpg</item>
/// </list>
/// </summary>
public static partial class Yun720TileInferencer
{
    private static readonly string[] Faces = ["f", "r", "b", "l", "u", "d"];

    /// <summary>常见完整网格尺寸（优先用于补全「只观察到部分瓦片」的情况）。</summary>
    private static readonly int[] CommonGridSizes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 16, 18, 20, 24, 32, 64];

    public static void EnrichScenes(PanoramaManifest manifest, IReadOnlyList<CapturedNetworkEntry> entries)
    {
        var matches = CollectMatches(entries);
        if (matches.Count == 0)
        {
            foreach (var scene in manifest.Scenes.Where(s =>
                         !string.IsNullOrWhiteSpace(s.TileBaseUrl) && s.Levels.Count == 0))
            {
                ApplyDefaultCubeLevels(scene);
            }

            return;
        }

        var groups = matches
            .GroupBy(m => NormalizeBase(m.BaseUrl), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        var assignedBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scene in manifest.Scenes)
        {
            var group = FindBestGroup(scene, groups, assignedBases);
            if (group is null)
            {
                continue;
            }

            ApplyGroupToScene(scene, group.ToList());
            assignedBases.Add(NormalizeBase(group.Key));
        }

        // 单场景且尚未绑定时，可用观测到的主瓦片组
        if (manifest.Scenes.Count == 1 &&
            string.IsNullOrWhiteSpace(manifest.Scenes[0].TileBaseUrl) &&
            groups.Count > 0)
        {
            ApplyGroupToScene(manifest.Scenes[0], groups[0].ToList());
        }

        // 已有配置根路径、但网络侧未观测到该场景瓦片：保留各自 TileBaseUrl，补默认层级
        foreach (var scene in manifest.Scenes.Where(s =>
                     !string.IsNullOrWhiteSpace(s.TileBaseUrl) && s.Levels.Count == 0))
        {
            ApplyDefaultCubeLevels(scene);
        }
    }

    private static string NormalizeBase(string baseUrl) =>
        (baseUrl ?? string.Empty).Trim().TrimEnd('/') + "/";


    private static List<TileMatch> CollectMatches(IReadOnlyList<CapturedNetworkEntry> entries)
    {
        var matches = new List<TileMatch>();
        foreach (var entry in entries)
        {
            var m = TryParseTileUrl(entry.Url);
            if (m is not null)
            {
                matches.Add(m);
            }
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.BodyText) || entry.BodyText.Length > 400_000)
            {
                continue;
            }

            foreach (Match hit in BodyTileUrlRegex().Matches(entry.BodyText))
            {
                var m = TryParseTileUrl(hit.Value);
                if (m is not null)
                {
                    matches.Add(m);
                }
            }

            foreach (Match hit in BodyMatrixTileUrlRegex().Matches(entry.BodyText))
            {
                var m = TryParseTileUrl(hit.Value);
                if (m is not null)
                {
                    matches.Add(m);
                }
            }
        }

        return matches;
    }

    private static void ApplyDefaultCubeLevels(PanoramaScene scene)
    {
        scene.Projection = "cube-multires";
        scene.Faces = Faces.ToList();
        scene.Levels =
        [
            new PanoramaLevel
            {
                Level = 1,
                Rows = 1,
                Cols = 1,
                IndexPad = 2,
                TileSize = 512,
                FaceSize = 512,
                UrlTemplate = "{face}/l{level}/{rowPadded}/l{level}_{face}_{rowPadded}_{colPadded}.jpg"
            },
            new PanoramaLevel
            {
                Level = 2,
                Rows = 3,
                Cols = 3,
                IndexPad = 2,
                TileSize = 512,
                FaceSize = 1536,
                UrlTemplate = "{face}/l{level}/{rowPadded}/l{level}_{face}_{rowPadded}_{colPadded}.jpg"
            },
            new PanoramaLevel
            {
                Level = 3,
                Rows = 5,
                Cols = 5,
                IndexPad = 2,
                TileSize = 512,
                FaceSize = 2560,
                UrlTemplate = "{face}/l{level}/{rowPadded}/l{level}_{face}_{rowPadded}_{colPadded}.jpg"
            }
        ];
    }

    private static IGrouping<string, TileMatch>? FindBestGroup(
        PanoramaScene scene,
        List<IGrouping<string, TileMatch>> groups,
        HashSet<string> assignedBases)
    {
        var available = groups
            .Where(g => !assignedBases.Contains(NormalizeBase(g.Key)))
            .ToList();
        if (available.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(scene.TileBaseUrl))
        {
            var want = NormalizeBase(scene.TileBaseUrl);
            var exact = available.FirstOrDefault(g =>
                string.Equals(NormalizeBase(g.Key), want, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            // 配置路径与观测路径互相包含（尾部 imgs/ 归一后）
            var soft = available.FirstOrDefault(g =>
            {
                var key = NormalizeBase(g.Key);
                return key.StartsWith(want, StringComparison.OrdinalIgnoreCase) ||
                       want.StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                       ShareResourceFolder(key, want);
            });
            if (soft is not null)
            {
                return soft;
            }
        }

        if (!string.IsNullOrWhiteSpace(scene.Id))
        {
            var byId = available.FirstOrDefault(g =>
                g.Key.Contains(scene.Id, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        // 用场景名中的数字/资源片段再试一次（弱匹配，仅未占用组）
        if (!string.IsNullOrWhiteSpace(scene.Name))
        {
            var token = ExtractResourceToken(scene.Id) ?? ExtractResourceToken(scene.Name);
            if (!string.IsNullOrWhiteSpace(token) && token.Length >= 4)
            {
                var byToken = available.FirstOrDefault(g =>
                    g.Key.Contains(token, StringComparison.OrdinalIgnoreCase));
                if (byToken is not null)
                {
                    return byToken;
                }
            }
        }

        return null;
    }

    private static bool ShareResourceFolder(string a, string b)
    {
        var ta = ExtractResourceTokenFromImgsPath(a);
        var tb = ExtractResourceTokenFromImgsPath(b);
        return !string.IsNullOrWhiteSpace(ta) &&
               string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从 .../{resourceId}/imgs/ 提取 resourceId。</summary>
    private static string? ExtractResourceTokenFromImgsPath(string url)
    {
        var m = ResourceFolderRegex().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ExtractResourceToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = Regex.Match(text, @"[A-Za-z0-9_-]{4,}");
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex(@"/([A-Za-z0-9_-]+)/imgs/?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ResourceFolderRegex();


    private static void ApplyGroupToScene(PanoramaScene scene, List<TileMatch> tiles)
    {
        var baseUrl = tiles[0].BaseUrl;
        scene.TileBaseUrl = baseUrl;

        var isMatrix = tiles.All(t => t.Kind == TileKind.Matrix);
        if (isMatrix)
        {
            scene.Projection = "matrix-multires";
            scene.Faces = [];
        }
        else
        {
            scene.Projection = "cube-multires";
            scene.Faces = Faces.ToList();
        }

        var levels = tiles.Select(t => t.Level).Distinct().OrderBy(x => x).ToList();
        var pad = tiles.Max(t => Math.Max(t.RowPad, t.ColPad));
        if (pad < 1)
        {
            pad = 2;
        }

        scene.Levels = levels.Select(level =>
        {
            var levelTiles = tiles.Where(t => t.Level == level).ToList();
            var maxRow = levelTiles.Count > 0 ? levelTiles.Max(t => t.Row) : 1;
            var maxCol = levelTiles.Count > 0 ? levelTiles.Max(t => t.Col) : 1;
            var rows = InferGridExtent(maxRow, level, isMatrix);
            var cols = isMatrix
                ? InferMatrixColExtent(maxCol, level)
                : InferGridExtent(maxCol, level, isMatrix: false);
            var gridRows = rows;
            var gridCols = isMatrix ? cols : Math.Max(rows, cols);
            if (!isMatrix)
            {
                gridRows = gridCols;
            }

            return new PanoramaLevel
            {
                Level = level,
                Rows = gridRows,
                Cols = gridCols,
                IndexPad = pad,
                TileSize = 512,
                FaceSize = isMatrix ? 0 : 512 * gridRows,
                UrlTemplate = isMatrix
                    ? "l{level}/{rowPadded}/l{level}_{rowPadded}_{colPadded}.jpg"
                    : "{face}/l{level}/{rowPadded}/l{level}_{face}_{rowPadded}_{colPadded}.jpg"
            };
        }).ToList();

        if (!isMatrix)
        {
            var maxLevel = levels.DefaultIfEmpty(1).Max();
            if (maxLevel < 3 && !scene.Levels.Any(l => l.Level == 3))
            {
                var template = scene.Levels.LastOrDefault();
                var grid = template is not null ? Math.Max(template.Rows, 5) : 5;
                var indexPad = template?.IndexPad ?? pad;
                scene.Levels.Add(new PanoramaLevel
                {
                    Level = 3,
                    Rows = grid,
                    Cols = grid,
                    IndexPad = indexPad,
                    TileSize = 512,
                    FaceSize = 512 * grid,
                    UrlTemplate = "{face}/l{level}/{rowPadded}/l{level}_{face}_{rowPadded}_{colPadded}.jpg"
                });
                scene.Levels = scene.Levels.OrderBy(l => l.Level).ToList();
            }
        }
    }

    /// <summary>
    /// 视口往往只加载部分瓦片：若只看到 1~4，按 720 云经典 l3=5×5 等常见尺寸向上补全。
    /// </summary>
    public static int InferGridExtent(int maxObserved, int level, bool isMatrix = false)
    {
        if (maxObserved < 1)
        {
            return level >= 2 ? 5 : 1;
        }

        if (!isMatrix)
        {
            if (level >= 3 && maxObserved < 5)
            {
                return 5;
            }

            if (level == 2 && maxObserved < 3)
            {
                return 3;
            }

            if (level <= 1)
            {
                return Math.Max(1, maxObserved);
            }
        }

        foreach (var size in CommonGridSizes)
        {
            if (size >= maxObserved)
            {
                return size;
            }
        }

        return maxObserved;
    }

    public static int InferMatrixColExtent(int maxObserved, int level)
    {
        if (maxObserved < 1)
        {
            return level >= 4 ? 16 : 8;
        }

        foreach (var size in CommonGridSizes)
        {
            if (size >= maxObserved)
            {
                return size;
            }
        }

        return maxObserved;
    }

    public static IReadOnlyList<TileRequest> BuildTileRequests(PanoramaScene scene, int? level = null)
    {
        if (string.IsNullOrWhiteSpace(scene.TileBaseUrl) || scene.Levels.Count == 0)
        {
            return [];
        }

        var target = level.HasValue
            ? scene.Levels.FirstOrDefault(l => l.Level == level.Value) ?? scene.Levels.OrderByDescending(l => l.Level).First()
            : scene.Levels.OrderByDescending(l => l.Level).First();

        return BuildTileRequestsForLevel(scene, target);
    }

    public static IReadOnlyList<TileRequest> BuildTileRequestsForLevel(PanoramaScene scene, PanoramaLevel target)
    {
        if (string.IsNullOrWhiteSpace(scene.TileBaseUrl))
        {
            return [];
        }

        var baseUrl = scene.TileBaseUrl!.TrimEnd('/') + "/";
        var list = new List<TileRequest>();
        var isMatrix = string.Equals(scene.Projection, "matrix-multires", StringComparison.OrdinalIgnoreCase) ||
                       target.UrlTemplate.Contains("{face}", StringComparison.Ordinal) == false;

        if (isMatrix)
        {
            for (var row = 1; row <= target.Rows; row++)
            {
                for (var col = 1; col <= target.Cols; col++)
                {
                    list.Add(CreateTileRequest(baseUrl, face: "", target, row, col));
                }
            }

            return list;
        }

        var faces = scene.Faces.Count > 0 ? scene.Faces : Faces.ToList();
        foreach (var face in faces)
        {
            for (var row = 1; row <= target.Rows; row++)
            {
                for (var col = 1; col <= target.Cols; col++)
                {
                    list.Add(CreateTileRequest(baseUrl, face, target, row, col));
                }
            }
        }

        return list;
    }

    /// <summary>生成一圈扩展瓦片（新增的行或列），用于探测更大网格。</summary>
    public static IReadOnlyList<TileRequest> BuildExpansionRing(
        PanoramaScene scene,
        PanoramaLevel level,
        int newRows,
        int newCols)
    {
        if (string.IsNullOrWhiteSpace(scene.TileBaseUrl))
        {
            return [];
        }

        var baseUrl = scene.TileBaseUrl!.TrimEnd('/') + "/";
        var isMatrix = string.Equals(scene.Projection, "matrix-multires", StringComparison.OrdinalIgnoreCase);
        var faces = isMatrix
            ? [""]
            : (scene.Faces.Count > 0 ? scene.Faces : Faces.ToList());
        var list = new List<TileRequest>();
        var oldRows = level.Rows;
        var oldCols = level.Cols;

        foreach (var face in faces)
        {
            for (var row = 1; row <= newRows; row++)
            {
                for (var col = 1; col <= newCols; col++)
                {
                    if (row <= oldRows && col <= oldCols)
                    {
                        continue;
                    }

                    list.Add(CreateTileRequest(baseUrl, face, level, row, col));
                }
            }
        }

        return list;
    }

    public static TileRequest CreateTileRequest(
        string baseUrl,
        string face,
        PanoramaLevel level,
        int row,
        int col)
    {
        var rowP = row.ToString().PadLeft(level.IndexPad, '0');
        var colP = col.ToString().PadLeft(level.IndexPad, '0');
        var relative = level.UrlTemplate
            .Replace("{face}", face, StringComparison.Ordinal)
            .Replace("{level}", level.Level.ToString(), StringComparison.Ordinal)
            .Replace("{rowPadded}", rowP, StringComparison.Ordinal)
            .Replace("{colPadded}", colP, StringComparison.Ordinal)
            .Replace("{row}", row.ToString(), StringComparison.Ordinal)
            .Replace("{col}", col.ToString(), StringComparison.Ordinal);

        var fileName = string.IsNullOrEmpty(face) ? $"{rowP}_{colP}.jpg" : $"{rowP}_{colP}.jpg";
        var relativePath = string.IsNullOrEmpty(face)
            ? Path.Combine($"l{level.Level}", fileName)
            : Path.Combine($"l{level.Level}", face, fileName);

        return new TileRequest
        {
            Url = baseUrl.TrimEnd('/') + "/" + relative.TrimStart('/'),
            RelativePath = relativePath,
            Face = face,
            Level = level.Level,
            Row = row,
            Col = col
        };
    }

    public static TileMatch? TryParseTileUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var bare = StripQuery(url);

        var classic = ClassicTileRegex().Match(bare);
        if (classic.Success)
        {
            var rowText = classic.Groups["row2"].Success && classic.Groups["row2"].Length > 0
                ? classic.Groups["row2"].Value
                : classic.Groups["row"].Value;
            var colText = classic.Groups["col"].Value;
            return new TileMatch
            {
                Kind = TileKind.Cube,
                BaseUrl = classic.Groups["base"].Value.TrimEnd('/') + "/",
                Face = classic.Groups["face"].Value.ToLowerInvariant(),
                Level = int.Parse(classic.Groups["level"].Value),
                Row = int.Parse(rowText),
                Col = int.Parse(colText),
                RowPad = rowText.Length,
                ColPad = colText.Length,
                Extension = classic.Groups["ext"].Value
            };
        }

        var matrix = MatrixTileRegex().Match(bare);
        if (matrix.Success)
        {
            var rowText = matrix.Groups["row2"].Success && matrix.Groups["row2"].Length > 0
                ? matrix.Groups["row2"].Value
                : matrix.Groups["row"].Value;
            var colText = matrix.Groups["col"].Value;
            return new TileMatch
            {
                Kind = TileKind.Matrix,
                BaseUrl = matrix.Groups["base"].Value.TrimEnd('/') + "/",
                Face = string.Empty,
                Level = int.Parse(matrix.Groups["level"].Value),
                Row = int.Parse(rowText),
                Col = int.Parse(colText),
                RowPad = rowText.Length,
                ColPad = colText.Length,
                Extension = matrix.Groups["ext"].Value
            };
        }

        return null;
    }

    private static string StripQuery(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        return q >= 0 ? url[..q] : url;
    }

    // .../imgs/f/l3/01/l3_f_01_02.jpg
    [GeneratedRegex(
        @"(?<base>https?://.+)/(?<face>[lfrbud])/l(?<level>\d+)/(?<row>\d+)/l\k<level>_\k<face>_(?<row2>\d+)_(?<col>\d+)\.(?<ext>jpg|jpeg|png|webp)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ClassicTileRegex();

    // .../imgs/l6/18/l6_18_64.jpg （平面矩阵，无 face）
    [GeneratedRegex(
        @"(?<base>https?://.+?)/l(?<level>\d+)/(?<row>\d+)/l\k<level>_(?<row2>\d+)_(?<col>\d+)\.(?<ext>jpg|jpeg|png|webp)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MatrixTileRegex();

    [GeneratedRegex(
        @"https?://[^\s""'<>]+/[lfrbud]/l\d+/\d+/l\d+_[lfrbud]_\d+_\d+\.(?:jpg|jpeg|png|webp)(?:\?[^\s""'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BodyTileUrlRegex();

    [GeneratedRegex(
        @"https?://[^\s""'<>]+/imgs/l\d+/\d+/l\d+_\d+_\d+\.(?:jpg|jpeg|png|webp)(?:\?[^\s""'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BodyMatrixTileUrlRegex();

    public enum TileKind
    {
        Cube,
        Matrix
    }

    public sealed class TileMatch
    {
        public TileKind Kind { get; set; } = TileKind.Cube;
        public string BaseUrl { get; set; } = string.Empty;
        public string Face { get; set; } = string.Empty;
        public int Level { get; set; }
        public int Row { get; set; }
        public int Col { get; set; }
        public int RowPad { get; set; }
        public int ColPad { get; set; }
        public string Extension { get; set; } = "jpg";
    }
}
