using System.Net;
using System.Text;
using System.Text.Json;

namespace Pandre.Core.Quark;

/// <summary>
/// 夸克网盘 Cookie 接口客户端。
/// 关键口径（P0.5 实测 + REA 逆向夸克桌面端 3.24.0 renderer）：file/download 必须用桌面客户端 UA 破 100MB 网页限（23018）；
/// 下载链 OSS 回调要求 Cookie 含 __puus（扫码后由 auth/pc/flush 补发）；
/// 全量 clouddrive 端点（含 share/sharepage/save）统一挂 drive-pc.quark.cn，无 drive.quark.cn 主机。
/// </summary>
public sealed class QuarkPanClient : IPanClient
{
    private const string DriveBase = "https://drive-pc.quark.cn/1/clouddrive";
    private const string Q = "pr=ucpro&fr=pc&sys=win32&uc_param_str=";

    private CookieContainer? _jar;
    private HttpClient? _http;
    private string? _nickname;

    /// <summary>
    /// 下载链有效期：经验值，未做真网实测标定。链接早衰的表现是下载失败，而不是返回明确的「过期」错误；
    /// 真网实测标定后如需调整，以实测为准。
    /// </summary>
    private static readonly TimeSpan DownloadLinkTtl = TimeSpan.FromHours(5);
    /// <summary>异步任务轮询超时上限（移动/删除/转存/分享/复制共用）。</summary>
    private static readonly TimeSpan TaskWaitTimeout = TimeSpan.FromMinutes(10);
    /// <summary>列表分页拉满上限（页数）：超过仍未见 _total 拉满时按服务端截断保护性报错，不静默少列。</summary>
    private const int MaxListPages = 100;
    /// <summary>分片上传重试参数（与客户端一致：3 次尝试、间隔 500ms；每次尝试独立超时）。</summary>
    private const int PartAttempts = 3;
    private static readonly TimeSpan PartRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PartTimeout = TimeSpan.FromSeconds(120);

    public PanProvider Provider => PanProvider.Quark;
    public bool IsAuthenticated => _http != null;

    /// <summary>解除会话：清空内存会话态（退出登录/会话过期用），IsAuthenticated 归 false。磁盘凭据由 CredentialsStore 负责。</summary>
    public void Detach()
    {
        Dispose();
        _jar = null;
        _nickname = null;
    }

    public void Attach(PanCredential credential)
    {
        if (credential.Provider != PanProvider.Quark)
            throw new ArgumentException("凭据提供方不匹配", nameof(credential));
        Dispose(); // 重复 Attach 先释放旧客户端，避免泄漏
        _jar = PanHttp.SeedCookies(credential.Cookie, PanHttp.QuarkCookieHosts);
        _http = PanHttp.CreateClient(_jar, PanHttp.BrowserUa);
        _http.DefaultRequestHeaders.Referrer = new Uri("https://pan.quark.cn/");
        _nickname = credential.Nickname;
    }

    public string? ExportCookie() =>
        _jar == null ? null : PanHttp.JarToCookieString(_jar, PanHttp.QuarkCookieHosts);

    // 未 Attach 或已 Detach 时统一抛业务口径的 PanException「未登录」：调用方拿到的是可呈现的业务异常，而非打到已释放/缺失实例的运行时异常
    private HttpClient Http => _http ?? throw new PanException("未登录：先 Attach");

    private static void ThrowIfNotOk(JsonElement root, string op)
    {
        var hasStatus = root.TryGetProperty("status", out var s);
        var hasCode = root.TryGetProperty("code", out var c);
        var status = hasStatus ? s.GetInt32() : 0;
        var code = hasCode ? c.GetInt32() : 0;
        if (status != 200 || code != 0)
        {
            var missing = !hasStatus || !hasCode ? "（响应缺 status/code 包络字段）" : "";
            throw new PanException($"夸克{op}失败 status={status} code={code}{missing}", code);
        }
    }

    /// <summary>
    /// 分页接口的服务端总量：data._total 优先，缺席再看 metadata._total，都无按 -1（未知）处理。
    /// 调用方以它判定「是否已拉满」；未知总量时才退回「页未满即末页」的旧行为。
    /// </summary>
    private static long GetPagedTotal(JsonElement root)
    {
        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("_total", out var t1) && t1.TryGetInt64(out var v1)) return v1;
        if (root.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object
            && md.TryGetProperty("_total", out var t2) && t2.TryGetInt64(out var v2)) return v2;
        return -1;
    }

    /// <summary>配额与会员信息走 member 接口（quota/use 是加密的流量包扣减口，不是容量查询）。</summary>
    public async Task<PanAccountInfo> GetAccountAsync(CancellationToken ct = default)
    {
        var root = JsonDocument.Parse(await Http.GetStringAsync($"{DriveBase}/member?{Q}", ct).ConfigureAwait(false)).RootElement;
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
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false)).RootElement;
            ThrowIfNotOk(root, "列目录");
            var list = root.GetProperty("data").GetProperty("list");
            if (list.GetArrayLength() == 0) break;
            foreach (var it in list.EnumerateArray())
            {
                var name = it.GetProperty("file_name").GetString()!;
                files.Add(MapFile(it, parentPath + "/" + name));
            }
            // 以 _total 拉满为准（_total 缺席才退回「页未满即末页」）：服务端单页截短（cap）时靠它续页，
            // 翻页超上限保护性报错——宁可显式失败也不静默少列文件
            var total = GetPagedTotal(root);
            if (total >= 0 ? files.Count >= total : list.GetArrayLength() < 100) break;
            if (page >= MaxListPages)
                throw new PanException($"夸克列目录翻页超过 {MaxListPages} 页仍未拉满（已取 {files.Count}，服务端报 _total={total}），结果可能不完整，已中止");
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
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false)).RootElement;
            ThrowIfNotOk(root, "搜索");
            var list = root.GetProperty("data").GetProperty("list");
            if (list.GetArrayLength() == 0) break;
            foreach (var it in list.EnumerateArray())
            {
                var name = it.GetProperty("file_name").GetString()!;
                files.Add(MapFile(it, "/" + name));
            }
            // 同列目录：按 _total 拉满，翻页超上限保护性报错
            var total = GetPagedTotal(root);
            if (total >= 0 ? files.Count >= total : list.GetArrayLength() < 100) break;
            if (page >= MaxListPages)
                throw new PanException($"夸克搜索翻页超过 {MaxListPages} 页仍未拉满（已取 {files.Count}，服务端报 _total={total}），结果可能不完整，已中止");
        }
        return files;
    }

    public async Task<IReadOnlyList<PanDownloadLink>> GetDownloadLinksAsync(
        IReadOnlyList<PanFile> files, CancellationToken ct = default)
    {
        await EnsurePuusAsync(ct).ConfigureAwait(false);
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

        var root = JsonDocument.Parse(await (await Http.SendAsync(req, ct).ConfigureAwait(false))
            .Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement;
        ThrowIfNotOk(root, "取下载链");

        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = PanHttp.QuarkDesktopUa,
            ["Cookie"] = cookie,
        };
        var links = new List<PanDownloadLink>();
        var byId = new Dictionary<string, PanFile>();
        foreach (var f in files)
            byId[f.Id] = f; // 按 fid 归并；调用方不应传重复行，重复时下方完整性校验会报「请求 N 个仅返回 M 个」
        foreach (var it in root.GetProperty("data").EnumerateArray())
        {
            var fid = it.GetProperty("fid").GetString()!;
            var durl = it.GetProperty("download_url").GetString()!;
            if (byId.TryGetValue(fid, out var file))
                links.Add(new PanDownloadLink(file, durl, headers, DateTimeOffset.Now + DownloadLinkTtl));
        }
        if (links.Count < files.Count)
            throw new PanException($"夸克取下载链不完整：请求 {files.Count} 个，仅返回 {links.Count} 个");
        return links;
    }

    /// <summary>确保 Cookie 里有 __puus：没有就调 flush 让服务端补发（Set-Cookie 自动进 jar）。</summary>
    private async Task EnsurePuusAsync(CancellationToken ct)
    {
        // 未 Attach 时 Cookie 为空：显式报「未登录」，不让 null 断言把真实原因砸成 NRE
        if (ExportCookie() is not { } cookie) throw new PanException("未登录");
        if (cookie.Contains("__puus=")) return;
        await Http.GetStringAsync($"{DriveBase}/auth/pc/flush?{Q}", ct).ConfigureAwait(false);
        if (!ExportCookie()!.Contains("__puus="))
            throw new PanException("flush 未补发 __puus，下载将不可用");
    }

    // ============ P2：管理与分享（规格逆向自 QuarkPanTool / QuarkPan / 夸克桌面端，见 docs/开发文档.md §4） ============

    /// <summary>POST JSON 并取 data（包络 status=200/code=0 校验）。</summary>
    private async Task<JsonElement> PostJsonAsync(string path, object body, string op, CancellationToken ct)
    {
        var resp = await Http.PostAsync($"{DriveBase}/{path}?{Q}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement;
        ThrowIfNotOk(root, op);
        return root.TryGetProperty("data", out var d) ? d.Clone() : default;
    }

    /// <summary>
    /// 轮询异步任务直到成功/失败。data.status：0 待 1 跑 2 成 3 败。超时上限 10 分钟。
    /// 瞬时网络错误按分片上传同口径重试（连续 3 次失败才放弃）；status 出现未知取值显式报错，
    /// 不当未知为「仍在跑」傻等——那只会把真实故障拖成 10 分钟超时假象。
    /// </summary>
    private async Task<JsonElement> WaitTaskAsync(string taskId, string op, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TaskWaitTimeout;
        var netFails = 0;
        for (var i = 0; ; i++)
        {
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(await Http.GetStringAsync(
                    $"{DriveBase}/task?{Q}&task_id={taskId}&retry_index={i}", ct).ConfigureAwait(false)).RootElement;
            }
            catch (HttpRequestException)
            {
                netFails++;
                if (netFails >= PartAttempts)
                    throw new PanException($"夸克{op}任务轮询连续 {PartAttempts} 次网络失败，已中止（任务可能仍在服务端执行）");
                await Task.Delay(500, ct).ConfigureAwait(false);
                i--; // 失败不计入 retry_index
                continue;
            }
            netFails = 0;
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
            if (status is not (0 or 1))
                throw new PanException($"夸克{op}任务状态未知（status={status}），已中止轮询");
            if (DateTime.UtcNow > deadline)
                throw new PanException($"夸克{op}任务超时（{TaskWaitTimeout.TotalMinutes} 分钟无终态），任务可能仍在服务端执行");
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    public async Task<PanFile> CreateFolderAsync(PanFile? dir, string name, CancellationToken ct = default)
    {
        var data = await PostJsonAsync("file",
            new { pdir_fid = dir?.Id ?? "0", file_name = name, dir_init_lock = false, dir_path = "" },
            "新建文件夹", ct).ConfigureAwait(false);
        var fid = data.GetProperty("fid").GetString()!;
        var parentPath = dir?.Path?.TrimEnd('/') ?? "";
        return new PanFile(fid, parentPath + "/" + name, name, 0, true,
            DateTimeOffset.Now, null, PanProvider.Quark);
    }

    public async Task RenameAsync(PanFile file, string newName, CancellationToken ct = default) =>
        await PostJsonAsync("file/rename", new { fid = file.Id, file_name = newName }, "重命名", ct).ConfigureAwait(false);

    public async Task MoveAsync(IReadOnlyList<PanFile> files, PanFile? targetDir, CancellationToken ct = default)
    {
        var data = await PostJsonAsync("file/move",
            new
            {
                action_type = 1,
                to_pdir_fid = targetDir?.Id ?? "0",
                filelist = files.Select(f => f.Id).ToArray(),
                exclude_fids = Array.Empty<string>(),
            }, "移动", ct).ConfigureAwait(false);
        await FinishTaskOrThrowAsync(data, "移动", ct).ConfigureAwait(false);
    }

    public async Task CopyAsync(IReadOnlyList<PanFile> files, PanFile? targetDir, CancellationToken ct = default)
    {
        // file/copy（规格逆向自夸克桌面端 t.copy：action_type=ASYNC、copy_source=1，随后 task 轮询）
        var data = await PostJsonAsync("file/copy",
            new
            {
                to_pdir_fid = targetDir?.Id ?? "0",
                filelist = files.Select(f => f.Id).ToArray(),
                action_type = 2,
                copy_source = 1,
            }, "复制", ct).ConfigureAwait(false);
        await FinishTaskOrThrowAsync(data, "复制", ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(IReadOnlyList<PanFile> files, CancellationToken ct = default)
    {
        var data = await PostJsonAsync("file/delete",
            new
            {
                action_type = 2,
                filelist = files.Select(f => f.Id).ToArray(),
                exclude_fids = Array.Empty<string>(),
            }, "删除", ct).ConfigureAwait(false);
        await FinishTaskOrThrowAsync(data, "删除", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 异步任务型操作的收尾：finish=true 同步完成；有 task_id 进轮询；
    /// 两者皆无（响应缺 data / 空对象）视为状态不明抛错，不静默当成功。
    /// </summary>
    private async Task FinishTaskOrThrowAsync(JsonElement data, string op, CancellationToken ct)
    {
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("finish", out var fin) && fin.GetBoolean()) return;
            if (data.TryGetProperty("task_id", out var tid))
            {
                await WaitTaskAsync(tid.GetString()!, op, ct).ConfigureAwait(false);
                return;
            }
        }
        throw new PanException($"夸克{op}失败（响应既无 finish 也无 task_id，状态不明）");
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
            "转存-取stoken", ct).ConfigureAwait(false);
        var stoken = tokenData.GetProperty("stoken").GetString()!;

        // 拉分享根目录全部条目（转存按根级条目选；文件夹整体递归进）
        var fids = new List<string>();
        var fidTokens = new List<string>();
        for (var page = 1; ; page++)
        {
            var url = $"{DriveBase}/share/sharepage/detail?{Q}&pwd_id={pwdId}&stoken={Uri.EscapeDataString(stoken)}" +
                      $"&pdir_fid=0&force=0&_page={page}&_size=50&_sort=file_type:asc,updated_at:desc";
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false)).RootElement;
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

        // save 走 DriveBase（REA 逆向实证：客户端全量 clouddrive 端点统一挂 drive-pc.quark.cn，无 drive.quark.cn 主机）
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
        var resp = await Http.PostAsync($"{DriveBase}/share/sharepage/save?{Q}",
            new StringContent(saveBody, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var saveRoot = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement;
        ThrowIfNotOk(saveRoot, "转存-提交");
        var taskId = saveRoot.GetProperty("data").GetProperty("task_id").GetString()!;

        var done = await WaitTaskAsync(taskId, "转存", ct).ConfigureAwait(false);
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
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false)).RootElement;
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
            // 同列目录：按 _total 拉满，翻页超上限保护性报错
            var total = GetPagedTotal(root);
            if (total >= 0 ? shares.Count >= total : list.GetArrayLength() < 50) break;
            if (page >= MaxListPages)
                throw new PanException($"夸克分享列表翻页超过 {MaxListPages} 页仍未拉满（已取 {shares.Count}，服务端报 _total={total}），结果可能不完整，已中止");
        }
        return shares;
    }

    /// <summary>创建分享（3 步链：share → task 取 share_id → share/password 取链接）。</summary>
    public async Task<PanShare> CreateShareAsync(PanShareOptions options, CancellationToken ct = default)
    {
        // expired_type：永久=1 1天=2 7天=3 30天=4（QuarkPanTool 菜单映射）。非支持档位显式拒绝，
        // 不静默收敛到 7 天档（否则 ExpiresAt 按请求档位算会谎报过期时间）
        var expiredType = options.ExpireDays switch
        {
            0 => 1, 1 => 2, 7 => 3, 30 => 4,
            _ => throw new PanException($"夸克分享不支持 {options.ExpireDays} 天有效期（支持 0/1/7/30）"),
        };
        var hasPass = !string.IsNullOrEmpty(options.Passcode);
        var body = new Dictionary<string, object?>
        {
            ["fid_list"] = options.Files.Select(f => f.Id).ToArray(),
            ["title"] = options.Title,
            ["url_type"] = hasPass ? 2 : 1,
            ["expired_type"] = expiredType,
        };
        if (hasPass) body["passcode"] = options.Passcode;

        var data = await PostJsonAsync("share", body, "创建分享", ct).ConfigureAwait(false);
        var taskId = data.GetProperty("task_id").GetString()!;
        var done = await WaitTaskAsync(taskId, "创建分享", ct).ConfigureAwait(false);
        var shareId = done.GetProperty("share_id").GetString()!;

        var pw = await PostJsonAsync("share/password", new { share_id = shareId }, "取分享链接", ct).ConfigureAwait(false);
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
        await PostJsonAsync("share/delete", new { share_ids = shareIds.ToArray() }, "删除分享", ct).ConfigureAwait(false);

    // ============ P2 阶段3：上传（pre → 单遍流式哈希 → update/hash → 逐分片 auth+PUT OSS → auth+POST 合并 → finish） ============

    /// <summary>分片大小缺省 4MB；真实值以 pre 响应 metadata.part_size 为准（65536 的倍数，hash-ctx 边界前提）。</summary>
    private const int DefaultChunkSize = 4 * 1024 * 1024;
    private const string OssMobileUa = "aliyun-sdk-js/1.0.0 Chrome Mobile 139.0.0.0 on Google Nexus 5 (Android 6.0)";
    private const string OssDesktopUa = "aliyun-sdk-js/1.0.0 Chrome 139.0.0.0 on OS X 10.15.7 64-bit";
    /// <summary>续传记录保留期：服务端上传会话有效期未知且必然更短，超期记录续传只会白费一次往返。</summary>
    private static readonly TimeSpan ResumeMaxAge = TimeSpan.FromDays(7);
    /// <summary>callback 以紧凑 JSON 固化字节，续传复用期间保持逐字节一致（签名基于这份内容）。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions CompactJsonOpts = new() { WriteIndented = false };

    /// <summary>
    /// 上传断点续传的存取口（主机侧注入，可空）。未注入时无续传，行为退化为一次性上传。
    /// 协议层只按「分片成功即记、上传成功即清」使用它，落盘位置与格式由实现方决定。
    /// </summary>
    public IUploadResumeStore? UploadResumeStore { get; set; }

    /// <summary>一次分片上传会话的固定参数：全新上传取自 pre 响应，续传从落盘记录还原。</summary>
    private readonly record struct UploadSession(
        string TaskId, string AuthInfo, string UploadId, string ObjKey, string Bucket, string CallbackJson,
        string LocalPath, long FileSize, int ChunkSize, int PartCount, string Mime);

    /// <summary>
    /// 上传本地文件：pre 预创建（可能秒传命中，取 metadata.part_size）→ 单遍流式哈希
    /// （MD5+SHA1+分片中间态快照，O(n)）→ update/hash（可能二次秒传/重复备份）→
    /// 逐分片 auth+PUT OSS（3 次重试）→ auth+POST 合并 XML → finish。
    /// 注入续传存储时，失败重试/重新添加上传会按文件身份自动跳过已完成分片。
    /// </summary>
    public async Task<PanUploadResult> UploadAsync(
        string localPath, PanFile? targetDir,
        IProgress<PanUploadProgress>? progress = null, CancellationToken ct = default)
    {
        var fi = new FileInfo(localPath);
        if (!fi.Exists) throw new FileNotFoundException("本地文件不存在", localPath);
        var fileSize = fi.Length;
        var fileName = fi.Name;
        var mime = GuessMime(fileName);
        var pdirFid = targetDir?.Id ?? "0";
        var dirPath = (targetDir?.Path ?? "/").TrimEnd('/');
        var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        // 断点续传：按文件身份（完整路径+长度+修改时刻）查上次中断留下的上传会话；
        // 命中且身份吻合则复用 upload_id 跳过已完成分片。未注入存储时 resumeKey 为 null，行为与无续传一致。
        var resumeKey = UploadResumeStore == null
            ? null
            : ResumeKey(fi.FullName, fileSize, fi.LastWriteTimeUtc);
        var resume = LoadResumeEntry(UploadResumeStore, resumeKey, fi.FullName, fileSize, fi.LastWriteTimeUtc);
        if (resume != null)
        {
            var resumed = await TryResumeUploadAsync(resume, resumeKey!, fi, mime, dirPath, progress, ct).ConfigureAwait(false);
            if (resumed != null)
            {
                RemoveResume(resumeKey); // 续传完成，记录使命结束
                return resumed;
            }
            // 服务端拒绝复用该会话：丢弃记录，同一调用内落回全新上传
            RemoveResume(resumeKey);
        }

        // 上传准备期间的防变更基线：此刻快照，开始分片上传前复检
        var snapshot = (Length: fileSize, Lwt: fi.LastWriteTimeUtc);

        // 步骤1：pre。finish=true 即秒传命中（镜像客户端 end(data) 语义）；metadata.part_size 是服务端下发的真实分片大小。
        progress?.Report(new(fileSize, 0, 0, 0, "预创建"));
        var pre = await PostJsonAsync("file/upload/pre", new
        {
            ccp_hash_update = true,
            parallel_upload = true,
            pdir_fid = pdirFid,
            dir_name = "",
            size = fileSize,
            file_name = fileName,
            format_type = mime,
            l_updated_at = nowMs,
            l_created_at = nowMs,
        }, "上传-预创建", ct).ConfigureAwait(false);
        var rapid = pre.TryGetProperty("finish", out var fin) && fin.ValueKind == JsonValueKind.True;
        if (!rapid && !pre.TryGetProperty("upload_id", out _))
        {
            // pre 既非秒传也未下发 upload_id：没有可继续的上传会话。包络只列字段名不取值，避免回显未知内容
            var fields = pre.ValueKind == JsonValueKind.Object
                ? string.Join(",", pre.EnumerateObject().Select(p => p.Name))
                : pre.ValueKind.ToString();
            throw new PanException($"夸克上传预创建响应异常：既非秒传（finish≠true）也无 upload_id，无法继续上传（包络字段：{fields}）");
        }
        if (rapid)
        {
            RemoveResume(resumeKey); // 文件已在服务端，旧续传记录作废
            return new PanUploadResult(true, MapUploadFile(pre, dirPath, fileName, fileSize));
        }

        var chunkSize = DefaultChunkSize;
        if (pre.TryGetProperty("metadata", out var md) && md.TryGetProperty("part_size", out var ps)
            && ps.TryGetInt64(out var psVal) && psVal > 0)
        {
            if (psVal % 65536 != 0)
                throw new PanException($"服务端下发分片大小 {psVal} 不是 65536 的倍数，无法按分片上传");
            chunkSize = (int)Math.Min(psVal, int.MaxValue);
        }
        // 分片数一律按服务端规则值切满（末片自然收尾）：服务端按自身 part_size 校验分片几何，
        // 实测首片实际字节偏离规则值（如 1MB 片）会被 OSS 403 拒收——4~5MB 文件不能退化成
        // 单个不足规则值的分片。会话参数的一部分，提前算好随会话与续传记录保存。
        var partCount = Math.Max(1, (int)((fileSize + chunkSize - 1) / chunkSize));
        var session = new UploadSession(
            TaskId: pre.GetProperty("task_id").GetString()!,
            AuthInfo: pre.GetProperty("auth_info").GetString() ?? "",
            UploadId: pre.GetProperty("upload_id").GetString()!,
            ObjKey: pre.GetProperty("obj_key").GetString()!,
            Bucket: pre.TryGetProperty("bucket", out var bk) ? bk.GetString() ?? "ul-zb" : "ul-zb",
            // callback 先按紧凑 JSON 固化字节：合并阶段的 x-oss-callback 头与签名都基于这份内容，续传期间保持逐字节一致
            CallbackJson: JsonSerializer.Serialize(pre.GetProperty("callback"), CompactJsonOpts),
            LocalPath: localPath,
            FileSize: fileSize,
            ChunkSize: chunkSize,
            PartCount: partCount,
            Mime: mime);

        // 步骤2：单遍流式哈希。MD5（完整哈希）+ SHA1（完整哈希 + 每 chunkSize 边界的中间态快照），O(n) 一次扫盘。
        var (md5Hex, sha1Hex, ctxByBoundary) = await ComputeHashesAsync(localPath, fileSize, chunkSize, doneBaseline: 0, progress, ct).ConfigureAwait(false);

        // 步骤3：update/hash。finish=true 即二次秒传命中（镜像客户端 FLASH_FINISH → end(data)）；repeat_backup 镜像客户端抛错。
        var upData = await PostJsonAsync("file/update/hash", new { task_id = session.TaskId, md5 = md5Hex, sha1 = sha1Hex },
            "上传-更新哈希", ct).ConfigureAwait(false);
        if (upData.ValueKind == JsonValueKind.Object)
        {
            if (upData.TryGetProperty("finish", out var uf) && uf.ValueKind == JsonValueKind.True)
            {
                RemoveResume(resumeKey); // 二次秒传命中，旧续传记录作废
                return new PanUploadResult(true, MapUploadFile(upData, dirPath, fileName, fileSize));
            }
            if (upData.TryGetProperty("repeat_backup", out var ur) && ur.ValueKind == JsonValueKind.True)
                throw new PanException("夸克判定为重复备份文件（repeat_backup），已中止上传");
        }

        // 开始上传前复检文件身份：预创建与哈希期间文件被改（截短/换内容）会让登记哈希与实传内容不一致
        fi.Refresh();
        if (fi.Length != snapshot.Length || fi.LastWriteTimeUtc != snapshot.Lwt)
            throw new PanException("文件在上传准备期间已变更，上传已中止，请重新上传");

        // 建立续传记录（此后每片成功即刷新）：失败重试/重新添加上传时据此跳过已完成分片
        var etags = new string[partCount];
        UploadResumeEntry? resumeBase = null;
        if (resumeKey != null)
        {
            resumeBase = new UploadResumeEntry
            {
                LocalPath = fi.FullName,
                Length = fileSize,
                LastWriteTimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
                UploadId = session.UploadId,
                TaskId = session.TaskId,
                AuthInfo = session.AuthInfo,
                ObjKey = session.ObjKey,
                Bucket = session.Bucket,
                CallbackJson = session.CallbackJson,
                ChunkSize = chunkSize,
                PartCount = partCount,
                Md5 = md5Hex,
                Sha1 = sha1Hex,
                Etags = etags.Select(e => (string?)e).ToList(),
                UpdatedAt = DateTimeOffset.Now,
            };
            SaveResumeProgress(resumeKey, resumeBase, etags);
        }
        await UploadPartsAsync(session, etags, ctxByBoundary, resumeKey, resumeBase, resuming: false, progress, ct).ConfigureAwait(false);

        // 步骤5：合并（auth+POST CompleteMultipartUpload；2xx 均视为成功，与客户端一致）
        progress?.Report(new(fileSize, fileSize, partCount, partCount, "合并"));
        await CompleteMultipartAsync(session, etags, ct).ConfigureAwait(false);

        // 步骤6：finish 通知夸克
        var done = await PostJsonAsync("file/upload/finish", new { task_id = session.TaskId, obj_key = session.ObjKey },
            "上传-完成", ct).ConfigureAwait(false);
        RemoveResume(resumeKey); // 上传成功，续传记录清掉
        return new PanUploadResult(false, MapUploadFile(done, dirPath, fileName, fileSize));
    }

    /// <summary>
    /// 单个分片：auth 换 auth_key，PUT 到 {bucket}.pds.quark.cn，返回 ETag（去引号）。
    /// 3 次尝试、间隔 500ms（网络错误/5xx/超时才重试）；每次尝试独立 120s 超时；
    /// 签名串与实际 URL 统一用转义后的 uploadId（OSS canonical resource 与 URL 同源，与客户端一致）。
    /// </summary>
    private async Task<string> PutPartAsync(UploadSession session, int partNumber, byte[] partBuf, string? hashCtx, CancellationToken ct)
    {
        var escapedUploadId = Uri.EscapeDataString(session.UploadId);
        for (var attempt = 1; ; attempt++)
        {
            var ossDate = OssDate();
            var authMeta = hashCtx == null
                ? $"PUT\n\n{session.Mime}\n{ossDate}\nx-oss-date:{ossDate}\nx-oss-user-agent:{OssMobileUa}\n/{session.Bucket}/{session.ObjKey}?partNumber={partNumber}&uploadId={escapedUploadId}"
                : $"PUT\n\n{session.Mime}\n{ossDate}\nx-oss-date:{ossDate}\nx-oss-hash-ctx:{hashCtx}\nx-oss-user-agent:{OssMobileUa}\n/{session.Bucket}/{session.ObjKey}?partNumber={partNumber}&uploadId={escapedUploadId}";
            var authData = await PostJsonAsync("file/upload/auth", new { task_id = session.TaskId, auth_info = session.AuthInfo, auth_meta = authMeta },
                $"上传-分片{partNumber}授权", ct).ConfigureAwait(false);
            var authKey = authData.TryGetProperty("auth_key", out var ak) ? ak.GetString() : null;
            if (string.IsNullOrEmpty(authKey))
                throw new PanException($"夸克上传分片{partNumber}授权响应缺 auth_key，无法签名分片请求");

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(PartTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Put,
                    $"https://{session.Bucket}.pds.quark.cn/{session.ObjKey}?partNumber={partNumber}&uploadId={escapedUploadId}")
                { Content = new ByteArrayContent(partBuf) };
                req.Content.Headers.TryAddWithoutValidation("Content-Type", session.Mime);
                req.Headers.TryAddWithoutValidation("x-oss-date", ossDate);
                req.Headers.TryAddWithoutValidation("x-oss-user-agent", OssMobileUa);
                req.Headers.TryAddWithoutValidation("authorization", authKey);
                if (hashCtx != null) req.Headers.TryAddWithoutValidation("X-Oss-Hash-Ctx", hashCtx);

                var resp = await Http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if ((int)resp.StatusCode >= 500 && attempt < PartAttempts)
                    {
                        await Task.Delay(PartRetryDelay, ct).ConfigureAwait(false);
                        continue;
                    }
                    // HTTP 状态放进 Code：续传路径据此识别「服务端拒绝该会话」并回落全新上传
                    throw new PanException($"夸克上传分片{partNumber}失败 {(int)resp.StatusCode} {body[..Math.Min(body.Length, 200)]}",
                        (int)resp.StatusCode);
                }
                var etag = (resp.Headers.ETag?.Tag ?? "").Trim('"');
                if (etag.Length == 0) etag = resp.Headers.TryGetValues("etag", out var ev) ? ev.First().Trim('"') : "";
                if (etag.Length == 0)
                    throw new PanException($"夸克上传分片{partNumber}成功但未取到ETag");
                return etag;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 单次尝试超时（网络过慢）
                if (attempt >= PartAttempts)
                    throw new PanException($"夸克上传分片{partNumber}超时（每次尝试 {PartTimeout.TotalSeconds}s，已重试 {PartAttempts} 次）");
                await Task.Delay(PartRetryDelay, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < PartAttempts)
            {
                await Task.Delay(PartRetryDelay, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // 末次尝试的网络异常按业务口径收口，不把传输层异常类型裸透传给 UI
                throw new PanException($"夸克上传分片{partNumber}网络错误（已重试 {PartAttempts} 次）：{ex.Message}");
            }
        }
    }

    /// <summary>合并：auth（upload_mode=complete）换 POST 授权，POST XML 到 OSS。2xx 均视为成功（与客户端一致）。</summary>
    private async Task CompleteMultipartAsync(UploadSession session, string[] etags, CancellationToken ct)
    {
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<CompleteMultipartUpload>\n" +
            string.Join("\n", etags.Select((e, i) =>
                $"<Part>\n<PartNumber>{i + 1}</PartNumber>\n<ETag>\"{e}\"</ETag>\n</Part>")) +
            "\n</CompleteMultipartUpload>";
        var xmlMd5 = Convert.ToBase64String(
            System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(xml)));
        var callbackB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(session.CallbackJson));
        var ossDate = OssDate();
        var escapedUploadId = Uri.EscapeDataString(session.UploadId);
        var authMeta = $"POST\n{xmlMd5}\napplication/xml\n{ossDate}\nx-oss-callback:{callbackB64}\nx-oss-date:{ossDate}\nx-oss-user-agent:{OssDesktopUa}\n/{session.Bucket}/{session.ObjKey}?uploadId={escapedUploadId}";

        var authData = await PostJsonAsync("file/upload/auth", new { task_id = session.TaskId, auth_meta = authMeta, auth_info = session.AuthInfo },
            "上传-合并授权", ct).ConfigureAwait(false);
        var authKey = authData.TryGetProperty("auth_key", out var ak) ? ak.GetString() : null;
        if (string.IsNullOrEmpty(authKey))
            throw new PanException("夸克上传合并授权响应缺 auth_key，无法签名合并请求");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://{session.Bucket}.pds.quark.cn/{session.ObjKey}?uploadId={escapedUploadId}");
        // Content-Type 必须恰为 application/xml（无 charset），否则与签名串不符 → SignatureDoesNotMatch
        var content = new StringContent(xml, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
        req.Content = content;
        req.Headers.TryAddWithoutValidation("x-oss-date", ossDate);
        req.Headers.TryAddWithoutValidation("x-oss-user-agent", OssDesktopUa);
        req.Headers.TryAddWithoutValidation("x-oss-callback", callbackB64);
        req.Content.Headers.TryAddWithoutValidation("Content-MD5", xmlMd5);
        req.Headers.TryAddWithoutValidation("authorization", authKey);

        var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new PanException($"夸克上传合并失败 {(int)resp.StatusCode} {body[..Math.Min(body.Length, 200)]}", (int)resp.StatusCode);
        }
    }

    private static string OssDate() => DateTimeOffset.UtcNow.ToString(
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>续传键：文件身份（完整路径+长度+修改时刻 UTC ticks）的稳定哈希。</summary>
    private static string ResumeKey(string fullPath, long length, DateTime lastWriteUtc)
    {
        var src = $"{fullPath}\n{length}\n{lastWriteUtc.Ticks}";
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(src))).ToLowerInvariant();
    }

    /// <summary>
    /// 读续传记录并校验：身份不吻合、超过保留期或缺关键会话字段都按无记录处理并清掉废记录。
    /// 存储故障一律按无记录降级：续传是上传的增强能力，存储不允许反过来打断上传。
    /// </summary>
    private static UploadResumeEntry? LoadResumeEntry(
        IUploadResumeStore? store, string? key, string fullPath, long length, DateTime lastWriteUtc)
    {
        if (store == null || key == null) return null;
        UploadResumeEntry? entry;
        try { entry = store.Load(key); }
        catch { return null; }
        if (entry == null) return null;
        var valid = entry.UpdatedAt >= DateTimeOffset.Now - ResumeMaxAge
            && entry.LocalPath == fullPath && entry.Length == length
            && entry.LastWriteTimeUtcTicks == lastWriteUtc.Ticks
            && !string.IsNullOrEmpty(entry.UploadId) && !string.IsNullOrEmpty(entry.TaskId);
        if (!valid)
        {
            try { store.Remove(key); } catch { /* 清不掉只是留一条废记录，保留期后不再命中 */ }
            return null;
        }
        return entry;
    }

    /// <summary>分片成功后刷新续传记录（每片一记，失败重试才有真实断点）。写失败只损失续传能力，不打断上传。</summary>
    private void SaveResumeProgress(string? resumeKey, UploadResumeEntry? resumeBase, string[] etags)
    {
        if (resumeKey == null || resumeBase == null) return;
        try
        {
            UploadResumeStore?.Save(resumeKey, resumeBase with
            {
                Etags = etags.Select(e => string.IsNullOrEmpty(e) ? (string?)null : e).ToList(),
                UpdatedAt = DateTimeOffset.Now,
            });
        }
        catch { /* 同上：记录写失败只损失续传能力 */ }
    }

    private void RemoveResume(string? resumeKey)
    {
        if (resumeKey == null) return;
        try { UploadResumeStore?.Remove(resumeKey); }
        catch { /* 清理失败仅遗留废记录，保留期后不再命中 */ }
    }

    /// <summary>
    /// 单遍流式哈希：MD5 完整值 + SHA1 完整值 + 按 chunkSize 边界的 SHA1 中间态快照，O(n) 一次扫盘。
    /// 全新上传与续传复用（续传时用快照重建分片中间态，并复核内容身份）。
    /// </summary>
    private async Task<(string Md5Hex, string Sha1Hex, List<string> CtxByBoundary)> ComputeHashesAsync(
        string localPath, long fileSize, int chunkSize, long doneBaseline,
        IProgress<PanUploadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new(fileSize, doneBaseline, 0, 0, "计算哈希"));
        var sha1 = new Sha1Incremental();
        using var md5 = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.MD5);
        var ctxByBoundary = new List<string> { "" }; // [k] = 前 k*chunkSize 字节的中间态；[0] 占位（part1 无 ctx）
        var buffer = new byte[1024 * 1024];
        var totalRead = 0L;
        var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true);
        await using (fs.ConfigureAwait(false))
        {
            int read;
            while ((read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                md5.AppendData(buffer, 0, read);
                var bufStart = totalRead;   // 本次读起始的绝对偏移
                totalRead += read;
                // 按分片边界逐段 Feed、到边界立即拍快照：chunkSize 非 1MB 倍数时一次读会跨多个
                // 分片边界，整段 Feed 完再拍会把中间分片的 ctx 全拍成同一错态。
                var consumed = 0;
                while (consumed < read)
                {
                    var nextBoundary = (long)ctxByBoundary.Count * chunkSize;
                    var segEnd = (int)(Math.Min(totalRead, nextBoundary) - bufStart);
                    sha1.Feed(buffer.AsSpan(consumed, segEnd - consumed));
                    consumed = segEnd;
                    if (bufStart + consumed == nextBoundary)
                        ctxByBoundary.Add(sha1.SnapshotCtx());
                }
            }
        }
        // 读循环提前退出（返回 0）而实读不足预期，说明哈希期间文件被截短或不可读：
        // 继续走只会登记一份与真实内容不符的哈希
        if (totalRead != fileSize)
            throw new PanException($"上传文件读取中断：文件在计算哈希期间发生变更（预期 {fileSize} 字节，实读 {totalRead}）");
        return (Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(), sha1.FinalizeHex(), ctxByBoundary);
    }

    /// <summary>
    /// 按续传记录继续上传：复用 upload_id，跳过已完成分片（进度基线=已跳过分片的累计字节）。
    /// 返回 null = 服务端拒绝复用该会话（分片 4xx / 合并失败 / finish 失败），调用方应丢弃记录走全新上传；
    /// 正常返回 = 续传完成；抛出 = 传输层失败（网络/超时/5xx/文件读取），记录保留待下次继续。
    /// </summary>
    private async Task<PanUploadResult?> TryResumeUploadAsync(
        UploadResumeEntry entry, string resumeKey, FileInfo fi, string mime, string dirPath,
        IProgress<PanUploadProgress>? progress, CancellationToken ct)
    {
        var fileSize = fi.Length;
        var fileName = fi.Name;
        // 分片大小先过规则校验再参与运算，避免除零/负数
        var chunkSize = (int)entry.ChunkSize;
        if (chunkSize <= 0 || chunkSize % 65536 != 0) return null;
        var partCount = Math.Max(1, (int)((fileSize + chunkSize - 1) / chunkSize));
        if (partCount != entry.PartCount || entry.Etags.Count != partCount) return null;

        // 开始续传前复检文件身份：记录之后文件已变则记录作废
        fi.Refresh();
        if (fi.Length != entry.Length || fi.LastWriteTimeUtc.Ticks != entry.LastWriteTimeUtcTicks) return null;

        var etags = new string[partCount];
        var hasPending = false;
        var pendingGe2 = false;
        for (var i = 0; i < partCount; i++)
        {
            if (entry.Etags[i] is { Length: > 0 } done) etags[i] = done;
            else { hasPending = true; pendingGe2 |= i >= 1; }
        }

        // 进度基线：已跳过分片的累计字节，UI 从这里起步而不是归零
        long baselineBytes = 0;
        var baselineParts = 0;
        for (var i = 0; i < partCount; i++)
        {
            if (etags[i] == null) continue;
            baselineParts = i + 1;
            baselineBytes = Math.Min((long)(i + 1) * chunkSize, fileSize);
        }
        progress?.Report(new(fileSize, baselineBytes, partCount, baselineParts, "续传检查"));

        List<string> ctxByBoundary;
        if (hasPending && pendingGe2)
        {
            // partNumber≥2 的分片要带 X-Oss-Hash-Ctx（SHA1 前缀中间态），只能重扫文件重建；
            // 重扫同时复核内容身份：哈希对不上说明内容已变，记录作废走全新上传
            var hashes = await ComputeHashesAsync(fi.FullName, fileSize, chunkSize, baselineBytes, progress, ct).ConfigureAwait(false);
            if (hashes.Md5Hex != entry.Md5 || hashes.Sha1Hex != entry.Sha1) return null;
            ctxByBoundary = hashes.CtxByBoundary;
        }
        else
        {
            ctxByBoundary = new List<string> { "" };
        }

        var session = new UploadSession(entry.TaskId, entry.AuthInfo, entry.UploadId, entry.ObjKey, entry.Bucket,
            entry.CallbackJson, fi.FullName, fileSize, chunkSize, partCount, mime);

        var accepted = await UploadPartsAsync(session, etags, ctxByBoundary, resumeKey, entry, resuming: true, progress, ct).ConfigureAwait(false);
        if (!accepted) return null;

        progress?.Report(new(fileSize, fileSize, partCount, partCount, "合并"));
        try
        {
            await CompleteMultipartAsync(session, etags, ct).ConfigureAwait(false);
        }
        catch (PanException)
        {
            return null; // 合并被服务端拒绝：会话已失效，由调用方丢弃记录走全新上传
        }

        try
        {
            var done = await PostJsonAsync("file/upload/finish", new { task_id = session.TaskId, obj_key = session.ObjKey },
                "上传-完成", ct).ConfigureAwait(false);
            return new PanUploadResult(false, MapUploadFile(done, dirPath, fileName, fileSize));
        }
        catch (PanException)
        {
            return null; // finish 被服务端拒绝：会话已失效，同上
        }
    }

    /// <summary>
    /// 逐分片上传：etags 已有值的分片跳过（断点续传）；新完成一片即刷新续传记录。
    /// 返回 false = 服务端拒绝该会话的分片请求（仅续传模式可能），调用方丢弃记录从头传；
    /// 文件读取异常按截短显式报错，不静默吞掉。
    /// </summary>
    private async Task<bool> UploadPartsAsync(
        UploadSession session, string[] etags, List<string> ctxByBoundary,
        string? resumeKey, UploadResumeEntry? resumeBase, bool resuming,
        IProgress<PanUploadProgress>? progress, CancellationToken ct)
    {
        var fs = new FileStream(session.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true);
        await using (fs.ConfigureAwait(false))
        {
            for (var seq = 1; seq <= session.PartCount; seq++)
            {
                ct.ThrowIfCancellationRequested();
                var offset = (long)(seq - 1) * session.ChunkSize;
                var partLen = (int)Math.Min(session.ChunkSize, session.FileSize - offset);
                if (etags[seq - 1] != null)
                {
                    // 断点续传：跳过已完成分片，进度按累计字节推进
                    progress?.Report(new(session.FileSize, offset + partLen, session.PartCount, seq, "续传跳过已传分片"));
                    continue;
                }

                var partBuf = new byte[partLen];
                fs.Seek(offset, SeekOrigin.Begin);
                var read = 0;
                while (read < partLen)
                {
                    var n = await fs.ReadAsync(partBuf.AsMemory(read, partLen - read), ct).ConfigureAwait(false);
                    // ReadAsync 返 0 意味着流到头：文件比开始上传时短（被截短），继续凑读会无限空转
                    if (n == 0)
                        throw new PanException($"上传分片{seq}读取失败：文件被截短（该分片期望 {partLen} 字节，实读 {read}），已中止");
                    read += n;
                }

                // X-Oss-Hash-Ctx：part>=2 带前缀中间态（流式快照，O(n) 总量）
                var hashCtx = seq > 1 && seq - 1 < ctxByBoundary.Count ? ctxByBoundary[seq - 1] : null;

                progress?.Report(new(session.FileSize, offset, session.PartCount, seq - 1, "上传分片"));
                try
                {
                    etags[seq - 1] = await PutPartAsync(session, seq, partBuf, hashCtx, ct).ConfigureAwait(false);
                }
                catch (PanException ex) when (resuming && ex.Code is >= 400 and < 500)
                {
                    return false; // 服务端拒绝该会话（如 upload_id 已失效），由调用方丢弃记录从头传
                }
                progress?.Report(new(session.FileSize, offset + partLen, session.PartCount, seq, "上传分片"));
                SaveResumeProgress(resumeKey, resumeBase, etags);
            }
        }
        return true;
    }

    private static PanFile MapUploadFile(JsonElement d, string dirPath, string fallbackName, long fallbackSize)
    {
        var id = d.TryGetProperty("fid", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()!
            : d.TryGetProperty("fid", out var f2) && f2.ValueKind == JsonValueKind.Number ? f2.GetInt64().ToString() : "0";
        var name = d.TryGetProperty("file_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : fallbackName;
        var size = d.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : fallbackSize;
        return new PanFile(id, (dirPath == "" ? "" : dirPath) + "/" + name, name, size, false,
            DateTimeOffset.Now, null, PanProvider.Quark);
    }

    private static string GuessMime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".txt" => "text/plain", ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png",
        ".gif" => "image/gif", ".webp" => "image/webp", ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska", ".avi" => "video/x-msvideo", ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg", ".flac" => "audio/flac", ".wav" => "audio/wav",
        ".pdf" => "application/pdf", ".zip" => "application/zip",
        ".7z" => "application/x-7z-compressed", ".rar" => "application/vnd.rar",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream",
    };

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

    public void Dispose()
    {
        _http?.Dispose();
        _http = null;   // 置 null 让 IsAuthenticated 归 false，后续调用走 Http 守卫报「未登录」，而不是打到已释放实例抛 ObjectDisposedException
    }
}
