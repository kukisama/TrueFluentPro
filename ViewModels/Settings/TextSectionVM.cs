using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Settings
{
    public class TextSectionVM : SettingsSectionBase
    {
        private bool _exportSrt;
        private bool _exportVtt = true;
        private int _defaultFontSize = 38;

        public bool ExportSrt { get => _exportSrt; set => Set(ref _exportSrt, value); }
        public bool ExportVtt { get => _exportVtt; set => Set(ref _exportVtt, value); }
        public int DefaultFontSize { get => _defaultFontSize; set => Set(ref _defaultFontSize, value); }

        public override void LoadFrom(AzureSpeechConfig config)
        {
            ExportSrt = config.ExportSrtSubtitles;
            ExportVtt = config.ExportVttSubtitles;
            DefaultFontSize = config.DefaultFontSize;
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            config.ExportSrtSubtitles = ExportSrt;
            config.ExportVttSubtitles = ExportVtt;
            config.DefaultFontSize = DefaultFontSize;
            Controls.AdvancedRichTextBox.DefaultFontSizeValue = DefaultFontSize;
        }
    }
}
