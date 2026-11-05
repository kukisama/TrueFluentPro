using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Views;

namespace TrueFluentPro;

/// <summary>
/// 窗口呈现协调：只处理本地外观，不拥有业务服务，不迁移 PageHost 或页面实例。
/// </summary>
public partial class MainWindow
{
    private readonly ShellAppearancePreferencesService _appearancePreferences;
    private ResourceInclude? _studyPalette;
    private long _appearanceVersion;
    private const string InkStudyCacheKey = "live-ink-study";
    private const string SongScrollCacheKey = "live-song-scroll";
    private string _currentNavTag = TrueFluentPro.ViewModels.MainWindowViewModel.NavTagLive;

    public ShellAppearancePreference CurrentShellAppearance { get; private set; }
    public bool IsSongStudyAppearance => CurrentShellAppearance == ShellAppearancePreference.SongStudy;
    public bool IsInkStudyAppearance => CurrentShellAppearance == ShellAppearancePreference.InkStudy;
    public bool IsSongScrollAppearance => CurrentShellAppearance == ShellAppearancePreference.SongScroll;

    /// <summary>仅应用 UI；菜单另行保存偏好。外部调用可用相同入口接入其他呈现方案。</summary>
    public void ApplyShellAppearance(ShellAppearancePreference appearance)
    {
        var focused = FocusManager?.GetFocusedElement() as InputElement;
        var version = ++_appearanceVersion;
        if (appearance is not ShellAppearancePreference.Classic and not ShellAppearancePreference.SongStudy
            and not ShellAppearancePreference.InkStudy and not ShellAppearancePreference.SongScroll)
            appearance = ShellAppearancePreference.Classic;

        CurrentShellAppearance = appearance;
        if (IsSongStudyAppearance)
        {
            _studyPalette ??= new ResourceInclude(new Uri("avares://TrueFluentPro/"))
            {
                Source = new Uri("avares://TrueFluentPro/Styles/SongStudyPalette.axaml")
            };
            if (!Resources.MergedDictionaries.Contains(_studyPalette))
                Resources.MergedDictionaries.Add(_studyPalette);
            RequestedThemeVariant = ThemeVariant.Light;
        }
        else
        {
            if (_studyPalette != null)
                Resources.MergedDictionaries.Remove(_studyPalette);
            // 不改 Application 的经典明暗值，恢复跟随原来的主题选择。
            RequestedThemeVariant = ThemeVariant.Default;
        }

        Classes.Set("song-study", IsSongStudyAppearance);
        StudyBook.IsVisible = IsSongStudyAppearance;
        StudyBamboo.IsVisible = IsSongStudyAppearance;
        StudyNavBamboo.IsVisible = IsSongStudyAppearance;
        StudyTassel.IsVisible = IsSongStudyAppearance;
        StudyBrand.IsVisible = IsSongStudyAppearance;
        ShellNavToggleButton.IsVisible = !IsSongStudyAppearance;
        PageHost.Margin = IsSongStudyAppearance ? new Thickness(22, 24, 28, 26) : default;
        ApplyMainNavPaneState(_viewModel?.IsMainNavPaneOpen ?? false);

        foreach (var page in _pageCache.Values)
            if (page is LiveTranslationView live)
                live.ApplyAppearance(IsSongStudyAppearance);

        // 仅切换呈现。ShowPage 不重建主 VM、服务或非翻译页面。
        if (_currentPage != null && (IsInkStudyAppearance || IsSongScrollAppearance
            || _currentPage is InkStudyTranslationView or SongScrollTranslationView))
            ShowPage(_currentNavTag);
        else
            UpdateInkStudyHost();
        UpdateThemePresentation();
        // 模板与主题重排可能暂时清除焦点；只恢复仍在本窗口内的原控件，不触碰内容。
        if (focused != null && TopLevel.GetTopLevel(focused) == this)
            Dispatcher.UIThread.Post(() =>
            {
                if (version == _appearanceVersion && focused.IsEffectivelyVisible && focused.IsEffectivelyEnabled
                    && TopLevel.GetTopLevel(focused) == this)
                    focused.Focus();
            }, DispatcherPriority.Loaded);
    }

    private void UpdateNavigationLabels(bool visible)
    {
        if (ShellNavRail == null) return;
        foreach (var visual in ShellNavRail.GetVisualDescendants())
            if (visual is TextBlock label && label.Classes.Contains("shell-nav-label"))
                label.IsVisible = visible;
    }

    private void UpdateThemePresentation()
    {
        if (ThemeNavButton == null || ShellThemeIcon == null) return;
        ShellThemeIcon.Value = IsSongStudyAppearance || IsInkStudyAppearance || IsSongScrollAppearance ? "fa-solid fa-book-open"
            : _viewModel?.ThemeToggleIconValue ?? "fa-solid fa-circle-half-stroke";
        var current = IsSongScrollAppearance ? "宋人书斋 · 长卷" : IsInkStudyAppearance ? "宋人书斋 · 原稿" : IsSongStudyAppearance ? "宋人书斋 · 明亮"
            : $"经典 · {ThemeModeLabel(_viewModel?.CurrentThemeMode ?? ThemeModePreference.System)}";
        ToolTip.SetTip(ThemeNavButton, $"当前：{current}；点击选择主题与界面外观");
    }

    private void ThemeNavButton_Click(object? sender, RoutedEventArgs e)
        => ShowAppearanceMenu(ThemeNavButton);

    private void ShowAppearanceMenu(Control target)
    {
        var menu = new MenuFlyout();
        foreach (var mode in new[] { ThemeModePreference.System, ThemeModePreference.Light, ThemeModePreference.Dark })
        {
            var selected = CurrentShellAppearance == ShellAppearancePreference.Classic
                && (_viewModel?.CurrentThemeMode ?? ThemeModePreference.System) == mode;
            var item = new MenuItem { Header = $"{(selected ? "✓ " : "　")}经典 · {ThemeModeLabel(mode)}" };
            item.Click += (_, _) =>
            {
                if (!SaveAndApplyAppearance(ShellAppearancePreference.Classic)) return;
                _viewModel?.UpdateThemeMode(mode);
                UpdateThemePresentation();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var study = new MenuItem { Header = $"{(IsSongStudyAppearance ? "✓ " : "　")}宋人书斋 · 明亮" };
        study.Click += (_, _) => SaveAndApplyAppearance(ShellAppearancePreference.SongStudy);
        menu.Items.Add(study);
        var ink = new MenuItem { Header = $"{(IsInkStudyAppearance ? "✓ " : "　")}宋人书斋 · 原稿（独立翻译页）" };
        ink.Click += (_, _) => SaveAndApplyAppearance(ShellAppearancePreference.InkStudy);
        menu.Items.Add(ink);
        var scroll = new MenuItem { Header = $"{(IsSongScrollAppearance ? "✓ " : "　")}宋人书斋 · 长卷（独立翻译页）" };
        scroll.Click += (_, _) => SaveAndApplyAppearance(ShellAppearancePreference.SongScroll);
        menu.Items.Add(scroll);
        if (target is Button button) button.Flyout = menu;
        menu.ShowAt(target);
    }

    private void UpdateInkStudyHost()
    {
        var active = IsInkStudyAppearance && _currentNavTag == TrueFluentPro.ViewModels.MainWindowViewModel.NavTagLive;
        var scrollActive = IsSongScrollAppearance && _currentNavTag == TrueFluentPro.ViewModels.MainWindowViewModel.NavTagLive;
        Classes.Set("ink-study", active);
        Classes.Set("song-scroll", scrollActive);
        LegacyWorkspace.IsVisible = !active && !scrollActive;
        ShellTitleBar.IsVisible = !scrollActive;
        InkStudyHost.IsVisible = active;
        SongScrollHost.IsVisible = scrollActive;
        // 新主题只覆盖自己的翻译呈现；其他页面恢复经典主题继承。
        RequestedThemeVariant = active || scrollActive || IsSongStudyAppearance ? ThemeVariant.Light : ThemeVariant.Default;
        UpdateNativeCaptionColors();
    }

    private bool SaveAndApplyAppearance(ShellAppearancePreference appearance)
    {
        try
        {
            _appearancePreferences.Save(appearance);
            ApplyShellAppearance(appearance);
            return true;
        }
        catch (Exception ex)
        {
            if (_viewModel != null)
                _viewModel.StatusMessage = $"外观切换未完成，请重试：{ex.Message}";
            else
                System.Diagnostics.Debug.WriteLine($"外观切换未完成：{ex.Message}");
            return false;
        }
    }

    private static string ThemeModeLabel(ThemeModePreference mode) => mode switch
    {
        ThemeModePreference.Light => "浅色",
        ThemeModePreference.Dark => "深色",
        _ => "跟随系统"
    };
}