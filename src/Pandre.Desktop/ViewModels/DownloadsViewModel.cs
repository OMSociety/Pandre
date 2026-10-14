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
    public DownloadRowViewModel(DownloadTask task) { Task = task; Refresh(); }

    public DownloadTask Task { get; }
    public string Name => Task.FileName;
    public string Dir => Task.SaveDir;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;

    public void Refresh()
    {
        var t = Task;
        Progress = t.TotalBytes > 0 ? t.Progress * 100.0 : 0;
        SpeedText = t.Status == DownloadStatus.Downloading && t.SpeedBps > 0
            ? FmtSize(t.SpeedBps) + "/s" : "";
        SizeText = t.TotalBytes > 0
            ? $"{FmtSize(t.CompletedBytes)} / {FmtSize(t.TotalBytes)}" : "";
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
    }

    internal static string FmtSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / 1073741824.0:F1} GB",
        >= 1L << 20 => $"{b / 1048576.0:F1} MB",
        >= 1L << 10 => $"{b / 1024.0:F1} KB",
        _ => $"{b} B",
    };
}

/// <summary>云端离线下载任务的一行（只读展示）。</summary>
public sealed class OfflineTaskRowViewModel : ViewModelBase
{
    public OfflineTaskRowViewModel(PanOfflineTask task) { Task = task; }

    public PanOfflineTask Task { get; }
    public string Name => Task.Name;
    public string SavePath => Task.SavePath;
    public string SizeText => Task.SizeBytes > 0
        ? $"{DownloadRowViewModel.FmtSize(Task.FinishedBytes)} / {DownloadRowViewModel.FmtSize(Task.SizeBytes)}" : "";
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
    public bool IsDone => Task.Status == 0;
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

    partial void OnDownloadDirChanged(string value) => AppServices.DownloadDir = value;

    /// <summary>从 DownloadManager 的快照重建行（线程→UI）。</summary>
    public void Refresh()
    {
        var tasks = AppServices.Downloads.Tasks;
        // 简单整体重建（P1 规模足够）
        Rows.Clear();
        foreach (var t in tasks) Rows.Add(new DownloadRowViewModel(t));
    }

    /// <summary>拉取云端离线下载任务（目前只百度支持，探针实测 6/6）。</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshOfflineAsync()
    {
        StatusText = "正在获取云端任务…";
        try
        {
            var tasks = await AppServices.Baidu.ListOfflineTasksAsync();
            OfflineRows.Clear();
            foreach (var t in tasks) OfflineRows.Add(new OfflineTaskRowViewModel(t));
            OfflineLoaded = true;
            StatusText = tasks.Count == 0 ? "云端暂无离线下载任务" : $"云端共 {tasks.Count} 个任务";
        }
        catch (PanException ex) { StatusText = $"获取云端任务失败：{ex.Message}"; }
        catch (Exception ex) { StatusText = $"获取云端任务异常：{ex.Message}"; }
    }

    private void OnTaskUpdated(DownloadTask t)
    {
        // 轮询线程上触发，切到 UI 线程
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var row = Rows.FirstOrDefault(r => r.Task == t);
            if (row == null)
            {
                row = new DownloadRowViewModel(t);
                Rows.Add(row);
            }
            row.Refresh();
        });
    }

    [RelayCommand] private async System.Threading.Tasks.Task PauseAsync(DownloadRowViewModel? row)
    {
        if (row == null) return;
        try { await AppServices.Downloads.PauseAsync(row.Task); row.Refresh(); }
        catch (Exception ex) { StatusText = $"暂停失败：{ex.Message}"; }
    }

    [RelayCommand] private async System.Threading.Tasks.Task ResumeAsync(DownloadRowViewModel? row)
    {
        if (row == null) return;
        try { await AppServices.Downloads.ResumeAsync(row.Task); row.Refresh(); }
        catch (Exception ex) { StatusText = $"继续失败：{ex.Message}"; }
    }

    [RelayCommand] private async System.Threading.Tasks.Task RemoveAsync(DownloadRowViewModel? row)
    {
        if (row == null) return;
        try
        {
            await AppServices.Downloads.RemoveAsync(row.Task);
            Rows.Remove(row);
        }
        catch (Exception ex) { StatusText = $"移除失败：{ex.Message}"; }
    }

    [RelayCommand] private void OpenDir()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppServices.DownloadDir) { UseShellExecute = true }); }
        catch { }
    }
}
