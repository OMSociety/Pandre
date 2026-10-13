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
    public static QuarkPanClient Quark { get; } = new();

    public static DownloadManager Downloads { get; } = new();

    // ===== 设置（theme/material/downloadDir 统一持久化到 settings.json） =====

    /// <summary>主题：system / light / dark</summary>
    public static string Theme
    {
        get => LoadSettings().Theme ?? "system";
        set { var s = LoadSettings(); s.Theme = value; SaveSettings(s); ApplyTheme(); }
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

    private sealed class Settings
    {
        public string? Theme { get; set; }
        public string? Material { get; set; }
        public string? DownloadDir { get; set; }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pandre", "settings.json");

    private static Settings? _settingsCache;

    private static Settings LoadSettings()
    {
        if (_settingsCache != null) return _settingsCache;
        try
        {
            if (File.Exists(SettingsPath))
                _settingsCache = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
            else
                _settingsCache = new Settings();
        }
        catch { _settingsCache = new Settings(); }
        return _settingsCache;
    }

    private static void SaveSettings(Settings s)
    {
        _settingsCache = s;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
        }
        catch { /* 写不进就算了 */ }
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

    /// <summary>
    /// 启动时初始化：启动 aria2，恢复两家凭据并验证有效性。
    /// 返回各家的验证结果（true=凭据有效已附着）。
    /// </summary>
    public static async Task<(bool BaiduOk, bool QuarkOk)> InitAsync()
    {
        Downloads.Start(Aria2Engine.ResolveExePath(DefaultAria2Path()));

        var baiduOk = false;
        var quarkOk = false;

        var baiduCred = await Credentials.LoadAsync(PanProvider.Baidu);
        if (baiduCred != null)
        {
            try
            {
                Baidu.Attach(baiduCred);
                var info = await Baidu.GetAccountAsync();
                // 持久化可能被续期的 Cookie
                PersistIfChangedAsync(PanProvider.Baidu, baiduCred);
                baiduOk = true;
            }
            catch (PanException ex) when (ex.IsAuthExpired)
            {
                await Credentials.ClearAsync(PanProvider.Baidu);
                Baidu.Dispose();
            }
            catch { /* 网络问题等：维持附着，让用户重试 */ baiduOk = Baidu.IsAuthenticated; }
        }

        var quarkCred = await Credentials.LoadAsync(PanProvider.Quark);
        if (quarkCred != null)
        {
            try
            {
                Quark.Attach(quarkCred);
                await Quark.GetAccountAsync();
                PersistIfChangedAsync(PanProvider.Quark, quarkCred);
                quarkOk = true;
            }
            catch (PanException ex) when (ex.IsAuthExpired)
            {
                await Credentials.ClearAsync(PanProvider.Quark);
                Quark.Dispose();
            }
            catch { quarkOk = Quark.IsAuthenticated; }
        }

        return (baiduOk, quarkOk);
    }

    private static void PersistIfChangedAsync(PanProvider provider, PanCredential old)
    {
        var client = provider == PanProvider.Baidu ? (IPanClient)Baidu : Quark;
        var fresh = client.ExportCookie();
        if (fresh != null && fresh != old.Cookie)
            _ = Credentials.SaveAsync(old with { Cookie = fresh, ObtainedAt = System.DateTimeOffset.Now });
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
