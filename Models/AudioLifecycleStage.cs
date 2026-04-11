namespace TrueFluentPro.Models
{
    /// <summary>音频生命周期阶段 — 代表处理管道中的各个阶段。</summary>
    public enum AudioLifecycleStage
    {
        /// <summary>已转录（第一步，后续所有 AI 处理的基础）</summary>
        Transcribed,

        /// <summary>已生成 AI 总结</summary>
        Summarized,

        /// <summary>已生成思维导图</summary>
        MindMap,

        /// <summary>已生成顿悟/洞察</summary>
        Insight,

        /// <summary>已生成播客台本文本</summary>
        PodcastScript,

        /// <summary>已基于台本生成播客音频（TTS）</summary>
        PodcastAudio,

        /// <summary>已翻译（文本翻译）</summary>
        Translated,

        /// <summary>已生成深度研究</summary>
        Research,
    }
}
