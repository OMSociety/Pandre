using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Pandre.Core;

namespace Pandre.Desktop.Services;

/// <summary>缩略图内存缓存：URL → Bitmap。失败进 60 秒负缓存防重试风暴（深审 B-07：不再固化会话期永久缺失）。</summary>
public static class ThumbCache
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Inflight = new();
    private static readonly ConcurrentDictionary<string, DateTime> NegativeUntil = new();
    private const int MaxEntries = 512;   // 容量守卫：超限整体清空，靠懒加载重建
    // 统一走 PanHttp 管道（charset 归一在里面）：传空 Cookie 容器即不带任何登录态，
    // 缩略图抓的是分享态图片，不该携带用户 Cookie
    private static readonly HttpClient Http = PanHttp.CreateClient(new CookieContainer(), PanHttp.BrowserUa, timeoutSec: 15);

    /// <summary>取缩略图 Bitmap；没有 or 失败返回 null。</summary>
    public static Task<Bitmap?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<Bitmap?>(null);
        if (Cache.TryGetValue(url, out var hit)) return Task.FromResult(hit);
        if (NegativeUntil.TryGetValue(url, out var until))
        {
            if (DateTime.UtcNow < until) return Task.FromResult<Bitmap?>(null);
            NegativeUntil.TryRemove(url, out _);   // 负缓存过期：给一次重试机会
        }
        // 并发去重
        return Inflight.GetOrAdd(url, async u =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(u, ct);
                var bmp = new Bitmap(new MemoryStream(bytes));
                Cache[u] = bmp;
                if (Cache.Count > MaxEntries)
                {
                    foreach (var k in Cache.Keys) Cache.TryRemove(k, out _);
                }
                return bmp;
            }
            catch
            {
                // 瞬时失败只负缓存 60 秒，不再固化为会话期永久缺失（深审 B-07）
                NegativeUntil[u] = DateTime.UtcNow.AddSeconds(60);
                return null;
            }
            finally { Inflight.TryRemove(u, out _); }
        });
    }
}
