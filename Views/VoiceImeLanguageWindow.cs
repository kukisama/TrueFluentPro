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

/// <summary>语言设置页；只 patch 最新共享配置的三个字段，统一工具窗可启用自动保存。</summary>
internal sealed class VoiceImeLanguagePage : VoiceImeToolPage
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
    private readonly bool _autoSave;
    private Task _pendingSave = Task.CompletedTask;
    private CancellationTokenSource? _saveDebounceCts;
    private bool _dirty;
    private bool _saveFailed;
    private int _editVersion;
    private bool _saving;
    private bool _closed;
    private bool _started;

    internal event Action<VoiceImeConfig>? Saved;

    internal VoiceImeLanguagePage() : this(new ConfigurationService().GetConfigFilePath()) { }

    internal VoiceImeLanguagePage(string path, bool autoSave = false)
    {
        _path = path;
        _autoSave = autoSave;
        DataContext = _vm;

        var source = new ComboBox { Name = "RecognitionLanguage", HorizontalAlignment = HorizontalAlignment.Stretch };
        source.Bind(ComboBox.ItemsSourceProperty, new Binding(nameof(_vm.SourceLanguages)) { Mode = BindingMode.OneWay });
        source.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(_vm.SelectedSource)) { Mode = BindingMode.TwoWay });
        var translate = new ToggleSwitch
        {
            Name = "EnableTranslation", OnContent = "", OffContent = "",
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(translate, "翻译后输入：开启时逐句翻译，只输入译文；关闭时直接输入识别原文。");
        translate.Bind(ToggleSwitch.IsCheckedProperty, new Binding(nameof(_vm.EnableTranslation)) { Mode = BindingMode.TwoWay });
        var target = new ComboBox { Name = "TargetLanguage", HorizontalAlignment = HorizontalAlignment.Stretch };
        target.Bind(ComboBox.ItemsSourceProperty, new Binding(nameof(_vm.TargetLanguages)) { Mode = BindingMode.OneWay });
        target.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(_vm.SelectedTarget)) { Mode = BindingMode.TwoWay });
        target.Bind(IsEnabledProperty, new Binding(nameof(_vm.EnableTranslation)) { Mode = BindingMode.OneWay });
        var sourceTitle = VoiceImeWindowDesign.Text("识别语言", 18);
        sourceTitle.FontWeight = FontWeight.SemiBold;
        var translationTitle = VoiceImeWindowDesign.Text("翻译后输入", 18);
        translationTitle.FontWeight = FontWeight.SemiBold;
        var toggleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        toggleRow.Children.Add(new StackPanel
        {
            Spacing = 5,
            Children = { translationTitle, VoiceImeWindowDesign.Text("逐句翻译，只输入译文", 13, VoiceImeWindowDesign.Muted) }
        });
        Grid.SetColumn(translate, 1);
        toggleRow.Children.Add(translate);
        _fields = new StackPanel
        {
            Margin = new Thickness(24, 20), Spacing = 16, IsEnabled = false,
            MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                VoiceImeWindowDesign.Card(new StackPanel
                {
                    Spacing = 10,
                    Children = { sourceTitle, VoiceImeWindowDesign.Text("你将使用什么语言说话", 13, VoiceImeWindowDesign.Muted), source }
                }),
                VoiceImeWindowDesign.Card(new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        toggleRow,
                        new Border { Height = 1, Background = VoiceImeWindowDesign.Line, Margin = new Thickness(0, 2) },
                        VoiceImeWindowDesign.Text("目标语言"), target,
                        new Border
                        {
                            Background = VoiceImeWindowDesign.Page, CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                            Child = VoiceImeWindowDesign.Text("关闭翻译时，直接输入识别原文。", 13, VoiceImeWindowDesign.Blue)
                        }
                    }
                }),
                VoiceImeWindowDesign.Text("复用主程序文本模型与登录\n翻译可能产生文本模型用量", 13, VoiceImeWindowDesign.Muted)
            }
        };
        _status = VoiceImeWindowDesign.Text("正在读取共享配置…", 12, VoiceImeWindowDesign.Muted);
        _status.Name = "SaveStatus";
        _status.VerticalAlignment = VerticalAlignment.Center;
        _save = new Button
        {
            Name = "SaveLanguage", Content = _autoSave ? "重试保存" : "保存设置",
            IsEnabled = false, IsVisible = !_autoSave, MinWidth = _autoSave ? 88 : 100,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _save.Classes.Add("accent");
        _reload = new Button { Content = "重新加载", MinWidth = 88 };
        _save.Click += async (_, _) => await SaveAsync();
        _reload.Click += async (_, _) => await LoadAsync();
        _vm.Changed += OnVmChanged;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(_reload);
        buttons.Children.Add(_save);
        var footerBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        footerBar.Children.Add(_status);
        Grid.SetColumn(buttons, 1);
        footerBar.Children.Add(buttons);
        var scroll = new ScrollViewer
        {
            Content = _fields,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        scroll.PropertyChanged += (_, change) =>
        {
            // Center 不会自动拉伸；显式宽度须扣除外边距，兼容小窗仍填满可用正文宽度。
            if (change.Property == ScrollViewer.ViewportProperty)
                _fields.Width = Math.Min(_fields.MaxWidth, Math.Max(0, scroll.Viewport.Width - _fields.Margin.Left - _fields.Margin.Right));
        };
        var footer = VoiceImeWindowDesign.Footer(footerBar);
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        Grid.SetRow(footer, 1);
        root.Children.Add(scroll);
        root.Children.Add(footer);
        Content = root;
    }

    internal override bool CanClose => !_saving;

    internal override async Task PrepareToLeaveAsync()
    {
        if (_autoSave && !_saving) CancelSaveDebounce();
        await _pendingSave;
        if (_closed || !_autoSave || !_dirty) return;
        await SaveAsync(); // 不等防抖，离页立即 flush；失败仍保留草稿并阻止切页/关闭。
        if (_dirty) throw new InvalidOperationException("语言设置尚未保存，请重试");
    }

    internal override async Task InitializeAsync()
    {
        if (_closed || _started) return;
        _started = true;
        await LoadAsync();
    }

    public override void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _vm.Changed -= OnVmChanged;
        // 统一 Shell 须先 await PrepareToLeaveAsync；这里只取消尚未开始写入的防抖。
        CancelSaveDebounce();
    }

    private void OnVmChanged()
    {
        if (_closed) return;
        _dirty = true;
        _saveFailed = false;
        _editVersion++;
        _status.Text = _autoSave ? "有修改 · 修改后自动保存，下次录音生效" : "有未保存的更改 · 点击“保存设置”后生效";
        _save.IsEnabled = !_saving;
        if (!_autoSave) return;
        _save.IsVisible = false;
        CancelSaveDebounce();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        var pendingSave = _pendingSave;
        _pendingSave = SaveDebouncedAsync(cts.Token, pendingSave);
    }

    private async Task SaveDebouncedAsync(CancellationToken token, Task pendingSave)
    {
        try
        {
            await Task.Delay(200, token);
            await pendingSave;
            token.ThrowIfCancellationRequested();
            if (!_closed && _dirty) await SaveAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 取消防抖不丢掉更早的在途保存；后续离页仍能等待完整任务链。
            await pendingSave;
        }
    }

    private void CancelSaveDebounce()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _saveDebounceCts = null;
    }

    private async Task LoadAsync()
    {
        if (_closed || _saving) return;
        CancelSaveDebounce(); // 显式重新加载仍丢弃未保存选择，不偷偷先写入。
        _fields.IsEnabled = _reload.IsEnabled = _save.IsEnabled = false;
        try
        {
            var root = await ReadLatestAsync(_path, CancellationToken.None);
            if (_closed) return;
            var settings = root["VoiceIme"]?.Deserialize<VoiceImeConfig>(JsonOptions) ?? new VoiceImeConfig();
            _vm.LoadFrom(settings);
            _dirty = _saveFailed = false;
            if (_autoSave) _save.IsVisible = false;
            _fields.IsEnabled = true;
            _status.Text = _autoSave ? "已加载已保存设置 · 修改后自动保存，下次录音生效" : "已加载已保存设置 · 修改仅影响下次录音";
        }
        catch { if (!_closed) _status.Text = "无法读取共享配置，请先在主程序完成配置后重新加载；未修改任何设置。"; }
        finally
        {
            if (!_closed)
            {
                _reload.IsEnabled = true;
                if (_autoSave) _save.IsEnabled = _save.IsVisible = _dirty && _saveFailed;
            }
        }
    }

    private Task SaveAsync()
    {
        if (_closed) return Task.CompletedTask;
        if (_saving) return _pendingSave;
        CancelSaveDebounce();
        return _pendingSave = SaveCoreAsync();
    }

    private async Task SaveCoreAsync()
    {
        var draft = _vm.CreateDraft(); // 开始保存即冻结三个字段，不能在 await 后重取可变 VM。
        var editVersion = _editVersion;
        VoiceImeConfig? saved = null;
        _saving = true;
        _saveFailed = false;
        _fields.IsEnabled = _reload.IsEnabled = _save.IsEnabled = false;
        if (_autoSave) _save.IsVisible = false;
        _status.Text = "正在保存…";
        try
        {
            await SaveSettingsAsync(_path, draft);
            if (_editVersion == editVersion) _dirty = false;
            saved = draft;
            if (!_closed)
                _status.Text = _dirty ? "有修改 · 当前更改尚未保存" : $"已保存 · {DateTime.Now:HH:mm:ss} · 下次录音生效";
        }
        catch
        {
            _dirty = _saveFailed = true;
            if (!_closed)
            {
                _status.Text = "保存未完成：配置可能正在被其他窗口修改或无法写入。请重试保存；当前选择已保留。";
                _save.IsEnabled = true;
            }
        }
        finally
        {
            _saving = false;
            if (!_closed)
            {
                _fields.IsEnabled = _reload.IsEnabled = true;
                _save.IsEnabled = _dirty;
                if (_autoSave) _save.IsVisible = _dirty && _saveFailed;
            }
        }
        // 通知携带实际写入的冻结快照；通知失败不伪装成磁盘保存失败，不触发 VM 编辑。
        if (!_closed && saved != null) Saved?.Invoke(saved);
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

/// <summary>保留浮条“言”入口、原构造签名与三字段保存 API。</summary>
public sealed class VoiceImeLanguageWindow : Window
{
    private readonly VoiceImeLanguagePage _page;

    public VoiceImeLanguageWindow() : this(new ConfigurationService().GetConfigFilePath()) { }

    internal VoiceImeLanguageWindow(string path)
    {
        _page = new VoiceImeLanguagePage(path);
        Title = "输入法语言";
        Width = 520;
        Height = 720;
        MinWidth = 480;
        MinHeight = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        VoiceImeWindowDesign.Apply(this);
        Content = VoiceImeToolPage.Wrap(this, _page, "语言与翻译", "保存后，下次录音生效。");
        Opened += async (_, _) =>
        {
            try
            {
                await VoiceImeToolPage.RenderedAsync(this);
                await _page.InitializeAsync();
            }
            catch (InvalidOperationException) when (!IsVisible) { }
        };
        Closing += (_, args) => { if (!_page.CanClose) args.Cancel = true; };
        Closed += (_, _) => _page.Dispose();
    }

    internal static Task SaveSettingsAsync(string path, VoiceImeConfig draft, CancellationToken token = default)
        => VoiceImeLanguagePage.SaveSettingsAsync(path, draft, token);
}