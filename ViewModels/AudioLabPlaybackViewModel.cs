using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using NAudio.Wave;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 听析中心增强版播放器：支持真实倍速控制、上下段跳转。
    /// 独立实例，不影响 ReviewMode 的播放器。
    /// </summary>
    public class AudioLabPlaybackViewModel : ViewModelBase, IDisposable
    {
        private WaveOutEvent? _output;
        private MediaFoundationReader? _reader;
        private VariSpeedSampleProvider? _speedProvider;
        private readonly DispatcherTimer _timer;

        private TimeSpan _position;
        private TimeSpan _duration;
        private double _progress;
        private bool _isReady;
        private bool _isPlaying;
        private bool _suppressSeek;
        private double _playbackSpeed = 1.0;

        private readonly Func<ObservableCollection<TranscriptSegment>> _segmentsProvider;
        private readonly Action<TranscriptSegment?> _currentSegmentSetter;
        private readonly Func<TranscriptSegment?> _currentSegmentGetter;

        public static readonly double[] SpeedOptions = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };

        public ICommand PlayCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand PreviousSegmentCommand { get; }
        public ICommand NextSegmentCommand { get; }
        public ICommand SetPlaybackSpeedCommand { get; }
        public ICommand TogglePlayPauseCommand { get; }

        public bool IsPlayEnabled => _isReady && !_isPlaying;
        public bool IsPauseEnabled => _isReady && _isPlaying;
        public bool IsStopEnabled => _isReady;
        public bool IsPlaying => _isPlaying;

        public string TimeText => $"{FormatTime(_position)} / {FormatTime(_duration)}";

        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set
            {
                if (!SetProperty(ref _playbackSpeed, value)) return;
                OnPropertyChanged(nameof(PlaybackSpeedText));
                if (_speedProvider != null)
                    _speedProvider.PlaybackRate = value;
            }
        }

        public string PlaybackSpeedText => $"{PlaybackSpeed:0.#}X";

        public double Progress
        {
            get => _progress;
            set
            {
                if (!SetProperty(ref _progress, value)) return;
                if (!_suppressSeek) SeekToProgress(value);
            }
        }

        public TimeSpan Position => _position;
        public TimeSpan Duration => _duration;

        public AudioLabPlaybackViewModel(
            Func<ObservableCollection<TranscriptSegment>> segmentsProvider,
            Action<TranscriptSegment?> currentSegmentSetter,
            Func<TranscriptSegment?> currentSegmentGetter)
        {
            _segmentsProvider = segmentsProvider;
            _currentSegmentSetter = currentSegmentSetter;
            _currentSegmentGetter = currentSegmentGetter;

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
            {
                UpdateProgressFromReader();
            });

            PlayCommand = new RelayCommand(_ => Play(), _ => IsPlayEnabled);
            PauseCommand = new RelayCommand(_ => Pause(), _ => IsPauseEnabled);
            StopCommand = new RelayCommand(_ => Stop(), _ => IsStopEnabled);
            TogglePlayPauseCommand = new RelayCommand(_ =>
            {
                if (_isPlaying) Pause();
                else Play();
            }, _ => _isReady);

            PreviousSegmentCommand = new RelayCommand(_ => SeekToPreviousSegment(), _ => _isReady);
            NextSegmentCommand = new RelayCommand(_ => SeekToNextSegment(), _ => _isReady);
            SetPlaybackSpeedCommand = new RelayCommand(p =>
            {
                if (p is double speed) PlaybackSpeed = speed;
                else if (p is string s && double.TryParse(s, out var sp)) PlaybackSpeed = sp;
            });
        }

        public void LoadAudio(string filePath)
        {
            StopInternal();
            _duration = TimeSpan.Zero;
            _position = TimeSpan.Zero;
            Progress = 0;

            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                UpdateState(false, false);
                return;
            }

            try
            {
                _reader = new MediaFoundationReader(filePath);
                _speedProvider = new VariSpeedSampleProvider(_reader.ToSampleProvider())
                {
                    PlaybackRate = _playbackSpeed
                };
                _output = new WaveOutEvent();
                _output.Init(_speedProvider);
                _output.PlaybackStopped += OnPlaybackStopped;
                _duration = _reader.TotalTime;
                _position = TimeSpan.Zero;
                Progress = 0;
                UpdateState(true, false);
                _timer.Stop();
            }
            catch
            {
                UpdateState(false, false);
            }
        }

        public void Play()
        {
            if (_output == null) return;
            _output.Play();
            _timer.Start();
            UpdateState(true, true);
        }

        public void Pause()
        {
            if (_output == null) return;
            _output.Pause();
            _timer.Stop();
            UpdateState(true, false);
        }

        public void Stop()
        {
            if (_output == null) return;
            _output.Stop();
            _timer.Stop();
            SeekToTime(TimeSpan.Zero);
            UpdateState(true, false);
        }

        public void SeekToTime(TimeSpan time)
        {
            if (_reader == null) return;
            var safe = TimeSpan.FromMilliseconds(Math.Clamp(time.TotalMilliseconds, 0,
                _duration > TimeSpan.Zero ? _duration.TotalMilliseconds : 0));
            _reader.CurrentTime = safe;
            _position = safe;
            UpdateProgressFromReader();
        }

        private void SeekToProgress(double progress)
        {
            if (_reader == null || _duration <= TimeSpan.Zero) return;
            var target = TimeSpan.FromMilliseconds(_duration.TotalMilliseconds * Math.Clamp(progress, 0, 1));
            SeekToTime(target);
        }

        private void SeekToPreviousSegment()
        {
            var segments = _segmentsProvider();
            if (segments.Count == 0) return;

            // 找到当前位置之前最近的段
            var current = _position;
            TranscriptSegment? target = null;
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                // 留 0.5s 容差：如果在段首 0.5s 内再按上一段，跳到前一段
                if (segments[i].StartTime < current - TimeSpan.FromMilliseconds(500))
                {
                    target = segments[i];
                    break;
                }
            }
            target ??= segments[0];
            SeekToTime(target.StartTime);
        }

        private void SeekToNextSegment()
        {
            var segments = _segmentsProvider();
            if (segments.Count == 0) return;

            var current = _position;
            var target = segments.FirstOrDefault(s => s.StartTime > current + TimeSpan.FromMilliseconds(100));
            if (target != null) SeekToTime(target.StartTime);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            _timer.Stop();
            UpdateProgressFromReader();
            UpdateState(_reader != null, false);
        }

        private void UpdateProgressFromReader()
        {
            if (_reader == null) return;

            _suppressSeek = true;
            var newPos = _reader.CurrentTime;
            var newDur = _reader.TotalTime;
            var newProg = newDur > TimeSpan.Zero
                ? newPos.TotalMilliseconds / newDur.TotalMilliseconds
                : 0;

            var posChanged = (newPos - _position).Duration() > TimeSpan.FromMilliseconds(50);
            _position = newPos;
            _duration = newDur;

            if (Math.Abs(newProg - _progress) > 0.0005d)
            {
                _progress = newProg;
                OnPropertyChanged(nameof(Progress));
            }

            if (posChanged)
            {
                OnPropertyChanged(nameof(TimeText));
                OnPropertyChanged(nameof(Position));
                UpdateCurrentSegment();
            }

            _suppressSeek = false;
        }

        private void UpdateCurrentSegment()
        {
            var segments = _segmentsProvider();
            if (segments.Count == 0) return;

            var current = _currentSegmentGetter();
            if (current != null && _position >= current.StartTime)
            {
                // 还在当前段范围内
                var idx = segments.IndexOf(current);
                if (idx >= 0 && (idx + 1 >= segments.Count || _position < segments[idx + 1].StartTime))
                    return;
            }

            // 找最匹配的段
            TranscriptSegment? match = null;
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                if (segments[i].StartTime <= _position)
                {
                    match = segments[i];
                    break;
                }
            }

            if (!ReferenceEquals(match, current))
            {
                if (current != null) current.IsCurrent = false;
                if (match != null) match.IsCurrent = true;
                _currentSegmentSetter(match);
            }
        }

        private void UpdateState(bool ready, bool playing)
        {
            _isReady = ready;
            _isPlaying = playing;
            OnPropertyChanged(nameof(IsPlayEnabled));
            OnPropertyChanged(nameof(IsPauseEnabled));
            OnPropertyChanged(nameof(IsStopEnabled));
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(TimeText));
            ((RelayCommand)PlayCommand).RaiseCanExecuteChanged();
            ((RelayCommand)PauseCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
            ((RelayCommand)TogglePlayPauseCommand).RaiseCanExecuteChanged();
            ((RelayCommand)PreviousSegmentCommand).RaiseCanExecuteChanged();
            ((RelayCommand)NextSegmentCommand).RaiseCanExecuteChanged();
        }

        private static string FormatTime(TimeSpan t)
            => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

        private void StopInternal()
        {
            try
            {
                if (_output != null)
                {
                    _output.PlaybackStopped -= OnPlaybackStopped;
                    _output.Stop();
                    _output.Dispose();
                }
            }
            catch { }
            finally { _output = null; }

            _speedProvider = null;

            try { _reader?.Dispose(); }
            catch { }
            finally { _reader = null; }

            _timer.Stop();
        }

        public void Dispose()
        {
            StopInternal();
            _timer.Stop();
        }
    }
}
