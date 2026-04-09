using System;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 统一编排：采集分轨帧 -> 插件预处理 -> 同时分发到录音与识别。
    /// 目标是让录音和识别听到同一份处理后音频，避免双管线偏差。
    /// </summary>
    public sealed class AudioProcessingCoordinator : IDisposable
    {
        private readonly IAudioPreProcessor _preProcessor;
        private readonly IAudioRecognitionSink _recognitionSink;
        private readonly IAudioRecordingSink? _recordingSink;
        private readonly AutoGainProcessor? _recognitionAutoGain;
        private bool _recognizeLoopback;
        private bool _recognizeMic;
        private bool _recordLoopback;
        private bool _recordMic;

        /// <summary>当前使用的预处理器</summary>
        public IAudioPreProcessor PreProcessor => _preProcessor;

        public AudioProcessingCoordinator(
            IAudioPreProcessor preProcessor,
            IAudioRecognitionSink recognitionSink,
            IAudioRecordingSink? recordingSink,
            bool recognizeLoopback,
            bool recognizeMic,
            bool recordLoopback,
            bool recordMic,
            bool recognitionAutoGainEnabled,
            double targetRms,
            double minGain,
            double maxGain,
            double smoothing)
        {
            _preProcessor = preProcessor;
            _recognitionSink = recognitionSink;
            _recordingSink = recordingSink;
            _recognizeLoopback = recognizeLoopback;
            _recognizeMic = recognizeMic;
            _recordLoopback = recordLoopback;
            _recordMic = recordMic;
            _recognitionAutoGain = recognitionAutoGainEnabled
                ? new AutoGainProcessor(targetRms, minGain, maxGain, smoothing)
                : null;
        }

        public void UpdateRouting(bool recognizeLoopback, bool recognizeMic, bool recordLoopback, bool recordMic)
        {
            _recognizeLoopback = recognizeLoopback;
            _recognizeMic = recognizeMic;
            _recordLoopback = recordLoopback;
            _recordMic = recordMic;
        }

        public byte[] ProcessAndDispatch(CapturedAudioFrame frame)
        {
            var cleanMic = _preProcessor.Process(frame);
            var recognitionChunk = BuildOutputChunk(cleanMic, frame.ReferencePcm16, _recognizeMic, _recognizeLoopback, frame.ByteLength);
            _recognitionAutoGain?.ProcessInPlace(recognitionChunk, recognitionChunk.Length);
            _recognitionSink.WriteChunk(recognitionChunk);

            if (_recordingSink != null)
            {
                var recordingChunk = BuildOutputChunk(cleanMic, frame.ReferencePcm16, _recordMic, _recordLoopback, frame.ByteLength);
                _recordingSink.WriteChunk(recordingChunk);
            }

            return recognitionChunk;
        }

        public void CloseRecognition()
        {
            _recognitionSink.Close();
        }

        private static byte[] BuildOutputChunk(byte[] cleanMic, byte[] reference, bool includeMic, bool includeReference, int fallbackLength)
        {
            if (includeMic && includeReference)
            {
                return Pcm16AudioMixer.MixMono(
                    Pcm16AudioMixer.CloneOrSilence(cleanMic, fallbackLength),
                    Pcm16AudioMixer.CloneOrSilence(reference, fallbackLength));
            }

            if (includeMic)
            {
                return Pcm16AudioMixer.CloneOrSilence(cleanMic, fallbackLength);
            }

            if (includeReference)
            {
                return Pcm16AudioMixer.CloneOrSilence(reference, fallbackLength);
            }

            return fallbackLength > 0 ? new byte[fallbackLength] : Array.Empty<byte>();
        }

        public void Dispose()
        {
            _recordingSink?.Dispose();
            _recognitionSink.Dispose();
            _preProcessor.Dispose();
        }
    }
}
