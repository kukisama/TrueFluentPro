using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IconTool.Core;

namespace IconToolUI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _directoryPath = "";

    [ObservableProperty]
    private string _statusMessage = "就绪 — 选择一个目录以浏览其中 EXE 文件的图标";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private IconItem? _selectedIcon;

    partial void OnSelectedIconChanged(IconItem? value)
    {
        if (value is not null)
        {
            StatusMessage = $"已选中: {value.DisplayLabel} ({value.SizeLabel})";
        }
    }

    [ObservableProperty]
    private bool _hasIcons;

    public ObservableCollection<IconItem> Icons { get; } = [];

    /// <summary>
    /// 浏览并加载指定目录中的 EXE 图标。
    /// </summary>
    [RelayCommand]
    private async Task BrowseDirectory(Avalonia.Visual? visual)
    {
        if (visual is null) return;

        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(visual);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择要浏览的目录",
                AllowMultiple = false
            });

        if (folders.Count == 0) return;

        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        DirectoryPath = path;
        await LoadIconsFromDirectory(path);
    }

    /// <summary>
    /// 手动输入路径后加载。
    /// </summary>
    [RelayCommand]
    private async Task LoadPath()
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath)) return;

        var path = Path.GetFullPath(DirectoryPath);
        if (!Directory.Exists(path))
        {
            StatusMessage = $"❌ 目录不存在: {path}";
            return;
        }

        DirectoryPath = path;
        await LoadIconsFromDirectory(path);
    }

    /// <summary>
    /// 将选中的图标设为目录图标。
    /// </summary>
    [RelayCommand]
    private void SetAsDirectoryIcon()
    {
        if (SelectedIcon is null || string.IsNullOrWhiteSpace(DirectoryPath))
        {
            StatusMessage = "⚠ 请先选择一个图标";
            return;
        }

        try
        {
            var icoFileName = $"{Path.GetFileNameWithoutExtension(SelectedIcon.ExeFileName)}_icon{SelectedIcon.GroupId}.ico";

            DirectoryIconService.SetDirectoryIconFromBytes(
                SelectedIcon.IcoData,
                icoFileName,
                DirectoryPath);

            StatusMessage = $"✅ 已将 {SelectedIcon.DisplayLabel} 设为目录图标 → {icoFileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 设置失败: {ex.Message}";
        }
    }

    private async Task LoadIconsFromDirectory(string dirPath)
    {
        IsLoading = true;
        Icons.Clear();
        SelectedIcon = null;
        HasIcons = false;
        StatusMessage = "正在扫描 EXE 文件...";

        try
        {
            var exeIcons = await Task.Run(() => PeIconExtractor.ScanDirectory(dirPath));

            if (exeIcons.Count == 0)
            {
                StatusMessage = "未找到包含图标的 EXE 文件";
                return;
            }

            int totalIcons = 0;
            foreach (var exe in exeIcons)
            {
                foreach (var group in exe.IconGroups)
                {
                    var sizeDesc = IcoHelper.DescribeSizes(group.IcoData);
                    var maxSize = IcoHelper.GetMaxSize(group.IcoData);

                    var item = new IconItem
                    {
                        ExeFileName = exe.ExeFileName,
                        GroupId = group.GroupId,
                        SizeDescription = sizeDesc,
                        MaxSize = maxSize,
                        FileSize = group.IcoData.Length,
                        IsHD = maxSize >= 256,
                        IcoData = group.IcoData,
                        Preview = CreatePreviewBitmap(group.IcoData)
                    };

                    Icons.Add(item);
                    totalIcons++;
                }
            }

            HasIcons = Icons.Count > 0;
            StatusMessage = $"找到 {totalIcons} 个图标（来自 {exeIcons.Count} 个 EXE 文件）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 扫描失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 从 ICO 数据创建预览 Bitmap。
    /// 优先提取最大尺寸的 PNG 数据；如果不是 PNG 格式，则直接从 ICO 流加载。
    /// </summary>
    private static Bitmap? CreatePreviewBitmap(byte[] icoData)
    {
        try
        {
            // 尝试提取最大尺寸条目的原始数据
            var imageData = IcoHelper.ExtractLargestImageData(icoData);
            if (imageData is not null && IcoHelper.IsPng(imageData))
            {
                // 条目是 PNG 格式，可以直接加载
                using var ms = new MemoryStream(imageData);
                return new Bitmap(ms);
            }

            // 回退：整个 ICO 交给 Avalonia 解码（支持 BMP-in-ICO）
            using var icoStream = new MemoryStream(icoData);
            return new Bitmap(icoStream);
        }
        catch
        {
            return null;
        }
    }
}
