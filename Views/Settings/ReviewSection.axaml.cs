using Avalonia.Controls;
using Avalonia.Interactivity;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views.Settings;

public partial class ReviewSection : UserControl
{
    private ReviewSectionVM? _boundVm;

    public ReviewSection()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is ReviewSectionVM vm)
        {
            WireHandlers(vm);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        UnwireHandlers();
        base.OnUnloaded(e);
    }

    private void WireHandlers(ReviewSectionVM vm)
    {
        if (ReferenceEquals(_boundVm, vm))
            return;

        UnwireHandlers();
        _boundVm = vm;
        ReviewSheetsEditorControl.ItemsChanged += ReviewSheetsEditorControl_ItemsChanged;
    }

    private void UnwireHandlers()
    {
        ReviewSheetsEditorControl.ItemsChanged -= ReviewSheetsEditorControl_ItemsChanged;
        _boundVm = null;
    }

    private void ReviewSheetsEditorControl_ItemsChanged()
    {
        _boundVm?.NotifyReviewSheetsChanged();
    }
}
