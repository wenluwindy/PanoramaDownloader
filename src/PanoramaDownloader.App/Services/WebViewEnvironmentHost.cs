using System.IO;
using Microsoft.Web.WebView2.Core;

namespace PanoramaDownloader.App.Services;

/// <summary>
/// 主窗口与登录弹窗共用同一 WebView2 环境，共享 Cookie / 登录态。
/// </summary>
public static class WebViewEnvironmentHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CoreWebView2Environment? _environment;
    private static string? _userDataFolder;

    public static async Task<CoreWebView2Environment> GetAsync(string userDataFolder)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_environment is not null &&
                string.Equals(_userDataFolder, userDataFolder, StringComparison.OrdinalIgnoreCase))
            {
                return _environment;
            }

            Directory.CreateDirectory(userDataFolder);
            _userDataFolder = userDataFolder;
            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder)
                .ConfigureAwait(false);
            return _environment;
        }
        finally
        {
            Gate.Release();
        }
    }
}
