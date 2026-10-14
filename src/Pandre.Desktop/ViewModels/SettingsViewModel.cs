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
    public required string Key { get; init; }     // Mica/Acrylic/MicaAlt/Aero/None
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

    [ObservableProperty] private ThemeOption? _selectedTheme;
    [ObservableProperty] private MaterialOption? _selectedMaterial;
    [ObservableProperty] private string _downloadDir = "";
    [ObservableProperty] private string _aria2Connections = "16";
    [ObservableProperty] private string _aria2MaxConcurrent = "5";

    // 功能开关已挪到独立的「界面功能开关」面板（FeaturePanelViewModel），
    // 设置页只留一个入口按钮，避免两处各持一份状态。

    public SettingsViewModel()
    {
        // 从持久化读当前值
        var theme = AppServices.Theme;
        SelectedTheme = ThemeOptions.Find(o => o.Key == theme) ?? ThemeOptions[0];
        var material = AppServices.Material;
        SelectedMaterial = MaterialOptions.Find(o => o.Key == material) ?? MaterialOptions[0];
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

    partial void OnDownloadDirChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != AppServices.DownloadDir)
            AppServices.DownloadDir = value;
    }

    partial void OnAria2ConnectionsChanged(string value)
    {
        if (int.TryParse(value, out var n) && n != AppServices.Aria2Connections)
            AppServices.Aria2Connections = n;
    }

    partial void OnAria2MaxConcurrentChanged(string value)
    {
        if (int.TryParse(value, out var n) && n != AppServices.Aria2MaxConcurrent)
            AppServices.Aria2MaxConcurrent = n;
    }
}
