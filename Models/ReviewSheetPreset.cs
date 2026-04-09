namespace TrueFluentPro.Models
{
    public class ReviewSheetPreset
    {
        public string Name { get; set; } = "";
        public string FileTag { get; set; } = "";
        public string Prompt { get; set; } = "";
        public bool IncludeInBatch { get; set; } = true;
        /// <summary>是否启用此模板（false 时下次录音不会处理此模板，但不删除）</summary>
        public bool IsEnabled { get; set; } = true;
    }
}
