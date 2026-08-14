using System;
using System.Collections.Generic;

namespace TrueFluentPro.Services.RealtimeSpeech.Pipeline
{
    /// <summary>
    /// 按需串联的音频处理链：每条「分支」= 一组可选处理积木 + 一个投递积木。
    /// 同一帧采集数据可同时分发到多条分支（如：录音分支接预处理→MP3，识别分支走裸 PCM→WebSocket），
    /// 各分支互不干扰、可任意增删。
    /// </summary>
    /// <remarks>
    /// 设计要点：调用方像搭积木一样 <see cref="AddBranch"/>。当某分支带处理积木时，
    /// Pipeline 会在进入该分支前 <see cref="AudioFrame.Clone"/> 一份，避免处理积木改写内容污染其它分支或采集缓冲。
    /// </remarks>
    public sealed class AudioPipeline
    {
        private readonly List<Branch> _branches = new();
        private readonly object _gate = new();
        private bool _closed;

        private readonly struct Branch
        {
            public Branch(IAudioSink sink, IReadOnlyList<IAudioProcessor> processors)
            {
                Sink = sink;
                Processors = processors;
            }

            public IAudioSink Sink { get; }
            public IReadOnlyList<IAudioProcessor> Processors { get; }
        }

        /// <summary>添加一条投递分支。processors 为该分支专属的处理积木（按序执行，可为空）。</summary>
        public AudioPipeline AddBranch(IAudioSink sink, params IAudioProcessor[] processors)
        {
            ArgumentNullException.ThrowIfNull(sink);
            _branches.Add(new Branch(sink, processors ?? Array.Empty<IAudioProcessor>()));
            return this;
        }

        public bool HasBranches => _branches.Count > 0;

        /// <summary>向所有分支推送一帧采集数据。线程安全。</summary>
        public void Push(AudioFrame frame)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                foreach (var branch in _branches)
                {
                    var current = branch.Processors.Count > 0 ? frame.Clone() : frame;
                    for (var i = 0; i < branch.Processors.Count; i++)
                    {
                        current = branch.Processors[i].Process(current);
                    }

                    branch.Sink.Write(current);
                }
            }
        }

        /// <summary>关闭所有分支投递积木（幂等）。</summary>
        public void Close()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                foreach (var branch in _branches)
                {
                    try
                    {
                        branch.Sink.Close();
                    }
                    catch
                    {
                        // 单个分支收尾失败不影响其它分支。
                    }
                }
            }
        }
    }
}
