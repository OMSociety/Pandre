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

    /// <summary>是否为登录态失效（百度 errno=-6/110；夸克 401/31001/31004/31007）。</summary>
    public bool IsAuthExpired => Code is -6 or 110 or 31001 or 31004 or 31007 || Message.Contains("401");
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
