using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    public class FileLibraryViewModel : ViewModelBase
    {
        private const double SubtitleCueRowHeight = 56;

        private readonly ObservableCollection<MediaFileItem> _audioFiles = new();
        private readonly ObservableCollection<MediaFileItem> _subtitleFiles = new();
        private ObservableCollection<SubtitleCue> _subtitleCues = new();

        private MediaFileItem? _selectedAudioFile;
        private MediaFileItem? _selectedSubtitleFile;
        private SubtitleCue? _selectedSubtitleCue;
        private double _subtitleListHeight;
        private bool _isLoadingSubtitleCues;

        private readonly Func<AzureSpeechConfig> _configProvider;
        private readonly IBatchPackageStateService _batchPackageStateService;
        private readonly Action<string> _statusSetter;
        private readonly Action<MediaFileItem?> _onAudioFileSelected;
        private readonly Func<bool> _suppressSubtitleSeekProvider;
        private readonly Action<SubtitleCue?> _onSubtitleCueSelected;
        private int _audioLibraryRefreshVersion;

        private sealed class AudioLibrarySnapshot
        {
            public required List<MediaFileItem> AudioFiles { get; init; }
        }

        public RelayCommand RefreshAudioLibraryCommand { get; }

        /// <summary>
        /// Raised after subtitle cues are loaded so the parent can refresh batch commands.
        /// </summary>
        public event Action? SubtitleCuesLoaded;

        /// <summary>
        /// Raised after the audio library is refreshed.
        /// </summary>
        public event Action? AudioLibraryRefreshed;

        public FileLibraryViewModel(
            Func<AzureSpeechConfig> configProvider,
            IBatchPackageStateService batchPackageStateService,
            Action<string> statusSetter,
            Action<MediaFileItem?> onAudioFileSelected,
            Func<bool> suppressSubtitleSeekProvider,
            Action<SubtitleCue?> onSubtitleCueSelected)
        {
            _configProvider = configProvider;
            _batchPackageStateService = batchPackageStateService;
            _statusSetter = statusSetter;
            _onAudioFileSelected = onAudioFileSelected;
            _suppressSubtitleSeekProvider = suppressSubtitleSeekProvider;
            _onSubtitleCueSelected = onSubtitleCueSelected;

            _subtitleCues.CollectionChanged += OnSubtitleCuesCollectionChanged;

            RefreshAudioLibraryCommand = new RelayCommand(
                execute: _ => RefreshAudioLibrary(),
                canExecute: _ => true);
        }

        public ObservableCollection<MediaFileItem> AudioFiles => _audioFiles;
        public ObservableCollection<MediaFileItem> SubtitleFiles => _subtitleFiles;
        public ObservableCollection<SubtitleCue> SubtitleCues
        {
            get => _subtitleCues;
            private set
            {
                if (ReferenceEquals(_subtitleCues, value))
                {
                    return;
                }

                _subtitleCues.CollectionChanged -= OnSubtitleCuesCollectionChanged;
                SetProperty(ref _subtitleCues, value);
                _subtitleCues.CollectionChanged += OnSubtitleCuesCollectionChanged;
            }
        }

        public MediaFileItem? SelectedAudioFile
        {
            get => _selectedAudioFile;
            set
            {
                if (!SetProperty(ref _selectedAudioFile, value))
                {
                    return;
                }

                _onAudioFileSelected(value);
                LoadSubtitleFilesForAudio(value);
            }
        }

        public MediaFileItem? SelectedSubtitleFile
        {
            get => _selectedSubtitleFile;
            set
            {
                if (!SetProperty(ref _selectedSubtitleFile, value))
                {
                    return;
                }

                LoadSubtitleCues(value);
            }
        }

        public SubtitleCue? SelectedSubtitleCue
        {
            get => _selectedSubtitleCue;
            set
            {
                if (!SetProperty(ref _selectedSubtitleCue, value))
                {
                    return;
                }

                if (_suppressSubtitleSeekProvider())
                {
                    return;
                }

                if (_isLoadingSubtitleCues)
                {
                    return;
                }

                if (value != null)
                {
                    _onSubtitleCueSelected(value);
                }
            }
        }

        // SubtitleListHeight 保留用于未来可能的外部消费场景，但不再绑定到 XAML 高度约束
        // 字幕列表现在通过 Grid * 行自然填充可用空间
        public double SubtitleListHeight
        {
            get => _subtitleListHeight;
            private set => SetProperty(ref _subtitleListHeight, value);
        }

        // ── Refresh / load methods ──

        public void RefreshAudioLibrary()
        {
            _ = RefreshAudioLibraryAsync();
        }

        public async Task RefreshAudioLibraryAsync(CancellationToken cancellationToken = default)
        {
            var refreshVersion = Interlocked.Increment(ref _audioLibraryRefreshVersion);
            AudioLibrarySnapshot snapshot;
            try
            {
                snapshot = await Task.Run(() => BuildAudioLibrarySnapshot(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (refreshVersion != _audioLibraryRefreshVersion)
                {
                    return;
                }

                ApplyAudioLibrarySnapshot(snapshot);
            });
        }

        private AudioLibrarySnapshot BuildAudioLibrarySnapshot(CancellationToken cancellationToken)
        {
            var sessionsPath = PathManager.Instance.SessionsPath;
            var audioFiles = new List<MediaFileItem>();

            if (!Directory.Exists(sessionsPath))
            {
                return new AudioLibrarySnapshot { AudioFiles = audioFiles };
            }

            var files = Directory.GetFiles(sessionsPath, "*.mp3")
                .Concat(Directory.GetFiles(sessionsPath, "*.wav"))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path));

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                audioFiles.Add(new MediaFileItem
                {
                    Name = Path.GetFileName(file),
                    FullPath = file
                });
            }

            return new AudioLibrarySnapshot
            {
                AudioFiles = audioFiles
            };
        }

        private void ApplyAudioLibrarySnapshot(AudioLibrarySnapshot snapshot)
        {
            var selectedAudioPath = _selectedAudioFile?.FullPath;

            _batchPackageStateService.EnsurePackages(snapshot.AudioFiles);
            _audioFiles.Clear();
            _subtitleFiles.Clear();
            SubtitleCues = new ObservableCollection<SubtitleCue>();

            foreach (var file in snapshot.AudioFiles)
            {
                _audioFiles.Add(file);
            }

            if (!string.IsNullOrWhiteSpace(selectedAudioPath))
            {
                var matchedAudio = _audioFiles.FirstOrDefault(item =>
                    string.Equals(item.FullPath, selectedAudioPath, StringComparison.OrdinalIgnoreCase));
                if (matchedAudio != null)
                {
                    SelectedAudioFile = matchedAudio;
                }
                else
                {
                    SelectedAudioFile = null;
                }
            }
            else if (_selectedAudioFile != null)
            {
                SelectedAudioFile = null;
            }

            RefreshAudioProcessingIndicators();

            AudioLibraryRefreshed?.Invoke();
        }

        public void RefreshAudioProcessingIndicators()
        {
            var snapshots = BuildDefaultProcessingSnapshots();
            ApplyAudioProcessingSnapshots(snapshots);
        }

        public void ApplyAudioProcessingSnapshots(IEnumerable<AudioFileProcessingSnapshot> snapshots)
        {
            var snapshotMap = snapshots
                .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.AudioPath))
                .GroupBy(snapshot => snapshot.AudioPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            foreach (var audioFile in _audioFiles)
            {
                if (snapshotMap.TryGetValue(audioFile.FullPath, out var snapshot))
                {
                    ApplyAudioProcessingSnapshot(audioFile, snapshot);
                }
                else
                {
                    ApplyAudioProcessingSnapshot(audioFile, new AudioFileProcessingSnapshot
                    {
                        AudioPath = audioFile.FullPath,
                        State = ProcessingDisplayState.None
                    });
                }
            }
        }

        private IReadOnlyList<AudioFileProcessingSnapshot> BuildDefaultProcessingSnapshots()
        {
            var batchSheets = _configProvider().AiConfig?.ReviewSheets?
                .Where(sheet => sheet.IncludeInBatch)
                .ToList() ?? new List<ReviewSheetPreset>();

            var snapshots = new List<AudioFileProcessingSnapshot>(_audioFiles.Count);
            foreach (var audioFile in _audioFiles)
            {
                snapshots.Add(BuildDefaultProcessingSnapshot(audioFile, batchSheets));
            }

            return snapshots;
        }

        private AudioFileProcessingSnapshot BuildDefaultProcessingSnapshot(
            MediaFileItem audioFile,
            IReadOnlyCollection<ReviewSheetPreset> batchSheets)
        {
            if (_batchPackageStateService.IsRemoved(audioFile.FullPath))
            {
                return new AudioFileProcessingSnapshot
                {
                    AudioPath = audioFile.FullPath,
                    State = ProcessingDisplayState.Removed,
                    BadgeText = "已删除",
                    DetailText = "已从批处理中心移除"
                };
            }

            var hasSpeechSubtitle = HasSpeechSubtitle(audioFile.FullPath);
            var completedSheets = batchSheets.Count(sheet => File.Exists(GetReviewSheetPath(audioFile.FullPath, sheet.FileTag)));
            var totalSheets = batchSheets.Count;

            if (totalSheets > 0 && completedSheets >= totalSheets)
            {
                return new AudioFileProcessingSnapshot
                {
                    AudioPath = audioFile.FullPath,
                    State = ProcessingDisplayState.Completed,
                    BadgeText = "已完成",
                    DetailText = hasSpeechSubtitle
                        ? $"字幕完成 · 复盘 {completedSheets}/{totalSheets}"
                        : $"复盘 {completedSheets}/{totalSheets}"
                };
            }

            if (hasSpeechSubtitle || completedSheets > 0)
            {
                return new AudioFileProcessingSnapshot
                {
                    AudioPath = audioFile.FullPath,
                    State = ProcessingDisplayState.Partial,
                    BadgeText = "进行中",
                    DetailText = totalSheets > 0
                        ? $"字幕 {(hasSpeechSubtitle ? "已生成" : "未生成")} · 复盘 {completedSheets}/{totalSheets}"
                        : (hasSpeechSubtitle ? "字幕已生成" : "待处理")
                };
            }

            return new AudioFileProcessingSnapshot
            {
                AudioPath = audioFile.FullPath,
                State = ProcessingDisplayState.Pending,
                BadgeText = "未处理",
                DetailText = totalSheets > 0 ? $"复盘 0/{totalSheets}" : "待处理"
            };
        }

        private static void ApplyAudioProcessingSnapshot(MediaFileItem audioFile, AudioFileProcessingSnapshot snapshot)
        {
            audioFile.ProcessingState = snapshot.State;
            audioFile.ProcessingBadgeText = snapshot.BadgeText;
            audioFile.ProcessingDetailText = snapshot.DetailText;
        }

        private static string GetReviewSheetPath(string audioFilePath, string fileTag)
        {
            var directory = Path.GetDirectoryName(audioFilePath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
            var tag = string.IsNullOrWhiteSpace(fileTag) ? "summary" : fileTag.Trim();
            return Path.Combine(directory, baseName + $".ai.{tag}.md");
        }

        public void LoadSubtitleFilesForAudio(MediaFileItem? audioFile)
        {
            _subtitleFiles.Clear();
            // 不在此处清空 SubtitleCues——SelectedSubtitleFile = null 会触发 LoadSubtitleCues 自动处理
            SelectedSubtitleFile = null;

            if (audioFile == null || string.IsNullOrWhiteSpace(audioFile.FullPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(audioFile.FullPath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFile.FullPath);
            var candidateBases = new[] { baseName };

            foreach (var candidate in candidateBases)
            {
                var speechVtt = Path.Combine(directory, candidate + ".speech.vtt");
                if (File.Exists(speechVtt))
                {
                    _subtitleFiles.Add(new MediaFileItem
                    {
                        Name = Path.GetFileName(speechVtt),
                        FullPath = speechVtt
                    });
                }

                var aiVtt = Path.Combine(directory, candidate + ".ai.vtt");
                if (File.Exists(aiVtt))
                {
                    _subtitleFiles.Add(new MediaFileItem
                    {
                        Name = Path.GetFileName(aiVtt),
                        FullPath = aiVtt
                    });
                }

                var aiSrt = Path.Combine(directory, candidate + ".ai.srt");
                if (File.Exists(aiSrt))
                {
                    _subtitleFiles.Add(new MediaFileItem
                    {
                        Name = Path.GetFileName(aiSrt),
                        FullPath = aiSrt
                    });
                }

                var srtPath = Path.Combine(directory, candidate + ".srt");
                if (File.Exists(srtPath))
                {
                    _subtitleFiles.Add(new MediaFileItem
                    {
                        Name = Path.GetFileName(srtPath),
                        FullPath = srtPath
                    });
                }

                var vttPath = Path.Combine(directory, candidate + ".vtt");
                if (File.Exists(vttPath))
                {
                    _subtitleFiles.Add(new MediaFileItem
                    {
                        Name = Path.GetFileName(vttPath),
                        FullPath = vttPath
                    });
                }
            }

            if (_subtitleFiles.Count > 0)
            {
                var config = _configProvider();
                var preferredPath = GetPreferredSubtitlePath(audioFile.FullPath, config.GetEffectiveReviewSubtitleSourceMode());
                var preferredFile = _subtitleFiles.FirstOrDefault(item =>
                    string.Equals(item.FullPath, preferredPath, StringComparison.OrdinalIgnoreCase));
                SelectedSubtitleFile = preferredFile ?? _subtitleFiles[0];
            }
        }

        private void LoadSubtitleCues(MediaFileItem? subtitleFile)
        {
            CrashLogger.AddBreadcrumb($"LoadSubtitleCues start: subtitle={DescribeMediaFile(subtitleFile)}");
            _isLoadingSubtitleCues = true;
            var loadedCueCount = 0;
            try
            {
                SelectedSubtitleCue = null;

                if (subtitleFile == null || string.IsNullOrWhiteSpace(subtitleFile.FullPath)
                    || !File.Exists(subtitleFile.FullPath))
                {
                    // 仅在需要清空时替换集合
                    if (_subtitleCues.Count > 0)
                    {
                        SubtitleCues = new ObservableCollection<SubtitleCue>();
                    }
                }
                else
                {
                    var extension = Path.GetExtension(subtitleFile.FullPath).ToLowerInvariant();
                    List<SubtitleCue> cues;
                    if (extension == ".srt")
                    {
                        cues = ParseSubtitleFile(subtitleFile.FullPath, expectsHeader: false);
                    }
                    else if (extension == ".vtt")
                    {
                        cues = ParseSubtitleFile(subtitleFile.FullPath, expectsHeader: true);
                    }
                    else
                    {
                        cues = new List<SubtitleCue>();
                    }

                    loadedCueCount = cues.Count;

                    // 一次性替换整个集合，避免逐条 Add 触发 N 次 CollectionChanged/UI 重绘
                    // 不先清空再加载——直接用有数据的集合替换，减少多余的布局重算
                    SubtitleCues = new ObservableCollection<SubtitleCue>(cues);

                    if (cues.Count > 0)
                    {
                        // 加载阶段自动选中第一条字幕。
                        // 当前处于 _isLoadingSubtitleCues=true，SelectedSubtitleCue setter 不会触发 seek 副作用。
                        SelectedSubtitleCue = cues[0];
                    }
                }
            }
            finally
            {
                _isLoadingSubtitleCues = false;
            }

            CrashLogger.AddBreadcrumb($"LoadSubtitleCues done: cues={loadedCueCount}, subtitle={DescribeMediaFile(subtitleFile)}");
            SubtitleCuesLoaded?.Invoke();
        }

        private static string DescribeMediaFile(MediaFileItem? item)
        {
            if (item == null)
            {
                return "(null)";
            }

            return $"name='{item.Name}', path='{item.FullPath}'";
        }

        // ── Static path helpers (public, used by batch) ──

        public static string? GetPreferredSubtitlePath(string audioFilePath)
        {
            var directory = Path.GetDirectoryName(audioFilePath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFilePath);

            var candidates = new[]
            {
                Path.Combine(directory, baseName + ".srt"),
                Path.Combine(directory, baseName + ".vtt")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        public static string? GetPreferredSubtitlePath(string audioFilePath, ReviewSubtitleSourceMode sourceMode)
        {
            return sourceMode switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => GetSpeechSubtitlePath(audioFilePath),
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => GetAiSubtitlePath(audioFilePath),
                _ => GetPreferredSubtitlePath(audioFilePath)
            };
        }

        public static bool HasAiSubtitle(string audioFilePath)
        {
            return File.Exists(GetAiSubtitlePath(audioFilePath)) || File.Exists(GetAiSubtitleSrtPath(audioFilePath));
        }

        public static bool HasSpeechSubtitle(string audioFilePath)
        {
            var speechPath = GetSpeechSubtitlePath(audioFilePath);
            return File.Exists(speechPath);
        }

        public static string GetSpeechSubtitlePath(string audioFilePath)
            => RealtimeSpeechTranscriber.GetSpeechSubtitlePath(audioFilePath);

        public static string GetAiSubtitlePath(string audioFilePath)
        {
            var directory = Path.GetDirectoryName(audioFilePath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
            return Path.Combine(directory, baseName + ".ai.vtt");
        }

        public static string GetAiSubtitleSrtPath(string audioFilePath)
        {
            var directory = Path.GetDirectoryName(audioFilePath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
            return Path.Combine(directory, baseName + ".ai.srt");
        }

        // ── Parsing ──

        private static List<SubtitleCue> ParseSubtitleFile(string path, bool expectsHeader)
        {
            var lines = File.ReadAllLines(path);
            return ParseSubtitleLines(lines, expectsHeader);
        }

        private static List<SubtitleCue> ParseSubtitleLines(string[] lines, bool expectsHeader)
        {
            var cues = new List<SubtitleCue>();
            var index = 0;
            if (expectsHeader && index < lines.Length && lines[index].StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }

            while (index < lines.Length)
            {
                var line = lines[index].Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    index++;
                    continue;
                }

                if (int.TryParse(line, out _))
                {
                    index++;
                    if (index >= lines.Length)
                    {
                        break;
                    }
                    line = lines[index].Trim();
                }

                if (!SubtitleFileParser.TryParseTimeRange(line, out var start, out var end))
                {
                    index++;
                    continue;
                }

                index++;
                var textLines = new List<string>();
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    textLines.Add(lines[index].Trim());
                    index++;
                }

                var text = string.Join(" ", textLines).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    cues.Add(new SubtitleCue
                    {
                        Start = start,
                        End = end,
                        Text = text
                    });
                }
            }

            return cues;
        }

        private void OnSubtitleCuesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateSubtitleListHeight();
        }

        private void UpdateSubtitleListHeight()
        {
            var visible = Math.Min(_subtitleCues.Count, 6);
            SubtitleListHeight = visible * SubtitleCueRowHeight;
        }
    }
}
