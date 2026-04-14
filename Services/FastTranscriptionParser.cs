using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 解析 Speech Fast Transcription API 返回的 JSON，复用 BatchSubtitleSplitOptions 的断句逻辑。
    /// Fast API 响应格式文档：https://learn.microsoft.com/en-us/rest/api/speechtotext/transcriptions/transcribe
    /// </summary>
    public static class FastTranscriptionParser
    {
        /// <summary>短于此字数的片段在合并时忽略句末标点限制，避免 LLM Speech 等细粒度输出碎片化。</summary>
        private const int MinMergeChars = 15;

        public static List<SubtitleCue> ParseFastTranscriptionToCues(
            string transcriptionJson, BatchSubtitleSplitOptions splitOptions)
        {
            var list = new List<SubtitleCue>();
            using var doc = JsonDocument.Parse(transcriptionJson);

            if (!doc.RootElement.TryGetProperty("phrases", out var phrases)
                || phrases.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var phrase in phrases.EnumerateArray())
            {
                var text = phrase.TryGetProperty("text", out var textEl)
                    ? textEl.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var speaker = phrase.TryGetProperty("speaker", out var speakerEl)
                    ? speakerEl.ToString()
                    : "";
                var speakerLabel = string.IsNullOrWhiteSpace(speaker) ? "Speaker" : $"Speaker {speaker}";

                if (splitOptions.EnableSentenceSplit
                    && TryGetPhraseWords(phrase, out var words)
                    && words.Count > 0)
                {
                    list.AddRange(SplitPhraseToCues(words, text, speakerLabel, splitOptions));
                    continue;
                }

                // 无断句时，使用整句时间戳
                if (!TryGetPhraseTimestamp(phrase, out var start, out var end))
                    continue;

                list.Add(new SubtitleCue
                {
                    Start = start,
                    End = end,
                    Text = $"{speakerLabel}: {text}"
                });
            }

            return MergeAdjacentCues(list.OrderBy(c => c.Start).ToList(), splitOptions);
        }

        // ── 跨 phrase 合并：同 Speaker 相邻 cue 若间隔 < PauseSplitMs 且不超限则合并 ──

        private static List<SubtitleCue> MergeAdjacentCues(
            List<SubtitleCue> cues, BatchSubtitleSplitOptions splitOptions)
        {
            if (cues.Count <= 1) return cues;

            var merged = new List<SubtitleCue> { cues[0] };

            for (var i = 1; i < cues.Count; i++)
            {
                var prev = merged[^1];
                var curr = cues[i];

                var prevSpeaker = ExtractSpeakerLabel(prev.Text);
                var currSpeaker = ExtractSpeakerLabel(curr.Text);

                var gapMs = (curr.Start - prev.End).TotalMilliseconds;
                var combinedDuration = (curr.End - prev.Start).TotalSeconds;
                var prevTextBody = ExtractTextBody(prev.Text);
                var currTextBody = ExtractTextBody(curr.Text);
                var combinedChars = prevTextBody.Length + currTextBody.Length;

                var canMerge = prevSpeaker == currSpeaker
                    && gapMs < splitOptions.PauseSplitMs
                    && (splitOptions.MaxChars <= 0 || combinedChars <= splitOptions.MaxChars)
                    && (splitOptions.MaxDurationSeconds <= 0 || combinedDuration <= splitOptions.MaxDurationSeconds);

                // 如果启用了按句子切分，仅当前一条不以句末标点结尾时才合并
                // 但对非常短的片段（< MinMergeChars 字符），忽略句末标点限制以避免碎片化
                if (canMerge && splitOptions.EnableSentenceSplit)
                {
                    var trimmed = prevTextBody.TrimEnd();
                    if (trimmed.Length >= MinMergeChars
                        && trimmed.Length > 0
                        && IsSentenceBreakPunctuation(trimmed[^1], splitOptions.SplitOnComma))
                        canMerge = false;
                }

                if (canMerge)
                {
                    merged[^1] = new SubtitleCue
                    {
                        Start = prev.Start,
                        End = curr.End,
                        Text = $"{prevSpeaker}: {prevTextBody}{currTextBody}"
                    };
                }
                else
                {
                    merged.Add(curr);
                }
            }

            return merged;
        }

        private static string ExtractSpeakerLabel(string cueText)
        {
            var colonIdx = cueText.IndexOf(':');
            return colonIdx > 0 ? cueText[..colonIdx].Trim() : "Speaker";
        }

        private static string ExtractTextBody(string cueText)
        {
            var colonIdx = cueText.IndexOf(':');
            return colonIdx >= 0 && colonIdx + 1 < cueText.Length
                ? cueText[(colonIdx + 1)..].TrimStart()
                : cueText;
        }

        // ── 词级解析 ──────────────────────────────────────────────

        private sealed class WordInfo
        {
            public required string Text { get; init; }
            public required TimeSpan Start { get; init; }
            public required TimeSpan End { get; init; }
        }

        private static bool TryGetPhraseWords(JsonElement phrase, out List<WordInfo> words)
        {
            words = new List<WordInfo>();
            if (!phrase.TryGetProperty("words", out var wordsEl)
                || wordsEl.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var wordEl in wordsEl.EnumerateArray())
            {
                var wordText = wordEl.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(wordText))
                    continue;

                if (!wordEl.TryGetProperty("offsetMilliseconds", out var offsetEl)
                    || !wordEl.TryGetProperty("durationMilliseconds", out var durEl))
                    continue;

                var offsetMs = offsetEl.GetInt64();
                var durationMs = durEl.GetInt64();
                words.Add(new WordInfo
                {
                    Text = wordText,
                    Start = TimeSpan.FromMilliseconds(offsetMs),
                    End = TimeSpan.FromMilliseconds(offsetMs + durationMs)
                });
            }

            return words.Count > 0;
        }

        private static bool TryGetPhraseTimestamp(JsonElement phrase, out TimeSpan start, out TimeSpan end)
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;

            if (!phrase.TryGetProperty("offsetMilliseconds", out var offsetEl)
                || !phrase.TryGetProperty("durationMilliseconds", out var durEl))
                return false;

            var offsetMs = offsetEl.GetInt64();
            var durationMs = durEl.GetInt64();
            start = TimeSpan.FromMilliseconds(offsetMs);
            end = TimeSpan.FromMilliseconds(offsetMs + durationMs);
            return true;
        }

        // ── 断句逻辑（复用 BatchTranscriptionParser 的算法） ──────────

        private static List<SubtitleCue> SplitPhraseToCues(
            List<WordInfo> words, string displayText, string speakerLabel,
            BatchSubtitleSplitOptions splitOptions)
        {
            var cues = new List<SubtitleCue>();
            if (words.Count == 0) return cues;

            var breakIndices = GetPunctuationBreakIndices(displayText, words, splitOptions.SplitOnComma);
            var segmentStartIndex = 0;
            var segmentStartCharIndex = 0;
            var segmentCharCount = 0;
            var segmentStartTime = words[0].Start;
            var currentCharIndex = 0;

            for (var i = 0; i < words.Count; i++)
            {
                var word = words[i];
                var wordLength = GetWordLength(word.Text);
                segmentCharCount += wordLength;
                currentCharIndex += wordLength;

                var durationSeconds = (word.End - segmentStartTime).TotalSeconds;
                var nextGapMs = i + 1 < words.Count
                    ? (words[i + 1].Start - word.End).TotalMilliseconds
                    : 0;

                var shouldSplit = false;
                if (breakIndices.Contains(i))
                    shouldSplit = true;
                else if (splitOptions.PauseSplitMs > 0 && nextGapMs >= splitOptions.PauseSplitMs)
                    shouldSplit = true;
                else if (splitOptions.MaxChars > 0 && segmentCharCount >= splitOptions.MaxChars)
                    shouldSplit = true;
                else if (splitOptions.MaxDurationSeconds > 0 && durationSeconds >= splitOptions.MaxDurationSeconds)
                    shouldSplit = true;

                if (!shouldSplit && i < words.Count - 1)
                    continue;

                var segmentEndIndex = i;
                var segmentEndCharIndex = currentCharIndex;
                var segmentText = TrySliceDisplaySegment(displayText, segmentStartCharIndex, segmentEndCharIndex)
                    ?? string.Concat(words.Skip(segmentStartIndex).Take(segmentEndIndex - segmentStartIndex + 1)
                        .Select(w => w.Text));

                segmentText = NormalizeSubtitleText(segmentText);
                if (!string.IsNullOrWhiteSpace(segmentText))
                {
                    cues.Add(new SubtitleCue
                    {
                        Start = segmentStartTime,
                        End = word.End,
                        Text = $"{speakerLabel}: {segmentText}"
                    });
                }

                segmentStartIndex = i + 1;
                segmentStartCharIndex = segmentEndCharIndex;
                segmentCharCount = 0;
                if (segmentStartIndex < words.Count)
                    segmentStartTime = words[segmentStartIndex].Start;
            }

            return cues;
        }

        private static HashSet<int> GetPunctuationBreakIndices(
            string displayText, List<WordInfo> words, bool splitOnComma)
        {
            var breakIndices = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(displayText) || words.Count == 0)
                return breakIndices;

            var wordEndOffsets = new List<int>(words.Count);
            var running = 0;
            foreach (var word in words)
            {
                running += GetWordLength(word.Text);
                wordEndOffsets.Add(running);
            }

            var charCount = 0;
            foreach (var ch in displayText)
            {
                if (char.IsWhiteSpace(ch)) continue;

                if (IsSentenceBreakPunctuation(ch, splitOnComma))
                {
                    if (charCount <= 0) continue;
                    var idx = wordEndOffsets.FindIndex(end => end >= charCount);
                    if (idx >= 0)
                        breakIndices.Add(idx);
                    continue;
                }

                if (IsSkippableDisplayChar(ch)) continue;
                charCount++;
            }

            return breakIndices;
        }

        private static string? TrySliceDisplaySegment(string displayText, int startCharIndex, int endCharIndex)
        {
            if (string.IsNullOrWhiteSpace(displayText)) return null;

            var charMap = new List<int>();
            for (var i = 0; i < displayText.Length; i++)
            {
                var ch = displayText[i];
                if (char.IsWhiteSpace(ch) || IsSkippableDisplayChar(ch)) continue;
                charMap.Add(i);
            }

            if (charMap.Count == 0) return displayText.Trim();

            var safeStart = Math.Clamp(startCharIndex, 0, charMap.Count - 1);
            var safeEnd = Math.Clamp(endCharIndex, safeStart + 1, charMap.Count);
            var startIndex = charMap[safeStart];
            var endIndex = charMap[safeEnd - 1];

            while (endIndex + 1 < displayText.Length)
            {
                var ch = displayText[endIndex + 1];
                if (char.IsWhiteSpace(ch) || IsSentenceBreakPunctuation(ch, splitOnComma: true))
                {
                    endIndex++;
                    continue;
                }
                break;
            }

            var segment = displayText.Substring(startIndex, endIndex - startIndex + 1);
            return segment.Trim();
        }

        private static int GetWordLength(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var count = 0;
            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch)) continue;
                if (IsSkippableDisplayChar(ch)) continue;
                count++;
            }
            return count;
        }

        private static bool IsSkippableDisplayChar(char ch) =>
            "。！？!?；;，,、：:".IndexOf(ch) >= 0;

        private static bool IsSentenceBreakPunctuation(char ch, bool splitOnComma)
        {
            if ("。！？!?；;".IndexOf(ch) >= 0) return true;
            if (splitOnComma && "，,".IndexOf(ch) >= 0) return true;
            return false;
        }

        private static string NormalizeSubtitleText(string text) =>
            string.IsNullOrWhiteSpace(text) ? "" :
            System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
    }
}
