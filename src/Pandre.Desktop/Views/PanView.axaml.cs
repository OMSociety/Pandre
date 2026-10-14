using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Pandre.Desktop.ViewModels;

namespace Pandre.Desktop.Views;

public partial class PanView : UserControl
{
    public PanView() { InitializeComponent(); }

    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not PanViewModel vm) return;
        if (FileList.SelectedItem is FileRowViewModel row && row.File.IsDirectory)
            await vm.OpenCommand.ExecuteAsync(row);
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is PanViewModel vm)
            await vm.SearchCommand.ExecuteAsync(null);
    }

    /// <summary>上传：系统文件选择器（可多选）→ VM 逐文件走五段链/秒传。</summary>
    private async void OnUploadClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PanViewModel vm) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传的文件",
            AllowMultiple = true,
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToArray();
        if (paths.Length > 0) await vm.UploadFilesAsync(paths);
    }
}
