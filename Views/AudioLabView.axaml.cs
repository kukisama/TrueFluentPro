using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Speech;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{
    public partial class AudioLabView : UserControl
    {
        private AudioLabViewModel? ViewModel => DataContext as AudioLabViewModel;
        private bool _initialized;

        /// <summary>与 MainWindow CompactNavWidth 同值。</summary>
        private const double CompactPanelWidth = 52;
        /// <summary>展开宽度。</summary>
        private const double ExpandedPanelWidth = 200;

        public AudioLabView()
        {
            InitializeComponent();

            // 拖拽支持
            AddHandler(DragDrop.DropEvent, OnDrop);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            DragDrop.SetAllowDrop(this, true);
        }

        public void Initialize(
            IAiInsightService aiInsightService,
            IAzureTokenProviderStore azureTokenProviderStore,
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IAiAudioTranscriptionService aiAudioTranscriptionService,
            Func<AzureSpeechConfig> configProvider,
            ConfigurationService configService,
            AudioLifecyclePipelineService pipeline,
            AudioLabControlPanelViewModel controlPanelViewModel,
            IAudioTaskQueueService? queueService = null,
            ITaskEventBus? eventBus = null)
        {
            if (_initialized) return;
            _initialized = true;
            var vm = new AudioLabViewModel(
                aiInsightService,
                azureTokenProviderStore,
                modelRuntimeResolver,
                speechResourceRuntimeResolver,
                aiAudioTranscriptionService,
                configProvider,
                configService,
                pipeline,
                controlPanelViewModel,
                queueService,
                eventBus);
            vm.FilePanelStateChanged += OnFilePanelStateChanged;
            DataContext = vm;
            ApplyFilePanelState(vm.IsFilePanelOpen);
            _ = vm.RefreshAudioFilesAsync();
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            ApplyFilePanelState(ViewModel?.IsFilePanelOpen ?? false);
        }

        // ── 文件面板展开/收起（与 MainWindow.ApplyMainNavPaneState 同模式） ──
        private void OnFilePanelStateChanged(bool isOpen)
        {
            ApplyFilePanelState(isOpen);
        }

        private void ApplyFilePanelState(bool isOpen)
        {
            if (FilePanelRail != null)
                FilePanelRail.Width = isOpen ? ExpandedPanelWidth : CompactPanelWidth;
        }

        private void ToggleFilePanel_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsFilePanelOpen = !ViewModel.IsFilePanelOpen;
        }

        // ── 生命周期面板展开/收起 ────────────────────────────
        private void ToggleLifecyclePanel_Click(object? sender, RoutedEventArgs e)
        {
            if (LifecycleOverlay != null)
                LifecycleOverlay.IsVisible = !LifecycleOverlay.IsVisible;
        }

        private void LifecycleOverlayBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (LifecycleOverlay != null)
                LifecycleOverlay.IsVisible = false;
        }

        // ── 标签页切换 ───────────────────────────────────────
        private void Tab_Summary_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Summary);
        private void Tab_Transcript_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Transcript);
        private void Tab_MindMap_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.MindMap);
        private void Tab_Insight_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Insight);
        private void Tab_Research_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Research);
        private void Tab_Podcast_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Podcast);
        private void Tab_Translation_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Translation);

        private void Tab_Custom_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            if (sender is RadioButton rb && rb.Tag is string stageKey)
            {
                ViewModel.CustomStageKey = stageKey;
                ViewModel.SelectedTab = AudioLabTabKind.Custom;
            }
        }

        private void SetTab(AudioLabTabKind tab)
        {
            if (ViewModel != null)
                ViewModel.SelectedTab = tab;
        }

        // ── 打开文件 ─────────────────────────────────────────
        private async void OpenFile_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择音频文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("音频文件")
                    {
                        Patterns = new[] { "*.wav", "*.mp3", "*.m4a", "*.flac", "*.ogg", "*.wma", "*.aac" }
                    },
                    FilePickerFileTypes.All
                }
            });

            if (files.Count > 0)
            {
                var path = files[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                    ViewModel?.LoadAudioFile(path);
            }
        }

        // ── 拖拽 ─────────────────────────────────────────────
#pragma warning disable CS0618 // Data/DataFormats deprecated but DataTransfer API not yet stable
        private void OnDragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (!e.Data.Contains(DataFormats.Files)) return;

            var files = e.Data.GetFiles()?.ToList();
            if (files == null || files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                ViewModel?.LoadAudioFile(path);

            e.Handled = true;
        }
#pragma warning restore CS0618

        // ── 转录列表交互 ─────────────────────────────────────

        private void TranscriptSegment_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (sender is ListBox lb && lb.SelectedItem is TranscriptSegment seg)
            {
                ViewModel?.Playback.SeekToTime(seg.StartTime);
                ViewModel?.Playback.Play();
            }
        }

        private void TranscriptSegment_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            if (sender is not ListBox listBox) return;

            // 命中测试：选中右键点击的项
            var hitItem = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext as TranscriptSegment;
            if (hitItem != null && !ReferenceEquals(listBox.SelectedItem, hitItem))
                listBox.SelectedItem = hitItem;

            var segment = listBox.SelectedItem as TranscriptSegment;
            if (segment == null) return;

            var flyout = new MenuFlyout();

            var playItem = new MenuItem { Header = "跳转并播放" };
            playItem.Click += (_, _) =>
            {
                ViewModel?.Playback.SeekToTime(segment.StartTime);
                ViewModel?.Playback.Play();
            };
            flyout.Items.Add(playItem);

            var copyItem = new MenuItem { Header = "复制字幕文本" };
            copyItem.Click += (_, _) =>
            {
                TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(segment.Text ?? "");
            };
            flyout.Items.Add(copyItem);

            flyout.Items.Add(new Separator());

            var askAiItem = new MenuItem { Header = "以此向AI提问" };
            askAiItem.Click += (_, _) =>
            {
                // 将字幕文本复制到剪贴板，供用户粘贴到创作工坊
                TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(
                    $"[{segment.TimeText}] {segment.Speaker}: {segment.Text}");
            };
            flyout.Items.Add(askAiItem);

            flyout.ShowAt(listBox, true);
            e.Handled = true;
        }
    }
}
