using System.Text.Json;
using Pandre.Core;

namespace Pandre.Download;

public enum DownloadStatus { Queued, Downloading, Paused, Complete, Error, Removed }

public sealed class DownloadTask
{
    public required string Id { get; init; }           // 内部 guid
    public required string FileName { get; init; }
    public required string SaveDir { get; init; }
    public required PanProvider Provider { get; init; }
    public string? Gid { get; set; }                   // aria2 gid
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long TotalBytes { get; set; }
    public long CompletedBytes { get; set; }
    public long SpeedBps { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public double Progress => TotalBytes > 0 ? (double)CompletedBytes / TotalBytes : 0;
    public string SavePath => Path.Combine(SaveDir, FileName);
}

/// <summary>
/// 下载队列：任务进 aria2，1 秒轮询 tellActive/tellWaiting/tellStopped 刷新状态。
/// aria2 自带 .aria2 控制文件实现断点续传（进程重启后继续任务不在 P1 范围）。
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly Aria2Engine _engine = new();
    private readonly List<DownloadTask> _tasks = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _pollCts;

    public event Action<DownloadTask>? TaskUpdated;

    public IReadOnlyList<DownloadTask> Tasks { get { lock (_gate) return _tasks.ToArray(); } }

    public void Start(string? aria2ExePath = null)
    {
        _engine.Start(aria2ExePath);
        _pollCts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoopAsync(_pollCts.Token));
    }

    public async Task<DownloadTask> EnqueueAsync(PanDownloadLink link, string saveDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(saveDir);
        var options = new Dictionary<string, object>
        {
            ["out"] = link.File.Name,
            ["dir"] = saveDir,
            ["header"] = link.Headers.Select(kv => $"{kv.Key}: {kv.Value}").ToArray(),
            ["max-connection-per-server"] = "16",
            ["split"] = "16",
        };
        var gid = (await _engine.CallAsync("aria2.addUri", new[] { link.Url }, options)).GetString()!;
        var task = new DownloadTask
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = link.File.Name,
            SaveDir = saveDir,
            Provider = link.File.Provider,
            Gid = gid,
            TotalBytes = link.File.Size,
        };
        lock (_gate) _tasks.Add(task);
        return task;
    }

    public async Task PauseAsync(DownloadTask t) { if (t.Gid != null) await _engine.CallAsync("aria2.pause", t.Gid); }

    public async Task ResumeAsync(DownloadTask t) { if (t.Gid != null) await _engine.CallAsync("aria2.unpause", t.Gid); }

    /// <summary>移除任务：活动中的先强制停，再清结果记录。磁盘上已下载的部分文件保留（用户自行删）。</summary>
    public async Task RemoveAsync(DownloadTask t)
    {
        if (t.Gid != null)
        {
            try
            {
                if (t.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Paused)
                    await _engine.CallAsync("aria2.forceRemove", t.Gid);
                else
                    await _engine.CallAsync("aria2.removeDownloadResult", t.Gid);
            }
            catch { /* 已完成的任务可能已被 aria2 自动清理，忽略 */ }
        }
        lock (_gate) _tasks.Remove(t);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var active = await _engine.CallAsync("aria2.tellActive");
                var waiting = await _engine.CallAsync("aria2.tellWaiting", 0, 100);
                var stopped = await _engine.CallAsync("aria2.tellStopped", 0, 100);
                var byGid = new Dictionary<string, JsonElement>();
                foreach (var el in active.EnumerateArray()) byGid[el.GetProperty("gid").GetString()!] = el;
                foreach (var el in waiting.EnumerateArray()) byGid[el.GetProperty("gid").GetString()!] = el;
                foreach (var el in stopped.EnumerateArray()) byGid[el.GetProperty("gid").GetString()!] = el;

                List<DownloadTask> snapshot;
                lock (_gate) snapshot = _tasks.ToList();
                foreach (var t in snapshot)
                {
                    if (t.Gid == null || !byGid.TryGetValue(t.Gid, out var st)) continue;
                    UpdateTask(t, st);
                    TaskUpdated?.Invoke(t);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 轮询失败（aria2 暂时不可用）下个周期再试 */ }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static void UpdateTask(DownloadTask t, JsonElement st)
    {
        t.Status = st.GetProperty("status").GetString() switch
        {
            "active" => DownloadStatus.Downloading,
            "waiting" => DownloadStatus.Queued,
            "paused" => DownloadStatus.Paused,
            "complete" => DownloadStatus.Complete,
            "error" => DownloadStatus.Error,
            "removed" => DownloadStatus.Removed,
            _ => t.Status,
        };
        t.TotalBytes = long.Parse(st.GetProperty("totalLength").GetString() ?? "0");
        t.CompletedBytes = long.Parse(st.GetProperty("completedLength").GetString() ?? "0");
        t.SpeedBps = long.Parse(st.GetProperty("downloadSpeed").GetString() ?? "0");
        if (t.Status == DownloadStatus.Error)
            t.ErrorMessage = st.TryGetProperty("errorMessage", out var em) ? em.GetString() : $"errorCode={st.GetProperty("errorCode").GetString()}";
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        _engine.Dispose();
    }
}
