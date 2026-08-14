using System;
using TrueFluentPro.Services.Audio;
using TrueFluentPro.Services.RealtimeSpeech.Pipeline;

namespace TrueFluentPro.Services.RealtimeSpeech.Sinks
{
    /// <summary>
    /// 录音落盘投递积木：把处理后的单声道 PCM16 写成 MP3。
    /// 薄封装现成的厂商无关组件 <see cref="ProcessedAudioMp3RecordingSink"/>，不复制其编码逻辑。
    /// </summary>
    public sealed class Mp3RecordingSink : IAudioSink
    {
        private readonly ProcessedAudioMp3RecordingSink _inner;
        private readonly object _gate = new();
        private bool _closed;

        public Mp3RecordingSink(
            string mp3Path,
            int sampleRate,
            int bitrateKbps,
            bool autoGainEnabled,
            double targetRms,
            double minGain,
            double maxGain,
            double smoothing)
        {
            Mp3Path = mp3Path;
            _inner = new ProcessedAudioMp3RecordingSink(
                mp3Path,
                sampleRate,
                bitrateKbps,
                autoGainEnabled,
                targetRms,
                minGain,
                maxGain,
                smoothing);
        }

        /// <summary>录音文件路径，供会话结束后注册到批量复盘。</summary>
        public string Mp3Path { get; }

        public void Write(AudioFrame frame)
        {
            if (frame.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _inner.WriteChunk(frame.Pcm16);
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                try
                {
                    _inner.Dispose();
                }
                catch
                {
                    // 收尾失败不影响其它分支。
                }
            }
        }
    }
}
