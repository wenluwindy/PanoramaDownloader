using SkiaSharp;

namespace PanoramaDownloader.Core.Stitcher;

/// <summary>
/// 将平面矩阵多分辨率瓦片拼成一张等距柱状图。
/// </summary>
public static class MatrixTileStitcher
{
    public static string Stitch(
        string tilesRoot,
        int level,
        int rows,
        int cols,
        int indexPad,
        string outputPath,
        int jpegQuality = 92,
        int? maxWidth = null)
    {
        SKBitmap?[,] tiles = new SKBitmap?[rows, cols];

        try
        {
            for (var r = 1; r <= rows; r++)
            {
                for (var c = 1; c <= cols; c++)
                {
                    var path = ResolveTilePath(tilesRoot, level, r, c, indexPad);
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
                throw new InvalidOperationException("矩阵瓦片为空，无法拼接整图。");
            }

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

            FillMissingSpans(colWidths);
            FillMissingSpans(rowHeights);

            var faceW = colWidths.Sum();
            var faceH = rowHeights.Sum();
            using var stitched = new SKBitmap(faceW, faceH);
            using (var canvas = new SKCanvas(stitched))
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

            using var final = ResizeIfNeeded(stitched, maxWidth);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var image = SKImage.FromBitmap(final);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(jpegQuality, 50, 100));
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

    public static (int rows, int cols) MeasureDownloadedGrid(
        string tilesRoot,
        int level,
        int indexPad,
        int maxRows,
        int maxCols)
    {
        var rows = 0;
        var cols = 0;
        for (var r = 1; r <= maxRows; r++)
        {
            for (var c = 1; c <= maxCols; c++)
            {
                if (ResolveTilePath(tilesRoot, level, r, c, indexPad) is null)
                {
                    continue;
                }

                rows = Math.Max(rows, r);
                cols = Math.Max(cols, c);
            }
        }

        return (Math.Max(rows, 1), Math.Max(cols, 1));
    }

    private static SKBitmap ResizeIfNeeded(SKBitmap source, int? maxWidth)
    {
        if (maxWidth is null || maxWidth <= 0 || source.Width <= maxWidth)
        {
            return source.Copy();
        }

        var w = maxWidth.Value;
        if (w % 2 != 0)
        {
            w++;
        }

        var h = Math.Max(1, (int)Math.Round(source.Height * (w / (double)source.Width)));
        var dest = new SKBitmap(w, h);
        using var canvas = new SKCanvas(dest);
        canvas.Clear(SKColors.Black);
        canvas.DrawBitmap(source, new SKRect(0, 0, w, h), SKSamplingOptions.Default);
        return dest;
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

    private static string? ResolveTilePath(string tilesRoot, int level, int row, int col, int indexPad)
    {
        var rowP = row.ToString().PadLeft(indexPad, '0');
        var colP = col.ToString().PadLeft(indexPad, '0');
        var path = Path.Combine(tilesRoot, $"l{level}", $"{rowP}_{colP}.jpg");
        if (File.Exists(path))
        {
            return path;
        }

        var dir = Path.Combine(tilesRoot, $"l{level}");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.GetFiles(dir, "*.jpg")
            .FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f)
                    .Contains($"{rowP}_{colP}", StringComparison.Ordinal));
    }
}
