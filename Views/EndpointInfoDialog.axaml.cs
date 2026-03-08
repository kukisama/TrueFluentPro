using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.Views;

public partial class EndpointInfoDialog : Window
{
    private Func<Task>? _testAllRequestedAsync;

    public EndpointInfoDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        TestAllButton.Click += TestAllButton_Click;
    }

    public EndpointInfoDialog(string title, string details)
        : this()
    {
        TitleTextBlock.Text = title;
        ShowPlainText(details);
    }

    public EndpointInfoDialog(string title, EndpointInspectionDetails details)
        : this()
    {
        TitleTextBlock.Text = title;
        TrySetStructuredDetails(details);
    }

    public Func<Task>? TestAllRequestedAsync
    {
        get => _testAllRequestedAsync;
        set
        {
            _testAllRequestedAsync = value;
            TestAllButton.IsVisible = value != null;
        }
    }

    private async void TestAllButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_testAllRequestedAsync == null)
            return;

        var originalContent = TestAllButton.Content;
        TestAllButton.IsEnabled = false;
        CloseButton.IsEnabled = false;

        try
        {
            TestAllButton.Content = "测试中...";
            await _testAllRequestedAsync();
            Close();
        }
        catch (Exception ex)
        {
            CrashLogger.Write("EndpointInfoDialog.TestAllButton_Click", ex, isTerminating: false);

            var message = ex.Message?.Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                message = "发生了未知错误。";
            }

            var errorDialog = new EndpointInfoDialog(
                "终结点测试失败",
                $"# 终结点测试失败\n\n- 错误信息：{message}\n\n请先检查当前终结点配置、模型能力与资料包声明是否完整，然后再重试。");
            await errorDialog.ShowDialog(this);
        }
        finally
        {
            TestAllButton.Content = originalContent;
            TestAllButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }

    private void TrySetStructuredDetails(EndpointInspectionDetails details)
    {
        try
        {
            DetailsScrollViewer.IsVisible = true;
            DetailsFallbackTextBox.IsVisible = false;
            PopulateStructuredDetails(details);
        }
        catch (Exception ex)
        {
            CrashLogger.Write("EndpointInfoDialog.TrySetStructuredDetails", ex, isTerminating: false);
            ShowPlainText(BuildPlainText(details));
        }
    }

    private void ShowPlainText(string details)
    {
        DetailsScrollViewer.IsVisible = false;
        DetailsFallbackTextBox.IsVisible = true;
        DetailsFallbackTextBox.Text = details;
    }

    private void PopulateStructuredDetails(EndpointInspectionDetails details)
    {
        DetailsContentPanel.Children.Clear();

        DetailsContentPanel.Children.Add(new SelectableTextBlock
        {
            Text = details.Title,
            FontSize = 26,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        });

        DetailsContentPanel.Children.Add(new SelectableTextBlock
        {
            Text = details.Intro,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Black
        });

        DetailsContentPanel.Children.Add(CreateSection("基本信息", details.SummaryRows));

        foreach (var section in details.Sections)
        {
            var sectionPanel = CreateSection(section.Heading, section.Rows);
            if (section.UrlItems.Count > 0)
            {
                sectionPanel.Children.Add(new SelectableTextBlock
                {
                    Text = $"URL 地址（共 {section.UrlItems.Count} 条资料包声明，带 * 表示首选命中地址）",
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 10, 0, 8),
                    TextWrapping = TextWrapping.Wrap
                });
                sectionPanel.Children.Add(CreateUrlTable(section.UrlItems));
            }

            DetailsContentPanel.Children.Add(sectionPanel);
        }

        if (!string.IsNullOrWhiteSpace(details.FooterNote))
        {
            DetailsContentPanel.Children.Add(new SelectableTextBlock
            {
                Text = details.FooterNote,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
    }

    private static StackPanel CreateSection(string heading, IReadOnlyList<EndpointInspectionRow> rows)
    {
        var section = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 18)
        };
        section.Children.Add(new SelectableTextBlock
        {
            Text = heading,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        section.Children.Add(CreateKeyValueTable(rows));
        return section;
    }

    private static Border CreateKeyValueTable(IReadOnlyList<EndpointInspectionRow> rows)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("170,*")
        };

        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddCell(grid, i, 0, rows[i].Label, true);
            AddCell(grid, i, 1, rows[i].Value, false);
        }

        return WrapTable(grid);
    }

    private static Border CreateUrlTable(IReadOnlyList<EndpointInspectionUrlItem> items)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("110,*")
        };

        for (var i = 0; i < items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddCell(grid, i, 0, items[i].Label, true);
            AddCell(grid, i, 1, items[i].Url, false, true);
        }

        return WrapTable(grid);
    }

    private static Border WrapTable(Grid grid)
        => new()
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#D1D5DB")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = grid
        };

    private static void AddCell(Grid grid, int row, int column, string text, bool isHeader, bool mono = false)
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#D1D5DB")),
            BorderThickness = new Thickness(column == 0 ? 0 : 1, row == 0 ? 0 : 1, 0, 0),
            Background = isHeader ? new SolidColorBrush(Color.Parse("#F3F4F6")) : Brushes.Transparent,
            Padding = new Thickness(10, 8)
        };

        border.Child = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
            FontFamily = mono ? new FontFamily("Consolas") : FontFamily.Default
        };

        if (border.Child is TextBlock tb)
        {
            border.Child = new SelectableTextBlock
            {
                Text = tb.Text,
                TextWrapping = tb.TextWrapping,
                FontWeight = tb.FontWeight,
                FontFamily = tb.FontFamily
            };
        }

        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static string BuildPlainText(EndpointInspectionDetails details)
    {
        var lines = new System.Collections.Generic.List<string>
        {
            details.Title,
            string.Empty,
            details.Intro,
            string.Empty,
            "基本信息"
        };

        foreach (var row in details.SummaryRows)
        {
            lines.Add($"- {row.Label}：{row.Value}");
        }

        foreach (var section in details.Sections)
        {
            lines.Add(string.Empty);
            lines.Add(section.Heading);
            foreach (var row in section.Rows)
            {
                lines.Add($"- {row.Label}：{row.Value}");
            }

            if (section.UrlItems.Count > 0)
            {
                lines.Add($"- URL 地址：共 {section.UrlItems.Count} 条资料包声明，带 * 表示首选命中地址");
            }

            foreach (var urlItem in section.UrlItems)
            {
                lines.Add($"- {urlItem.Label}：{urlItem.Url}");
            }
        }

        if (!string.IsNullOrWhiteSpace(details.FooterNote))
        {
            lines.Add(string.Empty);
            lines.Add(details.FooterNote);
        }
        return string.Join(Environment.NewLine, lines);
    }
}