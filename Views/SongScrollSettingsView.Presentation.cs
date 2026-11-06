using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace TrueFluentPro.Views;

/// <summary>
/// 只由长卷设置宿主的局部 Style 启用。原子实例不与旧外观共享；不改 Text、Value、
/// IsChecked、SelectedItem、DataContext，也不接触任何配置字号或保存/选择事件。
/// </summary>
public sealed class SongScrollSettingsTypography : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<SongScrollSettingsTypography, Control, bool>("Enabled");

    private static readonly FontFamily Kai = new("STKaiti, KaiTi, 华文楷体, 楷体, SimSun");
    private static readonly FontFamily Code = new("Cascadia Mono, Consolas, Microsoft YaHei UI, monospace");

    static SongScrollSettingsTypography()
    {
        EnabledProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            if (change.GetNewValue<bool>())
            {
                control.Loaded += Normalize;
                if (control.IsLoaded) Apply(control);
            }
            else control.Loaded -= Normalize;
        });
    }

    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);
    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);

    private static void Normalize(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control) Apply(control);
    }

    private static void Apply(Control control)
    {
        // 模板内字体由所属控件继承/TemplateBinding 管理，不更改字形图标或水印的字体绑定。
        if (control.TemplatedParent != null) return;
        var sizeProperty = control is TextBlock ? TextBlock.FontSizeProperty : TemplatedControl.FontSizeProperty;
        var familyProperty = control is TextBlock ? TextBlock.FontFamilyProperty : TemplatedControl.FontFamilyProperty;
        var weightProperty = control is TextBlock ? TextBlock.FontWeightProperty : TemplatedControl.FontWeightProperty;

        // 保留未来原子控件可能提供的动态/预览字号，不把展示值反向写入业务绑定。
        if (BindingOperations.GetBindingExpressionBase(control, sizeProperty) != null
            || BindingOperations.GetBindingExpressionBase(control, familyProperty) != null)
            return;

        // 缓存语义为可检查类名，Detach/重新选择或数据项复用不会重复换算字号。
        var role = control.Classes.Contains("scroll-form-title") ? "title"
            : control.Classes.Contains("scroll-form-heading") ? "heading"
            : control.Classes.Contains("scroll-form-caption") ? "caption"
            : control.Classes.Contains("scroll-form-body") ? "body" : null;
        if (role == null)
        {
            var size = control.GetValue(sizeProperty);
            role = control is not TextBlock ? "body"
                : control.Theme != null || control.Classes.Contains("title-text")
                    || control.Classes.Contains("subtitle-text") || size >= 16 ? "title"
                : control.GetValue(weightProperty) >= FontWeight.SemiBold ? "heading"
                : control.Classes.Contains("caption-text") || control.Classes.Contains("field-note")
                    || control.Classes.Contains("hint-text") || control.Classes.Contains("section-description")
                    || size < 13 ? "caption" : "body";
            control.Classes.Add("scroll-form-" + role);
        }

        // 仅这批私有视图实例的表现属性采用 LocalValue，可靠覆盖原 XAML 的 10/11/13/16。
        // 不使用全局字号覆盖，也不 ClearValue 或重新绑定任意业务属性。
        control.SetValue(sizeProperty, role switch { "title" => 17d, "heading" => 15d, "caption" => 13d, _ => 14d });
        var family = control.GetValue(familyProperty).ToString();
        var technical = control.Classes.Contains("scroll-form-code")
            || family.Contains("Mono", StringComparison.OrdinalIgnoreCase)
            || family.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
            || family.Contains("Courier", StringComparison.OrdinalIgnoreCase)
            || control is TextBox box && (box.Name is "ModelIdTextBox" or "TenantIdTextBox" or "ClientIdTextBox"
                || box.Classes.Contains("stage-key") || box.PasswordChar != '\0'
                || (box.Watermark?.ToString()?.Contains("https://", StringComparison.OrdinalIgnoreCase) ?? false)
                || (box.Watermark?.ToString()?.Contains("api://", StringComparison.OrdinalIgnoreCase) ?? false)
                || (box.Watermark?.ToString()?.Contains("DefaultEndpointsProtocol", StringComparison.Ordinal) ?? false));
        if (technical && !control.Classes.Contains("scroll-form-code")) control.Classes.Add("scroll-form-code");
        control.SetValue(familyProperty, technical ? Code : Kai);
    }

    /// <summary>使用 Avalonia 11.3.11 Fluent 模板实际消费的键；资源仅写入表单宿主。</summary>
    internal static void InstallPalette(IResourceDictionary resources)
    {
        var ink = Brush("#27332F");
        var accent = Brush("#4D7979");
        var paper = Brush("#FFF8EC");
        var border = Brush("#BFB29A");
        var muted = Brush("#797D70");
        var hover = Brush("#184D7979");
        var selected = Brush("#344D7979");
        var transparent = Brushes.Transparent;

        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            var disabled = state == "Disabled";
            var active = state is "PointerOver" or "Pressed";
            foreach (var check in new[] { "Unchecked", "Checked", "Indeterminate" })
            {
                var on = check != "Unchecked";
                resources["CheckBoxForeground" + check + state] = disabled ? muted : ink;
                resources["CheckBoxBackground" + check + state] = transparent;
                resources["CheckBoxBorderBrush" + check + state] = transparent;
                resources["CheckBoxCheckBackgroundFill" + check + state] = on ? (disabled ? muted : accent) : paper;
                resources["CheckBoxCheckBackgroundStroke" + check + state] = disabled ? muted : active || on ? accent : border;
                resources["CheckBoxCheckGlyphForeground" + check + state] = on ? paper : transparent;
            }
            foreach (var selection in new[] { "", "Selected" })
            {
                var suffix = selection + state;
                resources["ComboBoxItemForeground" + suffix] = disabled ? muted : ink;
                resources["ComboBoxItemBackground" + suffix] = disabled ? transparent : selection.Length > 0 ? selected : active ? hover : transparent;
                resources["ComboBoxItemBorderBrush" + suffix] = transparent;
            }
            foreach (var check in new[] { "", "Checked", "Indeterminate" })
            {
                resources["ToggleButtonForeground" + check + state] = disabled ? muted : ink;
                resources["ToggleButtonBackground" + check + state] = check.Length > 0 ? selected : active ? hover : paper;
                resources["ToggleButtonBorderBrush" + check + state] = disabled ? border : accent;
            }
            resources["ToggleSwitchFillOn" + state] = disabled ? muted : accent;
            resources["ToggleSwitchStrokeOn" + state] = disabled ? muted : accent;
            resources["ToggleSwitchFillOff" + state] = paper;
            resources["ToggleSwitchStrokeOff" + state] = disabled ? muted : border;
            resources["ToggleSwitchKnobFillOn" + state] = paper;
            resources["ToggleSwitchKnobFillOff" + state] = disabled ? muted : accent;
        }

        foreach (var state in new[] { "", "PointerOver", "Pressed", "Focused", "FocusedPressed", "Disabled" })
        {
            var disabled = state == "Disabled";
            resources["TextControlForeground" + state] = disabled ? muted : ink;
            resources["TextControlBackground" + state] = paper;
            resources["TextControlBorderBrush" + state] = disabled || state.Length == 0 ? border : accent;
            resources["TextControlPlaceholderForeground" + state] = muted;
            resources["ComboBoxForeground" + state] = disabled ? muted : ink;
            resources["ComboBoxPlaceHolderForeground" + state] = muted;
            resources["ComboBoxDropDownGlyphForeground" + state] = disabled ? muted : accent;
            resources["ComboBoxBackground" + state] = paper;
            resources["ComboBoxBorderBrush" + state] = disabled || state.Length == 0 ? border : accent;
        }
        resources["TextControlSelectionHighlightColor"] = selected;
        resources["ComboBoxBackgroundUnfocused"] = transparent;
        resources["ComboBoxBackgroundBorderBrushUnfocused"] = border;
        resources["ComboBoxBackgroundBorderBrushFocused"] = accent;
        resources["ComboBoxDropDownBackground"] = paper;
        resources["ComboBoxDropDownBorderBrush"] = border;
        resources["SystemControlHighlightAltBaseHighBrush"] = ink;
        resources["SystemControlHighlightListLowBrush"] = hover;
        resources["SystemControlHighlightListMediumBrush"] = selected;
        resources["SystemControlHighlightListAccentLowBrush"] = selected;
        resources["SystemControlHighlightListAccentMediumBrush"] = selected;
        resources["SystemControlHighlightListAccentHighBrush"] = selected;
        resources["SystemControlFocusVisualPrimaryBrush"] = accent;
        resources["SystemControlFocusVisualSecondaryBrush"] = paper;
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}