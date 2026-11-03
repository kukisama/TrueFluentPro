namespace TrueFluentPro.Models
{
    /// <summary>独立语音输入法配置；仅引用已有资源，不复制连接信息或凭据。</summary>
    public sealed class VoiceImeConfig
    {
        /// <summary>空字符串表示跟随主程序活动语音资源。</summary>
        public string SpeechResourceId { get; set; } = "";

        /// <summary>仅覆盖输入法本次运行快照，不修改共享 SpeechResource。</summary>
        public ModelReference? ModelRef { get; set; }

        /// <summary>空字符串表示使用系统默认麦克风。</summary>
        public string MicrophoneDeviceId { get; set; } = "";

        public string RecognitionLanguage { get; set; } = "auto";
        public double EndSilenceTimeoutSeconds { get; set; } = 0.8;
    }
}