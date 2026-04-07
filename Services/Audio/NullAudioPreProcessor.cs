using System;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>空实现：不做任何预处理，直接透传麦克风轨。</summary>
    public sealed class NullAudioPreProcessor : IAudioPreProcessor
    {
        public string Id => "none";
        public string DisplayName => "关闭";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public byte[] Process(CapturedAudioFrame frame)
        {
            if (frame.MicPcm16.Length == 0)
            {
                return new byte[frame.ByteLength > 0 ? frame.ByteLength : 0];
            }

            var clone = new byte[frame.MicPcm16.Length];
            Buffer.BlockCopy(frame.MicPcm16, 0, clone, 0, frame.MicPcm16.Length);
            return clone;
        }

        public void Dispose()
        {
        }
    }
}
