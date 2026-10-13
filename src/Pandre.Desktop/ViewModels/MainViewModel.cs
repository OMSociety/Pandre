using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Pandre.Core;

namespace Pandre.Desktop.ViewModels;

public sealed class NavItem
{
    public required string Title { get; init; }
    public required string Icon { get; init; }   // PathGeometry Figures 字符串
    public required ViewModelBase Page { get; init; }
}

public partial class MainViewModel : ViewModelBase
{
    public LoginViewModel Login { get; }
    public PanViewModel Baidu { get; }
    public PanViewModel Quark { get; }
    public DownloadsViewModel Downloads { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private string _statusText = "";

    public MainViewModel()
    {
        Downloads = new DownloadsViewModel();
        Login = new LoginViewModel();
        Baidu = new PanViewModel(PanProvider.Baidu, Downloads);
        Quark = new PanViewModel(PanProvider.Quark, Downloads);
        Settings = new SettingsViewModel();

        // 图标 geometry：钥匙 / 云(百度) / 云(夸克) / 向下箭头 / 齿轮
        const string keyIcon = "M14 10a4 4 0 1 0-3.9 5H11v2h2v2h2v2h4v-4.5L14.5 12A4 4 0 0 0 14 10zm-4 2a2 2 0 1 1 0-4 2 2 0 0 1 0 4z";
        const string cloudIcon = "M12 3a5 5 0 0 0-4.9 4.1A4.5 4.5 0 0 0 7.5 16h9a4 4 0 0 0 .6-7.96A5.5 5.5 0 0 0 12 3z";
        const string downIcon = "M12 3v10.17l3.59-3.58L17 11l-5 5-5-5 1.41-1.41L12 13.17V3h0zm-7 15h14v2H5v-2z";
        const string gearIcon = "M19.14 12.94c.04-.3.06-.61.06-.94s-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6a3.6 3.6 0 1 1 0-7.2 3.6 3.6 0 0 1 0 7.2z";
        NavItems = new ObservableCollection<NavItem>
        {
            new() { Title = "登录", Icon = keyIcon, Page = Login },
            new() { Title = "百度", Icon = cloudIcon, Page = Baidu },
            new() { Title = "夸克", Icon = cloudIcon, Page = Quark },
            new() { Title = "下载", Icon = downIcon, Page = Downloads },
            new() { Title = "设置", Icon = gearIcon, Page = Settings },
        };
        // 默认进登录页（首次启动没凭据）
        SelectedNav = NavItems[0];
        CurrentPage = Login;

        Login.Baidu.SessionChanged += OnSessionChanged;
        Login.Quark.SessionChanged += OnSessionChanged;
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        CurrentPage = value.Page;
        // 进入网盘页时按需激活
        if (value.Page is PanViewModel p) _ = p.ActivateAsync();
        else if (value.Page is DownloadsViewModel d) d.Refresh();
    }

    private void OnSessionChanged()
    {
        StatusText = (Services.AppServices.Baidu.IsAuthenticated ? "百度已登录 " : "")
                   + (Services.AppServices.Quark.IsAuthenticated ? "夸克已登录" : "");
    }

    /// <summary>启动时初始化：恢复登录态。</summary>
    public async Task InitAsync()
    {
        StatusText = "正在恢复登录态…";
        await Login.InitAsync();
        OnSessionChanged();
        // 若已有任一登录，默认停在第一个网盘页
        if (Login.Baidu.IsLoggedIn) SelectedNav = NavItems[1];
        else if (Login.Quark.IsLoggedIn) SelectedNav = NavItems[2];
        if (string.IsNullOrEmpty(StatusText)) StatusText = "就绪";

        // 自动验收：PANDRE_AUTOCHECK=<页索引> 时启动后直跳该页
        var auto = System.Environment.GetEnvironmentVariable("PANDRE_AUTOCHECK");
        if (int.TryParse(auto, out var idx) && idx >= 0 && idx < NavItems.Count)
            SelectedNav = NavItems[idx];
    }
}
