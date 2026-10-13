using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pandre.Core;
using Pandre.Desktop.Services;

namespace Pandre.Desktop.ViewModels;

/// <summary>文件列表的一行（PanFile + 展示辅助 + 缩略图懒加载）。</summary>
public partial class FileRowViewModel : ViewModelBase
{
    public FileRowViewModel(PanFile file)
    {
        File = file;
        SizeText = file.IsDirectory ? "—" : FmtSize(file.Size);
        ModifiedText = file.ModifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? "";
        if (file.ThumbnailUrl != null) _ = LoadThumbAsync(file.ThumbnailUrl);
    }

    public PanFile File { get; }
    public string SizeText { get; }
    public string ModifiedText { get; }

    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _thumb;

    private async Task LoadThumbAsync(string url)
    {
        try { Thumb = await ThumbCache.GetAsync(url); }
        catch { /* 缩略图失败不影响列表 */ }
    }

    private static string FmtSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / 1073741824.0:F1} GB",
        >= 1L << 20 => $"{b / 1048576.0:F1} MB",
        >= 1L << 10 => $"{b / 1024.0:F1} KB",
        _ => $"{b} B",
    };
}

/// <summary>单个网盘的浏览面板：导航 + 搜索 + 选择下载。</summary>
public partial class PanViewModel : ViewModelBase
{
    private readonly PanProvider _provider;
    private readonly DownloadsViewModel _downloads;

    // 目录栈（root=null 表示根目录）
    private readonly System.Collections.Generic.List<PanFile?> _dirStack = new() { null };
    private IReadOnlyList<PanFile> _currentItems = Array.Empty<PanFile>();

    public PanViewModel(PanProvider provider, DownloadsViewModel downloads)
    {
        _provider = provider;
        _downloads = downloads;
    }

    public string Title => _provider == PanProvider.Baidu ? "百度网盘" : "夸克网盘";

    public ObservableCollection<FileRowViewModel> Files { get; } = new();
    public ObservableCollection<FileRowViewModel> SelectedFiles { get; } = new();

    [ObservableProperty] private string _pathText = "/";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _canGoUp;

    private bool IsLoggedIn => _provider == PanProvider.Baidu
        ? AppServices.Baidu.IsAuthenticated : AppServices.Quark.IsAuthenticated;

    private IPanClient Client => _provider == PanProvider.Baidu
        ? (IPanClient)AppServices.Baidu : AppServices.Quark;

    /// <summary>进入此页面时（登录态可能刚变化）。</summary>
    public async Task ActivateAsync()
    {
        if (!IsLoggedIn)
        {
            StatusMessage = "未登录：请先到「登录」页扫码";
            Files.Clear();
            return;
        }
        if (Files.Count == 0) await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadCurrentAsync();

    [RelayCommand]
    private async Task UpAsync()
    {
        if (_dirStack.Count > 1)
        {
            _dirStack.RemoveAt(_dirStack.Count - 1);
            await LoadCurrentAsync();
        }
    }

    [RelayCommand]
    private async Task OpenAsync(FileRowViewModel? row)
    {
        if (row == null || !row.File.IsDirectory) return;
        _dirStack.Add(row.File);
        await LoadCurrentAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) { await LoadCurrentAsync(); return; }
        if (!IsLoggedIn) { StatusMessage = "未登录"; return; }
        IsLoading = true;
        StatusMessage = $"搜索「{SearchText}」…";
        try
        {
            var items = await Client.SearchAsync(SearchText.Trim(), CancellationToken.None);
            _currentItems = items;
            RebuildRows(items);
            PathText = $"搜索: {SearchText}";
            CanGoUp = true;
            StatusMessage = $"共 {items.Count} 条结果";
        }
        catch (PanException ex) { StatusMessage = $"搜索失败：{ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"搜索异常：{ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var files = SelectedFiles.Select(r => r.File).Where(f => !f.IsDirectory).ToArray();
        if (files.Length == 0) { StatusMessage = "请先勾选要下载的文件"; return; }
        if (!IsLoggedIn) { StatusMessage = "未登录"; return; }
        IsLoading = true;
        StatusMessage = "正在取下载链…";
        try
        {
            var links = await Client.GetDownloadLinksAsync(files);
            var dir = AppServices.DownloadDir;
            foreach (var l in links)
                await AppServices.Downloads.EnqueueAsync(l, dir);
            StatusMessage = $"已加入下载：{links.Count} 个文件 → {dir}";
            _downloads.Refresh();
        }
        catch (PanException ex) { StatusMessage = $"取下载链失败：{ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"下载加入失败：{ex.Message}"; }
        finally { IsLoading = false; }
    }

    private async Task LoadCurrentAsync()
    {
        if (!IsLoggedIn) { StatusMessage = "未登录"; return; }
        IsLoading = true;
        StatusMessage = "加载中…";
        try
        {
            var dir = _dirStack[^1];
            var items = await Client.ListAsync(dir, CancellationToken.None);
            _currentItems = items;
            RebuildRows(items);
            PathText = string.Join(" / ", _dirStack.Skip(0).Select(d => d?.Name ?? "根目录")).Replace("根目录", "/");
            CanGoUp = _dirStack.Count > 1;
            StatusMessage = $"共 {items.Count} 项";
        }
        catch (PanException ex) when (ex.IsAuthExpired)
        {
            StatusMessage = "登录已过期，请到「登录」页重新扫码";
            Files.Clear();
        }
        catch (PanException ex) { StatusMessage = $"加载失败：{ex.Message}"; }
        catch (Exception ex) { StatusMessage = $"加载异常：{ex.Message}"; }
        finally { IsLoading = false; }
    }

    private void RebuildRows(IReadOnlyList<PanFile> items)
    {
        Files.Clear();
        // 目录排前、文件名排序（服务端已排，但搜索/合并时保底）
        foreach (var f in items.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name))
            Files.Add(new FileRowViewModel(f));
    }
}
