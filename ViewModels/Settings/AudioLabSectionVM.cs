using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Speech;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.ViewModels.Settings
{
    public class AudioLabSectionVM : SettingsSectionBase
    {
        private readonly SpeechSynthesisService? _ttsService;
        private readonly IAzureTokenProviderStore? _tokenProviderStore;
        private Func<AzureSpeechConfig>? _configProvider;

        // ── 文本模型 ──
        private List<ModelOption> _textModels = new();
        private ModelOption? _selectedTextModel;

        // ── 语音来源模式 ──
        private int _speechModeIndex;
        private List<EndpointOption> _aadEndpoints = new();
        private List<EndpointOption> _speechEndpoints = new();
        private EndpointOption? _selectedAadEndpoint;
        private EndpointOption? _selectedSpeechEndpoint;
        private string _derivedSttUrl = "";
        private string _derivedTtsUrl = "";
        private string _derivedRegion = "";

        // ── 源语言 ──
        private string _sourceLanguage = "auto";

        // ── 播客语音配置 ──
        private string _podcastSpeakerAVoice = "XiaochenMultilingual";
        private string _podcastSpeakerBVoice = "Yunfeng";
        private string _podcastSpeakerCVoice = "Xiaoshuang";
        private string _podcastLanguage = "zh-CN";
        private string _podcastOutputFormat = "";

        // ── 语音列表（下拉选择用） ──
        private List<VoiceInfo> _allVoices = new();
        private bool _voicesLoaded;
        private bool _isLoadingVoices;
        private string _voiceLoadStatus = "";
        private VoiceLanguageOption? _selectedVoiceLanguage;

        // ── 听析阶段预设 ──
        private ObservableCollection<AudioLabStagePreset> _stagePresets = new();

        // ── 调试模式 ──
        private bool _debugMode;
        public bool DebugMode { get => _debugMode; set { if (SetProperty(ref _debugMode, value)) OnChanged(); } }

        public ObservableCollection<VoiceInfo> AvailableVoices { get; } = new();
        public ObservableCollection<VoiceLanguageOption> AvailableLanguages { get; } = new();
        public ObservableCollection<SpeakerProfile> SpeakerProfiles { get; } = new();

        public ObservableCollection<AudioLabStagePreset> StagePresets { get => _stagePresets; set => SetProperty(ref _stagePresets, value); }

        public void NotifyStagePresetsChanged()
        {
            OnPropertyChanged(nameof(StagePresets));
            OnChanged();
        }

        public bool VoicesLoaded { get => _voicesLoaded; set => SetProperty(ref _voicesLoaded, value); }
        public bool IsLoadingVoices { get => _isLoadingVoices; set => SetProperty(ref _isLoadingVoices, value); }
        public string VoiceLoadStatus { get => _voiceLoadStatus; set => SetProperty(ref _voiceLoadStatus, value); }

        public VoiceLanguageOption? SelectedVoiceLanguage
        {
            get => _selectedVoiceLanguage;
            set
            {
                if (!SetProperty(ref _selectedVoiceLanguage, value)) return;
                ApplyVoiceLanguageFilter();
                // 更新配置语言
                PodcastLanguage = value?.Locale ?? "";
                OnChanged();
            }
        }

        public ICommand LoadVoicesCommand { get; }

        public AudioLabSectionVM() : this(null, null) { }

        public AudioLabSectionVM(
            SpeechSynthesisService? ttsService,
            IAzureTokenProviderStore? tokenProviderStore)
        {
            _ttsService = ttsService;
            _tokenProviderStore = tokenProviderStore;

            // 初始化 3 个发言人
            SpeakerProfiles.Add(new SpeakerProfile("A", "发言人 A"));
            SpeakerProfiles.Add(new SpeakerProfile("B", "发言人 B"));
            SpeakerProfiles.Add(new SpeakerProfile("C", "发言人 C"));

            foreach (var p in SpeakerProfiles)
                p.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SpeakerProfile.Voice))
                    {
                        SyncSpeakerVoicesToConfig();
                        OnChanged();
                    }
                };

            LoadVoicesCommand = new RelayCommand(_ => _ = LoadVoicesAsync(), _ => !IsLoadingVoices);
        }

        public void SetConfigProvider(Func<AzureSpeechConfig> provider) => _configProvider = provider;

        // ═══ 文本模型 ═══
        public List<ModelOption> TextModels { get => _textModels; set => SetProperty(ref _textModels, value); }
        public bool HasTextModels => TextModels.Count > 0;
        public ModelOption? SelectedTextModel { get => _selectedTextModel; set => Set(ref _selectedTextModel, value); }

        // ═══ 语音来源模式 ═══
        public int SpeechModeIndex
        {
            get => _speechModeIndex;
            set => Set(ref _speechModeIndex, value, then: () =>
            {
                OnPropertyChanged(nameof(IsAadMode));
                OnPropertyChanged(nameof(IsTraditionalMode));
            });
        }
        public bool IsAadMode => SpeechModeIndex == 0;
        public bool IsTraditionalMode => SpeechModeIndex == 1;

        // ═══ AAD 模式 ═══
        public List<EndpointOption> AadEndpoints { get => _aadEndpoints; set => SetProperty(ref _aadEndpoints, value); }
        public bool HasAadEndpoints => AadEndpoints.Count > 0;
        public EndpointOption? SelectedAadEndpoint
        {
            get => _selectedAadEndpoint;
            set => Set(ref _selectedAadEndpoint, value, then: UpdateDerivedSpeechInfo);
        }
        public string DerivedSttUrl { get => _derivedSttUrl; set => SetProperty(ref _derivedSttUrl, value); }
        public string DerivedTtsUrl { get => _derivedTtsUrl; set => SetProperty(ref _derivedTtsUrl, value); }
        public string DerivedRegion { get => _derivedRegion; set => SetProperty(ref _derivedRegion, value); }

        // ═══ 传统语音终结点模式 ═══
        public List<EndpointOption> SpeechEndpoints { get => _speechEndpoints; set => SetProperty(ref _speechEndpoints, value); }
        public bool HasSpeechEndpoints => SpeechEndpoints.Count > 0;
        public EndpointOption? SelectedSpeechEndpoint
        {
            get => _selectedSpeechEndpoint;
            set => Set(ref _selectedSpeechEndpoint, value);
        }

        // ═══ 源语言 ═══
        public string SourceLanguage { get => _sourceLanguage; set => Set(ref _sourceLanguage, value); }

        // ═══ 播客语音配置 ═══
        public string PodcastSpeakerAVoice { get => _podcastSpeakerAVoice; set => Set(ref _podcastSpeakerAVoice, value); }
        public string PodcastSpeakerBVoice { get => _podcastSpeakerBVoice; set => Set(ref _podcastSpeakerBVoice, value); }
        public string PodcastSpeakerCVoice { get => _podcastSpeakerCVoice; set => Set(ref _podcastSpeakerCVoice, value); }
        public string PodcastLanguage { get => _podcastLanguage; set => Set(ref _podcastLanguage, value); }
        public string PodcastOutputFormat { get => _podcastOutputFormat; set => Set(ref _podcastOutputFormat, value); }

        private PodcastOutputFormatOption? _selectedPodcastOutputFormat;
        public PodcastOutputFormatOption? SelectedPodcastOutputFormat
        {
            get => _selectedPodcastOutputFormat;
            set
            {
                if (!SetProperty(ref _selectedPodcastOutputFormat, value)) return;
                PodcastOutputFormat = value?.HeaderValue ?? "";
                OnChanged();
            }
        }

        public static List<PodcastOutputFormatOption> PodcastOutputFormatOptions { get; } = new()
        {
            new("", "MP3 24kHz (默认)"),
            new("audio-24khz-96kbitrate-mono-mp3", "MP3 24kHz 96kbps"),
            new("audio-48khz-192kbitrate-mono-mp3", "MP3 48kHz 192kbps"),
            new("audio-16khz-64kbitrate-mono-mp3", "MP3 16kHz 64kbps"),
            new("riff-24khz-16bit-mono-pcm", "WAV 24kHz PCM"),
            new("ogg-24khz-16bit-mono-opus", "OGG 24kHz Opus"),
        };

        public static List<LanguageOption> SourceLanguageOptions { get; } = new()
        {
            new("auto", "自动检测"),
            new("zh-CN", "中文（简体）"),
            new("zh-TW", "中文（繁体）"),
            new("en-US", "英语"),
            new("ja-JP", "日语"),
            new("ko-KR", "韩语"),
            new("fr-FR", "法语"),
            new("de-DE", "德语"),
            new("es-ES", "西班牙语"),
        };

        public override void LoadFrom(AzureSpeechConfig config)
        {
            SpeechModeIndex = config.AudioLabSpeechMode;
            SourceLanguage = config.AudioLabSourceLanguage ?? "auto";

            // 播客语音配置
            PodcastSpeakerAVoice = config.AudioLabPodcastSpeakerAVoice ?? "XiaochenMultilingual";
            PodcastSpeakerBVoice = config.AudioLabPodcastSpeakerBVoice ?? "Yunfeng";
            PodcastSpeakerCVoice = config.AudioLabPodcastSpeakerCVoice ?? "Xiaoshuang";
            PodcastLanguage = config.AudioLabPodcastLanguage ?? "zh-CN";
            PodcastOutputFormat = config.AudioLabPodcastOutputFormat ?? "";
            // 恢复输出格式选中项
            _selectedPodcastOutputFormat = PodcastOutputFormatOptions.FirstOrDefault(f => f.HeaderValue == PodcastOutputFormat)
                ?? PodcastOutputFormatOptions.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedPodcastOutputFormat));

            // 听析阶段预设
            var merged = AudioLabStagePresetDefaults.MergeWithDefaults(config.AudioLabStagePresets);
            _stagePresets = new ObservableCollection<AudioLabStagePreset>(
                merged
                .Where(p => !string.Equals(p.Stage, nameof(AudioLifecycleStage.PodcastAudio), StringComparison.Ordinal))
                .Select(p => new AudioLabStagePreset
                {
                    Stage = p.Stage,
                    DisplayName = p.DisplayName,
                    SystemPrompt = p.SystemPrompt,
                    ShowInTab = p.ShowInTab,
                    IncludeInBatch = p.IncludeInBatch,
                    IsEnabled = p.IsEnabled,
                    DisplayMode = p.DisplayMode,
                }));
            OnPropertyChanged(nameof(StagePresets));

            DebugMode = config.AudioLabDebugMode;
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            config.AudioLabSpeechMode = SpeechModeIndex;
            config.AudioLabAadEndpointId = SelectedAadEndpoint?.EndpointId ?? "";
            config.AudioLabSpeechEndpointId = SelectedSpeechEndpoint?.EndpointId ?? "";
            config.AudioLabTextModelRef = SelectedTextModel?.Reference;
            config.AudioLabSourceLanguage = SourceLanguage;

            // 播客语音配置
            config.AudioLabPodcastSpeakerAVoice = PodcastSpeakerAVoice;
            config.AudioLabPodcastSpeakerBVoice = PodcastSpeakerBVoice;
            config.AudioLabPodcastSpeakerCVoice = PodcastSpeakerCVoice;
            config.AudioLabPodcastLanguage = PodcastLanguage;
            config.AudioLabPodcastOutputFormat = PodcastOutputFormat;

            config.AudioLabDebugMode = DebugMode;

            // 听析阶段预设 —— 仅持久化与内置默认有差异的提示词
            var builtinDefaults = AudioLabStagePresetDefaults.CreateDefaults()
                .ToDictionary(d => d.Stage, d => d.SystemPrompt ?? "");
            config.AudioLabStagePresets = StagePresets
                .Where(p => !string.IsNullOrWhiteSpace(p.Stage))
                .Select(p =>
                {
                    var prompt = p.SystemPrompt?.Trim() ?? "";
                    // 若提示词与内置默认相同，存空串 → 下次加载时自动获取最新默认
                    if (builtinDefaults.TryGetValue(p.Stage, out var defaultPrompt) && prompt == defaultPrompt)
                        prompt = "";
                    return new AudioLabStagePreset
                    {
                        Stage = p.Stage.Trim(),
                        DisplayName = p.DisplayName?.Trim() ?? "",
                        SystemPrompt = prompt,
                        ShowInTab = p.ShowInTab,
                        IncludeInBatch = p.IncludeInBatch,
                        IsEnabled = p.IsEnabled,
                        DisplayMode = p.DisplayMode,
                    };
                }).ToList();
        }

        public void SelectModels(AzureSpeechConfig config, List<ModelOption> textModels)
        {
            TextModels = textModels;
            SelectModelOption(config.AudioLabTextModelRef, textModels,
                v => _selectedTextModel = v, nameof(SelectedTextModel));
            OnPropertyChanged(nameof(HasTextModels));
        }

        public void RefreshModels(List<ModelOption> textModels)
        {
            var textRef = SelectedTextModel?.Reference;
            TextModels = textModels;
            SelectModelOption(textRef, textModels,
                v => _selectedTextModel = v, nameof(SelectedTextModel));
            OnPropertyChanged(nameof(HasTextModels));
        }

        /// <summary>从配置的终结点列表构建 AAD / 传统语音终结点选项</summary>
        public void LoadEndpoints(AzureSpeechConfig config)
        {
            // AAD 终结点：AzureOpenAi 类型且 AuthMode=AAD
            AadEndpoints = config.Endpoints
                .Where(ep => ep.IsEnabled
                    && ep.EndpointType == EndpointApiType.AzureOpenAi
                    && ep.AuthMode == AzureAuthMode.AAD)
                .Select(ep =>
                {
                    var region = ParseRegionFromFoundryUrl(ep.BaseUrl);
                    return new EndpointOption(ep.Id, ep.Name, ep.BaseUrl, region);
                })
                .ToList();

            // 传统语音终结点：AzureSpeech 类型
            SpeechEndpoints = config.Endpoints
                .Where(ep => ep.IsEnabled && ep.IsSpeechEndpoint)
                .Select(ep =>
                {
                    var region = !string.IsNullOrWhiteSpace(ep.SpeechRegion)
                        ? ep.SpeechRegion
                        : AzureSubscription.ParseRegionFromEndpoint(ep.SpeechEndpoint ?? "");
                    return new EndpointOption(ep.Id, ep.Name, ep.SpeechEndpoint ?? ep.BaseUrl, region);
                })
                .ToList();

            // 恢复选中项
            _selectedAadEndpoint = string.IsNullOrWhiteSpace(config.AudioLabAadEndpointId)
                ? AadEndpoints.FirstOrDefault()
                : AadEndpoints.FirstOrDefault(o => o.EndpointId == config.AudioLabAadEndpointId)
                  ?? AadEndpoints.FirstOrDefault();

            _selectedSpeechEndpoint = string.IsNullOrWhiteSpace(config.AudioLabSpeechEndpointId)
                ? SpeechEndpoints.FirstOrDefault()
                : SpeechEndpoints.FirstOrDefault(o => o.EndpointId == config.AudioLabSpeechEndpointId)
                  ?? SpeechEndpoints.FirstOrDefault();

            OnPropertyChanged(nameof(HasAadEndpoints));
            OnPropertyChanged(nameof(HasSpeechEndpoints));
            OnPropertyChanged(nameof(SelectedAadEndpoint));
            OnPropertyChanged(nameof(SelectedSpeechEndpoint));
            UpdateDerivedSpeechInfo();
        }

        public void RefreshEndpoints(AzureSpeechConfig config)
        {
            var aadId = SelectedAadEndpoint?.EndpointId;
            var speechId = SelectedSpeechEndpoint?.EndpointId;
            LoadEndpoints(config);

            if (aadId != null)
                _selectedAadEndpoint = AadEndpoints.FirstOrDefault(o => o.EndpointId == aadId) ?? AadEndpoints.FirstOrDefault();
            if (speechId != null)
                _selectedSpeechEndpoint = SpeechEndpoints.FirstOrDefault(o => o.EndpointId == speechId) ?? SpeechEndpoints.FirstOrDefault();

            OnPropertyChanged(nameof(SelectedAadEndpoint));
            OnPropertyChanged(nameof(SelectedSpeechEndpoint));
            UpdateDerivedSpeechInfo();
        }

        private void UpdateDerivedSpeechInfo()
        {
            var ep = SelectedAadEndpoint;
            if (ep == null || string.IsNullOrWhiteSpace(ep.Region))
            {
                DerivedRegion = "";
                DerivedSttUrl = "";
                DerivedTtsUrl = "";
                return;
            }

            DerivedRegion = ep.Region;

            // 根据域名判断国际版/中国区
            var baseUrl = ep.Url?.ToLowerInvariant() ?? "";
            if (baseUrl.Contains(".azure.cn"))
            {
                DerivedSttUrl = $"https://{ep.Region}.stt.speech.azure.cn";
                DerivedTtsUrl = $"https://{ep.Region}.tts.speech.azure.cn";
            }
            else
            {
                DerivedSttUrl = $"https://{ep.Region}.stt.speech.microsoft.com";
                DerivedTtsUrl = $"https://{ep.Region}.tts.speech.microsoft.com";
            }
        }

        // ═══ 从 Foundry URL 解析区域 ═══

        /// <summary>
        /// 从 Azure Foundry 终结点 URL 提取区域。
        /// 支持: {name}-{region}.openai.azure.com, {name}-{region}.services.ai.azure.com,
        /// {name}-{region}.cognitiveservices.azure.com
        /// </summary>
        /// <summary>从 Foundry / Cognitive Services URL 提取资源子域名（完整），如 "myresource-eastus2"</summary>
        public static string? ParseSubdomainFromFoundryUrl(string? baseUrl)
            => ParseSubdomainCore(baseUrl);

        /// <summary>从 Foundry / Cognitive Services URL 提取区域名，如 "eastus2"</summary>
        public static string? ParseRegionFromFoundryUrl(string? baseUrl)
        {
            var subdomain = ParseSubdomainCore(baseUrl);
            if (subdomain == null) return null;
            var lastDash = subdomain.LastIndexOf('-');
            return lastDash >= 0 ? subdomain[(lastDash + 1)..] : subdomain;
        }

        /// <summary>判断 URL 是否属于 Azure 中国云</summary>
        public static bool IsAzureChinaUrl(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return false;
            return baseUrl.Contains(".azure.cn", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ParseSubdomainCore(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;

            try
            {
                var url = baseUrl.Trim();
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url;

                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();

                string? subdomain = null;
                if (host.EndsWith(".openai.azure.com"))
                    subdomain = host[..^".openai.azure.com".Length];
                else if (host.EndsWith(".services.ai.azure.com"))
                    subdomain = host[..^".services.ai.azure.com".Length];
                else if (host.EndsWith(".cognitiveservices.azure.com"))
                    subdomain = host[..^".cognitiveservices.azure.com".Length];
                else if (host.EndsWith(".openai.azure.cn"))
                    subdomain = host[..^".openai.azure.cn".Length];
                else if (host.EndsWith(".services.ai.azure.cn"))
                    subdomain = host[..^".services.ai.azure.cn".Length];
                else if (host.EndsWith(".cognitiveservices.azure.cn"))
                    subdomain = host[..^".cognitiveservices.azure.cn".Length];

                return string.IsNullOrWhiteSpace(subdomain) ? null : subdomain;
            }
            catch
            {
                return null;
            }
        }

        private void SelectModelOption(ModelReference? reference, List<ModelOption> options,
            Action<ModelOption?> setter, string propertyName)
        {
            var match = reference == null
                ? null
                : options.FirstOrDefault(o =>
                    o.Reference.EndpointId == reference.EndpointId &&
                    o.Reference.ModelId == reference.ModelId);
            setter(match);
            OnPropertyChanged(propertyName);
        }

        // ── 语音列表加载 ─────────────────────────────

        private async Task LoadVoicesAsync()
        {
            if (IsLoadingVoices || _ttsService == null) return;
            IsLoadingVoices = true;
            VoiceLoadStatus = "正在加载语音列表...";

            try
            {
                var auth = BuildTtsAuthContext();
                var voices = await _ttsService.ListVoicesAsync(auth);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _allVoices = voices.OrderBy(v => v.Locale).ThenBy(v => v.ShortName).ToList();

                    // 构建语言筛选选项
                    AvailableLanguages.Clear();
                    AvailableLanguages.Add(new VoiceLanguageOption("", "全部语言"));
                    var locales = _allVoices
                        .Select(v => (v.Locale, v.LocaleName))
                        .Distinct()
                        .OrderBy(x => x.Locale);
                    foreach (var (locale, localeName) in locales)
                    {
                        var label = string.IsNullOrWhiteSpace(localeName) ? locale : $"{localeName} ({locale})";
                        AvailableLanguages.Add(new VoiceLanguageOption(locale, label));
                    }

                    // 默认选择配置的语言
                    var preferredLang = string.IsNullOrWhiteSpace(PodcastLanguage) ? "zh-CN" : PodcastLanguage;
                    _selectedVoiceLanguage = AvailableLanguages.FirstOrDefault(l => l.Locale == preferredLang)
                                          ?? AvailableLanguages.FirstOrDefault();
                    OnPropertyChanged(nameof(SelectedVoiceLanguage));
                    ApplyVoiceLanguageFilter();

                    VoicesLoaded = true;
                    VoiceLoadStatus = $"已加载 {voices.Count} 个语音";

                    ApplyDefaultVoicePresets();
                });
            }
            catch (Exception ex)
            {
                VoiceLoadStatus = $"加载语音失败：{ex.Message}";
            }
            finally
            {
                IsLoadingVoices = false;
            }
        }

        private void ApplyVoiceLanguageFilter()
        {
            AvailableVoices.Clear();
            var locale = SelectedVoiceLanguage?.Locale;
            var filtered = string.IsNullOrWhiteSpace(locale)
                ? _allVoices
                : _allVoices.Where(v => v.Locale.Equals(locale, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var v in filtered)
                AvailableVoices.Add(v);
        }

        private void ApplyDefaultVoicePresets()
        {
            var presets = new (string Tag, string Pattern)[]
            {
                ("A", PodcastSpeakerAVoice ?? "XiaochenMultilingual"),
                ("B", PodcastSpeakerBVoice ?? "Yunfeng"),
                ("C", PodcastSpeakerCVoice ?? "Xiaoshuang"),
            };

            foreach (var (tag, pattern) in presets)
            {
                var profile = SpeakerProfiles.FirstOrDefault(p => p.Tag == tag);
                if (profile == null || profile.Voice != null) continue;

                var match = AvailableVoices.FirstOrDefault(v =>
                    v.ShortName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    ?? _allVoices.FirstOrDefault(v =>
                    v.ShortName.Contains(pattern, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                    profile.Voice = match;
            }
        }

        private void SyncSpeakerVoicesToConfig()
        {
            var profileA = SpeakerProfiles.FirstOrDefault(p => p.Tag == "A");
            var profileB = SpeakerProfiles.FirstOrDefault(p => p.Tag == "B");
            var profileC = SpeakerProfiles.FirstOrDefault(p => p.Tag == "C");

            if (profileA?.Voice != null) PodcastSpeakerAVoice = ExtractVoiceKeyword(profileA.Voice.ShortName);
            if (profileB?.Voice != null) PodcastSpeakerBVoice = ExtractVoiceKeyword(profileB.Voice.ShortName);
            if (profileC?.Voice != null) PodcastSpeakerCVoice = ExtractVoiceKeyword(profileC.Voice.ShortName);
        }

        private static string ExtractVoiceKeyword(string shortName)
        {
            var lastDash = shortName.LastIndexOf('-');
            if (lastDash >= 0 && lastDash < shortName.Length - 1)
                return shortName[(lastDash + 1)..];
            return shortName;
        }

        private SpeechSynthesisService.TtsAuthContext BuildTtsAuthContext()
        {
            var config = _configProvider?.Invoke() ?? new AzureSpeechConfig();

            if (config.AudioLabSpeechMode == 0 && _tokenProviderStore != null)
            {
                var provider = _tokenProviderStore.GetProvider("ai");
                if (provider != null && provider.IsLoggedIn)
                {
                    var tokenTask = provider.GetTokenAsync(CancellationToken.None);
                    var token = tokenTask.IsCompleted ? tokenTask.Result : tokenTask.GetAwaiter().GetResult();

                    var speechRes = FindSpeechResource(config, SpeechCapability.TextToSpeech);
                    if (speechRes != null && !string.IsNullOrWhiteSpace(speechRes.Endpoint))
                    {
                        var endpoint = speechRes.Endpoint.TrimEnd('/');
                        var isCustomDomain = endpoint.Contains(".cognitiveservices.azure.", StringComparison.OrdinalIgnoreCase);
                        var resourceId = speechRes.Id;
                        var bearerValue = $"aad#{resourceId}#{token}";

                        return new SpeechSynthesisService.TtsAuthContext
                        {
                            AadBearerValue = bearerValue,
                            BaseUrl = endpoint,
                            IsCustomDomainEndpoint = isCustomDomain,
                        };
                    }
                }
            }

            {
                var speechRes = FindSpeechResource(config, SpeechCapability.TextToSpeech);
                if (speechRes != null && !string.IsNullOrWhiteSpace(speechRes.SubscriptionKey))
                {
                    var region = speechRes.ServiceRegion;
                    return new SpeechSynthesisService.TtsAuthContext
                    {
                        SubscriptionKey = speechRes.SubscriptionKey,
                        BaseUrl = $"https://{region}.tts.speech.microsoft.com",
                        IsCustomDomainEndpoint = false,
                    };
                }
            }

            throw new InvalidOperationException("无法建立 TTS 认证。请在设置中配置语音资源（AAD 或 API Key）。");
        }

        private static SpeechResource? FindSpeechResource(AzureSpeechConfig config, SpeechCapability capability)
        {
            var exact = config.SpeechResources?
                .FirstOrDefault(r => r.IsEnabled && r.Capabilities.HasFlag(capability));
            if (exact != null) return exact;
            return config.SpeechResources?
                .FirstOrDefault(r => r.IsEnabled
                    && r.Vendor == SpeechVendorType.Microsoft
                    && !string.IsNullOrWhiteSpace(r.SubscriptionKey));
        }
    }

    /// <summary>终结点下拉选项</summary>
    public class EndpointOption
    {
        public string EndpointId { get; }
        public string Name { get; }
        public string Url { get; }
        public string? Region { get; }

        public EndpointOption(string endpointId, string name, string url, string? region)
        {
            EndpointId = endpointId;
            Name = name;
            Url = url;
            Region = region;
        }

        public override string ToString()
        {
            if (!string.IsNullOrWhiteSpace(Region))
                return $"{Name} ({Region})";
            return Name;
        }
    }

    public class LanguageOption
    {
        public string Code { get; }
        public string DisplayName { get; }

        public LanguageOption(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }

        public override string ToString() => $"{DisplayName} ({Code})";
    }

    public class PodcastOutputFormatOption
    {
        public string HeaderValue { get; }
        public string DisplayName { get; }

        public PodcastOutputFormatOption(string headerValue, string displayName)
        {
            HeaderValue = headerValue;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
    }
}
