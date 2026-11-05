using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace TrueFluentPro.Views;

/// <summary>Two native book pages, sharing only the original view's business interaction methods.</summary>
public partial class SongScrollMediaCenterV2View : MediaCenterV2View, ISongScrollPage
{
    public SongScrollShell Shell => this.FindControl<SongScrollShell>("BookShell")!;

    public SongScrollMediaCenterV2View() : base(false)
    {
        InitializeComponent();
    }

    private void SelectedWorkspaceMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control target && ViewModel?.SelectedWorkspaceTab is { } tab)
        {
            // Reuse the mature confirmation flow: removing a tab must not delete its files.
            BuildWorkspaceFlyout(tab).ShowAt(target, true);
            e.Handled = true;
        }
    }

    private void CanvasPreview_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.IsEditMode != true)
        {
            PreviewSurface_PointerPressed(sender, e);
            return;
        }

        // The large surface is the reference input in edit mode, not the previous result.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var path = ViewModel.WorkspaceSession?.ReferenceImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var preview = new ImagePreviewWindow(new[] { path }, 0);
        if (TopLevel.GetTopLevel(this) is Window window) preview.Show(window);
        else preview.Show();
        e.Handled = true;
    }
}