using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PanoramaDownloader.App.Services;
using PanoramaDownloader.App.ViewModels;
using PanoramaDownloader.App.Views;
using PanoramaDownloader.Core.Models;
using PanoramaDownloader.Core.Services;

namespace PanoramaDownloader.App;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private bool _webViewReady;
    private readonly HashSet<string> _hookedRequestIds = [];
    private readonly Dictionary<string, string> _pendingBodyRequestIds = new(StringComparer.Ordinal);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Settings.DisclaimerAccepted)
        {
            var dialog = new DisclaimerWindow { Owner = this };
            var result = dialog.ShowDialog();
            if (result != true || !dialog.Accepted)
            {
                Close();
                return;
            }

            ViewModel.AcceptDisclaimer();
        }

        ViewModel.NavigateRequested += NavigateAsync;
        ViewModel.GetPageHtmlRequested += GetPageHtmlAsync;
        ViewModel.GetPageMetaRequested += GetPageMetaAsync;
        ViewModel.ClearSessionRequested += ClearSessionAsync;
        ViewModel.OpenLoginRequested += OpenLoginWindowAsync;
        ViewModel.GetCookieHeaderRequested += GetCookieHeaderAsync;
        ViewModel.ShowEquirectPreviewRequested += ShowEquirectPreviewAsync;
        ViewModel.OpenSettingsRequested += OpenSettingsDialog;
        ViewModel.OpenAboutRequested += OpenAboutDialog;
        ViewModel.OpenEquirect3DRequested += OpenEquirect3D;

        var (runtimeOk, runtimeDetail) = WebView2RuntimeChecker.Probe();
        if (!runtimeOk)
        {
            var ask = MessageBox.Show(
                this,
                "未检测到 Microsoft Edge WebView2 Runtime，无法加载作品页面。\n\n" +
                (string.IsNullOrWhiteSpace(runtimeDetail) ? "" : runtimeDetail + "\n\n") +
                "是否打开官方下载页？",
                "缺少 WebView2 Runtime",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (ask == MessageBoxResult.Yes)
            {
                WebView2RuntimeChecker.OpenDownloadPage();
            }

            WebViewStatusText.Text = "WebView2 Runtime 未安装";
            ViewModel.AppendLog("WebView2 Runtime 不可用：" + runtimeDetail);
            return;
        }

        await InitializeWebViewAsync();
    }

    private void OpenSettingsDialog()
    {
        var dialog = new SettingsWindow(ViewModel.Settings, ViewModel.SettingsStore)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.Saved)
        {
            ViewModel.NotifySettingsSaved();
        }
    }

    private void OpenAboutDialog()
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void OpenEquirect3D(string equirectPath)
    {
        var win = Panorama3DPreviewWindow.TryCreateFromFile(equirectPath, this);
        if (win is null)
        {
            MessageBox.Show(this, "无法加载整图进行 3D 预览。", "3D 预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        win.Show();
        ViewModel.AppendLog("已打开 3D 预览：" + equirectPath);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userData = ViewModel.WebViewUserDataFolder;
            var env = await WebViewEnvironmentHost.GetAsync(userData);
            await Browser.EnsureCoreWebView2Async(env);

            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = true;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = true;

            Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Browser.CoreWebView2.WebResourceResponseReceived += CoreWebView2_WebResourceResponseReceived;

            // 启用网络相关事件以便捕获请求元数据
            await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            Browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.responseReceived")
                .DevToolsProtocolEventReceived += OnNetworkResponseReceived;
            Browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.loadingFinished")
                .DevToolsProtocolEventReceived += OnNetworkLoadingFinished;

            _webViewReady = true;
            WebViewStatusText.Text = "WebView2 已就绪（登录态将保存在本地用户目录）";
            ViewModel.AppendLog($"WebView2 用户数据目录：{userData}");
            ThumbnailImageLoader.WebFetchAsync = FetchImageBytesAsync;
            ThumbnailImageLoader.CookieHeaderAsync = GetCookieHeaderAsync;

            Browser.CoreWebView2.Navigate("https://www.720yun.com/");
        }
        catch (Exception ex)
        {
            WebViewStatusText.Text = "WebView2 初始化失败，请安装 Edge WebView2 Runtime。";
            ViewModel.AppendLog("WebView2 初始化失败：" + ex.Message);
            MessageBox.Show(
                this,
                "无法初始化 WebView2。请安装 Microsoft Edge WebView2 Runtime 后重试。\n\n" + ex.Message,
                "初始化失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var url = Browser.CoreWebView2?.Source ?? string.Empty;
        WebViewStatusText.Text = e.IsSuccess
            ? $"已加载：{url}"
            : $"加载失败：{url}";

        var seemsLoginPage = url.Contains("login", StringComparison.OrdinalIgnoreCase);
        ViewModel.SetLoginStatus(!seemsLoginPage && url.Contains("720yun", StringComparison.OrdinalIgnoreCase));
    }

    private async void CoreWebView2_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var request = e.Request;
            var response = e.Response;
            var url = request.Uri;
            if (!ShouldCapture(url))
            {
                return;
            }

            string? body = null;
            string? contentType = null;
            try
            {
                foreach (var header in e.Response.Headers)
                {
                    if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        contentType = header.Value;
                        break;
                    }
                }
            }
            catch
            {
                contentType = null;
            }

            var isImage = contentType is not null && contentType.Contains("image", StringComparison.OrdinalIgnoreCase);
            var isTextLike = contentType is not null &&
                             (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                              contentType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
                              contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                              contentType.Contains("xml", StringComparison.OrdinalIgnoreCase));

            if (isImage)
            {
                try
                {
                    using var stream = await response.GetContentAsync();
                    if (stream is not null)
                    {
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        var bytes = ms.ToArray();
                        if (bytes.Length > 0)
                        {
                            NetworkImageCache.Set(url, bytes);
                        }
                    }
                }
                catch
                {
                    // 部分图片响应不可读
                }
            }
            else if (isTextLike)
            {
                try
                {
                    using var stream = await response.GetContentAsync();
                    if (stream is not null)
                    {
                        using var reader = new StreamReader(stream);
                        body = await reader.ReadToEndAsync();
                        if (body.Length > 512_000)
                        {
                            body = body[..512_000];
                        }
                    }
                }
                catch
                {
                    // 部分响应不可读，忽略正文
                }
            }

            ViewModel.AddCapturedEntry(new CapturedNetworkEntry
            {
                Url = url,
                Method = request.Method,
                StatusCode = response.StatusCode,
                ContentType = contentType,
                BodyText = body
            });
        }
        catch
        {
            // 捕获过程不影响浏览
        }
    }

    private void OnNetworkResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("response", out var response))
            {
                return;
            }

            var url = response.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || !ShouldCapture(url))
            {
                return;
            }

            var status = response.TryGetProperty("status", out var statusProp) ? statusProp.GetInt32() : (int?)null;
            var mime = response.TryGetProperty("mimeType", out var mimeProp) ? mimeProp.GetString() : null;
            var requestId = root.TryGetProperty("requestId", out var idProp) ? idProp.GetString() : null;
            if (!string.IsNullOrEmpty(requestId))
            {
                _hookedRequestIds.Add(requestId);
                if (ShouldFetchResponseBody(url, mime, status))
                {
                    _pendingBodyRequestIds[requestId] = url;
                }
            }

            ViewModel.AddCapturedEntry(new CapturedNetworkEntry
            {
                Url = url,
                StatusCode = status,
                ContentType = mime
            });
        }
        catch
        {
            // ignore CDP parse errors
        }
    }

    private async void OnNetworkLoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        string? requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            if (!doc.RootElement.TryGetProperty("requestId", out var idProp))
            {
                return;
            }

            requestId = idProp.GetString();
            if (string.IsNullOrEmpty(requestId) ||
                !_pendingBodyRequestIds.TryGetValue(requestId, out var url))
            {
                return;
            }

            _pendingBodyRequestIds.Remove(requestId);
            if (Browser.CoreWebView2 is null)
            {
                return;
            }

            var raw = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Network.getResponseBody",
                JsonSerializer.Serialize(new { requestId }));

            using var bodyDoc = JsonDocument.Parse(raw);
            if (!bodyDoc.RootElement.TryGetProperty("body", out var bodyProp))
            {
                return;
            }

            var body = bodyProp.GetString();
            var base64 = bodyDoc.RootElement.TryGetProperty("base64Encoded", out var b64) &&
                         b64.ValueKind == JsonValueKind.True;
            if (base64 || string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            if (body.Length > 512_000)
            {
                body = body[..512_000];
            }

            // 仅保留 JSON / 含瓦片线索的文本，避免日志膨胀
            var trimmed = body.TrimStart();
            if (!(trimmed.StartsWith('{') || trimmed.StartsWith('[') ||
                  body.Contains("/imgs/", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            ViewModel.AddCapturedEntry(new CapturedNetworkEntry
            {
                Url = url,
                StatusCode = 200,
                ContentType = "application/json",
                BodyText = body
            });
        }
        catch
        {
            if (!string.IsNullOrEmpty(requestId))
            {
                _pendingBodyRequestIds.Remove(requestId);
            }
        }
    }

    private static bool ShouldFetchResponseBody(string url, string? mime, int? status)
    {
        if (status is >= 400)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(url) || !ShouldCapture(url))
        {
            return false;
        }

        // 图片瓦片本身不需要 body
        if (url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
            url.Contains(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lower = url.ToLowerInvariant();
        if (lower.Contains("/api/") ||
            lower.Contains("720yun") ||
            lower.Contains("720static") ||
            lower.Contains("work") ||
            lower.Contains("pano") ||
            lower.Contains("scene") ||
            lower.Contains("product"))
        {
            return true;
        }

        return mime is not null &&
               (mime.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                mime.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                mime.Contains("text", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldCapture(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // 过滤常见静态噪声，保留 720 相关与媒体
        if (url.Contains("google-analytics", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("googletagmanager", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("favicon", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private Task<bool> ShowEquirectPreviewAsync(
        IReadOnlyDictionary<string, string> facePaths,
        string outputPath,
        int previewWidth,
        int finalWidth,
        int jpegQuality)
    {
        var window = new EquirectPreviewWindow(facePaths, outputPath, previewWidth, finalWidth, jpegQuality)
        {
            Owner = this
        };
        var result = window.ShowDialog();
        return Task.FromResult(result == true && window.Saved);
    }

    private async Task OpenLoginWindowAsync(string url)
    {
        ViewModel.AppendLog("打开登录弹窗…");
        ViewModel.StatusText = "请在登录弹窗中完成登录";

        var loginWindow = new LoginBrowserWindow(ViewModel.WebViewUserDataFolder, url)
        {
            Owner = this
        };

        var result = loginWindow.ShowDialog();
        if (result == true || loginWindow.Completed)
        {
            ViewModel.SetLoginStatus(true);
            ViewModel.AppendLog("登录弹窗已关闭。正在刷新主页面以同步登录态…");
            WebViewStatusText.Text = "登录完成，正在刷新…";

            if (_webViewReady && Browser.CoreWebView2 is not null)
            {
                // 重新导航以带上弹窗中写入的 Cookie
                var target = string.IsNullOrWhiteSpace(ViewModel.SourceUrl)
                    ? "https://www.720yun.com/"
                    : ViewModel.SourceUrl.Trim();
                Browser.CoreWebView2.Navigate(target);
                await Task.Delay(800);
            }

            ViewModel.StatusText = "登录流程结束，可重新点击「分析」";
        }
        else
        {
            ViewModel.AppendLog("已关闭登录弹窗。");
        }
    }

    private async Task NavigateAsync(string url)
    {
        if (!_webViewReady || Browser.CoreWebView2 is null)
        {
            ViewModel.AppendLog("WebView2 尚未就绪。");
            return;
        }

        ViewModel.IsWebViewVisible = true;
        WebViewStatusText.Text = $"正在打开：{url}";

        var current = Browser.CoreWebView2.Source ?? string.Empty;
        if (string.Equals(current.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            // 同 URL 强制刷新，确保缩略图/瓦片请求再次进入网络缓存
            Browser.CoreWebView2.Reload();
        }
        else
        {
            Browser.CoreWebView2.Navigate(url);
        }

        await Task.Delay(800);
    }

    private async Task<string?> GetPageHtmlAsync()
    {
        if (!_webViewReady || Browser.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
            return WebViewScriptResult.ToText(raw);
        }
        catch (Exception ex)
        {
            ViewModel.AppendLog("读取页面 HTML 失败：" + ex.Message);
            return null;
        }
    }

    private static string? ParseScriptResult(string? raw) => WebViewScriptResult.ToText(raw);

    private async Task<string?> GetPageMetaAsync()
    {
        if (!_webViewReady || Browser.CoreWebView2 is null)
        {
            return null;
        }

        // 1) 播放器 API  2) itemsWp / ImageMenuItem 全量菜单  3) 底部条兜底
        const string script = """
            (async () => {
              const result = { scenes: [], images: [], menuCount: 0, source: '' };

              const absUrl = (u) => {
                if (!u) return '';
                u = String(u).trim().replace(/^["']|["']$/g, '');
                if (u.startsWith('//')) u = 'https:' + u;
                return u;
              };

              const bgUrl = (el) => {
                if (!el) return '';
                const style = el.getAttribute('style') || '';
                let m = /background-image\s*:\s*url\(\s*(&quot;|["']?)(.*?)\1\s*\)/i.exec(style);
                if (m && m[2]) return absUrl(m[2].replace(/&quot;/g, '"').replace(/&gt;/g, '').replace(/&lt;/g, ''));
                m = /background-image\s*:\s*url\(\s*["']?(.*?)["']?\s*\)/i.exec(style);
                if (m && m[1]) return absUrl(m[1].replace(/&quot;/g, '"').replace(/&gt;/g, '').replace(/&lt;/g, ''));
                try {
                  const bg = getComputedStyle(el).backgroundImage || '';
                  m = /url\(\s*["']?(.*?)["']?\s*\)/i.exec(bg);
                  if (m && m[1] && m[1] !== 'none') return absUrl(m[1]);
                } catch (e) {}
                return '';
              };

              const resourceIdFromThumb = (thumb) => {
                const m = /\/([A-Za-z0-9_-]+)\/imgs\/(?:thumb\.[A-Za-z0-9]+)?/i.exec(thumb || '');
                return m ? m[1] : '';
              };

              const pushScene = (list, item) => {
                if (!item) return;
                const thumb = absUrl(item.thumbUrl || '');
                const name = (item.name || '').trim().replace(/\s+/g, ' ').slice(0, 80);
                let id = String(item.id || resourceIdFromThumb(thumb) || '').trim();
                if (!id && !thumb && !name) return;
                if (!id) id = 'menu_' + list.length;
                const key = thumb || id;
                if (list._keys.has(key) || list._ids.has(id)) {
                  // 同 id 时补缩略图/名
                  const existed = list.find(s => s.id === id || (thumb && s.thumbUrl === thumb));
                  if (existed) {
                    if (!existed.thumbUrl && thumb) existed.thumbUrl = thumb;
                    if ((!existed.name || existed.name === existed.id) && name) existed.name = name;
                    if (!existed.path && thumb) existed.path = thumb;
                  }
                  return;
                }
                list._keys.add(key);
                list._ids.add(id);
                list.push({
                  id,
                  name: name || id,
                  thumbUrl: thumb,
                  path: thumb || item.path || '',
                  source: item.source || 'dom'
                });
              };

              const scrapeItemsWp = () => {
                const list = [];
                list._keys = new Set();
                list._ids = new Set();

                const collectOnce = () => {
                  const roots = Array.from(document.querySelectorAll('.itemsWp, [class*="itemsWp"]'));
                  const scope = roots.length > 0 ? roots : [document];
                  scope.forEach(root => {
                    const items = root.querySelectorAll
                      ? root.querySelectorAll('[class*="ImageMenuItem"]')
                      : [];
                    items.forEach((item) => {
                      const content = item.querySelector('[class*="ImageMenuContent"]') || item;
                      let thumb = bgUrl(content);
                      if (!thumb) {
                        const img = item.querySelector('img[src*="720static"], img[src*="/imgs/"]');
                        if (img) thumb = absUrl(img.currentSrc || img.src);
                      }
                      const nameEl = item.querySelector('[class*="ImageMenuText"] .MarqueeDefault, .MarqueeDefault, [class*="ImageMenuText"], [class*="Marquee"]');
                      const name = ((nameEl && (nameEl.innerText || nameEl.textContent)) || item.getAttribute('data-tip') || '').trim();
                      const tip = item.getAttribute('data-tip') || '';
                      pushScene(list, {
                        id: resourceIdFromThumb(thumb),
                        name: name || tip,
                        thumbUrl: thumb,
                        path: thumb,
                        source: 'itemsWp'
                      });
                    });

                    if (items.length === 0) {
                      const nodes = root.querySelectorAll
                        ? root.querySelectorAll('[style*="background-image"], [class*="ImageMenuContent"]')
                        : [];
                      nodes.forEach(node => {
                        const thumb = bgUrl(node);
                        if (!thumb || thumb.indexOf('/imgs/') < 0) return;
                        const wrap = node.closest('[data-tip]') || node.parentElement || node;
                        const name = (wrap.getAttribute && wrap.getAttribute('data-tip')) ||
                          ((wrap.innerText || '').trim().split('\\n')[0] || '');
                        pushScene(list, {
                          id: resourceIdFromThumb(thumb),
                          name,
                          thumbUrl: thumb,
                          path: thumb,
                          source: 'itemsWp'
                        });
                      });
                    }
                  });
                };

                // 先点一遍可能的选项卡，再滚动列表，尽量让虚拟列表把节点挂出来
                try {
                  const tabLike = document.querySelectorAll(
                    '.itemsWp [class*="tab"], .itemsWp [class*="Tab"], [class*="itemsWp"] [class*="tab"], [class*="itemsWp"] [class*="Tab"]'
                  );
                  tabLike.forEach(t => { try { t.click(); } catch (e) {} });
                } catch (e) {}

                collectOnce();

                const scrollers = Array.from(document.querySelectorAll(
                  '.itemsWp, [class*="itemsWp"], .itemsWp *, [class*="itemsWp"] *'
                )).filter(el => {
                  try {
                    const st = getComputedStyle(el);
                    const oy = st.overflowY;
                    return (oy === 'auto' || oy === 'scroll') && el.scrollHeight > el.clientHeight + 20;
                  } catch (e) { return false; }
                }).slice(0, 6);

                scrollers.forEach(scroller => {
                  let last = -1;
                  for (let i = 0; i < 40; i++) {
                    collectOnce();
                    scroller.scrollTop = Math.min(scroller.scrollHeight, scroller.scrollTop + Math.max(80, scroller.clientHeight * 0.8));
                    if (scroller.scrollTop === last) break;
                    last = scroller.scrollTop;
                  }
                  scroller.scrollTop = 0;
                });

                collectOnce();
                delete list._keys;
                delete list._ids;
                return list;
              };

              try {
                const api = window.player && window.player.openApi;
                if (api && typeof api.getSceneList === 'function') {
                  const list = await api.getSceneList();
                  if (Array.isArray(list)) {
                    result.apiScenes = list.map(s => ({
                      id: String(s.id ?? s.partnerId ?? s.sceneId ?? ''),
                      name: s.name || s.title || '',
                      thumbUrl: s.thumbUrl || s.thumb || s.cover || s.coverUrl || '',
                      path: s.path || s.imgPath || s.tilePath || s.panoPath || s.resourcePath ||
                            s.imgs || s.texturePath || s.materialPath || s.cubePath || s.basePath || '',
                      source: 'playerApi'
                    }));
                  }
                }
              } catch (e) {
                result.playerError = String(e);
              }

              try {
                const menu = scrapeItemsWp();
                result.menuCount = menu.length;
                result.scenes = menu;
                result.source = menu.length > 0 ? 'itemsWp' : '';
              } catch (e) {
                result.menuError = String(e);
              }

              // 菜单为空时回退到播放器 API
              if (result.scenes.length === 0 && Array.isArray(result.apiScenes)) {
                result.scenes = result.apiScenes;
                result.source = 'playerApi';
              } else if (Array.isArray(result.apiScenes) && result.apiScenes.length > 0) {
                // 用 API 补全菜单项缺失的 path/name（按 id / 名）
                const byId = new Map(result.apiScenes.map(s => [s.id, s]));
                result.scenes.forEach(s => {
                  const a = byId.get(s.id);
                  if (!a) return;
                  if (!s.path && a.path) s.path = a.path;
                  if ((!s.name || s.name === s.id) && a.name) s.name = a.name;
                  if (!s.thumbUrl && a.thumbUrl) s.thumbUrl = a.thumbUrl;
                });
              }

              try {
                const vh = window.innerHeight || document.documentElement.clientHeight || 800;
                const candidates = [];
                document.querySelectorAll('img').forEach(img => {
                  const src = img.currentSrc || img.src || img.getAttribute('data-src') || '';
                  if (!src || src.startsWith('data:')) return;
                  const rect = img.getBoundingClientRect();
                  if (rect.width < 36 || rect.width > 220 || rect.height < 36 || rect.height > 220) return;
                  if (rect.bottom < vh - 220 || rect.top > vh - 20) return;
                  const label = (img.alt || img.title || img.getAttribute('aria-label') ||
                    (img.parentElement && (img.parentElement.innerText || img.parentElement.textContent)) || '')
                    .trim().replace(/\s+/g, ' ').slice(0, 80);
                  candidates.push({
                    src: absUrl(src),
                    alt: label,
                    x: Math.round(rect.left),
                    y: Math.round(rect.top)
                  });
                });
                candidates.sort((a, b) => a.x - b.x || a.y - b.y);
                result.images = candidates;

                if (result.scenes.length === 0 && candidates.length > 0) {
                  result.scenes = candidates.map((c, i) => ({
                    id: resourceIdFromThumb(c.src) || ('dom_' + i),
                    name: c.alt || ('场景' + (i + 1)),
                    thumbUrl: c.src,
                    path: c.src,
                    source: 'bottomBar'
                  }));
                  result.source = 'bottomBar';
                }
              } catch (e) {
                result.domError = String(e);
              }

              return result;
            })()
            """;

        try
        {
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync(script);
            var text = WebViewScriptResult.ToText(raw);
            if (string.IsNullOrWhiteSpace(text))
            {
                ViewModel.AppendLog("页面缩略图元数据为空。");
                return null;
            }

            // 统计菜单场景数量便于诊断
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                var sceneCount = 0;
                var menuCount = 0;
                var source = "";
                if (doc.RootElement.TryGetProperty("scenes", out var scenes))
                {
                    sceneCount = scenes.GetArrayLength();
                }

                if (doc.RootElement.TryGetProperty("menuCount", out var mc) &&
                    mc.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    menuCount = mc.GetInt32();
                }

                if (doc.RootElement.TryGetProperty("source", out var src))
                {
                    source = src.GetString() ?? "";
                }

                ViewModel.AppendLog($"页面元数据：来源 {source}，菜单项 {menuCount}，场景 {sceneCount}。");
            }
            catch
            {
                // ignore
            }

            return text;
        }
        catch (Exception ex)
        {
            ViewModel.AppendLog("提取页面缩略图元数据失败：" + ex.Message);
            return null;
        }
    }

    private Task<string?> GetCookieHeaderAsync(string url)
    {
        if (!_webViewReady || Browser.CoreWebView2 is null)
        {
            return Task.FromResult<string?>(null);
        }

        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => GetCookieHeaderOnUiAsync(url)).Task.Unwrap();
        }

        return GetCookieHeaderOnUiAsync(url);
    }

    private async Task<string?> GetCookieHeaderOnUiAsync(string url)
    {
        try
        {
            var uri = new Uri(url);
            var cookies = await Browser.CoreWebView2!.CookieManager.GetCookiesAsync(uri.GetLeftPart(UriPartial.Authority));
            if (cookies.Count == 0)
            {
                cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.720yun.com");
            }

            if (cookies.Count == 0)
            {
                return null;
            }

            return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        catch
        {
            return null;
        }
    }

    private Task<byte[]?> FetchImageBytesAsync(string url)
    {
        if (!_webViewReady || Browser.CoreWebView2 is null || string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult<byte[]?>(null);
        }

        // WebView2 脚本必须在 UI 线程执行
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => FetchImageBytesOnUiAsync(url)).Task.Unwrap();
        }

        return FetchImageBytesOnUiAsync(url);
    }

    private async Task<byte[]?> FetchImageBytesOnUiAsync(string url)
    {
        var script = $$"""
            (async () => {
              try {
                const res = await fetch({{JsonSerializer.Serialize(url)}}, {
                  credentials: 'include',
                  mode: 'cors',
                  cache: 'force-cache'
                });
                if (!res.ok) return null;
                const buf = await res.arrayBuffer();
                const bytes = new Uint8Array(buf);
                let binary = '';
                const chunk = 0x8000;
                for (let i = 0; i < bytes.length; i += chunk) {
                  binary += String.fromCharCode.apply(null, bytes.subarray(i, Math.min(i + chunk, bytes.length)));
                }
                return btoa(binary);
              } catch (e) {
                return null;
              }
            })()
            """;

        try
        {
            var raw = await Browser.CoreWebView2!.ExecuteScriptAsync(script);
            var b64 = WebViewScriptResult.ToText(raw);
            if (string.IsNullOrWhiteSpace(b64))
            {
                return null;
            }

            return Convert.FromBase64String(b64);
        }
        catch
        {
            return null;
        }
    }

    private async Task ClearSessionAsync()
    {
        if (!_webViewReady || Browser.CoreWebView2 is null)
        {
            return;
        }

        await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
        Browser.CoreWebView2.Navigate("https://www.720yun.com/");
        await Task.Delay(300);
    }
}
