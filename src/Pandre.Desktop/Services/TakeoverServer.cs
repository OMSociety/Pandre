using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pandre.Core;

namespace Pandre.Desktop.Services;

/// <summary>
/// 浏览器接管的本地接收入口：HttpListener 绑 127.0.0.1，扩展 service worker 直接 POST。
/// GET  /pandre/ping  → {"ok":true,...}（扩展探测存活与端口）
/// POST /pandre/add   → {"ok":true,"enqueued":n,"names":[...]}；头 X-Pandre-Token 校验。
/// 只监听回环地址，外网/局域网物理上够不到；token 是本机其他进程冒用的最后一道闸。
/// </summary>
public sealed class TakeoverServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>当前实际监听的端口（未运行时为 0）。</summary>
    public int Port { get; private set; }

    public bool IsRunning => _listener?.IsListening == true;

    public void Start(int port)
    {
        if (IsRunning) return;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _listener = listener;
        Port = port;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }   // Stop 打断 GetContextAsync
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[takeover] 接收失败: " + ex.Message); continue; }
            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // Host 校验防 DNS rebinding：监听在回环，Host 头仍可能是伪造域名（深审 CL-03）
            var reqHost = ctx.Request.Url?.Host;
            if (reqHost is not ("127.0.0.1" or "localhost"))
            {
                await WriteJsonAsync(ctx, 403, new { ok = false, error = "非法 Host" }).ConfigureAwait(false);
                return;
            }

            // 全挂 CORS 头：扩展 service worker 的请求不受 CORS 约束，但页面调试/未来变更时省一次排查。
            // ACAO=* 是扩展页面跨源读响应的功能依赖（深审 CL-03 裁决保留）；回环物理边界 + 令牌闸是安全边界
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "content-type, x-pandre-token";
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (ctx.Request.HttpMethod == "GET" && path == "/pandre/ping")
            {
                // ping 也过令牌闸：探活同样能泄露应用版本等信息（深审 CL-03）
                if (!IsValidToken(ctx.Request.Headers["X-Pandre-Token"]))
                {
                    await WriteJsonAsync(ctx, 401, new { ok = false, error = "令牌无效（扩展目录里的 config.js 与当前应用不一致，请在设置页重新安装扩展）" }).ConfigureAwait(false);
                    return;
                }
                var version = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
                await WriteJsonAsync(ctx, 200, new { ok = true, name = "pandre", version }).ConfigureAwait(false);
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/pandre/add")
            {
                if (!IsValidToken(ctx.Request.Headers["X-Pandre-Token"]))
                {
                    await WriteJsonAsync(ctx, 401, new { ok = false, error = "令牌无效（扩展目录里的 config.js 与当前应用不一致，请在设置页重新安装扩展）" }).ConfigureAwait(false);
                    return;
                }
                await HandleAddAsync(ctx).ConfigureAwait(false);
                return;
            }
            await WriteJsonAsync(ctx, 404, new { ok = false, error = "unknown route" }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 内部错误不外泄原始异常细节（可能含路径等内部信息，深审 F-C13）；全文进调试日志
            System.Diagnostics.Debug.WriteLine("[takeover] 内部错误: " + ex);
            try { await WriteJsonAsync(ctx, 500, new { ok = false, error = "Pandre 内部错误，详情见应用调试日志" }).ConfigureAwait(false); }
            catch { /* 连接可能已被对端断开 */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    /// <summary>常量时间令牌比对，防时序侧信道逐字节猜令牌（深审 CL-03）。</summary>
    private static bool IsValidToken(string? presented)
    {
        var expected = AppServices.TakeoverToken;
        if (string.IsNullOrEmpty(presented) || presented.Length != expected.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented), System.Text.Encoding.UTF8.GetBytes(expected));
    }

    private async Task HandleAddAsync(HttpListenerContext ctx)
    {
        AddRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<AddRequest>(ctx.Request.InputStream, JsonOptions).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, error = "请求体不是合法 JSON" }).ConfigureAwait(false);
            return;
        }
        if (body?.Files is not { Length: > 0 } || string.IsNullOrEmpty(body.Provider))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, error = "缺少 provider 或 files" }).ConfigureAwait(false);
            return;
        }

        var providerKey = body.Provider.ToLowerInvariant();
        if (providerKey is not ("baidu" or "quark"))
        {
            // 业务错按 400 报（此前经异常冒泡成 500，语义失真，深审 B-06）
            await WriteJsonAsync(ctx, 400, new { ok = false, error = $"不支持的 provider：{body.Provider}" }).ConfigureAwait(false);
            return;
        }
        IPanClient client = providerKey == "baidu" ? AppServices.Baidu : AppServices.Quark;
        if (!client.IsAuthenticated)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, error = providerKey == "baidu" ? "Pandre 未登录百度账号" : "Pandre 未登录夸克账号" }).ConfigureAwait(false);
            return;
        }

        var refs = body.Files
            .Where(f => !string.IsNullOrWhiteSpace(f.Id))
            .Select(f => new PanRemoteFileRef(f.Id!, f.Name, f.Size))
            .ToArray();
        var links = (await client.GetDownloadLinksByIdsAsync(refs).ConfigureAwait(false)).ToArray();
        if (links.Length == 0)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, error = "没有取到任何文件的可下载链接（文件可能已失效）" }).ConfigureAwait(false);
            return;
        }

        // 逐条入队：一条失败不影响其余，部分成功也如实报（深审 B-06/EC-07）
        var names = new List<string>(links.Length);
        var failed = 0; string? lastError = null;
        foreach (var link in links)
        {
            try
            {
                await AppServices.Downloads.EnqueueAsync(link, AppServices.DownloadDir).ConfigureAwait(false);
                names.Add(link.File.Name);
            }
            catch (Exception ex) { failed++; lastError = ex.Message; }
        }
        if (names.Count == 0)
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, error = $"0 个文件入队成功（共 {links.Length} 个，如：{lastError}）" }).ConfigureAwait(false);
            return;
        }
        await WriteJsonAsync(ctx, 200, new { ok = true, enqueued = names.Count, failed, names }).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();
    }

    private sealed class AddRequest
    {
        public string? Provider { get; set; }
        public AddFile[]? Files { get; set; }
    }

    private sealed class AddFile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public long Size { get; set; }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _listener = null;
    }
}
