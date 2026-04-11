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
        /// 尝试从数据库加载指定阶段的缓存内容。
        /// 如果不存在或已过期返回 null。
        /// </summary>
        public string? TryLoadCachedContent(string audioItemId, AudioLifecycleStage stage)
        {
            var record = _lifecycleRepo.Get(audioItemId, stage.ToString());
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
        {
            var now = DateTime.Now;
            _lifecycleRepo.Upsert(new AudioLifecycleRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                AudioItemId = audioItemId,
                Stage = stage.ToString(),
                ContentJson = contentJson,
                IsStale = false,
                GeneratedAt = now,
                UpdatedAt = now,
            });
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
            var fileName = $"podcast_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            var outputPath = Path.Combine(outputDirectory, fileName);
            await File.WriteAllBytesAsync(outputPath, audioBytes, ct);

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
    }
}
