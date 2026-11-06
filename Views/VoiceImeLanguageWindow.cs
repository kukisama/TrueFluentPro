using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Views;

/// <summary>浮条“言”入口的独立语言设置窗口；保存时只 patch 最新共享配置的三个字段。</summary>
public sealed class VoiceImeLanguageWindow : Window
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly VoiceImeLanguageViewModel _vm = new();
    private readonly StackPanel _fields;
    private readonly TextBlock _status;
    private readonly Button _save, _reload;
    private readonly string _path;
    private bool _saving;

    public VoiceImeLanguageWindow() : this(new ConfigurationService().GetConfigFilePath()) { }

    internal VoiceImeLanguageWindow(string path)
    {
        _path = path;
        Title = "输入法语言";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        MinWidth = 400;
        MinHeight = 440;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DataContext = _vm;

        var source = new ComboBox { Name = "RecognitionLanguage", HorizontalAlignment = HorizontalAlignment.Stretch };
        source.Bind(ComboBox.ItemsSourceProperty, new Binding(nameof(_vm.SourceLanguages)));
        source.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(_vm.SelectedSource)) { Mode = BindingMode.TwoWay });
        var translate = new ToggleSwitch { Name = "EnableTranslation", OnContent = "开", OffContent = "关" };
        translate.Bind(ToggleSwitch.IsCheckedProperty, new Binding(nameof(_vm.EnableTranslation)) { Mode = BindingMode.TwoWay });
        var target = new ComboBox { Name = "TargetLanguage", HorizontalAlignment = HorizontalAlignment.Stretch };
        target.Bind(ComboBox.ItemsSourceProperty, new Binding(nameof(_vm.TargetLanguages)));
        target.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(_vm.SelectedTarget)) { Mode = BindingMode.TwoWay });
        target.Bind(IsEnabledProperty, new Binding(nameof(_vm.EnableTranslation)));
        var toggleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        toggleRow.Children.Add(new TextBlock { Text = "翻译后输入", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
        Grid.SetColumn(translate, 1);
        toggleRow.Children.Add(translate);
        _fields = new StackPanel
        {
            Spacing = 10, IsEnabled = false,
            Children =
            {
                new TextBlock { Text = "识别语言", FontWeight = FontWeight.SemiBold }, source,
                toggleRow,
                new TextBlock { Text = "目标语言", FontWeight = FontWeight.SemiBold }, target,
                new TextBlock
                {
                    Text = "关闭时直接输入原文；开启后逐句翻译，只输入译文。\n复用主程序文本模型与登录，可能产生文本模型用量。",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72
                }
            }
        };
        _status = new TextBlock { Name = "SaveStatus", Text = "正在读取共享配置…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        _save = new Button { Name = "SaveLanguage", Content = "保存设置", IsEnabled = false, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        _save.Classes.Add("accent");
        _reload = new Button { Content = "重新加载", MinWidth = 88 };
        _save.Click += async (_, _) => await SaveAsync();
        _reload.Click += async (_, _) => await LoadAsync();
        _vm.Changed += () => { _status.Text = "有未保存的更改 · 点击“保存设置”后生效"; _save.IsEnabled = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(_reload);
        buttons.Children.Add(_save);
        Content = new StackPanel
        {
            Margin = new Thickness(24, 18), Spacing = 16,
            Children =
            {
                new TextBlock { Text = "语言与翻译", FontSize = 23, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "仅用于语音输入法 · 保存后下次录音生效", FontSize = 12, Opacity = 0.7 },
                new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)), Child = _fields },
                _status, buttons
            }
        };
        Opened += async (_, _) => await LoadAsync();
        Closing += (_, args) => { if (_saving) args.Cancel = true; };
    }

    private async Task LoadAsync()
    {
        if (_saving) return;
        _fields.IsEnabled = _reload.IsEnabled = _save.IsEnabled = false;
        try
        {
            var root = await ReadLatestAsync(_path, CancellationToken.None);
            var settings = root["VoiceIme"]?.Deserialize<VoiceImeConfig>(JsonOptions) ?? new VoiceImeConfig();
            _vm.LoadFrom(settings);
            _fields.IsEnabled = true;
            _status.Text = "已加载已保存设置 · 修改仅影响下次录音";
        }
        catch { _status.Text = "无法读取共享配置，请先在主程序完成配置后重新加载；未修改任何设置。"; }
        finally { _reload.IsEnabled = true; }
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        _fields.IsEnabled = _reload.IsEnabled = _save.IsEnabled = false;
        _status.Text = "正在保存…";
        try
        {
            await SaveSettingsAsync(_path, _vm.CreateDraft());
            _status.Text = $"已保存 · {DateTime.Now:HH:mm:ss} · 下次录音生效";
        }
        catch { _status.Text = "保存未完成：配置可能正在被其他窗口修改或无法写入。请重试保存；当前选择已保留。"; _save.IsEnabled = true; }
        finally { _saving = false; _fields.IsEnabled = _reload.IsEnabled = true; }
    }

    // 仅此入口写三个语言字段；原文、凭据和其它配置无需反序列化/迁移，未知字段亦保留。
    internal static async Task SaveSettingsAsync(string path, VoiceImeConfig draft, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(draft.RecognitionLanguage) || string.IsNullOrWhiteSpace(draft.TargetLanguage)
            || string.Equals(draft.TargetLanguage, "auto", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择有效的识别语言与翻译目标语言。");
        await SaveGate.WaitAsync(token).ConfigureAwait(false);
        var temp = path + ".voice-ime-language-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var original = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            var root = Parse(original);
            var settings = root["VoiceIme"] as JsonObject;
            if (root["VoiceIme"] != null && settings == null) throw new InvalidDataException();
            if (settings == null) root["VoiceIme"] = settings = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            settings[nameof(VoiceImeConfig.RecognitionLanguage)] = draft.RecognitionLanguage;
            settings[nameof(VoiceImeConfig.EnableTranslation)] = draft.EnableTranslation;
            settings[nameof(VoiceImeConfig.TargetLanguage)] = draft.TargetLanguage;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, root, JsonOptions, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            // 读写间发生更新时拒绝提交，不拿打开窗口时的长期旧快照覆盖共享配置。
            var latest = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (!original.SequenceEqual(latest)) throw new IOException("共享配置已更新，请重试保存。");
            token.ThrowIfCancellationRequested();
            File.Copy(path, path + ".bak", overwrite: true);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            SaveGate.Release();
        }
    }

    private static async Task<JsonObject> ReadLatestAsync(string path, CancellationToken token)
        => Parse(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));

    private static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException();
        return JsonNode.Parse(bytes, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
            ?? throw new InvalidDataException();
    }
}