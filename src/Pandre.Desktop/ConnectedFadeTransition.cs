using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace Pandre.Desktop;

/// <summary>
/// 页面浮起进场过渡：旧页 150ms 快速淡出，新页 300ms 淡入归位。
/// 方向随导航：前进（页索引增大）从下方 20px 浮起，后退从上方 20px 落下。
/// 动画跑在 Visual 上、Setter 里写 transform 的属性（与官方 PageSlide 同一写法，
/// TransformAnimator 需要目标本身是 Visual）。
/// </summary>
public sealed class ConnectedFadeTransition : IPageTransition
{
    private const double RiseOffset = 20d;

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        // 后退方向新页从上方落下（forward 此前未接，反向导航一直播放正向动画）
        var riseOffset = forward ? RiseOffset : -RiseOffset;
        var tasks = new List<Task>(2);

        if (from != null)
        {
            var fadeOut = new Animation
            {
                FillMode = FillMode.Forward,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new SineEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
                    new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(Visual.OpacityProperty, 0.0) } },
                },
            };
            tasks.Add(fadeOut.RunAsync(from, cancellationToken));
        }

        if (to != null)
        {
            to.IsVisible = true;
            to.RenderTransform = new TranslateTransform();
            var fadeIn = new Animation
            {
                FillMode = FillMode.Forward,
                Duration = TimeSpan.FromMilliseconds(300),
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(Visual.OpacityProperty, 0.0) } },
                    new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
                    new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(TranslateTransform.YProperty, riseOffset) } },
                    new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(TranslateTransform.YProperty, 0.0) } },
                },
            };
            tasks.Add(fadeIn.RunAsync(to, cancellationToken));
        }

        await Task.WhenAll(tasks);

        if (cancellationToken.IsCancellationRequested) return;

        if (from != null)
        {
            from.IsVisible = false;
            from.RenderTransform = null;
        }

        if (to != null) to.RenderTransform = null;
    }
}
