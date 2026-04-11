using System;
using System.Collections.Generic;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 音频任务 DAG 依赖定义 — 定义每个阶段的前置依赖阶段。
    /// 前置阶段的 audio_lifecycle 必须存在且 is_stale = 0 才可开始。
    /// </summary>
    public static class AudioTaskDependencies
    {
        /// <summary>
        /// 每个阶段的前置依赖阶段列表。
        /// </summary>
        public static readonly IReadOnlyDictionary<AudioLifecycleStage, AudioLifecycleStage[]> Prerequisites
            = new Dictionary<AudioLifecycleStage, AudioLifecycleStage[]>
            {
                [AudioLifecycleStage.Transcribed]   = Array.Empty<AudioLifecycleStage>(),
                [AudioLifecycleStage.Summarized]    = new[] { AudioLifecycleStage.Transcribed },
                [AudioLifecycleStage.MindMap]       = new[] { AudioLifecycleStage.Summarized },
                [AudioLifecycleStage.Insight]       = new[] { AudioLifecycleStage.Transcribed },
                [AudioLifecycleStage.PodcastScript] = new[] { AudioLifecycleStage.Transcribed },
                [AudioLifecycleStage.PodcastAudio]  = new[] { AudioLifecycleStage.PodcastScript },
                [AudioLifecycleStage.Research]      = new[] { AudioLifecycleStage.Transcribed },
                [AudioLifecycleStage.Translated]    = new[] { AudioLifecycleStage.Transcribed },
            };

        /// <summary>
        /// 获取指定阶段的所有下游阶段（直接和间接依赖此阶段的所有阶段）。
        /// </summary>
        public static List<AudioLifecycleStage> GetDownstreamStages(AudioLifecycleStage stage)
        {
            var result = new List<AudioLifecycleStage>();
            foreach (var kvp in Prerequisites)
            {
                if (kvp.Key == stage) continue;
                foreach (var prereq in kvp.Value)
                {
                    if (prereq == stage && !result.Contains(kvp.Key))
                    {
                        result.Add(kvp.Key);
                        // 递归添加间接下游
                        foreach (var indirect in GetDownstreamStages(kvp.Key))
                        {
                            if (!result.Contains(indirect))
                                result.Add(indirect);
                        }
                    }
                }
            }
            return result;
        }
    }
}
