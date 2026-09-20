using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PanoramaDownloader.App.Services;
using PanoramaDownloader.Core.Detectors;
using PanoramaDownloader.Core.Downloader;
using PanoramaDownloader.Core.Export;
using PanoramaDownloader.Core.Models;
using PanoramaDownloader.Core.Services;
using PanoramaDownloader.Core.Storage;

namespace PanoramaDownloader.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore;
    private readonly AnalysisService _analysisService;
    private readonly ExportService _exportService = new();
    private readonly List<CapturedNetworkEntry> _capturedEntries = [];
    private readonly object _captureLock = new();
    private CancellationTokenSource? _exportCts;

    public MainViewModel()
    {
        _settingsStore = new SettingsStore();
        _analysisService = new AnalysisService();
        Settings = _settingsStore.Load();
        SourceUrl = Settings.RecentUrls.FirstOrDefault() ?? "https://www.720yun.com/";
        AppendLog("就绪。请先「打开网址」查看全景，确认画面正常后再点「分析」。");
    }

    public AppSettings Settings { get; }

    public SettingsStore SettingsStore => _settingsStore;

    public string WebViewUserDataFolder => _settingsStore.UserDataDirectory;

    public ObservableCollection<SceneItemViewModel> Scenes { get; } = [];

    /// <summary>按「仅多瓦片」开关过滤后的列表，供左侧绑定。</summary>
    public ObservableCollection<SceneItemViewModel> VisibleScenes { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty]
    private string _sourceUrl = string.Empty;

    [ObservableProperty]
    private string _statusText = "未开始";

    [ObservableProperty]
    private string _loginStatusText = "未检测登录态";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isWebViewVisible = true;

    [ObservableProperty]
    private AnalysisStatus _status = AnalysisStatus.Idle;

    [ObservableProperty]
    private SceneItemViewModel? _selectedScene;

    [ObservableProperty]
    private string _manifestSummary = "尚未分析";

    [ObservableProperty]
    private string _sceneFilterHint = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>为 true 时左侧只显示已探测到多个瓦片的场景。</summary>
    [ObservableProperty]
    private bool _onlyShowMultiTileScenes;

    public PanoramaManifest? CurrentManifest { get; private set; }

    public event Func<string, Task>? NavigateRequested;
    public event Func<Task<string?>>? GetPageHtmlRequested;
    public event Func<Task<string?>>? GetPageMetaRequested;
    public event Func<Task>? ClearSessionRequested;
    public event Func<string, Task>? OpenLoginRequested;
    public event Func<string, Task<string?>>? GetCookieHeaderRequested;
    public event Func<IReadOnlyDictionary<string, string>, string, int, int, int, Task<bool>>? ShowEquirectPreviewRequested;
    public event Action? OpenSettingsRequested;
    public event Action? OpenAboutRequested;
    public event Action<string>? OpenEquirect3DRequested;

    [ObservableProperty]
    private string? _lastEquirectPath;

    partial void OnSelectedSceneChanged(SceneItemViewModel? value)
    {
        // binding hook for thumbnail side panel
    }

    partial void OnOnlyShowMultiTileScenesChanged(bool value) => RefreshVisibleScenes();

    private void ClearScenes()
    {
        Scenes.Clear();
        VisibleScenes.Clear();
        SceneFilterHint = string.Empty;
        SelectedScene = null;
    }

    private void RefreshVisibleScenes()
    {
        var previous = SelectedScene;
        VisibleScenes.Clear();

        IEnumerable<SceneItemViewModel> source = Scenes;
        if (OnlyShowMultiTileScenes)
        {
            source = Scenes.Where(s => s.HasMultipleTiles);
        }

        foreach (var item in source)
        {
            VisibleScenes.Add(item);
        }

        var multi = Scenes.Count(s => s.HasMultipleTiles);
        SceneFilterHint = Scenes.Count == 0
            ? string.Empty
            : OnlyShowMultiTileScenes
                ? $"显示 {VisibleScenes.Count}/{Scenes.Count}（已过滤无多瓦片项，共 {multi} 个有多瓦片）"
                : $"共 {Scenes.Count} 个场景，其中 {multi} 个探测到多个瓦片";

        if (previous is not null && VisibleScenes.Contains(previous))
        {
            SelectedScene = previous;
        }
        else
        {
            SelectedScene = VisibleScenes.FirstOrDefault();
        }
    }

    public void AddCapturedEntry(CapturedNetworkEntry entry)
    {
        lock (_captureLock)
        {
            _capturedEntries.Add(entry);
            if (_capturedEntries.Count > 5000)
            {
                _capturedEntries.RemoveRange(0, _capturedEntries.Count - 4000);
            }
        }
    }

    public void ClearCapturedEntries()
    {
        lock (_captureLock)
        {
            _capturedEntries.Clear();
        }
    }

    public IReadOnlyList<CapturedNetworkEntry> SnapshotCapturedEntries()
    {
        lock (_captureLock)
        {
            return _capturedEntries.ToList();
        }
    }

    public void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == false)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => LogLines.Insert(0, line));
        }
        else
        {
            LogLines.Insert(0, line);
        }
    }

    public void SetLoginStatus(bool seemsLoggedIn)
    {
        LoginStatusText = seemsLoggedIn ? "可能已登录（Cookie 已持久化）" : "未登录或匿名访问";
    }

    [RelayCommand]
    private async Task OpenUrlAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SourceUrl))
        {
            StatusText = "请输入 URL";
            return;
        }

        IsBusy = true;
        try
        {
            ClearCapturedEntries();
            NetworkImageCache.Clear();
            ClearScenes();
            CurrentManifest = null;
            ManifestSummary = "尚未分析";
            ProgressValue = 0;
            ProgressText = string.Empty;
            Status = AnalysisStatus.Loading;
            StatusText = "正在打开作品页…";
            IsWebViewVisible = true;
            AppendLog($"打开网址：{SourceUrl.Trim()}");

            if (NavigateRequested is not null)
            {
                await NavigateRequested.Invoke(SourceUrl.Trim());
            }

            StatusText = "页面已打开。请等待全景加载，可切换场景，再点「分析」";
            AppendLog("已打开页面并开始捕获网络请求。请确认右侧能看到全景后再分析。");
            _settingsStore.AddRecentUrl(Settings, SourceUrl.Trim());
        }
        catch (Exception ex)
        {
            Status = AnalysisStatus.Failed;
            StatusText = "打开失败";
            AppendLog("打开网址失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SourceUrl))
        {
            StatusText = "请输入 URL";
            return;
        }

        var entriesBefore = SnapshotCapturedEntries();
        if (entriesBefore.Count == 0)
        {
            StatusText = "请先点击「打开网址」并等待全景加载";
            AppendLog("尚未捕获到网络请求。请先打开网址，确认全景可见后再分析。");
            return;
        }

        IsBusy = true;
        Status = AnalysisStatus.Analyzing;
        StatusText = "正在解析资源…";
        ProgressValue = 0;
        ProgressText = string.Empty;
        AppendLog($"开始分析（基于当前已捕获 {entriesBefore.Count} 条请求，不重新加载页面）");

        try
        {
            // 不清理捕获、不导航：保留用户浏览/切换场景时加载的瓦片
            ClearScenes();
            CurrentManifest = null;
            ManifestSummary = "分析中…";

            string? html = null;
            if (GetPageHtmlRequested is not null)
            {
                html = await GetPageHtmlRequested.Invoke();
            }

            string? pageMeta = null;
            if (GetPageMetaRequested is not null)
            {
                pageMeta = await GetPageMetaRequested.Invoke();
                if (!string.IsNullOrWhiteSpace(pageMeta))
                {
                    AppendLog("已从页面提取场景/缩略图元数据。");
                }
            }

            var entries = SnapshotCapturedEntries();
            AppendLog($"已捕获网络请求 {entries.Count} 条，图片缓存 {NetworkImageCache.Count} 张。");

            var result = _analysisService.Analyze(SourceUrl.Trim(), entries, html);
            if (result.NeedLogin)
            {
                Status = AnalysisStatus.NeedLogin;
                StatusText = "需要登录才能查看该作品";
                ManifestSummary = result.Message ?? "需要登录";
                AppendLog(result.Message ?? "需要登录");
                AppendLog("提示：公开可看的作品不会要求登录。若页面里其实能看全景，请稍等页面加载后再点「分析」。");
                return;
            }

            if (!result.Success || result.Manifest is null)
            {
                Status = AnalysisStatus.Failed;
                StatusText = "分析失败";
                ManifestSummary = result.Message ?? "分析失败";
                AppendLog(result.Message ?? "分析失败");
                return;
            }

            Yun720Detector.EnrichWithPageMeta(result.Manifest, pageMeta, entries);
            CurrentManifest = result.Manifest;

            var referer = SourceUrl.Trim();
            var loadTasks = new List<Task>();
            foreach (var scene in result.Manifest.Scenes)
            {
                var item = new SceneItemViewModel(scene);
                Scenes.Add(item);
                loadTasks.Add(item.LoadThumbnailAsync(referer));
                AppendLog(
                    $"场景「{item.Name}」[{scene.Id}] → " +
                    (string.IsNullOrWhiteSpace(scene.TileBaseUrl) ? "无瓦片根路径" : scene.TileBaseUrl));
            }

            var distinctBases = result.Manifest.Scenes
                .Select(s => s.TileBaseUrl?.TrimEnd('/'))
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            AppendLog($"瓦片根路径去重后 {distinctBases} 个（多场景应尽量各不相同）。");
            if (result.Manifest.Scenes.Count > 1 && distinctBases <= 1)
            {
                AppendLog("提示：多场景但只识别到 1 个瓦片目录。请在网页中逐一切换场景让瓦片加载，再重新「分析」。");
            }

            var thumbReady = result.Manifest.Scenes.Count(s => !string.IsNullOrWhiteSpace(s.ThumbnailUrl));
            AppendLog($"场景缩略图地址：{thumbReady}/{result.Manifest.Scenes.Count} 个已解析，正在加载预览…");

            RefreshVisibleScenes();
            Status = AnalysisStatus.Succeeded;
            StatusText = "分析完成";
            ManifestSummary = $"{result.Manifest.Title} · {result.Manifest.Scenes.Count} 个场景";
            AppendLog(result.Message ?? "分析完成");
            _settingsStore.AddRecentUrl(Settings, SourceUrl.Trim());

            await Task.WhenAll(loadTasks);
            var ok = Scenes.Count(s => s.Thumbnail is not null);
            AppendLog($"缩略图加载完成：{ok}/{Scenes.Count}");
            if (ok == 0 && thumbReady > 0)
            {
                AppendLog("缩略图地址有了但未能解码显示。请确认页面底部场景缩略图已出现后再分析。");
            }
        }
        catch (Exception ex)
        {
            Status = AnalysisStatus.Failed;
            StatusText = "分析异常";
            ManifestSummary = ex.Message;
            AppendLog("分析异常：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenLoginAsync()
    {
        AppendLog("打开 720 云登录弹窗…");
        Status = AnalysisStatus.NeedLogin;
        StatusText = "请在登录弹窗中完成登录";

        if (OpenLoginRequested is not null)
        {
            await OpenLoginRequested.Invoke("https://www.720yun.com/");
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        AppendLog("正在清除本地登录态…");
        if (ClearSessionRequested is not null)
        {
            await ClearSessionRequested.Invoke();
        }

        SetLoginStatus(false);
        StatusText = "已退出登录";
        AppendLog("本地 WebView2 Cookie/会话已清除。");
    }

    [RelayCommand]
    private Task ExportTilesAsync() => RunExportAsync(ExportMode.Tiles);

    [RelayCommand]
    private Task ExportEquirectAsync() => RunExportAsync(ExportMode.Equirect);

    [RelayCommand]
    private Task ExportBothAsync() => RunExportAsync(ExportMode.Both);

    [RelayCommand]
    private void CancelExport()
    {
        if (_exportCts is { IsCancellationRequested: false })
        {
            _exportCts.Cancel();
            AppendLog("正在取消导出…");
            ProgressText = "正在取消…";
        }
    }

    private async Task RunExportAsync(ExportMode mode)
    {
        if (IsBusy)
        {
            return;
        }

        if (CurrentManifest is null || SelectedScene is null)
        {
            AppendLog("请先分析作品并选择一个场景。");
            return;
        }

        var scene = SelectedScene.Scene;
        if (string.IsNullOrWhiteSpace(scene.TileBaseUrl) || scene.Levels.Count == 0)
        {
            AppendLog("当前场景没有瓦片结构。请等待右侧全景加载出画面后重新「分析」。");
            StatusText = "缺少瓦片信息";
            return;
        }

        IsBusy = true;
        _exportCts = new CancellationTokenSource();
        ProgressValue = 0;
        ProgressText = "准备导出…";
        AppendLog($"开始导出（{mode}）：{scene.Name} [{scene.Id}]");
        if (!string.IsNullOrWhiteSpace(scene.TileBaseUrl))
        {
            AppendLog("瓦片根路径：" + scene.TileBaseUrl);
        }

        try
        {
            Directory.CreateDirectory(Settings.DownloadDirectory);
            string? cookie = null;
            if (GetCookieHeaderRequested is not null)
            {
                cookie = await GetCookieHeaderRequested.Invoke(CurrentManifest.SourceUrl);
            }

            var options = new DownloadOptions
            {
                Referer = CurrentManifest.SourceUrl,
                Cookie = cookie,
                MaxConcurrency = Settings.MaxConcurrency,
                RetryCount = Settings.RetryCount,
                TimeoutSeconds = Settings.TimeoutSeconds,
                CancellationToken = _exportCts.Token,
                Progress = new Progress<DownloadProgress>(p =>
                {
                    ProgressValue = p.Percent;
                    ProgressText = p.Phase switch
                    {
                        "probe" => p.StatusText ?? "探测网格…",
                        "stitch" => p.StatusText ?? "拼接图像…",
                        _ => $"下载瓦片 {p.Completed}/{p.Total}（失败 {p.Failed}）"
                    };
                })
            };

            var result = await _exportService.ExportSceneAsync(
                CurrentManifest,
                scene,
                Settings.DownloadDirectory,
                mode,
                options,
                Settings.JpegQuality,
                Settings.MaxEquirectWidth);

            ProgressValue = 100;
            ProgressText = result.Message ?? "";
            AppendLog(result.Message ?? (result.Success ? "导出完成" : "导出失败"));
            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                AppendLog("注意：" + result.Warning);
            }

            if (result.FailedTileUrls.Count > 0)
            {
                var preview = string.Join(Environment.NewLine, result.FailedTileUrls.Take(8));
                AppendLog($"失败瓦片示例（共 {result.FailedTileUrls.Count}）：{Environment.NewLine}{preview}");
            }

            if (result.Success &&
                result.NeedsEquirectPreview &&
                mode is ExportMode.Equirect or ExportMode.Both &&
                result.FacePaths is { Count: > 0 } &&
                !string.IsNullOrWhiteSpace(result.EquirectPath) &&
                ShowEquirectPreviewRequested is not null)
            {
                AppendLog("打开整图预览，可手动调整贴图位置…");
                ProgressText = "等待预览确认…";
                var previewW = Math.Min(result.SuggestedEquirectWidth, 2048);
                var saved = await ShowEquirectPreviewRequested.Invoke(
                    result.FacePaths,
                    result.EquirectPath!,
                    previewW,
                    result.SuggestedEquirectWidth,
                    Settings.JpegQuality);
                if (saved)
                {
                    LastEquirectPath = result.EquirectPath;
                    AppendLog("整图已保存：" + result.EquirectPath);
                    ProgressText = "整图已保存";
                }
                else
                {
                    AppendLog("已取消保存整图（瓦片面图仍保留在输出目录）。");
                    ProgressText = "已取消整图保存";
                }
            }
            else if (result.Success &&
                     !string.IsNullOrWhiteSpace(result.EquirectPath) &&
                     File.Exists(result.EquirectPath) &&
                     mode is ExportMode.Equirect or ExportMode.Both)
            {
                LastEquirectPath = result.EquirectPath;
                AppendLog("整图已保存：" + result.EquirectPath);
            }

            if (result.Success && !string.IsNullOrWhiteSpace(result.OutputDirectory))
            {
                _settingsStore.AddRecentExport(Settings, new ExportHistoryItem
                {
                    SourceUrl = CurrentManifest.SourceUrl,
                    Title = CurrentManifest.Title,
                    SceneName = scene.Name,
                    OutputDirectory = result.OutputDirectory!,
                    ExportMode = mode.ToString(),
                    ExportedAt = DateTimeOffset.Now
                });

                if (Settings.OpenFolderAfterExport)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = result.OutputDirectory,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // ignore open folder failures
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("导出已取消。");
            ProgressText = "已取消";
        }
        catch (Exception ex)
        {
            AppendLog("导出异常：" + ex.Message);
            ProgressText = "导出失败";
        }
        finally
        {
            IsBusy = false;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    [RelayCommand]
    private void ToggleWebView()
    {
        IsWebViewVisible = !IsWebViewVisible;
    }

    [RelayCommand]
    private void OpenLastEquirect3D()
    {
        if (string.IsNullOrWhiteSpace(LastEquirectPath) || !File.Exists(LastEquirectPath))
        {
            AppendLog("尚无可用的整图。请先「导出整图」或「全部导出」并保存 equirect.jpg。");
            StatusText = "无整图可预览";
            return;
        }

        OpenEquirect3DRequested?.Invoke(LastEquirectPath);
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    [RelayCommand]
    private void OpenAbout() => OpenAboutRequested?.Invoke();

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
        AppendLog("日志已清空。");
    }

    [RelayCommand]
    private void CopyLog()
    {
        try
        {
            var text = string.Join(Environment.NewLine, LogLines);
            System.Windows.Clipboard.SetText(text);
            AppendLog("日志已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            AppendLog("复制日志失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private Task ExportDefaultAsync()
    {
        var mode = Settings.DefaultExportMode switch
        {
            "Tiles" => ExportMode.Tiles,
            "Equirect" => ExportMode.Equirect,
            _ => ExportMode.Both
        };
        return RunExportAsync(mode);
    }

    public void NotifySettingsSaved()
    {
        AppendLog(
            $"设置已保存：目录={Settings.DownloadDirectory}，并发={Settings.MaxConcurrency}，" +
            $"重试={Settings.RetryCount}，整图宽≤{Settings.MaxEquirectWidth}，默认导出={Settings.DefaultExportMode}");
    }

    public void AcceptDisclaimer()
    {
        Settings.DisclaimerAccepted = true;
        _settingsStore.Save(Settings);
    }
}

public sealed partial class SceneItemViewModel : ObservableObject
{
    public SceneItemViewModel(PanoramaScene scene)
    {
        Scene = scene;
        Name = string.IsNullOrWhiteSpace(scene.Name) ? scene.Id : scene.Name;
        Id = scene.Id;
        ThumbnailUrl = scene.ThumbnailUrl;
    }

    public PanoramaScene Scene { get; }

    public string Id { get; }

    public string Name { get; }

    public string? ThumbnailUrl { get; private set; }

    public string MetaSummary
    {
        get
        {
            if (Scene.Levels.Count == 0)
            {
                return string.IsNullOrWhiteSpace(Scene.TileBaseUrl)
                    ? "尚未解析到瓦片结构"
                    : "已有瓦片根路径，待补层级";
            }

            var top = Scene.Levels.OrderByDescending(l => l.Level).First();
            var tiles = EstimateTileCount(top);
            return $"{Scene.Projection} · l{top.Level} {top.Rows}×{top.Cols} · 约 {tiles} 瓦片";
        }
    }

    /// <summary>是否已探测到超过 1 个瓦片（可用于导出的有效场景）。</summary>
    public bool HasMultipleTiles
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Scene.TileBaseUrl) || Scene.Levels.Count == 0)
            {
                return false;
            }

            var top = Scene.Levels.OrderByDescending(l => l.Level).First();
            return EstimateTileCount(top) > 1;
        }
    }

    private int EstimateTileCount(PanoramaLevel top) =>
        string.Equals(Scene.Projection, "matrix-multires", StringComparison.OrdinalIgnoreCase)
            ? top.Rows * top.Cols
            : 6 * top.Rows * top.Cols;

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    [ObservableProperty]
    private bool _isThumbnailLoading;

    public async Task LoadThumbnailAsync(string referer)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl))
        {
            return;
        }

        await SetOnUiAsync(() => IsThumbnailLoading = true);
        try
        {
            var image = await ThumbnailImageLoader.LoadAsync(ThumbnailUrl, referer);
            image ??= await ThumbnailImageLoader.LoadAsync(ThumbnailUrl, "https://www.720yun.com/");
            await SetOnUiAsync(() =>
            {
                Thumbnail = image;
                IsThumbnailLoading = false;
            });
        }
        catch
        {
            await SetOnUiAsync(() =>
            {
                Thumbnail = null;
                IsThumbnailLoading = false;
            });
        }
    }

    private static Task SetOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
