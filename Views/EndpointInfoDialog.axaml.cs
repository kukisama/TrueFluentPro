using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

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
        DetailsTextBox.Text = details;
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
        }
        finally
        {
            TestAllButton.Content = originalContent;
            TestAllButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
        }
    }
}