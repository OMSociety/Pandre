using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Pandre.Download;

/// <summary>
/// aria2c 子进程守护：随机端口 + 随机 rpc-secret 启动 JSON-RPC，进程随应用退出。
/// aria2 是 GPL-2.0 独立子进程（聚合分发，不传染本项目许可证），发布包须附带 assets/windows/aria2-COPYING.txt。
/// </summary>
public sealed class Aria2Engine : IDisposable
{
    private Process? _proc;
    private HttpClient? _http;
    private string _rpcUrl = "";
    private string _secret = "";
    private int _idSeq;
    private readonly StringBuilder _stderrTail = new();

    /// <summary>aria2c 进程是否存活（供调用方做死亡感知与自动重启，深审 EC-03）。</summary>
    public bool IsAlive => _proc is { HasExited: false };

    /// <summary>aria2c.exe 的约定位置：应用目录 assets/windows/aria2c.exe（Desktop 项目负责拷贝到输出目录）。</summary>
    public static string ResolveExePath(string? explicitPath = null)
    {
        if (explicitPath != null && File.Exists(explicitPath)) return explicitPath;
        var besideApp = Path.Combine(AppContext.BaseDirectory, "assets", "windows", "aria2c.exe");
        if (File.Exists(besideApp)) return besideApp;
        throw new FileNotFoundException("找不到 aria2c.exe（约定位置: " + besideApp + "）");
    }

    /// <summary>
    /// 启动 aria2c 并等 RPC 就绪（最多 10 秒）。
    /// 同步等待发生在 Task.Run 启动链上（非 UI 线程），不会死锁。
    /// FreePort 与 aria2 实际绑定之间有竞态窗口：进程立即退出（端口被占/参数错）时换端口重试，最多 3 次。
    /// </summary>
    public void Start(string? exePath = null, int connections = 16, int maxConcurrent = 5)
    {
        if (_proc != null) return;
        var exe = ResolveExePath(exePath);
        _secret = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

        for (var attempt = 1; ; attempt++)
        {
            _stderrTail.Clear();   // 每次尝试的 stderr 摘要只属本次
            var port = FreePort();
            _rpcUrl = $"http://127.0.0.1:{port}/jsonrpc";
            _proc = SpawnProcess(exe, port, connections, maxConcurrent);
            try
            {
                WaitRpcReady();
                break;
            }
            catch
            {
                // 每次失败都必须清掉 _proc：留着会让 Start 的 _proc != null 短路永久跳过重拉，
                // 兜底启动也只会撞「先 Start()」异常，应用重启前下载彻底不可用（深审 CL-01）
                _proc.Dispose();
                _proc = null;
                if (attempt >= 3) throw;
            }
        }
    }

    private Process SpawnProcess(string exe, int port, int connections, int maxConcurrent)
    {
        var startInfo = new ProcessStartInfo(exe,
            $"--enable-rpc --rpc-listen-all=false --rpc-listen-port={port} --rpc-secret={_secret} " +
            $"--max-connection-per-server={connections} --split={connections} --max-concurrent-downloads={maxConcurrent} " +
            "--continue=true --allow-overwrite=true --auto-save-interval=1 " +
            "--max-tries=5 --retry-wait=2 " +
            // 只走 IPv4：本机 DNS 对 d.pcs.baidu.com 这类 CDN 会返回 AAAA，但 IPv6 没有出口路由，
            // aria2 一旦选中 AAAA 就直接以 "unreachable network" 失败、不会回退 IPv4（实测）。
            "--disable-ipv6=true " +
            "--file-allocation=none --summary-interval=0 --show-console-readout=false")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // 不继承系统代理：下载链都是境内直连（实测直连 IPv4 通、且比经代理快一个量级），
        // 而经代理时 aria2 的 WinTLS 在并发连接上会随机报 "SSL/TLS handshake failure"。
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
        {
            startInfo.Environment.Remove(name);
        }

        var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        proc.Start();
        proc.BeginOutputReadLine();   // 丢弃输出，防管道堵死
        // stderr 留诊断：不堵管道的同时写调试输出，aria2 启动失败/下载错误可查
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Debug.WriteLine("[aria2] " + e.Data);
            lock (_stderrTail)   // 留尾部 2KB 供启动失败诊断（深审 F-C05）
            {
                _stderrTail.AppendLine(e.Data);
                if (_stderrTail.Length > 2048) _stderrTail.Remove(0, _stderrTail.Length - 2048);
            }
        };
        proc.Exited += (_, _) => Debug.WriteLine("[aria2] 进程退出");   // 死亡感知，重启由 DownloadManager 轮询触发
        proc.BeginErrorReadLine();
        return proc;
    }

    private void WaitRpcReady()
    {
        _http?.Dispose();   // 重试路径不再泄漏旧实例（深审 F-C06）
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.Now.AddSeconds(10);
        for (;;)
        {
            if (_proc!.HasExited)
            {
                string tail;
                lock (_stderrTail) tail = _stderrTail.ToString().Trim();
                throw new InvalidOperationException(
                    $"aria2c 启动失败(退出码 {_proc.ExitCode})" + (tail.Length > 0 ? $",stderr 摘要:{tail}" : ""));
            }
            try
            {
                CallAsync("aria2.getVersion").GetAwaiter().GetResult();
                break;
            }
            catch when (DateTime.Now < deadline) { Thread.Sleep(200); }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>调 aria2 JSON-RPC，自动带 rpc-secret token。</summary>
    public async Task<JsonElement> CallAsync(string method, params object?[] args)
    {
        if (_http == null) throw new InvalidOperationException("先 Start()");
        var id = ++_idSeq;
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = new object?[] { "token:" + _secret }.Concat(args).ToArray(),
        };
        var resp = await _http.PostAsync(_rpcUrl,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")).ConfigureAwait(false);
        // JsonDocument 用完即须释放；RootElement 是文档内存的视图，返回前克隆成独立副本，
        // 调用方（轮询的按 gid 状态缓存等）长期持有时才不会踩到已释放的文档
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"aria2 {method} 失败: {err.GetProperty("message").GetString()}");
        return root.GetProperty("result").Clone();
    }

    public void Dispose()
    {
        try { _http?.Dispose(); } catch { }
        if (_proc is { HasExited: false })
        {
            // 等一拍再收：--auto-save-interval=1 已把进度即时落 .aria2，强杀不再丢最多 60s 进度（深审 F-C17）
            try { _proc.Kill(); _proc.WaitForExit(3000); } catch { }
        }
        _proc?.Dispose();
        _proc = null;
    }
}
