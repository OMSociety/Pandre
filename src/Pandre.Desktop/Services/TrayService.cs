using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Pandre.Desktop.Services;

/// <summary>托盘与主窗口显隐：关闭按钮的去向、从托盘唤回、真正退出。</summary>
public static class TrayService
{
    /// <summary>已请求真正退出（托盘菜单「退出应用」）；置位后关闭窗口不再被拦成隐藏。</summary>
    public static bool ExitRequested { get; private set; }

    /// <summary>关闭主窗口时是否改为隐藏（最小化到托盘），取决于设置里的关闭按钮行为。</summary>
    public static bool ShouldHideOnClose => !ExitRequested && AppServices.CloseBehavior == "tray";

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    /// <summary>唤回主窗口：隐藏则显示，最小化则还原，然后激活到前台。</summary>
    public static void ShowMainWindow()
    {
        var win = Desktop?.MainWindow;
        if (win is null) return;
        if (!win.IsVisible) win.Show();
        if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
        win.Activate();
    }

    /// <summary>真正退出：先记账再关窗，走窗口关闭的正常分支（aria2 与客户端在 Application.Exit 里统一收尾）。</summary>
    public static void RequestExit()
    {
        ExitRequested = true;
        var win = Desktop?.MainWindow;
        if (win is not null) win.Close();
        else Desktop?.Shutdown();
    }
}
