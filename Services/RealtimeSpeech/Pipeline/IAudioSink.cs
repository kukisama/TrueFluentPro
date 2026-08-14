namespace TrueFluentPro.Services.RealtimeSpeech.Pipeline
{
    /// <summary>
    /// 投递积木：吃一帧 PCM，做副作用（落盘 / 发 WebSocket / 喂 SDK），不回写。
    /// 例如 MP3 录音 Sink、ASR WebSocket 投递 Sink。
    /// </summary>
    public interface IAudioSink
    {
        void Write(AudioFrame frame);

        /// <summary>会话结束时调用，完成收尾（刷盘 / 关闭通道）。</summary>
        void Close();
    }
}
