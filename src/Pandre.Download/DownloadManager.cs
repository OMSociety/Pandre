using System.Diagnostics;
using System.Text.Json;
using Pandre.Core;

namespace Pandre.Download;

public enum DownloadStatus { Queued, Downloading, Paused, Complete, Error, Removed }

public sealed class DownloadTask
{
    public required string Id { get; init; }           // 内部 guid
    public required string FileName { get; init; }
    public required string SaveDir { get; init; }
    public string? Gid { get; set; }                   // aria2 gid
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long TotalBytes { get; set; }
    public long CompletedBytes { get; set; }
    public long SpeedBps { get; set; }
    public string? ErrorMessage { get; set; }

    public double Progress => TotalBytes > 0 ? (double)CompletedBytes / TotalBytes : 0;
    public string SavePath => Path.Combine(SaveDir, FileName);
}

/// <summary>
/// 下载队列：任务进 aria2，1 秒轮询 tellActive/tellWaiting/tellStopped 刷新状态。
/// aria2 自带 .aria2 控制文件实现断点续传（仅对单个下载任务的分片续传有效；任务列表为内存态，
/// 应用重启后队列不恢复，见 README 功能说明）。
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly Aria2Engine _engine = new();
    private readonly List<DownloadTask> _tasks = new();
    private readonly object _gate = new();
    private readonly object _startGate = new();
    private CancellationTokenSource? _pollCts;
    private int _connections = 16;
    private string? _lastExePath;
    private int _lastConnections = 16;
    private int _lastMaxConcurrent = 5;

    public event Action<DownloadTask>? TaskUpdated;

    public IReadOnlyList<DownloadTask> Tasks { get { lock (_gate) return _tasks.ToArray(); } }

    public void Start(string? aria2ExePath = null, int connections = 16, int maxConcurrent = 5)
    {
        lock (_startGate)   // 幂等原子化：并发调用只起一次 aria2/轮询
        {
            if (_pollCts != null) return;
            _lastExePath = aria2ExePath;
            _lastConnections = connections;
            _lastMaxConcurrent = maxConcurrent;
            _connections = Math.Clamp(connections, 1, 64);
            _engine.Start(aria2ExePath, _connections, maxConcurrent);
            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoopAsync(_pollCts.Token));
        }
    }

    public async Task<DownloadTask> EnqueueAsync(PanDownloadLink link, string saveDir)
    {
        // 兜底：先用后启都自愈（沿用上次请求的配置，不经此路径绕过设置页）
        if (_pollCts == null) Start(_lastExePath, _lastConnections, _lastMaxConcurrent);
        Directory.CreateDirectory(saveDir);
        var options = new Dictionary<string, object>
        {
            ["out"] = link.File.Name,
            ["dir"] = saveDir,
            ["header"] = link.Headers.Select(kv => $"{kv.Key}: {kv.Value}").ToArray(),
            // 必须跟引擎级同源：aria2 的逐任务选项会覆盖全局值，写死 16 会让设置页的连接数失效
            ["max-connection-per-server"] = _connections.ToString(),
            ["split"] = _connections.ToString(),
            // 默认 20M 会让中等体积文件实际拿不到设定的连接数（file/min-split-size 才是真实并发）
            ["min-split-size"] = "4194304",
        };
        var gid = (await _engine.CallAsync("aria2.addUri", new[] { link.Url }, options).ConfigureAwait(false)).GetString()!;
        var task = new DownloadTask
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = link.File.Name,
            SaveDir = saveDir,
            Gid = gid,
            TotalBytes = link.File.Size,
        };
        lock (_gate) _tasks.Add(task);
        return task;
    }

    public async Task PauseAsync(DownloadTask t)
    {
        if (t.Gid != null) await _engine.CallAsync("aria2.pause", t.Gid).ConfigureAwait(false);
    }

    public async Task ResumeAsync(DownloadTask t)
    {
        if (t.Gid != null) await _engine.CallAsync("aria2.unpause", t.Gid).ConfigureAwait(false);
    }

    /// <summary>移除任务：活动中的先强制停，再清结果记录。磁盘上已下载的部分文件保留（用户自行删）。</summary>
    public async Task RemoveAsync(DownloadTask t)
    {
        if (t.Gid != null)
        {
            try
            {
                if (t.Status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Paused)
                    await _engine.CallAsync("aria2.forceRemove", t.Gid).ConfigureAwait(false);
                else
                    await _engine.CallAsync("aria2.removeDownloadResult", t.Gid).ConfigureAwait(false);
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
                var active = await _engine.CallAsync("aria2.tellActive").ConfigureAwait(false);
                // waiting/stopped 取 1000 条：队列或停止任务超过 100 时旧任务不丢状态更新
                var waiting = await _engine.CallAsync("aria2.tellWaiting", 0, 1000).ConfigureAwait(false);
                var stopped = await _engine.CallAsync("aria2.tellStopped", 0, 1000).ConfigureAwait(false);
                var byGid = new Dictionary<string, JsonElement>();
                foreach (var el in active.EnumerateArray()) byGid[el.GetProperty("gid").GetString()!] = el;
                foreach (var el in waiting.EnumerateArray()) byGid[el.GetProperty("gid").GetString()!] = el;
                foreach (var el in stopped.EnumerateArray()) byGid[el.GetProperty("gid").GetString()!] = el;

                List<DownloadTask> snapshot;
                lock (_gate) snapshot = _tasks.ToList();
                foreach (var t in snapshot)
                {
                    if (t.Gid == null || !byGid.TryGetValue(t.Gid, out var st)) continue;
                    try
                    {
                        UpdateTask(t, st);
                        TaskUpdated?.Invoke(t);
                    }
                    catch (Exception ex)
                    {
                        // 单个任务坏字段只影响该任务，不冻结整轮刷新
                        Debug.WriteLine($"[DownloadManager] 任务 {t.Id} 状态更新失败: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // 轮询失败（aria2 暂时不可用）下个周期再试；留调试痕迹不静默
                Debug.WriteLine("[DownloadManager] 轮询失败: " + ex.Message);
            }
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private static long SafeLong(JsonElement st, string key)
    {
        var v = st.TryGetProperty(key, out var e) ? e.GetString() : null;
        return long.TryParse(v, out var n) ? n : 0;
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
        // aria2 返回字符串数字：坏字段按 0 处理（安全解析，不炸整轮）
        t.TotalBytes = SafeLong(st, "totalLength");
        t.CompletedBytes = SafeLong(st, "completedLength");
        t.SpeedBps = SafeLong(st, "downloadSpeed");
        if (t.Status == DownloadStatus.Error)
            t.ErrorMessage = st.TryGetProperty("errorMessage", out var em) ? em.GetString() : $"errorCode={st.GetProperty("errorCode").GetString()}";
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        _engine.Dispose();
    }
}
