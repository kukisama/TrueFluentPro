using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        private readonly Dictionary<string, ScrollViewer> _sessionScrollViewers = new(StringComparer.OrdinalIgnoreCase);
        private ListBox? _sessionListBox;
        private Control? _sessionViewsHost;
        private bool _isRestoringScroll;
        private bool _isUserNearBottom = true;
        private int _restoreGeneration;
        private int _interactionLockGeneration;
        private bool _interactionsLocked;
        private int _switchTraceSequence;
        private DateTime _suppressScrollSaveUntilUtc = DateTime.MinValue;
        private const double AutoScrollThreshold = 80;
        private const double QuickNavVisibilityThreshold = 200;
        private const int ContentReadyRetryDelayMs = 60;
        private const int SaveSuppressAfterRestoreMs = 350;
        private const int ViewerResolveRetryCount = 30;

        private bool _initialized;

        public MediaStudioView()
        {
            InitializeComponent();
        }

        public void Initialize(AiConfig aiConfig, MediaGenConfig genConfig)
        {
            if (_initialized) return;
            _initialized = true;

            _viewModel = new MediaStudioViewModel(aiConfig, genConfig);
            DataContext = _viewModel;

            _sessionListBox = this.FindControl<ListBox>("SessionListBox");
            _sessionViewsHost = this.FindControl<Control>("SessionViewsHost");

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
        }

        public void Cleanup()
        {
            SaveCurrentSessionScrollState();

            _sessionScrollViewers.Clear();

            if (_attachedSession != null)
            {
                _attachedSession.Messages.CollectionChanged -= CurrentSessionMessages_CollectionChanged;
            }

            _viewModel?.Dispose();
        }

        private ScrollViewer? GetScrollViewerForSession(MediaSessionViewModel? session)
        {
            if (session == null)
            {
                return null;
            }

            return _sessionScrollViewers.TryGetValue(session.SessionId, out var viewer)
                ? viewer
                : null;
        }

        private void ScrollToBottom()
        {
            GetScrollViewerForSession(_attachedSession)?.ScrollToEnd();
        }

        private void ScrollToTop()
        {
            var currentViewer = GetScrollViewerForSession(_attachedSession);
            if (currentViewer == null)
                return;

            currentViewer.Offset = new Vector(currentViewer.Offset.X, 0);
        }

        private void AttachSession(MediaSessionViewModel? session)
        {
            if (ReferenceEquals(_attachedSession, session))
                return;

            var previous = _attachedSession;
            var traceId = Interlocked.Increment(ref _switchTraceSequence);
            LogSwitchAudit(traceId, "Switch.Begin",
                $"from={DescribeSession(previous)} to={DescribeSession(session)}");

            _restoreGeneration++;
            var shouldLockInteractions = previous != null;
            if (shouldLockInteractions)
            {
                LockSessionInteractions(_restoreGeneration);
            }
            else
            {
                UnlockSessionInteractions(_restoreGeneration);
            }

            _chatScrollViewer = GetScrollViewerForSession(_attachedSession);
            SaveCurrentSessionScrollState();

            if (_attachedSession != null)
            {
                _attachedSession.Messages.CollectionChanged -= CurrentSessionMessages_CollectionChanged;
            }

            _attachedSession = session;

            if (_attachedSession != null)
            {
                _attachedSession.Messages.CollectionChanged += CurrentSessionMessages_CollectionChanged;
                _chatScrollViewer = GetScrollViewerForSession(_attachedSession);
                RestoreScrollForSession(_attachedSession, _restoreGeneration, traceId);
            }
            else
            {
                _isUserNearBottom = true;
                UpdateQuickNavButtonsVisibility();
                UnlockSessionInteractions(_restoreGeneration);
            }

            LogSwitchAudit(traceId, "Switch.End",
                $"attached={DescribeSession(_attachedSession)} generation={_restoreGeneration}");
        }

        private void CurrentSessionMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            var currentViewer = GetScrollViewerForSession(_attachedSession);
            if (currentViewer == null || _isRestoringScroll)
                return;

            if ((e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
                && _isUserNearBottom)
            {
                ScrollToBottom();
            }
        }

        private void ChatScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer viewer)
                return;

            if (viewer.DataContext is not MediaSessionViewModel session)
                return;

            _sessionScrollViewers[session.SessionId] = viewer;

            if (!ReferenceEquals(session, _attachedSession))
            {
                return;
            }

            _chatScrollViewer = viewer;

            _isUserNearBottom = IsNearBottom(viewer);

            if (_isRestoringScroll)
            {
                UpdateQuickNavButtonsVisibility();
                return;
            }

            if (DateTime.UtcNow < _suppressScrollSaveUntilUtc)
            {
                UpdateQuickNavButtonsVisibility();
                return;
            }

            if (_attachedSession != null)
            {
                var offsetY = Math.Max(0, viewer.Offset.Y);
                var maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
                var ratio = maxY > 0 ? Math.Clamp(offsetY / maxY, 0, 1) : 0;
                _attachedSession.LastNonBottomScrollOffsetY = offsetY;
                _attachedSession.LastScrollAnchorRatio = ratio;
                _attachedSession.LastScrollSavedMaxY = maxY;

                if (TryCaptureVisibleAnchor(_attachedSession, viewer, out var anchorIndex, out var anchorOffsetY))
                {
                    _attachedSession.LastScrollAnchorMessageIndex = anchorIndex;
                    _attachedSession.LastScrollAnchorViewportOffsetY = anchorOffsetY;
                }
            }

            UpdateQuickNavButtonsVisibility();
        }

        private bool IsNearBottom(ScrollViewer scrollViewer)
        {
            var remaining = scrollViewer.Extent.Height - (scrollViewer.Offset.Y + scrollViewer.Viewport.Height);
            return remaining <= AutoScrollThreshold;
        }

        private void RestoreScrollForSession(MediaSessionViewModel session, int generation, int traceId)
            => RestoreScrollForSession(session, generation, traceId, ViewerResolveRetryCount);

        private void RestoreScrollForSession(MediaSessionViewModel session, int generation, int traceId, int viewerRetries)
        {
            if (!TryResolveSessionViewer(session, generation, traceId, viewerRetries))
            {
                return;
            }

            if (generation != _restoreGeneration || !ReferenceEquals(_attachedSession, session))
            {
                LogSwitchAudit(traceId, "Restore.Skip.Stale",
                    $"session={DescribeSession(session)} generation={generation} currentGeneration={_restoreGeneration}");
                UnlockSessionInteractions(generation);
                return;
            }

            _isRestoringScroll = true;
            LogSwitchAudit(traceId, "Restore.Begin",
                $"session={DescribeSession(session)} hasOffset={session.LastNonBottomScrollOffsetY.HasValue} loaded={session.IsContentLoaded}");

            if (!session.LastNonBottomScrollOffsetY.HasValue)
            {
                _chatScrollViewer?.ScrollToEnd();
                _isUserNearBottom = true;
                UpdateQuickNavButtonsVisibility();
                _suppressScrollSaveUntilUtc = DateTime.UtcNow.AddMilliseconds(SaveSuppressAfterRestoreMs);
                RestoreScrollForSession(session, generation, traceId, 8, 2);
                return;
            }

            RestoreScrollForSession(session, generation, traceId, 16, 0);
        }

        private bool TryResolveSessionViewer(MediaSessionViewModel session, int generation, int traceId, int retries)
        {
            var viewer = GetScrollViewerForSession(session);
            if (viewer != null)
            {
                _chatScrollViewer = viewer;
                return true;
            }

            if (generation != _restoreGeneration || !ReferenceEquals(_attachedSession, session))
            {
                return false;
            }

            if (retries > 0)
            {
                LogSwitchAudit(traceId, "Restore.Wait.Viewer",
                    $"session={DescribeSession(session)} retries={retries}");
                _ = Task.Delay(ContentReadyRetryDelayMs).ContinueWith(_ =>
                {
                    RestoreScrollForSession(session, generation, traceId, retries - 1);
                });
                return false;
            }

            _isRestoringScroll = false;
            UnlockSessionInteractions(generation);
            LogSwitchAudit(traceId, "Restore.Fail.ViewerTimeout",
                $"session={DescribeSession(session)}");
            return false;
        }

        private static bool HasMemoryAnchor(MediaSessionViewModel session)
            => session.LastNonBottomScrollOffsetY.HasValue;

        private void RestoreScrollForSession(
            MediaSessionViewModel session,
            int generation,
            int traceId,
            int retries,
            int passIndex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_chatScrollViewer == null)
                {
                    _isRestoringScroll = false;
                    UnlockSessionInteractions(generation);
                    LogSwitchAudit(traceId, "Restore.Abort.NoScrollViewer",
                        $"session={DescribeSession(session)}");
                    return;
                }

                if (generation != _restoreGeneration || !ReferenceEquals(_attachedSession, session))
                {
                    _isRestoringScroll = false;
                    UnlockSessionInteractions(generation);
                    LogSwitchAudit(traceId, "Restore.Abort.Stale",
                        $"session={DescribeSession(session)} generation={generation} currentGeneration={_restoreGeneration}");
                    return;
                }

                if (!session.IsContentLoaded)
                {
                    if (retries > 0)
                    {
                        LogSwitchAudit(traceId, "Restore.Wait.Content",
                            $"session={DescribeSession(session)} retries={retries}");
                        _ = Task.Delay(ContentReadyRetryDelayMs).ContinueWith(_ =>
                        {
                            RestoreScrollForSession(session, generation, traceId, retries - 1, passIndex);
                        });
                    }
                    else
                    {
                        _isUserNearBottom = true;
                        UpdateQuickNavButtonsVisibility();
                        _isRestoringScroll = false;
                        UnlockSessionInteractions(generation);
                        LogSwitchAudit(traceId, "Restore.Fail.ContentTimeout",
                            $"session={DescribeSession(session)}");
                    }
                    return;
                }

                var maxY = Math.Max(0, _chatScrollViewer.Extent.Height - _chatScrollViewer.Viewport.Height);
                var layoutNotReady = _chatScrollViewer.Extent.Height <= 0 || _chatScrollViewer.Viewport.Height <= 0;
                if (layoutNotReady && retries > 0)
                {
                    LogSwitchAudit(traceId, "Restore.Wait.Layout",
                        $"session={DescribeSession(session)} retries={retries} extent={_chatScrollViewer.Extent.Height:F1} viewport={_chatScrollViewer.Viewport.Height:F1}");
                    _ = Task.Delay(ContentReadyRetryDelayMs).ContinueWith(_ =>
                    {
                        RestoreScrollForSession(session, generation, traceId, retries - 1, passIndex);
                    });
                    return;
                }

                var passName = passIndex switch
                {
                    0 => "First",
                    1 => "Second",
                    _ => "Final"
                };

                if (session.LastNonBottomScrollOffsetY.HasValue)
                {
                    var targetY = Math.Clamp(session.LastNonBottomScrollOffsetY.Value, 0, maxY);
                    _chatScrollViewer.Offset = new Vector(_chatScrollViewer.Offset.X, targetY);
                    LogSwitchAudit(traceId, $"Restore.Step.Offset.{passName}",
                        $"session={DescribeSession(session)} targetY={targetY:F1} maxY={maxY:F1}");
                }
                else
                {
                    _chatScrollViewer.ScrollToEnd();
                    LogSwitchAudit(traceId, $"Restore.Step.Bottom.{passName}",
                        $"session={DescribeSession(session)} maxY={maxY:F1}");
                }

                if (passIndex >= 1)
                {
                    if (TryAlignToAnchorMessage(session, out var appliedDelta))
                    {
                        LogSwitchAudit(traceId, $"Restore.Step.Anchor.{passName}",
                            $"session={DescribeSession(session)} deltaY={appliedDelta:F1} offsetY={_chatScrollViewer.Offset.Y:F1}");
                    }
                    else
                    {
                        LogSwitchAudit(traceId, $"Restore.Step.Anchor.Miss.{passName}",
                            $"session={DescribeSession(session)}");
                    }
                }

                if (passIndex == 0)
                {
                    _suppressScrollSaveUntilUtc = DateTime.UtcNow.AddMilliseconds(SaveSuppressAfterRestoreMs);
                    UnlockSessionInteractions(generation);
                    LogSwitchAudit(traceId, "Restore.Unlock.Early",
                        $"session={DescribeSession(session)} after=First");
                    _ = Task.Delay(0).ContinueWith(_ =>
                    {
                        RestoreScrollForSession(session, generation, traceId, 8, 2);
                    });
                    return;
                }

                _isUserNearBottom = IsNearBottom(_chatScrollViewer);

                if (TryCaptureVisibleAnchor(session, _chatScrollViewer, out var restoredAnchorIndex, out var restoredAnchorOffsetY))
                {
                    session.LastScrollAnchorMessageIndex = restoredAnchorIndex;
                    session.LastScrollAnchorViewportOffsetY = restoredAnchorOffsetY;
                }

                UpdateQuickNavButtonsVisibility();
                _isRestoringScroll = false;
                UnlockSessionInteractions(generation);
                LogSwitchAudit(traceId, "Restore.Done",
                    $"session={DescribeSession(session)} offsetY={_chatScrollViewer.Offset.Y:F1} nearBottom={_isUserNearBottom}");
            }, DispatcherPriority.Background);
        }

        private void LockSessionInteractions(int generation)
        {
            _interactionLockGeneration = generation;
            _interactionsLocked = true;
            if (_sessionListBox != null)
            {
                _sessionListBox.IsHitTestVisible = false;
            }

            if (_sessionViewsHost != null)
            {
                _sessionViewsHost.IsHitTestVisible = false;
            }
        }

        private void UnlockSessionInteractions(int generation)
        {
            if (generation != _interactionLockGeneration)
            {
                return;
            }

            if (!_interactionsLocked)
            {
                return;
            }

            _interactionsLocked = false;

            if (_sessionListBox != null)
            {
                _sessionListBox.IsHitTestVisible = true;
            }

            if (_sessionViewsHost != null)
            {
                _sessionViewsHost.IsHitTestVisible = true;
            }
        }

        private void SaveCurrentSessionScrollState()
        {
            var viewer = GetScrollViewerForSession(_attachedSession);
            if (_attachedSession == null || viewer == null)
                return;

            var offsetY = Math.Max(0, viewer.Offset.Y);
            var maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            var ratio = maxY > 0 ? Math.Clamp(offsetY / maxY, 0, 1) : 0;

            _attachedSession.LastNonBottomScrollOffsetY = offsetY;
            _attachedSession.LastScrollAnchorRatio = ratio;
            _attachedSession.LastScrollSavedMaxY = maxY;

            var hasAnchor = TryCaptureVisibleAnchor(_attachedSession, viewer, out var anchorIndex, out var anchorOffsetY);
            if (hasAnchor)
            {
                _attachedSession.LastScrollAnchorMessageIndex = anchorIndex;
                _attachedSession.LastScrollAnchorViewportOffsetY = anchorOffsetY;
            }

            LogSwitchAudit(_switchTraceSequence, IsNearBottom(viewer) ? "Offset.Save.Bottom" : "Offset.Save",
                $"session={DescribeSession(_attachedSession)} offsetY={offsetY:F1} maxY={maxY:F1} ratio={ratio:F4} hasAnchor={hasAnchor} anchorIdx={(hasAnchor ? anchorIndex.ToString() : "null")} anchorOffY={(hasAnchor ? anchorOffsetY.ToString("F1") : "null")}");
        }

        private static string DescribeSession(MediaSessionViewModel? session)
            => session == null
                ? "<null>"
                : $"{session.SessionId}/{session.SessionName}(loaded={session.IsContentLoaded},msg={session.Messages.Count},task={session.TaskHistory.Count},anchorIdx={session.LastScrollAnchorMessageIndex?.ToString() ?? "null"})";

        private static bool IsSwitchAuditEnabled()
        {
            return AppLogService.IsInitialized && AppLogService.Instance.ShouldLogSuccess;
        }

        private static string GetSwitchAuditPath()
        {
            var logsPath = PathManager.Instance.LogsPath;
            Directory.CreateDirectory(logsPath);
            return Path.Combine(logsPath, "Audit.log");
        }

        private static void LogSwitchAudit(int traceId, string eventName, string message)
        {
            if (!IsSwitchAuditEnabled())
            {
                return;
            }

            _ = AppLogService.Instance.LogAuditAsync("MediaSwitch", traceId, eventName, message);
        }

        private bool TryCaptureVisibleAnchor(MediaSessionViewModel session, ScrollViewer scrollViewer, out int messageIndex, out double viewportOffsetY)
        {
            messageIndex = -1;
            viewportOffsetY = 0;

            var viewportCenterY = scrollViewer.Viewport.Height / 2;
            Border? bestCenterAnchor = null;
            double bestDistance = double.MaxValue;

            foreach (var border in scrollViewer.GetVisualDescendants().OfType<Border>())
            {
                if (border.DataContext is not ChatMessageViewModel)
                {
                    continue;
                }

                if (!border.Classes.Contains("user-msg") && !border.Classes.Contains("ai-msg"))
                {
                    continue;
                }

                var point = border.TranslatePoint(new Point(0, 0), scrollViewer);
                if (!point.HasValue)
                {
                    continue;
                }

                var y = point.Value.Y;
                var h = border.Bounds.Height;
                if (h <= 0)
                {
                    continue;
                }

                var bottom = y + h;
                if (bottom <= 0 || y >= scrollViewer.Viewport.Height)
                {
                    continue;
                }

                var centerY = y + h / 2;
                var distance = Math.Abs(centerY - viewportCenterY);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCenterAnchor = border;
                }
            }

            var anchorBorder = bestCenterAnchor;
            if (anchorBorder?.DataContext is not ChatMessageViewModel anchorMessage)
            {
                return false;
            }

            messageIndex = session.Messages.IndexOf(anchorMessage);
            if (messageIndex < 0)
            {
                return false;
            }

            var anchorPoint = anchorBorder.TranslatePoint(new Point(0, 0), scrollViewer);
            if (!anchorPoint.HasValue)
            {
                return false;
            }

            var anchorCenterY = anchorPoint.Value.Y + anchorBorder.Bounds.Height / 2;
            viewportOffsetY = anchorCenterY - viewportCenterY;
            return true;
        }

        private bool TryAlignToAnchorMessage(MediaSessionViewModel session, out double appliedDelta)
        {
            appliedDelta = 0;
            var viewer = GetScrollViewerForSession(session) ?? _chatScrollViewer;
            if (viewer == null
                || !session.LastScrollAnchorMessageIndex.HasValue
                || !session.LastScrollAnchorViewportOffsetY.HasValue)
            {
                return false;
            }

            var index = session.LastScrollAnchorMessageIndex.Value;
            if (index < 0 || index >= session.Messages.Count)
            {
                return false;
            }

            var anchorMessage = session.Messages[index];
            Border? anchorBorder = null;
            foreach (var border in viewer.GetVisualDescendants().OfType<Border>())
            {
                if (!ReferenceEquals(border.DataContext, anchorMessage))
                {
                    continue;
                }

                if (!border.Classes.Contains("user-msg") && !border.Classes.Contains("ai-msg"))
                {
                    continue;
                }

                anchorBorder = border;
                break;
            }

            if (anchorBorder == null)
            {
                return false;
            }

            var point = anchorBorder.TranslatePoint(new Point(0, 0), viewer);
            if (!point.HasValue)
            {
                return false;
            }

            var viewportCenterY = viewer.Viewport.Height / 2;
            var currentCenterY = point.Value.Y + anchorBorder.Bounds.Height / 2;
            var desiredCenterY = viewportCenterY + session.LastScrollAnchorViewportOffsetY.Value;
            appliedDelta = currentCenterY - desiredCenterY;
            if (Math.Abs(appliedDelta) < 1)
            {
                return true;
            }

            var maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            var targetOffset = Math.Clamp(viewer.Offset.Y + appliedDelta, 0, maxY);
            viewer.Offset = new Vector(viewer.Offset.X, targetOffset);
            return true;
        }

        private void UpdateQuickNavButtonsVisibility()
        {
            var viewer = GetScrollViewerForSession(_attachedSession);
            if (viewer == null)
                return;

            var topVisible = viewer.Offset.Y > QuickNavVisibilityThreshold;
            var distanceToBottom = viewer.Extent.Height - (viewer.Offset.Y + viewer.Viewport.Height);
            var bottomVisible = distanceToBottom > QuickNavVisibilityThreshold;

            var topButton = viewer.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ScrollToTopButton");
            var bottomButton = viewer.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ScrollToBottomButton");

            if (topButton != null)
            {
                topButton.IsVisible = topVisible;
            }

            if (bottomButton != null)
            {
                bottomButton.IsVisible = bottomVisible;
            }
        }

        private void SessionScrollViewer_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is not ScrollViewer viewer || viewer.DataContext is not MediaSessionViewModel session)
            {
                return;
            }

            _sessionScrollViewers[session.SessionId] = viewer;
            if (ReferenceEquals(session, _attachedSession))
            {
                _chatScrollViewer = viewer;
                UpdateQuickNavButtonsVisibility();
            }
        }

        private void SessionScrollViewer_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is not ScrollViewer viewer || viewer.DataContext is not MediaSessionViewModel session)
            {
                return;
            }

            if (_sessionScrollViewers.TryGetValue(session.SessionId, out var mapped) && ReferenceEquals(mapped, viewer))
            {
                _sessionScrollViewers.Remove(session.SessionId);
                if (ReferenceEquals(_attachedSession, session))
                {
                    _chatScrollViewer = null;
                }
            }
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
            previewWindow.Show(TopLevel.GetTopLevel(this) as Window);
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
    }
}
