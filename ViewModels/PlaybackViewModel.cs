using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using NAudio.Wave;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels
{
    public class PlaybackViewModel : ViewModelBase
    {
        private readonly Action<string> _statusSetter;
        private readonly Func<ObservableCollection<SubtitleCue>> _subtitleCuesProvider;
        private readonly Action<SubtitleCue?> _subtitleCueSetter;
        private readonly Func<SubtitleCue?> _subtitleCueGetter;

        private WaveOutEvent? _playbackOutput;
        private WaveStream? _playbackReader;
        private readonly DispatcherTimer _playbackTimer;
        private TimeSpan _playbackPosition = TimeSpan.Zero;
        private TimeSpan _playbackDuration = TimeSpan.Zero;
        private double _playbackProgress;
        private bool _isPlaybackReady;
        private bool _isPlaying;
        private bool _suppressSeek;

        public bool SuppressSubtitleSeek { get; private set; }

        public ICommand PlayAudioCommand { get; }
        public ICommand PauseAudioCommand { get; }
        public ICommand StopAudioCommand { get; }

        public bool IsPlayEnabled => _isPlaybackReady && !_isPlaying;

        public bool IsPauseEnabled => _isPlaybackReady && _isPlaying;

        public bool IsStopEnabled => _isPlaybackReady;

        public string PlaybackTimeText => $"{FormatTime(_playbackPosition)} / {FormatTime(_playbackDuration)}";

        public double PlaybackProgress
        {
            get => _playbackProgress;
            set
            {
                if (!SetProperty(ref _playbackProgress, value))
                {
                    return;
                }

                if (!_suppressSeek)
                {
                    SeekToProgress(value);
                }
            }
        }

        public PlaybackViewModel(
            Action<string> statusSetter,
            Func<ObservableCollection<SubtitleCue>> subtitleCuesProvider,
            Action<SubtitleCue?> subtitleCueSetter,
            Func<SubtitleCue?> subtitleCueGetter)
        {
            _statusSetter = statusSetter;
            _subtitleCuesProvider = subtitleCuesProvider;
            _subtitleCueSetter = subtitleCueSetter;
            _subtitleCueGetter = subtitleCueGetter;

            _playbackTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
            {
                UpdatePlaybackProgressFromReader();
            });

            PlayAudioCommand = new RelayCommand(
                execute: _ => PlayAudio(),
                canExecute: _ => IsPlayEnabled
            );

            PauseAudioCommand = new RelayCommand(
                execute: _ => PauseAudio(),
                canExecute: _ => IsPauseEnabled
            );

            StopAudioCommand = new RelayCommand(
                execute: _ => StopAudio(),
                canExecute: _ => IsStopEnabled
            );
        }

        public void LoadAudioForPlayback(MediaFileItem? audioFile)
        {
            StopPlaybackInternal();

            _playbackDuration = TimeSpan.Zero;
            _playbackPosition = TimeSpan.Zero;
            PlaybackProgress = 0;

            if (audioFile == null || string.IsNullOrWhiteSpace(audioFile.FullPath))
            {
                UpdatePlaybackState(false, false);
                return;
            }

            if (!System.IO.File.Exists(audioFile.FullPath))
            {
                UpdatePlaybackState(false, false);
                return;
            }

            try
            {
                // 使用 MediaFoundationReader 代替 AudioFileReader
                // AudioFileReader 内部使用 Mp3FileReader，会在构造时扫描整个文件建帧索引，
                // 对大文件（100MB+）耗时数秒甚至导致 UI 无响应。
                // MediaFoundationReader 基于 Windows Media Foundation，瞬时打开，原生支持 seek。
                _playbackReader = new MediaFoundationReader(audioFile.FullPath);
                _playbackOutput = new WaveOutEvent();
                _playbackOutput.Init(_playbackReader);
                _playbackOutput.PlaybackStopped += OnPlaybackStopped;
                _playbackDuration = _playbackReader.TotalTime;
                _playbackPosition = TimeSpan.Zero;
                PlaybackProgress = 0;
                UpdatePlaybackState(true, false);
                // 仅在真正播放时才启动定时器，避免空转刷新触发布局压力
                _playbackTimer.Stop();
            }
            catch (Exception ex)
            {
                UpdatePlaybackState(false, false);
                _statusSetter($"加载音频失败: {ex.Message}");
            }
        }

        private void PlayAudio()
        {
            if (_playbackOutput == null)
            {
                return;
            }

            _playbackOutput.Play();
            _playbackTimer.Start();
            UpdatePlaybackState(true, true);
        }

        private void PauseAudio()
        {
            if (_playbackOutput == null)
            {
                return;
            }

            _playbackOutput.Pause();
            _playbackTimer.Stop();
            UpdatePlaybackState(true, false);
        }

        private void StopAudio()
        {
            if (_playbackOutput == null)
            {
                return;
            }

            _playbackOutput.Stop();
            _playbackTimer.Stop();
            SeekToTime(TimeSpan.Zero);
            UpdatePlaybackState(true, false);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            _playbackTimer.Stop();
            UpdatePlaybackProgressFromReader();
            UpdatePlaybackState(_playbackReader != null, false);
        }

        public void SeekToTime(TimeSpan time)
        {
            if (_playbackReader == null)
            {
                return;
            }

            var safe = time;
            if (safe < TimeSpan.Zero)
            {
                safe = TimeSpan.Zero;
            }
            if (_playbackDuration > TimeSpan.Zero && safe > _playbackDuration)
            {
                safe = _playbackDuration;
            }

            _playbackReader.CurrentTime = safe;
            _playbackPosition = safe;
            UpdatePlaybackProgressFromReader();
        }

        private void SeekToProgress(double progress)
        {
            if (_playbackReader == null || _playbackDuration <= TimeSpan.Zero)
            {
                return;
            }

            var clamped = Math.Clamp(progress, 0, 1);
            var target = TimeSpan.FromMilliseconds(_playbackDuration.TotalMilliseconds * clamped);
            SeekToTime(target);
        }

        private void UpdatePlaybackProgressFromReader()
        {
            if (_playbackReader == null)
            {
                return;
            }

            _suppressSeek = true;
            var newPosition = _playbackReader.CurrentTime;
            var newDuration = _playbackReader.TotalTime;
            var newProgress = newDuration > TimeSpan.Zero
                ? newPosition.TotalMilliseconds / newDuration.TotalMilliseconds
                : 0;

            var positionChanged = (newPosition - _playbackPosition).Duration() > TimeSpan.FromMilliseconds(50);
            var durationChanged = (newDuration - _playbackDuration).Duration() > TimeSpan.FromMilliseconds(50);
            var progressChanged = Math.Abs(newProgress - _playbackProgress) > 0.0005d;

            _playbackPosition = newPosition;
            _playbackDuration = newDuration;

            if (progressChanged)
            {
                _playbackProgress = newProgress;
                OnPropertyChanged(nameof(PlaybackProgress));
            }

            if (positionChanged || durationChanged)
            {
                OnPropertyChanged(nameof(PlaybackTimeText));
            }

            if (positionChanged)
            {
                UpdateCurrentSubtitleCue(_playbackPosition);
            }

            _suppressSeek = false;
        }

        public void PlayFromSubtitleCue(SubtitleCue? cue)
        {
            if (cue == null || _playbackReader == null || _playbackOutput == null)
            {
                return;
            }

            SeekToTime(cue.Start);
            PlayAudio();
        }

        private void UpdateCurrentSubtitleCue(TimeSpan position)
        {
            var subtitleCues = _subtitleCuesProvider();
            if (subtitleCues.Count == 0)
            {
                return;
            }

            var selectedSubtitleCue = _subtitleCueGetter();
            if (selectedSubtitleCue != null
                && position >= selectedSubtitleCue.Start
                && position <= selectedSubtitleCue.End)
            {
                return;
            }

            var match = subtitleCues.FirstOrDefault(cue => position >= cue.Start && position <= cue.End);
            if (ReferenceEquals(match, selectedSubtitleCue))
            {
                return;
            }

            SuppressSubtitleSeek = true;
            _subtitleCueSetter(match);
            SuppressSubtitleSeek = false;
        }

        private void UpdatePlaybackState(bool ready, bool playing)
        {
            _isPlaybackReady = ready;
            _isPlaying = playing;
            OnPropertyChanged(nameof(IsPlayEnabled));
            OnPropertyChanged(nameof(IsPauseEnabled));
            OnPropertyChanged(nameof(IsStopEnabled));
            OnPropertyChanged(nameof(PlaybackTimeText));
            ((RelayCommand)PlayAudioCommand).RaiseCanExecuteChanged();
            ((RelayCommand)PauseAudioCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopAudioCommand).RaiseCanExecuteChanged();
        }

        private void StopPlaybackInternal()
        {
            try
            {
                if (_playbackOutput != null)
                {
                    _playbackOutput.PlaybackStopped -= OnPlaybackStopped;
                    _playbackOutput.Stop();
                    _playbackOutput.Dispose();
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                _playbackOutput = null;
            }

            try
            {
                _playbackReader?.Dispose();
            }
            catch
            {
                // ignore
            }
            finally
            {
                _playbackReader = null;
            }

            _playbackTimer.Stop();
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.TotalHours >= 1
                ? time.ToString(@"hh\:mm\:ss")
                : time.ToString(@"mm\:ss");
        }

        public void Dispose()
        {
            StopPlaybackInternal();
        }
    }
}
