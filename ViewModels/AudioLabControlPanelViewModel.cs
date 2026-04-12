using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Speech;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.ViewModels
{
    /// <summary>
    /// 听析中心控制面板 ViewModel — 管理音频生命周期阶段、
    /// 发言人语音配置、TTS 合成、以及自动化管道触发。
    /// </summary>
    public partial class AudioLabControlPanelViewModel : ViewModelBase
    {
        private readonly AudioLifecyclePipelineService _pipeline;
        private readonly SpeechSynthesisService _ttsService;
        private readonly Func<AzureSpeechConfig> _configProvider;
        private readonly IAzureTokenProviderStore _tokenProviderStore;
        private readonly ConfigurationService? _configService;

        private CancellationTokenSource? _cts;

        // ── 当前音频 ──────────────────────────────────

        [ObservableProperty] private string _currentAudioItemId = "";
        [ObservableProperty] private string _currentFileName = "";
        [ObservableProperty] private string _statusMessage = "就绪";
        [ObservableProperty] private bool _isBusy;

        // ── 生命周期阶段完成情况 ──────────────────────

        [ObservableProperty] private bool _hasTranscription;
        [ObservableProperty] private bool _hasSummary;
        [ObservableProperty] private bool _hasMindMap;
        [ObservableProperty] private bool _hasInsight;
        [ObservableProperty] private bool _hasPodcastScript;
        [ObservableProperty] private bool _hasPodcastAudio;
        [ObservableProperty] private bool _hasTranslation;
        [ObservableProperty] private bool _hasResearch;

        // ── 阶段可见性（跟随配置预设） ──────────────

        private bool _isSummaryStageVisible = true;
        public bool IsSummaryStageVisible { get => _isSummaryStageVisible; private set => SetProperty(ref _isSummaryStageVisible, value); }
        private bool _isMindMapStageVisible = true;
        public bool IsMindMapStageVisible { get => _isMindMapStageVisible; private set => SetProperty(ref _isMindMapStageVisible, value); }
        private bool _isInsightStageVisible = true;
        public bool IsInsightStageVisible { get => _isInsightStageVisible; private set => SetProperty(ref _isInsightStageVisible, value); }
        private bool _isResearchStageVisible = true;
        public bool IsResearchStageVisible { get => _isResearchStageVisible; private set => SetProperty(ref _isResearchStageVisible, value); }
        private bool _isPodcastStageVisible = true;
        public bool IsPodcastStageVisible { get => _isPodcastStageVisible; private set => SetProperty(ref _isPodcastStageVisible, value); }
        private bool _isTranslationStageVisible = true;
        public bool IsTranslationStageVisible { get => _isTranslationStageVisible; private set => SetProperty(ref _isTranslationStageVisible, value); }

        // ── TTS 配置 ──────────────────────────────────

        public ObservableCollection<SpeakerProfile> SpeakerProfiles { get; } = new();

        /// <summary>全部语音（未筛选）</summary>
        private List<VoiceInfo> _allVoices = new();

        /// <summary>按语言筛选后的语音列表</summary>
        public ObservableCollection<VoiceInfo> AvailableVoices { get; } = new();

        /// <summary>语言筛选选项列表</summary>
        public ObservableCollection<VoiceLanguageOption> AvailableLanguages { get; } = new();

        [ObservableProperty] private VoiceLanguageOption? _selectedLanguage;

        partial void OnSelectedLanguageChanged(VoiceLanguageOption? value)
        {
            ApplyVoiceLanguageFilter();
            SaveVoiceConfigToFile();
        }

        public ObservableCollection<SpeechSynthesisService.OutputFormatOption> AvailableOutputFormats { get; } = new();

        [ObservableProperty] private SpeechSynthesisService.OutputFormatOption? _selectedOutputFormat;
        [ObservableProperty] private bool _voicesLoaded;

        // ── 播客音频输出 ──────────────────────────────

        [ObservableProperty] private string _podcastAudioPath = "";
        [ObservableProperty] private bool _hasPodcastAudioFile;

        // ── Commands ──────────────────────────────────

        public ICommand LoadVoicesCommand { get; }
        public ICommand SynthesizePodcastCommand { get; }
        public ICommand AutoFillMissingCommand { get; }
        public ICommand CancelCommand { get; }

        public AudioLabControlPanelViewModel(
            AudioLifecyclePipelineService pipeline,
            SpeechSynthesisService ttsService,
            Func<AzureSpeechConfig> configProvider,
            IAzureTokenProviderStore tokenProviderStore,
            ConfigurationService? configService = null)
        {
            _pipeline = pipeline;
            _ttsService = ttsService;
            _configProvider = configProvider;
            _tokenProviderStore = tokenProviderStore;
            _configService = configService;

            // 初始化 3 个发言人
            SpeakerProfiles.Add(new SpeakerProfile("A", "发言人 A"));
            SpeakerProfiles.Add(new SpeakerProfile("B", "发言人 B"));
            SpeakerProfiles.Add(new SpeakerProfile("C", "发言人 C"));

            // 订阅语音变化以回写配置
            foreach (var p in SpeakerProfiles)
                p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SpeakerProfile.Voice)) SaveVoiceConfigToFile(); };

            // 预填输出格式（从配置恢复选中项）
            foreach (var fmt in SpeechSynthesisService.OutputFormats)
                AvailableOutputFormats.Add(fmt);
            var configFormat = configProvider().AudioLabPodcastOutputFormat;
            SelectedOutputFormat = (!string.IsNullOrWhiteSpace(configFormat)
                ? AvailableOutputFormats.FirstOrDefault(f => f.HeaderValue == configFormat)
                : null) ?? AvailableOutputFormats.FirstOrDefault();

            LoadVoicesCommand = new RelayCommand(_ => _ = LoadVoicesAsync(), _ => !IsBusy);
            SynthesizePodcastCommand = new RelayCommand(_ => _ = SynthesizePodcastAsync(), _ => !IsBusy && HasPodcastScript && VoicesLoaded);
            AutoFillMissingCommand = new RelayCommand(_ => _ = AutoFillMissingAsync(), _ => !IsBusy && HasTranscription);
            CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);

            RefreshStageVisibility();
        }

        /// <summary>切换到新的音频文件时调用，刷新生命周期状态。</summary>
        public void SetCurrentAudio(string audioItemId, string fileName)
        {
            CurrentAudioItemId = audioItemId;
            CurrentFileName = fileName;
            RefreshLifecycleStatus();
        }

        /// <summary>从数据库刷新各阶段完成状态。</summary>
        public void RefreshLifecycleStatus()
        {
            if (string.IsNullOrWhiteSpace(CurrentAudioItemId)) return;

            var completed = _pipeline.GetCompletedStages(CurrentAudioItemId);
            HasTranscription = completed.Contains(AudioLifecycleStage.Transcribed);
            HasSummary = completed.Contains(AudioLifecycleStage.Summarized);
            HasMindMap = completed.Contains(AudioLifecycleStage.MindMap);
            HasInsight = completed.Contains(AudioLifecycleStage.Insight);
            HasPodcastScript = completed.Contains(AudioLifecycleStage.PodcastScript);
            HasPodcastAudio = completed.Contains(AudioLifecycleStage.PodcastAudio);
            HasTranslation = completed.Contains(AudioLifecycleStage.Translated);
            HasResearch = completed.Contains(AudioLifecycleStage.Research);

            // 检查播客音频文件
            var audioPath = _pipeline.TryLoadCachedFilePath(CurrentAudioItemId, AudioLifecycleStage.PodcastAudio);
            PodcastAudioPath = audioPath ?? "";
            HasPodcastAudioFile = !string.IsNullOrWhiteSpace(audioPath);

            RaiseCommandsCanExecuteChanged();
        }

        /// <summary>根据配置预设刷新各阶段在控制面板中的可见性。</summary>
        public void RefreshStageVisibility()
        {
            var presets = _configProvider().AudioLabStagePresets;
            IsSummaryStageVisible = AudioLabStagePresetDefaults.ShouldShowTab(presets, "Summarized");
            IsMindMapStageVisible = AudioLabStagePresetDefaults.ShouldShowTab(presets, "MindMap");
            IsInsightStageVisible = AudioLabStagePresetDefaults.ShouldShowTab(presets, "Insight");
            IsResearchStageVisible = AudioLabStagePresetDefaults.ShouldShowTab(presets, "Research");
            IsPodcastStageVisible = AudioLabStagePresetDefaults.ShouldShowTab(presets, "PodcastScript");
            IsTranslationStageVisible = AudioLabStagePresetDefaults.ShouldShowTab(presets, "Translated");
        }

        // ── 加载语音列表 ──────────────────────────────

        public async Task LoadVoicesAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusMessage = "正在加载语音列表...";

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

                    // 默认选择配置的语言，否则 zh-CN，否则全部
                    var configLang = _configProvider().AudioLabPodcastLanguage;
                    var preferredLang = string.IsNullOrWhiteSpace(configLang) ? "zh-CN" : configLang;
                    SelectedLanguage = AvailableLanguages.FirstOrDefault(l => l.Locale == preferredLang)
                                    ?? AvailableLanguages.FirstOrDefault();

                    VoicesLoaded = true;
                    StatusMessage = $"已加载 {voices.Count} 个语音";

                    // 自动为发言人预设微软中文语音
                    ApplyDefaultVoicePresets();
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"加载语音失败：{ex.Message}";
            }
            finally
            {
                IsBusy = false;
                RaiseCommandsCanExecuteChanged();
            }
        }

        // ── TTS 合成播客 ──────────────────────────────

        /// <summary>
        /// 外部传入播客台本后触发合成。
        /// </summary>
        public async Task SynthesizePodcastAsync(string? podcastScript = null)
        {
            if (string.IsNullOrWhiteSpace(CurrentAudioItemId)) return;
            if (IsBusy)
            {
                StatusMessage = "控制面板正忙，无法自动合成播客音频。请稍后手动点击「合成音频」。";
                return;
            }

            // 如果没传入脚本，尝试从缓存读
            podcastScript ??= _pipeline.TryLoadCachedContent(CurrentAudioItemId, AudioLifecycleStage.PodcastScript);
            if (string.IsNullOrWhiteSpace(podcastScript))
            {
                StatusMessage = "没有可用的播客台本。请先生成播客脚本。";
                return;
            }

            var profiles = SpeakerProfiles
                .Where(p => p.Voice != null)
                .ToDictionary(p => p.Tag, p => p);

            if (profiles.Count == 0)
            {
                StatusMessage = "请先为发言人配置语音。";
                return;
            }

            IsBusy = true;
            _cts = new CancellationTokenSource();
            StatusMessage = "正在合成播客音频...";

            try
            {
                var auth = BuildTtsAuthContext();
                var outputFormat = SelectedOutputFormat?.HeaderValue ?? "audio-24khz-96kbitrate-mono-mp3";
                var outputDir = Path.Combine(PathManager.Instance.AppDataPath, "podcast-audio");

                var path = await _pipeline.SynthesizePodcastAsync(
                    auth, CurrentAudioItemId, podcastScript, profiles,
                    outputFormat, outputDir, _cts.Token);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PodcastAudioPath = path;
                    HasPodcastAudioFile = true;
                    HasPodcastAudio = true;
                    StatusMessage = $"播客音频已生成：{Path.GetFileName(path)}";
                    PodcastAudioSynthesized?.Invoke(path);
                });
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "播客合成已取消";
            }
            catch (Exception ex)
            {
                StatusMessage = $"播客合成失败：{ex.Message}";
            }
            finally
            {
                IsBusy = false;
                _cts?.Dispose();
                _cts = null;
                RaiseCommandsCanExecuteChanged();
            }
        }

        // ── 自动补齐缺失阶段 ──────────────────────────

        /// <summary>
        /// 通知控制面板：某阶段的内容已由外部生成（如 AudioLabViewModel 的转录/总结），
        /// 将其保存到生命周期数据库。
        /// </summary>
        public void NotifyStageCompleted(AudioLifecycleStage stage, string content)
        {
            if (string.IsNullOrWhiteSpace(CurrentAudioItemId)) return;
            _pipeline.SaveStageContent(CurrentAudioItemId, stage, content);
            RefreshLifecycleStatus();
        }

        /// <summary>
        /// 通知控制面板：转录数据已更新，需要使下游缓存失效。
        /// </summary>
        public void NotifyTranscriptionUpdated()
        {
            if (string.IsNullOrWhiteSpace(CurrentAudioItemId)) return;
            _pipeline.InvalidateDownstreamStages(CurrentAudioItemId, AudioLifecycleStage.Transcribed);
            RefreshLifecycleStatus();
        }

        private Task AutoFillMissingAsync()
        {
            // 自动补齐的逻辑由 AudioLabViewModel 触发各 GenerateXxxCommand
            // 这里仅标记意图，实际触发通过事件机制
            AutoFillMissingRequested?.Invoke();
            return Task.CompletedTask;
        }

        /// <summary>请求自动补齐缺失阶段 — 由 AudioLabViewModel 订阅处理。</summary>
        public event Action? AutoFillMissingRequested;

        /// <summary>播客音频合成完成 — 传递生成的文件路径。</summary>
        public event Action<string>? PodcastAudioSynthesized;

        // ── 认证上下文构建 ─────────────────────────────

        private SpeechSynthesisService.TtsAuthContext BuildTtsAuthContext()
        {
            var config = _configProvider();

            // 根据配置建立认证
            if (config.AudioLabSpeechMode == 0)
            {
                // AAD / Foundry 模式
                var provider = _tokenProviderStore.GetProvider("ai");
                if (provider != null && provider.IsLoggedIn)
                {
                    // 使用 AzureTokenProvider.GetTokenAsync 获取 Bearer Token
                    var tokenTask = provider.GetTokenAsync(CancellationToken.None);
                    var token = tokenTask.IsCompleted ? tokenTask.Result : tokenTask.GetAwaiter().GetResult();

                    // 从配置中获取 SpeechResource 来构建 endpoint 和 resourceId
                    var speechRes = FindSpeechResource(config, SpeechCapability.TextToSpeech);
                    if (speechRes != null && !string.IsNullOrWhiteSpace(speechRes.Endpoint))
                    {
                        var endpoint = speechRes.Endpoint.TrimEnd('/');
                        var isCustomDomain = endpoint.Contains(".cognitiveservices.azure.", StringComparison.OrdinalIgnoreCase);

                        // aad#{resourceId}#{accessToken} 格式
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

            // 传统 API Key 模式
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
            // 优先匹配显式声明了该能力的资源
            var exact = config.SpeechResources?
                .FirstOrDefault(r => r.IsEnabled && r.Capabilities.HasFlag(capability));
            if (exact != null) return exact;

            // Azure Speech 的 Key 同时支持 STT 和 TTS，回退到任意已启用的 Microsoft Speech 资源
            return config.SpeechResources?
                .FirstOrDefault(r => r.IsEnabled
                    && r.Vendor == SpeechVendorType.Microsoft
                    && !string.IsNullOrWhiteSpace(r.SubscriptionKey));
        }

        private void RaiseCommandsCanExecuteChanged()
        {
            ((RelayCommand)LoadVoicesCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SynthesizePodcastCommand).RaiseCanExecuteChanged();
            ((RelayCommand)AutoFillMissingCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        }

        // ── 缓存回填（加载时恢复 UI） ─────────────────

        /// <summary>
        /// 从数据库恢复指定阶段的缓存内容。
        /// </summary>
        public string? LoadCachedContent(AudioLifecycleStage stage)
        {
            if (string.IsNullOrWhiteSpace(CurrentAudioItemId)) return null;
            return _pipeline.TryLoadCachedContent(CurrentAudioItemId, stage);
        }

        private void ApplyVoiceLanguageFilter()
        {
            AvailableVoices.Clear();
            var locale = SelectedLanguage?.Locale;
            var filtered = string.IsNullOrWhiteSpace(locale)
                ? _allVoices
                : _allVoices.Where(v => v.Locale.Equals(locale, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var v in filtered)
                AvailableVoices.Add(v);
        }

        /// <summary>为发言人 A/B/C 自动预设微软中文语音（从配置文件读取）</summary>
        private void ApplyDefaultVoicePresets()
        {
            var config = _configProvider();
            var presets = new (string Tag, string Pattern)[]
            {
                ("A", config.AudioLabPodcastSpeakerAVoice ?? "XiaochenMultilingual"),
                ("B", config.AudioLabPodcastSpeakerBVoice ?? "Yunfeng"),
                ("C", config.AudioLabPodcastSpeakerCVoice ?? "Xiaoshuang"),
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

        /// <summary>将当前语音配置写回 config.json</summary>
        private void SaveVoiceConfigToFile()
        {
            if (_configService == null) return;
            var config = _configProvider();

            var profileA = SpeakerProfiles.FirstOrDefault(p => p.Tag == "A");
            var profileB = SpeakerProfiles.FirstOrDefault(p => p.Tag == "B");
            var profileC = SpeakerProfiles.FirstOrDefault(p => p.Tag == "C");

            // 将 ShortName 关键字写入配置（部分匹配规则不变）
            if (profileA?.Voice != null) config.AudioLabPodcastSpeakerAVoice = ExtractVoiceKeyword(profileA.Voice.ShortName);
            if (profileB?.Voice != null) config.AudioLabPodcastSpeakerBVoice = ExtractVoiceKeyword(profileB.Voice.ShortName);
            if (profileC?.Voice != null) config.AudioLabPodcastSpeakerCVoice = ExtractVoiceKeyword(profileC.Voice.ShortName);

            config.AudioLabPodcastLanguage = SelectedLanguage?.Locale ?? "";
            config.AudioLabPodcastOutputFormat = SelectedOutputFormat?.HeaderValue ?? "";

            _ = _configService.SaveConfigAsync(config);
        }

        /// <summary>从 ShortName 提取语音关键字（去掉 locale 前缀）</summary>
        private static string ExtractVoiceKeyword(string shortName)
        {
            // ShortName 格式: "zh-CN-XiaochenMultilingual" → "XiaochenMultilingual"
            var lastDash = shortName.LastIndexOf('-');
            if (lastDash >= 0 && lastDash < shortName.Length - 1)
                return shortName[(lastDash + 1)..];
            return shortName;
        }
    }

    /// <summary>语言筛选选项</summary>
    public sealed class VoiceLanguageOption
    {
        public string Locale { get; }
        public string DisplayName { get; }

        public VoiceLanguageOption(string locale, string displayName)
        {
            Locale = locale;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
    }
}
