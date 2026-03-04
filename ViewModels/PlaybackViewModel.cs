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
        private AudioFileReader? _playbackReader;
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
                _playbackReader = new AudioFileReader(audioFile.FullPath);
                _playbackOutput = new WaveOutEvent();
                _playbackOutput.Init(_playbackReader);
                _playbackOutput.PlaybackStopped += OnPlaybackStopped;
                _playbackDuration = _playbackReader.TotalTime;
                _playbackPosition = TimeSpan.Zero;
                PlaybackProgress = 0;
                UpdatePlaybackState(true, false);
                _playbackTimer.Start();
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
            UpdatePlaybackState(true, true);
        }

        private void PauseAudio()
        {
            if (_playbackOutput == null)
            {
                return;
            }

            _playbackOutput.Pause();
            UpdatePlaybackState(true, false);
        }

        private void StopAudio()
        {
            if (_playbackOutput == null)
            {
                return;
            }

            _playbackOutput.Stop();
            SeekToTime(TimeSpan.Zero);
            UpdatePlaybackState(true, false);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
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
            _playbackPosition = _playbackReader.CurrentTime;
            _playbackDuration = _playbackReader.TotalTime;
            _playbackProgress = _playbackDuration > TimeSpan.Zero
                ? _playbackPosition.TotalMilliseconds / _playbackDuration.TotalMilliseconds
                : 0;
            OnPropertyChanged(nameof(PlaybackProgress));
            OnPropertyChanged(nameof(PlaybackTimeText));
            UpdateCurrentSubtitleCue(_playbackPosition);
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
