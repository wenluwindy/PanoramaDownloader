using SkiaSharp;

namespace PanoramaDownloader.Core.Stitcher;

/// <summary>
/// 立方体面 → 等距柱状投影（2:1），支持偏航/俯仰/滚转与各面旋转、换贴。
/// </summary>
public static class EquirectProjector
{
    private static readonly string[] LogicalFaces = ["f", "r", "b", "l", "u", "d"];

    public static string Convert(
        IReadOnlyDictionary<string, string> facePaths,
        string outputPath,
        int? equirectWidth = null,
        int jpegQuality = 92,
        bool rotateUpDown180 = true)
    {
        var options = new EquirectProjectOptions
        {
            JpegQuality = jpegQuality,
            RotateUpDown180 = rotateUpDown180
        };
        return Convert(facePaths, outputPath, options, equirectWidth);
    }

    public static string Convert(
        IReadOnlyDictionary<string, string> facePaths,
        string outputPath,
        EquirectProjectOptions options,
        int? equirectWidth = null)
    {
        var bytes = ConvertToJpeg(facePaths, options, equirectWidth);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, bytes);
        return outputPath;
    }

    public static byte[] ConvertToJpeg(
        IReadOnlyDictionary<string, string> facePaths,
        EquirectProjectOptions options,
        int? equirectWidth = null)
    {
        var faces = LoadFaces(facePaths, options);
        try
        {
            if (faces.Count == 0)
            {
                throw new InvalidOperationException("没有可用的立方体面图。");
            }

            var faceSize = faces.Values.Max(f => f.Size);
            var width = equirectWidth ?? Math.Min(faceSize * 4, 16384);
            if (width % 2 != 0)
            {
                width++;
            }

            var height = width / 2;
            using var output = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var pixels = output.Pixels;

            var yaw = options.YawDegrees * Math.PI / 180.0;
            var pitch = options.PitchDegrees * Math.PI / 180.0;
            var roll = options.RollDegrees * Math.PI / 180.0;

            for (var y = 0; y < height; y++)
            {
                var lat = Math.PI * (0.5 - (y + 0.5) / height);
                var cosLat = Math.Cos(lat);
                var sinLat = Math.Sin(lat);

                for (var x = 0; x < width; x++)
                {
                    var lon = 2 * Math.PI * ((x + 0.5) / width - 0.5);
                    var cosLon = Math.Cos(lon);
                    var sinLon = Math.Sin(lon);

                    var dx = cosLat * sinLon;
                    var dy = sinLat;
                    var dz = cosLat * cosLon;

                    RotateVector(ref dx, ref dy, ref dz, yaw, pitch, roll);
                    pixels[y * width + x] = SampleCube(faces, dx, dy, dz, options.RotateUpDown180);
                }
            }

            output.Pixels = pixels;
            using var image = SKImage.FromBitmap(output);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(options.JpegQuality, 50, 100));
            return data.ToArray();
        }
        finally
        {
            foreach (var face in faces.Values)
            {
                face.Dispose();
            }
        }
    }

    private static Dictionary<string, FaceData> LoadFaces(
        IReadOnlyDictionary<string, string> facePaths,
        EquirectProjectOptions options)
    {
        var loadedSources = new Dictionary<string, SKBitmap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var kv in facePaths)
            {
                var bmp = SKBitmap.Decode(kv.Value);
                if (bmp is null)
                {
                    continue;
                }

                var key = kv.Key.ToLowerInvariant();
                var rot = options.FaceRotations.TryGetValue(key, out var r) ? r : 0;
                var rotated = RotateBitmap(bmp, rot);
                if (!ReferenceEquals(rotated, bmp))
                {
                    bmp.Dispose();
                }

                var size = Math.Max(rotated.Width, rotated.Height);
                if (rotated.Width != rotated.Height)
                {
                    var square = new SKBitmap(size, size);
                    using var canvas = new SKCanvas(square);
                    canvas.Clear(SKColors.Black);
                    canvas.DrawBitmap(
                        rotated,
                        (size - rotated.Width) / 2f,
                        (size - rotated.Height) / 2f,
                        SKSamplingOptions.Default);
                    rotated.Dispose();
                    rotated = square;
                }

                loadedSources[key] = rotated;
            }

            var result = new Dictionary<string, FaceData>(StringComparer.OrdinalIgnoreCase);
            foreach (var logical in LogicalFaces)
            {
                var sourceKey = options.FaceSourceMap.TryGetValue(logical, out var mapped)
                    ? mapped.ToLowerInvariant()
                    : logical;
                if (!loadedSources.TryGetValue(sourceKey, out var src))
                {
                    continue;
                }

                // 每个逻辑面复制一份像素（映射可能指向同一源）
                var copy = src.Copy();
                result[logical] = new FaceData(copy);
            }

            return result;
        }
        finally
        {
            foreach (var bmp in loadedSources.Values)
            {
                bmp.Dispose();
            }
        }
    }

    private static SKBitmap RotateBitmap(SKBitmap source, int degreesClockwise)
    {
        degreesClockwise = ((degreesClockwise % 360) + 360) % 360;
        if (degreesClockwise == 0)
        {
            return source;
        }

        var swap = degreesClockwise is 90 or 270;
        var w = swap ? source.Height : source.Width;
        var h = swap ? source.Width : source.Height;
        var dest = new SKBitmap(w, h);
        using var canvas = new SKCanvas(dest);
        canvas.Clear(SKColors.Black);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(degreesClockwise);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        return dest;
    }

    private static void RotateVector(ref double x, ref double y, ref double z, double yaw, double pitch, double roll)
    {
        // yaw around Y
        if (Math.Abs(yaw) > 1e-9)
        {
            var c = Math.Cos(yaw);
            var s = Math.Sin(yaw);
            var nx = x * c + z * s;
            var nz = -x * s + z * c;
            x = nx;
            z = nz;
        }

        // pitch around X
        if (Math.Abs(pitch) > 1e-9)
        {
            var c = Math.Cos(pitch);
            var s = Math.Sin(pitch);
            var ny = y * c - z * s;
            var nz = y * s + z * c;
            y = ny;
            z = nz;
        }

        // roll around Z
        if (Math.Abs(roll) > 1e-9)
        {
            var c = Math.Cos(roll);
            var s = Math.Sin(roll);
            var nx = x * c - y * s;
            var ny = x * s + y * c;
            x = nx;
            y = ny;
        }
    }

    private static SKColor SampleCube(
        Dictionary<string, FaceData> faces,
        double dx, double dy, double dz,
        bool rotateUpDown180)
    {
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        var az = Math.Abs(dz);

        string face;
        double u;
        double v;
        double ma;

        if (ax >= ay && ax >= az)
        {
            ma = ax;
            if (dx >= 0)
            {
                face = "r";
                u = -dz;
                v = dy;
            }
            else
            {
                face = "l";
                u = dz;
                v = dy;
            }
        }
        else if (ay >= ax && ay >= az)
        {
            ma = ay;
            if (dy >= 0)
            {
                face = "u";
                u = dx;
                v = -dz;
            }
            else
            {
                face = "d";
                u = dx;
                v = dz;
            }
        }
        else
        {
            ma = az;
            if (dz >= 0)
            {
                face = "f";
                u = dx;
                v = dy;
            }
            else
            {
                face = "b";
                u = -dx;
                v = dy;
            }
        }

        if (rotateUpDown180 && face is "u" or "d")
        {
            u = -u;
            v = -v;
        }

        if (!faces.TryGetValue(face, out var data))
        {
            data = faces.Values.First();
        }

        var uc = 0.5 * (u / ma + 1.0);
        var vc = 0.5 * (1.0 - v / ma);
        return data.SampleBilinear(uc, vc);
    }

    private sealed class FaceData : IDisposable
    {
        private readonly SKBitmap _bitmap;
        private readonly SKColor[] _pixels;

        public FaceData(SKBitmap bitmap)
        {
            _bitmap = bitmap;
            _pixels = bitmap.Pixels;
            Size = bitmap.Width;
        }

        public int Size { get; }

        public SKColor SampleBilinear(double u, double v)
        {
            u = Math.Clamp(u, 0, 1);
            v = Math.Clamp(v, 0, 1);
            var x = u * (Size - 1);
            var y = v * (Size - 1);
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            var x1 = Math.Min(x0 + 1, Size - 1);
            var y1 = Math.Min(y0 + 1, Size - 1);
            var tx = x - x0;
            var ty = y - y0;

            var c00 = _pixels[y0 * Size + x0];
            var c10 = _pixels[y0 * Size + x1];
            var c01 = _pixels[y1 * Size + x0];
            var c11 = _pixels[y1 * Size + x1];
            return Lerp(Lerp(c00, c10, tx), Lerp(c01, c11, tx), ty);
        }

        private static SKColor Lerp(SKColor a, SKColor b, double t)
        {
            var r = (byte)(a.Red + (b.Red - a.Red) * t);
            var g = (byte)(a.Green + (b.Green - a.Green) * t);
            var bl = (byte)(a.Blue + (b.Blue - a.Blue) * t);
            return new SKColor(r, g, bl);
        }

        public void Dispose() => _bitmap.Dispose();
    }
}
