using System;
using TrueFluentPro.Services.Audio;

namespace TrueFluentPro.Models;

/// <summary>业务已发布的只读发言快照，不含视图、样式或控件生命周期。</summary>
public sealed record LiveTranslationFrame(
    string OriginalText,
    string TranslatedText,
    DateTime Timestamp,
    VadGateController.ActiveSource Source,
    bool IsFinal,
    string? SegmentKey);