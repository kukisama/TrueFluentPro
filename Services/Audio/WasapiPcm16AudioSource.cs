using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TrueFluentPro.Services.Audio
{
    public sealed class WasapiPcm16AudioSource : IAsyncDisposable
    {
        private readonly string? _loopbackDeviceId;
        private readonly string? _micDeviceId;
        private readonly int _chunkDurationMs;
        private readonly object _mixLock = new();

        private WasapiLoopbackCapture? _loopbackCapture;
        private WasapiCapture? _micCapture;
        private BufferedWaveProvider? _loopbackBuffer;
        private BufferedWaveProvider? _micBuffer;

        private VolumeSampleProvider? _loopbackVolume;
        private VolumeSampleProvider? _micVolume;
        private float _loopbackCurrentVolume;
        private float _micCurrentVolume;
        private float _loopbackTargetVolume;
        private float _micTargetVolume;

        private IWaveProvider? _pcm16Provider;
        private CancellationTokenSource? _cts;
        private Task? _readerTask;
        private CancellationTokenSource? _fadeCts;
        private Task? _fadeTask;
        private readonly AutoResetEvent _dataAvailableEvent = new(false);
        private bool _silenceOnlyMode;
        private byte[]? _silenceChunkTemplate;

        public WaveFormat OutputWaveFormat { get; }

        public bool HasLoopbackCapture => _loopbackCapture != null;

        public bool HasMicCapture => _micCapture != null;

        public event Action<byte[]>? Pcm16ChunkReady;

        public WasapiPcm16AudioSource(
            string? loopbackDeviceId,
            string? micDeviceId,
            int chunkDurationMs,
            bool enableLoopback,
            bool enableMic,
            int sampleRate = 16000)
        {
            _loopbackDeviceId = string.IsNullOrWhiteSpace(loopbackDeviceId) ? null : loopbackDeviceId;
            _micDeviceId = string.IsNullOrWhiteSpace(micDeviceId) ? null : micDeviceId;
            _chunkDurationMs = chunkDurationMs <= 0 ? 200 : chunkDurationMs;
            _loopbackCurrentVolume = enableLoopback ? 1f : 0f;
            _micCurrentVolume = enableMic ? 1f : 0f;
            _loopbackTargetVolume = _loopbackCurrentVolume;
            _micTargetVolume = _micCurrentVolume;
            OutputWaveFormat = new WaveFormat(Math.Clamp(sampleRate, 8000, 48000), 16, 1);
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_readerTask != null)
            {
                throw new InvalidOperationException("Audio source already started.");
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("NAudio WASAPI is only supported on Windows.");
            }

            using var enumerator = new MMDeviceEnumerator();
            var loopbackDevice = GetDevice(enumerator, _loopbackDeviceId, DataFlow.Render);
            var micDevice = GetDevice(enumerator, _micDeviceId, DataFlow.Capture);

            var wantsLoopback = _loopbackCurrentVolume > 0.0001f;
            var wantsMic = _micCurrentVolume > 0.0001f;

            if ((wantsLoopback || wantsMic) && loopbackDevice == null && micDevice == null)
            {
                throw new InvalidOperationException("Unable to resolve audio device.");
            }

            if (loopbackDevice != null && _loopbackCurrentVolume > 0.0001f)
            {
                _loopbackCapture = new WasapiLoopbackCapture(loopbackDevice);
                _loopbackBuffer = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(5),
                    ReadFully = true
                };

                _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
                _loopbackCapture.RecordingStopped += OnRecordingStopped;
            }

            if (micDevice != null && _micCurrentVolume > 0.0001f)
            {
                _micCapture = new WasapiCapture(micDevice);
                _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(5),
                    ReadFully = true
                };

                _micCapture.DataAvailable += OnMicDataAvailable;
                _micCapture.RecordingStopped += OnRecordingStopped;
            }

            var providers = new System.Collections.Generic.List<ISampleProvider>(2);
            if (_loopbackBuffer != null)
            {
                var loopbackProvider = ConvertToOutputFormat(_loopbackBuffer.ToSampleProvider());
                _loopbackVolume = new VolumeSampleProvider(loopbackProvider)
                {
                    Volume = _loopbackCurrentVolume
                };
                providers.Add(_loopbackVolume);
            }

            if (_micBuffer != null)
            {
                var micProvider = ConvertToOutputFormat(_micBuffer.ToSampleProvider());
                _micVolume = new VolumeSampleProvider(micProvider)
                {
                    Volume = _micCurrentVolume
                };
                providers.Add(_micVolume);
            }

            if (providers.Count == 0)
            {
                _silenceOnlyMode = true;
                _silenceChunkTemplate = new byte[GetChunkByteCount(_chunkDurationMs)];
                _pcm16Provider = null;
            }
            else
            {
                _silenceOnlyMode = false;
                _silenceChunkTemplate = null;

                var mixer = new MixingSampleProvider(providers)
                {
                    ReadFully = true
                };
                _pcm16Provider = new SampleToWaveProvider16(mixer);
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readerTask = Task.Run(() => ReaderLoop(_cts.Token), CancellationToken.None);

            _loopbackCapture?.StartRecording();
            _micCapture?.StartRecording();
            return Task.CompletedTask;
        }

        public void UpdateRouting(bool enableLoopback, bool enableMic, int fadeMilliseconds = 30)
        {
            lock (_mixLock)
            {
                _loopbackTargetVolume = enableLoopback ? 1f : 0f;
                _micTargetVolume = enableMic ? 1f : 0f;
            }

            StartFadeTask(Math.Clamp(fadeMilliseconds, 10, 50));
        }

        public async Task StopAsync()
        {
            var cts = _cts;
            if (cts == null)
            {
                return;
            }

            cts.Cancel();
            _dataAvailableEvent.Set();
            _fadeCts?.Cancel();

            if (_loopbackCapture != null)
            {
                try
                {
                    _loopbackCapture.StopRecording();
                }
                catch
                {
                    // ignore
                }
            }

            if (_micCapture != null)
            {
                try
                {
                    _micCapture.StopRecording();
                }
                catch
                {
                    // ignore
                }
            }

            if (_readerTask != null)
            {
                try
                {
                    await _readerTask.ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }

            if (_fadeTask != null)
            {
                try
                {
                    await _fadeTask.ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }

            Cleanup();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _dataAvailableEvent.Dispose();
        }

        private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_loopbackBuffer == null)
            {
                return;
            }

            _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _dataAvailableEvent.Set();
        }

        private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_micBuffer == null)
            {
                return;
            }

            _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _dataAvailableEvent.Set();
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            _dataAvailableEvent.Set();
        }

        private async Task ReaderLoop(CancellationToken token)
        {
            if (_silenceOnlyMode)
            {
                var template = _silenceChunkTemplate ?? new byte[GetChunkByteCount(_chunkDurationMs)];
                var stopwatchSilence = System.Diagnostics.Stopwatch.StartNew();
                var nextDueSilence = TimeSpan.FromMilliseconds(_chunkDurationMs);

                while (!token.IsCancellationRequested)
                {
                    var chunk = new byte[template.Length];
                    Buffer.BlockCopy(template, 0, chunk, 0, template.Length);
                    Pcm16ChunkReady?.Invoke(chunk);

                    var delaySilence = nextDueSilence - stopwatchSilence.Elapsed;
                    if (delaySilence > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delaySilence, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }

                    nextDueSilence += TimeSpan.FromMilliseconds(_chunkDurationMs);
                    await Task.Yield();
                }

                return;
            }

            if (_pcm16Provider == null)
            {
                return;
            }

            var chunkBytes = GetChunkByteCount(_chunkDurationMs);
            var buffer = new byte[chunkBytes];
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var nextDue = TimeSpan.FromMilliseconds(_chunkDurationMs);

            while (!token.IsCancellationRequested)
            {
                var filled = 0;
                var frameDeadline = stopwatch.Elapsed + TimeSpan.FromMilliseconds(_chunkDurationMs);
                while (filled < buffer.Length && !token.IsCancellationRequested)
                {
                    if (!HasBufferedData())
                    {
                        var remaining = frameDeadline - stopwatch.Elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        _dataAvailableEvent.WaitOne(remaining > TimeSpan.FromMilliseconds(50)
                            ? TimeSpan.FromMilliseconds(50)
                            : remaining);
                        continue;
                    }

                    var read = _pcm16Provider.Read(buffer, filled, buffer.Length - filled);
                    if (read <= 0)
                    {
                        var remaining = frameDeadline - stopwatch.Elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        _dataAvailableEvent.WaitOne(remaining > TimeSpan.FromMilliseconds(20)
                            ? TimeSpan.FromMilliseconds(20)
                            : remaining);
                        continue;
                    }

                    filled += read;
                }

                if (filled < buffer.Length)
                {
                    Array.Clear(buffer, filled, buffer.Length - filled);
                }

                var chunk = new byte[buffer.Length];
                Buffer.BlockCopy(buffer, 0, chunk, 0, buffer.Length);
                Pcm16ChunkReady?.Invoke(chunk);

                var delay = nextDue - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                nextDue += TimeSpan.FromMilliseconds(_chunkDurationMs);

                await Task.Yield();
            }
        }

        private bool HasBufferedData()
        {
            var loopbackBuffered = _loopbackBuffer?.BufferedBytes ?? 0;
            var micBuffered = _micBuffer?.BufferedBytes ?? 0;
            return loopbackBuffered > 0 || micBuffered > 0;
        }

        private int GetChunkByteCount(int chunkDurationMs)
        {
            var ms = Math.Clamp(chunkDurationMs, 20, 2000);
            var bytes = (OutputWaveFormat.AverageBytesPerSecond * ms) / 1000;
            var align = OutputWaveFormat.BlockAlign;
            if (align <= 0)
            {
                return bytes;
            }

            return Math.Max(align, bytes - (bytes % align));
        }

        private ISampleProvider ConvertToOutputFormat(ISampleProvider provider)
        {
            var mono = ToMono(provider);
            if (mono.WaveFormat.SampleRate == OutputWaveFormat.SampleRate)
            {
                return mono;
            }

            return new WdlResamplingSampleProvider(mono, OutputWaveFormat.SampleRate);
        }

        private static ISampleProvider ToMono(ISampleProvider provider)
        {
            if (provider.WaveFormat.Channels == 1)
            {
                return provider;
            }

            if (provider.WaveFormat.Channels == 2)
            {
                return new StereoToMonoSampleProvider(provider)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
            }

            var mux = new MultiplexingSampleProvider(new[] { provider }, 1);
            mux.ConnectInputToOutput(0, 0);
            return mux;
        }

        private static MMDevice? GetDevice(MMDeviceEnumerator enumerator, string? deviceId, DataFlow flow)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    try
                    {
                        return enumerator.GetDevice(deviceId);
                    }
                    catch
                    {
                        // fallback to default endpoint when persisted device id is stale/missing
                    }
                }

                return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            }
            catch
            {
                return null;
            }
        }

        private void StartFadeTask(int fadeMilliseconds)
        {
            _fadeCts?.Cancel();
            _fadeCts?.Dispose();
            _fadeCts = new CancellationTokenSource();
            var token = _fadeCts.Token;

            _fadeTask = Task.Run(async () =>
            {
                var steps = Math.Max(1, fadeMilliseconds / 10);
                for (var i = 0; i < steps && !token.IsCancellationRequested; i++)
                {
                    lock (_mixLock)
                    {
                        _loopbackCurrentVolume = StepVolume(_loopbackCurrentVolume, _loopbackTargetVolume, steps - i);
                        _micCurrentVolume = StepVolume(_micCurrentVolume, _micTargetVolume, steps - i);
                        if (_loopbackVolume != null)
                        {
                            _loopbackVolume.Volume = _loopbackCurrentVolume;
                        }

                        if (_micVolume != null)
                        {
                            _micVolume.Volume = _micCurrentVolume;
                        }
                    }

                    try
                    {
                        await Task.Delay(10, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                lock (_mixLock)
                {
                    _loopbackCurrentVolume = _loopbackTargetVolume;
                    _micCurrentVolume = _micTargetVolume;
                    if (_loopbackVolume != null)
                    {
                        _loopbackVolume.Volume = _loopbackCurrentVolume;
                    }

                    if (_micVolume != null)
                    {
                        _micVolume.Volume = _micCurrentVolume;
                    }
                }
            }, token);
        }

        private static float StepVolume(float current, float target, int remainSteps)
        {
            if (remainSteps <= 1)
            {
                return target;
            }

            return current + ((target - current) / remainSteps);
        }

        private void Cleanup()
        {
            _cts?.Dispose();
            _cts = null;
            _fadeCts?.Dispose();
            _fadeCts = null;

            if (_loopbackCapture != null)
            {
                _loopbackCapture.DataAvailable -= OnLoopbackDataAvailable;
                _loopbackCapture.RecordingStopped -= OnRecordingStopped;
                _loopbackCapture.Dispose();
                _loopbackCapture = null;
            }

            if (_micCapture != null)
            {
                _micCapture.DataAvailable -= OnMicDataAvailable;
                _micCapture.RecordingStopped -= OnRecordingStopped;
                _micCapture.Dispose();
                _micCapture = null;
            }

            _readerTask = null;
            _fadeTask = null;
            _loopbackBuffer = null;
            _micBuffer = null;
            _loopbackVolume = null;
            _micVolume = null;
            _pcm16Provider = null;
            _silenceOnlyMode = false;
            _silenceChunkTemplate = null;
        }
    }
}
