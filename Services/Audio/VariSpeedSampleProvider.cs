using System;
using NAudio.Wave;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 通过线性插值实现变速播放的 ISampleProvider 包装。
    /// 改变播放速率但不保持音调（与大多数播放器快速倍速一致）。
    /// </summary>
    public sealed class VariSpeedSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _sourceBuffer = Array.Empty<float>();
        private double _position; // 小数位表示相邻采样点间的插值位置

        /// <summary>播放速率: 1.0 = 正常, 2.0 = 两倍速, 0.5 = 半速。</summary>
        public double PlaybackRate { get; set; } = 1.0;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public VariSpeedSampleProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var rate = Math.Clamp(PlaybackRate, 0.25, 4.0);
            if (Math.Abs(rate - 1.0) < 0.001)
            {
                return _source.Read(buffer, offset, count);
            }

            var channels = WaveFormat.Channels;
            // 需要从源读取的采样帧数（帧 = channels 个 sample）
            var framesNeeded = (int)Math.Ceiling(count / (double)channels * rate) + 2;
            var samplesNeeded = framesNeeded * channels;

            if (_sourceBuffer.Length < samplesNeeded)
            {
                _sourceBuffer = new float[samplesNeeded];
            }

            var sourceSamplesRead = _source.Read(_sourceBuffer, 0, samplesNeeded);
            if (sourceSamplesRead == 0) return 0;

            var sourceFrames = sourceSamplesRead / channels;
            var outputFrames = count / channels;
            var framesWritten = 0;
            _position = 0;

            for (int i = 0; i < outputFrames; i++)
            {
                var srcFrame = (int)_position;
                var frac = (float)(_position - srcFrame);

                if (srcFrame + 1 >= sourceFrames)
                    break;

                for (int ch = 0; ch < channels; ch++)
                {
                    var s0 = _sourceBuffer[srcFrame * channels + ch];
                    var s1 = _sourceBuffer[(srcFrame + 1) * channels + ch];
                    buffer[offset + i * channels + ch] = s0 + (s1 - s0) * frac;
                }

                framesWritten++;
                _position += rate;
            }

            return framesWritten * channels;
        }
    }
}
