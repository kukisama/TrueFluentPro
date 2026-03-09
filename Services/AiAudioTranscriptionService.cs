using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Services
{
    public sealed class AiAudioTranscriptionService : AiMediaServiceBase, IAiAudioTranscriptionService
    {
        public async Task<AiAudioTranscriptionResult> TranscribeAsync(
            ModelRuntimeResolution runtime,
            string audioPath,
            string? sourceLanguage,
            BatchSubtitleSplitOptions splitOptions,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(runtime);

            if (string.IsNullOrWhiteSpace(audioPath))
                throw new ArgumentException("音频路径为空。", nameof(audioPath));
            if (!File.Exists(audioPath))
                throw new FileNotFoundException("未找到音频文件。", audioPath);

            var requestConfig = runtime.CreateRequestConfig();
            if (requestConfig.AzureAuthMode == AzureAuthMode.AAD)
            {
                SetTokenProvider(new AzureTokenProvider(runtime.ProfileKey));
            }

            var urlCandidates = EndpointProfileUrlBuilder.BuildConfiguredAudioTranscriptionUrlCandidates(
                requestConfig.ApiEndpoint,
                requestConfig.ProfileId,
                requestConfig.EndpointType,
                runtime.EffectiveDeploymentName,
                requestConfig.ApiVersion);

            if (urlCandidates.Count == 0)
            {
                throw new InvalidOperationException("当前终结点资料包未声明语音转写接口路线，请先补齐资料包。");
            }

            var normalizedLanguage = NormalizeLanguageHint(sourceLanguage);
            HttpStatusCode? lastStatusCode = null;
            string lastBody = "";

            foreach (var candidateUrl in urlCandidates)
            {
                var outcome = await SendOnceAsync(candidateUrl, runtime, requestConfig, audioPath, normalizedLanguage, splitOptions, cancellationToken);
                if (outcome.Success)
                {
                    return outcome.Result!;
                }

                lastStatusCode = outcome.StatusCode;
                lastBody = outcome.ResponseBody;

                if (!outcome.ShouldRetryWithSubscriptionKeyQuery)
                {
                    continue;
                }

                var retryUrl = BuildApimSubscriptionKeyQueryUrl(candidateUrl, requestConfig.ApiKey);
                var retryOutcome = await SendOnceAsync(retryUrl, runtime, requestConfig, audioPath, normalizedLanguage, splitOptions, cancellationToken);
                if (retryOutcome.Success)
                {
                    return retryOutcome.Result!;
                }

                lastStatusCode = retryOutcome.StatusCode;
                lastBody = retryOutcome.ResponseBody;
            }

            var detail = string.IsNullOrWhiteSpace(lastBody)
                ? "无响应体"
                : TrimBody(lastBody);
            var status = lastStatusCode.HasValue ? $"HTTP {(int)lastStatusCode.Value}" : "请求未发出";
            throw new InvalidOperationException($"AI 语音转写失败：{status}，{detail}");
        }

        private async Task<TranscriptionAttemptOutcome> SendOnceAsync(
            string url,
            ModelRuntimeResolution runtime,
            AiConfig requestConfig,
            string audioPath,
            string? languageHint,
            BatchSubtitleSplitOptions splitOptions,
            CancellationToken cancellationToken)
        {
            using var request = await CreateRequestAsync(url, runtime, requestConfig, audioPath, languageHint, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var cues = ParseTranscriptionToCues(responseBody, splitOptions);
                if (cues.Count == 0)
                {
                    throw new InvalidOperationException("AI 语音转写已返回结果，但未解析出可用字幕片段。");
                }

                return TranscriptionAttemptOutcome.SuccessResult(new AiAudioTranscriptionResult
                {
                    Cues = cues,
                    RawJson = responseBody,
                    FinalUrl = url
                });
            }

            return new TranscriptionAttemptOutcome
            {
                Success = false,
                StatusCode = response.StatusCode,
                ResponseBody = responseBody,
                ShouldRetryWithSubscriptionKeyQuery = IsMissingApimSubscriptionKeyResponse(requestConfig, response, responseBody)
            };
        }

        private async Task<HttpRequestMessage> CreateRequestAsync(
            string url,
            ModelRuntimeResolution runtime,
            AiConfig requestConfig,
            string audioPath,
            string? languageHint,
            CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            await SetAuthHeadersAsync(request, requestConfig, cancellationToken);

            var form = new MultipartFormDataContent();
            var fileStream = File.OpenRead(audioPath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(BlobStorageService.GetAudioContentType(audioPath));
            form.Add(fileContent, "file", Path.GetFileName(audioPath));
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("0"), "temperature");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
            form.Add(new StringContent("word"), "timestamp_granularities[]");

            if (!string.IsNullOrWhiteSpace(languageHint))
            {
                form.Add(new StringContent(languageHint), "language");
            }

            if (!url.Contains("/deployments/", StringComparison.OrdinalIgnoreCase))
            {
                form.Add(new StringContent(runtime.ModelId), "model");
            }

            request.Content = form;
            return request;
        }

        private static string? NormalizeLanguageHint(string? sourceLanguage)
        {
            var normalized = RealtimeSpeechTranscriber.GetTranscriptionSourceLanguage(sourceLanguage ?? "auto");
            return string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
                ? null
                : normalized;
        }

        private static List<SubtitleCue> ParseTranscriptionToCues(string responseJson, BatchSubtitleSplitOptions splitOptions)
        {
            var result = new List<SubtitleCue>();
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;

            if (root.TryGetProperty("segments", out var segmentsElement) && segmentsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segmentsElement.EnumerateArray())
                {
                    var text = ReadText(segment, "text");
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var speakerLabel = BuildSpeakerLabel(segment);
                    if (TryReadWords(segment, out var words) && words.Count > 0)
                    {
                        result.AddRange(SplitWords(words, speakerLabel, splitOptions));
                        continue;
                    }

                    if (!TryReadSegmentTime(segment, out var start, out var end))
                    {
                        continue;
                    }

                    result.Add(new SubtitleCue
                    {
                        Start = start,
                        End = end,
                        Text = PrefixSpeaker(text, speakerLabel)
                    });
                }
            }

            if (result.Count > 0)
            {
                return result.OrderBy(c => c.Start).ToList();
            }

            if (TryReadWords(root, out var rootWords) && rootWords.Count > 0)
            {
                return SplitWords(rootWords, BuildSpeakerLabel(root), splitOptions)
                    .OrderBy(c => c.Start)
                    .ToList();
            }

            var fallbackText = ReadText(root, "text");
            if (string.IsNullOrWhiteSpace(fallbackText))
            {
                return result;
            }

            var duration = TryReadSeconds(root, "duration", out var totalSeconds)
                ? TimeSpan.FromSeconds(Math.Max(totalSeconds, 1))
                : TimeSpan.FromSeconds(Math.Max(1, Math.Min(8, fallbackText.Length / 8.0)));

            result.Add(new SubtitleCue
            {
                Start = TimeSpan.Zero,
                End = duration,
                Text = PrefixSpeaker(fallbackText, BuildSpeakerLabel(root))
            });

            return result;
        }

        private static List<SubtitleCue> SplitWords(
            List<AiWordInfo> words,
            string? speakerLabel,
            BatchSubtitleSplitOptions splitOptions)
        {
            var cues = new List<SubtitleCue>();
            if (words.Count == 0)
            {
                return cues;
            }

            var startIndex = 0;
            var startTime = words[0].Start;
            var currentChars = 0;

            for (var i = 0; i < words.Count; i++)
            {
                var word = words[i];
                currentChars += word.Text.Replace(" ", string.Empty).Length;
                var nextGapMs = i + 1 < words.Count
                    ? (words[i + 1].Start - word.End).TotalMilliseconds
                    : 0;
                var durationSeconds = (word.End - startTime).TotalSeconds;

                var shouldSplit = i == words.Count - 1;
                if (!shouldSplit && splitOptions.EnableSentenceSplit && EndsWithSentenceBreak(word.Text, splitOptions.SplitOnComma))
                {
                    shouldSplit = true;
                }
                else if (!shouldSplit && splitOptions.PauseSplitMs > 0 && nextGapMs >= splitOptions.PauseSplitMs)
                {
                    shouldSplit = true;
                }
                else if (!shouldSplit && splitOptions.MaxChars > 0 && currentChars >= splitOptions.MaxChars)
                {
                    shouldSplit = true;
                }
                else if (!shouldSplit && splitOptions.MaxDurationSeconds > 0 && durationSeconds >= splitOptions.MaxDurationSeconds)
                {
                    shouldSplit = true;
                }

                if (!shouldSplit)
                {
                    continue;
                }

                var segmentWords = words.Skip(startIndex).Take(i - startIndex + 1).Select(w => w.Text.Trim()).Where(t => !string.IsNullOrWhiteSpace(t));
                var segmentText = NormalizeSubtitleText(string.Join(" ", segmentWords));
                if (!string.IsNullOrWhiteSpace(segmentText))
                {
                    cues.Add(new SubtitleCue
                    {
                        Start = startTime,
                        End = word.End,
                        Text = PrefixSpeaker(segmentText, speakerLabel)
                    });
                }

                startIndex = i + 1;
                if (startIndex < words.Count)
                {
                    startTime = words[startIndex].Start;
                }

                currentChars = 0;
            }

            return cues;
        }

        private static bool TryReadWords(JsonElement element, out List<AiWordInfo> words)
        {
            words = new List<AiWordInfo>();
            if (!element.TryGetProperty("words", out var wordsElement) || wordsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var wordElement in wordsElement.EnumerateArray())
            {
                var text = ReadText(wordElement, "word", "text");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (!TryReadWordTime(wordElement, out var start, out var end))
                {
                    continue;
                }

                words.Add(new AiWordInfo
                {
                    Text = text,
                    Start = start,
                    End = end
                });
            }

            return words.Count > 0;
        }

        private static bool TryReadWordTime(JsonElement wordElement, out TimeSpan start, out TimeSpan end)
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;

            if (!TryReadSeconds(wordElement, "start", out var startSeconds))
            {
                return false;
            }

            start = TimeSpan.FromSeconds(startSeconds);

            if (TryReadSeconds(wordElement, "end", out var endSeconds))
            {
                end = TimeSpan.FromSeconds(Math.Max(endSeconds, startSeconds));
                return true;
            }

            if (TryReadSeconds(wordElement, "duration", out var durationSeconds))
            {
                end = start + TimeSpan.FromSeconds(Math.Max(durationSeconds, 0.1));
                return true;
            }

            end = start + TimeSpan.FromSeconds(0.4);
            return true;
        }

        private static bool TryReadSegmentTime(JsonElement segment, out TimeSpan start, out TimeSpan end)
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;

            if (!TryReadSeconds(segment, "start", out var startSeconds))
            {
                return false;
            }

            start = TimeSpan.FromSeconds(startSeconds);

            if (TryReadSeconds(segment, "end", out var endSeconds))
            {
                end = TimeSpan.FromSeconds(Math.Max(endSeconds, startSeconds));
                return true;
            }

            if (TryReadSeconds(segment, "duration", out var durationSeconds))
            {
                end = start + TimeSpan.FromSeconds(Math.Max(durationSeconds, 0.5));
                return true;
            }

            return false;
        }

        private static bool TryReadSeconds(JsonElement element, string propertyName, out double seconds)
        {
            seconds = 0;
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            switch (property.ValueKind)
            {
                case JsonValueKind.Number:
                    seconds = property.GetDouble();
                    return true;
                case JsonValueKind.String:
                    return double.TryParse(property.GetString(), out seconds);
                default:
                    return false;
            }
        }

        private static string ReadText(JsonElement element, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var property))
                {
                    continue;
                }

                if (property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static string? BuildSpeakerLabel(JsonElement element)
        {
            var speaker = ReadText(element, "speaker", "speaker_id", "speakerId");
            if (string.IsNullOrWhiteSpace(speaker))
            {
                return null;
            }

            return speaker.StartsWith("speaker", StringComparison.OrdinalIgnoreCase)
                ? speaker.Trim()
                : $"Speaker {speaker.Trim()}";
        }

        private static string PrefixSpeaker(string text, string? speakerLabel)
        {
            var normalized = NormalizeSubtitleText(text);
            if (string.IsNullOrWhiteSpace(speakerLabel))
            {
                return normalized;
            }

            return $"{speakerLabel}: {normalized}";
        }

        private static bool EndsWithSentenceBreak(string text, bool splitOnComma)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var last = text.TrimEnd().LastOrDefault();
            if ("。！？!?；;".IndexOf(last) >= 0)
            {
                return true;
            }

            return splitOnComma && "，,".IndexOf(last) >= 0;
        }

        private static string NormalizeSubtitleText(string text)
            => string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();

        private static string TrimBody(string body)
        {
            var normalized = NormalizeSubtitleText(body);
            return normalized.Length <= 280 ? normalized : normalized[..280] + "...";
        }

        private sealed class TranscriptionAttemptOutcome
        {
            public bool Success { get; init; }
            public AiAudioTranscriptionResult? Result { get; init; }
            public HttpStatusCode? StatusCode { get; init; }
            public string ResponseBody { get; init; } = string.Empty;
            public bool ShouldRetryWithSubscriptionKeyQuery { get; init; }

            public static TranscriptionAttemptOutcome SuccessResult(AiAudioTranscriptionResult result)
                => new()
                {
                    Success = true,
                    Result = result
                };
        }

        private sealed class AiWordInfo
        {
            public required string Text { get; init; }
            public required TimeSpan Start { get; init; }
            public required TimeSpan End { get; init; }
        }
    }
}
