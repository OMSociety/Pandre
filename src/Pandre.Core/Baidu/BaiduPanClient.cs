using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>下载链有效期经验值：dlink 有时效，过期需重取。</summary>
    private static readonly TimeSpan DownloadLinkTtl = TimeSpan.FromHours(4);

    public PanProvider Provider => PanProvider.Baidu;
    public bool IsAuthenticated => _http != null;

    public void Attach(PanCredential credential)
    {
        if (credential.Provider != PanProvider.Baidu)
            throw new ArgumentException("凭据提供方不匹配", nameof(credential));
        Dispose(); // 重复 Attach 先释放旧客户端，避免泄漏
        _jar = PanHttp.SeedCookies(credential.Cookie, PanHttp.BaiduCookieHosts);
        _http = PanHttp.CreateClient(_jar, PanHttp.BrowserUa);
        _http.DefaultRequestHeaders.Referrer = new Uri("https://pan.baidu.com/disk/home");
        _nickname = credential.Nickname;
    }

    public string? ExportCookie() =>
        _jar == null ? null : PanHttp.JarToCookieString(_jar, PanHttp.BaiduCookieHosts);

    private HttpClient Http => _http ?? throw new InvalidOperationException("未登录：先 Attach");

    /// <summary>errno != 0 时抛出；-6/110 为登录态失效。响应缺 errno 字段时在错误信息里标注。</summary>
    private static void ThrowIfErrno(JsonElement root, string op)
    {
        var hasErrno = root.TryGetProperty("errno", out var e);
        var errno = hasErrno ? e.GetInt32() : 0;
        if (errno != 0)
        {
            var missing = !hasErrno ? "（响应缺 errno 包络字段）" : "";
            throw new PanException($"百度{op}失败 errno={errno}{missing}", errno);
        }
    }

    public async Task<PanAccountInfo> GetAccountAsync(CancellationToken ct = default)
    {
        var root = JsonDocument.Parse(await Http.GetStringAsync(
            $"{ApiBase}/api/quota?checkfree=1&checkexpire=1", ct).ConfigureAwait(false)).RootElement;
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
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false)).RootElement;
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
            var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false)).RootElement;
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
        var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false)).RootElement;
        ThrowIfErrno(root, "取下载链");

        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = PanHttp.BaiduNetdiskUa,
            ["Cookie"] = cookie,
        };
        var byPath = new Dictionary<string, PanFile>();
        foreach (var f in files)
            byPath[f.Path] = f; // 重路径允许（UI 可能有重复行），取后者
        var links = new List<PanDownloadLink>();
        // 注意：filemetas 的条目在 info 数组下（不是 list）
        foreach (var it in root.GetProperty("info").EnumerateArray())
        {
            if (it.TryGetProperty("errno", out var ierr) && ierr.GetInt32() != 0) continue;
            var path = it.GetProperty("path").GetString()!;
            if (!it.TryGetProperty("dlink", out var dl) || dl.ValueKind != JsonValueKind.String) continue;
            var dlink = dl.GetString()!;
            if (byPath.TryGetValue(path, out var file))
                links.Add(new PanDownloadLink(file, dlink, headers, DateTimeOffset.Now + DownloadLinkTtl));
        }
        if (links.Count < files.Count)
            throw new PanException($"百度取下载链不完整：请求 {files.Count} 个，仅返回 {links.Count} 个");
        return links;
    }

    /// <summary>按 fs_id 解析下载链：filemetas 支持 fsids 参数，一次拿回 path/name/size/dlink（探针实测 errno=0）。</summary>
    public async Task<IReadOnlyList<PanDownloadLink>> GetDownloadLinksByIdsAsync(
        IReadOnlyList<PanRemoteFileRef> refs, CancellationToken ct = default)
    {
        if (refs.Count == 0) return [];
        var cookie = ExportCookie() ?? throw new InvalidOperationException("未登录");
        var fsids = JsonSerializer.Serialize(refs.Select(r => r.Id).ToArray());
        var url = $"{ApiBase}/api/filemetas?fsids={Uri.EscapeDataString(fsids)}&dlink=1&web=1";
        var root = JsonDocument.Parse(await Http.GetStringAsync(url, ct).ConfigureAwait(false)).RootElement;
        ThrowIfErrno(root, "取下载链");

        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = PanHttp.BaiduNetdiskUa,
            ["Cookie"] = cookie,
        };
        var links = new List<PanDownloadLink>();
        foreach (var it in root.GetProperty("info").EnumerateArray())
        {
            if (it.TryGetProperty("errno", out var ierr) && ierr.GetInt32() != 0) continue;
            if (!it.TryGetProperty("dlink", out var dl) || dl.ValueKind != JsonValueKind.String) continue;
            var path = it.GetProperty("path").GetString()!;
            var file = new PanFile(
                it.GetProperty("fs_id").ToString(), path,
                it.GetProperty("server_filename").GetString()!,
                it.GetProperty("size").GetInt64(),
                false, null, null, PanProvider.Baidu);
            links.Add(new PanDownloadLink(file, dl.GetString()!, headers, DateTimeOffset.Now + DownloadLinkTtl));
        }
        if (links.Count < refs.Count)
            throw new PanException($"百度取下载链不完整：请求 {refs.Count} 个，仅返回 {links.Count} 个（文件可能不属于当前账号）");
        return links;
    }

    // ============ P2：上传（规格逆向自 BaiduPCS-Go，见 docs/开发文档.md §3） ============

    // 分块阈值：<8GB→4MB，≥8GB→16MB，≥32GB→64MB（getBlockSize 口径）
    private const long BlockThreshold8G = 8L * 1024 * 1024 * 1024;
    private const long BlockThreshold32G = 32L * 1024 * 1024 * 1024;
    private const int SliceMd5Size = 262144;   // 256KB：slice-md5 只算文件前 256KB
    private const int DataContentSize = 4096;  // data_content 取 4KB
    private const string PanAppId = "250528";  // Pan web 端点
    private const string PcsAppId = "266719";  // PCS REST 端点

    private static int BlockSize(long fileSize) =>
        fileSize >= BlockThreshold32G ? 64 * 1024 * 1024
        : fileSize >= BlockThreshold8G ? 16 * 1024 * 1024
        : 4 * 1024 * 1024;

    private static string Md5Hex(byte[] data) =>
        Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    /// <summary>uk：api/user/getinfo?need_selfinfo=1 → records[0].uk（秒传 data_offset 依赖）。</summary>
    private async Task<long> GetUkAsync(CancellationToken ct)
    {
        var root = JsonDocument.Parse(await Http.GetStringAsync(
            $"{ApiBase}/api/user/getinfo?need_selfinfo=1", ct).ConfigureAwait(false)).RootElement;
        ThrowIfErrno(root, "取uk");
        return root.GetProperty("records")[0].GetProperty("uk").GetInt64();
    }

    /// <summary>流式算整文件 MD5、前 256KB 的 slice-MD5、分块 MD5 列表。</summary>
    private static async Task<(string contentMd5, string sliceMd5, List<string> blockList)> HashFileAsync(
        string localPath, int blockSize, CancellationToken ct)
    {
        await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true);
        using var contentHasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sliceHasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var blockList = new List<string>();
        var blockBuf = new byte[blockSize];
        var blockFill = 0;
        var sliceLeft = SliceMd5Size;
        var readBuf = new byte[1024 * 1024];
        int n;
        while ((n = await fs.ReadAsync(readBuf, ct).ConfigureAwait(false)) > 0)
        {
            contentHasher.AppendData(readBuf, 0, n);
            if (sliceLeft > 0)
            {
                var take = Math.Min(n, sliceLeft);
                sliceHasher.AppendData(readBuf, 0, take);
                sliceLeft -= take;
            }
            var off = 0;
            while (off < n)
            {
                var take = Math.Min(n - off, blockSize - blockFill);
                Array.Copy(readBuf, off, blockBuf, blockFill, take);
                blockFill += take; off += take;
                if (blockFill == blockSize)
                {
                    blockList.Add(Md5Hex(blockBuf));
                    blockFill = 0;
                }
            }
        }
        if (blockFill > 0)
            blockList.Add(Md5Hex(blockBuf.AsSpan(0, blockFill).ToArray()));
        return (Convert.ToHexString(contentHasher.GetHashAndReset()).ToLowerInvariant(),
                Convert.ToHexString(sliceHasher.GetHashAndReset()).ToLowerInvariant(),
                blockList);
    }

    /// <summary>上传本地文件（precreate 秒传 → superfile2 分片 → create 合并）。</summary>
    public async Task<PanUploadResult> UploadAsync(
        string localPath, PanFile? targetDir, PanConflictPolicy policy = PanConflictPolicy.Skip,
        IProgress<PanUploadProgress>? progress = null, CancellationToken ct = default)
    {
        var fi = new FileInfo(localPath);
        if (!fi.Exists) throw new FileNotFoundException("本地文件不存在", localPath);
        var fileSize = fi.Length;
        var fileName = fi.Name;
        var targetDirPath = (targetDir?.Path ?? "/").TrimEnd('/');
        if (targetDirPath == "") targetDirPath = "/";
        var targetPath = (targetDirPath == "/" ? "" : targetDirPath) + "/" + fileName;
        var rtype = policy == PanConflictPolicy.Skip ? "2" : "3";
        var blockSize = BlockSize(fileSize);

        progress?.Report(new(fileSize, 0, 0, 0, "计算哈希"));
        var (contentMd5, sliceMd5, blockList) = await HashFileAsync(localPath, blockSize, ct).ConfigureAwait(false);
        var uk = await GetUkAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();

        // data_offset 算法：MD5(uk + contentMd5 + dataTime) 前8hex→uint → % (size-4096+1)
        var dataTime = now;
        var mixed = Md5Hex(Encoding.UTF8.GetBytes($"{uk}{contentMd5}{dataTime}"));
        var rawOffset = Convert.ToUInt32(mixed[..8], 16);
        long dataOffset = (fileSize - DataContentSize + 1) <= 1 ? 0
            : rawOffset % (fileSize - DataContentSize + 1);
        string dataContent;
        int dataLength;
        {
            await using var fs = File.OpenRead(localPath);
            fs.Seek(dataOffset, SeekOrigin.Begin);
            var buf = new byte[Math.Min(DataContentSize, fileSize - dataOffset)];
            dataLength = await fs.ReadAsync(buf, ct).ConfigureAwait(false);
            dataContent = Convert.ToBase64String(buf, 0, dataLength).TrimEnd('=');
        }
        var blockListJson = "[\"" + string.Join("\",\"", blockList) + "\"]";

        // ---- 步骤1：precreate（return_type=2 秒传命中；=1 需真传，取 uploadid）----
        progress?.Report(new(fileSize, 0, blockList.Count, 0, "预创建"));
        var precreateForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["path"] = targetPath,
            ["target_path"] = targetDirPath == "/" ? "/" : targetDirPath + "/",
            ["size"] = fileSize.ToString(),
            ["data_offset"] = dataOffset.ToString(),
            ["isdir"] = "0",
            ["local_mtime"] = now.ToString(),
            ["local_ctime"] = now.ToString(),
            ["rtype"] = rtype,
            ["checkexist"] = "0",
            ["autoinit"] = "1",
            ["content-md5"] = contentMd5,
            ["slice-md5"] = sliceMd5,
            ["data_time"] = dataTime.ToString(),
            ["data_length"] = dataLength.ToString(),
            ["data_content"] = dataContent,
            ["block_list"] = blockListJson,
            ["mode"] = "1",
        });
        var preResp = await Http.PostAsync($"{ApiBase}/api/precreate", precreateForm, ct).ConfigureAwait(false);
        var preText = await preResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var preRoot = JsonDocument.Parse(preText).RootElement;
        ThrowIfErrno(preRoot, "上传-预创建");
        var returnType = preRoot.TryGetProperty("return_type", out var rt) ? rt.GetInt32() : 1;

        if (returnType == 2)
        {
            // 秒传命中：文件信息可能在 info 里，也可能平铺在顶层
            var info = preRoot.TryGetProperty("info", out var inf) && inf.ValueKind == JsonValueKind.Object
                ? inf : preRoot;
            try { return new PanUploadResult(true, MapFile(info)); }
            catch (Exception)
            {
                throw new PanException($"百度上传-秒传响应字段不符：{preText[..Math.Min(preText.Length, 500)]}");
            }
        }

        if (!preRoot.TryGetProperty("uploadid", out var uidEl))
            throw new PanException($"百度上传-预创建响应异常：{preText[..Math.Min(preText.Length, 400)]}");
        var uploadId = uidEl.GetString()!;

        // ---- 步骤0：取上传服务器（locateupload，主机名含 '-' 优先）----
        var locRoot = JsonDocument.Parse(await Http.GetStringAsync(
            $"https://pcs.baidu.com/rest/2.0/pcs/file?app_id={PanAppId}&method=locateupload&upload_version=2.0", ct).ConfigureAwait(false)).RootElement;
        var pcsHost = "pcs.baidu.com";
        if (locRoot.TryGetProperty("servers", out var servers))
            foreach (var s in servers.EnumerateArray())
                if (s.TryGetProperty("server", out var sv))
                {
                    var host = new Uri(sv.GetString()!).Host;
                    if (host.Contains('-')) { pcsHost = host; break; }
                    pcsHost = host; // 退化：用最后一个
                }

        // ---- 步骤2：superfile2 逐分片上传 ----
        var partCount = blockList.Count;
        await using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true))
        {
            for (var seq = 0; seq < partCount; seq++)
            {
                ct.ThrowIfCancellationRequested();
                var partOffset = (long)seq * blockSize;
                var partLen = (int)Math.Min(blockSize, fileSize - partOffset);
                var partBuf = new byte[partLen];
                fs.Seek(partOffset, SeekOrigin.Begin);
                var read = 0;
                while (read < partLen)
                    read += await fs.ReadAsync(partBuf.AsMemory(read, partLen - read), ct).ConfigureAwait(false);

                using var content = new MultipartFormDataContent();
                // 字段必须带文件名（BaiduPCS-Go：name="file"; filename="blob"），不带文件名会被边缘直接 RST
                content.Add(new ByteArrayContent(partBuf), "file", "blob");
                var url = $"https://{pcsHost}/rest/2.0/pcs/superfile2" +
                          $"?app_id={PcsAppId}&method=upload&type=tmpfile" +
                          $"&path={Uri.EscapeDataString(targetPath)}&partseq={seq}&partoffset={partOffset}" +
                          $"&uploadid={Uri.EscapeDataString(uploadId)}&vip=1";
                // PCS 端点要网盘客户端 UA（浏览器 UA 会被服务器断连），独立请求换 UA
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.TryAddWithoutValidation("User-Agent", PanHttp.BaiduNetdiskClientUa);
                req.Headers.ExpectContinue = false; // .NET 默认发 100-continue，百度边缘不吃
                var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                // PCS 回包 charset=utf8（无连字符）.NET 不认，手动解码
                var text = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("error_code", out var ec) && ec.GetInt32() != 0)
                {
                    var emsg = root.TryGetProperty("error_msg", out var em) ? em.GetString() : "";
                    throw new PanException($"百度上传-分片{seq}失败 error_code={ec.GetInt32()} {emsg}", ec.GetInt32());
                }
                progress?.Report(new(fileSize, partOffset + partLen, partCount, seq + 1, "上传分片"));
            }
        }

        // ---- 步骤3：create 合并 ----
        progress?.Report(new(fileSize, fileSize, partCount, partCount, "合并"));
        var createForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["uploadid"] = uploadId,
            ["path"] = targetPath,
            ["size"] = fileSize.ToString(),
            ["isdir"] = "0",
            ["rtype"] = rtype,
            ["block_list"] = blockListJson,
            ["target_path"] = targetDirPath, // 合并的 target_path 不带结尾 "/"
        });
        var createResp = await Http.PostAsync($"{ApiBase}/api/create", createForm, ct).ConfigureAwait(false);
        var createText = await createResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var createRoot = JsonDocument.Parse(createText).RootElement;
        ThrowIfErrno(createRoot, "上传-合并");
        if (!createRoot.TryGetProperty("fs_id", out _))
            throw new PanException($"百度上传-合并响应异常：{createText[..Math.Min(createText.Length, 300)]}");

        return new PanUploadResult(false, MapFile(createRoot));
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
            // 秒传/create 的响应可能没有 server_filename，从 path 末段推
            Name: it.TryGetProperty("server_filename", out var sfn) && sfn.GetString() is { Length: > 0 } sn
                ? sn : it.GetProperty("path").GetString()!.TrimEnd('/').Split('/')[^1],
            Size: it.GetProperty("size").GetInt64(),
            IsDirectory: it.GetProperty("isdir").GetInt32() == 1,
            ModifiedAt: it.TryGetProperty("server_mtime", out var mt)
                ? DateTimeOffset.FromUnixTimeSeconds(mt.GetInt64()) : null,
            ThumbnailUrl: thumb,
            Provider: PanProvider.Baidu);
    }

    // ============ P2：管理（PCS file 端点，param 为 multipart 的 JSON 字段） ============

    // 百度各端点回包常标 charset=utf8（无连字符），ReadAsStringAsync 会炸，统一手动解码
    private static async Task<string> ReadUtf8Async(HttpContent content, CancellationToken ct) =>
        Encoding.UTF8.GetString(await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));

    private async Task PcsFileOpAsync(string method, object listPayload, string op, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var paramJson = JsonSerializer.Serialize(new { list = listPayload });
        content.Add(new StringContent(paramJson, Encoding.UTF8), "param");
        var url = $"https://pcs.baidu.com/rest/2.0/pcs/file?app_id={PcsAppId}&method={method}";
        var resp = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        var root = JsonDocument.Parse(await ReadUtf8Async(resp.Content, ct).ConfigureAwait(false)).RootElement;
        // PCS 端点错误包裹 error_code；errno 包裹也兼容
        if (root.TryGetProperty("error_code", out var ec) && ec.GetInt32() != 0)
        {
            var emsg = root.TryGetProperty("error_msg", out var em) ? em.GetString() : "";
            throw new PanException($"百度{op}失败 error_code={ec.GetInt32()} {emsg}", ec.GetInt32());
        }
        if (root.TryGetProperty("errno", out var en) && en.GetInt32() != 0)
            throw new PanException($"百度{op}失败 errno={en.GetInt32()}", en.GetInt32());
    }

    public async Task<PanFile> CreateFolderAsync(PanFile? dir, string name, CancellationToken ct = default)
    {
        var parentPath = (dir?.Path ?? "/").TrimEnd('/');
        if (parentPath == "") parentPath = "/";
        var path = (parentPath == "/" ? "" : parentPath) + "/" + name;
        var root = JsonDocument.Parse(await Http.GetStringAsync(
            $"https://pcs.baidu.com/rest/2.0/pcs/file?app_id={PcsAppId}&method=mkdir&path={Uri.EscapeDataString(path)}", ct).ConfigureAwait(false)).RootElement;
        if (root.TryGetProperty("error_code", out var ec) && ec.GetInt32() != 0)
            throw new PanException($"百度新建文件夹失败 error_code={ec.GetInt32()}", ec.GetInt32());
        if (!root.TryGetProperty("fs_id", out var fidEl))
            throw new PanException("百度新建文件夹响应缺 fs_id");
        return new PanFile(
            fidEl.GetInt64().ToString(),
            path, name, 0, true, DateTimeOffset.Now, null, PanProvider.Baidu);
    }

    public async Task RenameAsync(PanFile file, string newName, CancellationToken ct = default)
    {
        var parentDir = file.Path.Contains('/') ? file.Path[..file.Path.LastIndexOf('/')] : "";
        if (parentDir == "") parentDir = "/";
        var to = (parentDir == "/" ? "" : parentDir) + "/" + newName;
        await PcsFileOpAsync("move", new[] { new { from = file.Path, to } }, "重命名", ct).ConfigureAwait(false);
    }

    public async Task MoveAsync(IReadOnlyList<PanFile> files, PanFile? targetDir, CancellationToken ct = default)
    {
        var targetPath = (targetDir?.Path ?? "/").TrimEnd('/');
        if (targetPath == "") targetPath = "/";
        var payload = files.Select(f => new
        {
            from = f.Path,
            to = (targetPath == "/" ? "" : targetPath) + "/" + f.Name,
        }).ToArray();
        await PcsFileOpAsync("move", payload, "移动", ct).ConfigureAwait(false);
    }

    public async Task CopyAsync(IReadOnlyList<PanFile> files, PanFile? targetDir, CancellationToken ct = default)
    {
        // PCS file method=copy：与 move 同构（from/to 路径对）
        var targetPath = (targetDir?.Path ?? "/").TrimEnd('/');
        if (targetPath == "") targetPath = "/";
        var payload = files.Select(f => new
        {
            from = f.Path,
            to = (targetPath == "/" ? "" : targetPath) + "/" + f.Name,
        }).ToArray();
        await PcsFileOpAsync("copy", payload, "复制", ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(IReadOnlyList<PanFile> files, CancellationToken ct = default)
    {
        var payload = files.Select(f => new { path = f.Path }).ToArray();
        await PcsFileOpAsync("delete", payload, "删除", ct).ConfigureAwait(false);
    }

    // ============ P2：分享（share/pset 私密分享；百度现行只发带提取码的，未指定时随机生成 4 位） ============

    /// <summary>创建私密分享。路径按 path_list JSON 数组提交，凭 Cookie 鉴权，无需 bdstoken。</summary>
    public async Task<PanShare> CreateShareAsync(PanShareOptions options, CancellationToken ct = default)
    {
        if (options.Files.Count == 0) throw new PanException("没有可分享的文件", -1);
        var pwd = options.Passcode;
        if (string.IsNullOrEmpty(pwd))
        {
            // 百度已取消无提取码的公开链接；随机生成 4 位提取码（去掉易混字符）
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            pwd = new string(Enumerable.Range(0, 4).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
        }
        var pathList = "[" + string.Join(",", options.Files.Select(f => JsonSerializer.Serialize(f.Path))) + "]";
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["path_list"] = pathList,
            ["schannel"] = "4",            // 私密分享
            ["channel_list"] = "[]",
            ["period"] = options.ExpireDays.ToString(), // 0=永久 1/7/30=天数
            ["pwd"] = pwd,
            ["share_type"] = "9",
        });
        var resp = await Http.PostAsync("https://pan.baidu.com/share/pset", body, ct).ConfigureAwait(false);
        var root = JsonDocument.Parse(await ReadUtf8Async(resp.Content, ct).ConfigureAwait(false)).RootElement;
        if (root.TryGetProperty("errno", out var en) && en.GetInt32() != 0)
            throw new PanException($"百度创建分享失败 errno={en.GetInt32()}", en.GetInt32());
        var link = root.GetProperty("link").GetString()!;
        return new PanShare(
            root.TryGetProperty("shareid", out var sid) ? sid.GetInt64().ToString() : "0",
            link, options.Title, pwd, options.Files.Count,
            DateTimeOffset.Now,
            options.ExpireDays == 0 ? null : DateTimeOffset.Now.AddDays(options.ExpireDays),
            PanProvider.Baidu);
    }

    // ============ P2：转存（share/verify → shorturlinfo + share/list → share/transfer，规格交叉验证 hxz393/BaiduPCS-Go/drunkdream） ============

    private string? _bdstoken;

    private async Task<string> GetBdstokenAsync(CancellationToken ct)
    {
        if (_bdstoken != null) return _bdstoken;
        var fields = Uri.EscapeDataString("[\"bdstoken\"]");
        var root = JsonDocument.Parse(await Http.GetStringAsync(
            $"{ApiBase}/api/gettemplatevariable?clienttype=0&app_id=38824127&web=1&fields={fields}", ct).ConfigureAwait(false)).RootElement;
        ThrowIfErrno(root, "取 bdstoken");
        _bdstoken = root.GetProperty("result").GetProperty("bdstoken").GetString()
            ?? throw new PanException("百度转存-bdstoken 为空");
        return _bdstoken;
    }

    private void SetCookie(string name, string value)
    {
        if (_jar == null) return;
        foreach (var host in PanHttp.BaiduCookieHosts)
            try { _jar.Add(new Uri(host), new Cookie(name, value, "/")); } catch (CookieException) { }
    }

    private void ClearCookie(string name)
    {
        if (_jar == null) return;
        foreach (var host in PanHttp.BaiduCookieHosts)
            foreach (Cookie c in _jar.GetCookies(new Uri(host)))
                if (c.Name == name) c.Expired = true;   // 置过期即从容器移除
    }

    /// <summary>分享根层条目（fs_id 字符串，兼容数字形态）。</summary>
    private static IEnumerable<long> ReadFsIds(JsonElement list)
    {
        foreach (var it in list.EnumerateArray())
        {
            if (!it.TryGetProperty("fs_id", out var f)) continue;
            var id = f.ValueKind switch
            {
                JsonValueKind.String when long.TryParse(f.GetString(), out var s) => s,
                JsonValueKind.Number => f.GetInt64(),
                _ => 0L,
            };
            if (id != 0) yield return id;
        }
    }

    public async Task<PanSaveShareResult> SaveShareAsync(string shareUrl, string? passcode, PanFile? targetDir, CancellationToken ct = default)
    {
        // 归一化：兼容 /s/1… 与 share/init?surl=…（后者可能是从网页复制的、带或不带主机的片段）
        var url = shareUrl.Trim();
        string key; // /s/ 后面的完整 key（首字符通常为 1）
        var surlMatch = Regex.Match(url, @"[?&]surl=([0-9A-Za-z_-]+)");
        if (surlMatch.Success)
        {
            key = surlMatch.Groups[1].Value;
        }
        else
        {
            var m = Regex.Match(url, @"pan\.baidu\.com/s/([0-9A-Za-z_-]+)");
            if (!m.Success) throw new PanException("不是百度分享链接（应形如 https://pan.baidu.com/s/1… 或 …/share/init?surl=…）");
            key = m.Groups[1].Value;
        }
        if (!key.StartsWith('1')) key = "1" + key;

        ClearCookie("BDCLND");   // 上个链接的提取码 cookie 会串味，先剥掉（drunkdream 实证）
        var bdstoken = await GetBdstokenAsync(ct).ConfigureAwait(false);
        var surl = key[1..];     // verify 的 surl 参数不带首字符 '1'（hxz393 口径）

        // 1. 验证提取码（可选）：randsk 只走 Cookie BDCLND，无 query 参数。
        //    Referer 必须显式设为分享页——客户端默认 Referer 是 disk/home，会话上下文不符服务端可能按参数错误拒（errno=2）。
        if (!string.IsNullOrEmpty(passcode))
        {
            var t = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var vUrl = $"{ApiBase}/share/verify?surl={surl}&bdstoken={bdstoken}&t={t}" +
                       $"&channel=chunlei&web=1&clienttype=0&app_id={PanAppId}";
            using var vReq = new HttpRequestMessage(HttpMethod.Post, vUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                { ["pwd"] = passcode, ["vcode"] = "", ["vcode_str"] = "" }),
                Headers = { Referrer = new Uri($"https://pan.baidu.com/s/{key}") },
            };
            var vResp = await Http.SendAsync(vReq, ct).ConfigureAwait(false);
            var vRoot = JsonDocument.Parse(await vResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement;
            var verrno = vRoot.TryGetProperty("errno", out var ve) ? ve.GetInt32() : -1;
            if (verrno != 0)
                throw new PanException(verrno switch
                {
                    -9 or -12 => "提取码错误",
                    105 => "分享已失效或不存在",
                    -62 => "该分享被访问次数过多，已触发百度验证，请稍后再试",
                    _ => $"验证提取码失败 errno={verrno}",
                }, verrno);
            if (!vRoot.TryGetProperty("randsk", out var rk) || rk.ValueKind != JsonValueKind.String)
                throw new PanException("验证提取码响应缺 randsk");
            SetCookie("BDCLND", rk.GetString()!);
        }

        // 2. 分享元数据 + 根层文件：走 share 接口，不解析分享页 HTML。
        //    页面取法已废：netdisk 客户端 UA 拿到的精简页（约 5.8KB）既无 locals.mset 也无文件列表，
        //    浏览器 UA 页面的字段形态又随页面模板漂移（2026-10-15 实测四种取法全部取不到 fs_id）。
        //    接口口径（同日实测）：shorturlinfo 的 shorturl 要「完整 key（带首字符 1）」；
        //    share/list 的 shorturl 要「去掉首字符 1」，且带提取码的分享必须先 verify 拿到 BDCLND。
        var siRoot = JsonDocument.Parse(await Http.GetStringAsync(
            $"{ApiBase}/api/shorturlinfo?shorturl={key}&web=1&app_id={PanAppId}&channel=chunlei&clienttype=0",
            ct).ConfigureAwait(false)).RootElement;
        if (!siRoot.TryGetProperty("shareid", out var sidEl) || !siRoot.TryGetProperty("uk", out var ukEl))
            throw new PanException("分享链接无效或已失效");
        var shareId = sidEl.GetInt64();
        var shareUk = ukEl.GetInt64();

        var fsIds = new List<long>();
        for (var page = 1; ; page++)
        {
            var listRoot = JsonDocument.Parse(await Http.GetStringAsync(
                $"{ApiBase}/share/list?shorturl={surl}&root=1&web=5&num=100&page={page}&dir=%2F" +
                $"&channel=chunlei&app_id={PanAppId}&clienttype=0", ct).ConfigureAwait(false)).RootElement;
            var lerrno = listRoot.TryGetProperty("errno", out var le) ? le.GetInt32() : -1;
            if (lerrno != 0)
                throw new PanException(lerrno switch
                {
                    -9 => "该分享需要提取码（或提取码错误）",
                    -21 => "分享已被取消或删除",
                    140 => "分享已失效或不存在",
                    8001 => "该分享触发了百度验证，请稍后再试",
                    _ => $"读取分享文件列表失败 errno={lerrno}",
                }, lerrno);
            if (!listRoot.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) break;
            var got = 0;
            foreach (var id in ReadFsIds(list)) { fsIds.Add(id); got++; }
            if (got < 100) break;   // 还有下一页
        }
        if (fsIds.Count == 0) throw new PanException("分享里没有可转存的文件");

        // 3. 转存：path=目标父目录（须已存在）；ondup=newcopy 同名生成副本不报错；目录 fs_id 一次转即递归整棵子树
        var targetPath = (targetDir?.Path ?? "/").TrimEnd('/');
        if (targetPath == "") targetPath = "/";
        var trUrl = $"{ApiBase}/share/transfer?shareid={shareId}&from={shareUk}&bdstoken={bdstoken}" +
                    $"&channel=chunlei&web=1&clienttype=0&app_id={PanAppId}&ondup=newcopy";
        using var trReq = new HttpRequestMessage(HttpMethod.Post, trUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["fsidlist"] = "[" + string.Join(",", fsIds) + "]", ["path"] = targetPath }),
        };
        // Referer 必须指向分享页：客户端默认的 disk/home 会被当参数错误拒（errno=2，且响应里没有 show_msg 业务字段）
        trReq.Headers.TryAddWithoutValidation("Referer", $"https://pan.baidu.com/s/{key}");
        var trResp = await Http.SendAsync(trReq, ct).ConfigureAwait(false);
        var trRoot = JsonDocument.Parse(await trResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement;
        var terrno = trRoot.TryGetProperty("errno", out var te) ? te.GetInt32() : -1;
        if (terrno != 0)
        {
            // show_msg 是服务端给的人话（如「文件已存在」= 转存自己分享的文件），有就带上，便于定位
            var showMsg = trRoot.TryGetProperty("show_msg", out var sm) && sm.ValueKind == JsonValueKind.String
                ? sm.GetString() : null;
            var reason = terrno switch
            {
                -4 or -6 => "登录态失效，请重新扫码",
                -8 or 4 => "目标目录已有同名文件",
                -10 or 20 => "网盘容量不足",
                // errno=2 有两义：参数/目录不存在，或转存自己网盘里的文件（服务端 show_msg「文件已存在」）
                2 => showMsg ?? "参数错误或目标目录不存在",
                12 => "转存文件数超过限制（免费用户单次约 500 项）",
                111 => "请求过快，请稍后重试",
                _ => $"转存失败 errno={terrno}",
            };
            throw new PanException(reason, terrno);
        }
        // 逐条 errno 计数：部分失败时只计成功条目，不虚报
        var saved = 0;
        if (trRoot.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in info.EnumerateArray())
                if (!it.TryGetProperty("errno", out var ie) || ie.GetInt32() == 0) saved++;
            if (saved == 0)
                throw new PanException("转存全部失败（服务端返回成功状态但无成功条目）");
        }
        else
        {
            saved = fsIds.Count; // 响应无 info 数组（历史形态），按请求条目数计
        }
        return new PanSaveShareResult(targetDir?.Name ?? "根目录", saved);
    }

    /// <summary>我的分享列表（share/record，时间倒序；不含提取码——百度要查得逐个调 share/surlinfo，不做）。</summary>
    public async Task<IReadOnlyList<PanShare>> ListSharesAsync(CancellationToken ct = default)
    {
        var list = new List<PanShare>();
        for (var page = 1; ; page++)
        {
            var root = JsonDocument.Parse(await Http.GetStringAsync(
                $"https://pan.baidu.com/share/record?page={page}&desc=1&order=time", ct).ConfigureAwait(false)).RootElement;
            if (root.TryGetProperty("errno", out var en) && en.GetInt32() != 0)
                throw new PanException($"百度取分享列表失败 errno={en.GetInt32()}", en.GetInt32());
            if (!root.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array) break;
            if (arr.GetArrayLength() == 0) break;
            foreach (var it in arr.EnumerateArray())
            {
                var title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                if (title.Length == 0 && it.TryGetProperty("typicalPath", out var tp) && tp.GetString() is { } path)
                    title = path[(path.LastIndexOf('/') + 1)..];
                var shortlink = it.TryGetProperty("shortlink", out var sl) ? sl.GetString() ?? "" : "";
                // 实测 shortlink 本身可能是完整 URL（https://pan.baidu.com/s/1xxx），也可能是 1xxx 段
                var shareUrl = shortlink.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? shortlink
                    : shortlink.Length > 0 ? $"https://pan.baidu.com/s/{shortlink}" : "";
                var expiredType = it.TryGetProperty("expiredType", out var et) ? et.GetInt32() : 0;
                var expiredTime = it.TryGetProperty("expiredTime", out var ex) ? ex.GetInt64() : 0;
                var ctime = it.TryGetProperty("ctime", out var c) ? c.GetInt64() : 0;
                list.Add(new PanShare(
                    it.TryGetProperty("shareId", out var sid) ? sid.GetInt64().ToString() : "0",
                    shareUrl,
                    title.Length > 0 ? title : "(未命名)",
                    null, // 列表响应不含提取码
                    it.TryGetProperty("fsIds", out var fs) ? fs.GetArrayLength() : 0,
                    ctime > 0 ? DateTimeOffset.FromUnixTimeSeconds(ctime) : DateTimeOffset.MinValue,
                    expiredType != 0 && expiredTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(expiredTime) : null,
                    PanProvider.Baidu));
            }
        }
        return list;
    }

    /// <summary>取消分享（share/cancel，纯 Cookie 无 query，BaiduProbe 已实测 errno=0）。</summary>
    public async Task DeleteSharesAsync(IReadOnlyList<string> shareIds, CancellationToken ct = default)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["shareid_list"] = "[" + string.Join(",", shareIds) + "]",
        });
        var resp = await Http.PostAsync("https://pan.baidu.com/share/cancel", body, ct).ConfigureAwait(false);
        var root = JsonDocument.Parse(await ReadUtf8Async(resp.Content, ct).ConfigureAwait(false)).RootElement;
        if (root.TryGetProperty("errno", out var en) && en.GetInt32() != 0)
            throw new PanException($"百度取消分享失败 errno={en.GetInt32()}", en.GetInt32());
    }

    // ============ P2：离线下载（cloud_dl，参数走 query 无请求体） ============
    private const string CloudDlBase = "https://pan.baidu.com/rest/2.0/services/cloud_dl";
    private const int OfflineTaskPageSize = 100;

    public async Task<PanOfflineTask> AddOfflineTaskAsync(string sourceUrl, PanFile? saveDir, CancellationToken ct = default)
    {
        var savePath = (saveDir?.Path ?? "/").TrimEnd('/') + "/";
        if (savePath == "//") savePath = "/";
        var url = $"{CloudDlBase}?method=add_task&app_id={PanAppId}&task_from=0&selected_idx=1" +
                  $"&save_path={Uri.EscapeDataString(savePath)}&source_url={Uri.EscapeDataString(sourceUrl)}";
        var resp = await Http.PostAsync(url, null, ct).ConfigureAwait(false);
        var root = JsonDocument.Parse(await ReadUtf8Async(resp.Content, ct).ConfigureAwait(false)).RootElement;
        if (root.TryGetProperty("error_code", out var ec) && ec.GetInt32() != 0)
        {
            var emsg = root.TryGetProperty("error_msg", out var em) ? em.GetString() : "";
            throw new PanException($"百度离线下载失败 error_code={ec.GetInt32()} {emsg}", ec.GetInt32());
        }
        var taskId = root.GetProperty("task_id").GetInt64().ToString();
        return new PanOfflineTask(taskId, "", 0, 0, 1, savePath, sourceUrl, PanProvider.Baidu);
    }

    public async Task<IReadOnlyList<PanOfflineTask>> ListOfflineTasksAsync(CancellationToken ct = default)
    {
        var tasks = new List<PanOfflineTask>();
        for (var start = 0; ; start += OfflineTaskPageSize)
        {
            var url = $"{CloudDlBase}?method=list_task&need_task_info=1&status=255&start={start}&limit={OfflineTaskPageSize}&app_id={PanAppId}";
            var resp = await Http.PostAsync(url, null, ct).ConfigureAwait(false);
            var root = JsonDocument.Parse(await ReadUtf8Async(resp.Content, ct).ConfigureAwait(false)).RootElement;
            if (root.TryGetProperty("error_code", out var ec) && ec.GetInt32() != 0)
                throw new PanException($"百度离线任务列表失败 error_code={ec.GetInt32()}", ec.GetInt32());
            if (!root.TryGetProperty("task_info", out var arr) || arr.ValueKind != JsonValueKind.Array) break;
            var count = arr.GetArrayLength();
            foreach (var it in arr.EnumerateArray())
            {
                // cloud_dl 任务对象字段 JSON 里全是字符串
                static long L(JsonElement e, string k) =>
                    e.TryGetProperty(k, out var v) && long.TryParse(v.GetString(), out var n) ? n : 0;
                static string S(JsonElement e, string k) =>
                    e.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                tasks.Add(new PanOfflineTask(
                    S(it, "task_id"), S(it, "task_name"), L(it, "file_size"), L(it, "finished_size"),
                    (int)L(it, "status"), S(it, "save_path"), S(it, "source_url"), PanProvider.Baidu));
            }
            if (count < OfflineTaskPageSize) break;
        }
        return tasks;
    }

    public void Dispose() => _http?.Dispose();
}
