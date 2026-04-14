using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 微软 Speech Fast Transcription API 客户端。
    /// 同步返回结果，延迟远低于 Batch API，适用于 ≤2h / ≤250MB 的单文件转录。
    /// 文档：https://learn.microsoft.com/en-us/azure/ai-services/speech-service/fast-transcription-create
    /// </summary>
    public static class SpeechFastTranscriptionClient
    {
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

        /// <summary>传统密钥认证的快速转录入口</summary>
        public static async Task<(List<SubtitleCue> Cues, string TranscriptionJson)> FastTranscribeToCuesAsync(
            string audioFilePath,
            string locale,
            AzureSubscription subscription,
            CancellationToken token,
            Action<string> onStatus,
            BatchSubtitleSplitOptions splitOptions,
            bool enableLlmSpeech = false,
            string? llmSpeechPrompt = null)
        {
            var cognitiveHost = subscription.GetCognitiveServicesHost();
            var endpoint = $"{cognitiveHost}/speechtotext/transcriptions:transcribe?api-version=2025-10-15";

            Action<HttpRequestMessage> setAuth = req =>
                req.Headers.Add("Ocp-Apim-Subscription-Key", subscription.SubscriptionKey);

            return await FastTranscribeCoreAsync(audioFilePath, locale, endpoint, setAuth, token, onStatus, splitOptions, enableLlmSpeech, llmSpeechPrompt);
        }

        /// <summary>AAD Bearer token 认证的快速转录入口</summary>
        public static async Task<(List<SubtitleCue> Cues, string TranscriptionJson)> FastTranscribeToCuesAsync(
            string audioFilePath,
            string locale,
            string fastEndpoint,
            Func<CancellationToken, Task<string>> getTokenAsync,
            CancellationToken token,
            Action<string> onStatus,
            BatchSubtitleSplitOptions splitOptions,
            bool enableLlmSpeech = false,
            string? llmSpeechPrompt = null)
        {
            Action<HttpRequestMessage> setAuth = req =>
            {
                var tokenTask = getTokenAsync(token);
                tokenTask.Wait(token);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenTask.Result);
            };

            return await FastTranscribeCoreAsync(audioFilePath, locale, fastEndpoint, setAuth, token, onStatus, splitOptions, enableLlmSpeech, llmSpeechPrompt);
        }

        private static async Task<(List<SubtitleCue> Cues, string TranscriptionJson)> FastTranscribeCoreAsync(
            string audioFilePath,
            string locale,
            string endpoint,
            Action<HttpRequestMessage> setAuth,
            CancellationToken token,
            Action<string> onStatus,
            BatchSubtitleSplitOptions splitOptions,
            bool enableLlmSpeech = false,
            string? llmSpeechPrompt = null)
        {
            if (!File.Exists(audioFilePath))
                throw new FileNotFoundException("音频文件不存在", audioFilePath);

            var fileInfo = new FileInfo(audioFilePath);
            if (fileInfo.Length > 250L * 1024 * 1024)
                throw new InvalidOperationException("快速转录仅支持 ≤250MB 的音频文件，当前文件过大。请改用批量转录模式。");

            onStatus(enableLlmSpeech ? "LLM Speech 增强转录：准备上传音频..." : "快速转录：准备上传音频...");

            // 构建 definition JSON
            string definitionJson;
            if (enableLlmSpeech)
            {
                // LLM Speech 增强模式：自动语言检测，支持自定义提示词
                var promptList = new List<string>();
                if (!string.IsNullOrWhiteSpace(llmSpeechPrompt))
                {
                    promptList.Add(llmSpeechPrompt.Trim());
                }
                else
                {
                    // 用户未填写自定义 prompt 时，根据 locale 生成默认提示词
                    promptList.Add(BuildDefaultLlmSpeechPrompt(locale));
                }

                var definition = new Dictionary<string, object>
                {
                    ["profanityFilterMode"] = "Masked",
                    ["diarizationEnabled"] = true,
                    ["wordLevelTimestampsEnabled"] = true,
                    ["enhancedMode"] = new Dictionary<string, object>
                    {
                        ["task"] = "transcribe",
                        ["prompt"] = promptList
                    }
                };
                definitionJson = JsonSerializer.Serialize(definition);
            }
            else
            {
                var definition = new
                {
                    locales = new[] { locale },
                    profanityFilterMode = "Masked",
                    diarizationEnabled = true,
                    wordLevelTimestampsEnabled = true
                };
                definitionJson = JsonSerializer.Serialize(definition);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            setAuth(request);

            using var content = new MultipartFormDataContent();

            // audio file part
            var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "audio", Path.GetFileName(audioFilePath));

            // definition part
            var definitionContent = new StringContent(definitionJson, Encoding.UTF8, "application/json");
            content.Add(definitionContent, "definition");

            request.Content = content;

            var modeLabel = enableLlmSpeech ? "LLM Speech 增强转录" : "快速转录";
            onStatus($"{modeLabel}：上传音频并等待结果...");

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var responseBody = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                var errorSummary = BuildFastTranscriptionErrorSummary(responseBody, response.StatusCode);
                throw new InvalidOperationException(errorSummary);
            }

            System.Diagnostics.Debug.WriteLine($"[FastTranscription] definition: {definitionJson}");
            System.Diagnostics.Debug.WriteLine($"[FastTranscription] response length={responseBody.Length}, first 2000 chars:");
            System.Diagnostics.Debug.WriteLine(responseBody.Length > 2000 ? responseBody[..2000] : responseBody);

            onStatus($"{modeLabel}：解析结果...");
            var cues = FastTranscriptionParser.ParseFastTranscriptionToCues(responseBody, splitOptions);
            return (cues, responseBody);
        }

        /// <summary>
        /// 根据 locale 推断 LLM Speech 的默认提示词。
        /// LLM Speech 不支持 locales 参数，需要通过 prompt 指定输出语言和风格。
        /// </summary>
        private static string BuildDefaultLlmSpeechPrompt(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale) || locale.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return "请准确转录音频内容。输出完整的句子，使用正确的标点符号。去除语气词（如嗯、啊、呃）和重复的填充词。";

            var lang = locale.ToLowerInvariant();
            if (lang.StartsWith("zh"))
                return "请用简体中文准确转录音频内容。输出完整的句子，使用正确的标点符号。去除语气词（如嗯、啊、呃）和重复的填充词。";
            if (lang.StartsWith("en"))
                return "Transcribe the audio accurately in English. Output complete sentences with proper punctuation. Remove filler words such as um, uh, and like.";
            if (lang.StartsWith("ja"))
                return "音声を正確に日本語で文字起こししてください。完全な文で出力し、正しい句読点を使用してください。";
            if (lang.StartsWith("ko"))
                return "오디오를 정확하게 한국어로 전사하세요. 완전한 문장으로 출력하고, 올바른 구두점을 사용하세요.";

            // 其他语言：通用英文提示
            return $"Transcribe the audio accurately in the language '{locale}'. Output complete sentences with proper punctuation. Remove filler words.";
        }

        private static string BuildFastTranscriptionErrorSummary(string responseBody, System.Net.HttpStatusCode statusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                var code = root.TryGetProperty("error", out var errorEl)
                    && errorEl.TryGetProperty("code", out var codeEl)
                    ? codeEl.GetString() : null;
                var message = root.TryGetProperty("error", out var errEl2)
                    && errEl2.TryGetProperty("message", out var msgEl)
                    ? msgEl.GetString() : null;

                if (!string.IsNullOrWhiteSpace(message))
                    return $"快速转录失败 ({statusCode}): {code} - {message}";
            }
            catch { }

            return $"快速转录失败 ({statusCode}): {responseBody}";
        }
    }
}
