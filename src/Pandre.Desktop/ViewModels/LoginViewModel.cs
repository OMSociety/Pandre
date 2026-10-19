using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pandre.Core;
using Pandre.Desktop.Services;

namespace Pandre.Desktop.ViewModels;

/// <summary>单个网盘的扫码卡片。</summary>
public partial class LoginCardViewModel : ViewModelBase
{
    private readonly PanProvider _provider;

    /// <summary>当前提供方对应的客户端实例（退出登录/会话过期时要连内存态一起解除）。</summary>
    private IPanClient CurrentClient => _provider == PanProvider.Baidu ? AppServices.Baidu : AppServices.Quark;
    private IQrLoginFlow? _flow;
    private CancellationTokenSource? _waitCts;

    public LoginCardViewModel(PanProvider provider, string title)
    {
        _provider = provider;
        Title = title;
    }

    public string Title { get; }

    /// <summary>弹窗标题图标（与页面图标一致）。</summary>
    public string Icon => _provider == PanProvider.Baidu ? AppIcons.BaiduNetdisk : AppIcons.QuarkNetdisk;

    /// <summary>扫码提示语。</summary>
    public string ScanHint => _provider == PanProvider.Baidu
        ? "用百度网盘 App 或百度 App 扫码"
        : "用夸克网盘 App 或夸克 App 扫码";

    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowQr))] private Avalonia.Media.Imaging.Bitmap? _qrImage;
    [ObservableProperty] private string _statusText = "未登录";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool _isLoggedIn;
    [ObservableProperty] private string _accountText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private string _accountName = "";
    [ObservableProperty] private string _hintText = "";

    public bool ShowQr => QrImage != null;

    /// <summary>页面右上角登录按钮的文字。</summary>
    public string ButtonText => IsLoggedIn
        ? (string.IsNullOrEmpty(AccountName) ? "已登录" : $"已登录 · {AccountName}")
        : "扫码登录";

    [RelayCommand]
    private async Task StartLoginAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在生成二维码…";
        QrImage = null;
        _waitCts = new CancellationTokenSource();
        try
        {
            _flow = Pandre.Core.PanAuth.StartQrLogin(_provider);
            var payload = await _flow.GetQrCodeAsync(_waitCts.Token);
            HintText = payload.Hint;

            if (payload.ImagePng != null)
                QrImage = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(payload.ImagePng));
            else if (payload.Text != null)
            {
                // 夸克：本地渲染 QR 文本
                var gen = new QRCoder.QRCodeGenerator();
                using var d = gen.CreateQrCode(payload.Text, QRCoder.QRCodeGenerator.ECCLevel.Q);
                var png = new QRCoder.PngByteQRCode(d).GetGraphic(10);
                QrImage = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(png));
            }
            StatusText = "等待扫码…";

            var progress = new Progress<QrLoginState>(s =>
            {
                StatusText = s switch
                {
                    QrLoginState.Waiting  => "等待扫码…",
                    QrLoginState.Scanned  => "已扫码，请在手机上确认…",
                    QrLoginState.Confirmed=> "已确认，完成登录…",
                    QrLoginState.Expired  => "二维码过期，请重新点击登录",
                    _ => StatusText,
                };
            });

            var cred = await _flow.WaitAsync(progress, _waitCts.Token);
            // 凭据已到手：落盘与取账号不再受取消影响——半途取消会「凭据已存却报已取消」
            await AppServices.SaveCredentialAsync(cred);

            var client = _provider == PanProvider.Baidu
                ? (IPanClient)AppServices.Baidu : AppServices.Quark;
            // 凭据已落盘且客户端已 Attach，登录即成立：账号信息只是补充展示，取失败不回滚登录态，
            // 否则登录卡停在「扫码登录」而网盘页按 IsAuthenticated 已可用，状态分裂至重启
            IsLoggedIn = true;
            AccountName = cred.Nickname ?? "已登录";
            StatusText = "已登录";
            QrImage = null;
            try
            {
                var info = await client.GetAccountAsync(CancellationToken.None);
                AccountName = info.Nickname ?? AccountName;
                AccountText = $"{AccountName} · {Fmt.Size(info.UsedBytes)} / {Fmt.Size(info.TotalBytes)}";
            }
            catch (Exception ex)
            {
                // 网络/接口失败非阻断：状态条提示未验证，进网盘页后重新拉取（与 RefreshAccountAsync 失败分支同口径）
                StatusText = "已登录（状态未验证）";
                System.Diagnostics.Debug.WriteLine(ex);
            }

            // 通知外层刷新导航可用性
            RaiseSessionChanged();
            RaiseLoginSucceeded();
        }
        catch (OperationCanceledException) { StatusText = "已取消"; }
        catch (PanException ex) { StatusText = $"登录失败：{ex.Message}"; System.Diagnostics.Debug.WriteLine(ex); }
        catch (Exception ex) { StatusText = $"登录异常：{ex.Message}"; System.Diagnostics.Debug.WriteLine(ex); }
        finally
        {
            IsBusy = false;
            _flow?.Dispose();
            _flow = null;
            _waitCts?.Dispose();   // 每轮扫码的取消源用完即弃，防句柄泄漏；Cancel() 对 null 安全
            _waitCts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        // 只发取消：状态与收尾统一由 StartLoginAsync 的 catch（「已取消」）/finally（IsBusy、Dispose）
        // 处理。此处置 IsBusy=false 会在流程收尾前提前放出「扫码登录」按钮，造成重入。
        _waitCts?.Cancel();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            await AppServices.Credentials.ClearAsync(_provider);
        }
        catch (Exception ex)
        {
            // 凭据还在盘上，登出没有完成：保持已登录态不动，否则界面显示未登录、重启后凭据又恢复（状态分裂）。
            // 退出登录按钮就在登录弹窗里，其上方的状态文字用户可见
            StatusText = $"退出登录失败：{ex.Message}";
            return;
        }
        // 内存会话态一并解除：否则 IsAuthenticated 恒真，网盘页继续用旧会话，与「未登录」UI 自相矛盾
        CurrentClient.Detach();
        IsLoggedIn = false;
        AccountName = "";
        AccountText = "";
        StatusText = "未登录";
        QrImage = null;
        RaiseSessionChanged();
    }

    /// <summary>启动时恢复：凭据存在且 GetAccountAsync 通了 = 已登录。</summary>
    public async Task RefreshAccountAsync()
    {
        var client = CurrentClient;
        var cred = await AppServices.Credentials.LoadAsync(_provider);
        if (cred == null) { IsLoggedIn = false; StatusText = "未登录"; return; }
        try
        {
            client.Attach(cred);
            var info = await client.GetAccountAsync();
            IsLoggedIn = true;
            AccountName = info.Nickname ?? cred.Nickname ?? "已登录";
            AccountText = $"{AccountName} · {Fmt.Size(info.UsedBytes)} / {Fmt.Size(info.TotalBytes)}";
            StatusText = "已登录";
        }
        catch (PanException ex) when (ex.IsAuthExpired)
        {
            // 会话在服务端已失效，先解除内存态让启动流程继续；盘上凭据删不掉（占用/权限）时不阻断启动，
            // 卡片状态条给出可见提示，残留文件由下次登录写入或下次过期清理兜底
            client.Detach();   // 会话过期：内存态一并解除，否则网盘页继续用旧会话
            IsLoggedIn = false;
            StatusText = "登录已过期，请重新扫码";
            try
            {
                await AppServices.Credentials.ClearAsync(_provider);
            }
            catch (Exception clearEx)
            {
                StatusText = $"登录已过期，请重新扫码；本地凭据清除失败：{clearEx.Message}";
            }
        }
        catch (Exception ex)
        {
            // 网络问题：保持附着状态，展示已保存的凭据
            IsLoggedIn = client.IsAuthenticated;
            StatusText = IsLoggedIn ? "已登录（状态未验证）" : "未登录";
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    public event Action? SessionChanged;
    private void RaiseSessionChanged() => SessionChanged?.Invoke();

    /// <summary>本次扫码登录成功（区别于"启动时恢复登录态"）。</summary>
    public event Action? LoginSucceeded;
    private void RaiseLoginSucceeded() => LoginSucceeded?.Invoke();
}

/// <summary>百度 + 夸克两张登录卡的容器（卡片显示在各网盘页的扫码弹窗里）。</summary>
public partial class LoginViewModel : ViewModelBase
{
    public LoginCardViewModel Baidu { get; }
    public LoginCardViewModel Quark { get; }

    public LoginViewModel()
    {
        Baidu = new LoginCardViewModel(PanProvider.Baidu, "百度网盘");
        Quark = new LoginCardViewModel(PanProvider.Quark, "夸克网盘");
    }

    public async Task InitAsync()
    {
        await Task.WhenAll(Baidu.RefreshAccountAsync(), Quark.RefreshAccountAsync());
    }
}
