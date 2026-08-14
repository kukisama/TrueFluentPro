using System;
using System.Buffers;
using System.Threading.Channels;
using TrueFluentPro.Services.RealtimeSpeech.Pipeline;

namespace TrueFluentPro.Services.RealtimeSpeech.Sinks
{
    /// <summary>
    /// 识别投递积木：把采集到的裸 PCM16 帧写入一个有界通道，由会话编排器的发送循环
    /// 取出后交给 ASR 连接器（IAsrConnector）按厂商帧约束切分并发往 WebSocket。
    /// </summary>
    /// <remarks>
    /// 该 Sink 本身不持有 WebSocket，只负责「把 PCM 交出去」；WebSocket 连接/收发生命周期由编排器统一拥有，
    /// 从而保持「一职责一文件」与积木解耦。
    /// </remarks>
    public sealed class AsrWebSocketSink : IAudioSink
    {
        private readonly ChannelWriter<byte[]> _uplink;
        private readonly Action<string>? _dropLog;

        public AsrWebSocketSink(ChannelWriter<byte[]> uplink, Action<string>? dropLog = null)
        {
            _uplink = uplink ?? throw new ArgumentNullException(nameof(uplink));
            _dropLog = dropLog;
        }

        public void Write(AudioFrame frame)
        {
            if (frame.Length == 0)
            {
                return;
            }

            // 复制一份独立缓冲交给发送循环，避免被后续采集复用的缓冲覆盖。
            var copy = new byte[frame.Length];
            Buffer.BlockCopy(frame.Pcm16, 0, copy, 0, frame.Length);

            if (!_uplink.TryWrite(copy))
            {
                _dropLog?.Invoke("[翻译流] AsrWebSocketSink 丢帧 channel-full");
            }
        }

        public void Close()
        {
            _uplink.TryComplete();
        }
    }
}
