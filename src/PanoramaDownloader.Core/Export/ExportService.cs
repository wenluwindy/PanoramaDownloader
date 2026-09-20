using System.Text.Json;
using System.Text.RegularExpressions;
using PanoramaDownloader.Core.Detectors;
using PanoramaDownloader.Core.Downloader;
using PanoramaDownloader.Core.Models;
using PanoramaDownloader.Core.Stitcher;

namespace PanoramaDownloader.Core.Export;

public sealed class ExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly TileDownloader _downloader = new();

    public async Task<ExportResult> ExportSceneAsync(
        PanoramaManifest manifest,
        PanoramaScene scene,
        string rootDirectory,
        ExportMode mode,
        DownloadOptions downloadOptions,
        int jpegQuality = 92,
        int maxEquirectWidth = 4096)
    {
        if (string.IsNullOrWhiteSpace(scene.TileBaseUrl) || scene.Levels.Count == 0)
        {
            return new ExportResult
            {
                Success = false,
                Message = "当前场景尚未解析到瓦片结构。请打开作品并等待全景加载后再分析。"
            };
        }

        var level = scene.Levels.OrderByDescending(l => l.Level).First();
        var isMatrix = IsMatrixProjection(scene);
        var workDir = Path.Combine(
            rootDirectory,
            Sanitize(manifest.Title),
            Sanitize(scene.Name) + "_" + Sanitize(scene.Id));
        var tilesDir = Path.Combine(workDir, "tiles");
        Directory.CreateDirectory(tilesDir);

        // 1) 按推断网格下载
        var tiles = Yun720TileInferencer.BuildTileRequestsForLevel(scene, level).ToList();
        if (tiles.Count == 0)
        {
            return new ExportResult
            {
                Success = false,
                Message = "未能生成瓦片下载列表。"
            };
        }

        ReportPhase(downloadOptions, "download", $"准备下载 {tiles.Count} 个瓦片…", 0, tiles.Count, 0);
        var download = await _downloader.DownloadAsync(tiles, tilesDir, downloadOptions).ConfigureAwait(false);
        var ok = download.Ok;
        var fail = download.Fail;
        var failedTiles = download.FailedTiles.ToList();

        // 2) 向外探测更大网格（视口常漏掉最后几行/列）
        ReportPhase(downloadOptions, "probe", "探测更大网格…", ok + fail, Math.Max(ok + fail, 1), fail);
        var expanded = await ExpandGridByProbingAsync(scene, level, tilesDir, downloadOptions, isMatrix)
            .ConfigureAwait(false);
        ok += expanded.ok;
        fail += expanded.fail;
        failedTiles.AddRange(expanded.failed);

        // 3) 按实际落盘文件收缩有效行列，避免空边
        int effRows;
        int effCols;
        if (isMatrix)
        {
            (effRows, effCols) = MatrixTileStitcher.MeasureDownloadedGrid(
                tilesDir, level.Level, level.IndexPad, level.Rows, level.Cols);
            level.Rows = effRows;
            level.Cols = effCols;
        }
        else
        {
            (effRows, effCols) = CubeFaceStitcher.MeasureDownloadedGrid(
                tilesDir,
                level.Level,
                scene.Faces,
                level.IndexPad,
                level.Rows,
                level.Cols);
            var grid = Math.Max(effRows, effCols);
            level.Rows = grid;
            level.Cols = grid;
            level.FaceSize = (level.TileSize > 0 ? level.TileSize : 512) * grid;
        }

        var tileCountEstimate = isMatrix
            ? level.Rows * level.Cols
            : 6 * level.Rows * level.Cols;

        // 写 manifest
        var sceneManifestPath = Path.Combine(workDir, "manifest.json");
        await File.WriteAllTextAsync(
            sceneManifestPath,
            JsonSerializer.Serialize(new
            {
                manifest.SourceUrl,
                manifest.Title,
                scene.Id,
                scene.Name,
                scene.Projection,
                scene.TileBaseUrl,
                Level = level,
                EffectiveRows = effRows,
                EffectiveCols = effCols,
                TileCount = tileCountEstimate,
                Downloaded = ok,
                Failed = fail,
                FailedUrls = failedTiles.Select(t => t.Url).Distinct().Take(50).ToList(),
                ExportedAt = DateTimeOffset.Now
            }, JsonOptions)).ConfigureAwait(false);

        string? equirectPath = null;
        Dictionary<string, string>? faceMap = null;
        var eqW = Math.Clamp(maxEquirectWidth, 1024, 16384);
        string? warning = null;
        var needsPreview = false;

        if (mode is ExportMode.Equirect or ExportMode.Both)
        {
            ReportPhase(downloadOptions, "stitch", "拼接图像…", ok + fail, Math.Max(ok + fail, 1), fail);
            downloadOptions.CancellationToken.ThrowIfCancellationRequested();

            if (isMatrix)
            {
                equirectPath = Path.Combine(workDir, "equirect.jpg");
                await Task.Run(() => MatrixTileStitcher.Stitch(
                    tilesDir,
                    level.Level,
                    level.Rows,
                    level.Cols,
                    level.IndexPad,
                    equirectPath,
                    jpegQuality,
                    eqW), downloadOptions.CancellationToken).ConfigureAwait(false);

                if (File.Exists(equirectPath))
                {
                    using var probe = SkiaSharp.SKBitmap.Decode(equirectPath);
                    if (probe is not null)
                    {
                        eqW = probe.Width;
                    }
                }
            }
            else
            {
                var facesDir = Path.Combine(workDir, "faces");
                faceMap = await Task.Run(() => CubeFaceStitcher.StitchAllFaces(
                    tilesDir,
                    level.Level,
                    level.Rows,
                    level.Cols,
                    level.IndexPad,
                    facesDir,
                    scene.Faces), downloadOptions.CancellationToken).ConfigureAwait(false);

                var faceW = level.FaceSize > 0 ? level.FaceSize : 2048;
                if (faceMap.Count > 0)
                {
                    using var probe = SkiaSharp.SKBitmap.Decode(faceMap.Values.First());
                    if (probe is not null)
                    {
                        faceW = probe.Width;
                    }
                }

                var cap = Math.Clamp(maxEquirectWidth, 1024, 16384);
                eqW = Math.Min(Math.Max(faceW * 4, 2048), cap);
                equirectPath = Path.Combine(workDir, "equirect.jpg");
                needsPreview = faceMap.Count > 0;
            }

            if (eqW > 8192)
            {
                warning = $"整图建议宽度 {eqW}px，体积与内存占用较大，可在设置中降低 MaxEquirectWidth。";
            }
        }

        var gridLabel = $"{level.Rows}×{level.Cols}";
        var message = mode switch
        {
            ExportMode.Tiles => $"瓦片导出完成：成功 {ok}，失败 {fail}，网格 {gridLabel} → {workDir}",
            ExportMode.Equirect when isMatrix =>
                $"矩阵整图已保存（{gridLabel}）：成功 {ok}，失败 {fail} → {equirectPath}",
            ExportMode.Equirect =>
                $"六面已生成（{gridLabel}），请在预览中调整贴图后保存整图。",
            ExportMode.Both when isMatrix =>
                $"瓦片 {ok} 已保存（{gridLabel}）；整图已写入 equirect.jpg。",
            _ => $"瓦片 {ok} 已保存（网格 {gridLabel}）；请在预览中调整并保存整图。"
        };

        if (fail > 0)
        {
            message += " 失败项可再次导出（已成功瓦片会跳过）。";
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            message += " " + warning;
        }

        return new ExportResult
        {
            Success = ok > 0,
            OutputDirectory = workDir,
            EquirectPath = equirectPath,
            FacePaths = faceMap,
            NeedsEquirectPreview = needsPreview,
            SuggestedEquirectWidth = eqW,
            DownloadedTiles = ok,
            FailedTiles = fail,
            FailedTileUrls = failedTiles.Select(t => t.Url).Distinct().ToList(),
            Warning = warning,
            Message = message
        };
    }

    /// <summary>
    /// 探测更大网格：立方体按正方形扩展；矩阵行列可独立扩展。最多扩到 16。
    /// </summary>
    private async Task<(int ok, int fail, List<TileRequest> failed)> ExpandGridByProbingAsync(
        PanoramaScene scene,
        PanoramaLevel level,
        string tilesDir,
        DownloadOptions options,
        bool isMatrix)
    {
        if (isMatrix)
        {
            return await ExpandMatrixGridAsync(scene, level, tilesDir, options).ConfigureAwait(false);
        }

        return await ExpandCubeGridAsync(scene, level, tilesDir, options).ConfigureAwait(false);
    }

    private async Task<(int ok, int fail, List<TileRequest> failed)> ExpandCubeGridAsync(
        PanoramaScene scene,
        PanoramaLevel level,
        string tilesDir,
        DownloadOptions options)
    {
        var ok = 0;
        var fail = 0;
        var failed = new List<TileRequest>();
        const int maxGrid = 16;
        var baseUrl = scene.TileBaseUrl!.TrimEnd('/') + "/";

        while (level.Rows < maxGrid && level.Cols < maxGrid)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var next = Math.Max(level.Rows, level.Cols) + 1;
            var probeA = Yun720TileInferencer.CreateTileRequest(baseUrl, "f", level, next, 1);
            var probeB = Yun720TileInferencer.CreateTileRequest(baseUrl, "f", level, 1, next);
            var hitA = await _downloader.TryDownloadOneAsync(probeA, tilesDir, options).ConfigureAwait(false);
            var hitB = await _downloader.TryDownloadOneAsync(probeB, tilesDir, options).ConfigureAwait(false);
            if (hitA)
            {
                ok++;
            }

            if (hitB)
            {
                ok++;
            }

            if (!hitA && !hitB)
            {
                break;
            }

            var ring = Yun720TileInferencer.BuildExpansionRing(scene, level, next, next);
            if (ring.Count > 0)
            {
                var result = await _downloader.DownloadAsync(ring, tilesDir, options).ConfigureAwait(false);
                ok += result.Ok;
                fail += result.Fail;
                failed.AddRange(result.FailedTiles);
            }

            level.Rows = next;
            level.Cols = next;
            level.FaceSize = (level.TileSize > 0 ? level.TileSize : 512) * next;
        }

        return (ok, fail, failed);
    }

    private async Task<(int ok, int fail, List<TileRequest> failed)> ExpandMatrixGridAsync(
        PanoramaScene scene,
        PanoramaLevel level,
        string tilesDir,
        DownloadOptions options)
    {
        var ok = 0;
        var fail = 0;
        var failed = new List<TileRequest>();
        const int maxGrid = 16;
        var baseUrl = scene.TileBaseUrl!.TrimEnd('/') + "/";

        while (level.Rows < maxGrid || level.Cols < maxGrid)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var oldRows = level.Rows;
            var oldCols = level.Cols;
            var newRows = oldRows;
            var newCols = oldCols;

            if (oldRows < maxGrid)
            {
                var probe = Yun720TileInferencer.CreateTileRequest(baseUrl, "", level, oldRows + 1, 1);
                if (await _downloader.TryDownloadOneAsync(probe, tilesDir, options).ConfigureAwait(false))
                {
                    ok++;
                    newRows = oldRows + 1;
                }
            }

            if (oldCols < maxGrid)
            {
                var probe = Yun720TileInferencer.CreateTileRequest(baseUrl, "", level, 1, oldCols + 1);
                if (await _downloader.TryDownloadOneAsync(probe, tilesDir, options).ConfigureAwait(false))
                {
                    ok++;
                    newCols = oldCols + 1;
                }
            }

            if (newRows == oldRows && newCols == oldCols)
            {
                break;
            }

            // BuildExpansionRing 以 level 当前行列为「旧尺寸」
            var ring = Yun720TileInferencer.BuildExpansionRing(scene, level, newRows, newCols);
            if (ring.Count > 0)
            {
                var result = await _downloader.DownloadAsync(ring, tilesDir, options).ConfigureAwait(false);
                ok += result.Ok;
                fail += result.Fail;
                failed.AddRange(result.FailedTiles);
            }

            level.Rows = newRows;
            level.Cols = newCols;
        }

        return (ok, fail, failed);
    }

    private static bool IsMatrixProjection(PanoramaScene scene) =>
        string.Equals(scene.Projection, "matrix-multires", StringComparison.OrdinalIgnoreCase) ||
        scene.Faces.Count == 0;

    private static void ReportPhase(
        DownloadOptions options,
        string phase,
        string status,
        int completed,
        int total,
        int failed)
    {
        options.Progress?.Report(new DownloadProgress
        {
            Phase = phase,
            StatusText = status,
            Completed = completed,
            Total = Math.Max(total, 1),
            Failed = failed
        });
    }

    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "untitled";
        }

        var invalid = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var cleaned = Regex.Replace(name, "[" + invalid + "]+", "_").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "untitled" : cleaned;
    }
}
