using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace TrueFluentPro.Services;

/// <summary>独立输入法窗口的蓝白呈现；仅依赖 Fluent，不修改应用级资源或业务状态。</summary>
internal static class VoiceImeWindowDesign
{
    internal static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#132B49"));
    internal static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#62758B"));
    internal static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#246BEB"));
    internal static readonly IBrush Line = new SolidColorBrush(Color.Parse("#E3EAF3"));
    internal static readonly IBrush Page = new SolidColorBrush(Color.Parse("#F3F7FC"));

    internal static void Apply(Window window)
    {
        window.RequestedThemeVariant = ThemeVariant.Light;
        window.FontFamily = new FontFamily("Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, sans-serif");
        window.FontSize = 14;
        window.Foreground = Ink;
        window.Background = Page;
        window.SystemDecorations = SystemDecorations.BorderOnly;
        window.Opened += (_, _) => UpdateNativeFrame(window);
        window.PropertyChanged += (_, change) =>
        {
            if (change.Property == Window.WindowStateProperty || change.Property == Window.IsActiveProperty)
                UpdateNativeFrame(window);
        };

        // 保留 Fluent 原模板、焦点和弹层行为；动态资源只覆盖当前窗口及其弹层。
        var resources = window.Resources;
        resources["ControlCornerRadius"] = new CornerRadius(8);
        resources["OverlayCornerRadius"] = new CornerRadius(8);
        resources["SystemAccentColor"] = Color.Parse("#246BEB");
        resources["SystemControlFocusVisualPrimaryBrush"] = Blue;
        resources["SystemControlFocusVisualSecondaryBrush"] = Brushes.White;

        IBrush hover = new SolidColorBrush(Color.Parse("#EAF1FD"));
        IBrush selected = new SolidColorBrush(Color.Parse("#DCE8FC"));
        IBrush disabledBackground = new SolidColorBrush(Color.Parse("#EDF1F6"));
        IBrush accentHover = new SolidColorBrush(Color.Parse("#1B59CC"));
        IBrush accentPressed = new SolidColorBrush(Color.Parse("#174CA9"));

        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            var disabled = state == "Disabled";
            var background = disabled ? disabledBackground : state == "Pressed" ? selected
                : state == "PointerOver" ? hover : Brushes.White;
            var foreground = disabled ? Muted : Ink;
            var accent = disabled ? Line : state == "Pressed" ? accentPressed
                : state == "PointerOver" ? accentHover : Blue;
            resources["ButtonBackground" + state] = background;
            resources["ButtonForeground" + state] = foreground;
            resources["ButtonBorderBrush" + state] = Line;
            resources["AccentButtonBackground" + state] = accent;
            resources["AccentButtonForeground" + state] = disabled ? Muted : Brushes.White;
            resources["AccentButtonBorderBrush" + state] = accent;

            resources["ToggleSwitchFillOn" + state] = disabled ? Line : accent;
            resources["ToggleSwitchStrokeOn" + state] = disabled ? Muted : accent;
            resources["ToggleSwitchKnobFillOn" + state] = disabled ? Muted : Brushes.White;
            resources["ToggleSwitchFillOff" + state] = background;
            resources["ToggleSwitchStrokeOff" + state] = Muted;
            resources["ToggleSwitchKnobFillOff" + state] = disabled ? Muted : Ink;

            foreach (var selection in new[] { "", "Selected" })
            {
                var suffix = selection + state;
                foreach (var control in new[] { "ComboBoxItem", "ListBoxItem" })
                {
                    resources[control + "Foreground" + suffix] = foreground;
                    resources[control + "Background" + suffix] = disabled ? disabledBackground
                        : selection.Length > 0 ? selected : state.Length > 0 ? hover : Brushes.Transparent;
                    resources[control + "BorderBrush" + suffix] = Brushes.Transparent;
                }
            }
        }

        foreach (var state in new[] { "", "PointerOver", "Pressed", "Focused", "FocusedPressed", "Disabled" })
        {
            var disabled = state == "Disabled";
            resources["ComboBoxForeground" + state] = disabled ? Muted : Ink;
            resources["ComboBoxPlaceHolderForeground" + state] = Muted;
            resources["ComboBoxDropDownGlyphForeground" + state] = Muted;
            resources["ComboBoxBackground" + state] = disabled ? disabledBackground : Brushes.White;
            resources["ComboBoxBorderBrush" + state] = disabled || state.Length == 0 ? Line : Blue;
            resources["TextControlForeground" + state] = disabled ? Muted : Ink;
            resources["TextControlPlaceholderForeground" + state] = Muted;
            resources["TextControlBackground" + state] = disabled ? disabledBackground : Brushes.White;
            resources["TextControlBorderBrush" + state] = disabled || state.Length == 0 ? Line : Blue;
        }
        resources["ComboBoxBackgroundUnfocused"] = Brushes.Transparent;
        resources["ComboBoxBackgroundBorderBrushUnfocused"] = Line;
        resources["ComboBoxBackgroundBorderBrushFocused"] = Blue;
        resources["ComboBoxDropDownBackground"] = Brushes.White;
        resources["ComboBoxDropDownBorderBrush"] = Line;
        resources["TextControlSelectionHighlightColor"] = selected;
        resources["SystemControlHighlightAltBaseHighBrush"] = Ink;
        resources["SystemControlHighlightListLowBrush"] = hover;
        resources["SystemControlHighlightListMediumBrush"] = selected;
        resources["SystemControlHighlightListAccentLowBrush"] = selected;
        resources["SystemControlHighlightListAccentMediumBrush"] = selected;
        resources["SystemControlHighlightListAccentHighBrush"] = selected;
        resources["ProgressBarForeground"] = Blue;

        window.Styles.Add(new Style(s => s.OfType<Button>())
        {
            Setters =
            {
                new Setter(Button.MinHeightProperty, 40d),
                new Setter(Button.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(Button.PaddingProperty, new Thickness(14, 8)),
                new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center)
            }
        });
        window.Styles.Add(new Style(s => s.OfType<Button>().Class("voice-ime-link"))
        {
            Setters =
            {
                new Setter(Button.ForegroundProperty, Blue),
                new Setter(Button.BackgroundProperty, Brushes.Transparent),
                new Setter(Button.BorderThicknessProperty, new Thickness(0)),
                new Setter(Button.PaddingProperty, new Thickness(4, 8)),
                new Setter(Button.FontSizeProperty, 12d)
            }
        });
        window.Styles.Add(new Style(s => s.OfType<ComboBox>())
        {
            Setters =
            {
                new Setter(ComboBox.MinHeightProperty, 40d),
                new Setter(ComboBox.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(ComboBox.PaddingProperty, new Thickness(12, 7, 0, 7)),
                new Setter(ComboBox.VerticalContentAlignmentProperty, VerticalAlignment.Center)
            }
        });
        window.Styles.Add(new Style(s => s.OfType<NumericUpDown>())
        {
            Setters =
            {
                new Setter(NumericUpDown.MinHeightProperty, 40d),
                new Setter(NumericUpDown.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(NumericUpDown.VerticalContentAlignmentProperty, VerticalAlignment.Center)
            }
        });
        window.Styles.Add(new Style(s => s.OfType<ToggleSwitch>())
        {
            Setters = { new Setter(ToggleSwitch.MinHeightProperty, 40d) }
        });
    }

    internal static Border Header(Window window, string title, string subtitle, Control? aside = null, bool compact = false)
    {
        var paleBlue = new SolidColorBrush(Color.Parse("#B8CEE9"));
        var heading = Text(title, 26, Brushes.White);
        heading.FontWeight = FontWeight.SemiBold;
        var drag = new Border
        {
            Name = "VoiceImeTitleDragArea", Height = 38, Background = Brushes.Transparent,
            Padding = new Thickness(24, 0),
            Child = new TextBlock
            {
                Text = "译见pro / 语音输入法", FontSize = 12, Foreground = paleBlue,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        void ToggleMaximize()
        {
            if (window.CanMaximize)
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        void Drag(object? sender, PointerPressedEventArgs args)
        {
            if (!args.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            if (args.ClickCount == 2) ToggleMaximize();
            else window.BeginMoveDrag(args);
            args.Handled = true;
        }
        drag.PointerPressed += Drag;
        var minimize = CaptionButton("VoiceImeMinimize", "\uE921", "最小化");
        var maximize = CaptionButton("VoiceImeMaximize", "\uE922", "最大化");
        var close = CaptionButton("VoiceImeClose", "\uE8BB", "关闭", true);
        minimize.Click += (_, _) => { if (window.CanMinimize) window.WindowState = WindowState.Minimized; };
        maximize.Click += (_, _) => ToggleMaximize();
        close.Click += (_, _) => window.Close();
        void UpdateButtons()
        {
            var maximized = window.WindowState == WindowState.Maximized;
            ((TextBlock)maximize.Content!).Text = maximized ? "\uE923" : "\uE922";
            var label = maximized ? "还原" : "最大化";
            ToolTip.SetTip(maximize, label);
            AutomationProperties.SetName(maximize, label);
            minimize.IsEnabled = window.CanMinimize;
            maximize.IsEnabled = window.CanMaximize;
        }
        window.PropertyChanged += (_, change) =>
        {
            if (change.Property == Window.WindowStateProperty || change.Property == Window.CanMaximizeProperty
                || change.Property == Window.CanMinimizeProperty) UpdateButtons();
        };
        UpdateButtons();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { minimize, maximize, close } };
        var titleBar = new Grid { Name = "VoiceImeTitleBar", ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleBar.Children.Add(drag);
        Grid.SetColumn(buttons, 1);
        titleBar.Children.Add(buttons);
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16,
            Margin = new Thickness(24, 0, 24, 16)
        };
        content.Children.Add(new StackPanel { Spacing = 5, Children = { heading, Text(subtitle, 13, paleBlue) } });
        if (aside != null)
        {
            Grid.SetColumn(aside, 1);
            content.Children.Add(aside);
        }
        content.PointerPressed += Drag;
        var header = new Border
        {
            Name = "VoiceImePageHeader",
            Background = Ink,
            Child = new StackPanel
            {
                Children = { titleBar }
            }
        };
        if (!compact) ((StackPanel)header.Child!).Children.Add(content);
        return header;
    }

    internal static Button IconButton(string name, string glyph, string label)
    {
        var button = new Button
        {
            Name = name, Width = 32, Height = 32, MinHeight = 0, MinWidth = 0,
            Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Content = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        ToolTip.SetTip(button, label);
        AutomationProperties.SetName(button, label);
        return button;
    }

    internal static Button SheetButton(string name, string label, string glyph)
    {
        var content = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });
        var text = new TextBlock
        {
            Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        var button = new Button
        {
            Name = name, Tag = label, Content = content, Height = 56, MinWidth = 0,
            CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
        return button;
    }

    internal static void SelectSheet(Button button, bool selected, string label)
    {
        button.Classes.Set("accent", selected);
        AutomationProperties.SetName(button, label + (selected ? "，已选中" : ""));
    }

    private static Button CaptionButton(string name, string glyph, string label, bool close = false)
    {
        var button = IconButton(name, glyph, label);
        button.Width = 46;
        button.Height = 38;
        button.CornerRadius = new CornerRadius(0);
        button.Foreground = Brushes.White;
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            button.Resources["ButtonForeground" + state] = state == "Disabled" ? Muted : Brushes.White;
            button.Resources["ButtonBorderBrush" + state] = Brushes.Transparent;
            button.Resources["ButtonBackground" + state] = state is "PointerOver" or "Pressed"
                ? new SolidColorBrush(Color.Parse(close ? "#C42B1C" : "#25476F")) : Brushes.Transparent;
        }
        return button;
    }

    private static void UpdateNativeFrame(Window window)
    {
        // BorderOnly 保留系统缩放边框；Win11 上沿的非客户区填色同步到深蓝页头。
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || Design.IsDesignMode
            || window.TryGetPlatformHandle() is not { Handle: var hwnd } || hwnd == IntPtr.Zero) return;
        var color = ((ISolidColorBrush)Ink).Color;
        var colorRef = (uint)(color.R | color.G << 8 | color.B << 16);
        _ = DwmSetWindowAttribute(hwnd, 35, ref colorRef, sizeof(uint));
        _ = DwmSetWindowAttribute(hwnd, 34, ref colorRef, sizeof(uint));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);

    internal static Border Card(Control child, double padding = 20) => new()
    {
        Background = Brushes.White,
        CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(1),
        BorderBrush = Line,
        Padding = new Thickness(padding),
        Child = child
    };

    internal static Border Footer(Control child) => new()
    {
        Background = Brushes.White,
        BorderBrush = Line,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Padding = new Thickness(24, 14),
        Child = child
    };

    internal static TextBlock Text(string text, double size = 14, IBrush? color = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color ?? Ink,
        TextWrapping = TextWrapping.Wrap
    };
}