using System;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 应用层音频预处理插件：输入麦克风原始轨 + 参考轨，输出处理后的干净麦克风轨。
    /// 插件插在采集之后、录音/识别之前。
    /// </summary>
    public interface IAudioPreProcessor : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }
        bool IsAvailable { get; }
        string? UnavailableReason { get; }

        byte[] Process(CapturedAudioFrame frame);
    }
}
