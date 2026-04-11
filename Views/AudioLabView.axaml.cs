using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
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
            Func<AzureSpeechConfig> configProvider)
        {
            if (_initialized) return;
            _initialized = true;
            var vm = new AudioLabViewModel(
                aiInsightService,
                azureTokenProviderStore,
                modelRuntimeResolver,
                speechResourceRuntimeResolver,
                aiAudioTranscriptionService,
                configProvider);
            vm.FilePanelStateChanged += OnFilePanelStateChanged;
            DataContext = vm;
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
    }
}
