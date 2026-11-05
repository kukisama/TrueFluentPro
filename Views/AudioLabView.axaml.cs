using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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
        public AudioLabViewModel? ViewModel => DataContext as AudioLabViewModel;
        private AudioLabViewModel? _boundViewModel;
        private bool _initialized;
        private bool _isExportPickerOpen;

        public Func<string, string, string, Task>? OpenTranscriptChatAsync { get; set; }

        /// <summary>与 MainWindow CompactNavWidth 同值。</summary>
        private const double CompactPanelWidth = 52;
        /// <summary>展开宽度。</summary>
        private const double ExpandedPanelWidth = 200;

        public AudioLabView() : this(true)
        {
        }

        protected AudioLabView(bool loadDefaultView)
        {
            if (loadDefaultView)
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
            IAudioLabExportService exportService,
            IDesktopFolderLauncher folderLauncher,
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
                exportService,
                folderLauncher,
                queueService,
                eventBus);
            BindViewModel(vm);
            _ = vm.RefreshAudioFilesAsync();
        }

        /// <summary>仅绑定呈现层；会话、播放器和控制面板由宿主共享并在最终关闭时释放。</summary>
        public void BindViewModel(AudioLabViewModel vm)
        {
            ArgumentNullException.ThrowIfNull(vm);
            _initialized = true;
            DataContext = vm;
            ObserveViewModel(vm);
            ApplyFilePanelState(vm.IsFilePanelOpen);
        }

        private void ObserveViewModel(AudioLabViewModel? vm)
        {
            if (ReferenceEquals(_boundViewModel, vm)) return;
            if (_boundViewModel != null)
                _boundViewModel.FilePanelStateChanged -= OnFilePanelStateChanged;
            _boundViewModel = vm;
            if (vm != null)
                vm.FilePanelStateChanged += OnFilePanelStateChanged;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            ObserveViewModel(ViewModel);
            ApplyFilePanelState(ViewModel?.IsFilePanelOpen ?? false);
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            ObserveViewModel(ViewModel);
            ApplyFilePanelState(ViewModel?.IsFilePanelOpen ?? false);
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            ObserveViewModel(null);
            // 只解除本视图订阅；不能在换页/换主题时 Dispose 共享 VM。
            base.OnUnloaded(e);
        }

        // ── 空格键 播放/暂停 ─────────────────────────────────
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                // 焦点在文本输入控件时不拦截
                var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                if (focused is TextBox || focused is AutoCompleteBox)
                {
                    base.OnKeyDown(e);
                    return;
                }

                if (ViewModel?.Playback.TogglePlayPauseCommand is { } cmd && cmd.CanExecute(null))
                {
                    cmd.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            base.OnKeyDown(e);
        }

        // ── 文件面板展开/收起（与 MainWindow.ApplyMainNavPaneState 同模式） ──
        private void OnFilePanelStateChanged(bool isOpen)
        {
            ApplyFilePanelState(isOpen);
        }

        protected virtual void ApplyFilePanelState(bool isOpen)
        {
            if (this.FindControl<Border>("FilePanelRail") is { } rail)
                rail.Width = isOpen ? ExpandedPanelWidth : CompactPanelWidth;
        }

        protected void ToggleFilePanel_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsFilePanelOpen = !ViewModel.IsFilePanelOpen;
        }

        // ── 生命周期面板展开/收起 ────────────────────────────
        protected void ToggleLifecyclePanel_Click(object? sender, RoutedEventArgs e)
        {
            if (this.FindControl<Panel>("LifecycleOverlay") is { } overlay)
                overlay.IsVisible = !overlay.IsVisible;
        }

        protected void LifecycleOverlayBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (this.FindControl<Panel>("LifecycleOverlay") is { } overlay)
                overlay.IsVisible = false;
        }

        // ── 标签页切换 ───────────────────────────────────────
        protected void Tab_Summary_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Summary);
        protected void Tab_Transcript_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Transcript);
        protected void Tab_MindMap_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.MindMap);
        protected void Tab_Insight_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Insight);
        protected void Tab_Research_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Research);
        protected void Tab_Podcast_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Podcast);
        protected void Tab_Translation_Click(object? sender, RoutedEventArgs e)
            => SetTab(AudioLabTabKind.Translation);

        protected void Tab_Custom_Click(object? sender, RoutedEventArgs e)
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
        protected async void OpenFile_Click(object? sender, RoutedEventArgs e)
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
                        Patterns = new[] { "*.wav", "*.mp3", "*.m4a", "*.flac", "*.ogg", "*.opus", "*.wma", "*.aac", "*.webm", "*.amr", "*.mp4" }
                    },
                    FilePickerFileTypes.All
                }
            });

            if (files.Count > 0)
            {
                var path = files[0].TryGetLocalPath();
                if (!string.IsNullOrEmpty(path))
                    await LoadAudioFileWithFormatCheckAsync(path);
            }
        }

        public async Task LoadAudioFileWithFormatCheckAsync(string rawPath)
        {
            var viewModel = ViewModel;
            if (viewModel == null || string.IsNullOrWhiteSpace(rawPath))
                return;

            var path = rawPath.Trim().Trim('\u202a', '\u202b', '\u202c', '\u200e', '\u200f', '"');
            if (!File.Exists(path))
            {
                viewModel.StatusMessage = $"音频文件不存在：{path}";
                return;
            }

            if (AudioFormatConverter.IsMicrosoftSpeechSupportedFormat(path, out _))
            {
                viewModel.LoadAudioFile(path);
                return;
            }

            AudioMp3ConversionPlan plan;
            try
            {
                plan = await Task.Run(() => AudioFormatConverter.BuildMp3ConversionPlan(path));
            }
            catch (Exception ex)
            {
                viewModel.StatusMessage = $"读取音频格式信息失败：{ex.Message}";
                return;
            }

            var confirmed = await ShowFormatConversionConfirmDialogAsync(plan);
            if (!confirmed)
            {
                viewModel.StatusMessage = "已取消音频格式转换。";
                return;
            }

            viewModel.IsGenerating = true;
            viewModel.StatusMessage = $"正在转换音频格式：{Path.GetFileName(plan.TargetMp3Path)}（{plan.TargetBitrateKbps} kbps，不高于原始 {plan.SourceBitrateKbps} kbps）...";

            try
            {
                var resultPlan = await AudioFormatConverter.ConvertToMp3CappedBitrateAsync(path, plan);
                viewModel.IsGenerating = false;
                viewModel.StatusMessage = $"格式转换完成：{Path.GetFileName(resultPlan.TargetMp3Path)}（{resultPlan.TargetBitrateKbps} kbps），正在提交转录...";
                viewModel.LoadAudioFile(resultPlan.TargetMp3Path);
            }
            catch (Exception ex)
            {
                viewModel.IsGenerating = false;
                viewModel.StatusMessage = $"音频格式转换失败：{ex.Message}";
            }
        }

        private async Task<bool> ShowFormatConversionConfirmDialogAsync(AudioMp3ConversionPlan plan)
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null)
                return true;

            var confirmed = false;
            var dialog = new Window
            {
                Title = "音频格式不支持直接转录",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var titleBlock = new TextBlock
            {
                Text = "当前音频格式不是微软语音转录直接支持的格式",
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };

            var descBlock = new TextBlock
            {
                Text = $"检测到输入文件为 {plan.SourceFormatLabel} 格式，微软语音转录接口无法直接解码该格式。点击「确定」将开始转换为兼容的 MP3 格式，转换完成后自动以新文件提交转录。",
                FontSize = 13,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            };

            var detailPanel = new StackPanel { Spacing = 6 };
            detailPanel.Children.Add(new TextBlock
            {
                Text = $"原始文件：{Path.GetFileName(plan.SourcePath)}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            detailPanel.Children.Add(new TextBlock
            {
                Text = $"转换后文件：{Path.GetFileName(plan.TargetMp3Path)}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            detailPanel.Children.Add(new TextBlock
            {
                Text = $"码流限制：原始约 {plan.SourceBitrateKbps} kbps → 转换目标 {plan.TargetBitrateKbps} kbps（不超过原始音频码流）",
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap
            });

            var detailBorder = new Border
            {
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = this.TryFindResource("CardBorderBrush", out var borderBrush) && borderBrush is IBrush bb
                    ? bb
                    : new SolidColorBrush(Color.Parse("#33888888")),
                Background = this.TryFindResource("CardBackgroundFillColorDefaultBrush", out var bgBrush) && bgBrush is IBrush bg
                    ? bg
                    : new SolidColorBrush(Color.Parse("#14888888")),
                Child = detailPanel
            };

            var cancelButton = new Button
            {
                Content = "取消",
                Width = 88,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsCancel = true
            };
            cancelButton.Click += (_, _) =>
            {
                confirmed = false;
                dialog.Close();
            };

            var confirmButton = new Button
            {
                Content = "确定",
                Width = 96,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsDefault = true,
                Classes = { "accent" }
            };
            confirmButton.Click += (_, _) =>
            {
                confirmed = true;
                dialog.Close();
            };

            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Children = { cancelButton, confirmButton }
            };

            dialog.Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children = { titleBlock, descBlock, detailBorder, buttonBar }
            };

            await dialog.ShowDialog(owner);
            return confirmed;
        }

        // ── 通用导出 ─────────────────────────────────────────
        protected async void ExportCurrent_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var viewModel = ViewModel;
            if (_isExportPickerOpen || topLevel == null || viewModel == null || string.IsNullOrWhiteSpace(viewModel.CurrentFilePath))
                return;

            _isExportPickerOpen = true;
            try
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择导出目录",
                    AllowMultiple = false
                });

                var directory = folders.FirstOrDefault()?.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    await viewModel.ExportCurrentTabAsync(directory);
                }
                else if (folders.Count > 0)
                {
                    viewModel.StatusMessage = "导出失败：当前仅支持选择本地目录。";
                    viewModel.HasExportFeedback = true;
                }
            }
            catch (Exception ex)
            {
                viewModel.StatusMessage = $"选择导出目录失败：{ex.Message}";
                viewModel.HasExportFeedback = true;
            }
            finally
            {
                _isExportPickerOpen = false;
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

        private async void OnDrop(object? sender, DragEventArgs e)
        {
            if (!e.Data.Contains(DataFormats.Files)) return;

            var files = e.Data.GetFiles()?.ToList();
            if (files == null || files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            e.Handled = true;
            if (!string.IsNullOrEmpty(path))
                await LoadAudioFileWithFormatCheckAsync(path);
        }
#pragma warning restore CS0618

        // ── 转录列表交互 ─────────────────────────────────────

        protected void TranscriptSegment_Tapped(object? sender, TappedEventArgs e)
        {
            if (sender is ListBox lb && lb.SelectedItem is TranscriptSegment seg)
            {
                ViewModel?.Playback.SeekToTime(seg.StartTime);
                ViewModel?.Playback.Play();
            }
        }

        protected void TranscriptSegment_PointerReleased(object? sender, PointerReleasedEventArgs e)
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
            askAiItem.Click += async (_, _) =>
            {
                var vm = ViewModel;
                if (vm == null) return;
                try
                {
                    if (OpenTranscriptChatAsync == null)
                        throw new InvalidOperationException("聊天窗口尚未就绪，请稍后重试。");

                    var transcript = AudioLabExportService.BuildTranscriptMarkdown(
                        Path.GetFileNameWithoutExtension(vm.CurrentFilePath),
                        Path.GetFileName(vm.CurrentFilePath),
                        vm.Segments.ToList());
                    await OpenTranscriptChatAsync(vm.CurrentFilePath, transcript,
                        $"[{segment.TimeText}] {segment.Speaker}: {segment.Text}");
                }
                catch (Exception ex)
                {
                    vm.StatusMessage = $"打开字幕问答失败：{ex.Message}";
                    vm.HasExportFeedback = true;
                }
            };
            flyout.Items.Add(askAiItem);

            flyout.ShowAt(listBox, true);
            e.Handled = true;
        }
    }
}
