using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace TrueFluentPro.Models;

/// <summary>只复制本页可编辑字段，不克隆/展示终结点凭据，保存前不引用可变业务对象。</summary>
public sealed class SongScrollInsightDraft
{
    public string SystemPrompt { get; set; }
    public string UserTemplate { get; set; }
    public bool BufferOutput { get; set; }
    public ObservableCollection<InsightPresetButton> Presets { get; }
    public ModelReference? QuickModel { get; set; }
    public bool AutoAvailable { get; set; }
    public int AutoModeIndex { get; set; }
    public int AutoIntervalSeconds { get; set; }
    public string? AutoPrompt { get; set; }
    public SongScrollInsightDraft(AiConfig config)
    {
        SystemPrompt = config.InsightSystemPrompt; UserTemplate = config.InsightUserContentTemplate;
        BufferOutput = config.AutoInsightBufferOutput;
        Presets = new(config.PresetButtons.Select(p => new InsightPresetButton { Name = p.Name, Prompt = p.Prompt }));
        QuickModel = Copy(config.QuickModelRef);
        AutoAvailable = config.SongScrollAutoAvailable; AutoModeIndex = config.SongScrollAutoModeIndex;
        AutoIntervalSeconds = config.SongScrollAutoIntervalSeconds; AutoPrompt = config.SongScrollAutoPrompt;
    }
    private static ModelReference? Copy(ModelReference? reference) => reference == null ? null : new ModelReference { EndpointId = reference.EndpointId, ModelId = reference.ModelId };
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SystemPrompt) || !UserTemplate.Contains("{history}") || !UserTemplate.Contains("{question}"))
            throw new InvalidOperationException("请填写系统提示词，并在模板中保留 {history} 与 {question}。");
        if (Presets.Any(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Prompt)))
            throw new InvalidOperationException("每项预设都需要名称与提示词，或删除空白预设。");
    }
    public void ApplyTo(AiConfig config)
    {
        Validate(); config.InsightSystemPrompt = SystemPrompt.Trim(); config.InsightUserContentTemplate = UserTemplate;
        config.AutoInsightBufferOutput = BufferOutput; config.QuickModelRef = Copy(QuickModel);
        config.PresetButtons = Presets.Select(p => new InsightPresetButton { Name = p.Name.Trim(), Prompt = p.Prompt }).ToList();
        config.SongScrollAutoAvailable = AutoAvailable; config.SongScrollAutoModeIndex = AutoModeIndex;
        config.SongScrollAutoIntervalSeconds = Math.Clamp(AutoIntervalSeconds, 10, 3600); config.SongScrollAutoPrompt = AutoPrompt;
    }
}