using System.Windows;
using Microsoft.Web.WebView2.Core;
using PanoramaDownloader.App.Services;

namespace PanoramaDownloader.App.Views;

public partial class LoginBrowserWindow : Window
{
    private readonly string _userDataFolder;
    private readonly string _startUrl;
    private bool _ready;

    public LoginBrowserWindow(string userDataFolder, string? startUrl = null)
    {
        _userDataFolder = userDataFolder;
        _startUrl = string.IsNullOrWhiteSpace(startUrl)
            ? "https://www.720yun.com/"
            : startUrl;
        InitializeComponent();
        Loaded += LoginBrowserWindow_Loaded;
        Closed += LoginBrowserWindow_Closed;
    }

    public bool Completed { get; private set; }

    private async void LoginBrowserWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await WebViewEnvironmentHost.GetAsync(_userDataFolder);
            await Browser.EnsureCoreWebView2Async(env);

            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = true;
            Browser.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                var url = Browser.CoreWebView2?.Source ?? string.Empty;
                StatusText.Text = args.IsSuccess
                    ? $"当前页面：{url}"
                    : $"加载失败：{url}";
            };

            _ready = true;
            StatusText.Text = "登录页已打开，可拖动/放大本窗口后输入账号密码。";
            Browser.CoreWebView2.Navigate(_startUrl);
        }
        catch (Exception ex)
        {
            StatusText.Text = "登录窗口初始化失败：" + ex.Message;
            MessageBox.Show(
                this,
                "无法打开登录页。请确认已安装 WebView2 Runtime。\n\n" + ex.Message,
                "登录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void LoginBrowserWindow_Closed(object? sender, EventArgs e)
    {
        try
        {
            Browser.Dispose();
        }
        catch
        {
            // ignore dispose errors
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_ready && Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.Reload();
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        Completed = true;
        DialogResult = true;
        Close();
    }
}
