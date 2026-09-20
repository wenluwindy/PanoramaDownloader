using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PanoramaDownloader.Core.Storage;

namespace PanoramaDownloader.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _target;
    private readonly SettingsStore _store;

    public SettingsWindow(AppSettings settings, SettingsStore store)
    {
        InitializeComponent();
        _target = settings;
        _store = store;

        DownloadDirBox.Text = settings.DownloadDirectory;
        ConcurrencyBox.Text = settings.MaxConcurrency.ToString();
        TimeoutBox.Text = settings.TimeoutSeconds.ToString();
        RetryBox.Text = settings.RetryCount.ToString();
        JpegBox.Text = settings.JpegQuality.ToString();
        MaxWidthBox.Text = settings.MaxEquirectWidth.ToString();
        OpenFolderCheck.IsChecked = settings.OpenFolderAfterExport;

        foreach (ComboBoxItem item in ExportModeBox.Items)
        {
            if (item.Tag is string tag &&
                string.Equals(tag, settings.DefaultExportMode, StringComparison.OrdinalIgnoreCase))
            {
                ExportModeBox.SelectedItem = item;
                break;
            }
        }

        if (ExportModeBox.SelectedItem is null)
        {
            ExportModeBox.SelectedIndex = 2;
        }
    }

    public bool Saved { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择下载目录"
        };
        if (!string.IsNullOrWhiteSpace(DownloadDirBox.Text) && Directory.Exists(DownloadDirBox.Text))
        {
            dialog.InitialDirectory = DownloadDirBox.Text;
        }

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            DownloadDirBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ConcurrencyBox.Text.Trim(), out var concurrency) ||
            !int.TryParse(TimeoutBox.Text.Trim(), out var timeout) ||
            !int.TryParse(RetryBox.Text.Trim(), out var retry) ||
            !int.TryParse(JpegBox.Text.Trim(), out var jpeg) ||
            !int.TryParse(MaxWidthBox.Text.Trim(), out var maxWidth))
        {
            MessageBox.Show(this, "请填写有效的数字设置项。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dir = DownloadDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            MessageBox.Show(this, "请选择下载目录。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法创建下载目录：" + ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _target.DownloadDirectory = dir;
        _target.MaxConcurrency = concurrency;
        _target.TimeoutSeconds = timeout;
        _target.RetryCount = retry;
        _target.JpegQuality = jpeg;
        _target.MaxEquirectWidth = maxWidth;
        _target.OpenFolderAfterExport = OpenFolderCheck.IsChecked == true;
        _target.DefaultExportMode = (ExportModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Both";
        SettingsStore.Clamp(_target);
        _store.Save(_target);

        // 刷新界面上的夹紧后数值
        ConcurrencyBox.Text = _target.MaxConcurrency.ToString();
        TimeoutBox.Text = _target.TimeoutSeconds.ToString();
        RetryBox.Text = _target.RetryCount.ToString();
        JpegBox.Text = _target.JpegQuality.ToString();
        MaxWidthBox.Text = _target.MaxEquirectWidth.ToString();

        Saved = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
