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
        private IWaveProvider? _micMonoProvider;
        private IWaveProvider? _loopbackMonoProvider;
        private CancellationTokenSource? _cts;
        private Task? _readerTask;
        private CancellationTokenSource? _fadeCts;
        private Task? _fadeTask;
        private readonly AutoResetEvent _dataAvailableEvent = new(false);
        private bool _silenceOnlyMode;
        private byte[]? _silenceChunkTemplate;
        private readonly bool _interleavedDualChannel;
        private readonly bool _emitCapturedFrames;
        private readonly Action<string>? _log;
        private long _loopbackBytesReceived;
        private long _micBytesReceived;
        private int _diagFrameCounter;

        public WaveFormat OutputWaveFormat { get; }

        public bool HasLoopbackCapture => _loopbackCapture != null;

        public bool HasMicCapture => _micCapture != null;

        public event Action<byte[]>? Pcm16ChunkReady;
        public event Action<CapturedAudioFrame>? CapturedAudioFrameReady;

        /// <param name="interleavedDualChannel">
        /// true = 输出 2 声道交错 PCM（ch1=mic, ch2=loopback），供 MAS AEC 使用参考通道。
        /// false = 保持现有混合单声道行为。
        /// </param>
        public WasapiPcm16AudioSource(
            string? loopbackDeviceId,
            string? micDeviceId,
            int chunkDurationMs,
            bool enableLoopback,
            bool enableMic,
            int sampleRate = 16000,
            bool interleavedDualChannel = false,
            bool emitCapturedFrames = false,
            Action<string>? log = null)
        {
            _loopbackDeviceId = string.IsNullOrWhiteSpace(loopbackDeviceId) ? null : loopbackDeviceId;
            _micDeviceId = string.IsNullOrWhiteSpace(micDeviceId) ? null : micDeviceId;
            _chunkDurationMs = chunkDurationMs <= 0 ? 200 : chunkDurationMs;
            _loopbackCurrentVolume = enableLoopback ? 1f : 0f;
            _micCurrentVolume = enableMic ? 1f : 0f;
            _loopbackTargetVolume = _loopbackCurrentVolume;
            _micTargetVolume = _micCurrentVolume;
            _interleavedDualChannel = interleavedDualChannel && enableLoopback && enableMic;
            _emitCapturedFrames = emitCapturedFrames;
            _log = log;
            var channels = _interleavedDualChannel ? 2 : 1;
            OutputWaveFormat = new WaveFormat(Math.Clamp(sampleRate, 8000, 48000), 16, channels);
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

            _log?.Invoke($"[音源诊断] StartAsync 期望[环回={wantsLoopback} 麦={wantsMic}] 配置环回ID='{_loopbackDeviceId}' 配置麦ID='{_micDeviceId}' 解析环回设备='{loopbackDevice?.FriendlyName ?? "(null)"}' (ID={loopbackDevice?.ID ?? "(null)"}) 解析麦设备='{micDevice?.FriendlyName ?? "(null)"}' (ID={micDevice?.ID ?? "(null)"}) 输出格式={OutputWaveFormat.SampleRate}Hz/{OutputWaveFormat.BitsPerSample}bit/{OutputWaveFormat.Channels}ch");

            if ((wantsLoopback || wantsMic) && loopbackDevice == null && micDevice == null)
            {
                throw new InvalidOperationException("Unable to resolve audio device.");
            }

            if (loopbackDevice != null && _loopbackCurrentVolume > 0.0001f)
            {
                _loopbackCapture = new WasapiLoopbackCapture(loopbackDevice);
                _log?.Invoke($"[音源诊断] WasapiLoopbackCapture 已创建 设备='{loopbackDevice.FriendlyName}' 原生格式={_loopbackCapture.WaveFormat.SampleRate}Hz/{_loopbackCapture.WaveFormat.BitsPerSample}bit/{_loopbackCapture.WaveFormat.Channels}ch encoding={_loopbackCapture.WaveFormat.Encoding}");
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
                _log?.Invoke($"[音源诊断] WasapiCapture 已创建 设备='{micDevice.FriendlyName}' 原生格式={_micCapture.WaveFormat.SampleRate}Hz/{_micCapture.WaveFormat.BitsPerSample}bit/{_micCapture.WaveFormat.Channels}ch encoding={_micCapture.WaveFormat.Encoding}");
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
                var loopbackMono = ConvertToOutputRate(_loopbackBuffer.ToSampleProvider());
                _loopbackMonoProvider = new SampleToWaveProvider16(loopbackMono);

                var loopbackProvider = ConvertToOutputFormat(_loopbackBuffer.ToSampleProvider());
                _loopbackVolume = new VolumeSampleProvider(loopbackProvider)
                {
                    Volume = _loopbackCurrentVolume
                };
                providers.Add(_loopbackVolume);
            }

            if (_micBuffer != null)
            {
                var micMono = ConvertToOutputRate(_micBuffer.ToSampleProvider());
                _micMonoProvider = new SampleToWaveProvider16(micMono);

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
            else if (_interleavedDualChannel && _micBuffer != null && _loopbackBuffer != null)
            {
                // 双通道模式：mic 和 loopback 各自独立转为单声道 PCM16，不混合
                _silenceOnlyMode = false;
                _silenceChunkTemplate = null;
                _pcm16Provider = null; // 不使用混合 provider
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
            _readerTask = _emitCapturedFrames && (_micMonoProvider != null || _loopbackMonoProvider != null)
                ? Task.Run(() => CapturedFrameReaderLoop(_cts.Token), CancellationToken.None)
                : _interleavedDualChannel && _micMonoProvider != null && _loopbackMonoProvider != null
                    ? Task.Run(() => DualChannelReaderLoop(_cts.Token), CancellationToken.None)
                    : Task.Run(() => ReaderLoop(_cts.Token), CancellationToken.None);

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
            var prev = System.Threading.Interlocked.Add(ref _loopbackBytesReceived, e.BytesRecorded) - e.BytesRecorded;
            // 第一次收到数据 / 每收到累计 ~10s（按 192KB/s 估算约 1.92MB）打一次
            if (prev == 0 || (prev / 2_000_000) != ((prev + e.BytesRecorded) / 2_000_000))
            {
                _log?.Invoke($"[音源诊断] 环回 OnDataAvailable 累计字节={prev + e.BytesRecorded} 本次={e.BytesRecorded}");
            }
            _dataAvailableEvent.Set();
        }

        private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_micBuffer == null)
            {
                return;
            }

            _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            var prev = System.Threading.Interlocked.Add(ref _micBytesReceived, e.BytesRecorded) - e.BytesRecorded;
            if (prev == 0 || (prev / 2_000_000) != ((prev + e.BytesRecorded) / 2_000_000))
            {
                _log?.Invoke($"[音源诊断] 麦克 OnDataAvailable 累计字节={prev + e.BytesRecorded} 本次={e.BytesRecorded}");
            }
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

        private async Task CapturedFrameReaderLoop(CancellationToken token)
        {
            var monoChunkBytes = GetMonoChunkByteCount(_chunkDurationMs);
            var micBuf = new byte[monoChunkBytes];
            var loopBuf = new byte[monoChunkBytes];
            var silenceTemplate = new byte[monoChunkBytes];
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var nextDue = TimeSpan.FromMilliseconds(_chunkDurationMs);

            while (!token.IsCancellationRequested)
            {
                var frameDeadline = stopwatch.Elapsed + TimeSpan.FromMilliseconds(_chunkDurationMs);

                while (!HasBufferedData() && !_silenceOnlyMode && !token.IsCancellationRequested)
                {
                    var remaining = frameDeadline - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    _dataAvailableEvent.WaitOne(remaining > TimeSpan.FromMilliseconds(50)
                        ? TimeSpan.FromMilliseconds(50)
                        : remaining);
                }

                var micChunk = ReadMonoChunk(_micMonoProvider, micBuf, silenceTemplate);
                var loopChunk = ReadMonoChunk(_loopbackMonoProvider, loopBuf, silenceTemplate);

                lock (_mixLock)
                {
                    Pcm16AudioMixer.ApplyGainInPlace(micChunk, _micCurrentVolume);
                    Pcm16AudioMixer.ApplyGainInPlace(loopChunk, _loopbackCurrentVolume);
                }

                // 每秒（按 chunkDurationMs 计 5 次/s 默认）抽样一次诊断
                var counter = System.Threading.Interlocked.Increment(ref _diagFrameCounter);
                var sampleEvery = Math.Max(1, 1000 / _chunkDurationMs);
                if (counter % sampleEvery == 0)
                {
                    var micRms = VadGateController.ComputeRmsPcm16(micChunk);
                    var loopRms = VadGateController.ComputeRmsPcm16(loopChunk);
                    var micPeak = Pcm16Peak(micChunk);
                    var loopPeak = Pcm16Peak(loopChunk);
                    _log?.Invoke($"[音源诊断] 帧#{counter} mic[bytes={micChunk.Length} rms={micRms:F4} peak={micPeak} vol={_micCurrentVolume:F2}] loop[bytes={loopChunk.Length} rms={loopRms:F4} peak={loopPeak} vol={_loopbackCurrentVolume:F2}]");
                }

                CapturedAudioFrameReady?.Invoke(new CapturedAudioFrame(micChunk, loopChunk, OutputWaveFormat.SampleRate));

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

        /// <summary>转为单声道并重采样到目标采样率（不带音量控制，供双通道模式使用）。</summary>
        private ISampleProvider ConvertToOutputRate(ISampleProvider provider)
        {
            var mono = ToMono(provider);
            if (mono.WaveFormat.SampleRate == OutputWaveFormat.SampleRate)
            {
                return mono;
            }

            return new WdlResamplingSampleProvider(mono, OutputWaveFormat.SampleRate);
        }

        /// <summary>双通道交错读取：ch1=mic, ch2=loopback，每帧按采样点交错写入。</summary>
        private async Task DualChannelReaderLoop(CancellationToken token)
        {
            // 每声道每 chunk 的字节数（16-bit 单声道）
            var monoChunkBytes = GetMonoChunkByteCount(_chunkDurationMs);
            var micBuf = new byte[monoChunkBytes];
            var loopBuf = new byte[monoChunkBytes];
            // 输出 = 2 声道交错，字节数翻倍
            var interleavedBytes = monoChunkBytes * 2;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var nextDue = TimeSpan.FromMilliseconds(_chunkDurationMs);

            while (!token.IsCancellationRequested)
            {
                var frameDeadline = stopwatch.Elapsed + TimeSpan.FromMilliseconds(_chunkDurationMs);

                // 等待至少一路有数据
                while (!HasBufferedData() && !token.IsCancellationRequested)
                {
                    var remaining = frameDeadline - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero) break;
                    _dataAvailableEvent.WaitOne(remaining > TimeSpan.FromMilliseconds(50)
                        ? TimeSpan.FromMilliseconds(50) : remaining);
                }

                // 分别读取 mic 和 loopback 各自的单声道 PCM
                var micFilled = _micMonoProvider!.Read(micBuf, 0, monoChunkBytes);
                if (micFilled < monoChunkBytes) Array.Clear(micBuf, micFilled, monoChunkBytes - micFilled);

                var loopFilled = _loopbackMonoProvider!.Read(loopBuf, 0, monoChunkBytes);
                if (loopFilled < monoChunkBytes) Array.Clear(loopBuf, loopFilled, monoChunkBytes - loopFilled);

                // 交错：[mic_sample0, loop_sample0, mic_sample1, loop_sample1, ...]
                var interleaved = new byte[interleavedBytes];
                var sampleCount = monoChunkBytes / 2; // 16-bit = 2 bytes per sample
                for (var i = 0; i < sampleCount; i++)
                {
                    var srcOffset = i * 2;
                    var dstOffset = i * 4; // 2 channels * 2 bytes
                    interleaved[dstOffset] = micBuf[srcOffset];
                    interleaved[dstOffset + 1] = micBuf[srcOffset + 1];
                    interleaved[dstOffset + 2] = loopBuf[srcOffset];
                    interleaved[dstOffset + 3] = loopBuf[srcOffset + 1];
                }

                Pcm16ChunkReady?.Invoke(interleaved);

                var delay = nextDue - stopwatch.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }

                nextDue += TimeSpan.FromMilliseconds(_chunkDurationMs);
                await Task.Yield();
            }
        }

        private int GetMonoChunkByteCount(int chunkDurationMs)
        {
            var ms = Math.Clamp(chunkDurationMs, 20, 2000);
            var sampleRate = OutputWaveFormat.SampleRate;
            var bytes = (sampleRate * 2 * ms) / 1000; // 16-bit mono = 2 bytes/sample
            return Math.Max(2, bytes - (bytes % 2));
        }

        private static byte[] ReadMonoChunk(IWaveProvider? provider, byte[] workBuffer, byte[] silenceTemplate)
        {
            if (provider == null)
            {
                var silence = new byte[silenceTemplate.Length];
                Buffer.BlockCopy(silenceTemplate, 0, silence, 0, silenceTemplate.Length);
                return silence;
            }

            var read = provider.Read(workBuffer, 0, workBuffer.Length);
            if (read < workBuffer.Length)
            {
                Array.Clear(workBuffer, read, workBuffer.Length - read);
            }

            var chunk = new byte[workBuffer.Length];
            Buffer.BlockCopy(workBuffer, 0, chunk, 0, workBuffer.Length);
            return chunk;
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

        private static int Pcm16Peak(byte[] buf)
        {
            if (buf == null || buf.Length < 2) return 0;
            int peak = 0;
            for (int i = 0; i + 1 < buf.Length; i += 2)
            {
                short s = (short)(buf[i] | (buf[i + 1] << 8));
                int abs = s < 0 ? -s : s;
                if (abs > peak) peak = abs;
            }
            return peak;
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
            _micMonoProvider = null;
            _loopbackMonoProvider = null;
            _silenceOnlyMode = false;
            _silenceChunkTemplate = null;
        }
    }
}
