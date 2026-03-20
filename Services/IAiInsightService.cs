using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public interface IAiInsightService
    {
        Task StreamChatAsync(
            AiConfig config,
            string systemPrompt,
            string userContent,
            Action<string> onChunk,
            CancellationToken cancellationToken,
            AiChatProfile profile = AiChatProfile.Quick,
            bool enableReasoning = false,
            Action<AiRequestOutcome>? onOutcome = null,
            Action<string>? onReasoningChunk = null,
            Action<AiRequestTrace>? onTrace = null,
            IReadOnlyList<string>? urlCandidatesOverride = null,
            bool allowNextUrlRetry = true,
            bool allowApimSubscriptionKeyQueryRetry = true);

        Task StreamChatAsync(
            AiChatRequestConfig request,
            string systemPrompt,
            string userContent,
            Action<string> onChunk,
            CancellationToken cancellationToken,
            AiChatProfile profile = AiChatProfile.Quick,
            bool enableReasoning = false,
            Action<AiRequestOutcome>? onOutcome = null,
            Action<string>? onReasoningChunk = null,
            Action<AiRequestTrace>? onTrace = null,
            IReadOnlyList<string>? urlCandidatesOverride = null,
            bool allowNextUrlRetry = true,
            bool allowApimSubscriptionKeyQueryRetry = true);
    }
}
