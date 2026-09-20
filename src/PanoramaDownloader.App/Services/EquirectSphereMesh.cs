using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace PanoramaDownloader.App.Services;

/// <summary>构建等距柱状贴图用的内视球面网格。</summary>
public static class EquirectSphereMesh
{
    public static MeshGeometry3D Create(int segments = 64, int rings = 32, double radius = 1.0)
    {
        segments = Math.Clamp(segments, 16, 128);
        rings = Math.Clamp(rings, 8, 64);

        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var textureCoordinates = new PointCollection();
        var triangleIndices = new Int32Collection();

        for (var ring = 0; ring <= rings; ring++)
        {
            var v = (double)ring / rings;
            var phi = Math.PI * v; // 0..π
            var y = Math.Cos(phi);
            var sinPhi = Math.Sin(phi);

            for (var seg = 0; seg <= segments; seg++)
            {
                var u = (double)seg / segments;
                // 水平翻转，使拖拽方向与常见全景查看器一致
                var theta = 2 * Math.PI * (1.0 - u);
                var x = sinPhi * Math.Sin(theta);
                var z = sinPhi * Math.Cos(theta);

                var p = new Point3D(x * radius, y * radius, z * radius);
                positions.Add(p);
                // 法线朝内（相机在球心）
                normals.Add(new Vector3D(-x, -y, -z));
                textureCoordinates.Add(new Point(u, v));
            }
        }

        var stride = segments + 1;
        for (var ring = 0; ring < rings; ring++)
        {
            for (var seg = 0; seg < segments; seg++)
            {
                var i0 = ring * stride + seg;
                var i1 = i0 + 1;
                var i2 = i0 + stride;
                var i3 = i2 + 1;

                // 内侧可见：逆时针绕序（相对外视）
                triangleIndices.Add(i0);
                triangleIndices.Add(i2);
                triangleIndices.Add(i1);

                triangleIndices.Add(i1);
                triangleIndices.Add(i2);
                triangleIndices.Add(i3);
            }
        }

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TextureCoordinates = textureCoordinates,
            TriangleIndices = triangleIndices
        };
        mesh.Freeze();
        return mesh;
    }

    public static ImageBrush CreateEquirectBrush(BitmapSource image)
    {
        var brush = new ImageBrush(image)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            TileMode = TileMode.None,
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }
}
