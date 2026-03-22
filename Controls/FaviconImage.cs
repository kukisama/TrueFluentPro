using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace TrueFluentPro.Controls;

/// <summary>
/// 网站图标控件：从 icon.horse 异步加载 favicon，失败时显示首字母占位。
/// 照搬 Cherry Studio FallbackFavicon 的逻辑。
/// </summary>
public sealed class FaviconImage : Border
{
    public static readonly StyledProperty<string> HostnameProperty =
        AvaloniaProperty.Register<FaviconImage, string>(nameof(Hostname), "");

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly ConcurrentDictionary<string, Bitmap?> s_cache = new();

    private readonly Image _image;
    private readonly TextBlock _placeholder;

    public FaviconImage()
    {
        Width = 16;
        Height = 16;
        CornerRadius = new CornerRadius(4);
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));

        _image = new Image { Width = 16, Height = 16, IsVisible = false };
        _placeholder = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Child = new Panel { Children = { _placeholder, _image } };
    }

    public string Hostname
    {
        get => GetValue(HostnameProperty);
        set => SetValue(HostnameProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostnameProperty)
        {
            var hostname = change.GetNewValue<string>() ?? "";
            UpdatePlaceholder(hostname);
            _ = LoadFaviconAsync(hostname);
        }
    }

    private void UpdatePlaceholder(string hostname)
    {
        _placeholder.Text = string.IsNullOrEmpty(hostname)
            ? "?"
            : hostname[0].ToString().ToUpperInvariant();
        _placeholder.IsVisible = true;
        _image.IsVisible = false;
    }

    private async Task LoadFaviconAsync(string hostname)
    {
        if (string.IsNullOrEmpty(hostname)) return;

        try
        {
            if (s_cache.TryGetValue(hostname, out var cached))
            {
                if (cached != null)
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (Hostname == hostname) ShowBitmap(cached);
                    });
                return;
            }

            var url = $"https://icon.horse/icon/{Uri.EscapeDataString(hostname)}";
            var data = await s_http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(data);
            var bitmap = new Bitmap(ms);
            s_cache.TryAdd(hostname, bitmap);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Hostname == hostname) ShowBitmap(bitmap);
            });
        }
        catch
        {
            s_cache.TryAdd(hostname, null);
        }
    }

    private void ShowBitmap(Bitmap bitmap)
    {
        _image.Source = bitmap;
        _image.IsVisible = true;
        _placeholder.IsVisible = false;
        Background = Brushes.Transparent;
    }
}
