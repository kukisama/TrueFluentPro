using System;
using System.Collections.Generic;
using System.Linq;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels.Settings
{
    public class VideoGenSectionVM : SettingsSectionBase
    {
        private ModelOption? _selectedVideoModel;
        private List<ModelOption> _videoModels = new();

        private int _videoApiModeIndex;
        private int _videoWidth = 1280;
        private int _videoHeight = 720;
        private int _videoPollIntervalMs = 3000;
        private string _videoAspectRatio = "16:9";
        private string _videoResolution = "720p";
        private int _videoSeconds = 5;
        private int _videoVariants = 1;

        private int _maxLoadedSessionsInMemory = 8;
        private string _mediaOutputDirectory = "";

        private List<string> _videoAspectRatioOptions = new() { "16:9", "9:16" };
        private List<string> _videoResolutionOptions = new() { "720p" };
        private List<int> _videoSecondsOptions = new() { 4, 8, 12 };
        private List<int> _videoVariantsOptions = new() { 1 };

        public List<ModelOption> VideoModels { get => _videoModels; set => SetProperty(ref _videoModels, value); }
        public ModelOption? SelectedVideoModel { get => _selectedVideoModel;
            set => Set(ref _selectedVideoModel, value, then: RefreshVideoCapabilityOptions); }

        public List<string> VideoAspectRatioOptions { get => _videoAspectRatioOptions; private set => SetProperty(ref _videoAspectRatioOptions, value); }
        public List<string> VideoResolutionOptions { get => _videoResolutionOptions; private set => SetProperty(ref _videoResolutionOptions, value); }
        public List<int> VideoSecondsOptions { get => _videoSecondsOptions; private set => SetProperty(ref _videoSecondsOptions, value); }
        public List<int> VideoVariantsOptions { get => _videoVariantsOptions; private set => SetProperty(ref _videoVariantsOptions, value); }

        public string VideoAspectRatio { get => _videoAspectRatio;
            set => Set(ref _videoAspectRatio, value, then: SyncVideoDimensionsFromSelection); }
        public string VideoResolution { get => _videoResolution;
            set => Set(ref _videoResolution, value, then: SyncVideoDimensionsFromSelection); }
        public int VideoApiModeIndex { get => _videoApiModeIndex;
            set => Set(ref _videoApiModeIndex, value, then: RefreshVideoCapabilityOptions); }

        public int VideoWidth { get => _videoWidth; private set => SetProperty(ref _videoWidth, value); }
        public int VideoHeight { get => _videoHeight; private set => SetProperty(ref _videoHeight, value); }
        public int VideoSeconds { get => _videoSeconds; set => Set(ref _videoSeconds, value); }
        public int VideoVariants { get => _videoVariants; set => Set(ref _videoVariants, value); }
        public int VideoPollIntervalMs { get => _videoPollIntervalMs; set => Set(ref _videoPollIntervalMs, value); }
        public int MaxLoadedSessionsInMemory { get => _maxLoadedSessionsInMemory; set => Set(ref _maxLoadedSessionsInMemory, value); }
        public string MediaOutputDirectory { get => _mediaOutputDirectory; set => Set(ref _mediaOutputDirectory, value); }

        /// <summary>内部访问配置，由宿主注入</summary>
        internal AzureSpeechConfig Config { get; set; } = new();

        public override void LoadFrom(AzureSpeechConfig config)
        {
            Config = config;
            config.MediaGenConfig ??= new MediaGenConfig();
            var media = config.MediaGenConfig;

            VideoApiModeIndex = media.VideoApiMode == VideoApiMode.Videos ? 1 : 0;
            VideoPollIntervalMs = media.VideoPollIntervalMs <= 0 ? 3000 : media.VideoPollIntervalMs;

            _videoAspectRatio = string.IsNullOrWhiteSpace(media.VideoAspectRatio) ? "16:9" : media.VideoAspectRatio;
            _videoResolution = string.IsNullOrWhiteSpace(media.VideoResolution) ? "720p" : media.VideoResolution;
            _videoSeconds = media.VideoSeconds <= 0 ? 5 : media.VideoSeconds;
            _videoVariants = media.VideoVariants <= 0 ? 1 : media.VideoVariants;

            RefreshVideoCapabilityOptions();
            ApplyVideoSizeToAspectResolution(media.VideoWidth, media.VideoHeight);

            MaxLoadedSessionsInMemory = media.MaxLoadedSessionsInMemory <= 0 ? 8 : media.MaxLoadedSessionsInMemory;
            MediaOutputDirectory = media.OutputDirectory ?? "";

            OnPropertyChanged(nameof(VideoAspectRatio));
            OnPropertyChanged(nameof(VideoResolution));
            OnPropertyChanged(nameof(VideoSeconds));
            OnPropertyChanged(nameof(VideoVariants));
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            var media = config.MediaGenConfig;

            media.VideoApiMode = VideoApiModeIndex == 1 ? VideoApiMode.Videos : VideoApiMode.SoraJobs;
            media.VideoAspectRatio = _videoAspectRatio ?? "16:9";
            media.VideoResolution = _videoResolution ?? "720p";
            media.VideoWidth = Math.Max(64, VideoWidth);
            media.VideoHeight = Math.Max(64, VideoHeight);
            media.VideoSeconds = Math.Clamp(_videoSeconds, 1, 120);
            media.VideoVariants = Math.Clamp(_videoVariants, 1, 4);
            media.VideoPollIntervalMs = Math.Clamp(VideoPollIntervalMs, 500, 60000);

            media.MaxLoadedSessionsInMemory = Math.Clamp(MaxLoadedSessionsInMemory, 1, 64);
            media.OutputDirectory = MediaOutputDirectory?.Trim() ?? "";

            if (SelectedVideoModel?.Reference != null)
            {
                media.VideoModel = SelectedVideoModel.Reference.ModelId;
                media.VideoModelRef = SelectedVideoModel.Reference;
                var (vidEp, _) = config.ResolveModel(SelectedVideoModel.Reference);
                if (vidEp != null)
                {
                    media.VideoProviderType = vidEp.ProviderType;
                    media.VideoApiEndpoint = vidEp.BaseUrl?.Trim() ?? "";
                    media.VideoApiKey = vidEp.ApiKey?.Trim() ?? "";
                    media.VideoAzureAuthMode = vidEp.AuthMode;
                    media.VideoAzureTenantId = vidEp.AzureTenantId ?? "";
                    media.VideoAzureClientId = vidEp.AzureClientId ?? "";
                }
            }
        }

        public void SelectModels(ModelReference? videoModelRef, List<ModelOption> videoModels)
        {
            VideoModels = videoModels;
            SelectModelOption(videoModelRef, videoModels, v => _selectedVideoModel = v, nameof(SelectedVideoModel));
        }

        public void RefreshModels(List<ModelOption> videoModels)
        {
            var videoRef = SelectedVideoModel?.Reference;
            VideoModels = videoModels;
            SelectModelOption(videoRef, videoModels, v => _selectedVideoModel = v, nameof(SelectedVideoModel));
        }

        private void SelectModelOption(ModelReference? reference, List<ModelOption> options, Action<ModelOption?> setter, string propertyName)
        {
            var match = reference == null ? null
                : options.FirstOrDefault(o => o.Reference.EndpointId == reference.EndpointId && o.Reference.ModelId == reference.ModelId);
            setter(match);
            OnPropertyChanged(propertyName);
        }

        private void RefreshVideoCapabilityOptions()
        {
            var mode = VideoApiModeIndex == 1 ? VideoApiMode.Videos : VideoApiMode.SoraJobs;
            var modelId = SelectedVideoModel?.Reference?.ModelId ?? Config.MediaGenConfig.VideoModel;
            var profile = VideoCapabilityResolver.ResolveProfile(mode, modelId);

            VideoAspectRatioOptions = profile.AspectRatioOptions.ToList();
            VideoResolutionOptions = profile.ResolutionOptions.ToList();
            VideoSecondsOptions = profile.DurationOptions.ToList();
            VideoVariantsOptions = profile.CountOptions.ToList();

            if (!VideoAspectRatioOptions.Contains(VideoAspectRatio))
                VideoAspectRatio = VideoAspectRatioOptions.FirstOrDefault() ?? "16:9";
            if (!VideoResolutionOptions.Contains(VideoResolution))
                VideoResolution = VideoResolutionOptions.FirstOrDefault() ?? "720p";
            if (!VideoSecondsOptions.Contains(VideoSeconds))
                VideoSeconds = VideoSecondsOptions.FirstOrDefault();
            if (!VideoVariantsOptions.Contains(VideoVariants))
                VideoVariants = VideoVariantsOptions.FirstOrDefault();

            SyncVideoDimensionsFromSelection();
        }

        private void SyncVideoDimensionsFromSelection()
        {
            var mode = VideoApiModeIndex == 1 ? VideoApiMode.Videos : VideoApiMode.SoraJobs;
            var modelId = SelectedVideoModel?.Reference?.ModelId ?? Config.MediaGenConfig.VideoModel;
            if (VideoCapabilityResolver.TryResolveSize(mode, modelId, VideoAspectRatio, VideoResolution, out var w, out var h))
            {
                VideoWidth = w;
                VideoHeight = h;
            }
        }

        private void ApplyVideoSizeToAspectResolution(int width, int height)
        {
            var mode = VideoApiModeIndex == 1 ? VideoApiMode.Videos : VideoApiMode.SoraJobs;
            var modelId = SelectedVideoModel?.Reference?.ModelId ?? Config.MediaGenConfig.VideoModel;
            var profile = VideoCapabilityResolver.ResolveProfile(mode, modelId);

            foreach (var aspect in profile.AspectRatioOptions)
            {
                foreach (var res in profile.ResolutionOptions)
                {
                    if (profile.TryResolveSize(aspect, res, out var w, out var h)
                        && w == width && h == height)
                    {
                        VideoAspectRatio = aspect;
                        VideoResolution = res;
                        return;
                    }
                }
            }

            SyncVideoDimensionsFromSelection();
        }
    }
}
