using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using TrueFluentPro.ViewModels.EndpointTesting;
using TrueFluentPro.Services.EndpointTesting;

namespace TrueFluentPro.Views.EndpointTesting;

public partial class EndpointBatchTestDialog : Window
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Func<IProgress<EndpointBatchTestProgressSnapshot>, CancellationToken, Task>? _runTestAsync;
    private bool _testStarted;

    public EndpointBatchTestDialog()
    {
        InitializeComponent();
        CloseButton.Click += CloseButton_Click;
        CancelButton.Click += CancelButton_Click;
        Opened += EndpointBatchTestDialog_Opened;
        Closed += EndpointBatchTestDialog_Closed;
    }

    public EndpointBatchTestDialog(
        EndpointBatchTestDialogViewModel viewModel,
        Func<IProgress<EndpointBatchTestProgressSnapshot>, CancellationToken, Task> runTestAsync)
        : this()
    {
        DataContext = viewModel;
        _runTestAsync = runTestAsync;
    }

    private async void EndpointBatchTestDialog_Opened(object? sender, EventArgs e)
    {
        if (_testStarted || DataContext is not EndpointBatchTestDialogViewModel viewModel || _runTestAsync == null)
            return;

        _testStarted = true;
        viewModel.MarkStarted();

        try
        {
            await _runTestAsync(viewModel.CreateProgressReporter(), _cancellationTokenSource.Token);
            viewModel.MarkFinished();
        }
        catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
        {
            viewModel.MarkCanceled();
        }
        catch (Exception ex)
        {
            viewModel.MarkFailed(ex);
        }
    }

    private void CancelButton_Click(object? sender, EventArgs e)
    {
        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
        }
    }

    private void CloseButton_Click(object? sender, EventArgs e)
    {
        if (DataContext is EndpointBatchTestDialogViewModel { CanClose: false })
            return;

        Close();
    }

    private void EndpointBatchTestDialog_Closed(object? sender, EventArgs e)
    {
        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
        }

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void ItemHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: EndpointBatchTestItemViewModel itemViewModel })
            return;

        if (DataContext is EndpointBatchTestDialogViewModel dialogViewModel)
        {
            dialogViewModel.ToggleExpanded(itemViewModel);
            e.Handled = true;
        }
    }
}
