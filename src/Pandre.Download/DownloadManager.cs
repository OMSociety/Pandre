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
    public string? Url { get; init; }                  // 原始链接，同链接重复入队查重用
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
    // 入队在途占位（u|链接 / p|目录|文件名）：查重通过后立即占键，直到任务落表或入队失败回滚，
    // 封住「查重在锁内、addUri 在锁外」窗口里同键请求双双通过的竞态
    private readonly HashSet<string> _inflight = new(StringComparer.Ordinal);
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
        // 服务端来源文件名先把关再入队：携带分隔符/盘符/相对段的名字会让 aria2 把结果写出下载目录，
        // 这里显式拒绝；不静默清洗，否则实际落盘位置与列表显示的文件名对不上
        if (IsUnsafeFileName(link.File.Name))
            throw new InvalidOperationException($"拒绝下载：文件名包含路径分隔符、盘符或相对路径段:{link.File.Name}");
        // 兜底：先用后启都自愈（沿用上次请求的配置，不经此路径绕过设置页）。
        // Start 同步等 RPC 就绪（最长 10s），不能占着 UI 线程，丢线程池（深审 F-C12）
        if (_pollCts == null)
            await Task.Run(() => Start(_lastExePath, _lastConnections, _lastMaxConcurrent)).ConfigureAwait(false);
        Directory.CreateDirectory(saveDir);
        var urlKey = "u|" + link.Url;
        var pathKey = "p|" + saveDir.ToLowerInvariant() + "|" + link.File.Name.ToLowerInvariant();
        lock (_gate)
        {
            // 查重与占键同一临界区：判完即占，之后 addUri 在锁外进行也不放行同键请求。
            // 同链接拦重复流量；同目录同名（不同链接）拦落盘竞写。只拦活跃任务，删完可重新入队。
            var active = _tasks.Where(x => x.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused);
            if (active.Any(x => x.Url == link.Url) || _inflight.Contains(urlKey))
                throw new InvalidOperationException($"同一链接已在下载队列中:{link.File.Name}");
            if (active.Any(x => string.Equals(x.SaveDir, saveDir, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(x.FileName, link.File.Name, StringComparison.OrdinalIgnoreCase)) ||
                _inflight.Contains(pathKey))
                throw new InvalidOperationException($"同目录已有同名下载任务:{link.File.Name}");
            _inflight.Add(urlKey);
            _inflight.Add(pathKey);
        }
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
        string gid;
        try
        {
            gid = (await _engine.CallAsync("aria2.addUri", new[] { link.Url }, options).ConfigureAwait(false)).GetString()!;
        }
        catch
        {
            // addUri 失败必须退占位：死占位会把同键后续的合法入队永久挡在门外
            lock (_gate)
            {
                _inflight.Remove(urlKey);
                _inflight.Remove(pathKey);
            }
            throw;
        }
        var task = new DownloadTask
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = link.File.Name,
            SaveDir = saveDir,
            Gid = gid,
            Url = link.Url,
            TotalBytes = link.File.Size,
        };
        lock (_gate)
        {
            _tasks.Add(task);
            _inflight.Remove(urlKey);
            _inflight.Remove(pathKey);
        }
        return task;
    }

    /// <summary>
    /// 服务端来源文件名只放行纯文件名：带分隔符即成路径（绝对/相对/UNC），
    /// 盘符前缀会把写入重定向到对应驱动器的当前目录；Windows 创建文件时会剥掉结尾的点与空格，
    /// 「..」「.」因此能借「.. 」「...」这类变体出现，一并拦下。全角字符、空格、中文不受影响。
    /// </summary>
    private static bool IsUnsafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return true;
        if (name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0])) return true;
        var trimmed = name.TrimEnd(' ', '.');
        return trimmed.Length == 0 || trimmed is "." or "..";
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
        var effective = t.Status;
        if (t.Gid != null)
        {
            // 先问 aria2 要权威状态：完成刚落 aria2 但 UI 轮询未刷时，内存态仍显示「下载中」，
            // 按内存态判会误把成品当过程文件删掉（深审 F-C09）
            try
            {
                var st = await _engine.CallAsync("aria2.tellStatus", t.Gid).ConfigureAwait(false);
                effective = st.GetProperty("status").GetString() switch
                {
                    "active" => DownloadStatus.Downloading,
                    "waiting" => DownloadStatus.Queued,
                    "paused" => DownloadStatus.Paused,
                    "complete" => DownloadStatus.Complete,
                    "error" => DownloadStatus.Error,
                    "removed" => DownloadStatus.Removed,
                    _ => effective,
                };
            }
            catch { /* 任务已不在（结果被清）——按内存态 */ }
            try
            {
                if (effective is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Paused)
                    await _engine.CallAsync("aria2.forceRemove", t.Gid).ConfigureAwait(false);
                else
                    await _engine.CallAsync("aria2.removeDownloadResult", t.Gid).ConfigureAwait(false);
            }
            catch { /* 已完成的任务可能已被 aria2 自动清理，忽略 */ }
        }
        // 磁盘清理只允许落在下载目录之内（FileName 来自服务端，防 ../ 越界）；越界路径无从清理，
        // 记录行照常移除——留在列表里也无法对目录外的文件做重试
        var fullDir = Path.GetFullPath(t.SaveDir);
        var fullPath = Path.GetFullPath(t.SavePath);
        if (!fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate) _tasks.Remove(t);
            return;
        }

        var deletePartial = effective != DownloadStatus.Complete;
        if (deletePartial || deleteLocalFiles)
        {
            // 清理失败时记录行留在列表：aria2 侧已停，重走删除入口即可对同一行重跑清理；
            // 行先消失会让「移除失败」提示与仍在的残件对不上，错误也失去重试载体
            try
            {
                TryDelete(fullPath);
                // aria2 在 forceRemove 返回之后才把 .aria2 控制文件落盘（供断点续传），
                // 只删一遍会被它重建——短轮询盯着，写回来就再删，连续两轮不在才收手（实测）。
                await WatchDeleteAsync(fullPath + ".aria2").ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                // 行留在列表就要显一致的错误态：置 Error 也防轮询把活跃行改写成「与引擎失去联系」
                t.Status = DownloadStatus.Error;
                t.ErrorMessage = "已停止，本地文件清理失败";
                throw new InvalidOperationException($"任务已停止，但本地文件清理失败:{ex.Message}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                t.Status = DownloadStatus.Error;
                t.ErrorMessage = "已停止，本地文件无删除权限";
                throw new InvalidOperationException($"任务已停止，但本地文件无删除权限:{ex.Message}", ex);
            }
        }
        lock (_gate) _tasks.Remove(t);
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
        // forceRemove 之后 aria2 可能还握着句柄，撞锁时短等重试一次；重试耗尽或无删除权限
        // 都上抛交由调用方统一处置——在此吞掉会把「已删净」变成假象，用户无从补救
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) when (attempt == 1) { Thread.Sleep(200); }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var active = await _engine.CallAsync("aria2.tellActive").ConfigureAwait(false);
                // waiting/stopped 各取 1000 条：任务多于默认页（100 条）时不丢状态更新
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
                    try
                    {
                        if (t.Gid == null || !byGid.TryGetValue(t.Gid, out var st))
                        {
                            // gid 不在 aria2 三张表里且任务仍处活跃态：引擎重启过或结果被清理，
                            // 置 Error 防任务行冻结在「下载中」（深审 F-C08/EC-03）；终态行不动
                            if (t.Gid != null && t.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused)
                            {
                                t.Status = DownloadStatus.Error;
                                t.ErrorMessage = "与下载引擎失去联系（引擎重启或结果被清理）";
                                TaskUpdated?.Invoke(t);
                            }
                            continue;
                        }
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
                // 引擎进程死了（CallAsync 连不上即知）：自动拉起，不再整轮空转（深审 EC-03）。
                // 队列是 aria2 内存态，重启后旧 gid 全失效，上面的 gid 失配逻辑会把活跃行转 Error
                // 令牌已取消就不再拉起：正常退出会先停轮询再收引擎，收尾之后复活的 aria2c 无人收尸
                if (!ct.IsCancellationRequested && !_engine.IsAlive)
                {
                    try
                    {
                        lock (_startGate)
                        {
                            if (!ct.IsCancellationRequested && !_engine.IsAlive)
                            {
                                _engine.Dispose();
                                _engine.Start(_lastExePath, _lastConnections, _lastMaxConcurrent);
                                Debug.WriteLine("[DownloadManager] 检测到 aria2c 退出，已自动重启引擎");
                            }
                        }
                    }
                    catch (Exception re) { Debug.WriteLine("[DownloadManager] 引擎自动重启失败: " + re.Message); }
                }
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
        // waiting 阶段总长未探明会回 0：0 值不清掉入队时已知的文件大小，否则尺寸与进度闪归零
        var total = SafeLong(st, "totalLength");
        if (total > 0) t.TotalBytes = total;
        t.CompletedBytes = SafeLong(st, "completedLength");
        t.SpeedBps = SafeLong(st, "downloadSpeed");
        if (t.Status == DownloadStatus.Error)
        {
            // errorMessage 缺失时降级取 errorCode；两者皆缺给兜底文案，不让 KeyNotFound 被
            // 外层逐任务 catch 吞掉后错误信息留空（深审 F-C14）
            if (st.TryGetProperty("errorMessage", out var em) && em.GetString() is { Length: > 0 } msg)
                t.ErrorMessage = msg;
            else if (st.TryGetProperty("errorCode", out var ec))
                t.ErrorMessage = $"errorCode={ec.GetString()}";
            else
                t.ErrorMessage = "下载失败（aria2 未返回错误详情）";
        }
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        _engine.Dispose();
    }
}
