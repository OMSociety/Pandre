using System;
using Avalonia.Animation.Easings;

namespace Pandre.Desktop;

/// <summary>
/// 轻微过冲的收尾缓动（欠阻尼弹簧的阶跃响应）：
/// f(p) = 1 - e^(-ζωn·p)·(cos(ωd·p) + ζωn/ωd·sin(ωd·p))
/// 过冲幅度由阻尼比决定，σ = e^(-πζ/√(1-ζ²))，反解 ζ 后取 ωd 让 p=1 时基本落定。
/// 比内置 BackEaseOut 的固定 ~10% 过冲柔和可控（默认 3.5%）。
/// </summary>
public sealed class SoftBackEaseOut : Easing
{
    private const double OmegaD = 5.5;

    private readonly double _decay;
    private readonly double _ratio;

    /// <param name="overshoot">目标过冲比例，0.035 表示冲过目标 3.5% 后回落。</param>
    public SoftBackEaseOut(double overshoot = 0.035)
    {
        overshoot = Math.Clamp(overshoot, 0.0, 0.2);
        if (overshoot <= 0)
        {
            _decay = OmegaD * 2;
            _ratio = 0;
            return;
        }

        var ln = Math.Log(1 / overshoot);
        var zeta = ln / Math.Sqrt(Math.PI * Math.PI + ln * ln);
        _ratio = zeta / Math.Sqrt(1 - zeta * zeta);
        _decay = _ratio * OmegaD;
    }

    public override double Ease(double progress)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        return 1 - Math.Exp(-_decay * progress) * (Math.Cos(OmegaD * progress) + _ratio * Math.Sin(OmegaD * progress));
    }
}
