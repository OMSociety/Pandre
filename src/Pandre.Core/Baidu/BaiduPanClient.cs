using System.Net;
using System.Text.Json;

namespace Pandre.Core.Baidu;

/// <summary>
/// 百度网盘 Cookie 接口客户端。认证：BDUSS + STOKEN。
/// 下载链：api/filemetas?dlink=1 返回的 dlink 直连，下载时 UA 必须为 "netdisk" 且带 BDUSS。
/// </summary>
public sealed class BaiduPanClient : IPanClient
{
    private const string ApiBase = "https://pan.baidu.com";

    private CookieContainer? _jar;
    private HttpClient? _http;
    private string? _nickname;

    public PanProvider Provider => PanProvider.Baidu;
    public bool IsAuthenticated => _http != null;

    public void Attach(PanCredential credential)
    {
        if (credential.Provider != PanProvider.Baidu)
            throw new ArgumentException("凭据提供方不匹配", nameof(credential));
        _jar = PanHttp.SeedCookies(credential.Cookie, PanHttp.BaiduCookieHosts);
        _http = PanHttp.CreateClient(_jar, PanHttp.BrowserUa);
        _http.DefaultRequestHeaders.Referrer = new Uri("https://pan.baidu.com/disk/home");
        _nickname = credential.Nickname;
    }

    public string? ExportCookie() =>
        _jar == null ? null : PanHttp.JarToCookieString(_jar, PanHttp.BaiduCookieHosts);

    private HttpClient Http => _http ?? throw new InvalidOperationException("未登录：先 Attach");

    /// <summary>errno != 0 时抛出；-6/110 为登录态失效。</summary>
    private static void ThrowIfErrno(JsonElement root, string op)
    {
        var errno = root.TryGetProperty("errno", out var e) ? e.GetInt32() : 0;
        if (errno != 0)
            throw new PanException($"百度{op}失败 errno={errno}", errno);
    }

    public async Task<PanAccountInfo> GetAccountAsync(CancellationToken ct = default)
    {
        var root = JsonDocument.Parse(await Http.GetStringAsync(
            $"{ApiBase}/api/quota?checkfree=1&checkexpire=1", ct)).RootElement;
        ThrowIfErrno(root, "配额查询");
        return new PanAccountInfo(
            _nickname,
            root.GetProperty("total").GetInt64(),
            root.GetProperty("used").GetInt64(),
            null);
    }

    public async Task<IReadOnlyList<PanFile>> ListAsync(PanFile? dir, CancellationToken ct = default)
    {
        var dirPath = dir?.Path ?? "/";
        var files = new List<PanFile>();
        for (var page = 1; ; page++)
        {
            var url = $"{ApiBase}/api/list?dir={Uri.EscapeDataString(dirPath)}&order=name&desc=0&num=1000&page={page}&web=1";
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct)).RootElement;
            ThrowIfErrno(root, "列目录");
            var list = root.GetProperty("list");
            if (list.GetArrayLength() == 0) break;
            foreach (var it in list.EnumerateArray())
                files.Add(MapFile(it));
            if (list.GetArrayLength() < 1000) break;
        }
        return files;
    }

    public async Task<IReadOnlyList<PanFile>> SearchAsync(string keyword, CancellationToken ct = default)
    {
        var files = new List<PanFile>();
        for (var page = 1; ; page++)
        {
            var url = $"{ApiBase}/api/search?key={Uri.EscapeDataString(keyword)}&recursion=1&num=200&page={page}&web=1";
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct)).RootElement;
            ThrowIfErrno(root, "搜索");
            var list = root.GetProperty("list");
            if (list.GetArrayLength() == 0) break;
            foreach (var it in list.EnumerateArray())
                files.Add(MapFile(it));
            if (list.GetArrayLength() < 200) break;
        }
        return files;
    }

    public async Task<IReadOnlyList<PanDownloadLink>> GetDownloadLinksAsync(
        IReadOnlyList<PanFile> files, CancellationToken ct = default)
    {
        var cookie = ExportCookie() ?? throw new InvalidOperationException("未登录");
        var targets = JsonSerializer.Serialize(files.Select(f => f.Path).ToArray());
        var url = $"{ApiBase}/api/filemetas?dlink=1&target={Uri.EscapeDataString(targets)}";
        var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct)).RootElement;
        ThrowIfErrno(root, "取下载链");

        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = PanHttp.BaiduNetdiskUa,
            ["Cookie"] = cookie,
        };
        var byPath = files.ToDictionary(f => f.Path);
        var links = new List<PanDownloadLink>();
        // 注意：filemetas 的条目在 info 数组下（不是 list）
        foreach (var it in root.GetProperty("info").EnumerateArray())
        {
            if (it.TryGetProperty("errno", out var ierr) && ierr.GetInt32() != 0) continue;
            var path = it.GetProperty("path").GetString()!;
            if (!it.TryGetProperty("dlink", out var dl) || dl.ValueKind != JsonValueKind.String) continue;
            var dlink = dl.GetString()!;
            if (byPath.TryGetValue(path, out var file))
                links.Add(new PanDownloadLink(file, dlink, headers, DateTimeOffset.Now.AddHours(4)));
        }
        return links;
    }

    private static PanFile MapFile(JsonElement it)
    {
        string? thumb = null;
        if (it.TryGetProperty("thumbs", out var thumbs) &&
            thumbs.TryGetProperty("url3", out var u3))
            thumb = u3.GetString();
        return new PanFile(
            Id: it.GetProperty("fs_id").GetInt64().ToString(),
            Path: it.GetProperty("path").GetString()!,
            Name: it.GetProperty("server_filename").GetString()!,
            Size: it.GetProperty("size").GetInt64(),
            IsDirectory: it.GetProperty("isdir").GetInt32() == 1,
            ModifiedAt: it.TryGetProperty("server_mtime", out var mt)
                ? DateTimeOffset.FromUnixTimeSeconds(mt.GetInt64()) : null,
            ThumbnailUrl: thumb,
            Provider: PanProvider.Baidu);
    }

    public void Dispose() => _http?.Dispose();
}
