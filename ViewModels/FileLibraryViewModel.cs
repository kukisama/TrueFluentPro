using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private readonly ObservableCollection<SubtitleCue> _subtitleCues = new();

        private MediaFileItem? _selectedAudioFile;
        private MediaFileItem? _selectedSubtitleFile;
        private SubtitleCue? _selectedSubtitleCue;
        private double _subtitleListHeight;

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

            _subtitleCues.CollectionChanged += (_, _) => UpdateSubtitleListHeight();

            RefreshAudioLibraryCommand = new RelayCommand(
                execute: _ => RefreshAudioLibrary(),
                canExecute: _ => true);
        }

        public ObservableCollection<MediaFileItem> AudioFiles => _audioFiles;
        public ObservableCollection<MediaFileItem> SubtitleFiles => _subtitleFiles;
        public ObservableCollection<SubtitleCue> SubtitleCues => _subtitleCues;

        public MediaFileItem? SelectedAudioFile
        {
            get => _selectedAudioFile;
            set
            {
                if (!SetProperty(ref _selectedAudioFile, value))
                {
                    return;
                }

                LoadSubtitleFilesForAudio(value);
                _onAudioFileSelected(value);
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

                if (value != null)
                {
                    _onSubtitleCueSelected(value);
                }
            }
        }

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
            _subtitleCues.Clear();

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
            _subtitleCues.Clear();
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
            _subtitleCues.Clear();
            SelectedSubtitleCue = null;

            if (subtitleFile == null || string.IsNullOrWhiteSpace(subtitleFile.FullPath))
            {
                return;
            }

            if (!File.Exists(subtitleFile.FullPath))
            {
                return;
            }

            var extension = Path.GetExtension(subtitleFile.FullPath).ToLowerInvariant();
            if (extension == ".srt")
            {
                ParseSrt(subtitleFile.FullPath);
            }
            else if (extension == ".vtt")
            {
                ParseVtt(subtitleFile.FullPath);
            }

            UpdateSubtitleListHeight();
            SubtitleCuesLoaded?.Invoke();
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

        private void ParseSrt(string path)
        {
            var lines = File.ReadAllLines(path);
            ParseSubtitleLines(lines, expectsHeader: false);
        }

        private void ParseVtt(string path)
        {
            var lines = File.ReadAllLines(path);
            ParseSubtitleLines(lines, expectsHeader: true);
        }

        private void ParseSubtitleLines(string[] lines, bool expectsHeader)
        {
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
                    _subtitleCues.Add(new SubtitleCue
                    {
                        Start = start,
                        End = end,
                        Text = text
                    });
                }
            }

            UpdateSubtitleListHeight();
        }

        private void UpdateSubtitleListHeight()
        {
            var visible = Math.Min(_subtitleCues.Count, 6);
            SubtitleListHeight = visible * SubtitleCueRowHeight;
        }
    }
}
