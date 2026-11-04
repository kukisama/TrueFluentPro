using System.Collections.ObjectModel;
using System.ComponentModel;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Presentation;

/// <summary>翻译页的只读数据契约；外观不拥有或启停数据源。</summary>
public interface ILiveTranslationPresentationSource : INotifyPropertyChanged
{
    string CurrentOriginal { get; }
    string CurrentTranslated { get; }
    ObservableCollection<TranslationItem> History { get; }
    LiveTranslationFrame? LatestRealtimeFrame { get; }
}