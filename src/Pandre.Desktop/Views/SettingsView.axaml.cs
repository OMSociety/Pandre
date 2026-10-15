using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Pandre.Desktop.ViewModels;

namespace Pandre.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() { InitializeComponent(); }

    /// <summary>选下载目录：系统文件夹选择器，选中后写回视图模型（由它持久化）。</summary>
    private async void OnBrowseDownloadDir(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择下载目录",
            AllowMultiple = false,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) vm.DownloadDir = path;
    }

    /// <summary>用系统默认浏览器打开外部链接（按钮 Tag 决定目标：repo=仓库，author=开发者主页）。</summary>
    private async void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string target }) return;
        var url = target switch
        {
            "repo" => Services.AppServices.RepositoryUrl,
            "author" => Services.AppServices.AuthorUrl,
            _ => null,
        };
        if (url is null) return;
        if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher) return;
        await launcher.LaunchUriAsync(new System.Uri(url));
    }

    /// <summary>「安装扩展」：复制扩展文件到 AppData、写入端口/令牌，然后打开所在文件夹。</summary>
    private void OnInstallExtensionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.InstallExtension();
    }
}
