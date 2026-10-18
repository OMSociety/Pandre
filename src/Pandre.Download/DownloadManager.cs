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
            _connections = Math.Clamp(connections, 1, 16);   // aria2 max-connection-per-server 硬上限 16
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

    /// <summary>
    /// 移除任务：活动中的先强制停，再清结果记录。
    /// 未完成的任务总是连过程文件一起删（分片残件 + .aria2 控制文件）；
    /// 已完成的任务仅在 deleteLocalFiles 为真时删除成品文件（由界面询问决定）。
    /// </summary>
    public async Task RemoveAsync(DownloadTask t, bool deleteLocalFiles = false)
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

        // 磁盘清理只允许落在下载目录之内（FileName 来自服务端，防 ../ 越界）
        var fullDir = Path.GetFullPath(t.SaveDir);
        var fullPath = Path.GetFullPath(t.SavePath);
        if (!fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

        var deletePartial = t.Status != DownloadStatus.Complete;
        if (deletePartial || deleteLocalFiles)
        {
            TryDelete(fullPath);
            // aria2 在 forceRemove 返回之后才把 .aria2 控制文件落盘（供断点续传），
            // 只删一遍会被它重建——短轮询盯着，写回来就再删，连续两轮不在才收手（实测）。
            await WatchDeleteAsync(fullPath + ".aria2").ConfigureAwait(false);
        }
    }

    private static async Task WatchDeleteAsync(string path)
    {
        for (var round = 0; round < 12; round++)
        {
            TryDelete(path);
            await Task.Delay(220).ConfigureAwait(false);
            if (!File.Exists(path))
            {
                await Task.Delay(220).ConfigureAwait(false);
                if (!File.Exists(path)) return;
            }
        }
    }

    private static void TryDelete(string path)
    {
        // forceRemove 之后 aria2 可能还握着句柄，撞锁时短等重试一次
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) when (attempt == 0) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { return; }
        }
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
