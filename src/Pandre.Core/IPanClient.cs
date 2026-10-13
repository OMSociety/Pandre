namespace Pandre.Core;

/// <summary>
/// 网盘客户端抽象。P1 范围：配额 / 列表 / 搜索 / 取下载链。
/// 管理类操作（重命名、移动、删除等）与上传、分享在 P2 扩展本接口。
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
