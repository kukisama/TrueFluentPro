using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 媒体中心工作区选项卡：每个 tab 持有独立画布上下文与运行状态。
    /// </summary>
    public sealed class MediaWorkspaceTabViewModel : ViewModelBase, IDisposable
    {
        private readonly Action<MediaWorkspaceTabViewModel>? _onStateChanged;
        private readonly Dictionary<ChatMessageViewModel, NotifyCollectionChangedEventHandler> _messageMediaHandlers = new();
        private bool _disposed;
        private bool _isSelected;
        private CreatorCanvasMode _canvasMode;
        private CreatorMediaKind _mediaKind;
        private string _previewPath = string.Empty;
        private bool _hasCompletedResult;

        public MediaWorkspaceTabViewModel(
            MediaSessionViewModel session,
            CreatorCanvasMode canvasMode,
            CreatorMediaKind mediaKind,
            Action<MediaWorkspaceTabViewModel>? onStateChanged = null)
        {
            Session = session;
            _canvasMode = canvasMode;
            _mediaKind = mediaKind;
            _onStateChanged = onStateChanged;

            Session.PropertyChanged += Session_PropertyChanged;
            Session.RunningTasks.CollectionChanged += RunningTasks_CollectionChanged;
            Session.Messages.CollectionChanged += Messages_CollectionChanged;

            foreach (var message in Session.Messages)
            {
                SubscribeMessage(message);
            }

            RefreshDerivedState();
        }

        public MediaSessionViewModel Session { get; }

        public CreatorCanvasMode CanvasMode
        {
            get => _canvasMode;
            set
            {
                if (SetProperty(ref _canvasMode, value))
                {
                    OnPropertyChanged(nameof(WorkflowText));
                    NotifyStateChanged();
                }
            }
        }

        public CreatorMediaKind MediaKind
        {
            get => _mediaKind;
            set
            {
                if (SetProperty(ref _mediaKind, value))
                {
                    OnPropertyChanged(nameof(WorkflowText));
                    NotifyStateChanged();
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string Title => Session.SessionName;

        public string WorkflowText => $"{(CanvasMode == CreatorCanvasMode.Draw ? "绘制" : "编辑")} · {(MediaKind == CreatorMediaKind.Video ? "视频" : "图片")}";

        public bool HasRunningTasks => Session.RunningTaskCount > 0;
        public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewPath);
        public bool HasNoPreview => !HasPreview;
        public bool IsVideo => MediaKind == CreatorMediaKind.Video;
        public bool IsImage => MediaKind == CreatorMediaKind.Image;
        public bool HasCompletedResult => _hasCompletedResult;
        public bool IsIdle => !HasRunningTasks && !HasCompletedResult;
        public string PreviewPath => _previewPath;
        public string KindBadgeText => IsVideo ? "视频" : "图片";
        public string StatusBadgeText => HasRunningTasks ? "运行中" : HasCompletedResult ? "已完成" : "待命";
        public string StatusDotBrush => HasRunningTasks ? "#F59E0B" : HasCompletedResult ? "#22C55E" : "#94A3B8";
        public string KindBadgeBackground => IsVideo ? "#7C3AED" : "#2563EB";

        public string RunningCountText => Session.RunningTaskCount > 0 ? Session.RunningTaskCount.ToString() : string.Empty;

        public string BadgeText => Session.RunningTaskCount > 0 ? "进行中" : "就绪";

        public string SummaryText
        {
            get
            {
                var runningPrompt = Session.RunningTasks
                    .OrderByDescending(t => t.CreatedAt)
                    .Select(t => t.Prompt)
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                if (!string.IsNullOrWhiteSpace(runningPrompt))
                {
                    return Compact(runningPrompt, 26);
                }

                var lastTaskPrompt = Session.TaskHistory
                    .OrderByDescending(t => t.CreatedAt)
                    .Select(t => t.Prompt)
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                if (!string.IsNullOrWhiteSpace(lastTaskPrompt))
                {
                    return Compact(lastTaskPrompt, 26);
                }

                if (!string.IsNullOrWhiteSpace(Session.StatusText) && !string.Equals(Session.StatusText, "就绪", StringComparison.Ordinal))
                {
                    return Compact(Session.StatusText, 26);
                }

                return "空白画布";
            }
        }

        private void Session_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MediaSessionViewModel.SessionName)
                or nameof(MediaSessionViewModel.StatusText)
                or nameof(MediaSessionViewModel.RunningTaskCount)
                or nameof(MediaSessionViewModel.IsGenerating)
                or nameof(MediaSessionViewModel.ReferenceImageCount))
            {
                NotifyStateChanged();
            }
        }

        private void RunningTasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            NotifyStateChanged();
        }

        private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var message in e.OldItems.OfType<ChatMessageViewModel>())
                {
                    UnsubscribeMessage(message);
                }
            }

            if (e.NewItems != null)
            {
                foreach (var message in e.NewItems.OfType<ChatMessageViewModel>())
                {
                    SubscribeMessage(message);
                }
            }

            NotifyStateChanged();
        }

        private void SubscribeMessage(ChatMessageViewModel message)
        {
            if (_messageMediaHandlers.ContainsKey(message))
            {
                return;
            }

            NotifyCollectionChangedEventHandler handler = (_, _) => NotifyStateChanged();
            message.MediaPaths.CollectionChanged += handler;
            _messageMediaHandlers[message] = handler;
        }

        private void UnsubscribeMessage(ChatMessageViewModel message)
        {
            if (!_messageMediaHandlers.TryGetValue(message, out var handler))
            {
                return;
            }

            message.MediaPaths.CollectionChanged -= handler;
            _messageMediaHandlers.Remove(message);
        }

        private void NotifyStateChanged()
        {
            RefreshDerivedState();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(WorkflowText));
            OnPropertyChanged(nameof(HasRunningTasks));
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(HasNoPreview));
            OnPropertyChanged(nameof(IsVideo));
            OnPropertyChanged(nameof(IsImage));
            OnPropertyChanged(nameof(HasCompletedResult));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(PreviewPath));
            OnPropertyChanged(nameof(KindBadgeText));
            OnPropertyChanged(nameof(StatusBadgeText));
            OnPropertyChanged(nameof(StatusDotBrush));
            OnPropertyChanged(nameof(KindBadgeBackground));
            OnPropertyChanged(nameof(RunningCountText));
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(SummaryText));
            _onStateChanged?.Invoke(this);
        }

        private void RefreshDerivedState()
        {
            _previewPath = ResolvePreviewPath();
            _hasCompletedResult = Session.TaskHistory.Any(t => t.Status == MediaGenStatus.Completed) || !string.IsNullOrWhiteSpace(_previewPath);
        }

        private string ResolvePreviewPath()
        {
            // 视频会话：优先从已完成的视频任务找首帧预览
            if (_mediaKind == CreatorMediaKind.Video)
            {
                var videoTaskResult = Session.TaskHistory
                    .Where(t => t.Type == MediaGenType.Video && t.Status == MediaGenStatus.Completed)
                    .OrderByDescending(t => t.CreatedAt)
                    .Select(t => t.ResultFilePath)
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

                if (!string.IsNullOrWhiteSpace(videoTaskResult))
                {
                    var firstFrame = Path.Combine(
                        Path.GetDirectoryName(videoTaskResult) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(videoTaskResult) + ".first.png");
                    if (File.Exists(firstFrame))
                    {
                        return firstFrame;
                    }
                }
            }

            foreach (var message in Session.Messages.Reverse())
            {
                foreach (var path in message.MediaPaths.Reverse())
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    if (VideoFrameExtractorService.IsLastFrameImagePath(path))
                    {
                        continue;
                    }

                    if (IsImageFile(path))
                    {
                        return path;
                    }
                }
            }

            var completedPath = Session.TaskHistory
                .Where(t => t.Status == MediaGenStatus.Completed)
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => t.ResultFilePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && IsImageFile(path));

            return completedPath ?? string.Empty;
        }

        private static bool IsImageFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            var ext = Path.GetExtension(filePath);
            return ext is not null && (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase));
        }

        private static string Compact(string text, int maxLength)
        {
            var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length > maxLength ? singleLine[..maxLength] + "..." : singleLine;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Session.PropertyChanged -= Session_PropertyChanged;
            Session.RunningTasks.CollectionChanged -= RunningTasks_CollectionChanged;
            Session.Messages.CollectionChanged -= Messages_CollectionChanged;

            foreach (var pair in _messageMediaHandlers.ToList())
            {
                pair.Key.MediaPaths.CollectionChanged -= pair.Value;
            }

            _messageMediaHandlers.Clear();
        }
    }
}
