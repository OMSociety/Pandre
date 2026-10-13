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
    private IQrLoginFlow? _flow;
    private CancellationTokenSource? _waitCts;

    public LoginCardViewModel(PanProvider provider, string title)
    {
        _provider = provider;
        Title = title;
    }

    public string Title { get; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowQr))] private Avalonia.Media.Imaging.Bitmap? _qrImage;
    [ObservableProperty] private string _statusText = "未登录";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _accountText = "";
    [ObservableProperty] private string _hintText = "";

    public bool ShowQr => QrImage != null;

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
            await AppServices.SaveCredentialAsync(cred);

            var client = _provider == PanProvider.Baidu
                ? (IPanClient)AppServices.Baidu : AppServices.Quark;
            var info = await client.GetAccountAsync(_waitCts.Token);
            IsLoggedIn = true;
            AccountText = $"{(info.Nickname ?? cred.Nickname ?? "已登录")} · {FmtSize(info.UsedBytes)} / {FmtSize(info.TotalBytes)}";
            StatusText = "已登录";
            QrImage = null;

            // 通知外层刷新导航可用性
            RaiseSessionChanged();
        }
        catch (OperationCanceledException) { StatusText = "已取消"; }
        catch (PanException ex) { StatusText = $"登录失败：{ex.Message}"; System.Diagnostics.Debug.WriteLine(ex); }
        catch (Exception ex) { StatusText = $"登录异常：{ex.Message}"; System.Diagnostics.Debug.WriteLine(ex); }
        finally
        {
            IsBusy = false;
            _flow?.Dispose();
            _flow = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _waitCts?.Cancel();
        _flow?.Dispose();
        QrImage = null;
        StatusText = "已取消";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await AppServices.Credentials.ClearAsync(_provider);
        IsLoggedIn = false;
        AccountText = "";
        StatusText = "未登录";
        QrImage = null;
        RaiseSessionChanged();
    }

    /// <summary>启动时恢复：凭据存在且 GetAccountAsync 通了 = 已登录。</summary>
    public async Task RefreshAccountAsync()
    {
        var client = _provider == PanProvider.Baidu ? (IPanClient)AppServices.Baidu : AppServices.Quark;
        var cred = await AppServices.Credentials.LoadAsync(_provider);
        if (cred == null) { IsLoggedIn = false; StatusText = "未登录"; return; }
        try
        {
            client.Attach(cred);
            var info = await client.GetAccountAsync();
            IsLoggedIn = true;
            AccountText = $"{(info.Nickname ?? cred.Nickname ?? "已登录")} · {FmtSize(info.UsedBytes)} / {FmtSize(info.TotalBytes)}";
            StatusText = "已登录";
        }
        catch (PanException ex) when (ex.IsAuthExpired)
        {
            await AppServices.Credentials.ClearAsync(_provider);
            IsLoggedIn = false;
            StatusText = "登录已过期，请重新扫码";
        }
        catch (Exception ex)
        {
            // 网络问题：保持附着状态，展示已保存的凭据
            IsLoggedIn = client.IsAuthenticated;
            StatusText = IsLoggedIn ? "已登录（状态未验证）" : "未登录";
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private static string FmtSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / 1073741824.0:F1} GB",
        >= 1L << 20 => $"{b / 1048576.0:F1} MB",
        >= 1L << 10 => $"{b / 1024.0:F1} KB",
        _ => $"{b} B",
    };

    public event Action? SessionChanged;
    private void RaiseSessionChanged() => SessionChanged?.Invoke();
}

/// <summary>登录页：百度 + 夸克两张卡。</summary>
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
