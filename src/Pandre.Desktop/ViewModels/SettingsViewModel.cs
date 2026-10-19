using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Pandre.Desktop.Services;

namespace Pandre.Desktop.ViewModels;

public sealed class ThemeOption
{
    public required string Key { get; init; }     // system/light/dark
    public required string Label { get; init; }   // 显示名
    public override string ToString() => Label;
}

public sealed class MaterialOption
{
    public required string Key { get; init; }     // Mica/Acrylic/Aero/None
    public required string Label { get; init; }
    public override string ToString() => Label;
}

public sealed class CloseBehaviorOption
{
    public required string Key { get; init; }     // exit/tray
    public required string Label { get; init; }
    public override string ToString() => Label;
}

public partial class SettingsViewModel : ViewModelBase
{
    public List<ThemeOption> ThemeOptions { get; } = new()
    {
        new() { Key = "system", Label = "跟随系统设置" },
        new() { Key = "light", Label = "浅色" },
        new() { Key = "dark", Label = "深色" },
    };

    public List<MaterialOption> MaterialOptions { get; } = new()
    {
        new() { Key = "Mica", Label = "Mica" },
        new() { Key = "Acrylic", Label = "Acrylic" },
        new() { Key = "Aero", Label = "Aero" },
        new() { Key = "None", Label = "None" },
    };

    public List<CloseBehaviorOption> CloseBehaviorOptions { get; } = new()
    {
        new() { Key = "exit", Label = "退出应用" },
        new() { Key = "tray", Label = "最小化到托盘" },
    };

    [ObservableProperty] private ThemeOption? _selectedTheme;
    [ObservableProperty] private MaterialOption? _selectedMaterial;
    [ObservableProperty] private CloseBehaviorOption? _selectedCloseBehavior;
    [ObservableProperty] private string _downloadDir = "";
    [ObservableProperty] private string _aria2Connections = "16";
    [ObservableProperty] private string _aria2MaxConcurrent = "5";

    /// <summary>两条数字输入各自独立的校验提示（此前共享一条互相抹除）。</summary>
    [ObservableProperty] private string? _aria2ConnectionsHint;
    [ObservableProperty] private string? _aria2MaxConcurrentHint;

    // 功能开关已挪到独立的「界面功能开关」面板（FeaturePanelViewModel），
    // 设置页只留一个入口按钮，避免两处各持一份状态。

    public SettingsViewModel()
    {
        // 从持久化读当前值
        var theme = AppServices.Theme;
        SelectedTheme = ThemeOptions.Find(o => o.Key == theme) ?? ThemeOptions[0];
        var material = AppServices.Material;
        SelectedMaterial = MaterialOptions.Find(o => o.Key == material) ?? MaterialOptions[0];
        var closeBehavior = AppServices.CloseBehavior;
        SelectedCloseBehavior = CloseBehaviorOptions.Find(o => o.Key == closeBehavior) ?? CloseBehaviorOptions[1];
        DownloadDir = AppServices.DownloadDir;
        Aria2Connections = AppServices.Aria2Connections.ToString();
        Aria2MaxConcurrent = AppServices.Aria2MaxConcurrent.ToString();
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (value != null && value.Key != AppServices.Theme) AppServices.Theme = value.Key;
    }

    partial void OnSelectedMaterialChanged(MaterialOption? value)
    {
        if (value != null && value.Key != AppServices.Material) AppServices.Material = value.Key;
    }

    partial void OnSelectedCloseBehaviorChanged(CloseBehaviorOption? value)
    {
        if (value != null && value.Key != AppServices.CloseBehavior) AppServices.CloseBehavior = value.Key;
    }

    partial void OnDownloadDirChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != AppServices.DownloadDir)
            AppServices.DownloadDir = value;
    }

    partial void OnAria2ConnectionsChanged(string value)
    {
        // 解析失败不静默：给出可见提示（此前静默不保存，用户不知道改动没生效）
        if (int.TryParse(value, out var n))
        {
            // aria2 的 max-connection-per-server 硬上限 16，超限 aria2c 直接拒绝启动（exit 28），故此处同步钳制
            Aria2ConnectionsHint = n is > 0 and <= 16 ? null : "连接数需在 1-16 之间";
            if (n is > 0 and <= 16 && n != AppServices.Aria2Connections)
            {
                // 引擎只在应用启动时按当时配置拉起，运行期改动只落设置、不热生效，不提示会被当成已生效
                AppServices.Aria2Connections = n;
                Aria2ConnectionsHint = "重启应用后生效";
            }
        }
        else Aria2ConnectionsHint = "连接数需为整数（当前改动未保存）";
    }

    partial void OnAria2MaxConcurrentChanged(string value)
    {
        if (int.TryParse(value, out var n))
        {
            Aria2MaxConcurrentHint = n is > 0 and <= 20 ? null : "同时下载数需在 1-20 之间";
            if (n != AppServices.Aria2MaxConcurrent && n is > 0 and <= 20)
                AppServices.Aria2MaxConcurrent = n;
        }
        else Aria2MaxConcurrentHint = "同时下载数需为整数（当前改动未保存）";
    }
}
