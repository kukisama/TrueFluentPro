using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.RealtimeSpeech.Translation
{
    /// <summary>
    /// MT 翻译角色积木：原文 → 译文。与 ASR 角色完全解耦，可独立选厂商、独立配置、独立替换。
    /// 失败或未配置时约定返回空字符串（编排器据此回退为仅显示原文）。
    /// </summary>
    public interface ITranslationProvider
    {
        /// <summary>厂商显示名（用于状态提示与日志）。</summary>
        string DisplayName { get; }

        /// <summary>是否已配置可用凭据。未配置时编排器跳过翻译、仅显示原文。</summary>
        bool IsConfigured { get; }

        /// <summary>翻译一句原文。失败返回空字符串（不抛出，由内部状态回调上报错误）。</summary>
        Task<string> TranslateAsync(string sourceText, string? sourceLanguage, string? targetLanguage, CancellationToken cancellationToken);
    }
}
