using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pandre.Core;

namespace Pandre.Desktop.ViewModels;

public sealed partial class NavItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public required string Title { get; init; }
    public required string Icon { get; init; }         // Regular（未选中）
    public string? IconFilled { get; init; }           // Filled（当前页）
    public required ViewModelBase Page { get; init; }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(PanelClasses))]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(CurrentIcon))]
    private bool _isCurrent;

    /// <summary>当前页面板类名（配合 App.axaml 的 .nav-item/.current 样式）。</summary>
    public string PanelClasses => IsCurrent ? "nav-item current" : "nav-item";

    /// <summary>当前页图标：Filled 优先，回落 Regular。</summary>
    public string CurrentIcon => IsCurrent ? (IconFilled ?? Icon) : Icon;
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
    [ObservableProperty] private bool _isNavBack;

    /// <summary>当前页面对应的登录卡（百度/夸克页才有，其余页为 null）。</summary>
    [ObservableProperty] private LoginCardViewModel? _loginCard;

    /// <summary>登录伪弹窗是否打开。</summary>
    [ObservableProperty] private bool _isLoginPanelOpen;

    /// <summary>登录态是否已从磁盘恢复完成（恢复期间不触发刷新）。</summary>
    private bool _sessionRestored;

    /// <summary>构造与登录态恢复期间抑制页面激活（那时凭据还没挂上）。</summary>
    private bool _suppressPageActivation = true;

    public MainViewModel()
    {
        Downloads = new DownloadsViewModel();
        Login = new LoginViewModel();
        Baidu = new PanViewModel(PanProvider.Baidu, Downloads);
        Quark = new PanViewModel(PanProvider.Quark, Downloads);
        Settings = new SettingsViewModel();

        // 微软 Fluent System Icons（MIT），见 Icons.cs
        NavItems = new ObservableCollection<NavItem>
        {
            new() { Title = "百度", Icon = AppIcons.Cloud, IconFilled = AppIcons.CloudFilled, Page = Baidu },
            new() { Title = "夸克", Icon = AppIcons.CloudSync, IconFilled = AppIcons.CloudSyncFilled, Page = Quark },
            new() { Title = "下载", Icon = AppIcons.ArrowDownload, IconFilled = AppIcons.ArrowDownloadFilled, Page = Downloads },
            new() { Title = "设置", Icon = AppIcons.Settings, IconFilled = AppIcons.SettingsFilled, Page = Settings },
        };
        SelectedNav = NavItems[0];
        NavItems[0].IsCurrent = true;
        CurrentPage = Baidu;
        LoginCard = Login.Baidu;

        Login.Baidu.SessionChanged += OnSessionChanged;
        Login.Quark.SessionChanged += OnSessionChanged;
        Login.Baidu.LoginSucceeded += OnLoginSucceeded;
        Login.Quark.LoginSucceeded += OnLoginSucceeded;
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        var oldIdx = _selectedNav != null ? NavItems.IndexOf(_selectedNav) : -1;
        var newIdx = NavItems.IndexOf(value);
        IsNavBack = newIdx < oldIdx;
        foreach (var n in NavItems) n.IsCurrent = n == value;
        CurrentPage = value.Page;
        // 顶栏登录按钮跟着页面走：只有百度/夸克页有
        LoginCard = ReferenceEquals(value.Page, Baidu) ? Login.Baidu
                  : ReferenceEquals(value.Page, Quark) ? Login.Quark
                  : null;
        if (LoginCard is not { IsLoggedIn: true }) IsLoginPanelOpen = false;
        if (_suppressPageActivation) return;
        // 进入网盘页时按需激活
        if (value.Page is PanViewModel p) _ = p.ActivateAsync();
        else if (value.Page is DownloadsViewModel d) d.Refresh();
    }

    [RelayCommand]
    private void OpenLoginPanel()
    {
        if (LoginCard != null) IsLoginPanelOpen = true;
    }

    [RelayCommand]
    private void CloseLoginPanel() => IsLoginPanelOpen = false;

    private void OnSessionChanged()
    {
        // 登录成功后把当前网盘页刷出来
        if (_sessionRestored && CurrentPage is PanViewModel pan) _ = pan.ReloadAsync();
    }

    /// <summary>本次扫码登录成功 → 收起弹窗（启动时恢复登录态不走这里）。</summary>
    private void OnLoginSucceeded()
    {
        if (IsLoginPanelOpen) IsLoginPanelOpen = false;
    }

    /// <summary>启动时初始化：恢复登录态。</summary>
    public async Task InitAsync()
    {
        await Login.InitAsync();
        OnSessionChanged();
        _sessionRestored = true;
        // 默认停在已登录的网盘页，都没有则停在百度页
        SelectedNav = Login.Quark.IsLoggedIn && !Login.Baidu.IsLoggedIn ? NavItems[1] : NavItems[0];

        // 自动验收：PANDRE_AUTOCHECK=<页索引> 时启动后直跳该页
        var auto = System.Environment.GetEnvironmentVariable("PANDRE_AUTOCHECK");
        if (int.TryParse(auto, out var idx) && idx >= 0 && idx < NavItems.Count)
            SelectedNav = NavItems[idx];

        // 恢复完成后统一激活当前页（此前 SelectedNav 可能没变化、也可能在凭据挂上前就激活过）
        _suppressPageActivation = false;
        if (CurrentPage is PanViewModel current) await current.ActivateAsync();
        else if (CurrentPage is DownloadsViewModel downloads) downloads.Refresh();

        // 首次运行没凭据：直接把当前网盘的扫码弹窗弹出来
        if (LoginCard is { IsLoggedIn: false }) IsLoginPanelOpen = true;

        // 自动验收：PANDRE_LOGINPANEL=1 时强制弹开扫码弹窗
        if (System.Environment.GetEnvironmentVariable("PANDRE_LOGINPANEL") == "1") IsLoginPanelOpen = true;
    }
}
