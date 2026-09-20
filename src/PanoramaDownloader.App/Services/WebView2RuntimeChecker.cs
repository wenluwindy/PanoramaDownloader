using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace PanoramaDownloader.App.Services;

/// <summary>检测本机 Edge WebView2 Runtime 是否可用。</summary>
public static class WebView2RuntimeChecker
{
    public const string DownloadUrl =
        "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static (bool Ok, string Detail) Probe()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
            {
                return (false, "GetAvailableBrowserVersionString 返回空。");
            }

            return (true, version);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DownloadUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }
}
