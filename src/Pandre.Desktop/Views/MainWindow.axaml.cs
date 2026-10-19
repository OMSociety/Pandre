using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Pandre.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>指示条基准高度（拉伸脉冲围绕它做，布局居中由 VerticalAlignment 负责）。</summary>
    private const double IndicatorHeight = 20;

    private int _indicatorRetries;
    private double _indicatorOffset = double.NaN;
    private bool _indicatorReady;
    private bool _pulsePending = true;
    private CancellationTokenSource? _pulseCts;

    public MainWindow()
    {
        InitializeComponent();
        NavList.SelectionChanged += (_, _) => RequestIndicatorUpdate(pulse: true);
        // 窗口缩放/最大化会改变导航区高度：旧位移是按旧高度算的，必须重算，否则指示条会飘到顶上
        NavArea.SizeChanged += (_, _) => RequestIndicatorUpdate(pulse: false);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateMaximizeIcon();
        };
        Opened += (_, _) => RequestIndicatorUpdate(pulse: false);
    }

    /// <summary>最大化/还原按钮图标随窗口状态切换。</summary>
    private void UpdateMaximizeIcon()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>关闭按钮行为为「最小化到托盘」时，把关闭改成隐藏：窗口从任务栏消失、应用继续在后台跑，
    /// 托盘图标左键唤回；托盘菜单「退出应用」会先置位再关窗，走正常退出分支。</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Services.TrayService.ShouldHideOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>标题栏拖拽移动窗口。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>点遮罩空白处收起弹窗（点卡片内部不关）。</summary>
    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is ViewModels.MainViewModel vm)
            vm.CloseLoginPanelCommand.Execute(null);
    }

    /// <summary>弹窗右上角关闭按钮。</summary>
    private void OnCloseLoginPanelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm) vm.CloseLoginPanelCommand.Execute(null);
    }

    /// <summary>功能开关弹窗：点遮罩空白关闭。</summary>
    private void OnFeatureOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is ViewModels.MainViewModel vm)
            vm.IsFeaturePanelOpen = false;
    }

    /// <summary>功能开关弹窗左上角返回箭头。</summary>
    private void OnCloseFeaturePanelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm) vm.IsFeaturePanelOpen = false;
    }

    /// <summary>网盘弹窗：点遮罩空白关闭（遮罩的 DataContext 是 MainViewModel，弹窗卡片才是 PanViewModel）。</summary>
    private void OnPanDialogScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is ViewModels.MainViewModel vm && vm.CurrentPan != null)
            vm.CurrentPan.DialogCancelCommand.Execute(null);
    }

    /// <summary>复制网盘弹窗正文（分享链接）到剪贴板。</summary>
    private async void OnCopyPanDialogTextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ViewModels.PanViewModel vm) return;
        try
        {
            var clipboard = Clipboard;
            if (clipboard != null && vm.DialogText.Length > 0)
            {
                // Avalonia 12 新剪贴板 API：DataTransfer + DataTransferItem
                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.CreateText(vm.DialogText));
                await clipboard.SetDataAsync(transfer);
                vm.StatusMessage = "已复制到剪贴板";
            }
        }
        catch (Exception ex)
        {
            // 剪贴板被占用等瞬时故障不崩进程（async void 未捕获异常直接炸，深审 CL-02）
            vm.StatusMessage = "复制失败：" + ex.Message;
        }
    }

    /// <summary>分享管理行内「复制」：复制该条链接（有提取码则带上 ?pwd=）。</summary>
    private async void OnCopyShareLinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ViewModels.ShareRowViewModel row) return;
        try
        {
            var clipboard = Clipboard;
            if (clipboard == null || row.Share.ShareUrl.Length == 0) return;
            var url = row.Share.Passcode != null && !row.Share.ShareUrl.Contains("?pwd=")
                ? $"{row.Share.ShareUrl}?pwd={row.Share.Passcode}" : row.Share.ShareUrl;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(url));
            await clipboard.SetDataAsync(transfer);
        }
        catch (Exception ex)
        {
            // 同上：async void 必须自接异常（深审 CL-02）
            System.Diagnostics.Debug.WriteLine("[ui] 复制分享链接失败: " + ex.Message);
        }
    }

    /// <summary>Esc 收起弹窗（登录弹窗 / 功能开关 / 网盘操作弹窗）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ViewModels.MainViewModel vm)
        {
            if (vm.IsFeaturePanelOpen)
            {
                vm.IsFeaturePanelOpen = false;
                e.Handled = true;
                return;
            }
            if (vm.IsLoginPanelOpen)
            {
                vm.CloseLoginPanelCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (vm.CurrentPan is { IsDialogOpen: true } pan)
            {
                pan.DialogCancelCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    /// <summary>等渲染完成后量坐标（选择变化时容器可能还没布局）。</summary>
    private void RequestIndicatorUpdate(bool pulse)
    {
        _pulsePending = pulse;
        _indicatorRetries = 0;
        Dispatcher.UIThread.Post(UpdateNavIndicator, DispatcherPriority.Render);
    }

    /// <summary>
    /// 果冻指示条：实测选中项中心 → 相对导航区中心算位移；
    /// 位移走 RenderTransform 过渡（SoftBackEaseOut 轻过冲回弹），
    /// 拉伸脉冲走 Height 过渡（跳得越远拉得越长）。两者都只动合成/轻量布局。
    /// pulse=false 用于窗口尺寸变化：瞬时跟位，不脉冲。
    /// </summary>
    private void UpdateNavIndicator()
    {
        var idx = NavList.SelectedIndex;
        if (idx < 0) { NavIndicator.IsVisible = false; return; }

        if (NavList.ContainerFromIndex(idx) is not Visual container
            || container.Bounds.Height <= 0
            || NavArea.Bounds.Height <= 0)
        {
            if (_indicatorRetries++ < 10)
                Dispatcher.UIThread.Post(UpdateNavIndicator, DispatcherPriority.Render);
            return;
        }

        var pulse = _pulsePending;
        _pulsePending = true;

        var topLeft = container.TranslatePoint(new Point(0, 0), NavArea) ?? new Point(4, 0);
        var targetCenter = topLeft.Y + container.Bounds.Height / 2;
        var offset = targetCenter - NavArea.Bounds.Height / 2;
        NavIndicator.Margin = new Thickness(topLeft.X + 4, 0, 0, 0);
        var transform = TransformOperations.Parse(TranslateY(offset));

        // 首次落位：不滑动、不拉伸，落位后再挂过渡
        if (!_indicatorReady)
        {
            _indicatorReady = true;
            _indicatorOffset = offset;
            NavIndicator.RenderTransform = transform;
            NavIndicator.Transitions = new Transitions
            {
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(420),
                    Easing = new SoftBackEaseOut(),
                },
                new DoubleTransition
                {
                    Property = Layoutable.HeightProperty,
                    Duration = TimeSpan.FromMilliseconds(200),
                    Easing = new CubicEaseOut(),
                },
            };
            NavIndicator.IsVisible = true;
            return;
        }

        if (!pulse)
        {
            // 尺寸变化：摘掉过渡瞬时跟位，避免缩放时指示条滞后或飘位
            var transitions = NavIndicator.Transitions;
            NavIndicator.Transitions = null;
            NavIndicator.RenderTransform = transform;
            NavIndicator.Height = IndicatorHeight;
            NavIndicator.Transitions = transitions;
            _indicatorOffset = offset;
            NavIndicator.IsVisible = true;
            return;
        }

        var distance = Math.Abs(offset - _indicatorOffset);
        _indicatorOffset = offset;
        if (distance < 0.5)
        {
            NavIndicator.IsVisible = true;
            return;
        }

        NavIndicator.RenderTransform = transform;
        _ = PulseAsync(distance);
        NavIndicator.IsVisible = true;
    }

    /// <summary>拉伸脉冲：先拉长再缩回基准高度（距离越远拉得越长）。</summary>
    private async Task PulseAsync(double distance)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pulseCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            var stretch = Math.Clamp(IndicatorHeight * (1.0 + distance / 90.0 * 0.45), IndicatorHeight, IndicatorHeight * 1.9);
            NavIndicator.Height = stretch;
            await Task.Delay(190, cts.Token);
            NavIndicator.Height = IndicatorHeight;
        }
        catch (OperationCanceledException)
        {
            // 被下一次切换打断：高度由新脉冲接管
        }
    }

    private static string TranslateY(double offset) =>
        "translateY(" + offset.ToString("0.###", CultureInfo.InvariantCulture) + "px)";
}
