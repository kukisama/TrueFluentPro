using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro.Controls;
using TrueFluentPro.Models;
using TrueFluentPro.ViewModels;
using TrueFluentPro.ViewModels.Presentation;

namespace TrueFluentPro.Views;

/// <summary>以 2560 为设计宽度，书页高度随窗口调整；换页/换主题不会停止业务。</summary>
public partial class SongScrollTranslationView : UserControl
{
    private ILiveTranslationPresentationSource? _source;
    private readonly List<INotifyPropertyChanged> _subscriptions = new();
    private readonly List<Bitmap> _bitmaps = new();
    private readonly List<(Control Control, double Top, double Height, bool StretchHeight)> _verticalDecorations = new();
    private Window? _window;
    private bool _syncingRoutes, _follow = true;
    private Vector _historyPosition, _insightPosition;
    private Point? _floatDrag;
    private bool _floatPinned;
    private Control? _dialog;
    private InputElement? _dialogFocus;
    private Func<AzureSpeechConfig>? _draftConfigProvider;
    private Func<SongScrollInsightDraft, System.Threading.Tasks.Task>? _saveDraft;
    public SongScrollPresentationViewModel? Presentation { get; private set; }
    public event Action<string>? NavigationRequested;
    public event Action<Control>? ThemeRequested;

    public SongScrollTranslationView()
    {
        AvaloniaXamlLoader.Load(this);
        BuildOrnaments();
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window != null) _window.PropertyChanged += WindowChanged;
            UpdateStageHeight(); UpdateCaption(); SyncSession();
        };
        DetachedFromVisualTree += (_, _) => { if (_window != null) _window.PropertyChanged -= WindowChanged; _window = null; };
        SizeChanged += (_, _) =>
        {
            UpdateStageHeight();
            if (this.FindControl<Border>("ScrollFloatingPaper")!.IsVisible)
                Dispatcher.UIThread.Post(() => ClampFloat(), DispatcherPriority.Loaded);
        };
        this.FindControl<ScrollViewer>("ScrollConversation")!.ScrollChanged += (_, e) =>
        {
            var scroll = this.FindControl<ScrollViewer>("ScrollConversation")!;
            if (e.ExtentDelta.Y == 0 && e.OffsetDelta.Y != 0) _follow = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 28;
            if (_follow && e.ExtentDelta.Y != 0) Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Loaded);
        };
    }

    private void UpdateStageHeight()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        // 舞台宽高比跟随客户区，Uniform 仍只等比缩放文字、按钮与书签。
        // 不设 1440 高度下限，否则较宽的最大化窗口又会出现左右空边。
        var height = Bounds.Height * 2560 / Bounds.Width;
        if (!double.IsFinite(height)) return;
        var extra = height - 1440;
        this.FindControl<Canvas>("ScrollStage")!.Height = height;
        this.FindControl<Canvas>("ScrollOrnaments")!.Height = height;
        foreach (var item in _verticalDecorations)
        {
            Canvas.SetTop(item.Control, item.Top + (item.StretchHeight ? 0 : extra));
            item.Control.Height = Math.Max(0, item.Height + (item.StretchHeight ? extra : 0));
        }
        this.FindControl<Border>("ScrollConversationPaper")!.Height = Math.Max(0, 1090 + extra);
        this.FindControl<ScrollViewer>("ScrollConversation")!.Height = Math.Max(0, 585 + extra);
        this.FindControl<Grid>("ScrollRightPage")!.Height = Math.Max(0, 1074 + extra);
        // 较矮的设计画布收紧导航底部纸面，不把设置/主题移到最后一枚书签上。
        var sidebarFooter = this.FindControl<Border>("ScrollSidebarFooter")!;
        Canvas.SetTop(sidebarFooter, 1133 + Math.Max(0, extra));
        sidebarFooter.Height = Math.Max(0, 262 + Math.Min(0, extra));
        Canvas.SetTop(this.FindControl<TextBlock>("ScrollColophon")!, 1296 + extra);
    }

    public void BindSession(ILiveTranslationPresentationSource? source)
    {
        if (ReferenceEquals(_source, source) && Presentation != null) return;
        DisposePresentation();
        if (source == null) return;
        _source = source; Presentation = new(source); DataContext = Presentation;
        Presentation.PropertyChanged += PresentationChanged;
        foreach (var value in new object?[] { source, Property(source, "ConfigVM"), Property(source, "AudioDevices"), Property(source, "AiInsight") })
            if (value is INotifyPropertyChanged changed) { changed.PropertyChanged += SessionChanged; _subscriptions.Add(changed); }
        if (source is MainWindowViewModel main)
        {
            main.AiInsight.LoadSongScrollAutoPreferences();
            BindConfigurationDrafts(() => main.ConfigVM.Config, async draft => { await main.Settings.SaveSongScrollInsightAsync(draft); main.AiInsight.UpdateConfig(); });
            // 窄协调命令仍属于同一业务 VM；不通过假计时器模拟运行。
            this.FindControl<Button>("ScrollStart")!.Command = main.SongScrollToggleCommand;
            this.FindControl<Button>("ScrollSend")!.Command = main.AiInsight.SongScrollSendCommand;
            this.FindControl<Button>("ScrollInsightStop")!.Command = main.AiInsight.SongScrollStopCommand;
        }
        SyncSession();
    }
    public void BindConfigurationDrafts(Func<AzureSpeechConfig> configProvider, Func<SongScrollInsightDraft, System.Threading.Tasks.Task> save)
    { _draftConfigProvider = configProvider; _saveDraft = save; }
    public void DisposePresentation()
    {
        foreach (var changed in _subscriptions) changed.PropertyChanged -= SessionChanged;
        _subscriptions.Clear();
        var old = Presentation; Presentation = null; _source = null; DataContext = null; old?.Dispose();
        if (old != null) old.PropertyChanged -= PresentationChanged;
        _draftConfigProvider = null; _saveDraft = null;
        CloseDialog(); this.FindControl<Border>("ScrollFloatingPaper")!.IsVisible = false;
        // 不停止或释放业务；素材跟随页面缓存，由应用退出回收。
    }
    private static object? Property(object? owner, string name) => owner?.GetType().GetProperty(name)?.GetValue(owner);
    private bool AudioFlag(string name) => Property(Property(_source, "AudioDevices"), name) is true;
    private void SessionChanged(object? sender, PropertyChangedEventArgs e) => SyncSession();
    private void PresentationChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(SongScrollPresentationViewModel.ReadingSize)) SyncSession(); }
    private void SyncSession()
    {
        if (_source == null) return;
        if (_source is MainWindowViewModel session) session.AiInsight.LoadSongScrollAutoPreferences();
        _syncingRoutes = true;
        try
        {
            this.FindControl<ComboBox>("ScrollRecognition")!.SelectedIndex = AudioFlag("IsRecognitionMicOnly") ? 0 : AudioFlag("IsRecognitionLoopbackOnly") ? 1 : 2;
            this.FindControl<ComboBox>("ScrollRecording")!.SelectedIndex = AudioFlag("IsRecordingMicOnly") ? 0 : AudioFlag("IsRecordingLoopbackOnly") ? 1 : 2;
        }
        finally { _syncingRoutes = false; }
        var names = new[] { "麦克风", "系统音频", "麦克风 + 系统音频" };
        var recognition = names[this.FindControl<ComboBox>("ScrollRecognition")!.SelectedIndex];
        var recording = names[this.FindControl<ComboBox>("ScrollRecording")!.SelectedIndex];
        this.FindControl<TextBlock>("ScrollSourceSummary")!.Text = $"识别：{recognition} · 录制：{recording}";
        var config = Property(_source, "ConfigVM");
        var sourceNames = new[] { "自动识别", "英语", "中文", "日语", "韩语", "法语", "德语", "西班牙语" };
        var targetNames = sourceNames.Skip(1).ToArray();
        var si = Property(config, "SourceLanguageIndex") is int i ? Math.Clamp(i, 0, 7) : 0;
        var ti = Property(config, "TargetLanguageIndex") is int j ? Math.Clamp(j, 0, 6) : 0;
        var readingLabel = Presentation?.ReadingSize switch { 20 => "大字号", 24 => "特大字号", _ => "中字号" };
        this.FindControl<TextBlock>("ScrollSettingsSummary")!.Text = $"{Property(config, "ActiveSpeechResourceStatus") ?? "未配置"} | {recognition} | {sourceNames[si]} → {targetNames[ti]} | {readingLabel}";
        var subtitleOpen = Property(_source, "IsAnySubtitleOpen") is true;
        this.FindControl<Border>("ScrollSubtitleTrack")!.Background = new SolidColorBrush(Color.Parse(subtitleOpen ? "#4D7979" : "#D1D3D6"));
        this.FindControl<Border>("ScrollSubtitleThumb")!.HorizontalAlignment = subtitleOpen ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        if (this.FindControl<Button>("ScrollSubtitle")!.ContextFlyout is MenuFlyout subtitleMenu)
            foreach (var item in subtitleMenu.Items.OfType<MenuItem>()) item.DataContext = Presentation;
        if (_source is MainWindowViewModel main) this.FindControl<TextBlock>("ScrollStartLabel")!.Text = main.SongScrollToggleText;
    }
    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == Window.WindowStateProperty || e.Property == Window.CanMaximizeProperty || e.Property == Window.CanMinimizeProperty) UpdateCaption(); }
    private void UpdateCaption()
    {
        var button = this.FindControl<Button>("ScrollMaximize")!;
        var maximized = _window?.WindowState == WindowState.Maximized;
        AutomationProperties.SetName(button, maximized ? "还原" : "最大化"); ToolTip.SetTip(button, maximized ? "还原" : "最大化");
        this.FindControl<Path>("ScrollMaximizeMark")!.Data = Geometry.Parse(maximized ? "M6 2 H22 V18 M2 6 H18 V22 H2 Z" : "M2 2 H22 V22 H2 Z");
        button.IsEnabled = _window?.CanMaximize ?? true; this.FindControl<Button>("ScrollMinimize")!.IsEnabled = _window?.CanMinimize ?? true;
    }
    private void TitleDrag(object? sender, PointerPressedEventArgs e)
    { if (_window == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; if (e.ClickCount == 2) ToggleMaximize(); else _window.BeginMoveDrag(e); e.Handled = true; }
    private void ToggleMaximize() { if (_window?.CanMaximize == true) _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
    private void Minimize(object? sender, RoutedEventArgs e) { if (_window?.CanMinimize == true) _window.WindowState = WindowState.Minimized; }
    private void Maximize(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseWindow(object? sender, RoutedEventArgs e) => _window?.Close();
    private void Navigate(object? sender, RoutedEventArgs e) { if (sender is Button { Tag: string tag }) NavigationRequested?.Invoke(tag); }
    private void ThemeClick(object? sender, RoutedEventArgs e) { if (sender is Button button) ThemeRequested?.Invoke(button); }
    private void DisplayClick(object? sender, RoutedEventArgs e) { if (sender is Button { Tag: string mode }) Presentation?.SetDisplay(mode); }
    private void TabClick(object? sender, RoutedEventArgs e)
    {
        if (Presentation == null || sender is not Button { Tag: string tag }) return;
        var history = this.FindControl<ScrollViewer>("ScrollHistoryContent")!; var insight = this.FindControl<ScrollViewer>("ScrollInsightContent")!;
        if (Presentation.IsHistory) _historyPosition = history.Offset; else _insightPosition = insight.Offset;
        Presentation.SetTab(tag);
        Dispatcher.UIThread.Post(() => { if (Presentation?.IsHistory == true) history.Offset = _historyPosition; else insight.Offset = _insightPosition; }, DispatcherPriority.Loaded);
    }
    private void SettingsClick(object? sender, RoutedEventArgs e) { if (Presentation != null) Presentation.SettingsExpanded = !Presentation.SettingsExpanded; }
    private void HistoryAll(object? sender, RoutedEventArgs e) { if (Presentation != null) Presentation.AllHistory = !Presentation.AllHistory; }
    private void HistoryEntryClick(object? sender, RoutedEventArgs e) { if (sender is Button { DataContext: SongScrollCard card }) card.IsExpanded = !card.IsExpanded; }
    private void ChangeRoute(string[] properties, int index)
    {
        if (_syncingRoutes || index < 0 || Property(Property(_source, "ConfigVM"), "IsConfigurationEnabled") is false) return;
        var audio = Property(_source, "AudioDevices"); audio?.GetType().GetProperty(properties[index])?.SetValue(audio, true); SyncSession();
    }
    private void RecognitionChanged(object? sender, SelectionChangedEventArgs e) => ChangeRoute(new[] { "IsRecognitionMicOnly", "IsRecognitionLoopbackOnly", "IsRecognitionLoopbackMix" }, (sender as ComboBox)?.SelectedIndex ?? -1);
    private void RecordingChanged(object? sender, SelectionChangedEventArgs e) => ChangeRoute(new[] { "IsRecordingMicOnly", "IsRecordingLoopbackOnly", "IsRecordingLoopbackMix" }, (sender as ComboBox)?.SelectedIndex ?? -1);
    private void PresetsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _source == null) return;
        var insight = Property(_source, "AiInsight");
        var menu = new MenuFlyout();
        if (Property(insight, "InsightPresetButtons") is IEnumerable<InsightPresetButton> presets)
            foreach (var preset in presets)
            {
                var item = new MenuItem { Header = preset.Name };
                if (insight is AiInsightViewModel ai)
                { item.Command = ai.SongScrollPresetCommand; item.CommandParameter = preset.Prompt; }
                else { item.Command = Property(insight, "SendPresetInsightCommand") as System.Windows.Input.ICommand; item.CommandParameter = preset.Prompt; }
                menu.Items.Add(item);
            }
        button.Flyout = menu; menu.ShowAt(button);
    }
    private void InsightClear(object? sender, RoutedEventArgs e)
    {
        if (_source is MainWindowViewModel main) main.AiInsight.ClearSongScrollInsight();
        else (Property(Property(_source, "AiInsight"), "ClearInsightCommand") as System.Windows.Input.ICommand)?.Execute(null);
    }
    private void HistoryClear(object? sender, RoutedEventArgs e)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = PaperButton("取消", (_, _) => CloseDialog());
        var confirm = PaperButton("确认清空", (_, _) => { (Property(_source, "ClearHistoryCommand") as System.Windows.Input.ICommand)?.Execute(null); CloseDialog(); });
        actions.Children.Add(cancel); actions.Children.Add(confirm);
        var body = new StackPanel { Spacing = 20 };
        body.Children.Add(new TextBlock { Text = "清空当前翻译记录？", FontFamily = "STKaiti, KaiTi", FontSize = 30 });
        body.Children.Add(new TextBlock { Text = "将清空当前历史、原文与译文、实时快照和字幕内容。此操作不可恢复；不会删除历史文件。", TextWrapping = TextWrapping.Wrap, FontSize = 18 });
        body.Children.Add(actions); ShowDialog(body, cancel);
    }
    private static Button PaperButton(string text, EventHandler<RoutedEventArgs> action)
    { var button = new Button { Content = text, FontSize = 19, MinHeight = 40 }; button.Click += action; return button; }
    private void ShowDialog(Control content, Control? focus = null)
    {
        CloseDialog();
        _dialogFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as InputElement;
        var overlay = new Grid { Background = new SolidColorBrush(Color.Parse("#4426352D")), Width = Bounds.Width, Height = Bounds.Height };
        var border = new Border { Width = Math.Min(680, Bounds.Width - 32), MaxHeight = Math.Max(100, Bounds.Height - 32), Padding = new Thickness(24), Background = new SolidColorBrush(Color.Parse("#F5EEE0")), BorderBrush = new SolidColorBrush(Color.Parse("#C5AC85")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        overlay.Children.Add(border); overlay.ZIndex = 100; this.FindControl<Canvas>("ScrollOverlay")!.Children.Add(overlay); _dialog = overlay;
        // 背后业务控件禁用，保留原焦点可由 Esc/取消安全返回。
        this.FindControl<Viewbox>("ScrollScale")!.IsEnabled = false;
        this.FindControl<Border>("ScrollFloatingPaper")!.IsEnabled = false;
        focus?.Focus();
    }
    private void CloseDialog()
    {
        if (_dialog != null) this.FindControl<Canvas>("ScrollOverlay")!.Children.Remove(_dialog);
        _dialog = null; this.FindControl<Viewbox>("ScrollScale")!.IsEnabled = true; this.FindControl<Border>("ScrollFloatingPaper")!.IsEnabled = true;
        if (_dialogFocus is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }) _dialogFocus.Focus();
        _dialogFocus = null;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        { if (_dialog != null) CloseDialog(); else this.FindControl<Border>("ScrollFloatingPaper")!.IsVisible = false; e.Handled = true; }
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Button focused)
        {
            var tabs = new[] { "ScrollHistoryTab", "ScrollInsightTab" }.Select(name => this.FindControl<Button>(name)!).ToArray();
            var segments = new[] { "ScrollOriginal", "ScrollTranslated", "ScrollBilingual" }.Select(name => this.FindControl<Button>(name)!).ToArray();
            var group = tabs.Contains(focused) ? tabs : segments.Contains(focused) ? segments : null;
            if (group == null) return;
            var index = e.Key == Key.Home ? 0 : e.Key == Key.End ? group.Length - 1 : (Array.IndexOf(group, focused) + (e.Key == Key.Right ? 1 : group.Length - 1)) % group.Length;
            group[index].Focus(); if (group == tabs) TabClick(group[index], new()); else DisplayClick(group[index], new()); e.Handled = true;
        }
    }
    private void FloatClick(object? sender, RoutedEventArgs e)
    {
        var paper = this.FindControl<Border>("ScrollFloatingPaper")!; paper.IsVisible = !paper.IsVisible;
        if (paper.IsVisible) ClampFloat(initial: true);
    }
    private void ClampFloat(double? x = null, double? y = null, bool initial = false)
    {
        var paper = this.FindControl<Border>("ScrollFloatingPaper")!;
        var reading = this.FindControl<Border>("ScrollReading")!; var controls = this.FindControl<Border>("ScrollSessionControls")!;
        var anchor = TransformedBounds(reading, this); var bottom = Math.Min(Bounds.Height - 12, TransformedBounds(controls, this).Top - 12);
        paper.Width = Math.Min(560, Math.Min(Math.Max(300, anchor.Width - 24), Bounds.Width - 24));
        paper.Height = Math.Max(80, Math.Min(480, bottom - Math.Max(12, anchor.Top + 12)));
        var left = x ?? (initial ? anchor.Right - paper.Width - 12 : Canvas.GetLeft(paper)); var top = y ?? (initial ? anchor.Top + 12 : Canvas.GetTop(paper));
        Canvas.SetLeft(paper, Math.Clamp(double.IsNaN(left) ? 12 : left, 12, Math.Max(12, Bounds.Width - paper.Width - 12)));
        Canvas.SetTop(paper, Math.Clamp(double.IsNaN(top) ? 12 : top, 12, Math.Max(12, bottom - paper.Height)));
        this.FindControl<SongScrollMarkdownBlock>("ScrollFloatMarkdown")!.MaxContentWidth = Math.Max(40, paper.Width - 32);
    }
    public static Rect TransformedBounds(Visual visual, Visual ancestor)
    {
        var matrix = visual.TransformToVisual(ancestor) ?? throw new InvalidOperationException("控件不在同一视觉树。");
        return new Rect(visual.Bounds.Size).TransformToAABB(matrix);
    }
    private void FloatPin(object? sender, RoutedEventArgs e)
    { _floatPinned = !_floatPinned; var paper = this.FindControl<Border>("ScrollFloatingPaper")!; paper.ZIndex = _floatPinned ? 90 : 70; paper.BorderBrush = new SolidColorBrush(Color.Parse(_floatPinned ? "#5E8270" : "#BCA27B")); this.FindControl<Button>("ScrollFloatPin")!.Background = _floatPinned ? new SolidColorBrush(Color.Parse("#DCE9E1")) : Brushes.Transparent; }
    private void FloatFont(object? sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string value } && int.TryParse(value, out var delta)) { var md = this.FindControl<SongScrollMarkdownBlock>("ScrollFloatMarkdown")!; md.ReadingFontSize = Math.Clamp(md.ReadingFontSize + delta, 14, 28); } }
    private void FloatDragStart(object? sender, PointerPressedEventArgs e)
    { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || (e.Source as Visual)?.GetVisualAncestors().OfType<Button>().Any() == true) return; var paper = this.FindControl<Border>("ScrollFloatingPaper")!; _floatDrag = e.GetPosition(this) - new Vector(Canvas.GetLeft(paper), Canvas.GetTop(paper)); e.Pointer.Capture(sender as IInputElement); e.Handled = true; }
    private void FloatDragMove(object? sender, PointerEventArgs e)
    { if (_floatDrag is { } offset) { var p = e.GetPosition(this); ClampFloat(p.X - offset.X, p.Y - offset.Y); } }
    private void FloatDragEnd(object? sender, PointerReleasedEventArgs e) { _floatDrag = null; e.Pointer.Capture(null); }
    private void FloatDragLost(object? sender, PointerCaptureLostEventArgs e) => _floatDrag = null;

    private Bitmap Asset(string name)
    { var bitmap = new Bitmap(AssetLoader.Open(new Uri($"avares://TrueFluentPro/Assets/Appearance/SongScroll/{name}.png"))); _bitmaps.Add(bitmap); return bitmap; }
    private ImageBrush Texture(string name)
    { var image = Asset(name); return new ImageBrush(image) { TileMode = TileMode.Tile, DestinationRect = new RelativeRect(new Rect(image.Size), RelativeUnit.Absolute) }; }
    private void BuildOrnaments()
    {
        var canvas = this.FindControl<Canvas>("ScrollStage")!;
        canvas.Background = Texture("desk-paper");
        this.FindControl<Border>("ScrollFloatingPaper")!.Background = Texture("page-paper");
        void Place(Control control, double x, double y, double w, double h, int z, bool stretchHeight = false, bool anchorBottom = false)
        {
            control.Width = w; control.Height = h; Canvas.SetLeft(control, x); Canvas.SetTop(control, y); control.ZIndex = z; canvas.Children.Add(control);
            if (stretchHeight || anchorBottom) _verticalDecorations.Add((control, y, h, stretchHeight));
        }
        void Image(string name, double x, double y, double w, double h, int z, IBrush? mask = null, bool stretchHeight = false, bool anchorBottom = false)
        { Place(new Image { Source = Asset(name), Stretch = Stretch.Fill, OpacityMask = mask }, x, y, w, h, z, stretchHeight, anchorBottom); }
        LinearGradientBrush Gradient(params (double Offset, string Color)[] stops)
        { var gradient = new LinearGradientBrush { StartPoint = new RelativePoint(0, .5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, .5, RelativeUnit.Relative) }; foreach (var stop in stops) gradient.GradientStops.Add(new GradientStop(Color.Parse(stop.Color), stop.Offset)); return gradient; }
        Image("bamboo-top", 0, 0, 307, 103, 1); Image("bamboo-side", 0, 103, 64, 802, 1); Image("bamboo-foot", 0, 907, 54, 288, 1);
        var sidebar = new Border { Background = Texture("wood-edge"), BorderBrush = new SolidColorBrush(Color.Parse("#856755")), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(17,5,5,17), BoxShadow = BoxShadows.Parse("4 12 21 0 #55554334") };
        sidebar.Child = new Border { Background = Gradient((0,"#66604733"),(.2,"#44D5BBA3"),(.75,"#77A69B6F"),(1,"#AA395A45")), CornerRadius = sidebar.CornerRadius };
        Place(sidebar,50,226,275,1165,2,stretchHeight: true);
        Place(new Border { Background = new SolidColorBrush(Color.Parse("#56716B")), BorderBrush = new SolidColorBrush(Color.Parse("#786B55")), BorderThickness = new Thickness(8), CornerRadius = new CornerRadius(21,19,17,19), BoxShadow = BoxShadows.Parse("1 20 28 0 #554B3C28") },312,68,2248,1337,2,stretchHeight: true);
        foreach (var (x,w,radius,shadow) in new[] { (343d,1035d,new CornerRadius(1,24,16,0),"-15 0 22 0 #228C7951 inset"),(1378d,1143d,new CornerRadius(23,0,0,13),"16 0 24 0 #2D8D7965 inset") })
        { var page = new Border { Background = Texture("page-paper"), CornerRadius = radius, BoxShadow = BoxShadows.Parse(shadow), Child = new Border { CornerRadius = radius, Background = Gradient((0,"#A0F2ECDF"),(.22,"#A0FFFBF2"),(.72,"#BBFFFDF8"),(1,"#88E9DECD")) } }; Place(page,x,80,w,1308,3,stretchHeight: true); }
        Image("bamboo-page",2345,120,116,117,4);
        var fade = new LinearGradientBrush { StartPoint = new RelativePoint(0,0,RelativeUnit.Relative), EndPoint = new RelativePoint(0,1,RelativeUnit.Relative), GradientStops = new GradientStops { new(Colors.Transparent,0),new(Colors.Black,.15),new(Colors.Black,.93),new(Colors.Transparent,1) } };
        Image("landscape",406,940,950,356,7,fade,anchorBottom: true);
        Image("book-top",308,64,2252,56,20); Image("book-bottom",308,1352,2252,62,20,anchorBottom: true); Image("book-left",308,120,98,1232,20,stretchHeight: true); Image("book-right",2485,120,75,1232,20,stretchHeight: true); Image("book-spine",1359,115,37,1237,20,stretchHeight: true);
        Place(new Image { Source = Asset("inkstone"), Stretch = Stretch.Uniform },2468,1270,92,160,5,anchorBottom: true);
    }
    private void AutoClick(object? sender, RoutedEventArgs e) => OpenAutoDraft();
    private void AdvancedClick(object? sender, RoutedEventArgs e) => OpenAdvancedDraft();
}

/// <summary>复用真实 Markdown AST；只修饰本控件生成的节点，不改变公共渲染器或全局字体。</summary>
public sealed class SongScrollMarkdownBlock : ChatMarkdownBlock
{
    private bool _restyling;
    public static readonly StyledProperty<double> ReadingFontSizeProperty = AvaloniaProperty.Register<SongScrollMarkdownBlock, double>(nameof(ReadingFontSize),26);
    public double ReadingFontSize { get => GetValue(ReadingFontSizeProperty); set => SetValue(ReadingFontSizeProperty,value); }
    public double MajorHeadingOffset { get; set; } = 10;
    public double MinorHeadingOffset { get; set; } = 4;
    public SongScrollMarkdownBlock() { Children.CollectionChanged += (_, _) => Restyle(); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == ReadingFontSizeProperty || change.Property == MarkdownProperty || change.Property == MaxContentWidthProperty) Restyle(); }
    private void Restyle()
    {
        if (_restyling) return;
        _restyling = true;
        try
        {
        Spacing = 4;
        foreach (var text in this.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            var heading = text.FontWeight >= FontWeight.SemiBold;
            text.FontFamily = "STKaiti, KaiTi, SimSun"; text.FontSize = ReadingFontSize + (heading ? MinorHeadingOffset : 0);
            text.LineHeight = text.FontSize * (heading ? 1.2 : 1.3); text.Foreground = new SolidColorBrush(Color.Parse("#27332F"));
            text.Margin = heading ? new Thickness(0,8,0,4) : default;
            text.Padding = default;
        }
        var markdown = Markdown ?? "";
        var document = Markdig.Markdown.Parse(markdown);
        for (var i = 0; i < Math.Min(document.Count, Children.Count); i++)
        {
            var control = Children[i];
            if (document[i] is Markdig.Syntax.ListBlock && control is StackPanel list) list.Spacing = 0;
            if (document[i] is Markdig.Syntax.HeadingBlock h)
            {
                var heading = control as SelectableTextBlock ?? control.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();
                if (heading == null) continue;
                heading.FontSize = ReadingFontSize + (h.Level <= 2 ? MajorHeadingOffset : MinorHeadingOffset);
                heading.LineHeight = heading.FontSize * (h.Level <= 2 ? 1.15 : 1.2);
                if (h.Level <= 2 && control is StackPanel stack)
                {
                    if (!stack.Children.OfType<Border>().Any())
                    {
                        stack.Children.Remove(heading);
                        stack.Children.Clear();
                        stack.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#4D7979")), BorderThickness = new Thickness(4,0,0,0), Padding = new Thickness(16,0,0,0), Child = heading });
                    }
                    heading.Margin = default; stack.Margin = new Thickness(0,0,0,6);
                }
            }
            else if (i < document.Count && document[i].Span.Start >= 0 && document[i].Span.Start < markdown.Length
                && markdown.AsSpan(document[i].Span.Start).StartsWith("生成时间", StringComparison.Ordinal))
            {
                if (control is SelectableTextBlock meta) { meta.FontSize = 18; meta.LineHeight = 23.4; meta.Margin = new Thickness(0,0,0,8); }
            }
        }
        }
        finally { _restyling = false; }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Restyle(); }
}