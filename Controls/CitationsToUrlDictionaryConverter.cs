using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using TrueFluentPro.Services.WebSearch;

namespace TrueFluentPro.Controls;

/// <summary>
/// Citations リスト → {引用番号 → URL} 辞書。
/// ChatMarkdownBlock の CitationUrls プロパティに渡して角標クリックで URL を開く。
/// </summary>
public sealed class CitationsToUrlDictionaryConverter : IValueConverter
{
    public static readonly CitationsToUrlDictionaryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IReadOnlyList<SearchCitation> citations && citations.Count > 0)
            return citations.ToDictionary(c => c.Number, c => c.Url);
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
