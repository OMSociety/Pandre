using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pandre.Core;
using Pandre.Download;
using Pandre.Desktop.Services;

namespace Pandre.Desktop.ViewModels;

/// <summary>下载列表的一行。</summary>
public partial class DownloadRowViewModel : ViewModelBase
{
    private readonly DownloadsViewModel _owner;

    public DownloadRowViewModel(DownloadTask task, DownloadsViewModel owner) { Task = task; _owner = owner; Refresh(); }

    public DownloadTask Task { get; }
    public string Name => Task.FileName;
    public string Dir => Task.SaveDir;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _isSelected;

    /// <summary>已完成：删除按钮走「询问是否删本地文件」的菜单，而不是直接删。</summary>
    public bool IsComplete => Task.Status == DownloadStatus.Complete;

    public void Refresh()
    {
        var t = Task;
        Progress = t.TotalBytes > 0 ? t.Progress * 100.0 : 0;
        SpeedText = t.Status == DownloadStatus.Downloading && t.SpeedBps > 0
            ? Fmt.Size(t.SpeedBps) + "/s" : "";
        SizeText = t.TotalBytes > 0
            ? $"{Fmt.Size(t.CompletedBytes)} / {Fmt.Size(t.TotalBytes)}" : "";
        StatusText = t.Status switch
        {
            DownloadStatus.Queued => "排队",
            DownloadStatus.Downloading => "下载中",
            DownloadStatus.Paused => "已暂停",
            DownloadStatus.Complete => "完成",
            DownloadStatus.Error => "出错" + (t.ErrorMessage != null ? $"：{t.ErrorMessage}" : ""),
            DownloadStatus.Removed => "已移除",
            _ => "",
        };
        CanPause = t.Status is DownloadStatus.Downloading or DownloadStatus.Queued;
        CanResume = t.Status == DownloadStatus.Paused;
        OnPropertyChanged(nameof(IsComplete));
    }

    /// <summary>已完成任务的删除：仅移除记录。命令放行级，弹出的菜单绑定不到外层 DataContext。</summary>
    [RelayCommand] private System.Threading.Tasks.Task RemoveKeepAsync() => _owner.RemoveRowAsync(this, deleteLocalFiles: false);

    /// <summary>已完成任务的删除：连本地文件一起删。</summary>
    [RelayCommand] private System.Threading.Tasks.Task RemoveWithFilesAsync() => _owner.RemoveRowAsync(this, deleteLocalFiles: true);
}

/// <summary>云端离线下载任务的一行（只读展示）。</summary>
public sealed class OfflineTaskRowViewModel : ViewModelBase
{
    public OfflineTaskRowViewModel(PanOfflineTask task) { Task = task; }

    public PanOfflineTask Task { get; }
    public string Name => Task.Name;
    public string SavePath => Task.SavePath;
    public string SizeText => Task.SizeBytes > 0
        ? $"{Fmt.Size(Task.FinishedBytes)} / {Fmt.Size(Task.SizeBytes)}" : "";
    public string StatusText => Task.Status switch
    {
        0 => "完成",
        1 => "下载中",
        2 => "系统错误",
        3 => "资源不存在",
        4 => "下载超时",
        5 => "失败",
        6 => "空间不足",
        7 => "已取消",
        _ => $"未知({Task.Status})",
    };
}

/// <summary>下载面板：刷新、暂停/继续、移除；目录设置；云端离线任务只读列表。</summary>
public partial class DownloadsViewModel : ViewModelBase
{
    public DownloadsViewModel()
    {
        AppServices.Downloads.TaskUpdated += OnTaskUpdated;
        AppServices.FeatureFlagsChanged += OnFeatureFlagsChanged;
    }

    private void OnFeatureFlagsChanged() => OnPropertyChanged(nameof(ShowOfflineSection));

    /// <summary>下载页的「云端离线下载」整块可见性，跟随设置页的功能开关。</summary>
    public bool ShowOfflineSection => AppServices.ShowOfflineDownload;

    public ObservableCollection<DownloadRowViewModel> Rows { get; } = new();
    /// <summary>云端离线下载任务（百度）。</summary>
    public ObservableCollection<OfflineTaskRowViewModel> OfflineRows { get; } = new();

    [ObservableProperty] private string _downloadDir = AppServices.DownloadDir;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _offlineLoaded;
    [ObservableProperty] private bool _isMultiSelect;

    /// <summary>批量条上的「已选 N 项」。</summary>
    public string SelectedCountText => $"已选 {Rows.Count(r => r.IsSelected)} 项";

    partial void OnDownloadDirChanged(string value) => AppServices.DownloadDir = value;

    private int _statusStamp;

    /// <summary>
    /// 写下载页状态条。操作反馈默认 10 秒后自动清除（此前会一直挂着）；sticky=true 用于错误，保留到下次写入。
    /// 计时带版本号：期间若又写了新状态，旧计时作废，不会把新提示提前抹掉。
    /// </summary>
    private void SetStatus(string text, bool sticky = false)
    {
        StatusText = text;
        var stamp = ++_statusStamp;
        if (sticky) return;
        _ = ClearStatusLaterAsync(stamp);
    }

    private async System.Threading.Tasks.Task ClearStatusLaterAsync(int stamp)
    {
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (stamp == _statusStamp) StatusText = "";
        });
    }

    partial void OnIsMultiSelectChanged(bool value)
    {
        // 退出多选时顺手清掉勾选，免得下次进来还挂着旧状态
        if (!value) foreach (var r in Rows) r.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCountText));
    }

    /// <summary>从 DownloadManager 的快照重建行（线程→UI）。</summary>
    public void Refresh()
    {
        // 「保存到」实时跟设置页走：入队实际读 AppServices.DownloadDir，标签冻结启动值会误导（深审 B-02）
        DownloadDir = AppServices.DownloadDir;
        var tasks = AppServices.Downloads.Tasks;
        // 简单整体重建（P1 规模足够）
        Rows.Clear();
        foreach (var t in tasks) AddRow(t);
        OnPropertyChanged(nameof(SelectedCountText));
    }

    private void AddRow(DownloadTask t)
    {
        var row = new DownloadRowViewModel(t, this);
        // 勾选数变化要刷新批量条上的计数
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadRowViewModel.IsSelected)) OnPropertyChanged(nameof(SelectedCountText));
        };
        Rows.Add(row);
    }

    /// <summary>拉取云端离线下载任务（目前只百度支持）。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshOfflineAsync()
    {
        SetStatus("正在获取云端任务…");
        try
        {
            var tasks = await AppServices.Baidu.ListOfflineTasksAsync();
            OfflineRows.Clear();
            foreach (var t in tasks) OfflineRows.Add(new OfflineTaskRowViewModel(t));
            OfflineLoaded = true;
            SetStatus(tasks.Count == 0 ? "云端暂无离线下载任务" : $"云端共 {tasks.Count} 个任务");
        }
        catch (PanException ex) { SetStatus($"获取云端任务失败：{ex.Message}", sticky: true); }
        catch (Exception ex) { SetStatus($"获取云端任务异常：{ex.Message}", sticky: true); }
    }

    private void OnTaskUpdated(DownloadTask t)
    {
        // 轮询线程上触发，切到 UI 线程
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Task == t);
            if (row == null)
            {
                AddRow(t);
            }
            else
            {
                row.Refresh();
            }
        });
    }

    [RelayCommand] private async System.Threading.Tasks.Task PauseAsync(DownloadRowViewModel? row)
    {
        if (row == null) return;
        try { await AppServices.Downloads.PauseAsync(row.Task); row.Refresh(); }
        catch (Exception ex) { SetStatus($"暂停失败：{ex.Message}", sticky: true); }
    }

    [RelayCommand] private async System.Threading.Tasks.Task ResumeAsync(DownloadRowViewModel? row)
    {
        if (row == null) return;
        try { await AppServices.Downloads.ResumeAsync(row.Task); row.Refresh(); }
        catch (Exception ex) { SetStatus($"继续失败：{ex.Message}", sticky: true); }
    }

    [RelayCommand] private async System.Threading.Tasks.Task RemoveAsync(DownloadRowViewModel? row)
    {
        // 未完成任务的删除入口：直接删，引擎会连过程文件一起清；已完成任务的删除走行内菜单（RemoveKeep/RemoveWithFiles）
        if (row == null || row.IsComplete) return;
        await RemoveRowAsync(row, deleteLocalFiles: false);
    }

    /// <summary>删除一行任务。未完成 → 连过程文件一起删；已完成 → 按 deleteLocalFiles 决定是否删成品文件。</summary>
    public async System.Threading.Tasks.Task RemoveRowAsync(DownloadRowViewModel row, bool deleteLocalFiles)
    {
        try
        {
            await AppServices.Downloads.RemoveAsync(row.Task, deleteLocalFiles);
            Rows.Remove(row);
            OnPropertyChanged(nameof(SelectedCountText));
            SetStatus(row.IsComplete
                ? (deleteLocalFiles ? "已移除，并删除了本地文件" : "已从列表移除")
                : "已删除任务与过程文件");
        }
        catch (Exception ex) { SetStatus($"移除失败：{ex.Message}", sticky: true); }
    }

    [RelayCommand] private void SelectAll() { foreach (var r in Rows) r.IsSelected = true; }

    [RelayCommand] private void ClearSelection() { foreach (var r in Rows) r.IsSelected = false; }

    /// <summary>批量删除：保留已完成任务的本地文件。</summary>
    [RelayCommand] private System.Threading.Tasks.Task RemoveSelectedKeepFilesAsync() => RemoveSelectedAsync(deleteLocalFiles: false);

    /// <summary>批量删除：已完成任务的本地文件也一起删。</summary>
    [RelayCommand] private System.Threading.Tasks.Task RemoveSelectedWithFilesAsync() => RemoveSelectedAsync(deleteLocalFiles: true);

    private async System.Threading.Tasks.Task RemoveSelectedAsync(bool deleteLocalFiles)
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) { SetStatus("没有勾选任何任务"); return; }
        var done = 0; var failed = 0;
        foreach (var row in selected)
        {
            try
            {
                await AppServices.Downloads.RemoveAsync(row.Task, deleteLocalFiles);
                Rows.Remove(row);
                done++;
            }
            catch { failed++; }
        }
        IsMultiSelect = false;   // 批量操作完退出多选，顺带清空勾选
        SetStatus(failed == 0 ? $"已删除 {done} 项" : $"已删除 {done} 项，{failed} 项失败");
    }

    [RelayCommand] private void OpenDir()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppServices.DownloadDir) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus($"无法打开下载目录：{ex.Message}", sticky: true); }
    }
}
