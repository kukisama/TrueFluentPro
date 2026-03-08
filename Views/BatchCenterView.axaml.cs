using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class BatchCenterView : UserControl
{
    public BatchCenterView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void PackageRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BatchPackageItem package)
        {
            return;
        }

        var vm = ViewModel?.BatchProcessing;
        if (vm == null)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
        {
            vm.SelectedPackage = package;
        }
    }

    private void PackageRow_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BatchPackageItem package)
        {
            return;
        }

        var vm = ViewModel?.BatchProcessing;
        if (vm == null)
        {
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            ExecuteCommand(vm.TogglePackageExpandedCommand, package);
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        var flyout = BuildPackageFlyout(package, vm);
        flyout.ShowAt(control, true);
        e.Handled = true;
    }

    private void SubtaskRow_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        if (sender is not Control control || control.DataContext is not BatchSubtaskItem subtask)
        {
            return;
        }

        var vm = ViewModel?.BatchProcessing;
        if (vm == null)
        {
            return;
        }

        var flyout = new MenuFlyout();
        var regenerateItem = new MenuItem
        {
            Header = "重新生成该任务",
            IsEnabled = subtask.CanRegenerate
        };
        regenerateItem.Click += (_, _) => ExecuteCommand(vm.RegenerateSubtaskCommand, subtask);
        flyout.Items.Add(regenerateItem);
        flyout.ShowAt(control, true);
        e.Handled = true;
    }

    private static MenuFlyout BuildPackageFlyout(BatchPackageItem package, BatchProcessingViewModel vm)
    {
        var flyout = new MenuFlyout();

        if (package.CanEnqueue)
        {
            var startItem = new MenuItem { Header = "开始" };
            startItem.Click += (_, _) => ExecuteCommand(vm.StartPackageCommand, package);
            flyout.Items.Add(startItem);
        }

        if (!package.CanEnqueue && !package.IsRemoved)
        {
            var regenerateItem = new MenuItem { Header = "重新生成整个文件包" };
            regenerateItem.Click += (_, _) => ExecuteCommand(vm.RegeneratePackageCommand, package);
            flyout.Items.Add(regenerateItem);
        }

        if (package.CanPause)
        {
            var pauseItem = new MenuItem { Header = "暂停" };
            pauseItem.Click += (_, _) => ExecuteCommand(vm.PausePackageCommand, package);
            flyout.Items.Add(pauseItem);
        }

        if (package.CanResume)
        {
            var resumeItem = new MenuItem { Header = "继续" };
            resumeItem.Click += (_, _) => ExecuteCommand(vm.ResumePackageCommand, package);
            flyout.Items.Add(resumeItem);
        }

        if (package.CanDelete || package.IsRemoved)
        {
            flyout.Items.Add(new Separator());
        }

        if (package.CanDelete)
        {
            var deleteItem = new MenuItem { Header = "删除" };
            deleteItem.Click += (_, _) => ExecuteCommand(vm.RemovePackageCommand, package);
            flyout.Items.Add(deleteItem);
        }

        if (package.IsRemoved)
        {
            var restoreItem = new MenuItem { Header = "恢复" };
            restoreItem.Click += (_, _) => ExecuteCommand(vm.RestorePackageCommand, package);
            flyout.Items.Add(restoreItem);
        }

        return flyout;
    }

    private static void ExecuteCommand(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }
    }
}
