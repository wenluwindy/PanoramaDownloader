using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using PanoramaDownloader.App.Services;

namespace PanoramaDownloader.App.Views;

public partial class Panorama3DPreviewWindow : Window
{
    private bool _dragging;
    private Point _lastPos;
    private double _yaw;
    private double _pitch;
    private double _fov = 75;

    public Panorama3DPreviewWindow(BitmapSource equirectImage, string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = title;
        }

        ApplyTexture(equirectImage);
        ApplyOrientation();
    }

    public static Panorama3DPreviewWindow? TryCreateFromFile(string equirectPath, Window? owner = null)
    {
        if (string.IsNullOrWhiteSpace(equirectPath) || !File.Exists(equirectPath))
        {
            return null;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(equirectPath, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 2048;
            bmp.EndInit();
            bmp.Freeze();

            var win = new Panorama3DPreviewWindow(bmp, "3D 全景预览 — " + Path.GetFileName(equirectPath));
            if (owner is not null)
            {
                win.Owner = owner;
            }

            return win;
        }
        catch
        {
            return null;
        }
    }

    public static Panorama3DPreviewWindow? TryCreateFromJpegBytes(byte[] jpegBytes, Window? owner = null, string? title = null)
    {
        if (jpegBytes is null || jpegBytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(jpegBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            var win = new Panorama3DPreviewWindow(bmp, title ?? "3D 全景预览");
            if (owner is not null)
            {
                win.Owner = owner;
            }

            return win;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyTexture(BitmapSource image)
    {
        var mesh = EquirectSphereMesh.Create();
        var brush = EquirectSphereMesh.CreateEquirectBrush(image);
        var material = new DiffuseMaterial(brush);
        var geometry = new GeometryModel3D(mesh, material)
        {
            BackMaterial = material
        };
        SphereVisual.Content = geometry;
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _lastPos = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
        e.Handled = true;
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _dragging = false;
        if (Viewport.IsMouseCaptured)
        {
            Viewport.ReleaseMouseCapture();
        }
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var pos = e.GetPosition(Viewport);
        var dx = pos.X - _lastPos.X;
        var dy = pos.Y - _lastPos.Y;
        _lastPos = pos;

        // 拖拽方向：向右拖 = 向右转头看右侧
        _yaw = NormalizeAngle(_yaw + dx * 0.25);
        _pitch = Math.Clamp(_pitch - dy * 0.25, -89, 89);
        ApplyOrientation();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _fov = Math.Clamp(_fov - e.Delta / 40.0, 35, 100);
        Camera.FieldOfView = _fov;
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.R)
        {
            _yaw = 0;
            _pitch = 0;
            _fov = 75;
            Camera.FieldOfView = _fov;
            ApplyOrientation();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void ApplyOrientation()
    {
        var yawRad = _yaw * Math.PI / 180.0;
        var pitchRad = _pitch * Math.PI / 180.0;
        var cosPitch = Math.Cos(pitchRad);
        var look = new Vector3D(
            cosPitch * Math.Sin(yawRad),
            Math.Sin(pitchRad),
            -cosPitch * Math.Cos(yawRad));
        look.Normalize();

        var worldUp = new Vector3D(0, 1, 0);
        var right = Vector3D.CrossProduct(look, worldUp);
        if (right.LengthSquared < 1e-8)
        {
            right = new Vector3D(1, 0, 0);
        }
        else
        {
            right.Normalize();
        }

        var up = Vector3D.CrossProduct(right, look);
        up.Normalize();

        Camera.LookDirection = look;
        Camera.UpDirection = up;
    }

    private static double NormalizeAngle(double degrees)
    {
        degrees %= 360;
        if (degrees > 180)
        {
            degrees -= 360;
        }
        else if (degrees < -180)
        {
            degrees += 360;
        }

        return degrees;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
