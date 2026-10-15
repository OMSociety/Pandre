using System;
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
            // 全挂 CORS 头：扩展 service worker 的请求不受 CORS 约束，但页面调试/未来变更时省一次排查
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
                var version = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
                await WriteJsonAsync(ctx, 200, new { ok = true, name = "pandre", version }).ConfigureAwait(false);
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/pandre/add")
            {
                if (ctx.Request.Headers["X-Pandre-Token"] != AppServices.TakeoverToken)
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
            try { await WriteJsonAsync(ctx, 500, new { ok = false, error = ex.Message }).ConfigureAwait(false); }
            catch { /* 连接可能已被对端断开 */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
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

        IPanClient client = body.Provider.ToLowerInvariant() switch
        {
            "baidu" => AppServices.Baidu,
            "quark" => AppServices.Quark,
            _ => throw new PanException($"不支持的 provider：{body.Provider}"),
        };
        if (!client.IsAuthenticated)
            throw new PanException(body.Provider == "baidu" ? "Pandre 未登录百度账号" : "Pandre 未登录夸克账号");

        var refs = body.Files
            .Where(f => !string.IsNullOrWhiteSpace(f.Id))
            .Select(f => new PanRemoteFileRef(f.Id!, f.Name, f.Size))
            .ToArray();
        var links = await client.GetDownloadLinksByIdsAsync(refs).ConfigureAwait(false);

        var names = new string[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            await AppServices.Downloads.EnqueueAsync(links[i], AppServices.DownloadDir).ConfigureAwait(false);
            names[i] = links[i].File.Name;
        }
        await WriteJsonAsync(ctx, 200, new { ok = true, enqueued = links.Count, names }).ConfigureAwait(false);
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
