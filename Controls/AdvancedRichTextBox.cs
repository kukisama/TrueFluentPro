using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System.ComponentModel;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using System;

namespace TrueFluentPro.Controls
{
    public class AdvancedRichTextBox : UserControl, INotifyPropertyChanged
    {
        private readonly string _editorName;
        private static int _instanceCounter = 0;
        private TextEditor _textEditor = null!;
        private StackPanel _toolbar = null!;
   
        private ComboBox _fontSizeCombo = null!;

        /// <summary>默认字号，可从配置加载</summary>
        public static int DefaultFontSizeValue { get; set; } = 38;

        public new event PropertyChangedEventHandler? PropertyChanged;

        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<AdvancedRichTextBox, string>(nameof(Text), "");

        public static readonly StyledProperty<string> PlaceholderProperty =
            AvaloniaProperty.Register<AdvancedRichTextBox, string>(nameof(Placeholder), "");
        public AdvancedRichTextBox()
        {
            _instanceCounter++;
            _editorName = $"AdvancedEditor_{_instanceCounter}";
            System.Diagnostics.Debug.WriteLine($"创建新的AdvancedRichTextBox实例: {_editorName}");

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            var dockPanel = new DockPanel();

            CreateToolbar();

            // 直接设置工具栏为顶部停靠，无需标题栏
            DockPanel.SetDock(_toolbar, Dock.Top);
            dockPanel.Children.Add(_toolbar);

            _textEditor = new TextEditor
            {
                FontFamily = new FontFamily("Consolas, 'Courier New', monospace"),
                FontSize = DefaultFontSizeValue,
                ShowLineNumbers = false,
                WordWrap = true,
                MinHeight = 200,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            try
            {
                _textEditor.SyntaxHighlighting = null;
            }
            catch
            {
            }
 
            _textEditor.Document = new TextDocument();
            _textEditor.TextChanged += OnTextChanged;

            dockPanel.Children.Add(_textEditor);
            Content = dockPanel;

            // 初始应用主题色，并监听主题切换
            ApplyThemeColors();
            ActualThemeVariantChanged += (_, _) => ApplyThemeColors();
        }

        private void ApplyThemeColors()
        {
            var isDark = ActualThemeVariant == ThemeVariant.Dark;
            if (isDark)
            {
                _textEditor.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                _textEditor.Foreground = new SolidColorBrush(Color.FromRgb(229, 231, 235));
                _toolbar.Background = new SolidColorBrush(Color.FromRgb(45, 45, 45));
            }
            else
            {
                _textEditor.Background = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                _textEditor.Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
                _toolbar.Background = new SolidColorBrush(Color.FromRgb(233, 236, 239));
            }
        }
        private void OnTextChanged(object? sender, EventArgs e)
        {
            var newText = _textEditor.Text ?? "";
            if (Text != newText)
            {
                SetValue(TextProperty, newText);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
        private void UpdateEditorText()
        {
            if (_textEditor != null && _textEditor.Text != Text)
            {
                _textEditor.Text = Text;
            }
        }
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == TextProperty)
            {
                UpdateEditorText();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
        public string Placeholder
        {
            get => GetValue(PlaceholderProperty);
            set => SetValue(PlaceholderProperty, value);
        }

        private void CreateToolbar()
        {
            _toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(8, 8, 8, 6),
                MinHeight = 32
            };

            _toolbar.Children.Add(new TextBlock
            {
                Text = "字号:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 2, 0)
            }); _fontSizeCombo = new ComboBox
            {
                Width = 70,
                Height = 30,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = new[] { "24", "26", "28", "30", "32", "34", "36", "38", "40", "42", "44", "46", "48", "50" },
                SelectedItem = DefaultFontSizeValue.ToString()
            };
            _fontSizeCombo.SelectionChanged += OnFontSizeChanged;
            _toolbar.Children.Add(_fontSizeCombo);



        }

        #region 事件处理

        private void OnFontSizeChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_fontSizeCombo.SelectedItem?.ToString() is string sizeStr &&
                double.TryParse(sizeStr, out double size))
            {
                _textEditor.FontSize = size;
            }
        }




        #endregion
    }
}
