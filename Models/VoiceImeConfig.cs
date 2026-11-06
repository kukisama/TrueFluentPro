namespace TrueFluentPro.Models
{
    /// <summary>独立语音输入法配置；仅引用已有资源，不复制连接信息或凭据。</summary>
    public sealed class VoiceImeConfig
    {
        /// <summary>切换到输入法时是否后台预热识别连接与设备（默认开启）。</summary>
        public bool EnablePrewarm { get; set; } = true;

        /// <summary>空字符串表示跟随主程序活动语音资源。</summary>
        public string SpeechResourceId { get; set; } = "";

        /// <summary>仅覆盖输入法本次运行快照，不修改共享 SpeechResource。</summary>
        public ModelReference? ModelRef { get; set; }

        /// <summary>空字符串表示使用系统默认麦克风。</summary>
        public string MicrophoneDeviceId { get; set; } = "";

        public string RecognitionLanguage { get; set; } = "auto";

        /// <summary>仅影响独立输入法；默认保持原文听写，不改变聊天听写或主界面翻译。</summary>
        public bool EnableTranslation { get; set; } = false;

        public string TargetLanguage { get; set; } = "en-US";
        public double EndSilenceTimeoutSeconds { get; set; } = 0.8;
    }
}