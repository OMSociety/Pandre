using System;
using System.Collections.Generic;
using System.IO;
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
    public required string Key { get; init; }     // Mica/Acrylic/MicaAlt/Aero/None
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
        new() { Key = "MicaAlt", Label = "MicaAlt" },
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

    /// <summary>数字输入校验提示（解析失败时显示，此前静默不保存用户无感知）。</summary>
    [ObservableProperty] private string? _aria2Hint;

    // ---- 浏览器接管 ----
    [ObservableProperty] private bool _takeoverEnabled;

    /// <summary>接管状态说明（未启用 / 监听地址 / 启动失败原因），为 null 时整行隐藏。</summary>
    [ObservableProperty] private string? _takeoverStatus;

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
        _takeoverEnabled = AppServices.TakeoverEnabled;   // 直接赋字段，避免 OnChanged 误触发
        RefreshTakeoverStatus();
    }

    partial void OnTakeoverEnabledChanged(bool value)
    {
        if (value == AppServices.TakeoverEnabled) { RefreshTakeoverStatus(); return; }
        AppServices.TakeoverEnabled = value;
        if (value)
        {
            AppServices.StartTakeover();
            try { ExtensionInstaller.Install(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("安装扩展失败: " + ex.Message); }
        }
        else AppServices.StopTakeover();
        RefreshTakeoverStatus();
    }

    /// <summary>「安装扩展」：把扩展文件复制到 AppData 并写入端口/令牌，然后打开所在文件夹。</summary>
    public void InstallExtension()
    {
        try
        {
            var dir = ExtensionInstaller.Install();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
            RefreshTakeoverStatus();
        }
        catch (Exception ex)
        {
            TakeoverStatus = "安装扩展失败：" + ex.Message;
        }
    }

    private void RefreshTakeoverStatus()
    {
        if (!AppServices.TakeoverEnabled) { TakeoverStatus = null; return; }
        if (!AppServices.Takeover.IsRunning)
        {
            TakeoverStatus = "接管入口启动失败（端口可能被占用），重启应用后再试。";
            return;
        }
        var installed = File.Exists(Path.Combine(ExtensionInstaller.TargetDir, "config.js"));
        TakeoverStatus = installed
            ? $"本地入口运行中：127.0.0.1:{AppServices.Takeover.Port}\n扩展目录：AppData\\Roaming\\Pandre\\extension（chrome://extensions 加载该目录）"
            : $"本地入口运行中：127.0.0.1:{AppServices.Takeover.Port}\n点「安装扩展」生成扩展目录，再到 chrome://extensions 加载它。";
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
            Aria2Hint = n is > 0 and <= 64 ? null : "连接数需在 1-64 之间";
            if (n != AppServices.Aria2Connections && n is > 0 and <= 64)
                AppServices.Aria2Connections = n;
        }
        else Aria2Hint = "连接数需为整数（当前改动未保存）";
    }

    partial void OnAria2MaxConcurrentChanged(string value)
    {
        if (int.TryParse(value, out var n))
        {
            Aria2Hint = n is > 0 and <= 20 ? null : "同时下载数需在 1-20 之间";
            if (n != AppServices.Aria2MaxConcurrent && n is > 0 and <= 20)
                AppServices.Aria2MaxConcurrent = n;
        }
        else Aria2Hint = "同时下载数需为整数（当前改动未保存）";
    }
}
