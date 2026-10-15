namespace Pandre.Core;

/// <summary>
/// 网盘客户端抽象。P1 范围：配额 / 列表 / 搜索 / 取下载链。
/// P2 扩展：管理（建文件夹/重命名/移动/删除）、转存、分享、离线下载（仅百度）。
/// P2 方法带默认实现（抛 NotSupportedException），客户端按能力覆盖，不影响 P1 已实现方。
/// </summary>
public interface IPanClient : IDisposable
{
    PanProvider Provider { get; }

    /// <summary>是否已附着有效登录态。</summary>
    bool IsAuthenticated { get; }

    /// <summary>附着登录态（扫码结果或凭据存储恢复）。</summary>
    void Attach(PanCredential credential);

    /// <summary>导出当前最新 Cookie（接口可能续期/补发 Cookie，如夸克 __puus），供持久化。</summary>
    string? ExportCookie();

    Task<PanAccountInfo> GetAccountAsync(CancellationToken ct = default);

    /// <summary>列目录。dir=null 表示根目录。</summary>
    Task<IReadOnlyList<PanFile>> ListAsync(PanFile? dir, CancellationToken ct = default);

    Task<IReadOnlyList<PanFile>> SearchAsync(string keyword, CancellationToken ct = default);

    /// <summary>取下载链。链接有时效，应取得后立即交给下载器。</summary>
    Task<IReadOnlyList<PanDownloadLink>> GetDownloadLinksAsync(IReadOnlyList<PanFile> files, CancellationToken ct = default);

    /// <summary>按文件 Id（百度 fs_id / 夸克 fid）直接解析下载链：浏览器接管入口用，不需要先列目录拿到 PanFile。</summary>
    Task<IReadOnlyList<PanDownloadLink>> GetDownloadLinksByIdsAsync(IReadOnlyList<PanRemoteFileRef> refs, CancellationToken ct = default) =>
        throw new NotSupportedException();

    // ============ P2：管理与分享（默认抛 NotSupported，客户端按能力覆盖） ============

    /// <summary>新建文件夹，返回新目录。dir=null 表示根目录。</summary>
    Task<PanFile> CreateFolderAsync(PanFile? dir, string name, CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task RenameAsync(PanFile file, string newName, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>移动到目标目录（targetDir=null 表示根目录）。</summary>
    Task MoveAsync(IReadOnlyList<PanFile> files, PanFile? targetDir, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>复制到目标目录（targetDir=null 表示根目录）。夸克走 file/copy 异步任务；百度走 PCS method=copy。</summary>
    Task CopyAsync(IReadOnlyList<PanFile> files, PanFile? targetDir, CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task DeleteAsync(IReadOnlyList<PanFile> files, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>转存外部分享链接到目标目录（targetDir=null 表示根目录）。</summary>
    Task<PanSaveShareResult> SaveShareAsync(string shareUrl, string? passcode, PanFile? targetDir, CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<PanShare>> ListSharesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<PanShare> CreateShareAsync(PanShareOptions options, CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task DeleteSharesAsync(IReadOnlyList<string> shareIds, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>离线下载（仅百度支持）。</summary>
    Task<PanOfflineTask> AddOfflineTaskAsync(string sourceUrl, PanFile? saveDir, CancellationToken ct = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<PanOfflineTask>> ListOfflineTasksAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// <summary>
    /// 上传本地文件到目标目录（targetDir=null 表示根目录）。
    /// 百度走 precreate 秒传→superfile2 分片→create 合并；夸克走 upload/pre→分片→finish。
    /// </summary>
    Task<PanUploadResult> UploadAsync(
        string localPath, PanFile? targetDir, PanConflictPolicy policy = PanConflictPolicy.Skip,
        IProgress<PanUploadProgress>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

/// <summary>一次扫码登录会话：先取二维码展示，再轮询等待确认。</summary>
public interface IQrLoginFlow : IDisposable
{
    PanProvider Provider { get; }

    Task<QrCodePayload> GetQrCodeAsync(CancellationToken ct = default);

    /// <summary>轮询直到确认/过期/取消；通过 progress 上报 Waiting→Scanned→Confirmed 状态。</summary>
    Task<PanCredential> WaitAsync(IProgress<QrLoginState>? progress = null, CancellationToken ct = default);
}

/// <summary>扫码登录入口工厂。</summary>
public static class PanAuth
{
    public static IQrLoginFlow StartQrLogin(PanProvider provider) => provider switch
    {
        PanProvider.Baidu => new Baidu.BaiduQrLogin(),
        PanProvider.Quark => new Quark.QuarkQrLogin(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}
