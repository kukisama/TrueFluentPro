using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TrueFluentPro.Controls;

/// <summary>不参与命中测试的十五宫格书册底图，不拥有业务内容或生命周期。</summary>
public partial class SongStudyBookFrame : UserControl
{
    public SongStudyBookFrame()
    {
        InitializeComponent();

        foreach (var child in BookSlices.Children)
        {
            if (child is not Image { Tag: string path } image)
                continue;

            var uri = new Uri(path, UriKind.Absolute);
            try
            {
                if (!AssetLoader.Exists(uri))
                    continue;

                using var stream = AssetLoader.Open(uri);
                image.Source = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine($"书册装饰未能加载，使用纸白底色：{path}；{ex.Message}");
            }
        }
    }
}