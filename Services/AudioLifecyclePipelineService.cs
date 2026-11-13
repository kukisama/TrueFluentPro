using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Speech;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 音频生命周期管道服务 — 编排从转录到 TTS 的全流程，
    /// 利用数据库缓存避免重复生成，自动补齐缺失阶段。
    /// </summary>
    public sealed class AudioLifecyclePipelineService
    {
        private readonly IAudioLibraryRepository _audioRepo;
        private readonly IAudioLifecycleRepository _lifecycleRepo;
        private readonly IAiInsightService _aiInsightService;
        private readonly SpeechSynthesisService _ttsService;

        /// <summary>转录或总结保存后通知音频 ID；订阅方负责调度 UI 刷新。</summary>
        public event Action<string>? RecordingTitleChanged;

        public AudioLifecyclePipelineService(
            IAudioLibraryRepository audioRepo,
            IAudioLifecycleRepository lifecycleRepo,
            IAiInsightService aiInsightService,
            SpeechSynthesisService ttsService)
        {
            _audioRepo = audioRepo;
            _lifecycleRepo = lifecycleRepo;
            _aiInsightService = aiInsightService;
            _ttsService = ttsService;
        }

        // ── 缓存加载 ──────────────────────────────────

        /// <summary>
        /// 根据已保存的转录和总结返回显示名称；重新生成期间仍保留上次成功标题。
        /// 兼容当前绝对路径与旧导入的 Sessions 相对路径（使用 / 分隔）。
        /// </summary>
        public string GetAudioDisplayName(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            var audio = _audioRepo.GetByFilePath(filePath);
            if (audio == null && !string.IsNullOrWhiteSpace(filePath))
            {
                string absolutePath;
                string relativePath;
                try
                {
                    var root = Path.GetFullPath(PathManager.Instance.SessionsPath);
                    absolutePath = Path.GetFullPath(filePath, root);
                    relativePath = Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return fileName;
                }

                if (!string.Equals(filePath, absolutePath, StringComparison.Ordinal))
                    audio = _audioRepo.GetByFilePath(absolutePath);

                if (audio == null && !Path.IsPathRooted(relativePath)
                    && relativePath != ".." && !relativePath.StartsWith("../", StringComparison.Ordinal)
                    && !string.Equals(filePath, relativePath, StringComparison.Ordinal))
                    audio = _audioRepo.GetByFilePath(relativePath);
            }

            if (audio == null || audio.IsDeleted) return fileName;

            var transcript = _lifecycleRepo.Get(audio.Id, AudioLifecycleStage.Transcribed.ToString());
            var summary = _lifecycleRepo.Get(audio.Id, AudioLifecycleStage.Summarized.ToString());
            if (transcript == null || string.IsNullOrWhiteSpace(transcript.ContentJson)
                || summary == null || string.IsNullOrWhiteSpace(summary.ContentJson))
                return fileName;

            return AudioRecordingTitle.Build(fileName, audio.CreatedAt, summary.ContentJson);
        }

        /// <summary>
        /// 列出所有未删除且已有非空转录的数据库记录，包括过期内容，不检查源文件是否存在。
        /// IsStale 表示转录或总结任一阶段已过期，供导出提示使用。
        /// </summary>
        public List<(AudioItemRecord Audio, string? TranscriptJson, string? SummaryMarkdown, bool IsStale)> ListStoredTranscripts()
        {
            var result = new List<(AudioItemRecord Audio, string? TranscriptJson, string? SummaryMarkdown, bool IsStale)>();
            foreach (var audio in _audioRepo.List(limit: int.MaxValue))
            {
                if (audio.IsDeleted) continue;
                var transcript = _lifecycleRepo.Get(audio.Id, AudioLifecycleStage.Transcribed.ToString());
                if (transcript == null || string.IsNullOrWhiteSpace(transcript.ContentJson)) continue;

                var summary = _lifecycleRepo.Get(audio.Id, AudioLifecycleStage.Summarized.ToString());
                result.Add((audio, transcript.ContentJson, summary?.ContentJson,
                    transcript.IsStale || summary?.IsStale == true));
            }
            return result;
        }

        /// <summary>
        /// 尝试从数据库加载指定阶段的缓存内容。
        /// 如果不存在或已过期返回 null。
        /// </summary>
        public string? TryLoadCachedContent(string audioItemId, AudioLifecycleStage stage)
        {
            return TryLoadCachedContent(audioItemId, stage.ToString());
        }

        /// <summary>
        /// 尝试从数据库加载指定阶段（字符串键）的缓存内容。
        /// 支持自定义阶段名称。
        /// </summary>
        public string? TryLoadCachedContent(string audioItemId, string stageKey)
        {
            var record = _lifecycleRepo.Get(audioItemId, stageKey);
            if (record == null || record.IsStale) return null;
            return record.ContentJson;
        }

        /// <summary>
        /// 尝试加载指定阶段的缓存文件路径（如 TTS 音频）。
        /// </summary>
        public string? TryLoadCachedFilePath(string audioItemId, AudioLifecycleStage stage)
        {
            var record = _lifecycleRepo.Get(audioItemId, stage.ToString());
            if (record == null || record.IsStale) return null;
            if (!string.IsNullOrWhiteSpace(record.FilePath) && File.Exists(record.FilePath))
                return record.FilePath;
            return null;
        }

        /// <summary>获取音频项的所有已完成阶段。</summary>
        public List<AudioLifecycleStage> GetCompletedStages(string audioItemId)
        {
            var records = _lifecycleRepo.GetAllStages(audioItemId);
            var stages = new List<AudioLifecycleStage>();
            foreach (var r in records)
            {
                if (!r.IsStale && Enum.TryParse<AudioLifecycleStage>(r.Stage, out var stage))
                    stages.Add(stage);
            }
            return stages;
        }

        // ── 保存阶段结果 ──────────────────────────────

        /// <summary>保存文本类阶段结果到数据库。</summary>
        public void SaveStageContent(string audioItemId, AudioLifecycleStage stage, string contentJson)
            => SaveStageContent(audioItemId, stage.ToString(), contentJson);

        /// <summary>保存文本类阶段结果到数据库（支持自定义阶段字符串标识）。</summary>
        public void SaveStageContent(string audioItemId, string stage, string contentJson)
        {
            contentJson = StripMarkdownCodeFence(contentJson);
            var now = DateTime.Now;
            _lifecycleRepo.Upsert(new AudioLifecycleRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                AudioItemId = audioItemId,
                Stage = stage,
                ContentJson = contentJson,
                IsStale = false,
                GeneratedAt = now,
                UpdatedAt = now,
            });

            if (stage == nameof(AudioLifecycleStage.Transcribed) || stage == nameof(AudioLifecycleStage.Summarized))
            {
                var handlers = RecordingTitleChanged;
                if (handlers == null) return;
                foreach (Action<string> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(audioItemId);
                    }
                    catch (Exception ex)
                    {
                        // 通知失败不能将已经成功的存储误报为失败，也不能阻断其他订阅者。
                        System.Diagnostics.Debug.WriteLine($"刷新音频显示名称失败: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>保存文件类阶段结果到数据库（如 TTS 音频文件路径）。</summary>
        public void SaveStageFile(string audioItemId, AudioLifecycleStage stage, string filePath, string? contentJson = null)
        {
            var now = DateTime.Now;
            _lifecycleRepo.Upsert(new AudioLifecycleRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                AudioItemId = audioItemId,
                Stage = stage.ToString(),
                ContentJson = contentJson,
                FilePath = filePath,
                IsStale = false,
                GeneratedAt = now,
                UpdatedAt = now,
            });
        }

        /// <summary>
        /// 将指定阶段自身的 lifecycle 标记为 stale。
        /// 用于重新生成前确保下游依赖检查不会通过旧数据。
        /// </summary>
        public void MarkStageStale(string audioItemId, AudioLifecycleStage stage)
        {
            _lifecycleRepo.MarkStale(audioItemId, stage.ToString());
        }

        /// <summary>将自定义字符串阶段标记为 stale。</summary>
        public void MarkStageStale(string audioItemId, string stageKey)
        {
            if (!string.IsNullOrWhiteSpace(stageKey))
                _lifecycleRepo.MarkStale(audioItemId, stageKey.Trim());
        }

        /// <summary>
        /// 当转录结果发生变化时，标记所有下游阶段为过期。
        /// </summary>
        public void InvalidateDownstreamStages(string audioItemId, AudioLifecycleStage changedStage)
        {
            // 转录是上游根，任何转录变更都使全部下游过期
            if (changedStage == AudioLifecycleStage.Transcribed)
            {
                _lifecycleRepo.MarkStale(audioItemId); // 标记该音频的所有阶段
                return;
            }

            // 台本变更只影响播客音频
            if (changedStage == AudioLifecycleStage.PodcastScript)
            {
                _lifecycleRepo.MarkStale(audioItemId, AudioLifecycleStage.PodcastAudio.ToString());
            }
        }

        // ── TTS 合成 ──────────────────────────────────

        /// <summary>
        /// 将播客台本文本合成为音频。
        /// </summary>
        public async Task<string> SynthesizePodcastAsync(
            SpeechSynthesisService.TtsAuthContext ttsAuth,
            string audioItemId,
            string podcastScript,
            Dictionary<string, SpeakerProfile> speakerProfiles,
            string outputFormat,
            string outputDirectory,
            CancellationToken ct = default)
        {
            // 解析台本
            var lines = SpeechSynthesisService.ParseScript(podcastScript);
            if (lines.Count == 0)
                throw new InvalidOperationException("台本中没有识别到有效的发言人行。请使用「发言人 A：文本」格式。");

            if (lines.Count > 50)
                throw new InvalidOperationException($"台本包含 {lines.Count} 条发言，超过 Azure TTS REST API 的 50 条限制。请减少对话轮次。");

            // 为每条台本行构建 SSML segment
            var segments = new List<(string Text, VoiceInfo Voice, string? Style, double StyleDegree, string? Role, string? Rate, string? Pitch, SpeechAdvancedOptions? Advanced)>();

            foreach (var (speaker, text) in lines)
            {
                if (!speakerProfiles.TryGetValue(speaker, out var profile) || profile.Voice == null)
                    throw new InvalidOperationException($"发言人 {speaker} 未配置语音。请先在控制面板中为所有发言人分配语音。");

                segments.Add((text, profile.Voice, profile.SelectedStyle, profile.StyleDegree,
                    profile.SelectedRole, profile.Rate, profile.Pitch, profile.AdvancedOptions));
            }

            // 构建 SSML
            var ssml = SpeechSynthesisService.BuildMultiVoiceSsml(segments);

            // 合成
            var audioBytes = await _ttsService.SynthesizeAsync(ttsAuth, ssml, outputFormat, ct);

            // 保存文件
            Directory.CreateDirectory(outputDirectory);
            var ext = outputFormat.Contains("mp3") ? ".mp3"
                    : outputFormat.Contains("pcm") || outputFormat.Contains("riff") ? ".wav"
                    : outputFormat.Contains("opus") ? ".opus"
                    : outputFormat.Contains("ogg") ? ".ogg"
                    : ".audio";
            var safeAudioId = new string(audioItemId.Where(char.IsLetterOrDigit).Take(16).ToArray());
            if (string.IsNullOrWhiteSpace(safeAudioId)) safeAudioId = "audio";
            var fileName = $"podcast_{safeAudioId}_{DateTime.Now:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}{ext}";
            var outputPath = Path.Combine(outputDirectory, fileName);
            await using (var output = new FileStream(
                outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await output.WriteAsync(audioBytes, ct);
            }

            // 保存到生命周期
            var configJson = JsonSerializer.Serialize(new
            {
                SpeakerCount = speakerProfiles.Count,
                LineCount = lines.Count,
                OutputFormat = outputFormat,
                FileSizeBytes = audioBytes.Length,
            });
            SaveStageFile(audioItemId, AudioLifecycleStage.PodcastAudio, outputPath, configJson);

            return outputPath;
        }

        // ── 确保音频库项目存在 ─────────────────────────

        /// <summary>确保音频文件在数据库中有对应的 AudioItemRecord，返回其 ID。</summary>
        public string EnsureAudioItem(string filePath)
        {
            var existing = _audioRepo.GetByFilePath(filePath);
            if (existing != null) return existing.Id;

            var id = Guid.NewGuid().ToString("N");
            var fi = new FileInfo(filePath);
            _audioRepo.Upsert(new AudioItemRecord
            {
                Id = id,
                FilePath = filePath,
                FileName = fi.Name,
                DirectoryPath = fi.DirectoryName ?? "",
                FileSize = fi.Exists ? fi.Length : 0,
                ProcessingState = "None",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            });
            return id;
        }

        /// <summary>剥离 AI 输出偶尔包裹的 ```markdown / ``` 代码围栏。</summary>
        private static string StripMarkdownCodeFence(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var s = text.AsSpan().Trim();
            // 匹配 ```markdown 或 ``` 开头
            if (s.StartsWith("```"))
            {
                // 跳过 ``` 及同行标签 (markdown / md / …)
                int firstNewline = s.IndexOf('\n');
                if (firstNewline < 0) return text; // 无换行，原样返回
                s = s.Slice(firstNewline + 1);
                // 去掉末尾 ```
                if (s.EndsWith("```"))
                    s = s.Slice(0, s.Length - 3);
                return s.Trim().ToString();
            }
            return text;
        }
    }
}
