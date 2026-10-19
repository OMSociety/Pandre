namespace Pandre.Core;

/// <summary>网盘提供方。</summary>
public enum PanProvider
{
    Baidu,
    Quark,
}

/// <summary>统一的文件/目录模型。Id：百度=fs_id，夸克=fid；Path：全路径（百度按路径导航，夸克仅展示）。</summary>
public sealed record PanFile(
    string Id,
    string Path,
    string Name,
    long Size,
    bool IsDirectory,
    DateTimeOffset? ModifiedAt,
    string? ThumbnailUrl,
    PanProvider Provider);

/// <summary>账号配额与显示信息。</summary>
public sealed record PanAccountInfo(
    string? Nickname,
    long TotalBytes,
    long UsedBytes,
    string? MemberNote);

/// <summary>一条可直接交给下载器的下载链（含必备请求头与保守过期时刻）。</summary>
public sealed record PanDownloadLink(
    PanFile File,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt);

/// <summary>网盘业务异常。Code 为服务端业务码（如百度 errno、夸克 code）。</summary>
public sealed class PanException(string message, long? code = null) : Exception(message)
{
    public long? Code { get; } = code;

    /// <summary>
    /// 是否为登录态失效（百度 errno=-6/110；夸克 code 401/31001/31004/31007，抛出点已把 code 放进 Code）。
    /// 百度转存链路把 errno=-4 也按登录态失效处理（转存分支有显式 -4 判定），本属性有意不收编 -4：
    /// -4 在各端点的语义未逐一证实、真网探针也未能触发，收进全局判定会把含义未知的 -4
    /// 一律升级为「清凭据+登出」级处理；两处口径的现状差异由转存路径的显式 -4 分支兜住。
    /// </summary>
    public bool IsAuthExpired => Code is -6 or 110 or 401 or 31001 or 31004 or 31007;
}

/// <summary>扫码登录状态机。</summary>
public enum QrLoginState
{
    Waiting,
    Scanned,
    Confirmed,
    Expired,
    Failed,
}

/// <summary>二维码载荷：百度给服务端图片字节；夸克给文本（由 UI 渲染）。</summary>
public sealed record QrCodePayload(byte[]? ImagePng, string? Text, string Hint);

// ============ P2：管理与分享 ============

/// <summary>转存一份外部分享链接到自己网盘的结果。</summary>
public sealed record PanSaveShareResult(
    string SavedToDirName,
    int SavedCount,
    int SkippedInvalid = 0);

/// <summary>一条分享记录（我的分享列表项）。</summary>
public sealed record PanShare(
    string ShareId,
    string ShareUrl,
    string Title,
    string? Passcode,
    long FileCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    PanProvider Provider);

/// <summary>创建分享的参数。</summary>
public sealed record PanShareOptions(
    IReadOnlyList<PanFile> Files,
    string Title,
    /// <summary>有效期：0=永久（百度 period=0）；夸克 expired_type=1。</summary>
    int ExpireDays,
    /// <summary>4 位提取码；null=公开链接（夸克 url_type=1）。</summary>
    string? Passcode);

/// <summary>离线下载任务。</summary>
public sealed record PanOfflineTask(
    string TaskId,
    string Name,
    long SizeBytes,
    long FinishedBytes,
    /// <summary>0成功/1进行中/2系统错误/3资源不存在/4超时/5失败/6空间不足/7取消。</summary>
    int Status,
    string SavePath,
    string SourceUrl,
    PanProvider Provider);

// ============ P2：上传 ============

/// <summary>上传进度上报。</summary>
public readonly record struct PanUploadProgress(
    long TotalBytes,
    long DoneBytes,
    int TotalParts,
    int DoneParts,
    string Phase);

/// <summary>上传结果。Rapid=true 表示秒传命中（未走真实分片传输）。</summary>
public sealed record PanUploadResult(
    bool Rapid,
    PanFile File);
