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

    // ============ P2：管理与分享（规格逆向自 QuarkPanTool / QuarkPan，见 docs/开发文档.md §4） ============

    /// <summary>POST JSON 并取 data（包络 status=200/code=0 校验）。</summary>
    private async Task<JsonElement> PostJsonAsync(string path, object body, string op, CancellationToken ct)
    {
        var resp = await Http.PostAsync($"{DriveBase}/{path}?{Q}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
        ThrowIfNotOk(root, op);
        return root.TryGetProperty("data", out var d) ? d.Clone() : default;
    }

    /// <summary>轮询异步任务直到成功/失败。data.status：0待 1跑 2成 3败。</summary>
    private async Task<JsonElement> WaitTaskAsync(string taskId, string op, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            var root = JsonDocument.Parse(await Http.GetStringAsync(
                $"{DriveBase}/task?{Q}&task_id={taskId}&retry_index={i}", ct)).RootElement;
            ThrowIfNotOk(root, op);
            var data = root.GetProperty("data");
            var status = data.GetProperty("status").GetInt32();
            if (status == 2) return data.Clone();
            if (status == 3)
            {
                var msg = data.TryGetProperty("message", out var m) ? m.GetString() : "";
                var code = data.TryGetProperty("code", out var c) ? c.GetInt64() : (long?)null;
                throw new PanException($"夸克{op}任务失败 code={code} {msg}", code);
            }
            await Task.Delay(500, ct);
        }
    }

    public async Task<PanFile> CreateFolderAsync(PanFile? dir, string name, CancellationToken ct = default)
    {
        var data = await PostJsonAsync("file",
            new { pdir_fid = dir?.Id ?? "0", file_name = name, dir_init_lock = false, dir_path = "" },
            "新建文件夹", ct);
        var fid = data.GetProperty("fid").GetString()!;
        var parentPath = dir?.Path?.TrimEnd('/') ?? "";
        return new PanFile(fid, parentPath + "/" + name, name, 0, true,
            DateTimeOffset.Now, null, PanProvider.Quark);
    }

    public async Task RenameAsync(PanFile file, string newName, CancellationToken ct = default) =>
        await PostJsonAsync("file/rename", new { fid = file.Id, file_name = newName }, "重命名", ct);

    public async Task MoveAsync(IReadOnlyList<PanFile> files, PanFile? targetDir, CancellationToken ct = default)
    {
        var data = await PostJsonAsync("file/move",
            new
            {
                action_type = 1,
                to_pdir_fid = targetDir?.Id ?? "0",
                filelist = files.Select(f => f.Id).ToArray(),
                exclude_fids = Array.Empty<string>(),
            }, "移动", ct);
        if (data.ValueKind == JsonValueKind.Object &&
            !(data.TryGetProperty("finish", out var fin) && fin.GetBoolean()) &&
            data.TryGetProperty("task_id", out var tid))
            await WaitTaskAsync(tid.GetString()!, "移动", ct);
    }

    public async Task DeleteAsync(IReadOnlyList<PanFile> files, CancellationToken ct = default)
    {
        var data = await PostJsonAsync("file/delete",
            new
            {
                action_type = 2,
                filelist = files.Select(f => f.Id).ToArray(),
                exclude_fids = Array.Empty<string>(),
            }, "删除", ct);
        if (data.ValueKind == JsonValueKind.Object &&
            !(data.TryGetProperty("finish", out var fin) && fin.GetBoolean()) &&
            data.TryGetProperty("task_id", out var tid))
            await WaitTaskAsync(tid.GetString()!, "删除", ct);
    }

    /// <summary>转存外部分享链接（4 步链：token → detail → save → task）。</summary>
    public async Task<PanSaveShareResult> SaveShareAsync(
        string shareUrl, string? passcode, PanFile? targetDir, CancellationToken ct = default)
    {
        // pwd_id = 链接 /s/ 后的那一段
        var m = System.Text.RegularExpressions.Regex.Match(shareUrl, @"/s/([0-9A-Za-z]+)");
        if (!m.Success)
            throw new PanException("无法识别的夸克分享链接（应为 https://pan.quark.cn/s/…）");
        var pwdId = m.Groups[1].Value;

        var tokenData = await PostJsonAsync("share/sharepage/token",
            new { pwd_id = pwdId, passcode = passcode ?? "", support_visit_limit_private_share = true },
            "转存-取stoken", ct);
        var stoken = tokenData.GetProperty("stoken").GetString()!;

        // 拉分享根目录全部条目（转存按根级条目选；文件夹整体递归进）
        var fids = new List<string>();
        var fidTokens = new List<string>();
        for (var page = 1; ; page++)
        {
            var url = $"{DriveBase}/share/sharepage/detail?{Q}&pwd_id={pwdId}&stoken={Uri.EscapeDataString(stoken)}" +
                      $"&pdir_fid=0&force=0&_page={page}&_size=50&_sort=file_type:asc,updated_at:desc";
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct)).RootElement;
            ThrowIfNotOk(root, "转存-取列表");
            var data = root.GetProperty("data");
            if (data.TryGetProperty("is_owner", out var own) && own.GetInt32() == 1)
                throw new PanException("这是你自己网盘里的文件，无需转存");
            var list = data.GetProperty("list");
            foreach (var it in list.EnumerateArray())
            {
                fids.Add(it.GetProperty("fid").GetString()!);
                fidTokens.Add(it.GetProperty("share_fid_token").GetString()!);
            }
            var count = root.TryGetProperty("metadata", out var md) && md.TryGetProperty("_count", out var cnt)
                ? cnt.GetInt32() : list.GetArrayLength();
            if (list.GetArrayLength() == 0 || count < 50) break;
        }
        if (fids.Count == 0)
            throw new PanException("分享里没有可转存的文件");

        // save 走 drive.quark.cn（与 drive-pc 通用，逆向口径如此）
        var saveBody = JsonSerializer.Serialize(new
        {
            fid_list = fids.ToArray(),
            fid_token_list = fidTokens.ToArray(),
            to_pdir_fid = targetDir?.Id ?? "0",
            pwd_id = pwdId,
            stoken,
            pdir_fid = "0",
            scene = "link",
        });
        var resp = await Http.PostAsync($"https://drive.quark.cn/1/clouddrive/share/sharepage/save?{Q}",
            new StringContent(saveBody, Encoding.UTF8, "application/json"), ct);
        var saveRoot = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
        ThrowIfNotOk(saveRoot, "转存-提交");
        var taskId = saveRoot.GetProperty("data").GetProperty("task_id").GetString()!;

        var done = await WaitTaskAsync(taskId, "转存", ct);
        var toDirName = done.TryGetProperty("save_as", out var sa) && sa.TryGetProperty("to_pdir_name", out var tn)
            ? tn.GetString() ?? targetDir?.Name ?? "根目录"
            : targetDir?.Name ?? "根目录";
        return new PanSaveShareResult(toDirName, fids.Count);
    }

    public async Task<IReadOnlyList<PanShare>> ListSharesAsync(CancellationToken ct = default)
    {
        var shares = new List<PanShare>();
        for (var page = 1; ; page++)
        {
            var url = $"{DriveBase}/share/mypage/detail?{Q}&_page={page}&_size=50&_fetch_total=1&_sort=created_at:desc";
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct)).RootElement;
            ThrowIfNotOk(root, "分享列表");
            var list = root.GetProperty("data").GetProperty("list");
            if (list.GetArrayLength() == 0) break;
            foreach (var it in list.EnumerateArray())
            {
                var createdMs = it.TryGetProperty("created_at", out var ca) ? ca.GetInt64() : 0;
                var expiredMs = it.TryGetProperty("expired_at", out var ea) ? ea.GetInt64() : 0;
                shares.Add(new PanShare(
                    it.GetProperty("share_id").GetString()!,
                    it.TryGetProperty("share_url", out var su) ? su.GetString() ?? "" : "",
                    it.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                    null, // 列表不含提取码；要取得逐个调 share/password，P2 UI 不展示
                    it.TryGetProperty("file_num", out var fn) ? fn.GetInt64() : 0,
                    DateTimeOffset.FromUnixTimeMilliseconds(createdMs),
                    expiredMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(expiredMs) : null,
                    PanProvider.Quark));
            }
            if (list.GetArrayLength() < 50) break;
        }
        return shares;
    }

    /// <summary>创建分享（3 步链：share → task 取 share_id → share/password 取链接）。</summary>
    public async Task<PanShare> CreateShareAsync(PanShareOptions options, CancellationToken ct = default)
    {
        // expired_type：永久=1 1天=2 7天=3 30天=4（QuarkPanTool 菜单映射）
        var expiredType = options.ExpireDays switch { 0 => 1, 1 => 2, 30 => 4, _ => 3 };
        var hasPass = !string.IsNullOrEmpty(options.Passcode);
        var body = new Dictionary<string, object?>
        {
            ["fid_list"] = options.Files.Select(f => f.Id).ToArray(),
            ["title"] = options.Title,
            ["url_type"] = hasPass ? 2 : 1,
            ["expired_type"] = expiredType,
        };
        if (hasPass) body["passcode"] = options.Passcode;

        var data = await PostJsonAsync("share", body, "创建分享", ct);
        var taskId = data.GetProperty("task_id").GetString()!;
        var done = await WaitTaskAsync(taskId, "创建分享", ct);
        var shareId = done.GetProperty("share_id").GetString()!;

        var pw = await PostJsonAsync("share/password", new { share_id = shareId }, "取分享链接", ct);
        var shareUrl2 = pw.TryGetProperty("share_url", out var su) ? su.GetString() ?? "" : "";
        var pass = hasPass ? options.Passcode
            : pw.TryGetProperty("passcode", out var pc) && pc.ValueKind == JsonValueKind.String ? pc.GetString() : null;
        if (!string.IsNullOrEmpty(pass) && !shareUrl2.Contains("?pwd="))
            shareUrl2 += "?pwd=" + pass;
        return new PanShare(
            shareId, shareUrl2, options.Title, pass,
            options.Files.Count, DateTimeOffset.Now,
            options.ExpireDays > 0 ? DateTimeOffset.Now.AddDays(options.ExpireDays) : null,
            PanProvider.Quark);
    }

    public async Task DeleteSharesAsync(IReadOnlyList<string> shareIds, CancellationToken ct = default) =>
        await PostJsonAsync("share/delete", new { share_ids = shareIds.ToArray() }, "删除分享", ct);


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
