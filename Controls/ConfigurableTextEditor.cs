using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TrueFluentPro.Models;
using System;

namespace TrueFluentPro.Controls
{
    public class ConfigurableTextEditor : UserControl
    {
        private AdvancedRichTextBox? _editor;
        private IDisposable? _textSyncSubscription;

        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<ConfigurableTextEditor, string>(nameof(Text), "");

        public static readonly StyledProperty<string> PlaceholderProperty =
            AvaloniaProperty.Register<ConfigurableTextEditor, string>(nameof(Placeholder), "");

        public static readonly StyledProperty<TextEditorType> EditorTypeProperty =
            AvaloniaProperty.Register<ConfigurableTextEditor, TextEditorType>(nameof(EditorType), TextEditorType.Advanced);

        public ConfigurableTextEditor()
        {
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            EnsureEditorCreatedIfNeeded();
        }

        private void EnsureEditorCreatedIfNeeded()
        {
            if (_editor != null)
            {
                return;
            }

            if (!IsVisible || this.GetVisualRoot() == null)
            {
                return;
            }

            CreateEditor();
        }

        private void CreateEditor()
        {
            _textSyncSubscription?.Dispose();
            _textSyncSubscription = null;

            _editor = new AdvancedRichTextBox();
            Content = _editor;

            _editor.Text = Text;
            _editor.Placeholder = Placeholder;

            // 只订阅一次：AdvancedRichTextBox.TextProperty 变化时回写到本控件的 TextProperty
            _textSyncSubscription = _editor.GetObservable(AdvancedRichTextBox.TextProperty)
                .Subscribe(new TextSyncObserver(this));
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsVisibleProperty)
            {
                if (change.NewValue is bool isVisible && isVisible)
                {
                    EnsureEditorCreatedIfNeeded();
                }
            }
            else if (change.Property == TextProperty)
            {
                if (_editor != null && _editor.Text != Text)
                {
                    _editor.Text = Text;
                }
            }
            else if (change.Property == PlaceholderProperty)
            {
                if (_editor != null)
                {
                    _editor.Placeholder = Placeholder;
                }
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

        public TextEditorType EditorType
        {
            get => GetValue(EditorTypeProperty);
            set => SetValue(EditorTypeProperty, value);
        }

        /// <summary>用于订阅 AdvancedRichTextBox.TextProperty 变化的观察者，避免 lambda 类型不兼容</summary>
        private sealed class TextSyncObserver : IObserver<string>
        {
            private readonly ConfigurableTextEditor _owner;
            public TextSyncObserver(ConfigurableTextEditor owner) => _owner = owner;
            public void OnNext(string value)
            {
                if (_owner.Text != value)
                    _owner.SetValue(TextProperty, value);
            }
            public void OnError(Exception error) { }
            public void OnCompleted() { }
        }
    }
}
