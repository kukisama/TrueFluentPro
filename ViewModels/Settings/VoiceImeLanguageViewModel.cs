using System;
using System.Collections.ObjectModel;
using System.Linq;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Settings;

/// <summary>仅保存三个语言字段的草稿，不持有整个共享配置快照。</summary>
public sealed class VoiceImeLanguageViewModel : ViewModelBase
{
    private bool _loading;
    private bool _enableTranslation;
    private LanguageOption? _source, _target;
    public ObservableCollection<LanguageOption> SourceLanguages { get; } = new(AudioLabSectionVM.SourceLanguageOptions);
    public ObservableCollection<LanguageOption> TargetLanguages { get; } = new(AudioLabSectionVM.SourceLanguageOptions
        .Where(option => option.Code != "auto"));
    public event Action? Changed;

    public LanguageOption? SelectedSource
    {
        get => _source;
        set { if (value != null && SetProperty(ref _source, value) && !_loading) Changed?.Invoke(); }
    }
    public LanguageOption? SelectedTarget
    {
        get => _target;
        set { if (value != null && SetProperty(ref _target, value) && !_loading) Changed?.Invoke(); }
    }
    public bool EnableTranslation
    {
        get => _enableTranslation;
        set { if (SetProperty(ref _enableTranslation, value) && !_loading) Changed?.Invoke(); }
    }

    public void LoadFrom(VoiceImeConfig settings)
    {
        _loading = true;
        try
        {
            SelectedSource = Select(SourceLanguages, settings.RecognitionLanguage, "auto");
            SelectedTarget = Select(TargetLanguages, settings.TargetLanguage, "en-US");
            EnableTranslation = settings.EnableTranslation;
        }
        finally { _loading = false; }
    }

    public VoiceImeConfig CreateDraft() => new()
    {
        RecognitionLanguage = SelectedSource?.Code ?? "auto",
        EnableTranslation = EnableTranslation,
        TargetLanguage = SelectedTarget?.Code ?? "en-US"
    };

    private static LanguageOption Select(ObservableCollection<LanguageOption> options, string? code, string fallback)
    {
        code = string.IsNullOrWhiteSpace(code) ? fallback : code.Trim();
        var option = options.FirstOrDefault(item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
        if (option != null) return option;
        option = new LanguageOption(code, "已保存的语言");
        options.Add(option); // 不因列表变化悄悄改掉用户原有选择。
        return option;
    }
}