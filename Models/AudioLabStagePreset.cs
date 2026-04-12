namespace TrueFluentPro.Models
{
    /// <summary>阶段内容的 UI 展示模式。</summary>
    public enum StageDisplayMode
    {
        /// <summary>纯 Markdown 显示（总结、顿悟、研究、播客、翻译等）。</summary>
        Markdown = 0,
        /// <summary>思维导图显示（JSON 树 → Canvas 渲染）。</summary>
        MindMap = 2,
    }

    /// <summary>
    /// 听析中心阶段预设 — 定义每个 AI 阶段的提示词、显示、批处理参与等配置。
    /// 参考 ReviewSheetPreset 设计，但该阶段与 AudioLifecycleStage 一一对应。
    /// </summary>
    public class AudioLabStagePreset
    {
        /// <summary>阶段标识（对应 AudioLifecycleStage 枚举名）。</summary>
        public string Stage { get; set; } = "";

        /// <summary>显示名称，如"总结"、"顿悟"、"翻译"。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>系统提示词（传给 AI 的 system prompt）。为空则使用内置默认值。</summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>是否在听析中心显示此阶段的 Tab。</summary>
        public bool ShowInTab { get; set; } = true;

        /// <summary>是否参与 SubmitAll 批处理（加载音频时自动提交）。</summary>
        public bool IncludeInBatch { get; set; } = true;

        /// <summary>是否启用（false 时不显示也不处理，但配置保留）。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>内容展示模式：Markdown / Transcript / MindMap。</summary>
        public StageDisplayMode DisplayMode { get; set; } = StageDisplayMode.Markdown;

        /// <summary>用于 ComboBox SelectedIndex 绑定（0=Markdown, 1=导图）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int DisplayModeIndex
        {
            get => DisplayMode == StageDisplayMode.MindMap ? 1 : 0;
            set => DisplayMode = value == 1 ? StageDisplayMode.MindMap : StageDisplayMode.Markdown;
        }
    }
}
