using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PanoramaDownloader.Core.Stitcher;

namespace PanoramaDownloader.App.Views;

public partial class EquirectPreviewWindow : Window
{
    private readonly EquirectPreviewViewModel _vm;
    private readonly List<Border> _faceTiles = [];
    private FaceSlotViewModel? _dragSlot;
    private FaceSlotViewModel? _hoverSlot;
    private Border? _captureTile;
    private BitmapImage? _dragThumb;
    private int _dragRotation;
    private Point _dragStart;
    private bool _dragging;
    private bool _previewSwapped;
    private bool _committingDrop;

    public EquirectPreviewWindow(
        IReadOnlyDictionary<string, string> facePaths,
        string outputPath,
        int previewWidth,
        int finalWidth,
        int jpegQuality)
    {
        InitializeComponent();
        _vm = new EquirectPreviewViewModel(facePaths, outputPath, previewWidth, finalWidth, jpegQuality);
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            CollectFaceTiles();
            if (_vm.RefreshPreviewCommand.CanExecute(null))
            {
                await _vm.RefreshPreviewCommand.ExecuteAsync(null);
            }
        };
    }

    public bool Saved => _vm.Saved;
    public string? OutputPath => _vm.OutputPath;

    private void CollectFaceTiles()
    {
        _faceTiles.Clear();
        _faceTiles.AddRange([TileU, TileL, TileF, TileR, TileB, TileD]);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var ok = await _vm.SaveFinalAsync();
        if (ok)
        {
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void FaceTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 预览生成中也允许拖拽换位
        if (sender is not Border border || border.DataContext is not FaceSlotViewModel slot)
        {
            return;
        }

        // 旋转按钮点击不启动拖拽
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (_faceTiles.Count == 0)
        {
            CollectFaceTiles();
        }

        _dragSlot = slot;
        _captureTile = border;
        _dragThumb = slot.Thumbnail;
        _dragRotation = slot.Rotation;
        _dragStart = e.GetPosition(this);
        _dragging = false;
        _previewSwapped = false;
        _hoverSlot = null;
        _committingDrop = false;
        border.CaptureMouse();
        e.Handled = true;
    }

    private void FaceTile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSlot is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var pos = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragging = true;
            _dragSlot.IsDragSource = true;
            DragGhost.Visibility = Visibility.Visible;
            DragGhostImage.Source = _dragThumb;
            DragGhostRotate.Angle = _dragRotation;
            Mouse.OverrideCursor = Cursors.Hand;
        }

        UpdateGhostPosition(pos);
        UpdateHoverTarget(e.GetPosition(FacePuzzleRoot));
    }

    private async void FaceTile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragSlot is null)
        {
            return;
        }

        var source = _dragSlot;
        var didDrag = _dragging;
        var pos = e.GetPosition(FacePuzzleRoot);
        var target = didDrag ? HitTestSlot(pos) : null;
        if (target is not null && ReferenceEquals(target, source))
        {
            target = null;
        }

        // 先撤销拖拽过程中的临时互换，再按松手位置正式交换一次
        if (_previewSwapped && _hoverSlot is not null)
        {
            _vm.SwapSlots(source, _hoverSlot);
            _previewSwapped = false;
        }

        _committingDrop = true;
        ClearDragVisuals(restorePreview: false);

        if (_captureTile is not null && _captureTile.IsMouseCaptured)
        {
            _captureTile.ReleaseMouseCapture();
        }

        _captureTile = null;
        _committingDrop = false;

        if (!didDrag || target is null)
        {
            return;
        }

        _vm.SwapSlots(source, target);
        _vm.NotifyMapsFromSlots();
        await _vm.RefreshPreviewCommand.ExecuteAsync(null);
    }

    private void FaceTile_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_committingDrop)
        {
            return;
        }

        if (_dragSlot is not null)
        {
            ClearDragVisuals(restorePreview: true);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragSlot is not null)
        {
            ClearDragVisuals(restorePreview: true);
            if (_captureTile is not null && _captureTile.IsMouseCaptured)
            {
                _captureTile.ReleaseMouseCapture();
            }

            _captureTile = null;
            e.Handled = true;
        }
    }

    private void UpdateGhostPosition(Point posInWindow)
    {
        var rootPos = FacePuzzleRoot.TranslatePoint(new Point(0, 0), this);
        Canvas.SetLeft(DragGhost, posInWindow.X - rootPos.X - 40);
        Canvas.SetTop(DragGhost, posInWindow.Y - rootPos.Y - 40);
    }

    private void UpdateHoverTarget(Point posInPuzzle)
    {
        var hit = HitTestSlot(posInPuzzle);
        if (ReferenceEquals(hit, _hoverSlot))
        {
            return;
        }

        // 撤销上一次实时预览互换
        if (_previewSwapped && _dragSlot is not null && _hoverSlot is not null)
        {
            _vm.SwapSlots(_dragSlot, _hoverSlot);
            _previewSwapped = false;
        }

        if (_hoverSlot is not null)
        {
            _hoverSlot.IsDropTarget = false;
        }

        _hoverSlot = hit is not null && !ReferenceEquals(hit, _dragSlot) ? hit : null;

        if (_hoverSlot is not null && _dragSlot is not null)
        {
            _hoverSlot.IsDropTarget = true;
            _vm.SwapSlots(_dragSlot, _hoverSlot);
            _previewSwapped = true;
        }
    }

    private FaceSlotViewModel? HitTestSlot(Point posInPuzzle)
    {
        // 用拼图块实际矩形命中，避免 Capture/幽灵层干扰 VisualTreeHelper.HitTest
        FaceSlotViewModel? best = null;
        var bestArea = double.MaxValue;

        foreach (var tile in _faceTiles)
        {
            if (tile.DataContext is not FaceSlotViewModel slot)
            {
                continue;
            }

            var topLeft = tile.TranslatePoint(new Point(0, 0), FacePuzzleRoot);
            var rect = new Rect(topLeft, new Size(tile.ActualWidth, tile.ActualHeight));
            if (!rect.Contains(posInPuzzle))
            {
                continue;
            }

            var area = rect.Width * rect.Height;
            if (area < bestArea)
            {
                bestArea = area;
                best = slot;
            }
        }

        return best;
    }

    private void ClearDragVisuals(bool restorePreview)
    {
        if (restorePreview && _previewSwapped && _dragSlot is not null && _hoverSlot is not null)
        {
            _vm.SwapSlots(_dragSlot, _hoverSlot);
        }

        if (_dragSlot is not null)
        {
            _dragSlot.IsDragSource = false;
        }

        if (_hoverSlot is not null)
        {
            _hoverSlot.IsDropTarget = false;
        }

        _dragSlot = null;
        _hoverSlot = null;
        _dragging = false;
        _previewSwapped = false;
        _dragThumb = null;
        DragGhost.Visibility = Visibility.Collapsed;
        DragGhostImage.Source = null;
        Mouse.OverrideCursor = null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

public sealed partial class FaceSlotViewModel : ObservableObject
{
    public FaceSlotViewModel(string logicalKey, string label)
    {
        LogicalKey = logicalKey;
        Label = label;
    }

    public string LogicalKey { get; }
    public string Label { get; }

    [ObservableProperty] private string _sourceKey = "";
    [ObservableProperty] private BitmapImage? _thumbnail;
    [ObservableProperty] private int _rotation;
    [ObservableProperty] private bool _isDragSource;
    [ObservableProperty] private bool _isDropTarget;

    public string SourceBadge => SourceKey.ToUpperInvariant();

    partial void OnSourceKeyChanged(string value) => OnPropertyChanged(nameof(SourceBadge));
}

public sealed partial class EquirectPreviewViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<string, string> _facePaths;
    private readonly Dictionary<string, BitmapImage> _sourceThumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _previewWidth;
    private readonly int _finalWidth;
    private readonly EquirectProjectOptions _options = new();
    private readonly Dictionary<string, int> _rotations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["f"] = 0, ["r"] = 0, ["b"] = 0, ["l"] = 0, ["u"] = 0, ["d"] = 0
    };

    public EquirectPreviewViewModel(
        IReadOnlyDictionary<string, string> facePaths,
        string outputPath,
        int previewWidth,
        int finalWidth,
        int jpegQuality)
    {
        _facePaths = facePaths;
        OutputPath = outputPath;
        _previewWidth = Math.Clamp(previewWidth, 1024, 4096);
        _finalWidth = Math.Clamp(finalWidth, 1024, 8192);
        _options.JpegQuality = jpegQuality;

        SlotU = new FaceSlotViewModel("u", "上 U");
        SlotL = new FaceSlotViewModel("l", "左 L");
        SlotF = new FaceSlotViewModel("f", "前 F");
        SlotR = new FaceSlotViewModel("r", "右 R");
        SlotB = new FaceSlotViewModel("b", "后 B");
        SlotD = new FaceSlotViewModel("d", "下 D");
        AllSlots = [SlotF, SlotR, SlotB, SlotL, SlotU, SlotD];

        LoadThumbnails();
        ResetMaps();
    }

    public FaceSlotViewModel SlotF { get; }
    public FaceSlotViewModel SlotR { get; }
    public FaceSlotViewModel SlotB { get; }
    public FaceSlotViewModel SlotL { get; }
    public FaceSlotViewModel SlotU { get; }
    public FaceSlotViewModel SlotD { get; }
    public IReadOnlyList<FaceSlotViewModel> AllSlots { get; }

    public string OutputPath { get; }
    public bool Saved { get; private set; }

    [ObservableProperty] private BitmapImage? _previewImage;
    [ObservableProperty] private string _statusText = "正在生成预览…";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _yawDegrees;
    [ObservableProperty] private double _pitchDegrees;
    [ObservableProperty] private double _rollDegrees;
    [ObservableProperty] private bool _rotateUpDown180 = true;

    public string YawText => $"偏航 Yaw：{YawDegrees:0}°";
    public string PitchText => $"俯仰 Pitch：{PitchDegrees:0}°";
    public string RollText => $"滚转 Roll：{RollDegrees:0}°";

    partial void OnYawDegreesChanged(double value) => OnPropertyChanged(nameof(YawText));
    partial void OnPitchDegreesChanged(double value) => OnPropertyChanged(nameof(PitchText));
    partial void OnRollDegreesChanged(double value) => OnPropertyChanged(nameof(RollText));

    private void LoadThumbnails()
    {
        foreach (var kv in _facePaths)
        {
            var key = kv.Key.ToLowerInvariant();
            var thumb = TryLoadThumb(kv.Value);
            if (thumb is not null)
            {
                _sourceThumbs[key] = thumb;
            }
        }
    }

    private static BitmapImage? TryLoadThumb(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 160;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void ResetMaps()
    {
        foreach (var key in _rotations.Keys.ToList())
        {
            _rotations[key] = 0;
        }

        ApplyIdentityMapping();
        YawDegrees = 0;
        PitchDegrees = 0;
        RollDegrees = 0;
        RotateUpDown180 = true;
    }

    private void ApplyIdentityMapping()
    {
        foreach (var slot in AllSlots)
        {
            AssignSource(slot, slot.LogicalKey);
        }
    }

    private void AssignSource(FaceSlotViewModel slot, string sourceKey)
    {
        slot.SourceKey = sourceKey;
        slot.Thumbnail = _sourceThumbs.TryGetValue(sourceKey, out var img) ? img : null;
        slot.Rotation = _rotations.TryGetValue(sourceKey, out var rot) ? rot : 0;
    }

    public void SwapSlots(FaceSlotViewModel a, FaceSlotViewModel b)
    {
        var srcA = a.SourceKey;
        var srcB = b.SourceKey;
        AssignSource(a, srcB);
        AssignSource(b, srcA);
    }

    public void NotifyMapsFromSlots()
    {
        // 供外部在互换提交后同步状态文案
        StatusText = "贴图已互换，正在刷新预览…";
    }

    private EquirectProjectOptions BuildOptions()
    {
        var o = _options.Clone();
        o.YawDegrees = YawDegrees;
        o.PitchDegrees = PitchDegrees;
        o.RollDegrees = RollDegrees;
        o.RotateUpDown180 = RotateUpDown180;
        o.FaceSourceMap = AllSlots.ToDictionary(
            s => s.LogicalKey,
            s => s.SourceKey,
            StringComparer.OrdinalIgnoreCase);
        o.FaceRotations = new Dictionary<string, int>(_rotations, StringComparer.OrdinalIgnoreCase);
        return o;
    }

    [RelayCommand]
    private void RotateSlot(FaceSlotViewModel? slot)
    {
        if (slot is null || IsBusy)
        {
            return;
        }

        var next = (slot.Rotation + 90) % 360;
        _rotations[slot.SourceKey] = next;
        // 同一源文件可能只出现在一个逻辑槽，刷新显示旋转
        foreach (var s in AllSlots)
        {
            if (string.Equals(s.SourceKey, slot.SourceKey, StringComparison.OrdinalIgnoreCase))
            {
                s.Rotation = next;
            }
        }

        _ = RefreshPreviewAsync();
    }

    [RelayCommand]
    private async Task RefreshPreviewAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在生成预览…";
        try
        {
            var options = BuildOptions();
            var jpeg = await Task.Run(() =>
                EquirectProjector.ConvertToJpeg(_facePaths, options, _previewWidth));

            PreviewImage = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(jpeg);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            });

            StatusText = $"预览已更新（{_previewWidth}px）。拖拽拼图块可实时换位，确认后保存整图。";
        }
        catch (Exception ex)
        {
            StatusText = "预览失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        ResetMaps();
        await RefreshPreviewAsync();
    }

    [RelayCommand]
    private async Task Open3DPreviewAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在生成 3D 预览贴图…";
        try
        {
            // 视角偏移由 3D 窗口拖拽完成；贴图换位/旋转与上下面校正仍写入纹理
            var options = BuildOptions();
            options.YawDegrees = 0;
            options.PitchDegrees = 0;
            options.RollDegrees = 0;
            var width = Math.Min(_previewWidth, 2048);
            var jpeg = await Task.Run(() =>
                EquirectProjector.ConvertToJpeg(_facePaths, options, width));

            var owner = Application.Current.Windows.OfType<EquirectPreviewWindow>().FirstOrDefault();
            var win = Panorama3DPreviewWindow.TryCreateFromJpegBytes(jpeg, owner, "3D 全景预览");
            if (win is null)
            {
                StatusText = "无法打开 3D 预览。";
                return;
            }

            StatusText = "已打开 3D 预览（拖拽旋转，滚轮缩放）。";
            win.Show();
        }
        catch (Exception ex)
        {
            StatusText = "3D 预览失败：" + ex.Message;
            MessageBox.Show(StatusText, "3D 预览", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> SaveFinalAsync()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        StatusText = $"正在导出最终整图（{_finalWidth}px）…";
        try
        {
            var options = BuildOptions();
            await Task.Run(() =>
                EquirectProjector.Convert(_facePaths, OutputPath, options, _finalWidth));
            Saved = true;
            StatusText = "已保存：" + OutputPath;
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
            MessageBox.Show(StatusText, "保存整图", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
