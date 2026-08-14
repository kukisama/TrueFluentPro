using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.RealtimeSpeech.Translation
{
    /// <summary>「不翻译」占位 Provider：始终返回空字符串，编排器据此仅显示原文。</summary>
    public sealed class NullTranslationProvider : ITranslationProvider
    {
        public string DisplayName => "不翻译";

        public bool IsConfigured => false;

        public Task<string> TranslateAsync(string sourceText, string? sourceLanguage, string? targetLanguage, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);
    }
}
