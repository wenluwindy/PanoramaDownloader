using PanoramaDownloader.Core.Stitcher;
using SkiaSharp;

namespace PanoramaDownloader.Tests;

public class StitcherTests
{
    [Fact]
    public void CubeFaceStitcher_Stitches2x2Tiles()
    {
        var root = CreateTempDir();
        try
        {
            WriteSolidTile(Path.Combine(root, "l3", "f", "01_01.jpg"), 64, 64, SKColors.Red);
            WriteSolidTile(Path.Combine(root, "l3", "f", "01_02.jpg"), 64, 64, SKColors.Green);
            WriteSolidTile(Path.Combine(root, "l3", "f", "02_01.jpg"), 64, 64, SKColors.Blue);
            WriteSolidTile(Path.Combine(root, "l3", "f", "02_02.jpg"), 64, 64, SKColors.Yellow);

            var outPath = Path.Combine(root, "face_f.jpg");
            CubeFaceStitcher.StitchFace(root, "f", 3, 2, 2, 2, outPath);

            Assert.True(File.Exists(outPath));
            using var bmp = SKBitmap.Decode(outPath);
            Assert.NotNull(bmp);
            Assert.Equal(128, bmp!.Width);
            Assert.Equal(128, bmp.Height);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MatrixTileStitcher_StitchesAndRespectsMaxWidth()
    {
        var root = CreateTempDir();
        try
        {
            WriteSolidTile(Path.Combine(root, "l2", "01_01.jpg"), 100, 50, SKColors.Orange);
            WriteSolidTile(Path.Combine(root, "l2", "01_02.jpg"), 100, 50, SKColors.Purple);

            var outPath = Path.Combine(root, "equirect.jpg");
            MatrixTileStitcher.Stitch(root, 2, 1, 2, 2, outPath, jpegQuality: 90, maxWidth: 100);

            Assert.True(File.Exists(outPath));
            using var bmp = SKBitmap.Decode(outPath);
            Assert.NotNull(bmp);
            Assert.Equal(100, bmp!.Width);
            Assert.True(bmp.Height > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void EquirectProjector_Produces2to1Jpeg()
    {
        var root = CreateTempDir();
        try
        {
            var faces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var face in new[] { "f", "r", "b", "l", "u", "d" })
            {
                var path = Path.Combine(root, $"{face}.jpg");
                WriteSolidTile(path, 64, 64, SKColors.Gray);
                faces[face] = path;
            }

            var outPath = Path.Combine(root, "eq.jpg");
            EquirectProjector.Convert(faces, outPath, equirectWidth: 256, jpegQuality: 85);

            Assert.True(File.Exists(outPath));
            using var bmp = SKBitmap.Decode(outPath);
            Assert.NotNull(bmp);
            Assert.Equal(256, bmp!.Width);
            Assert.Equal(128, bmp.Height);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MeasureDownloadedGrid_IgnoresMissingEdges()
    {
        var root = CreateTempDir();
        try
        {
            WriteSolidTile(Path.Combine(root, "l3", "f", "01_01.jpg"), 32, 32, SKColors.White);
            WriteSolidTile(Path.Combine(root, "l3", "f", "02_02.jpg"), 32, 32, SKColors.White);

            var (rows, cols) = CubeFaceStitcher.MeasureDownloadedGrid(
                root, 3, ["f"], 2, maxRows: 5, maxCols: 5);

            Assert.Equal(2, rows);
            Assert.Equal(2, cols);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pano-stitch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteSolidTile(string path, int w, int h, SKColor color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(color);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var fs = File.OpenWrite(path);
        data.SaveTo(fs);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // ignore cleanup failures on Windows file locks
        }
    }
}
