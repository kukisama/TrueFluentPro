using System;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public interface IRealtimeTranslationService
    {
        RealtimeConnectorFamily ConnectorFamily { get; }

        event EventHandler<TranslationItem>? OnRealtimeTranslationReceived;
        event EventHandler<TranslationItem>? OnFinalTranslationReceived;
        event EventHandler<string>? OnStatusChanged;
        event EventHandler<string>? OnReconnectTriggered;
        event EventHandler<double>? OnAudioLevelUpdated;
        event EventHandler<string>? OnDiagnosticsUpdated;

        Task<bool> StartTranslationAsync();
        Task StopTranslationAsync();
        Task UpdateConfigAsync(AzureSpeechConfig newConfig);
        bool TryApplyLiveAudioRoutingFromCurrentConfig(int fadeMilliseconds = 30);
    }
}
