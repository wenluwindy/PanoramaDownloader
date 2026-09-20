using System.Diagnostics;
using System.Reflection;
using System.Windows;
using PanoramaDownloader.App.Services;

namespace PanoramaDownloader.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "1.0.0";
        VersionText.Text = "版本 " + version;

        var (ok, detail) = WebView2RuntimeChecker.Probe();
        RuntimeStatusText.Text = ok
            ? "本机 WebView2 Runtime：" + detail
            : "未检测到可用的 WebView2 Runtime。" + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail);
        RuntimeStatusText.Foreground = ok
            ? System.Windows.Media.Brushes.DarkGreen
            : System.Windows.Media.Brushes.DarkRed;
    }

    private void OpenRuntime_Click(object sender, RoutedEventArgs e)
    {
        WebView2RuntimeChecker.OpenDownloadPage();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
