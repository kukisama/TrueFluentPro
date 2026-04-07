using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IconToolUI.ViewModels;

/// <summary>
/// 表示 UI 中一个可选择的图标项。
/// </summary>
public partial class IconItem : ObservableObject
{
    /// <summary>来源 EXE 文件名</summary>
    [ObservableProperty]
    private string _exeFileName = "";

    /// <summary>图标组 ID</summary>
    [ObservableProperty]
    private int _groupId;

    /// <summary>尺寸描述文本</summary>
    [ObservableProperty]
    private string _sizeDescription = "";

    /// <summary>最大尺寸</summary>
    [ObservableProperty]
    private int _maxSize;

    /// <summary>ICO 文件总大小（字节）</summary>
    [ObservableProperty]
    private long _fileSize;

    /// <summary>是否高清（最大尺寸 >= 256）</summary>
    [ObservableProperty]
    private bool _isHD;

    /// <summary>预览图片</summary>
    [ObservableProperty]
    private Bitmap? _preview;

    /// <summary>完整 ICO 文件字节</summary>
    public byte[] IcoData { get; set; } = [];

    /// <summary>格式化后的文件大小</summary>
    public string FileSizeText => FormatFileSize(FileSize);

    /// <summary>显示标签</summary>
    public string DisplayLabel => $"{ExeFileName} #{GroupId}";

    /// <summary>尺寸标签</summary>
    public string SizeLabel => IsHD ? $"{SizeDescription} ★HD" : SizeDescription;

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }
}
