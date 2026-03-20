using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Views
{
    public partial class MediaStudioView : UserControl
    {
        private MediaStudioViewModel? _viewModel;
        private MediaSessionViewModel? _attachedSession;
        private ScrollViewer? _chatScrollViewer;
        private ListBox? _sessionListBox;
        private bool _isUserNearBottom = true;
        private const double AutoScrollThreshold = 80;
        private const double QuickNavVisibilityThreshold = 200;

        private string? _capturedSelection;
        private bool _initialized;

        public MediaStudioView()
        {
            InitializeComponent();
        }

        public void Initialize(
            AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints,
            IModelRuntimeResolver modelRuntimeResolver, IAzureTokenProviderStore azureTokenProviderStore)
        {
            if (_initialized) return;
            _initialized = true;

            _viewModel = new MediaStudioViewModel(aiConfig, genConfig, endpoints, modelRuntimeResolver, azureTokenProviderStore);
            DataContext = _viewModel;

            _sessionListBox = this.FindControl<ListBox>("SessionListBox");

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
        }

        public void UpdateConfiguration(AiConfig aiConfig, MediaGenConfig genConfig, List<AiEndpoint> endpoints)
        {
            _viewModel?.UpdateConfiguration(aiConfig, genConfig, endpoints);
        }

        public void Cleanup()
        {
            if (_attachedSession != null)
                _attachedSession.Messages.CollectionChanged -= CurrentSessionMessages_CollectionChanged;

            _viewModel?.Dispose();
        }

        private void ScrollToBottom()
        {
            _chatScrollViewer?.ScrollToEnd();
        }

        private void ScrollToTop()
        {
            if (_chatScrollViewer == null) return;
            _chatScrollViewer.Offset = new Vector(_chatScrollViewer.Offset.X, 0);
        }

        private void AttachSession(MediaSessionViewModel? session)
        {
            if (ReferenceEquals(_attachedSession, session))
                return;

            if (_attachedSession != null)
                _attachedSession.Messages.CollectionChanged -= CurrentSessionMessages_CollectionChanged;

            _attachedSession = session;
            _isUserNearBottom = true;

            if (_attachedSession != null)
            {
                _attachedSession.Messages.CollectionChanged += CurrentSessionMessages_CollectionChanged;
                Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Background);
            }
        }

        private void CurrentSessionMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_chatScrollViewer == null) return;

            if ((e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
                && _isUserNearBottom)
            {
                ScrollToBottom();
            }
        }

        private void ChatScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer viewer) return;

            _chatScrollViewer = viewer;
            _isUserNearBottom = IsNearBottom(viewer);
            UpdateQuickNavButtonsVisibility();
        }

        private static bool IsNearBottom(ScrollViewer scrollViewer)
        {
            var remaining = scrollViewer.Extent.Height - (scrollViewer.Offset.Y + scrollViewer.Viewport.Height);
            return remaining <= AutoScrollThreshold;
        }

        private void UpdateQuickNavButtonsVisibility()
        {
            if (_chatScrollViewer == null) return;

            var topVisible = _chatScrollViewer.Offset.Y > QuickNavVisibilityThreshold;
            var distanceToBottom = _chatScrollViewer.Extent.Height - (_chatScrollViewer.Offset.Y + _chatScrollViewer.Viewport.Height);
            var bottomVisible = distanceToBottom > QuickNavVisibilityThreshold;

            var topButton = _chatScrollViewer.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ScrollToTopButton");
            var bottomButton = _chatScrollViewer.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ScrollToBottomButton");

            if (topButton != null)
                topButton.IsVisible = topVisible;

            if (bottomButton != null)
                bottomButton.IsVisible = bottomVisible;
        }

        private void ScrollToTop_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ScrollToTop();
            e.Handled = true;
        }

        private void ScrollToBottom_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ScrollToBottom();
            e.Handled = true;
        }

        private async void PromptTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
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

            // 从命中点向上查找 SelectableTextBlock，在它处理 PointerPressed 之前保存选区
            var hit = this.InputHitTest(e.GetPosition(this)) as Visual;
            for (var v = hit; v != null; v = v.GetVisualParent() as Visual)
            {
                if (v is SelectableTextBlock stb)
                {
                    _capturedSelection = stb.SelectedText;
                    return;
                }
            }
        }

        private void ChatMessageBorder_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            if (sender is not Border border) return;
            if (border.DataContext is not ChatMessageViewModel msg) return;
            if (_viewModel?.CurrentSession == null) return;

            // 查找 Border 内的 SelectableTextBlock
            var stb = border.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .FirstOrDefault();

            // 使用隧道阶段捕获的选区（右键点击后 SelectableTextBlock 会清除选区）
            var captured = _capturedSelection;
            _capturedSelection = null;

            var flyout = new MenuFlyout();

            // 复制 — 有选中文本时复制选中，否则复制全文
            var copyItem = new MenuItem { Header = "复制" };
            copyItem.Click += async (_, _) =>
            {
                var text = !string.IsNullOrEmpty(captured) ? captured : msg.Text;
                if (!string.IsNullOrEmpty(text))
                    await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(text);
            };
            flyout.Items.Add(copyItem);

            // 全选
            if (stb != null)
            {
                var selectAllItem = new MenuItem { Header = "全选" };
                selectAllItem.Click += (_, _) =>
                {
                    stb.SelectionStart = 0;
                    stb.SelectionEnd = stb.Text?.Length ?? 0;
                };
                flyout.Items.Add(selectAllItem);
            }

            flyout.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = "删除此条记录", Tag = msg };
            deleteItem.Click += DeleteChatMessage_Click;
            flyout.Items.Add(deleteItem);

            flyout.ShowAt(border, true);
            e.Handled = true;
        }
    }
}
