using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views;

/// <summary>独立长卷外层，仅复用原子分区与同一 MainWindowViewModel.Settings。</summary>
public partial class SongScrollSettingsView : SettingsView, ISongScrollPage
{
    public SongScrollShell Shell { get; }

    public SongScrollSettingsView() : base(false)
    {
        AvaloniaXamlLoader.Load(this);
        Shell = this.FindControl<SongScrollShell>("BookShell")!;
        InitializeSections(
            this.FindControl<ListBox>("ScrollSectionNavigation")!,
            this.FindControl<ScrollViewer>("ScrollSettingsScroller")!,
            this.FindControl<ContentControl>("ScrollSelectedSectionHost")!);
        // 不指定 Text 或改写字号；默认选择仍由 SettingsView.OnLoaded 处理。
    }

    protected override void PrepareSection(string sectionName, Control section)
    {
        base.PrepareSection(sectionName, section);
        // 终结点原子表单含双栏目录与固定宽凭据行；窄右页通过同一个
        // ScrollViewer 横向访问，不裁掉字段，也不把宽窗表单无限拉长。
        this.FindControl<ContentControl>("ScrollSelectedSectionHost")!.MinWidth =
            sectionName == "Section_Endpoints" ? 760 : 640;
        if (sectionName != "Section_Text" || section.DataContext is not TextSectionVM vm
            || section.FindControl<ComboBox>("DefaultFontSizeComboBox") is not { } fontSizes)
            return;

        // 原表单 OnLoaded 对列表外字号会选第一个值。先以无业务上下文的状态
        // 选中现有值；后续原 OnLoaded 仍照常接线，但不会把查看变成配置修改。
        var current = vm.DefaultFontSize.ToString(CultureInfo.InvariantCulture);
        var option = fontSizes.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), current, StringComparison.Ordinal));
        var context = section.DataContext;
        section.DataContext = null;
        try
        {
            if (option == null)
            {
                option = new ComboBoxItem { Tag = current, Content = current };
                fontSizes.Items.Add(option);
            }
            fontSizes.SelectedItem = option;
        }
        finally { section.DataContext = context; }
    }

    protected override void OnSectionShown(string sectionName)
    {
        base.OnSectionShown(sectionName);
        if (this.FindControl<ListBox>("ScrollSectionNavigation")?.SelectedItem is not ListBoxItem { Content: Grid item })
            return;
        var labels = item.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().ToArray();
        if (labels == null || labels.Length < 2) return;
        this.FindControl<TextBlock>("ScrollSectionHeading")!.Text = labels[0].Text;
        this.FindControl<TextBlock>("ScrollSectionDescription")!.Text = labels[1].Text;
    }

    private void OpenConfigLocation_Click(object? sender, RoutedEventArgs e)
        => OpenConfigLocationButton_Click(sender, e);
}

/// <summary>舞台坐标转回表单 DIP，保证宽窗不无限拉长、窄页仍可滚动访问字段。</summary>
public sealed class SongScrollSettingsFormWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double width && double.IsFinite(width) && width > 0
            ? Math.Clamp(width / 2, 640, 760)
            : 760d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}