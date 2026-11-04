using TrueFluentPro.Models;
using TrueFluentPro.Services.Audio;
using TrueFluentPro.ViewModels.Presentation;

namespace TrueFluentPro.ViewModels;

public partial class MainWindowViewModel : ILiveTranslationPresentationSource
{
    private LiveTranslationFrame? _latestRealtimeFrame;

    public LiveTranslationFrame? LatestRealtimeFrame
    {
        get => _latestRealtimeFrame;
        private set => SetProperty(ref _latestRealtimeFrame, value);
    }

    private void PublishPresentationFrame(TranslationItem item, bool isFinal)
    {
        var source = item.SubtitleSource is { } routed && routed != VadGateController.ActiveSource.None
            ? routed : item.Source;
        LatestRealtimeFrame = new LiveTranslationFrame(item.OriginalText, item.TranslatedText,
            item.Timestamp, source, isFinal, item.SubtitleSegmentKey);
    }
}