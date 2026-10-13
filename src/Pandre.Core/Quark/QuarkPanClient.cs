using System.Net;
using System.Text;
using System.Text.Json;

namespace Pandre.Core.Quark;

/// <summary>
/// 夸克网盘 Cookie 接口客户端。
/// 关键口径（P0.5 实测）：file/download 必须用桌面客户端 UA 破 100MB 网页限（23018）；
/// 下载链 OSS 回调要求 Cookie 含 __puus（扫码后由 auth/pc/flush 补发）。
/// </summary>
public sealed class QuarkPanClient : IPanClient
{
    private const string DriveBase = "https://drive-pc.quark.cn/1/clouddrive";
    private const string Q = "pr=ucpro&fr=pc&sys=win32&uc_param_str=";

    private CookieContainer? _jar;
    private HttpClient? _http;
    private string? _nickname;

    public PanProvider Provider => PanProvider.Quark;
    public bool IsAuthenticated => _http != null;

    public void Attach(PanCredential credential)
    {
        if (credential.Provider != PanProvider.Quark)
            throw new ArgumentException("凭据提供方不匹配", nameof(credential));
        _jar = PanHttp.SeedCookies(credential.Cookie, PanHttp.QuarkCookieHosts);
        _http = PanHttp.CreateClient(_jar, PanHttp.BrowserUa);
        _http.DefaultRequestHeaders.Referrer = new Uri("https://pan.quark.cn/");
        _nickname = credential.Nickname;
    }

    public string? ExportCookie() =>
        _jar == null ? null : PanHttp.JarToCookieString(_jar, PanHttp.QuarkCookieHosts);

    private HttpClient Http => _http ?? throw new InvalidOperationException("未登录：先 Attach");

    private static void ThrowIfNotOk(JsonElement root, string op)
    {
        var status = root.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
        if (status != 200 || code != 0)
            throw new PanException($"夸克{op}失败 status={status} code={code}", code);
    }

    /// <summary>配额与会员信息走 member 接口（quota/use 是加密的流量包扣减口，不是容量查询）。</summary>
    public async Task<PanAccountInfo> GetAccountAsync(CancellationToken ct = default)
    {
        var root = JsonDocument.Parse(await Http.GetStringAsync($"{DriveBase}/member?{Q}", ct)).RootElement;
        ThrowIfNotOk(root, "配额查询");
        var d = root.GetProperty("data");
        var memberType = d.TryGetProperty("member_type", out var mt) ? mt.GetString() : null;
        return new PanAccountInfo(
            _nickname,
            d.GetProperty("total_capacity").GetInt64(),
            d.GetProperty("use_capacity").GetInt64(),
            memberType);
    }

    public async Task<IReadOnlyList<PanFile>> ListAsync(PanFile? dir, CancellationToken ct = default)
    {
        var pdirFid = dir?.Id ?? "0";
        var parentPath = dir?.Path?.TrimEnd('/') ?? "";
        var files = new List<PanFile>();
        for (var page = 1; ; page++)
        {
            var url = $"{DriveBase}/file/sort?{Q}&pdir_fid={pdirFid}&_page={page}&_size=100&_fetch_total=1&_sort=file_type:asc,updated_at:desc";
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct)).RootElement;
            ThrowIfNotOk(root, "列目录");
            var list = root.GetProperty("data").GetProperty("list");
            if (list.GetArrayLength() == 0) break;
            foreach (var it in list.EnumerateArray())
            {
                var name = it.GetProperty("file_name").GetString()!;
                files.Add(MapFile(it, parentPath + "/" + name));
            }
            if (list.GetArrayLength() < 100) break;
        }
        return files;
    }

    public async Task<IReadOnlyList<PanFile>> SearchAsync(string keyword, CancellationToken ct = default)
    {
        // 搜索接口（GET，参数逆向自桌面端 searchList）。结果路径不可完整还原，Path 仅作展示。
        var files = new List<PanFile>();
        for (var page = 1; ; page++)
        {
            var url = $"{DriveBase}/file/search?{Q}&q={Uri.EscapeDataString(keyword)}&_page={page}&_size=100&_fetch_total=1&_is_hl=1";
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct)).RootElement;
            ThrowIfNotOk(root, "搜索");
            var list = root.GetProperty("data").GetProperty("list");
            if (list.GetArrayLength() == 0) break;
            foreach (var it in list.EnumerateArray())
            {
                var name = it.GetProperty("file_name").GetString()!;
                files.Add(MapFile(it, "/" + name));
            }
            if (list.GetArrayLength() < 100) break;
        }
        return files;
    }

    public async Task<IReadOnlyList<PanDownloadLink>> GetDownloadLinksAsync(
        IReadOnlyList<PanFile> files, CancellationToken ct = default)
    {
        await EnsurePuusAsync(ct);
        var cookie = ExportCookie()!;

        // 桌面 UA 是破 23018 的关键；本次请求用独立 HttpRequestMessage 换 UA
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{DriveBase}/file/download?{Q}&ve=2.5.56&ut=&guid=")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { fids = files.Select(f => f.Id).ToArray() }),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", PanHttp.QuarkDesktopUa);
        req.Headers.TryAddWithoutValidation("Origin", "https://pan.quark.cn");

        var root = JsonDocument.Parse(await (await Http.SendAsync(req, ct)).Content.ReadAsStringAsync(ct)).RootElement;
        ThrowIfNotOk(root, "取下载链");

        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = PanHttp.QuarkDesktopUa,
            ["Cookie"] = cookie,
        };
        var links = new List<PanDownloadLink>();
        var byId = files.ToDictionary(f => f.Id);
        foreach (var it in root.GetProperty("data").EnumerateArray())
        {
            var fid = it.GetProperty("fid").GetString()!;
            var durl = it.GetProperty("download_url").GetString()!;
            if (byId.TryGetValue(fid, out var file))
                links.Add(new PanDownloadLink(file, durl, headers, DateTimeOffset.Now.AddHours(5)));
        }
        return links;
    }

    /// <summary>确保 Cookie 里有 __puus：没有就调 flush 让服务端补发（Set-Cookie 自动进 jar）。</summary>
    private async Task EnsurePuusAsync(CancellationToken ct)
    {
        if (ExportCookie()!.Contains("__puus=")) return;
        await Http.GetStringAsync($"{DriveBase}/auth/pc/flush?{Q}", ct);
        if (!ExportCookie()!.Contains("__puus="))
            throw new PanException("flush 未补发 __puus，下载将不可用");
    }

    private static PanFile MapFile(JsonElement it, string path)
    {
        var isDir = it.GetProperty("file_type").GetInt32() == 0;
        string? thumb = null;
        if (!isDir && it.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.String)
            thumb = th.GetString();
        return new PanFile(
            Id: it.GetProperty("fid").GetString()!,
            Path: path,
            Name: it.GetProperty("file_name").GetString()!,
            Size: it.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
            IsDirectory: isDir,
            ModifiedAt: it.TryGetProperty("updated_at", out var up) && up.GetInt64() > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(up.GetInt64()) : null,
            ThumbnailUrl: thumb,
            Provider: PanProvider.Quark);
    }

    public void Dispose() => _http?.Dispose();
}
