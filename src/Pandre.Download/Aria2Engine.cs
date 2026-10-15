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
            var port = FreePort();
            _rpcUrl = $"http://127.0.0.1:{port}/jsonrpc";
            _proc = SpawnProcess(exe, port, connections, maxConcurrent);
            try
            {
                WaitRpcReady();
                break;
            }
            catch when (attempt < 3)
            {
                _proc.Dispose();
                _proc = null;
            }
        }
    }

    private Process SpawnProcess(string exe, int port, int connections, int maxConcurrent)
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo(exe,
                $"--enable-rpc --rpc-listen-all=false --rpc-listen-port={port} --rpc-secret={_secret} " +
                $"--max-connection-per-server={connections} --split={connections} --max-concurrent-downloads={maxConcurrent} " +
                "--continue=true --allow-overwrite=true " +
                "--max-tries=5 --retry-wait=2 " +
                "--file-allocation=none --summary-interval=0 --show-console-readout=false")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        proc.Start();
        proc.BeginOutputReadLine();   // 丢弃输出，防管道堵死
        // stderr 留诊断：不堵管道的同时写调试输出，aria2 启动失败/下载错误可查
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Debug.WriteLine("[aria2] " + e.Data); };
        proc.BeginErrorReadLine();
        return proc;
    }

    private void WaitRpcReady()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.Now.AddSeconds(10);
        for (;;)
        {
            if (_proc!.HasExited) throw new InvalidOperationException("aria2c 启动失败");
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
        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
        if (root.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"aria2 {method} 失败: {err.GetProperty("message").GetString()}");
        return root.GetProperty("result");
    }

    public void Dispose()
    {
        try { _http?.Dispose(); } catch { }
        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(); } catch { }
        }
        _proc?.Dispose();
        _proc = null;
    }
}
