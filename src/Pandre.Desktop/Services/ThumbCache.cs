using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Pandre.Core;

namespace Pandre.Desktop.Services;

/// <summary>缩略图内存缓存：URL → Bitmap。失败记 null 防重试风暴（仅本会话有效）。</summary>
public static class ThumbCache
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Inflight = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static ThumbCache()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", PanHttp.BrowserUa);
    }

    /// <summary>取缩略图 Bitmap；没有 or 失败返回 null。</summary>
    public static Task<Bitmap?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<Bitmap?>(null);
        if (Cache.TryGetValue(url, out var hit)) return Task.FromResult(hit);
        // 并发去重
        return Inflight.GetOrAdd(url, async u =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(u, ct);
                var bmp = new Bitmap(new MemoryStream(bytes));
                Cache[u] = bmp;
                return bmp;
            }
            catch { Cache[u] = null; return null; }
            finally { Inflight.TryRemove(u, out _); }
        });
    }
}
