using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
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
        private readonly Action<string> _statusSetter;
        private readonly Action<MediaFileItem?> _onAudioFileSelected;
        private readonly Func<bool> _suppressSubtitleSeekProvider;
        private readonly Action<SubtitleCue?> _onSubtitleCueSelected;

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
            Action<string> statusSetter,
            Action<MediaFileItem?> onAudioFileSelected,
            Func<bool> suppressSubtitleSeekProvider,
            Action<SubtitleCue?> onSubtitleCueSelected)
        {
            _configProvider = configProvider;
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
            _audioFiles.Clear();
            _subtitleFiles.Clear();
            SubtitleCues = new ObservableCollection<SubtitleCue>();

            var sessionsPath = PathManager.Instance.SessionsPath;
            if (!Directory.Exists(sessionsPath))
            {
                return;
            }

            var files = Directory.GetFiles(sessionsPath, "*.mp3")
                .Concat(Directory.GetFiles(sessionsPath, "*.wav"))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path));

            foreach (var file in files)
            {
                _audioFiles.Add(new MediaFileItem
                {
                    Name = Path.GetFileName(file),
                    FullPath = file
                });
            }

            if (_selectedAudioFile != null && !_audioFiles.Any(item => item.FullPath == _selectedAudioFile.FullPath))
            {
                SelectedAudioFile = null;
            }

            AudioLibraryRefreshed?.Invoke();
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
                var shouldUseSpeech = config.BatchStorageIsValid
                    && !string.IsNullOrWhiteSpace(config.BatchStorageConnectionString)
                    && config.UseSpeechSubtitleForReview;
                var speechPath = GetSpeechSubtitlePath(audioFile.FullPath);
                var speechFile = _subtitleFiles.FirstOrDefault(item =>
                    string.Equals(item.FullPath, speechPath, StringComparison.OrdinalIgnoreCase));
                SelectedSubtitleFile = shouldUseSpeech && speechFile != null
                    ? speechFile
                    : _subtitleFiles[0];
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
                Path.Combine(directory, baseName + ".speech.vtt"),
                Path.Combine(directory, baseName + ".ai.srt"),
                Path.Combine(directory, baseName + ".ai.vtt"),
                Path.Combine(directory, baseName + ".srt"),
                Path.Combine(directory, baseName + ".vtt")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        public static bool HasAiSubtitle(string audioFilePath)
        {
            var directory = Path.GetDirectoryName(audioFilePath) ?? PathManager.Instance.SessionsPath;
            var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
            var speechVtt = Path.Combine(directory, baseName + ".speech.vtt");
            var aiSrt = Path.Combine(directory, baseName + ".ai.srt");
            var aiVtt = Path.Combine(directory, baseName + ".ai.vtt");
            return File.Exists(speechVtt) || File.Exists(aiSrt) || File.Exists(aiVtt);
        }

        public static bool HasSpeechSubtitle(string audioFilePath)
        {
            var speechPath = GetSpeechSubtitlePath(audioFilePath);
            return File.Exists(speechPath);
        }

        public static string GetSpeechSubtitlePath(string audioFilePath)
            => RealtimeSpeechTranscriber.GetSpeechSubtitlePath(audioFilePath);

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
