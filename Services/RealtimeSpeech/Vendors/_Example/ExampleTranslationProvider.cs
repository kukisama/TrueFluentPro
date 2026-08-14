using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Services.RealtimeSpeech.Translation;

namespace TrueFluentPro.Services.RealtimeSpeech.Vendors.Example
{
    /// <summary>
    /// ════════════════════════════════════════════════════════════════════════
    /// 【示例·翻译积木】演示如何为一个新厂商接入「机器翻译（MT）」。
    /// ════════════════════════════════════════════════════════════════════════
    ///
    /// 这是一个**可编译、带满注释的样板**。翻译积木比识别积木简单得多，
    /// 只有 3 个成员：显示名、是否配好凭据、翻译一句话。
    ///
    /// ── 与识别完全解耦 ──
    /// 识别用哪个厂商、翻译用哪个厂商是分开选的，可任意组合
    /// （例如「示例厂商识别 + 百度翻译」）。所以翻译积木只管翻译，不关心识别。
    ///
    /// ── 核心怎么用它 ──
    /// 每当识别出一句**定稿**原文（IAsrConnector 里调了 ReportFinal），
    /// 核心就会调用本类的 TranslateAsync 把它译成目标语言。
    /// 失败返回 null 不会影响原文展示（只是没有译文）。
    /// </summary>
    public sealed class ExampleTranslationProvider : ITranslationProvider
    {
        private readonly string _appId;
        private readonly string _apiKey;
        private readonly Action<string>? _status;
        private readonly Action<string>? _log;

        // 用一个静态 HttpClient 复用连接（真实厂商建议这样，避免端口耗尽）。
        private static readonly HttpClient Http = new();

        /// <summary>
        /// 构造时把凭据传进来。这些值来自 SpeechResource 的 Translate* 字段，
        /// 由装配器 SpeechRoleResolver 取出后传入（见文档「接线」一节）。
        /// </summary>
        public ExampleTranslationProvider(
            string appId,
            string apiKey,
            Action<string>? status = null,
            Action<string>? log = null)
        {
            _appId = appId?.Trim() ?? "";
            _apiKey = apiKey?.Trim() ?? "";
            _status = status;
            _log = log;
        }

        // ── 厂商显示名：出现在状态栏与日志里。──
        public string DisplayName => "示例翻译";

        // ── 是否配好了凭据：缺凭据时核心会跳过翻译、只显示原文。──
        public bool IsConfigured => !string.IsNullOrWhiteSpace(_appId) && !string.IsNullOrWhiteSpace(_apiKey);

        /// <summary>
        /// 把一句定稿原文译成目标语言。
        ///   · 成功 → 返回译文字符串
        ///   · 失败 / 没配凭据 → **返回空字符串 ""**（核心据此优雅降级为只显示原文，切勿抛异常）
        /// 入参 sourceLanguage / targetLanguage 是核心给出的源/目标语言提示（可能为 null，按需使用）。
        /// 下面用伪代码演示「调一个 HTTP 翻译接口」的位置，真实厂商照其文档填即可。
        /// </summary>
        public async Task<string> TranslateAsync(string sourceText, string? sourceLanguage, string? targetLanguage, CancellationToken cancellationToken)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(sourceText))
            {
                return string.Empty;
            }

            try
            {
                // ─────────────────────────────────────────────────────────────
                // 真实实现：在这里构造请求、带上签名、POST 到厂商翻译接口、解析译文。
                // 例如（伪代码）：
                //   var req = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/translate");
                //   req.Content = JsonContent.Create(new { appId = _appId, q = sourceText,
                //                                            from = sourceLanguage, to = targetLanguage, sign = ... });
                //   var resp = await Http.SendAsync(req, cancellationToken);
                //   var dto  = await resp.Content.ReadFromJsonAsync<...>(cancellationToken);
                //   return dto?.TranslatedText ?? string.Empty;
                // ─────────────────────────────────────────────────────────────

                // 本示例不真正联网，仅原样回显并打个标记，证明链路被调用到。
                _log?.Invoke($"[示例翻译] 收到原文：{sourceText}（{sourceLanguage} → {targetLanguage}）");
                await Task.Yield(); // 占位：真实实现这里是 await 网络请求。
                return $"[示例译文] {sourceText}";
            }
            catch (Exception ex)
            {
                _status?.Invoke($"示例翻译失败：{ex.Message}");
                return string.Empty; // 失败不抛异常，返回空字符串让核心降级显示原文。
            }
        }
    }
}
