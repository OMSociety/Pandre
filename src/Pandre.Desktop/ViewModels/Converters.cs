using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Pandre.Desktop.ViewModels;

/// <summary>bool → 样式类（当前未用；保留供后续需要时直接用）。</summary>
public sealed class BoolToClassConverter : IValueConverter
{
    public static readonly BoolToClassConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var baseClass = parameter as string ?? "";
        return value is true ? baseClass + " current" : baseClass;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
