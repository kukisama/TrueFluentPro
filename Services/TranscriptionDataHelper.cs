using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 转录数据助手 — 提供原始 JSON 解析、段落计算、纯文本提取，
    /// 并兼容旧的 TranscriptSegmentDto[] 格式。
    /// </summary>
    public static class TranscriptionDataHelper
    {
        /// <summary>
        /// 检测 lifecycle 存储的 JSON 是否为新的 RawTranscriptionData 格式（v2）。
        /// 旧格式是 TranscriptSegmentDto[] 数组，新格式是 { "v": 2, ... } 对象。
        /// </summary>
        public static bool IsRawFormat(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            var trimmed = json.TrimStart();
            if (!trimmed.StartsWith("{")) return false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("v", out var v) && v.GetInt32() >= 2;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解析 RawTranscriptionData JSON 字符串。
        /// </summary>
        public static RawTranscriptionData? ParseRawData(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<RawTranscriptionData>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将 RawTranscriptionData 序列化为 JSON 字符串。
        /// </summary>
        public static string SerializeRawData(RawTranscriptionData data)
            => JsonSerializer.Serialize(data);

        /// <summary>
        /// 从原始 API 响应重新计算段落（使用当前拆分配置）。
        /// </summary>
        public static List<TranscriptSegment> ComputeSegments(
            RawTranscriptionData rawData, BatchSubtitleSplitOptions splitOptions)
        {
            if (string.IsNullOrWhiteSpace(rawData.RawResponse))
                return new List<TranscriptSegment>();

            List<SubtitleCue> cues;
            if (string.Equals(rawData.ApiType, "batch", StringComparison.OrdinalIgnoreCase))
                cues = BatchTranscriptionParser.ParseBatchTranscriptionToCues(rawData.RawResponse, splitOptions);
            else
                cues = FastTranscriptionParser.ParseFastTranscriptionToCues(rawData.RawResponse, splitOptions);

            return BuildSegmentsFromCues(cues);
        }

        /// <summary>
        /// 从原始 API 响应 + 拆分配置计算段落，再格式化为 AI 下游文本。
        /// 与 UI 显示使用相同的拆分逻辑，确保断句一致。
        /// </summary>
        public static string ComputeTranscriptTextForAi(
            RawTranscriptionData rawData, BatchSubtitleSplitOptions splitOptions)
        {
            var segments = ComputeSegments(rawData, splitOptions);
            if (segments.Count == 0) return "(暂无转录内容)";
            var sb = new StringBuilder();
            foreach (var seg in segments)
            {
                var time = seg.StartTime.ToString(@"hh\:mm\:ss");
                sb.AppendLine($"[{time}] {seg.Speaker}: {seg.Text}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 从原始 API 响应直接提取文本（不经过拆分配置，用于特殊场景）。
        /// 优先从 phrases（Fast）/ recognizedPhrases.nBest（Batch）提取完整文本，
        /// 附加时间戳和说话人信息。
        /// </summary>
        public static string ExtractTranscriptText(RawTranscriptionData rawData)
        {
            if (string.IsNullOrWhiteSpace(rawData.RawResponse))
                return "(暂无转录内容)";

            try
            {
                using var doc = JsonDocument.Parse(rawData.RawResponse);

                if (string.Equals(rawData.ApiType, "batch", StringComparison.OrdinalIgnoreCase))
                    return ExtractBatchText(doc);
                else
                    return ExtractFastText(doc);
            }
            catch
            {
                return "(转录数据解析失败)";
            }
        }

        /// <summary>
        /// 从旧格式 TranscriptSegmentDto[] JSON 反序列化为段落列表。
        /// </summary>
        public static List<TranscriptSegment> DeserializeLegacySegments(string json)
        {
            try
            {
                var dtos = JsonSerializer.Deserialize<List<LegacySegmentDto>>(json);
                if (dtos == null || dtos.Count == 0) return new();
                return dtos.Select(d => new TranscriptSegment
                {
                    Speaker = d.Speaker ?? "",
                    SpeakerIndex = d.SpeakerIndex,
                    StartTime = new TimeSpan(d.StartTimeTicks),
                    Text = d.Text ?? "",
                }).ToList();
            }
            catch
            {
                return new();
            }
        }

        /// <summary>
        /// 从旧格式 TranscriptSegmentDto[] JSON 格式化为 AI 下游文本。
        /// </summary>
        public static string FormatLegacySegmentsForAi(string json)
        {
            var segments = DeserializeLegacySegments(json);
            if (segments.Count == 0) return "(暂无转录内容)";
            var sb = new StringBuilder();
            foreach (var seg in segments)
            {
                var time = seg.StartTime.ToString(@"hh\:mm\:ss");
                sb.AppendLine($"[{time}] {seg.Speaker}: {seg.Text}");
            }
            return sb.ToString();
        }

        // ── Fast API 文本提取 ────────────────────────────────

        private static string ExtractFastText(JsonDocument doc)
        {
            var sb = new StringBuilder();

            // 优先使用 phrases（带时间戳和说话人），格式与旧版一致
            if (doc.RootElement.TryGetProperty("phrases", out var phrases)
                && phrases.ValueKind == JsonValueKind.Array)
            {
                foreach (var phrase in phrases.EnumerateArray())
                {
                    var text = phrase.TryGetProperty("text", out var textEl)
                        ? textEl.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var speaker = phrase.TryGetProperty("speaker", out var spEl)
                        ? spEl.ToString() : "";
                    var speakerLabel = string.IsNullOrWhiteSpace(speaker) ? "Speaker" : $"Speaker {speaker}";

                    var offsetMs = phrase.TryGetProperty("offsetMilliseconds", out var oEl)
                        ? oEl.GetInt64() : 0;
                    var time = TimeSpan.FromMilliseconds(offsetMs).ToString(@"hh\:mm\:ss");

                    sb.AppendLine($"[{time}] {speakerLabel}: {text}");
                }
            }

            return sb.Length > 0 ? sb.ToString() : "(暂无转录内容)";
        }

        // ── Batch API 文本提取 ───────────────────────────────

        private static string ExtractBatchText(JsonDocument doc)
        {
            var sb = new StringBuilder();

            if (doc.RootElement.TryGetProperty("recognizedPhrases", out var phrases)
                && phrases.ValueKind == JsonValueKind.Array)
            {
                foreach (var phrase in phrases.EnumerateArray())
                {
                    var text = ExtractBatchPhraseText(phrase);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var speaker = phrase.TryGetProperty("speaker", out var spEl)
                        ? spEl.ToString() : "";
                    var speakerLabel = string.IsNullOrWhiteSpace(speaker) ? "Speaker" : $"Speaker {speaker}";

                    TimeSpan start = TimeSpan.Zero;
                    if (phrase.TryGetProperty("offsetInTicks", out var ticksEl))
                        start = TimeSpan.FromTicks(ticksEl.GetInt64());
                    else if (phrase.TryGetProperty("offset", out var offEl))
                    {
                        try { start = System.Xml.XmlConvert.ToTimeSpan(offEl.GetString() ?? ""); }
                        catch { /* ignore invalid offset */ }
                    }

                    var time = start.ToString(@"hh\:mm\:ss");
                    sb.AppendLine($"[{time}] {speakerLabel}: {text}");
                }
            }

            return sb.Length > 0 ? sb.ToString() : "(暂无转录内容)";
        }

        private static string ExtractBatchPhraseText(JsonElement phrase)
        {
            if (phrase.TryGetProperty("nBest", out var nbest)
                && nbest.ValueKind == JsonValueKind.Array && nbest.GetArrayLength() > 0)
            {
                var best = nbest[0];
                if (best.TryGetProperty("display", out var displayEl))
                    return displayEl.GetString() ?? "";
            }
            return "";
        }

        // ── 通用段落构建（从 cues） ─────────────────────────

        private static List<TranscriptSegment> BuildSegmentsFromCues(IList<SubtitleCue> cues)
        {
            var segments = new List<TranscriptSegment>();
            if (cues == null || cues.Count == 0) return segments;

            var speakerMap = new Dictionary<string, int>();
            foreach (var cue in cues)
            {
                var (speaker, text) = ParseSpeaker(cue.Text);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (!speakerMap.ContainsKey(speaker))
                    speakerMap[speaker] = speakerMap.Count;

                segments.Add(new TranscriptSegment
                {
                    Speaker = speaker,
                    SpeakerIndex = speakerMap[speaker],
                    StartTime = cue.Start,
                    Text = text,
                    SourceCues = new List<SubtitleCue> { cue }
                });
            }
            return segments;
        }

        private static (string speaker, string text) ParseSpeaker(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return ("未知", "");

            var colonIdx = raw.IndexOf(':');
            if (colonIdx < 0) colonIdx = raw.IndexOf('：');

            if (colonIdx > 0 && colonIdx < 30)
            {
                var speaker = raw[..colonIdx].Trim();
                var text = raw[(colonIdx + 1)..].Trim();
                if (!string.IsNullOrEmpty(speaker))
                    return (speaker, text);
            }

            return ("未知", raw.Trim());
        }

        /// <summary>旧版段落 DTO 用于反序列化兼容。</summary>
        private sealed class LegacySegmentDto
        {
            public string? Speaker { get; set; }
            public int SpeakerIndex { get; set; }
            public long StartTimeTicks { get; set; }
            public string? Text { get; set; }
        }
    }
}
