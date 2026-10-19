namespace Pandre.Core;

/// <summary>
/// 上传断点续传记录：一次分片上传会话在服务端的全部参数 + 已完成分片的落点。
/// 只承载会话级数据（upload_id/task_id 等服务端上传会话标识），不含任何账号凭据。
/// </summary>
public sealed record UploadResumeEntry
{
    /// <summary>本地文件完整路径（身份三要素之一）。</summary>
    public string LocalPath { get; init; } = "";
    /// <summary>记录时的文件长度（身份三要素之二）。</summary>
    public long Length { get; init; }
    /// <summary>记录时的 LastWriteTimeUtc.Ticks（身份三要素之三）。</summary>
    public long LastWriteTimeUtcTicks { get; init; }
    /// <summary>服务端上传会话 id（pre 下发）。</summary>
    public string UploadId { get; init; } = "";
    /// <summary>服务端任务 id（pre 下发）。</summary>
    public string TaskId { get; init; } = "";
    /// <summary>分片授权交换的会话参数（pre 下发，仅对本上传会话有效）。</summary>
    public string AuthInfo { get; init; } = "";
    /// <summary>OSS 对象键（pre 下发）。</summary>
    public string ObjKey { get; init; } = "";
    /// <summary>OSS 桶名（pre 下发，缺省 ul-zb）。</summary>
    public string Bucket { get; init; } = "";
    /// <summary>合并阶段 callback 的紧凑 JSON 字节（合并签名基于这份内容，续传期间须逐字节一致）。</summary>
    public string CallbackJson { get; init; } = "";
    /// <summary>分片大小（服务端规则值）。</summary>
    public long ChunkSize { get; init; }
    /// <summary>总分片数。</summary>
    public int PartCount { get; init; }
    /// <summary>完整 MD5 hex（续传重扫哈希时复核内容身份）。</summary>
    public string Md5 { get; init; } = "";
    /// <summary>完整 SHA1 hex（同上）。</summary>
    public string Sha1 { get; init; } = "";
    /// <summary>各分片 ETag；null = 该分片未完成。长度恒等于 PartCount。</summary>
    public List<string?> Etags { get; init; } = new();
    /// <summary>最近一次记录时间（保留期判定）。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// 上传断点续传的存取口（主机侧实现并注入客户端）。实现必须容错：任何故障按「无记录」降级，
/// 不允许续传存储的异常反过来打断上传。键由协议层按文件身份生成，实现方不解释键的含义。
/// </summary>
public interface IUploadResumeStore
{
    UploadResumeEntry? Load(string key);
    void Save(string key, UploadResumeEntry entry);
    void Remove(string key);
}
