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

    public void Start(string? exePath = null)
    {
        if (_proc != null) return;
        var exe = ResolveExePath(exePath);
        _secret = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        var port = FreePort();
        _rpcUrl = $"http://127.0.0.1:{port}/jsonrpc";

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo(exe,
                $"--enable-rpc --rpc-listen-all=false --rpc-listen-port={port} --rpc-secret={_secret} " +
                "--max-connection-per-server=16 --split=16 --continue=true --max-tries=5 --retry-wait=2 " +
                "--file-allocation=none --summary-interval=0 --show-console-readout=false")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        _proc.Start();
        _proc.BeginOutputReadLine();   // 丢弃输出，防管道堵死
        _proc.BeginErrorReadLine();

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // 等 RPC 就绪（最多 10 秒）
        var deadline = DateTime.Now.AddSeconds(10);
        for (;;)
        {
            if (_proc.HasExited) throw new InvalidOperationException("aria2c 启动失败");
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
