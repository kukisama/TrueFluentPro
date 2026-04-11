using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.Speech
{
    /// <summary>
    /// Azure Speech REST API TTS 服务 — 纯 HttpClient 实现，无 SDK 依赖。
    /// 支持 API Key 和 AAD 两种认证方式。
    /// 改编自 DocumentTranslation.Desktop 的 SpeechSynthesisService。
    /// </summary>
    public sealed class SpeechSynthesisService
    {
        public sealed record OutputFormatOption(string Label, string HeaderValue, bool Requires48KhzSupport = false);

        private readonly HttpClient _httpClient = new();
        private List<VoiceInfo>? _cachedVoices;
        private string? _cachedVoicesKey;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <summary>支持的输出格式。</summary>
        public static readonly IReadOnlyList<OutputFormatOption> OutputFormats = new OutputFormatOption[]
        {
            new("MP3 24kHz",     "audio-24khz-96kbitrate-mono-mp3"),
            new("MP3 48kHz",     "audio-48khz-192kbitrate-mono-mp3", true),
            new("MP3 16kHz",     "audio-16khz-64kbitrate-mono-mp3"),
            new("Opus 24kHz",    "audio-24khz-48kbitrate-mono-opus"),
            new("WAV 16kHz",     "riff-16khz-16bit-mono-pcm"),
            new("WAV 24kHz PCM", "riff-24khz-16bit-mono-pcm"),
            new("WAV 48kHz PCM", "riff-48khz-16bit-mono-pcm", true),
            new("OGG 24kHz",     "ogg-24khz-16bit-mono-opus"),
            new("OGG 48kHz",     "ogg-48khz-16bit-mono-opus", true),
        };

        public static IReadOnlyList<OutputFormatOption> GetAvailableFormats(bool supports48Khz)
            => OutputFormats.Where(f => supports48Khz || !f.Requires48KhzSupport).ToList();

        // ── 认证参数 ─────────────────────────────────

        /// <summary>TTS REST 认证上下文 — 调用方构建后传入。</summary>
        public sealed class TtsAuthContext
        {
            /// <summary>API Key 模式：直接传 subscription key。</summary>
            public string? SubscriptionKey { get; init; }

            /// <summary>AAD 模式：格式 aad#{resourceId}#{token}。</summary>
            public string? AadBearerValue { get; init; }

            /// <summary>Speech REST API 基础 URL（含 scheme）。</summary>
            public required string BaseUrl { get; init; }

            /// <summary>是否为自定义域名端点（*.cognitiveservices.azure.com）。</summary>
            public bool IsCustomDomainEndpoint { get; init; }
        }

        // ── URL 构建 ──────────────────────────────────

        private static string BuildUrl(TtsAuthContext auth, string path)
        {
            var host = auth.BaseUrl.TrimEnd('/');
            var normalizedPath = path.TrimStart('/');
            return auth.IsCustomDomainEndpoint
                ? $"{host}/tts/{normalizedPath}"
                : $"{host}/{normalizedPath}";
        }

        private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url, TtsAuthContext auth)
        {
            var request = new HttpRequestMessage(method, url);

            if (!string.IsNullOrWhiteSpace(auth.AadBearerValue))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AadBearerValue);
            }
            else if (!string.IsNullOrWhiteSpace(auth.SubscriptionKey))
            {
                request.Headers.Add("Ocp-Apim-Subscription-Key", auth.SubscriptionKey);
            }
            else
            {
                throw new InvalidOperationException("TTS 认证信息缺失：需要 AAD Bearer 或 Subscription Key。");
            }

            return request;
        }

        // ── 语音列表 ──────────────────────────────────

        public async Task<List<VoiceInfo>> ListVoicesAsync(TtsAuthContext auth, bool forceRefresh = false, CancellationToken ct = default)
        {
            var cacheKey = auth.BaseUrl;
            if (!forceRefresh && _cachedVoices != null && _cachedVoicesKey == cacheKey)
                return _cachedVoices;

            if (!forceRefresh && TryReadVoicesFromDiskCache(cacheKey, out var cached))
            {
                _cachedVoices = cached;
                _cachedVoicesKey = cacheKey;
                return cached;
            }

            var url = BuildUrl(auth, "cognitiveservices/voices/list");
            using var request = CreateAuthenticatedRequest(HttpMethod.Get, url, auth);
            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var voices = JsonSerializer.Deserialize<List<VoiceInfo>>(json) ?? new List<VoiceInfo>();

            TryWriteVoicesToDiskCache(cacheKey, json);

            _cachedVoices = voices;
            _cachedVoicesKey = cacheKey;
            return voices;
        }

        // ── 合成 ──────────────────────────────────────

        public async Task<byte[]> SynthesizeAsync(TtsAuthContext auth, string ssml, string outputFormat, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ssml))
                throw new ArgumentException("SSML 内容不能为空。", nameof(ssml));

            var url = BuildUrl(auth, "cognitiveservices/v1");
            using var request = CreateAuthenticatedRequest(HttpMethod.Post, url, auth);

            request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");
            request.Headers.Add("X-Microsoft-OutputFormat", outputFormat);
            request.Headers.Add("User-Agent", "TrueFluentPro");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync(ct);
        }

        // ── SSML 构建 ─────────────────────────────────

        public static string BuildSsml(
            string text, VoiceInfo voice,
            string? style = null, double styleDegree = 1.0, string? role = null,
            string? rate = null, string? pitch = null, string? volume = null,
            SpeechAdvancedOptions? advancedOptions = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\"");
            sb.AppendLine("       xmlns:mstts=\"https://www.w3.org/2001/mstts\"");
            sb.AppendLine($"       xml:lang=\"{Esc(voice.Locale)}\">");
            sb.AppendLine(BuildVoiceBlock(text, voice, style, styleDegree, role, rate, pitch, volume, advancedOptions, 1));
            sb.AppendLine("</speak>");
            return sb.ToString();
        }

        public static string BuildMultiVoiceSsml(IEnumerable<(string Text, VoiceInfo Voice, string? Style, double StyleDegree, string? Role, string? Rate, string? Pitch, SpeechAdvancedOptions? Advanced)> segments)
        {
            var list = segments.Where(s => !string.IsNullOrWhiteSpace(s.Text) && s.Voice != null).ToList();
            if (list.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\"");
            sb.AppendLine("       xmlns:mstts=\"https://www.w3.org/2001/mstts\"");
            sb.AppendLine($"       xml:lang=\"{Esc(list[0].Voice.Locale)}\">");

            foreach (var seg in list)
            {
                sb.AppendLine(BuildVoiceBlock(seg.Text, seg.Voice, seg.Style, seg.StyleDegree, seg.Role, seg.Rate, seg.Pitch, null, seg.Advanced, 1));
            }

            sb.AppendLine("</speak>");
            return sb.ToString();
        }

        public static string BuildVoiceBlock(
            string text, VoiceInfo voice,
            string? style = null, double styleDegree = 1.0, string? role = null,
            string? rate = null, string? pitch = null, string? volume = null,
            SpeechAdvancedOptions? adv = null,
            int indentLevel = 0)
        {
            var ind = new string(' ', indentLevel * 2);
            var ind1 = ind + "  ";
            var ind2 = ind1 + "  ";
            var sb = new StringBuilder();

            var effect = Norm(adv?.Effect);
            var langOverride = voice.SupportsLangTag ? Norm(adv?.LanguageOverride) : null;
            var resolvedRate = voice.SupportsProsodyControl ? Norm(rate) : null;
            var resolvedPitch = voice.SupportsProsodyControl ? Norm(pitch) : null;
            var resolvedVolume = voice.SupportsProsodyControl ? FirstNonEmpty(volume, adv?.Volume) : null;
            var range = voice.SupportsProsodyControl ? Norm(adv?.Range) : null;
            var contour = voice.SupportsProsodyControl ? Norm(adv?.Contour) : null;
            var breakStrength = voice.SupportsBreakTag ? Norm(adv?.BreakStrength) : null;
            var breakTime = voice.SupportsBreakTag ? Norm(adv?.BreakTime) : null;
            var silenceType = voice.SupportsSilenceTag ? Norm(adv?.SilenceType) : null;
            var silenceValue = voice.SupportsSilenceTag ? Norm(adv?.SilenceValue) : null;
            var emphasisLevel = voice.SupportsEmphasisTag ? Norm(adv?.EmphasisLevel) : null;
            var useExpressAs = voice.SupportsExpressAs && (!string.IsNullOrWhiteSpace(style) || !string.IsNullOrWhiteSpace(role));
            var useProsody = !string.IsNullOrWhiteSpace(resolvedRate) || !string.IsNullOrWhiteSpace(resolvedPitch)
                          || !string.IsNullOrWhiteSpace(resolvedVolume) || !string.IsNullOrWhiteSpace(range) || !string.IsNullOrWhiteSpace(contour);

            sb.Append(ind);
            sb.Append($"<voice name=\"{Esc(voice.ShortName)}\"");
            if (!string.IsNullOrWhiteSpace(effect)) sb.Append($" effect=\"{Esc(effect)}\"");
            sb.AppendLine(">");

            if (!string.IsNullOrWhiteSpace(silenceType) && !string.IsNullOrWhiteSpace(silenceValue))
                sb.AppendLine($"{ind1}<mstts:silence type=\"{Esc(silenceType)}\" value=\"{Esc(silenceValue)}\"/>");

            if (!string.IsNullOrWhiteSpace(langOverride))
                sb.AppendLine($"{ind1}<lang xml:lang=\"{Esc(langOverride)}\">");

            if (useExpressAs)
            {
                sb.Append($"{ind1}<mstts:express-as");
                if (!string.IsNullOrWhiteSpace(style)) sb.Append($" style=\"{Esc(style)}\"");
                if (Math.Abs(styleDegree - 1.0) > 0.01) sb.Append($" styledegree=\"{styleDegree:F1}\"");
                if (!string.IsNullOrWhiteSpace(role)) sb.Append($" role=\"{Esc(role)}\"");
                sb.AppendLine(">");
            }

            if (useProsody)
            {
                sb.Append($"{ind1}<prosody");
                if (!string.IsNullOrWhiteSpace(resolvedRate)) sb.Append($" rate=\"{Esc(resolvedRate)}\"");
                if (!string.IsNullOrWhiteSpace(resolvedPitch)) sb.Append($" pitch=\"{Esc(resolvedPitch)}\"");
                if (!string.IsNullOrWhiteSpace(resolvedVolume)) sb.Append($" volume=\"{Esc(resolvedVolume)}\"");
                if (!string.IsNullOrWhiteSpace(range)) sb.Append($" range=\"{Esc(range)}\"");
                if (!string.IsNullOrWhiteSpace(contour)) sb.Append($" contour=\"{Esc(contour)}\"");
                sb.AppendLine(">");
            }

            if (!string.IsNullOrWhiteSpace(emphasisLevel))
                sb.AppendLine($"{ind1}<emphasis level=\"{Esc(emphasisLevel)}\">");

            if (!string.IsNullOrWhiteSpace(breakStrength) || !string.IsNullOrWhiteSpace(breakTime))
            {
                sb.Append($"{ind2}<break");
                if (!string.IsNullOrWhiteSpace(breakStrength)) sb.Append($" strength=\"{Esc(breakStrength)}\"");
                if (!string.IsNullOrWhiteSpace(breakTime)) sb.Append($" time=\"{Esc(breakTime)}\"");
                sb.AppendLine("/>");
            }

            sb.AppendLine($"{ind2}{EscContent(text)}");

            if (!string.IsNullOrWhiteSpace(emphasisLevel)) sb.AppendLine($"{ind1}</emphasis>");
            if (useProsody) sb.AppendLine($"{ind1}</prosody>");
            if (useExpressAs) sb.AppendLine($"{ind1}</mstts:express-as>");
            if (!string.IsNullOrWhiteSpace(langOverride)) sb.AppendLine($"{ind1}</lang>");
            sb.Append($"{ind}</voice>");

            return sb.ToString();
        }

        // ── 台本解析 ──────────────────────────────────

        private static readonly Regex ScriptSpeakerRegex = new(
            @"^\s*发言人\s*([A-Ca-c])\s*[：:]\s*(.+?)\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// 解析播客台本文本为 (发言人标签, 文本) 列表。
        /// 格式：发言人 A：文本内容
        /// </summary>
        public static List<(string Speaker, string Text)> ParseScript(string script)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(script)) return result;

            foreach (var line in script.Split('\n'))
            {
                var match = ScriptSpeakerRegex.Match(line);
                if (match.Success)
                {
                    var speaker = match.Groups[1].Value.ToUpperInvariant();
                    var text = match.Groups[2].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add((speaker, text));
                }
            }
            return result;
        }

        // ── 磁盘缓存 ──────────────────────────────────

        private static string GetCachePath(string key)
        {
            var safe = Regex.Replace(key, @"[^a-zA-Z0-9\-\.]", "_");
            return Path.Combine(PathManager.Instance.AppDataPath, "cache", $"tts-voices-{safe}.json");
        }

        private static bool TryReadVoicesFromDiskCache(string key, out List<VoiceInfo> voices)
        {
            voices = new List<VoiceInfo>();
            try
            {
                var path = GetCachePath(key);
                if (!File.Exists(path)) return false;
                var info = new FileInfo(path);
                if ((DateTime.UtcNow - info.LastWriteTimeUtc) > TimeSpan.FromHours(24)) return false;

                var json = File.ReadAllText(path);
                voices = JsonSerializer.Deserialize<List<VoiceInfo>>(json, JsonOpts) ?? new();
                return voices.Count > 0;
            }
            catch { return false; }
        }

        private static void TryWriteVoicesToDiskCache(string key, string json)
        {
            try
            {
                var path = GetCachePath(key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            catch { /* best-effort */ }
        }

        // ── XML 辅助 ──────────────────────────────────

        private static string Esc(string value) => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        private static string EscContent(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        private static string? Norm(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        private static string? FirstNonEmpty(params string?[] vals) => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }
}
