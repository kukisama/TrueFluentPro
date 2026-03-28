using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using FaIcon = Projektanker.Icons.Avalonia.Icon;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.WebSearch;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{
    public partial class MediaStudioView : UserControl
    {
        private MediaStudioViewModel? _viewModel;
        private MediaSessionViewModel? _attachedSession;
        private ListBox? _sessionListBox;

        // ── 滚动诊断 ──
        private ScrollViewer? _diagScrollViewer;
        private double _diagLastExtentH;
        private int _diagScrollEventCount;
        private DateTime _diagLastLogTime = DateTime.MinValue;

        // ── 懒加载防重入 ──
        private bool _isLoadingOlderMessages;

        private string? _capturedSelection;
        private SelectableTextBlock? _capturedStb;
        private int _capturedSelStart;
        private int _capturedSelEnd;
        private TrueFluentPro.Controls.Markdown.MarkdownRenderer? _capturedRenderer;
        private bool _initialized;

        // ── 高度缓存：防 VirtualizingStackPanel 估算误差 ──
        private ListBox? _messageListBox;

        public MediaStudioView()
        {
            InitializeComponent();
        }

        public void Initialize(
            AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints,
            IModelRuntimeResolver modelRuntimeResolver, IAzureTokenProviderStore azureTokenProviderStore,
            ConfigurationService configurationService, Func<AzureSpeechConfig> configProvider,
            Action<AzureSpeechConfig>? onGlobalConfigUpdated = null)
        {
            if (_initialized) return;
            _initialized = true;

            _viewModel = new MediaStudioViewModel(
                aiConfig,
                genConfig,
                endpoints,
                modelRuntimeResolver,
                azureTokenProviderStore,
                configurationService,
                configProvider,
                onGlobalConfigUpdated);
            DataContext = _viewModel;

            _sessionListBox = this.FindControl<ListBox>("SessionListBox");

            // 配置会话视图缓存：用 SessionId 做 key，缓存已渲染的视觉树
            var sessionContentHost = this.FindControl<Controls.CachedContentControl>("SessionContentHost");
            if (sessionContentHost != null)
            {
                sessionContentHost.KeySelector = content =>
                    (content as MediaSessionViewModel)?.SessionId;
                sessionContentHost.MaxCachedViews = Math.Clamp(
                    genConfig?.MaxLoadedSessionsInMemory ?? 8, 1, 64);
            }

            AttachSession(_viewModel.CurrentSession);

            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MediaStudioViewModel.CurrentSession))
                {
                    AttachSession(_viewModel.CurrentSession);
                }
            };

            // 在隧道阶段订阅 KeyDown，确保在 TextBox 内部处理 Ctrl+V / Ctrl+Enter 之前拦截
            this.AddHandler(
                InputElement.KeyDownEvent,
                PromptTextBox_KeyDown,
                Avalonia.Interactivity.RoutingStrategies.Tunnel);

            // 用 MenuFlyout 替代原生 ContextMenu（ContextMenu 在 NavigationView 内 Popup 不渲染）
            _sessionListBox!.AddHandler(
                InputElement.PointerReleasedEvent,
                SessionListBox_PointerReleased,
                Avalonia.Interactivity.RoutingStrategies.Tunnel);

            // 隧道阶段捕获选中文本，防止右键点击后 SelectableTextBlock 清除选区
            this.AddHandler(
                InputElement.PointerPressedEvent,
                CaptureSelectionBeforeRightClick,
                Avalonia.Interactivity.RoutingStrategies.Tunnel);

            // 隧道阶段拦截右键释放，在 STB 处理 PointerReleased 之前显示菜单并恢复选区
            this.AddHandler(
                InputElement.PointerReleasedEvent,
                HandleRightClickContextMenu,
                Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        public void UpdateConfiguration(AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints,
            string? webSearchProviderId = null, WebSearchTriggerMode? webSearchTriggerMode = null, int? webSearchMaxResults = null,
            bool? webSearchEnableIntentAnalysis = null, bool? webSearchEnableResultCompression = null,
            string? webSearchMcpEndpoint = null, string? webSearchMcpToolName = null, string? webSearchMcpApiKey = null)
        {
            _viewModel?.UpdateConfiguration(aiConfig, genConfig, endpoints,
                webSearchProviderId, webSearchTriggerMode, webSearchMaxResults,
                webSearchEnableIntentAnalysis, webSearchEnableResultCompression,
                webSearchMcpEndpoint, webSearchMcpToolName, webSearchMcpApiKey);
        }

        public void Cleanup()
        {
            _viewModel?.Dispose();
        }

        /// <summary>
        /// 通过视觉树查找当前可见的 chat-message-list ListBox。
        /// CachedContentControl 中可能同时存在多个隐藏的旧会话视觉树，必须过滤 IsEffectivelyVisible。
        /// </summary>
        private ListBox? FindMessageListBox()
        {
            return this.GetVisualDescendants()
                .OfType<ListBox>()
                .FirstOrDefault(lb => lb.Classes.Contains("chat-message-list") && lb.IsEffectivelyVisible);
        }

        /// <summary>
        /// 通过视觉树查找 MessageListBox 内部的 ScrollViewer（ListBox 在 DataTemplate 内，FindControl 无法获取）。
        /// </summary>
        private ScrollViewer? FindChatScrollViewer()
        {
            return FindMessageListBox()
                ?.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
        }

        /// <summary>
        /// 容器回收复用时：恢复缓存高度，防止 VirtualizingStackPanel 用平均值估算。
        /// </summary>
        private void MessageListBox_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            var container = e.Container;
            if (container.DataContext is ChatMessageViewModel msg && !double.IsNaN(msg.CachedRenderHeight))
            {
                // 立即恢复缓存高度，面板 Measure 时就能用到精确值
                container.Height = msg.CachedRenderHeight;
            }
            container.SizeChanged += MessageContainer_SizeChanged;
        }

        /// <summary>
        /// 容器被清理时：取消 SizeChanged 订阅。
        /// </summary>
        private void MessageListBox_ContainerClearing(object? sender, ContainerClearingEventArgs e)
        {
            e.Container.SizeChanged -= MessageContainer_SizeChanged;
            // 清除强制高度，让下一次复用自然布局
            e.Container.Height = double.NaN;
        }

        /// <summary>
        /// 容器尺寸变化时：缓存高度到 ViewModel，并清除强制高度让后续布局自然。
        /// </summary>
        private void MessageContainer_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (sender is Control container && container.DataContext is ChatMessageViewModel msg)
            {
                var newH = e.NewSize.Height;
                if (newH > 0 && Math.Abs(newH - msg.CachedRenderHeight) > 1)
                {
                    msg.CachedRenderHeight = newH;
                }
                // 首次渲染后清除强制高度，使编辑/折叠等操作能自然改变高度
                if (!double.IsNaN(container.Height))
                {
                    container.Height = double.NaN;
                }
            }
        }

        private void AttachSession(MediaSessionViewModel? session)
        {
            if (ReferenceEquals(_attachedSession, session))
                return;

            // 解除旧诊断订阅
            if (_diagScrollViewer != null)
            {
                _diagScrollViewer.ScrollChanged -= DiagScrollViewer_ScrollChanged;
                _diagScrollViewer = null;
            }

            // 解除旧会话的 CollectionChanged 订阅
            if (_attachedSession != null)
                _attachedSession.Messages.CollectionChanged -= OnAttachedSessionMessagesReset;

            _attachedSession = session;

            if (_attachedSession == null)
                return;

            // 判断是否缓存命中（CachedContentControl 已保留滚动位置）
            var sessionContentHost = this.FindControl<Controls.CachedContentControl>("SessionContentHost");
            var isCacheHit = sessionContentHost?.LastSwitchWasCacheHit ?? false;

            // 通用策略：监听 Messages 集合重置（ReplaceAllMessages 会 Clear + Add）
            // 无论 HIT/MISS，只要 Messages 从空→有数据，就 scroll-to-bottom。
            // 真正的缓存 HIT 且数据未卸载时，CollectionChanged 不会触发，滚动位置自然保持。
            _attachedSession.Messages.CollectionChanged += OnAttachedSessionMessagesReset;

            // 延迟到视觉树就绪后订阅
            Dispatcher.UIThread.Post(() =>
            {
                // 订阅 ListBox 容器生命周期事件，实现高度缓存
                var newListBox = FindMessageListBox();
                if (newListBox != null && !ReferenceEquals(newListBox, _messageListBox))
                {
                    if (_messageListBox != null)
                    {
                        _messageListBox.ContainerPrepared -= MessageListBox_ContainerPrepared;
                        _messageListBox.ContainerClearing -= MessageListBox_ContainerClearing;
                    }
                    _messageListBox = newListBox;
                    _messageListBox.ContainerPrepared += MessageListBox_ContainerPrepared;
                    _messageListBox.ContainerClearing += MessageListBox_ContainerClearing;
                }

                _diagScrollViewer = FindChatScrollViewer();
                if (_diagScrollViewer != null)
                {
                    _diagScrollEventCount = 0;
                    _diagLastExtentH = _diagScrollViewer.Extent.Height;
                    _diagScrollViewer.ScrollChanged += DiagScrollViewer_ScrollChanged;
                }

                // 缓存未命中 且 已有数据 → 立即滚到底（首会话在启动时已加载）
                if (!isCacheHit && session!.Messages.Count > 0)
                {
                    ScrollToBottomReliable();
                }
                // 其余情况（MISS+空数据、HIT+数据被卸载）由 OnAttachedSessionMessagesReset 兜底
            }, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 通用 CollectionChanged 监听：检测 Messages 经历 Reset（Clear+批量 Add）后，
        /// 在布局完成时 scroll-to-bottom。覆盖所有"数据延迟加载"场景。
        /// 只响应一次 Reset 周期，避免向上滚动加载时误触发。
        /// </summary>
        private bool _pendingScrollAfterReset;

        private void OnAttachedSessionMessagesReset(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Reset = Clear 操作，标记"下次有数据时需要 scroll-to-bottom"
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                _pendingScrollAfterReset = true;
                return;
            }

            // Add 操作 + 有待处理的 Reset → 数据填充完成，安排 scroll
            if (_pendingScrollAfterReset && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && _attachedSession?.Messages.Count > 0)
            {
                _pendingScrollAfterReset = false;
                Dispatcher.UIThread.Post(() => ScrollToBottomReliable(), DispatcherPriority.Loaded);
            }
        }

        /// <summary>可靠滚到底：双轮 ScrollToEnd，覆盖 StackPanel 布局延迟。</summary>
        private void ScrollToBottomReliable()
        {
            var sv = FindChatScrollViewer();
            if (sv == null) return;

            var lb = FindMessageListBox();
            if (lb != null && lb.ItemCount > 0)
                lb.ScrollIntoView(lb.ItemCount - 1);
            sv.ScrollToEnd();

            Dispatcher.UIThread.Post(() =>
            {
                if (lb != null && lb.ItemCount > 0)
                    lb.ScrollIntoView(lb.ItemCount - 1);
                sv.ScrollToEnd();
            }, DispatcherPriority.Render);
        }

        private void DiagScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_diagScrollViewer == null || _attachedSession == null) return;

            var offsetY = _diagScrollViewer.Offset.Y;
            var extentH = _diagScrollViewer.Extent.Height;
            var viewportH = _diagScrollViewer.Viewport.Height;

            // ── 向上滚动接近顶部 → 加载更多旧消息 ──
            if (offsetY < viewportH * 0.5 && _attachedSession.HasMoreMessages && !_isLoadingOlderMessages)
            {
                _isLoadingOlderMessages = true;

                // 记住当前锚点：最顶部可见项及其相对偏移
                var oldExtent = extentH;

                var loaded = _attachedSession.LoadOlderMessages();
                if (loaded > 0)
                {
                    // 布局后补偿滚动位置，使用户看到的内容不跳动
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_diagScrollViewer != null)
                        {
                            var newExtent = _diagScrollViewer.Extent.Height;
                            var delta = newExtent - oldExtent;
                            if (delta > 0)
                            {
                                _diagScrollViewer.Offset = new Vector(
                                    _diagScrollViewer.Offset.X,
                                    _diagScrollViewer.Offset.Y + delta);
                            }
                        }
                        _isLoadingOlderMessages = false;
                    }, DispatcherPriority.Render);
                }
                else
                {
                    _isLoadingOlderMessages = false;
                }
            }

            // ── 诊断日志（降频）──
            _diagScrollEventCount++;
            var now = DateTime.UtcNow;
            var extentDelta = extentH - _diagLastExtentH;
            if ((now - _diagLastLogTime).TotalMilliseconds >= 200 || Math.Abs(extentDelta) > 1)
            {
                Helpers.ScrollDiagLog.Log($"[ScrollDiag] #{_diagScrollEventCount} OffsetY={offsetY:F0} Extent={extentH:F0} ExtentΔ={extentDelta:+0.0;-0.0;0} Viewport={viewportH:F0} Ratio={offsetY / Math.Max(1, extentH - viewportH):P0}");
                _diagLastLogTime = now;
            }
            _diagLastExtentH = extentH;
        }

        private void ScrollToTop_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var sv = FindChatScrollViewer();
            if (sv != null)
                sv.Offset = new Vector(sv.Offset.X, 0);
            e.Handled = true;
        }

        private void ScrollToBottom_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var sv = FindChatScrollViewer();
            sv?.ScrollToEnd();
            Dispatcher.UIThread.Post(() =>
            {
                sv?.ScrollToEnd();
            }, DispatcherPriority.Render);
            e.Handled = true;
        }

        private void ToggleReasoning_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is Avalonia.Controls.Button btn && btn.DataContext is ChatMessageViewModel msg)
            {
                msg.IsReasoningExpanded = !msg.IsReasoningExpanded;
            }
        }

        private async void PromptTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            // Ctrl+C：优先处理跨块选择复制
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (TryCopyCrossBlockSelection())
                {
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl+F：打开/关闭对话搜索
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _viewModel?.CurrentSession?.ToggleSearch();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (await TryAttachReferenceImageFromClipboardAsync())
                {
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (_viewModel?.CurrentSession?.GenerateCommand.CanExecute(null) == true)
                {
                    _viewModel.CurrentSession.GenerateCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        /// <summary>
        /// 如果当前有管理选择，复制（纯文本 + HTML）到剪贴板并返回 true。
        /// </summary>
        private bool TryCopyCrossBlockSelection()
        {
            // 在整个 View 中遍历查找有管理选择的 MarkdownRenderer
            foreach (var renderer in this.GetVisualDescendants().OfType<TrueFluentPro.Controls.Markdown.MarkdownRenderer>())
            {
                if (renderer.HasCrossBlockSelection)
                {
                    var text = renderer.GetSelectedText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _ = CopyTextWithHtmlAsync(text);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 同时将纯文本和 HTML 格式放入剪贴板（粘贴到富文本编辑器时保留格式）。
        /// </summary>
        private async Task CopyTextWithHtmlAsync(string plainText)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;

            var html = Markdig.Markdown.ToHtml(plainText);
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(plainText));
            data.Add(DataTransferItem.Create(
                DataFormat.CreateStringPlatformFormat("text/html"), html));
            await clipboard.SetDataAsync(data);
        }

        private async void AttachReferenceImage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_viewModel?.CurrentSession == null)
            {
                return;
            }

            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider == null)
            {
                return;
            }

            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择参考图",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图片")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif" }
                    }
                }
            });

            if (files == null || files.Count == 0)
            {
                return;
            }

            foreach (var file in files)
            {
                var localPath = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(localPath))
                {
                    continue;
                }

                await _viewModel.CurrentSession.SetReferenceImageFromFileAsync(localPath);
            }
        }

        private async Task<bool> TryAttachReferenceImageFromClipboardAsync()
        {
            if (_viewModel?.CurrentSession == null)
            {
                return false;
            }

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null)
            {
                return false;
            }

            using (var bitmap = await clipboard.TryGetBitmapAsync())
            {
                if (bitmap != null)
                {
                    var tempPngPath = Path.Combine(Path.GetTempPath(), $"refclip_{Guid.NewGuid():N}.png");
                    try
                    {
                        bitmap.Save(tempPngPath, 100);
                        return await _viewModel.CurrentSession.SetReferenceImageFromFileAsync(tempPngPath);
                    }
                    finally
                    {
                        try
                        {
                            if (File.Exists(tempPngPath))
                            {
                                File.Delete(tempPngPath);
                            }
                        }
                        catch
                        {
                            // ignore temp cleanup errors
                        }
                    }
                }
            }

            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var candidates = text
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().Trim('"'))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var attachedAny = false;
                foreach (var candidate in candidates)
                {
                    if (!File.Exists(candidate) || !IsImageFile(candidate))
                    {
                        continue;
                    }

                    attachedAny |= await _viewModel.CurrentSession.SetReferenceImageFromFileAsync(candidate);
                }

                if (attachedAny)
                    return true;
            }

            if (OperatingSystem.IsWindows())
            {
                return await TryAttachReferenceImageFromWindowsClipboardBitmapAsync();
            }

            return false;
        }

        private async Task<bool> TryAttachReferenceImageFromWindowsClipboardBitmapAsync()
        {
            if (_viewModel?.CurrentSession == null)
            {
                return false;
            }

            IntPtr gdipToken = IntPtr.Zero;
            IntPtr gdipBitmap = IntPtr.Zero;
            string? tempPngPath = null;

            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    return false;
                }

                try
                {
                    var hBitmap = GetClipboardData(CF_BITMAP);
                    if (hBitmap == IntPtr.Zero)
                    {
                        return false;
                    }

                    var startupInput = new GdiplusStartupInput { GdiplusVersion = 1 };
                    if (GdiplusStartup(out gdipToken, ref startupInput, IntPtr.Zero) != 0)
                    {
                        return false;
                    }

                    if (GdipCreateBitmapFromHBITMAP(hBitmap, IntPtr.Zero, out gdipBitmap) != 0)
                    {
                        return false;
                    }

                    tempPngPath = Path.Combine(Path.GetTempPath(), $"refclip_{Guid.NewGuid():N}.png");
                    var pngEncoderClsid = new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");
                    if (GdipSaveImageToFile(gdipBitmap, tempPngPath, ref pngEncoderClsid, IntPtr.Zero) != 0)
                    {
                        return false;
                    }
                }
                finally
                {
                    CloseClipboard();
                }

                if (string.IsNullOrWhiteSpace(tempPngPath) || !File.Exists(tempPngPath))
                {
                    return false;
                }

                return await _viewModel.CurrentSession.SetReferenceImageFromFileAsync(tempPngPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"从 Windows 剪贴板读取图片失败: {ex.Message}");
                return false;
            }
            finally
            {
                if (gdipBitmap != IntPtr.Zero)
                {
                    GdipDisposeImage(gdipBitmap);
                }

                if (gdipToken != IntPtr.Zero)
                {
                    GdiplusShutdown(gdipToken);
                }

                if (!string.IsNullOrWhiteSpace(tempPngPath))
                {
                    try
                    {
                        File.Delete(tempPngPath);
                    }
                    catch
                    {
                        // ignore temp cleanup errors
                    }
                }
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("gdiplus.dll")]
        private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

        [DllImport("gdiplus.dll")]
        private static extern void GdiplusShutdown(IntPtr token);

        [DllImport("gdiplus.dll")]
        private static extern int GdipCreateBitmapFromHBITMAP(IntPtr hbm, IntPtr hpal, out IntPtr bitmap);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
        private static extern int GdipSaveImageToFile(IntPtr image, string filename, ref Guid clsidEncoder, IntPtr encoderParams);

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiplusStartupInput
        {
            public uint GdiplusVersion;
            public IntPtr DebugEventCallback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;
        }

        private const uint CF_BITMAP = 2;

        private void SessionListBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.F2)
            {
                _ = StartRenameSessionAsync();
                e.Handled = true;
            }
        }

        private void SessionItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control control)
                return;

            if (control.DataContext is not MediaSessionViewModel session)
                return;

            var point = e.GetCurrentPoint(control);
            if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
            {
                if (_viewModel != null)
                    _viewModel.CurrentSession = session;
            }
        }

        private void RenameSession_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _ = StartRenameSessionAsync();
        }

        private void ResumeVideoTasks_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_viewModel?.CurrentSession == null) return;

            var session = _viewModel.CurrentSession;
            var cancelledTasks = session.TaskHistory
                .Where(t => t.Type == MediaGenType.Video
                    && (t.Status == MediaGenStatus.Cancelled || t.Status == MediaGenStatus.Failed)
                    && !string.IsNullOrEmpty(t.RemoteVideoId))
                .ToList();

            if (cancelledTasks.Count == 0)
            {
                return;
            }

            foreach (var task in cancelledTasks)
            {
                session.ResumeVideoTask(task);
            }
        }

        private void DeleteSession_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            var sessionListBox = this.FindControl<ListBox>("SessionListBox");
            var target = sessionListBox?.SelectedItem as MediaSessionViewModel
                ?? _viewModel.CurrentSession;
            if (target == null)
            {
                return;
            }

            if (_viewModel.DeleteSessionCommand.CanExecute(target))
            {
                _viewModel.DeleteSessionCommand.Execute(target);
                e.Handled = true;
            }
        }

        private void DeleteChatMessage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_viewModel?.CurrentSession == null) return;
            if (sender is not MenuItem mi) return;
            if (mi.Tag is not ChatMessageViewModel msg) return;

            var cmd = _viewModel.CurrentSession.DeleteMessageCommand;
            if (cmd.CanExecute(msg))
            {
                cmd.Execute(msg);
                e.Handled = true;
            }
        }

        private async Task StartRenameSessionAsync()
        {
            if (_viewModel?.CurrentSession == null) return;

            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            if (parentWindow == null) return;

            var currentName = _viewModel.CurrentSession.SessionName;

            var dialog = new Window
            {
                Title = "重命名会话",
                Width = 360,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                ShowInTaskbar = false
            };

            var textBox = new TextBox
            {
                Text = currentName,
                Margin = new Thickness(16, 16, 16, 8),
                Watermark = "输入新名称..."
            };

            var okButton = new Button
            {
                Content = "确定",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Padding = new Thickness(20, 6),
                IsDefault = true
            };

            var cancelButton = new Button
            {
                Content = "取消",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Padding = new Thickness(20, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };

            string? result = null;
            okButton.Click += (_, _) =>
            {
                result = textBox.Text?.Trim();
                dialog.Close();
            };
            cancelButton.Click += (_, _) => dialog.Close();

            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    result = textBox.Text?.Trim();
                    dialog.Close();
                    e.Handled = true;
                }
            };

            var buttonPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(16, 0, 16, 16),
                Spacing = 8
            };
            buttonPanel.Children.Add(cancelButton);
            buttonPanel.Children.Add(okButton);

            var stack = new StackPanel();
            stack.Children.Add(textBox);
            stack.Children.Add(buttonPanel);
            dialog.Content = stack;

            dialog.Opened += (_, _) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

            await dialog.ShowDialog(parentWindow);

            if (!string.IsNullOrWhiteSpace(result) && result != currentName)
            {
                _viewModel.RenameCurrentSession(result);
            }
        }

        private void MediaThumbnail_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.ClickCount >= 1 && sender is Border border)
            {
                var filePath = border.DataContext as string;
                if (border.Tag is ChatMessageViewModel message)
                {
                    OpenImagePreview(message.MediaPaths, filePath);
                    e.Handled = true;
                }
                else if (!string.IsNullOrWhiteSpace(filePath))
                {
                    OpenImagePreview(new[] { filePath }, filePath);
                    e.Handled = true;
                }
            }
        }

        private async void ReferenceImageThumbnail_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_viewModel?.CurrentSession == null)
                return;

            var session = _viewModel.CurrentSession;
            if (!session.IsVideoMode)
                return;

            if (sender is not Border border)
                return;

            if (border.DataContext is not string filePath || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            if (!session.TryGetCurrentVideoTargetSize(out var targetWidth, out var targetHeight))
            {
                session.StatusText = "当前视频参数组合无可用尺寸映射";
                return;
            }

            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            if (parentWindow == null) return;

            var cropWindow = new ReferenceImageCropWindow(filePath, targetWidth, targetHeight);
            var result = await cropWindow.ShowDialog<bool>(parentWindow);
            if (!result)
                return;

            session.NotifyReferenceImageUpdated(filePath);
            session.StatusText = $"已将参考图裁切为 {targetWidth}×{targetHeight}";
            e.Handled = true;
        }

        private void OpenImagePreview(System.Collections.Generic.IReadOnlyList<string> mediaPaths, string? filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            if (VideoFrameExtractorService.TryResolveVideoPathFromFirstFrame(filePath, out var videoPath))
            {
                OpenExternalFile(videoPath);
                return;
            }

            var imagePaths = mediaPaths
                .Where(IsImageFile)
                .ToList();

            if (imagePaths.Count == 0)
            {
                OpenExternalFile(filePath);
                return;
            }

            var index = imagePaths.IndexOf(filePath);
            if (index < 0)
            {
                index = 0;
            }

            var previewWindow = new ImagePreviewWindow(imagePaths, index);
            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            if (parentWindow != null)
                previewWindow.Show(parentWindow);
            else
                previewWindow.Show();
        }

        private static bool IsImageFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
        }

        private static void OpenExternalFile(string filePath)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        // ===== MenuFlyout 右键菜单（替代原生 ContextMenu，解决 NavigationView 内 Popup 不渲染问题）=====

        private void SessionListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            if (_viewModel?.CurrentSession == null) return;

            var flyout = new MenuFlyout();

            var renameItem = new MenuItem { Header = "重命名 (F2)" };
            renameItem.Click += RenameSession_Click;
            flyout.Items.Add(renameItem);

            var resumeItem = new MenuItem { Header = "恢复已取消的视频作业" };
            resumeItem.Click += ResumeVideoTasks_Click;
            flyout.Items.Add(resumeItem);

            flyout.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = "删除会话" };
            deleteItem.Click += DeleteSession_Click;
            flyout.Items.Add(deleteItem);

            flyout.ShowAt(_sessionListBox!, true);
            e.Handled = true;
        }

        private void CaptureSelectionBeforeRightClick(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
            _capturedSelection = null;
            _capturedStb = null;
            _capturedRenderer = null;
            _capturedSelStart = 0;
            _capturedSelEnd = 0;

            var hit = this.InputHitTest(e.GetPosition(this)) as Visual;

            // 优先：从命中点向上查找有管理选择的 MarkdownRenderer
            for (var v = hit; v != null; v = v.GetVisualParent() as Visual)
            {
                if (v is TrueFluentPro.Controls.Markdown.MarkdownRenderer renderer && renderer.HasCrossBlockSelection)
                {
                    _capturedSelection = renderer.GetSelectedText();
                    _capturedRenderer = renderer;
                    e.Handled = true;
                    return;
                }
            }

            // Fallback：walk-up 未找到（右键可能在选区外但同一消息内），
            // 搜索所有 renderer——静态跟踪保证最多只有一个有选区
            foreach (var renderer in this.GetVisualDescendants()
                         .OfType<TrueFluentPro.Controls.Markdown.MarkdownRenderer>())
            {
                if (renderer.HasCrossBlockSelection)
                {
                    _capturedSelection = renderer.GetSelectedText();
                    _capturedRenderer = renderer;
                    e.Handled = true;
                    return;
                }
            }

            // 回退：从命中点向上查找 SelectableTextBlock（单块选择 / 用户消息）
            for (var v = hit; v != null; v = v.GetVisualParent() as Visual)
            {
                if (v is SelectableTextBlock stb)
                {
                    _capturedSelection = stb.SelectedText;
                    _capturedStb = stb;
                    _capturedSelStart = stb.SelectionStart;
                    _capturedSelEnd = stb.SelectionEnd;
                    e.Handled = true;
                    return;
                }
            }
        }

        /// <summary>
        /// 隧道阶段拦截右键释放：当右键命中 STB 时，在 STB 处理 PointerReleased 之前
        /// 显示右键菜单并恢复选区高亮。
        /// 若右键未命中 STB（如点击时间戳），不拦截，让 XAML PointerReleased 正常触发。
        /// </summary>
        private void HandleRightClickContextMenu(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;

            // 优先处理管理选择模式（通过 renderer 定位 Border/msg）
            if (_capturedRenderer != null)
            {
                Border? msgBorder = null;
                ChatMessageViewModel? msg = null;
                for (var v = _capturedRenderer as Visual; v != null; v = v.GetVisualParent() as Visual)
                {
                    if (v is Border b && b.DataContext is ChatMessageViewModel vm)
                    { msgBorder = b; msg = vm; break; }
                }
                _capturedRenderer = null;
                if (msgBorder != null && msg != null && _viewModel?.CurrentSession != null)
                {
                    e.Handled = true;
                    ShowChatContextMenu(msgBorder, msg);
                }
                return;
            }

            if (_capturedStb == null) return;

            // 从命中的 STB 向上查找 chat-message Border
            Border? border = null;
            ChatMessageViewModel? chatMsg = null;
            for (var v = _capturedStb as Visual; v != null; v = v.GetVisualParent() as Visual)
            {
                if (v is Border b && b.DataContext is ChatMessageViewModel vm)
                { border = b; chatMsg = vm; break; }
            }

            if (border == null || chatMsg == null || _viewModel?.CurrentSession == null)
            {
                _capturedStb = null;
                _capturedSelection = null;
                return;
            }

            e.Handled = true;
            ShowChatContextMenu(border, chatMsg);
        }

        private void ShowChatContextMenu(Border border, ChatMessageViewModel msg)
        {
            var captured = _capturedSelection;
            var stb = _capturedStb;
            var selStart = _capturedSelStart;
            var selEnd = _capturedSelEnd;
            _capturedSelection = null;
            _capturedStb = null;

            var flyout = new MenuFlyout();

            // 有选区时：复制选中（带格式）
            if (!string.IsNullOrEmpty(captured))
            {
                var selText = captured;
                var copySelItem = new MenuItem { Header = "复制选中" };
                copySelItem.Click += async (_, _) => await CopyTextWithHtmlAsync(selText);
                flyout.Items.Add(copySelItem);
            }

            // 复制全文（始终显示）
            var copyAllItem = new MenuItem { Header = "复制全文" };
            copyAllItem.Click += async (_, _) =>
            {
                if (!string.IsNullOrEmpty(msg.Text))
                    await CopyTextWithHtmlAsync(msg.Text);
            };
            flyout.Items.Add(copyAllItem);

            // 全选（针对当前右键点击的 SelectableTextBlock）
            if (stb != null)
            {
                var selectAllItem = new MenuItem { Header = "全选" };
                var targetStb = stb;
                selectAllItem.Click += (_, _) =>
                {
                    targetStb.SelectionStart = 0;
                    targetStb.SelectionEnd = targetStb.Text?.Length ?? 0;
                };
                flyout.Items.Add(selectAllItem);
            }

            flyout.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = "删除此条记录", Tag = msg };
            deleteItem.Click += DeleteChatMessage_Click;
            flyout.Items.Add(deleteItem);

            flyout.ShowAt(border, true);

            // 恢复选区高亮（flyout 打开后 STB 失焦不影响 Avalonia 绘制选区，
            // 但需重新设置 start/end 以抵消任何清除行为）
            if (stb != null && selStart != selEnd)
            {
                var restoreStb = stb;
                var rs = selStart;
                var re = selEnd;
                Dispatcher.UIThread.Post(() =>
                {
                    restoreStb.SelectionStart = rs;
                    restoreStb.SelectionEnd = re;
                }, DispatcherPriority.Render);
            }
        }

        private void ChatMessageBorder_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // 此方法现在只处理右键未命中 STB 的情况（如时间戳、空白区域）
            // STB 右键已由 HandleRightClickContextMenu 隧道处理器接管
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            if (sender is not Border border) return;
            if (border.DataContext is not ChatMessageViewModel msg) return;
            if (_viewModel?.CurrentSession == null) return;

            var flyout = new MenuFlyout();

            var copyItem = new MenuItem { Header = "复制全文" };
            copyItem.Click += async (_, _) =>
            {
                if (!string.IsNullOrEmpty(msg.Text))
                    await CopyTextWithHtmlAsync(msg.Text);
            };
            flyout.Items.Add(copyItem);

            flyout.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = "删除此条记录", Tag = msg };
            deleteItem.Click += DeleteChatMessage_Click;
            flyout.Items.Add(deleteItem);

            flyout.ShowAt(border, true);
            e.Handled = true;
        }

        // ── 搜索相关事件 ─────────────────────────────────

        private void ToggleSearch_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _viewModel?.CurrentSession?.ToggleSearch();
        }

        private void SearchNext_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _viewModel?.CurrentSession?.SearchNext();
        }

        private void SearchPrevious_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _viewModel?.CurrentSession?.SearchPrevious();
        }

        private async void WebSearchSelector_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not Button button || _viewModel == null)
                return;

            var flyout = new MenuFlyout();
            var menuForeground = ResolveThemeBrush("TextPrimaryBrush", Brushes.Black);

            if (_viewModel.CurrentSession?.EnableWebSearch == true)
            {
                var disableItem = new MenuItem
                {
                    Header = "关闭联网搜索",
                    Foreground = menuForeground,
                    Icon = new FaIcon
                    {
                        Value = "fa-solid fa-link-slash",
                        FontSize = 13,
                        Foreground = ResolveThemeBrush("TextMutedBrush", Brushes.Gray)
                    }
                };
                disableItem.Click += async (_, _) =>
                {
                    await _viewModel.DisableWebSearchAsync();
                };
                flyout.Items.Add(disableItem);
                flyout.Items.Add(new Separator());
            }

            foreach (var (providerId, displayName) in WebSearchProviderFactory.AvailableProviders)
            {
                var isCurrent = string.Equals(providerId, _viewModel.CurrentWebSearchProviderId, StringComparison.OrdinalIgnoreCase);
                var item = new MenuItem
                {
                    Header = isCurrent ? $"✓ {displayName}" : displayName,
                    Foreground = menuForeground,
                    FontWeight = isCurrent ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal,
                    Icon = CreateSearchProviderIcon(providerId, isCurrent)
                };

                item.Click += async (_, _) =>
                {
                    await _viewModel.SelectWebSearchProviderAsync(providerId, enableForCurrentSession: true);
                };

                flyout.Items.Add(item);
            }

            flyout.ShowAt(button, true);
            e.Handled = true;
        }

        private FaIcon CreateSearchProviderIcon(string providerId, bool isCurrent)
        {
            var value = providerId.ToLowerInvariant() switch
            {
                "bing" => "fa-brands fa-microsoft",
                "bing-cn" => "fa-solid fa-earth-asia",
                "google" => "fa-brands fa-google",
                "bing-news" => "fa-regular fa-newspaper",
                "baidu" => "fa-solid fa-paw",
                "duckduckgo" => "fa-solid fa-feather-pointed",
                "mcp" => "fa-solid fa-plug",
                _ => "fa-solid fa-globe"
            };

            var brush = isCurrent
                ? ResolveThemeBrush("AccentBlueBrush", Brushes.DodgerBlue)
                : ResolveThemeBrush("TextMutedBrush", Brushes.Gray);

            return new FaIcon
            {
                Value = value,
                FontSize = 13,
                Foreground = brush
            };
        }

        private IBrush ResolveThemeBrush(string resourceKey, IBrush fallback)
        {
            if (TryGetResource(resourceKey, ActualThemeVariant, out var resource) && resource is IBrush brush)
            {
                return brush;
            }

            return fallback;
        }

        private void SourceButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not ChatMessageViewModel msg || msg.Citations.Count == 0)
                return;

            var flyout = new MenuFlyout();
            flyout.Items.Add(new MenuItem
            {
                Header = $"共 {msg.Citations.Count} 个来源",
                IsEnabled = false
            });
            flyout.Items.Add(new Separator());

            foreach (var citation in msg.Citations)
            {
                var url = citation.Url;
                var item = new MenuItem
                {
                    Header = string.IsNullOrWhiteSpace(citation.Hostname)
                        ? $"[{citation.Number}] {citation.Title}"
                        : $"[{citation.Number}] {citation.Title} · {citation.Hostname}"
                };

                item.Click += (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(url))
                        return;

                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // ignore open failure
                    }
                };

                flyout.Items.Add(item);
            }

            flyout.ShowAt(button, true);
            e.Handled = true;
        }

        private void CloseSearch_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_viewModel?.CurrentSession != null)
                _viewModel.CurrentSession.IsSearchVisible = false;
        }

        // ── 快捷短语事件 ─────────────────────────────────

        private void QuickPhrase_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string phraseContent && _viewModel?.CurrentSession != null)
            {
                _viewModel.CurrentSession.PromptText += phraseContent;
            }
        }

        // ── 拖拽上传事件 ─────────────────────────────────

        private void ChatArea_DragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = DragDropEffects.Copy;
        }

#pragma warning disable CS0618 // DragEventArgs.Data / DataFormats are deprecated but DataTransfer API not yet stable
        private async void ChatArea_Drop(object? sender, DragEventArgs e)
        {
            var session = _viewModel?.CurrentSession;
            if (session == null) return;

            if (e.Data.Contains(DataFormats.Files))
            {
                var files = e.Data.GetFiles();
                if (files != null)
                {
                    foreach (var item in files)
                    {
                        var path = item.Path?.LocalPath;
                        if (string.IsNullOrEmpty(path)) continue;

                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
                        {
                            await session.SetReferenceImageFromFileAsync(path);
                        }
                        else if (ext is ".txt" or ".md" or ".json" or ".csv" or ".log")
                        {
                            try
                            {
                                var content = await File.ReadAllTextAsync(path);
                                if (content.Length > 10000)
                                    content = content[..10000] + "\n...(已截断)";
                                session.PromptText += $"\n--- {Path.GetFileName(path)} ---\n{content}\n";
                            }
                            catch { /* 静默 */ }
                        }
                    }
                }
            }
            else if (e.Data.Contains(DataFormats.Text))
            {
                var text = e.Data.GetText();
                if (!string.IsNullOrEmpty(text))
                    session.PromptText += text;
            }
        }
#pragma warning restore CS0618
    }
}
