using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{
    public partial class TaskQueueMonitorView : UserControl
    {
        public TaskQueueMonitorView()
        {
            InitializeComponent();
        }

        private TaskQueueMonitorViewModel? ViewModel => DataContext as TaskQueueMonitorViewModel;

        internal void TaskRow_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right
                || sender is not Control control
                || control.DataContext is not TaskQueueItemViewModel task)
            {
                return;
            }

            var vm = ViewModel;
            if (vm == null) return;

            vm.SelectedTask = task;
            var flyout = BuildTaskFlyout(task, vm);
            if (flyout.Items.Count > 0)
                flyout.ShowAt(control, true);
            e.Handled = true;
        }

        private static MenuFlyout BuildTaskFlyout(TaskQueueItemViewModel task, TaskQueueMonitorViewModel vm)
        {
            var flyout = new MenuFlyout();

            if (task.Status == AudioTaskStatus.Failed || task.Status == AudioTaskStatus.Cancelled)
            {
                var retryItem = new MenuItem { Header = "重试" };
                retryItem.Click += (_, _) => vm.RetryTask(task.TaskId);
                flyout.Items.Add(retryItem);
            }

            if (task.Status == AudioTaskStatus.Pending || task.Status == AudioTaskStatus.Running)
            {
                var cancelItem = new MenuItem { Header = "取消" };
                cancelItem.Click += (_, _) => vm.CancelTask(task.TaskId);
                flyout.Items.Add(cancelItem);
            }

            return flyout;
        }

        internal async void ShowDebugText_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string text || string.IsNullOrEmpty(text))
                return;

            var window = TopLevel.GetTopLevel(this) as Window;
            if (window == null) return;

            var dialog = new Window
            {
                Title = "调试文本",
                Width = 700,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    Content = new TextBox
                    {
                        Text = text,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontFamily = new Avalonia.Media.FontFamily("Cascadia Code, Consolas, monospace"),
                        FontSize = 12,
                        Margin = new Avalonia.Thickness(8),
                    }
                }
            };
            await dialog.ShowDialog(window);
        }
    }
}
