using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views.Settings;

public partial class ReviewSection : UserControl
{
    public ReviewSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainWindowViewModel vm)
        {
            ReviewSheetsEditorControl.ItemsChanged += () => vm.Settings.NotifyReviewSheetsChanged();
        }
    }
}
