using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Pandre.Core;
using Pandre.Core.Baidu;
using Pandre.Core.Quark;
using Pandre.Download;

namespace Pandre.Desktop.Services;

/// <summary>应用级服务容器：凭据存储、两家客户端（单例）、下载管理器（单例）。</summary>
public static class AppServices
{
    public static CredentialStore Credentials { get; } = new();

    public static BaiduPanClient Baidu { get; } = new();

    /// <summary>夸克客户端单例；注入断点续传存储（%APPDATA%\Pandre\upload-resume.json，仅服务端上传会话标识，不含 Cookie/凭据）。</summary>
    public static QuarkPanClient Quark { get; } = new() { UploadResumeStore = new JsonUploadResumeStore(AppDataDirPath) };

    public static DownloadManager Downloads { get; } = new();

    /// <summary>设置文件所在目录（%APPDATA%\Pandre）。</summary>
    public static string AppDataDirPath => Path.GetDirectoryName(SettingsPath)!;

    /// <summary>项目仓库地址（关于页跳转用）。</summary>
    public const string RepositoryUrl = "https://github.com/OMSociety/Pandre";

    /// <summary>开发者主页地址（关于页跳转用）。</summary>
    public const string AuthorUrl = "https://github.com/OMSociety";

    // ===== 设置（theme/material/downloadDir 统一持久化到 settings.json） =====

    /// <summary>主题：system / light / dark。变更后需重刷材质：None 材质的窗口实底色依赖当前主题。</summary>
    public static string Theme
    {
        get => LoadSettings().Theme ?? "system";
        set { var s = LoadSettings(); s.Theme = value; SaveSettings(s); ApplyTheme(); ApplyMaterial(); }
    }

    /// <summary>透明材质：Mica / Acrylic / MicaAlt / Aero / None</summary>
    public static string Material
    {
        get => LoadSettings().Material ?? "Mica";
        set { var s = LoadSettings(); s.Material = value; SaveSettings(s); ApplyMaterial(); }
    }

    /// <summary>下载落盘目录（默认 ~/Downloads/Pandre）。</summary>
    public static string DownloadDir
    {
        get => LoadSettings().DownloadDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Pandre");
        set { var s = LoadSettings(); s.DownloadDir = value; SaveSettings(s); }
    }

    /// <summary>单任务连接数（aria2 --split / --max-connection-per-server），1-16（aria2 硬上限 16，超限引擎拒绝启动），默认 16。引擎在应用启动时固定此值，改后重启应用生效。</summary>
    public static int Aria2Connections
    {
        get => LoadSettings().Aria2Connections is > 0 and <= 16 ? LoadSettings().Aria2Connections!.Value : 16;
        set { var s = LoadSettings(); s.Aria2Connections = Math.Clamp(value, 1, 16); SaveSettings(s); }
    }

    /// <summary>全局同时下载数（aria2 --max-concurrent-downloads），1-20，默认 5。引擎在应用启动时固定此值，改后重启应用生效。</summary>
    public static int Aria2MaxConcurrent
    {
        get => LoadSettings().Aria2MaxConcurrent is > 0 and <= 20 ? LoadSettings().Aria2MaxConcurrent!.Value : 5;
        set { var s = LoadSettings(); s.Aria2MaxConcurrent = Math.Clamp(value, 1, 20); SaveSettings(s); }
    }

    /// <summary>标题栏关闭按钮行为：exit（退出应用）/ tray（最小化到托盘，应用继续在后台运行）。默认 tray。</summary>
    public static string CloseBehavior
    {
        get => LoadSettings().CloseBehavior == "exit" ? "exit" : "tray";
        set { var s = LoadSettings(); s.CloseBehavior = value == "exit" ? "exit" : "tray"; SaveSettings(s); }
    }

    // ===== 功能开关（全局：百度与夸克同时生效） =====
    /// <summary>功能开关变化后通知各页面刷新可见性。设置页改动时触发。</summary>
    public static event Action? FeatureFlagsChanged;

    /// <summary>是否启用「转存」（外部分享链接保存到当前目录）。</summary>
    public static bool ShowSaveShare
    {
        get => LoadSettings().ShowSaveShare ?? true;
        set { var s = LoadSettings(); s.ShowSaveShare = value; SaveSettings(s); FeatureFlagsChanged?.Invoke(); }
    }

    /// <summary>是否启用「离线下载」（百度 magnet/ed2k 云端下载）。</summary>
    public static bool ShowOfflineDownload
    {
        get => LoadSettings().ShowOfflineDownload ?? true;
        set { var s = LoadSettings(); s.ShowOfflineDownload = value; SaveSettings(s); FeatureFlagsChanged?.Invoke(); }
    }

    /// <summary>是否启用「分享管理」（列出/复制/取消我的分享）。</summary>
    public static bool ShowShareManage
    {
        get => LoadSettings().ShowShareManage ?? true;
        set { var s = LoadSettings(); s.ShowShareManage = value; SaveSettings(s); FeatureFlagsChanged?.Invoke(); }
    }

    private sealed class Settings
    {
        public string? Theme { get; set; }
        public string? Material { get; set; }
        public string? DownloadDir { get; set; }
        public int? Aria2Connections { get; set; }
        public int? Aria2MaxConcurrent { get; set; }
        public bool? ShowSaveShare { get; set; }
        public bool? ShowOfflineDownload { get; set; }
        public bool? ShowShareManage { get; set; }
        public string? CloseBehavior { get; set; }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pandre", "settings.json");

    private static readonly object _settingsGate = new();
    private static Settings? _settingsCache;

    private static Settings LoadSettings()
    {
        // 加锁懒初始化：UI 线程与 Task.Run 启动链并发读时，避免各建一份实例互相丢更新
        lock (_settingsGate)
        {
            if (_settingsCache != null) return _settingsCache;
            try
            {
                if (File.Exists(SettingsPath))
                    _settingsCache = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
                else
                    _settingsCache = new Settings();
            }
            catch (Exception ex)
            {
                // 坏文件回默认值，留调试痕迹
                System.Diagnostics.Debug.WriteLine("[AppServices] 设置读取失败，回默认值: " + ex.Message);
                _settingsCache = new Settings();
            }
            return _settingsCache;
        }
    }

    private static void SaveSettings(Settings s)
    {
        _settingsCache = s;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch (Exception ex)
        {
            // 写不进（目录只读/磁盘满等）留调试痕迹，不静默
            System.Diagnostics.Debug.WriteLine("[AppServices] 设置写盘失败: " + ex.Message);
        }
    }

    /// <summary>把主题应用到当前 Application。</summary>
    public static void ApplyTheme()
    {
        var app = Avalonia.Application.Current;
        if (app == null) return;
        app.RequestedThemeVariant = Theme switch
        {
            "light" => Avalonia.Styling.ThemeVariant.Light,
            "dark" => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };
    }

    /// <summary>把透明材质应用到主窗口。</summary>
    public static void ApplyMaterial()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            // Avalonia 12：按优先级给一串候选，框架取第一个被系统支持的
            desktop.MainWindow.TransparencyLevelHint = Material switch
            {
                "Acrylic" => new[] { Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur, Avalonia.Controls.WindowTransparencyLevel.None },
                "MicaAlt" => new[] { Avalonia.Controls.WindowTransparencyLevel.Mica, Avalonia.Controls.WindowTransparencyLevel.None },
                "Aero" => new[] { Avalonia.Controls.WindowTransparencyLevel.Blur, Avalonia.Controls.WindowTransparencyLevel.None },
                "None" => new[] { Avalonia.Controls.WindowTransparencyLevel.None },
                _ => new[] { Avalonia.Controls.WindowTransparencyLevel.Mica, Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur, Avalonia.Controls.WindowTransparencyLevel.None },
            };
            // None 时要给窗口一个实底，否则全透明；底色跟随主题
            if (Material == "None")
            {
                var isLight = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;
                desktop.MainWindow.Background = new Avalonia.Media.SolidColorBrush(
                    Avalonia.Media.Color.Parse(isLight ? "#F3F3F3" : "#202020"));
            }
            else
            {
                desktop.MainWindow.Background = Avalonia.Media.Brushes.Transparent;
            }
        }
    }

    /// <summary>启动 aria2 下载引擎（幂等）。aria2 缺失/启动失败不阻断登录恢复；下载时引擎会报具体原因。</summary>
    public static void StartDownloads()
    {
        try { Downloads.Start(DefaultAria2Path(), Aria2Connections, Aria2MaxConcurrent); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("aria2 启动失败: " + ex.Message); }
    }

    public static async Task SaveCredentialAsync(PanCredential cred)
    {
        await Credentials.SaveAsync(cred);
        if (cred.Provider == PanProvider.Baidu) Baidu.Attach(cred); else Quark.Attach(cred);
    }

    private static string? DefaultAria2Path()
    {
        // Desktop 项目通过 CopyToOutputDirectory 把 aria2 放到输出 assets/windows/
        var besideApp = Path.Combine(AppContext.BaseDirectory, "assets", "windows", "aria2c.exe");
        return File.Exists(besideApp) ? besideApp : null;
    }
}
