using Avalonia.Controls;
using Avalonia.Input;
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
}
