using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pandre.Desktop.Services;
using Pandre.Desktop.ViewModels;

namespace Pandre.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            var win = new Views.MainWindow { DataContext = vm };
            desktop.MainWindow = win;

            // 强调色跟随系统：把当前 Windows 强调色写进深浅两套主题字典（系统换色时即时跟随）
            AccentService.Attach();

            // 应用持久化的主题与透明材质
            AppServices.ApplyTheme();
            AppServices.ApplyMaterial();

            // 恢复登录态（不阻塞出窗；continuation 回 UI 线程更新属性）
            _ = InitSafelyAsync(vm);

            desktop.Exit += (_, _) =>
            {
                try { AppServices.Downloads.Dispose(); } catch { }
                try { AppServices.Baidu.Dispose(); AppServices.Quark.Dispose(); } catch { }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>托盘图标左键单击：唤回主窗口。</summary>
    private void OnTrayIconClicked(object? sender, System.EventArgs e) => TrayService.ShowMainWindow();

    /// <summary>托盘菜单「显示主界面」。</summary>
    private void OnTrayShowClicked(object? sender, System.EventArgs e) => TrayService.ShowMainWindow();

    /// <summary>托盘菜单「退出应用」：真正退出（下载与客户端在 Exit 里收尾）。</summary>
    private void OnTrayExitClicked(object? sender, System.EventArgs e) => TrayService.RequestExit();

    private static async System.Threading.Tasks.Task InitSafelyAsync(MainViewModel vm)
    {
        try { await vm.InitAsync(); }
        catch (System.Exception ex)
        {
            // 启动失败可见化：窗口底部状态条提示（此前只写调试输出，用户看不到）
            System.Diagnostics.Debug.WriteLine(ex);
            vm.InitError = "启动初始化失败：" + ex.Message;
        }
    }
}
