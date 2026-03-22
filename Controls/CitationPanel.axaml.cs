using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TrueFluentPro.Services.WebSearch;

namespace TrueFluentPro.Controls;

public partial class CitationPanel : UserControl
{
    private bool _isExpanded;

    public static readonly StyledProperty<IReadOnlyList<SearchCitation>> CitationsProperty =
        AvaloniaProperty.Register<CitationPanel, IReadOnlyList<SearchCitation>>(
            nameof(Citations), defaultValue: []);

    public IReadOnlyList<SearchCitation> Citations
    {
        get => GetValue(CitationsProperty);
        set => SetValue(CitationsProperty, value);
    }

    public CitationPanel()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CitationsProperty)
        {
            UpdateCitations();
        }
    }

    private void UpdateCitations()
    {
        var citations = Citations;
        if (citations.Count == 0)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        SummaryText.Text = $"{citations.Count} 个来源";
        CitationList.ItemsSource = citations;
        BuildFaviconPreview(citations);
    }

    /// <summary>构建叠加 favicon 预览（最多3个，Cherry 风格）</summary>
    private void BuildFaviconPreview(IReadOnlyList<SearchCitation> citations)
    {
        FaviconPreviewPanel.Children.Clear();
        var count = Math.Min(citations.Count, 3);
        // 每个 favicon 16px，叠加偏移 9px
        FaviconPreviewPanel.Width = count > 0 ? 16 + (count - 1) * 9 : 0;

        for (int i = 0; i < count; i++)
        {
            var favicon = new FaviconImage
            {
                Hostname = citations[i].Hostname,
                Margin = new Thickness(i * 9, 0, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            // 后面的 favicon 叠在前面之上（ZIndex 递增）
            favicon.ZIndex = i;
            FaviconPreviewPanel.Children.Add(favicon);
        }
    }

    private void OnToggleClick(object? sender, RoutedEventArgs e)
    {
        _isExpanded = !_isExpanded;
        CitationList.IsVisible = _isExpanded;
        ArrowText.Text = _isExpanded ? "▴" : "▾";
    }

    private void OnCitationPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is string url && !string.IsNullOrEmpty(url))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { /* 打开失败静默处理 */ }
        }
    }
}
