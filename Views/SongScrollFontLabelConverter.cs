using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace TrueFluentPro.Views;

public sealed class SongScrollFontLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { 20 => "大（20）", 24 => "特大（24）", _ => "中（16）" };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => AvaloniaProperty.UnsetValue;
}