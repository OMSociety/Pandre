using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Pandre.Desktop.Services;

/// <summary>
/// 强调色跟随 Windows 系统强调色（设置 → 个性化 → 颜色）。
/// 取色走 Avalonia 的 IPlatformSettings（与注册表 ColorizationColor 同源），不自己 P/Invoke；
/// 系统换色时经 ColorValuesChanged 即时跟随，深浅两套主题字典各写一份。
/// 系统色在某个主题底色上对比度不足 4.5:1 时，只沿明度轴推到可读为止，色相与饱和度不动。
/// </summary>
public static class AccentService
{
    // 选中背板透明度：与 App.axaml 里原有的 2A / 1F 一致
    private const double NavSelectedAlphaDark = 0x2A / 255d;
    private const double NavSelectedAlphaLight = 0x1F / 255d;

    private static readonly Color DarkSurface = Color.Parse("#202020");
    private static readonly Color LightSurface = Color.Parse("#FFFFFF");

    private static bool _attached;

    /// <summary>启动时调用一次：先订阅系统换色，再落一次当前色。</summary>
    public static void Attach()
    {
        var settings = Application.Current?.PlatformSettings;
        if (settings is null) return;

        if (!_attached)
        {
            _attached = true;
            settings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(Apply);
        }

        Apply();
    }

    /// <summary>取当前系统强调色，写进深浅两套主题字典，并同步 Fluent 内建控件的强调色。</summary>
    public static void Apply()
    {
        var app = Application.Current;
        var settings = app?.PlatformSettings;
        if (app is null || settings is null) return;

        Color accent;
        try
        {
            accent = settings.GetColorValues().AccentColor1;
        }
        catch
        {
            return; // 平台读不到就保留 App.axaml 里的默认色
        }

        // 平台给的 alpha 不参与配色，统一按不透明处理
        accent = Color.FromRgb(accent.R, accent.G, accent.B);

        var darkAccent = Readable(accent, DarkSurface);
        var lightAccent = Readable(accent, LightSurface);

        WriteTheme(app, ThemeVariant.Dark, darkAccent, NavSelectedAlphaDark);
        WriteTheme(app, ThemeVariant.Light, lightAccent, NavSelectedAlphaLight);
        WriteFluentAccent(app, darkAccent, lightAccent);
    }

    private static void WriteTheme(Application app, ThemeVariant variant, Color accent, double navAlpha)
    {
        if (app.Resources.ThemeDictionaries.TryGetValue(variant, out var node)
            && node is ResourceDictionary dict)
        {
            dict["AccentColor"] = accent;
            dict["AccentBrush"] = new SolidColorBrush(accent);
            dict["NavSelectedBrush"] = new SolidColorBrush(accent, navAlpha);
            dict["OnAccentBrush"] = new SolidColorBrush(OnAccent(accent));
        }
    }

    private static void WriteFluentAccent(Application app, Color darkAccent, Color lightAccent)
    {
        var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent is null) return;

        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = darkAccent };
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = lightAccent };
    }

    /// <summary>压在强调色上的文字取黑或白，谁对比度高用谁。</summary>
    private static Color OnAccent(Color accent)
        => Contrast(Colors.White, accent) >= Contrast(Colors.Black, accent) ? Colors.White : Colors.Black;

    /// <summary>在指定底色上保证可读（WCAG 对比度 4.5:1）；已达标就原样返回。</summary>
    private static Color Readable(Color accent, Color surface)
    {
        const double target = 4.5;
        if (Contrast(accent, surface) >= target) return accent;

        var (h, s, l) = ToHsl(accent);
        var towardDark = Luminance(surface) > 0.5; // 浅底就压暗，深底就提亮

        for (var step = 1; step <= 100; step++)
        {
            var nl = towardDark ? l - step * 0.01 : l + step * 0.01;
            if (nl < 0 || nl > 1) break;

            var candidate = FromHsl(h, s, nl);
            if (Contrast(candidate, surface) >= target) return candidate;
        }

        return towardDark ? Colors.Black : Colors.White;
    }

    private static double ChannelToLinear(double v)
        => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double Luminance(Color c)
        => 0.2126 * ChannelToLinear(c.R / 255d)
         + 0.7152 * ChannelToLinear(c.G / 255d)
         + 0.0722 * ChannelToLinear(c.B / 255d);

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d == 0) return (0, 0, l);

        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;

        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0)
        {
            var v = (byte)Math.Round(Math.Clamp(l, 0, 1) * 255);
            return Color.FromRgb(v, v, v);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        return Color.FromRgb(
            (byte)Math.Round(Math.Clamp(HueToRgb(p, q, h + 1d / 3), 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(HueToRgb(p, q, h), 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(HueToRgb(p, q, h - 1d / 3), 0, 1) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1d / 6) return p + (q - p) * 6 * t;
        if (t < 1d / 2) return q;
        if (t < 2d / 3) return p + (q - p) * (2d / 3 - t) * 6;
        return p;
    }
}