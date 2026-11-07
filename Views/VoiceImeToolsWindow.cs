using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TrueFluentPro.Services;

namespace TrueFluentPro.Views;

/// <summary>唯一输入法工具外壳；三个入口选择页签，缓存页面不重复加载业务。</summary>
public sealed class VoiceImeToolsWindow : Window
{
    private readonly Func<VoiceImeToolTab, VoiceImeToolPage> _factory;
    private readonly Dictionary<VoiceImeToolTab, VoiceImeToolPage> _pages = new();
    private readonly Dictionary<VoiceImeToolTab, Button> _tabs = new();
    private readonly Grid _body = new() { Name = "VoiceImeSheetHost" };
    private readonly TextBlock _pageTitle = VoiceImeWindowDesign.Text("语言与翻译", 26);
    private readonly TextBlock _notice = VoiceImeWindowDesign.Text("", 12, VoiceImeWindowDesign.Muted);
    private readonly SemaphoreSlim _navigation = new(1, 1);
    private readonly CancellationTokenSource _closedCts = new();
    private VoiceImeToolTab _requested;
    private VoiceImeToolTab? _selected;
    private long _request;
    private bool _shown, _closed, _closing, _allowClose;
    private static WeakReference<VoiceImeToolsWindow>? _current;
    internal static Window? Current => _current != null && _current.TryGetTarget(out var window) && window.IsVisible ? window : null;
    internal VoiceImeToolTab? SelectedTab => _selected;
    internal Task LastNavigation { get; private set; } = Task.CompletedTask;

    public VoiceImeToolsWindow() : this(VoiceImeToolTab.Settings) { }

    internal VoiceImeToolsWindow(VoiceImeToolTab initial, Func<VoiceImeToolTab, VoiceImeToolPage>? factory = null)
    {
        _factory = factory ?? CreatePage;
        _requested = initial;
        Title = "译见pro · 输入法工具";
        Width = 1040;
        Height = 820;
        MinWidth = 760;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        VoiceImeWindowDesign.Apply(this);
        var nav = new StackPanel { Name = "VoiceImeCategoryNavigation", Spacing = 8, Margin = new Thickness(12, 20) };
        var entries = new[]
        {
            (VoiceImeToolTab.Usage, "用量", "\uE9D2"), (VoiceImeToolTab.Language, "语言与翻译", "\uE775"), (VoiceImeToolTab.Settings, "设置", "\uE713")
        };
        for (var index = 0; index < entries.Length; index++)
        {
            var (kind, label, glyph) = entries[index];
            var button = VoiceImeWindowDesign.SheetButton("VoiceImeTab" + kind, label, glyph);
            button.Height = 64;
            button.Padding = new Thickness(12, 0);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Click += (_, _) => Navigate(kind);
            AutomationProperties.SetName(button, label);
            nav.Children.Add(button);
            _tabs.Add(kind, button);
        }
        _notice.Name = "VoiceImeSheetStatus";
        _notice.IsVisible = false;
        _notice.Margin = new Thickness(24, 12, 24, 0);
        _pageTitle.FontWeight = FontWeight.SemiBold;
        _pageTitle.Margin = new Thickness(24, 20, 24, 0);
        _pageTitle.IsVisible = false;
        var navigation = new Border
        {
            Name = "VoiceImeSidebar", Width = 176, Background = VoiceImeWindowDesign.Page,
            BorderBrush = VoiceImeWindowDesign.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = nav
        };
        var work = new Grid { Name = "VoiceImeWorkspace", RowDefinitions = new("Auto,Auto,*") };
        work.Children.Add(_notice);
        Grid.SetRow(_pageTitle, 1);
        work.Children.Add(_pageTitle);
        Grid.SetRow(_body, 2);
        work.Children.Add(_body);
        var layout = new Grid { Name = "VoiceImeToolColumns", ColumnDefinitions = new("176,*") };
        layout.Children.Add(navigation);
        Grid.SetColumn(work, 1);
        layout.Children.Add(work);
        var root = new Grid { RowDefinitions = new("Auto,*") };
        root.Children.Add(VoiceImeWindowDesign.Header(this, "输入法工具", "F8 开始 / 停止听写", compact: true));
        Grid.SetRow(layout, 1);
        root.Children.Add(layout);
        Content = root;
        Opened += (_, _) =>
        {
            _shown = true;
            _current = new WeakReference<VoiceImeToolsWindow>(this);
            Navigate(_requested);
        };
        Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (!_closing) _ = CloseAfterSaveAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _closedCts.Cancel();
            foreach (var page in _pages.Values) page.Dispose();
            if (_current != null && _current.TryGetTarget(out var window) && ReferenceEquals(window, this)) _current = null;
        };
    }

    private static VoiceImeToolPage CreatePage(VoiceImeToolTab kind) => kind switch
    {
        VoiceImeToolTab.Usage => new VoiceImeUsagePage(unified: true),
        VoiceImeToolTab.Language => new VoiceImeLanguagePage(new ConfigurationService().GetConfigFilePath(), autoSave: true),
        VoiceImeToolTab.Settings => new VoiceImeSettingsPage(unified: true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal void Navigate(VoiceImeToolTab kind)
    {
        if (!_tabs.ContainsKey(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        _requested = kind;
        if (_closed || !_shown || _closing) return;
        LastNavigation = SelectPageAsync(kind, ++_request);
    }

    private async Task SelectPageAsync(VoiceImeToolTab kind, long request)
    {
        await _navigation.WaitAsync();
        try
        {
            if (_closed || _closing || request != _request || _selected == kind) return;
            if (_selected is { } previous) await _pages[previous].PrepareToLeaveAsync();
            if (_closed || _closing || request != _request) return;
            if (!_pages.TryGetValue(kind, out var page))
            {
                page = _factory(kind);
                page.CloseRequested += Close;
                page.NavigationRequested += Navigate;
                if (page is VoiceImeLanguagePage language) language.Saved += saved =>
                {
                    if (_pages.TryGetValue(VoiceImeToolTab.Settings, out var settings))
                        ((VoiceImeSettingsPage)settings).UpdateLanguageSettings(saved);
                };
                _pages.Add(kind, page);
                page.IsVisible = false;
                _body.Children.Add(page);
            }
            foreach (var entry in _pages) entry.Value.IsVisible = entry.Key == kind;
            _selected = kind;
            _notice.IsVisible = false;
            _pageTitle.IsVisible = kind == VoiceImeToolTab.Language;
            foreach (var entry in _tabs)
            {
                VoiceImeWindowDesign.SelectSheet(entry.Value, entry.Key == kind, (string)entry.Value.Tag!);
            }
            await VoiceImeToolPage.RenderedAsync(this, _closedCts.Token);
            if (!_closed) await page.InitializeAsync();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch
        {
            if (!_closed)
            {
                _notice.Text = "设置尚未保存或页面加载未完成，请在当前页重试。";
                _notice.IsVisible = true;
            }
        }
        finally { _navigation.Release(); }
    }

    private async Task CloseAfterSaveAsync()
    {
        _closing = true;
        await _navigation.WaitAsync();
        try
        {
            foreach (var page in _pages.Values) await page.PrepareToLeaveAsync();
            if (_pages.Values.Any(page => !page.CanClose)) return;
            _allowClose = true;
            Close();
        }
        catch
        {
            _notice.Text = "设置尚未保存，请重试保存后再关闭。";
            _notice.IsVisible = true;
        }
        finally { _closing = false; _navigation.Release(); }
    }
}