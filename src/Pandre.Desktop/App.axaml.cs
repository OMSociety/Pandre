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
