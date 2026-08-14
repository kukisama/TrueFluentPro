namespace TrueFluentPro.Services.RealtimeSpeech.Pipeline
{
    /// <summary>
    /// 处理积木：吃一帧 PCM，吐一帧 PCM（可改写、可放大、可门控）。
    /// 不需要的环节直接不加入链；某厂商有自己的降噪实现就换一个 IAudioProcessor。
    /// </summary>
    public interface IAudioProcessor
    {
        AudioFrame Process(AudioFrame input);
    }
}
