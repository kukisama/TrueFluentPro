using System;
using System.Threading;
using System.Windows.Input;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Settings
{
    public class StorageSectionVM : SettingsSectionBase
    {
        private bool _enableRecording = true;
        private int _recordingMp3BitrateKbps = 256;
        private string _sessionDirectory = "";
        private string _batchStorageConnectionString = "";
        private string _batchStorageStatus = "";
        private bool _batchStorageIsValid;
        private int _batchLogLevelIndex;
        private bool _batchForceRegeneration;
        private bool _contextMenuForceRegeneration = true;
        private bool _enableBatchSentenceSplit = true;
        private bool _batchSplitOnComma;
        private int _batchMaxChars = 24;
        private double _batchMaxDuration = 6;
        private int _batchPauseSplitMs = 500;
        private ReviewSubtitleSourceMode _reviewSubtitleSourceMode;

        public StorageSectionVM()
        {
            ValidateBatchStorageCommand = new RelayCommand(async _ => await ValidateBatchStorageAsync());
        }

        public bool EnableRecording { get => _enableRecording; set => Set(ref _enableRecording, value); }
        public int RecordingMp3BitrateKbps { get => _recordingMp3BitrateKbps; set => Set(ref _recordingMp3BitrateKbps, value); }
        public string SessionDirectory { get => _sessionDirectory; set => Set(ref _sessionDirectory, value); }
        public string BatchStorageConnectionString { get => _batchStorageConnectionString; set => SetProperty(ref _batchStorageConnectionString, value); }
        public string BatchStorageStatus { get => _batchStorageStatus; set => SetProperty(ref _batchStorageStatus, value); }
        public bool BatchStorageIsValid { get => _batchStorageIsValid; set => SetProperty(ref _batchStorageIsValid, value); }
        public int BatchLogLevelIndex { get => _batchLogLevelIndex; set => Set(ref _batchLogLevelIndex, value); }
        public bool BatchForceRegeneration { get => _batchForceRegeneration; set => Set(ref _batchForceRegeneration, value); }
        public bool ContextMenuForceRegeneration { get => _contextMenuForceRegeneration; set => Set(ref _contextMenuForceRegeneration, value); }
        public bool EnableBatchSentenceSplit { get => _enableBatchSentenceSplit; set => Set(ref _enableBatchSentenceSplit, value); }
        public bool BatchSplitOnComma { get => _batchSplitOnComma; set => Set(ref _batchSplitOnComma, value); }
        public int BatchMaxChars { get => _batchMaxChars; set => Set(ref _batchMaxChars, value); }
        public double BatchMaxDuration { get => _batchMaxDuration; set => Set(ref _batchMaxDuration, value); }
        public int BatchPauseSplitMs { get => _batchPauseSplitMs; set => Set(ref _batchPauseSplitMs, value); }
        public ReviewSubtitleSourceMode ReviewSubtitleSourceMode { get => _reviewSubtitleSourceMode; set => Set(ref _reviewSubtitleSourceMode, value); }
        public string ReviewSubtitleSourceDisplayText => BuildReviewSubtitleSourceDisplayText();
        public string ReviewSubtitleSourceDescription => BuildReviewSubtitleSourceDescription();
        public int ReviewSubtitleSourceModeIndex
        {
            get => (int)ReviewSubtitleSourceMode;
            set => ReviewSubtitleSourceMode = Enum.IsDefined(typeof(ReviewSubtitleSourceMode), value)
                ? (ReviewSubtitleSourceMode)value
                : ReviewSubtitleSourceMode.DefaultSubtitle;
        }

        public ICommand ValidateBatchStorageCommand { get; }

        /// <summary>内部访问配置，由宿主注入</summary>
        internal AzureSpeechConfig Config { get; set; } = new();

        public override void LoadFrom(AzureSpeechConfig config)
        {
            Config = config;
            EnableRecording = config.EnableRecording;
            RecordingMp3BitrateKbps = config.RecordingMp3BitrateKbps;
            SessionDirectory = config.SessionDirectory;
            BatchStorageConnectionString = config.BatchStorageConnectionString;
            BatchStorageIsValid = config.BatchStorageIsValid;
            BatchStorageStatus = config.BatchStorageIsValid ? "已验证存储账号" : "";
            BatchLogLevelIndex = config.BatchLogLevel switch
            {
                BatchLogLevel.FailuresOnly => 1,
                BatchLogLevel.SuccessAndFailure => 2,
                _ => 0
            };
            BatchForceRegeneration = config.BatchForceRegeneration;
            ContextMenuForceRegeneration = config.ContextMenuForceRegeneration;
            EnableBatchSentenceSplit = config.EnableBatchSubtitleSentenceSplit;
            BatchSplitOnComma = config.BatchSubtitleSplitOnComma;
            BatchMaxChars = config.BatchSubtitleMaxChars;
            BatchMaxDuration = config.BatchSubtitleMaxDurationSeconds;
            BatchPauseSplitMs = config.BatchSubtitlePauseSplitMs;
            ReviewSubtitleSourceMode = config.GetEffectiveReviewSubtitleSourceMode();
            OnPropertyChanged(nameof(ReviewSubtitleSourceDisplayText));
            OnPropertyChanged(nameof(ReviewSubtitleSourceDescription));
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            config.EnableRecording = EnableRecording;
            config.RecordingMp3BitrateKbps = RecordingMp3BitrateKbps;
            var defaultDir = Services.PathManager.Instance.DefaultSessionsPath;
            config.SessionDirectoryOverride = string.IsNullOrWhiteSpace(SessionDirectory)
                ? null
                : (string.Equals(SessionDirectory, defaultDir, StringComparison.OrdinalIgnoreCase) ? null : SessionDirectory);
            Services.PathManager.Instance.SetSessionsPath(config.SessionDirectoryOverride);

            config.BatchStorageConnectionString = BatchStorageConnectionString?.Trim() ?? "";
            config.BatchAudioContainerName = AzureSpeechConfig.DefaultBatchAudioContainerName;
            config.BatchResultContainerName = AzureSpeechConfig.DefaultBatchResultContainerName;
            config.BatchLogLevel = BatchLogLevelIndex switch
            {
                1 => BatchLogLevel.FailuresOnly,
                2 => BatchLogLevel.SuccessAndFailure,
                _ => BatchLogLevel.Off
            };
            config.BatchForceRegeneration = BatchForceRegeneration;
            config.ContextMenuForceRegeneration = ContextMenuForceRegeneration;
            config.EnableBatchSubtitleSentenceSplit = EnableBatchSentenceSplit;
            config.BatchSubtitleSplitOnComma = BatchSplitOnComma;
            config.BatchSubtitleMaxChars = BatchMaxChars;
            config.BatchSubtitleMaxDurationSeconds = BatchMaxDuration;
            config.BatchSubtitlePauseSplitMs = BatchPauseSplitMs;
            config.ReviewSubtitleSourceMode = ReviewSubtitleSourceMode.DefaultSubtitle;
            config.UseSpeechSubtitleForReview = config.GetEffectiveReviewSubtitleSourceMode() == ReviewSubtitleSourceMode.SpeechSubtitle;
        }

        private async System.Threading.Tasks.Task ValidateBatchStorageAsync()
        {
            var cs = BatchStorageConnectionString?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(cs))
            {
                BatchStorageStatus = "请填写存储账号连接字符串";
                BatchStorageIsValid = false;
                return;
            }

            BatchStorageStatus = "验证中...";
            try
            {
                var client = new Azure.Storage.Blobs.BlobServiceClient(cs);
                await client.GetAccountInfoAsync(CancellationToken.None);

                var audioContainer = client.GetBlobContainerClient(AzureSpeechConfig.DefaultBatchAudioContainerName);
                await audioContainer.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);

                var resultContainer = client.GetBlobContainerClient(AzureSpeechConfig.DefaultBatchResultContainerName);
                await resultContainer.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None);

                BatchStorageStatus = "存储账号验证成功，可用";
                BatchStorageIsValid = true;
                Config.BatchStorageIsValid = true;
                Config.BatchStorageConnectionString = cs;
                OnChanged();
            }
            catch (Exception ex)
            {
                BatchStorageStatus = $"存储账号验证失败: {ex.Message}";
                BatchStorageIsValid = false;
                Config.BatchStorageIsValid = false;
            }
        }

        private string BuildReviewSubtitleSourceDisplayText()
        {
            var resource = Config.GetActiveSpeechResource();
            if (resource == null)
            {
                return "未选择首页语音资源";
            }

            return Config.GetEffectiveReviewSubtitleSourceMode() switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => $"跟随首页语音资源：Speech 字幕（{resource.GetDisplayName()}）",
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => $"跟随首页语音资源：AI 转写字幕（{resource.GetDisplayName()}）",
                _ => $"跟随首页语音资源：普通字幕（{resource.GetDisplayName()}）"
            };
        }

        private string BuildReviewSubtitleSourceDescription()
        {
            var resource = Config.GetActiveSpeechResource();
            if (resource == null)
            {
                return "复盘 / 批处理的字幕来源不再单独配置；会优先跟随首页当前选中的语音资源。当前尚未选择资源，因此不会自动切到 Speech / AI 转写链路。";
            }

            return Config.GetEffectiveReviewSubtitleSourceMode() switch
            {
                ReviewSubtitleSourceMode.SpeechSubtitle => "复盘 / 批处理将直接跟随首页当前的 Microsoft Speech 资源；若要切换为 AI 转写，请回到首页切换语音资源。",
                ReviewSubtitleSourceMode.AiTranscriptionSubtitle => "复盘 / 批处理将直接跟随首页当前的 AI 语音资源；若要切换为 Azure Speech，请回到首页切换语音资源。",
                _ => "当前首页语音资源不对应可用的 Speech / AI 转写链路，复盘 / 批处理会继续按现有字幕文件工作。"
            };
        }
    }
}
