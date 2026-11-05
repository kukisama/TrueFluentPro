using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace TrueFluentPro.Views;

/// <summary>独立长卷任务树。共享宿主 VM，不查询第二份队列，不接管其计时器和释放。</summary>
public partial class SongScrollTaskQueueMonitorView : TaskQueueMonitorView, ISongScrollPage
{
    public SongScrollShell Shell => this.FindControl<SongScrollShell>("BookShell")!;

    public SongScrollTaskQueueMonitorView() : base(false)
    {
        InitializeComponent();
        // 单元格可能已经处理指针事件，仍需保留原页右键菜单。
        TaskGrid.AddHandler(PointerReleasedEvent, OnTaskGridPointerReleased, RoutingStrategies.Bubble, true);
    }

    private void OnTaskGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        var source = e.Source as Control;
        var row = source as DataGridRow ?? source?.FindAncestorOfType<DataGridRow>();
        if (row != null) TaskRow_PointerReleased(row, e);
    }

    private void SortHeader_Click(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null || sender is not Button { Tag: string column }
            || !vm.SortByColumnCommand.CanExecute(column)) return;

        var selectedId = vm.SelectedTask?.TaskId;
        vm.SortByColumnCommand.Execute(column);
        // VM 排序会创建新的行对象；按业务 ID 恢复选中项和真实执行历史。
        if (selectedId != null)
            vm.SelectedTask = vm.CurrentBucketTasks.FirstOrDefault(t => t.TaskId == selectedId);
        e.Handled = true;
    }
}

public sealed class SongScrollTaskQueueMonitorViewSortCaptionConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = parameter as string ?? "";
        var label = key switch
        {
            "TaskId" => "任务 ID",
            "AudioFileName" => "音频",
            "Stage" => "阶段",
            "SubmittedAt" => "发起时间",
            _ => key
        };
        return values.Count == 2 && values[0] is string current && current == key
            ? $"{label} {(values[1] is true ? "↑" : "↓")}" : $"{label} ↕";
    }
}