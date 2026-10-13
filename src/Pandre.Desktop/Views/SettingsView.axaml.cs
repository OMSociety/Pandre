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
}
