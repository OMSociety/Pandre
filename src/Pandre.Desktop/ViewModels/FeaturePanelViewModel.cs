using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pandre.Desktop.Services;

namespace Pandre.Desktop.ViewModels;

/// <summary>功能开关面板里的一项（标题 + 说明 + 开关）。</summary>
public partial class FeatureToggleViewModel : ViewModelBase
{
    private readonly Action<bool> _apply;

    public FeatureToggleViewModel(string title, string description, bool initial, Action<bool> apply)
    {
        Title = title;
        Description = description;
        _apply = apply;
        _isOn = initial;
    }

    public string Title { get; }
    public string Description { get; }

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => _apply(value);
}

/// <summary>
/// 界面功能开关面板：列出可显示/隐藏的功能，改动立即生效并持久化。
/// 提示文案由 IsVisible 绑标题，避免整块开关同步刷新。
/// </summary>
public sealed class FeaturePanelViewModel : ViewModelBase
{
    public FeaturePanelViewModel()
    {
        Toggles = new ObservableCollection<FeatureToggleViewModel>
        {
            new("转存", "把外部分享链接保存到当前目录",
                AppServices.ShowSaveShare, v => AppServices.ShowSaveShare = v),
            new("离线下载", "云端直接下载 magnet / ed2k（百度）",
                AppServices.ShowOfflineDownload, v => AppServices.ShowOfflineDownload = v),
            new("分享管理", "集中管理已创建的分享链接",
                AppServices.ShowShareManage, v => AppServices.ShowShareManage = v),
        };
    }

    public ObservableCollection<FeatureToggleViewModel> Toggles { get; }
}
