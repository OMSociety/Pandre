using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pandre.Core;
using Pandre.Desktop.Services;

namespace Pandre.Desktop.ViewModels;

public sealed partial class NavItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public required string Title { get; init; }
    public required string Icon { get; init; }         // Regular（未选中）
    public string? IconFilled { get; init; }           // Filled（当前页）
    public required ViewModelBase Page { get; init; }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(CurrentIcon))]
    private bool _isCurrent;

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

    /// <summary>功能开关面板（改一项立即写盘并广播）。</summary>
    public FeaturePanelViewModel FeaturePanel { get; } = new();

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private bool _isNavBack;

    /// <summary>当前页面对应的登录卡（百度/夸克页才有，其余页为 null）。</summary>
    [ObservableProperty] private LoginCardViewModel? _loginCard;

    /// <summary>登录伪弹窗是否打开。</summary>
    [ObservableProperty] private bool _isLoginPanelOpen;

    /// <summary>启动初始化失败信息（可见化：窗口底部状态条提示，此前只写调试输出用户看不到）。</summary>
    [ObservableProperty] private string? _initError;

    /// <summary>功能开关伪弹窗是否打开。</summary>
    [ObservableProperty] private bool _isFeaturePanelOpen;

    [RelayCommand] private void OpenFeaturePanel() => IsFeaturePanelOpen = true;
    [RelayCommand] private void CloseFeaturePanel() => IsFeaturePanelOpen = false;

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

        // 两个网盘用各自的品牌图标（Icons.Brand.cs）；下载与设置用微软 Fluent System Icons（MIT），见 Icons.cs
        // 品牌图标是单色单形态，选中态由背板强调色与左侧指示条表达，不换图标
        NavItems = new ObservableCollection<NavItem>
        {
            new() { Title = "百度", Icon = AppIcons.BaiduNetdisk, Page = Baidu },
            new() { Title = "夸克", Icon = AppIcons.QuarkNetdisk, Page = Quark },
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
        // 弹窗可见性要跟着两侧 IsDialogOpen 变化通知
        Baidu.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanViewModel.IsDialogOpen)) OnPropertyChanged(nameof(IsPanDialogOpen));
        };
        Quark.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanViewModel.IsDialogOpen)) OnPropertyChanged(nameof(IsPanDialogOpen));
        };
    }

    /// <summary>当前页若是网盘页则返回其 VM（网盘弹窗遮罩要挂窗口级盖住侧栏），否则 null。</summary>
    public PanViewModel? CurrentPan => CurrentPage as PanViewModel;

    /// <summary>网盘弹窗可见性（null 安全，永远能算出真值——弹窗 Grid 的 IsVisible 直接绑它，别绑 CurrentPan.IsDialogOpen：后者在 CurrentPan=null 时是 UnsetValue，会让 IsVisible 回默认值 true）。</summary>
    public bool IsPanDialogOpen => CurrentPan is { IsDialogOpen: true };

    partial void OnCurrentPageChanged(ViewModelBase? value) => OnPropertyChanged(nameof(CurrentPan));

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        // 切页时收掉另一页可能开着的弹窗
        Baidu.DialogCancelCommand.Execute(null);
        Quark.DialogCancelCommand.Execute(null);
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

    /// <summary>启动时初始化：起下载引擎 + 恢复登录态。</summary>
    public async Task InitAsync()
    {
        try
        {
            // 引擎启动含同步等待 RPC 就绪，放线程池并行，不占 UI 线程
            _ = System.Threading.Tasks.Task.Run(AppServices.StartDownloads);
            await Login.InitAsync();
            OnSessionChanged();
            _sessionRestored = true;
            // 默认停在已登录的网盘页，都没有则停在百度页
            SelectedNav = Login.Quark.IsLoggedIn && !Login.Baidu.IsLoggedIn ? NavItems[1] : NavItems[0];

            // 自动验收：PANDRE_AUTOCHECK=<页索引> 时启动后直跳该页
            var auto = System.Environment.GetEnvironmentVariable("PANDRE_AUTOCHECK");
            if (int.TryParse(auto, out var idx) && idx >= 0 && idx < NavItems.Count)
                SelectedNav = NavItems[idx];

            // 首次运行没凭据：直接把当前网盘的扫码弹窗弹出来
            if (LoginCard is { IsLoggedIn: false }) IsLoginPanelOpen = true;

            // 自动验收：PANDRE_LOGINPANEL=1 时强制弹开扫码弹窗
            if (System.Environment.GetEnvironmentVariable("PANDRE_LOGINPANEL") == "1") IsLoginPanelOpen = true;
        }
        finally
        {
            // 无论成败都复位抑制并激活当前页（异常时此前永不复位，页面激活会全空转）
            _suppressPageActivation = false;
            if (CurrentPage is PanViewModel current) await current.ActivateAsync();
            else if (CurrentPage is DownloadsViewModel downloads) downloads.Refresh();
        }
    }
}
