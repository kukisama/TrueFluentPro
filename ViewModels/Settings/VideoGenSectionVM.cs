using System;
using System.Collections.Generic;
using System.Linq;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;
using TrueFluentPro.Services;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.ViewModels.Settings
{
    public class VideoGenSectionVM : SettingsSectionBase
    {
        public sealed class VideoApiModeOptionView
        {
            public required VideoApiMode Mode { get; init; }
            public required string DisplayName { get; init; }
            public string Description { get; init; } = "";
            public override string ToString() => DisplayName;
        }

        private ModelOption? _selectedVideoModel;
        private List<ModelOption> _videoModels = new();
        private bool _suppressVideoModelAutoApiMode;

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
        private List<VideoApiModeOptionView> _videoApiModeOptions = new()
        {
            new VideoApiModeOptionView
            {
                Mode = VideoApiMode.Videos,
                DisplayName = "videos (/v1/videos)",
                Description = "标准视频接口。"
            }
        };

        public List<ModelOption> VideoModels { get => _videoModels; set => SetProperty(ref _videoModels, value); }
        public ModelOption? SelectedVideoModel
        {
            get => _selectedVideoModel;
            set
            {
                if (SetProperty(ref _selectedVideoModel, value))
                {
                    if (!_suppressVideoModelAutoApiMode && value != null)
                    {
                        RefreshVideoApiModeOptions();
                        ApplyProfileMappedVideoMode(value.Reference?.ModelId);
                    }

                    RefreshVideoCapabilityOptions();
                    OnChanged();
                }
            }
        }

        public List<string> VideoAspectRatioOptions { get => _videoAspectRatioOptions; private set => SetProperty(ref _videoAspectRatioOptions, value); }
        public List<string> VideoResolutionOptions { get => _videoResolutionOptions; private set => SetProperty(ref _videoResolutionOptions, value); }
        public List<int> VideoSecondsOptions { get => _videoSecondsOptions; private set => SetProperty(ref _videoSecondsOptions, value); }
        public List<int> VideoVariantsOptions { get => _videoVariantsOptions; private set => SetProperty(ref _videoVariantsOptions, value); }
        public List<VideoApiModeOptionView> VideoApiModeOptions { get => _videoApiModeOptions; private set => SetProperty(ref _videoApiModeOptions, value); }
        public string SelectedVideoApiModeDescription
            => VideoApiModeOptions.ElementAtOrDefault(VideoApiModeIndex)?.Description ?? "";

        public string VideoAspectRatio { get => _videoAspectRatio;
            set => Set(ref _videoAspectRatio, value, then: SyncVideoDimensionsFromSelection); }
        public string VideoResolution { get => _videoResolution;
            set => Set(ref _videoResolution, value, then: SyncVideoDimensionsFromSelection); }
        public int VideoApiModeIndex
        {
            get => _videoApiModeIndex;
            set
            {
                if (SetProperty(ref _videoApiModeIndex, value))
                {
                    OnPropertyChanged(nameof(SelectedVideoApiModeDescription));
                    RefreshVideoCapabilityOptions();
                    OnChanged();
                }
            }
        }

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

            VideoPollIntervalMs = media.VideoPollIntervalMs <= 0 ? 3000 : media.VideoPollIntervalMs;

            _videoAspectRatio = string.IsNullOrWhiteSpace(media.VideoAspectRatio) ? "16:9" : media.VideoAspectRatio;
            _videoResolution = string.IsNullOrWhiteSpace(media.VideoResolution) ? "720p" : media.VideoResolution;
            _videoSeconds = media.VideoSeconds <= 0 ? 5 : media.VideoSeconds;
            _videoVariants = media.VideoVariants <= 0 ? 1 : media.VideoVariants;

            RefreshVideoApiModeOptions();
            var resolvedMode = ResolveProfileMappedVideoApiMode(media.VideoModelRef?.ModelId, media.VideoApiMode);
            SetVideoApiModeIndexInternal(GetVideoApiModeIndex(resolvedMode));
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
            var supportedModes = GetCurrentSupportedVideoApiModes();

            media.VideoApiMode = supportedModes.ElementAtOrDefault(VideoApiModeIndex);
            if (!supportedModes.Contains(media.VideoApiMode))
            {
                media.VideoApiMode = VideoApiMode.Videos;
            }

            media.VideoAspectRatio = _videoAspectRatio ?? "16:9";
            media.VideoResolution = _videoResolution ?? "720p";
            media.VideoWidth = Math.Max(64, VideoWidth);
            media.VideoHeight = Math.Max(64, VideoHeight);
            media.VideoSeconds = Math.Clamp(_videoSeconds, 1, 120);
            media.VideoVariants = Math.Clamp(_videoVariants, 1, 4);
            media.VideoPollIntervalMs = Math.Clamp(VideoPollIntervalMs, 500, 60000);

            media.MaxLoadedSessionsInMemory = Math.Clamp(MaxLoadedSessionsInMemory, 1, 64);
            media.OutputDirectory = MediaOutputDirectory?.Trim() ?? "";
            media.VideoModelRef = SelectedVideoModel?.Reference;
        }

        public void SelectModels(ModelReference? videoModelRef, List<ModelOption> videoModels)
        {
            VideoModels = videoModels;
            SelectModelOption(videoModelRef, videoModels, v => _selectedVideoModel = v, nameof(SelectedVideoModel));
            RefreshVideoApiModeOptions();
            ApplyProfileMappedVideoMode(videoModelRef?.ModelId);
        }

        public void RefreshModels(List<ModelOption> videoModels)
        {
            var videoRef = SelectedVideoModel?.Reference;
            VideoModels = videoModels;
            SelectModelOption(videoRef, videoModels, v => _selectedVideoModel = v, nameof(SelectedVideoModel));
            RefreshVideoApiModeOptions();
            ApplyProfileMappedVideoMode(videoRef?.ModelId);
        }

        private void SelectModelOption(ModelReference? reference, List<ModelOption> options, Action<ModelOption?> setter, string propertyName)
        {
            var match = reference == null ? null
                : options.FirstOrDefault(o => o.Reference.EndpointId == reference.EndpointId && o.Reference.ModelId == reference.ModelId);
            _suppressVideoModelAutoApiMode = true;
            setter(match);
            _suppressVideoModelAutoApiMode = false;
            OnPropertyChanged(propertyName);
        }

        private void RefreshVideoApiModeOptions()
        {
            var modeOptions = GetCurrentVideoApiModeOptions();
            VideoApiModeOptions = modeOptions
                .ToList();

            if (VideoApiModeIndex >= VideoApiModeOptions.Count)
            {
                SetVideoApiModeIndexInternal(0);
            }

            OnPropertyChanged(nameof(SelectedVideoApiModeDescription));
        }

        private void ApplyProfileMappedVideoMode(string? modelId)
        {
            var resolved = ResolveProfileMappedVideoApiMode(modelId, GetCurrentVideoApiMode());
            SetVideoApiModeIndexInternal(GetVideoApiModeIndex(resolved));
        }

        private List<VideoApiModeOptionView> GetCurrentVideoApiModeOptions()
        {
            var endpoint = GetSelectedEndpoint();
            var videoConfig = EndpointProfileRuntimeResolver.Resolve(endpoint?.ProfileId, endpoint?.EndpointType ?? EndpointApiType.OpenAiCompatible)
                ?.Video;

            var configuredOptions = videoConfig?.ApiModeOptions ?? new List<EndpointProfileVideoApiModeOption>();

            var options = configuredOptions
                .Select(option => Enum.TryParse<VideoApiMode>(option.Mode, ignoreCase: true, out var parsed)
                    ? new VideoApiModeOptionView
                    {
                        Mode = parsed,
                        DisplayName = string.IsNullOrWhiteSpace(option.DisplayName) ? parsed.ToString() : option.DisplayName,
                        Description = option.Description ?? ""
                    }
                    : null)
                .Where(option => option != null)
                .Select(option => option!)
                .GroupBy(option => option.Mode)
                .Select(group => group.First())
                .ToList();

            if (options.Count > 0)
            {
                return options;
            }

            var fallbackModes = videoConfig?.SupportedApiModes
                ?? new List<string>();

            options = fallbackModes
                .Select(mode => Enum.TryParse<VideoApiMode>(mode, ignoreCase: true, out var parsed)
                    ? new VideoApiModeOptionView
                    {
                        Mode = parsed,
                        DisplayName = parsed.ToString(),
                        Description = ""
                    }
                    : null)
                .Where(option => option != null)
                .Select(option => option!)
                .GroupBy(option => option.Mode)
                .Select(group => group.First())
                .ToList();

            if (options.Count == 0)
            {
                options.Add(new VideoApiModeOptionView
                {
                    Mode = VideoApiMode.Videos,
                    DisplayName = "videos (/v1/videos)",
                    Description = "标准视频接口。"
                });
            }

            return options;
        }

        private List<VideoApiMode> GetCurrentSupportedVideoApiModes()
            => GetCurrentVideoApiModeOptions().Select(option => option.Mode).ToList();

        private VideoApiMode GetCurrentVideoApiMode()
        {
            var supportedModes = GetCurrentSupportedVideoApiModes();
            var mode = supportedModes.ElementAtOrDefault(VideoApiModeIndex);
            return supportedModes.Contains(mode) ? mode : VideoApiMode.Videos;
        }

        private VideoApiMode ResolveProfileMappedVideoApiMode(string? modelId, VideoApiMode fallbackMode)
            => EndpointProfileVideoModeResolver.ResolveVideoApiMode(
                GetSelectedEndpoint()?.ProfileId,
                GetSelectedEndpoint()?.EndpointType ?? EndpointApiType.OpenAiCompatible,
                modelId,
                fallbackMode);

        private int GetVideoApiModeIndex(VideoApiMode mode)
        {
            var index = VideoApiModeOptions.FindIndex(option => option.Mode == mode);
            return index >= 0 ? index : 0;
        }

        private AiEndpoint? GetSelectedEndpoint()
        {
            var endpointId = SelectedVideoModel?.Reference?.EndpointId;
            if (string.IsNullOrWhiteSpace(endpointId))
            {
                return null;
            }

            return Config?.Endpoints?.FirstOrDefault(endpoint => endpoint.Id == endpointId);
        }

        private void RefreshVideoCapabilityOptions()
        {
            var supportedModes = GetCurrentSupportedVideoApiModes();
            var mode = supportedModes.ElementAtOrDefault(VideoApiModeIndex);
            if (!supportedModes.Contains(mode))
            {
                mode = VideoApiMode.Videos;
            }

            var modelId = SelectedVideoModel?.Reference?.ModelId ?? string.Empty;
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
            var supportedModes = GetCurrentSupportedVideoApiModes();
            var mode = supportedModes.ElementAtOrDefault(VideoApiModeIndex);
            if (!supportedModes.Contains(mode))
            {
                mode = VideoApiMode.Videos;
            }

            var modelId = SelectedVideoModel?.Reference?.ModelId ?? string.Empty;
            if (VideoCapabilityResolver.TryResolveSize(mode, modelId, VideoAspectRatio, VideoResolution, out var w, out var h))
            {
                VideoWidth = w;
                VideoHeight = h;
            }
        }

        private void ApplyVideoSizeToAspectResolution(int width, int height)
        {
            var supportedModes = GetCurrentSupportedVideoApiModes();
            var mode = supportedModes.ElementAtOrDefault(VideoApiModeIndex);
            if (!supportedModes.Contains(mode))
            {
                mode = VideoApiMode.Videos;
            }

            var modelId = SelectedVideoModel?.Reference?.ModelId ?? string.Empty;
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

        private void SetVideoApiModeIndexInternal(int value)
        {
            if (_videoApiModeIndex == value)
                return;

            _videoApiModeIndex = value;
            OnPropertyChanged(nameof(VideoApiModeIndex));
        }
    }
}
