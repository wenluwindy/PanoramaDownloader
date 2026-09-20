using SkiaSharp;

namespace PanoramaDownloader.Core.Stitcher;

public static class CubeFaceStitcher
{
    /// <summary>
    /// 将某一面的行列瓦片拼成完整面图，保存为 JPEG。
    /// 按实际瓦片宽高累加放置（兼容边缘不足 512 的余数瓦片），避免黑边导致「缺一块」。
    /// </summary>
    public static string StitchFace(
        string tilesRoot,
        string face,
        int level,
        int rows,
        int cols,
        int indexPad,
        string outputPath)
    {
        SKBitmap?[,] tiles = new SKBitmap?[rows, cols];

        try
        {
            for (var r = 1; r <= rows; r++)
            {
                for (var c = 1; c <= cols; c++)
                {
                    var path = ResolveTilePath(tilesRoot, face, level, r, c, indexPad);
                    if (path is null)
                    {
                        continue;
                    }

                    var bmp = SKBitmap.Decode(path);
                    if (bmp is null)
                    {
                        continue;
                    }

                    tiles[r - 1, c - 1] = bmp;
                }
            }

            // 收缩到实际存在瓦片的最大行列，去掉探测过大时的空边
            var effectiveRows = 0;
            var effectiveCols = 0;
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    if (tiles[r, c] is null)
                    {
                        continue;
                    }

                    effectiveRows = Math.Max(effectiveRows, r + 1);
                    effectiveCols = Math.Max(effectiveCols, c + 1);
                }
            }

            if (effectiveRows == 0 || effectiveCols == 0)
            {
                throw new InvalidOperationException($"面 {face} 没有可用瓦片。");
            }

            // 每列宽度 / 每行高度：取该列/行内最大瓦片尺寸（余数瓦片通常更小）
            var colWidths = new int[effectiveCols];
            var rowHeights = new int[effectiveRows];
            for (var r = 0; r < effectiveRows; r++)
            {
                for (var c = 0; c < effectiveCols; c++)
                {
                    var tile = tiles[r, c];
                    if (tile is null)
                    {
                        continue;
                    }

                    colWidths[c] = Math.Max(colWidths[c], tile.Width);
                    rowHeights[r] = Math.Max(rowHeights[r], tile.Height);
                }
            }

            // 空列/空行用邻近满瓦片尺寸填，避免整列丢失时塌缩
            FillMissingSpans(colWidths);
            FillMissingSpans(rowHeights);

            var faceW = colWidths.Sum();
            var faceH = rowHeights.Sum();
            if (faceW <= 0 || faceH <= 0)
            {
                throw new InvalidOperationException($"面 {face} 拼接尺寸无效。");
            }

            using var faceBmp = new SKBitmap(faceW, faceH);
            using (var canvas = new SKCanvas(faceBmp))
            {
                canvas.Clear(SKColors.Black);
                var y = 0;
                for (var r = 0; r < effectiveRows; r++)
                {
                    var x = 0;
                    for (var c = 0; c < effectiveCols; c++)
                    {
                        var tile = tiles[r, c];
                        if (tile is not null)
                        {
                            canvas.DrawBitmap(tile, x, y, SKSamplingOptions.Default);
                        }

                        x += colWidths[c];
                    }

                    y += rowHeights[r];
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var image = SKImage.FromBitmap(faceBmp);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            using var fs = File.OpenWrite(outputPath);
            data.SaveTo(fs);
            return outputPath;
        }
        finally
        {
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    tiles[r, c]?.Dispose();
                }
            }
        }
    }

    private static void FillMissingSpans(int[] spans)
    {
        var fallback = spans.FirstOrDefault(v => v > 0);
        if (fallback <= 0)
        {
            fallback = 512;
        }

        for (var i = 0; i < spans.Length; i++)
        {
            if (spans[i] <= 0)
            {
                spans[i] = fallback;
            }
        }
    }

    private static string? ResolveTilePath(
        string tilesRoot,
        string face,
        int level,
        int row,
        int col,
        int indexPad)
    {
        var rowP = row.ToString().PadLeft(indexPad, '0');
        var colP = col.ToString().PadLeft(indexPad, '0');
        var path = Path.Combine(tilesRoot, $"l{level}", face, $"{rowP}_{colP}.jpg");
        if (File.Exists(path))
        {
            return path;
        }

        var dir = Path.Combine(tilesRoot, $"l{level}", face);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.GetFiles(dir, "*.jpg")
            .FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f)
                    .Contains($"{rowP}_{colP}", StringComparison.Ordinal));
    }

    public static Dictionary<string, string> StitchAllFaces(
        string tilesRoot,
        int level,
        int rows,
        int cols,
        int indexPad,
        string outputDirectory,
        IEnumerable<string> faces)
    {
        Directory.CreateDirectory(outputDirectory);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var face in faces)
        {
            var outPath = Path.Combine(outputDirectory, $"{face}.jpg");
            StitchFace(tilesRoot, face, level, rows, cols, indexPad, outPath);
            map[face] = outPath;
        }

        return map;
    }

    /// <summary>
    /// 根据已下载文件统计实际最大行列（用于裁掉探测过大的空边）。
    /// </summary>
    public static (int rows, int cols) MeasureDownloadedGrid(
        string tilesRoot,
        int level,
        IEnumerable<string> faces,
        int indexPad,
        int maxRows,
        int maxCols)
    {
        var rows = 0;
        var cols = 0;
        foreach (var face in faces)
        {
            for (var r = 1; r <= maxRows; r++)
            {
                for (var c = 1; c <= maxCols; c++)
                {
                    if (ResolveTilePath(tilesRoot, face, level, r, c, indexPad) is null)
                    {
                        continue;
                    }

                    rows = Math.Max(rows, r);
                    cols = Math.Max(cols, c);
                }
            }
        }

        return (Math.Max(rows, 1), Math.Max(cols, 1));
    }
}
