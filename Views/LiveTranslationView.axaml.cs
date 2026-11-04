using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace TrueFluentPro.Views;

public partial class LiveTranslationView : UserControl
{
    private readonly IResourceDictionary _classicActionResources;
    private readonly ResourceDictionary _studyActionResources = new();
    private readonly Dictionary<TextEditor, EditorAppearance> _editorAppearances = new();
    private bool _appearanceReady;
    private bool? _compactStudy;
    private bool _studySettingsExpanded = true;

    /// <summary>仅表示本页面的 UI 外观，不参与业务配置或服务生命周期。</summary>
    public bool IsStudyAppearance { get; private set; }

    public LiveTranslationView()
    {
        InitializeComponent();
        _classicActionResources = ActionRegion.Resources;
        _appearanceReady = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            // 子编辑器在主题事件中写入本地颜色，等其完成后再应用纸面色。
            if (IsStudyAppearance)
                Dispatcher.UIThread.Post(UpdateEditorAppearance);
        };
    }

    /// <summary>
    /// 由 MainWindow 在 UI 线程调用。保留页面、VM、编辑器和全部内容控件实例。
    /// </summary>
    public void ApplyAppearance(bool isStudy)
    {
        if (IsStudyAppearance == isStudy)
        {
            UpdateResponsiveLayout();
            return;
        }

        if (IsStudyAppearance && _compactStudy != true)
            _studySettingsExpanded = TranslationSettings.IsExpanded;

        IsStudyAppearance = isStudy;
        Classes.Set("study-live", isStudy);
        Classes.Set("classic-live", !isStudy);
        _compactStudy = null;

        // 经典底栏的蓝色悬停资源不应遮住书斋的同名局部色板。
        ActionRegion.Resources = isStudy ? _studyActionResources : _classicActionResources;
        LayoutRoot.RowDefinitions = new RowDefinitions(isStudy ? "64,*,Auto,Auto" : "Auto,*,Auto");
        LayoutRoot.ColumnDefinitions = new ColumnDefinitions(isStudy ? "12,*,28,*,12" : "12,3*,10,2*,12");
        LayoutRoot.ColumnDefinitions[1].MinWidth = isStudy ? 0 : 420;
        LayoutRoot.ColumnDefinitions[3].MinWidth = isStudy ? 0 : 300;
        LayoutRoot.RowDefinitions[1].MinHeight = isStudy ? 128 : 0;

        Grid.SetRow(ToolbarRegion, isStudy ? 2 : 0);
        Grid.SetColumn(ToolbarRegion, isStudy ? 3 : 0);
        Grid.SetColumnSpan(ToolbarRegion, isStudy ? 1 : 5);
        Grid.SetRowSpan(TranscriptRegion, isStudy ? 2 : 1);
        Grid.SetRow(ActionRegion, isStudy ? 3 : 2);
        TranscriptSplitter.IsVisible = !isStudy;

        ActionLayout.RowDefinitions = new RowDefinitions(isStudy ? "Auto,Auto" : "Auto");
        Grid.SetRow(StatusRegion, isStudy ? 1 : 0);
        Grid.SetColumn(StatusRegion, isStudy ? 0 : 7);
        Grid.SetColumnSpan(StatusRegion, isStudy ? 8 : 1);
        StatusRegion.Margin = isStudy ? new Thickness(0,4,0,0) : new Thickness(0);
        TranslationStatus.MaxWidth = isStudy ? 280 : 400;

        TranslationSettings.IsExpanded = !isStudy || _studySettingsExpanded;
        SettingsScroll.VerticalScrollBarVisibility = isStudy
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        if (!isStudy)
        {
            ToolbarRegion.MaxHeight = double.PositiveInfinity;
            SettingsScroll.MaxHeight = double.PositiveInfinity;
        }

        UpdateResponsiveLayout();
        UpdateEditorAppearance();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_appearanceReady)
            UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (!IsStudyAppearance || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        // 这里的尺寸是 PageHost 内的可用尺寸，非整个主窗口尺寸。
        var compact = Bounds.Width < 900 || Bounds.Height < 520;
        if (_compactStudy != compact)
        {
            if (compact)
            {
                _studySettingsExpanded = TranslationSettings.IsExpanded;
                TranslationSettings.IsExpanded = false;
            }
            else if (_compactStudy == true)
            {
                TranslationSettings.IsExpanded = _studySettingsExpanded;
            }
            _compactStudy = compact;
        }

        // 标题、页脚和历史先保留空间；展开设置只滚动参数，不挤掉开始／停止。
        var settingsHeight = Math.Clamp(Bounds.Height - 64 - 80 - 128 - 12, 88, 232);
        ToolbarRegion.MaxHeight = settingsHeight;
        SettingsScroll.MaxHeight = Math.Max(24, settingsHeight - 64);
    }

    private void OnTranscriptEditorLoaded(object? sender, RoutedEventArgs e)
    {
        if (IsStudyAppearance)
            UpdateEditorAppearance();
    }

    private void UpdateEditorAppearance()
    {
        if (!IsStudyAppearance)
        {
            foreach (var (editor, appearance) in _editorAppearances)
            {
                appearance.ForegroundBinding.Dispose();
                editor.MinHeight = appearance.MinHeight;
                // 同一主题原样恢复；跨明暗切换时与 AdvancedRichTextBox 原颜色保持一致。
                var sameTheme = editor.ActualThemeVariant == appearance.Theme;
                var dark = editor.ActualThemeVariant == ThemeVariant.Dark;
                editor.Background = sameTheme ? appearance.Background
                    : new SolidColorBrush(dark ? Color.FromRgb(30, 30, 30) : Colors.White);
                editor.Foreground = sameTheme ? appearance.Foreground
                    : new SolidColorBrush(dark ? Color.FromRgb(229, 231, 235) : Color.FromRgb(17, 24, 39));
                if (appearance.Toolbar != null)
                    appearance.Toolbar.Background = sameTheme ? appearance.ToolbarBackground
                        : new SolidColorBrush(dark ? Color.FromRgb(45, 45, 45) : Color.FromRgb(233, 236, 239));
            }
            _editorAppearances.Clear();
            return;
        }

        foreach (var editor in TranscriptRegion.GetVisualDescendants().OfType<TextEditor>())
        {
            if (_editorAppearances.TryGetValue(editor, out var existing))
            {
                // ActualThemeVariantChanged 可能重新写入本地色，恢复资源绑定即可。
                existing.ForegroundBinding.Dispose();
                existing.ForegroundBinding = editor.Bind(TextEditor.ForegroundProperty,
                    editor.GetResourceObservable("TextPrimaryBrush"));
                editor.Background = Brushes.Transparent;
                if (existing.Toolbar != null)
                    existing.Toolbar.Background = Brushes.Transparent;
                continue;
            }

            var toolbar = (editor.Parent as DockPanel)?.Children.OfType<StackPanel>().FirstOrDefault();
            var appearance = new EditorAppearance
            {
                MinHeight = editor.MinHeight,
                Background = editor.Background,
                Foreground = editor.Foreground,
                Theme = editor.ActualThemeVariant,
                Toolbar = toolbar,
                ToolbarBackground = toolbar?.Background,
                ForegroundBinding = editor.Bind(TextEditor.ForegroundProperty,
                    editor.GetResourceObservable("TextPrimaryBrush"))
            };
            _editorAppearances.Add(editor, appearance);
            editor.MinHeight = 0;
            editor.Background = Brushes.Transparent;
            if (toolbar != null)
                toolbar.Background = Brushes.Transparent;
        }
    }

    private sealed class EditorAppearance
    {
        public double MinHeight { get; init; }
        public IBrush? Background { get; init; }
        public IBrush? Foreground { get; init; }
        public ThemeVariant Theme { get; init; } = ThemeVariant.Light;
        public StackPanel? Toolbar { get; init; }
        public IBrush? ToolbarBackground { get; init; }
        public IDisposable ForegroundBinding { get; set; } = null!;
    }
}
