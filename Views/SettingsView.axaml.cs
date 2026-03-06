using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views;

public partial class SettingsView : UserControl
{
    private bool _suppressScrollSync;
    private Control[]? _sectionControls;
    private static readonly JsonSerializerOptions TransferJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // 缓存所有 Section UserControl
        _sectionControls = SectionsPanel.Children
            .OfType<UserControl>()
            .Where(c => c.Name?.StartsWith("Section_") == true)
            .ToArray<Control>();

        // 默认选中第一项
        if (NavListBox.SelectedIndex < 0 && NavListBox.ItemCount > 0)
            NavListBox.SelectedIndex = 0;
    }

    /// <summary>点击左侧导航 → 滚动到对应分区</summary>
    private void OnNavSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressScrollSync) return;
        if (e.AddedItems.Count == 0) return;

        ResetTransientSectionStates();

        string? tag = null;
        if (e.AddedItems[0] is ListBoxItem lbi)
            tag = lbi.Tag as string;
        else if (e.AddedItems[0] is { } item && NavListBox.ContainerFromItem(item) is ListBoxItem container)
            tag = container.Tag as string;

        if (string.IsNullOrEmpty(tag)) return;

        var target = _sectionControls?.FirstOrDefault(c => c.Name == tag);
        if (target == null) return;

        var transform = target.TransformToVisual(SettingsScroller);
        if (transform != null)
        {
            var pos = transform.Value.Transform(new Point(0, 0));
            var newOffset = SettingsScroller.Offset.Y + pos.Y;
            _suppressScrollSync = true;
            SettingsScroller.Offset = new Avalonia.Vector(SettingsScroller.Offset.X, Math.Max(0, newOffset));
            _suppressScrollSync = false;
        }
    }

    /// <summary>右侧滚动 → 更新左侧导航高亮</summary>
    private void OnSettingsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_sectionControls == null || _sectionControls.Length == 0) return;

        Control? topVisible = null;
        double bestY = double.NegativeInfinity;

        foreach (var section in _sectionControls)
        {
            var transform = section.TransformToVisual(SettingsScroller);
            if (transform == null) continue;

            var pos = transform.Value.Transform(new Point(0, 0));
            if (pos.Y <= 40 && pos.Y > bestY)
            {
                topVisible = section;
                bestY = pos.Y;
            }
        }

        if (topVisible == null)
        {
            foreach (var section in _sectionControls)
            {
                var transform = section.TransformToVisual(SettingsScroller);
                if (transform == null) continue;
                var pos = transform.Value.Transform(new Point(0, 0));
                if (pos.Y >= 0)
                {
                    topVisible = section;
                    break;
                }
            }
        }

        if (topVisible == null) return;

        var targetIndex = -1;
        for (int i = 0; i < NavListBox.ItemCount; i++)
        {
            if (NavListBox.ContainerFromIndex(i) is ListBoxItem li && li.Tag as string == topVisible.Name)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex >= 0 && NavListBox.SelectedIndex != targetIndex)
        {
            ResetTransientSectionStates();
            _suppressScrollSync = true;
            NavListBox.SelectedIndex = targetIndex;
            _suppressScrollSync = false;
        }
    }

    private void ResetTransientSectionStates()
    {
        Section_Subscription?.ResetTransientUiState();
        Section_Endpoints?.ResetTransientUiState();
    }

    private async void OnExportSettingsPackageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var provider = topLevel?.StorageProvider;
        if (provider == null)
        {
            vm.StatusMessage = "导出失败：无法获取文件保存能力";
            return;
        }

        try
        {
            var package = vm.Settings.CreateExportPackage();
            var targetFile = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出资源配置",
                SuggestedFileName = $"truefluentpro-resource-config-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultExtension = "json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JSON 文件")
                    {
                        Patterns = new[] { "*.json" }
                    }
                }
            });

            if (targetFile == null)
            {
                return;
            }

            await using var stream = await targetFile.OpenWriteAsync();
            await JsonSerializer.SerializeAsync(stream, package, TransferJsonOptions);

            var filePath = targetFile.TryGetLocalPath() ?? targetFile.Name ?? "已选文件";
            vm.StatusMessage = $"资源配置已导出：{filePath}";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"导出失败: {ex.Message}";
        }
    }

    private async void OnImportSettingsPackageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var provider = topLevel?.StorageProvider;
        if (provider == null)
        {
            vm.StatusMessage = "导入失败：无法获取文件选择能力";
            return;
        }

        try
        {
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入资源配置",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON 文件")
                    {
                        Patterns = new[] { "*.json" }
                    }
                }
            });

            if (files == null || files.Count == 0)
            {
                return;
            }

            var selectedFile = files[0];
            SettingsTransferPackage? package;
            var localPath = selectedFile.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                var json = await File.ReadAllTextAsync(localPath);
                package = JsonSerializer.Deserialize<SettingsTransferPackage>(json, TransferJsonOptions);
            }
            else
            {
                await using var stream = await selectedFile.OpenReadAsync();
                package = await JsonSerializer.DeserializeAsync<SettingsTransferPackage>(stream, TransferJsonOptions);
            }

            if (package == null)
            {
                throw new InvalidOperationException("文件内容为空或格式不正确。");
            }

            await vm.Settings.ImportPackageAsync(package);

            var displayName = selectedFile.TryGetLocalPath() ?? selectedFile.Name ?? "所选文件";
            vm.StatusMessage = $"资源配置已导入并立即生效：{displayName}";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"导入失败: {ex.Message}";
        }
    }
}
