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
                var auth = await BuildTtsAuthContextAsync();
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
                var auth = await BuildTtsAuthContextAsync();
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

        private async Task<SpeechSynthesisService.TtsAuthContext> BuildTtsAuthContextAsync()
        {
            var config = _configProvider();

            // 根据配置建立认证
            if (config.AudioLabSpeechMode == 0)
            {
                // AAD / Foundry 模式
                var endpoint = config.Endpoints.FirstOrDefault(e =>
                    string.Equals(e.Id, config.AudioLabAadEndpointId, StringComparison.OrdinalIgnoreCase));
                var resolveError = string.Empty;
                if (endpoint != null && FoundrySpeechEndpointResolver.TryResolve(endpoint, out var derived, out resolveError) && derived != null)
                {
                    var provider = await _tokenProviderStore.GetAuthenticatedProviderAsync(
                        FoundrySpeechEndpointResolver.BuildEndpointProfileKey(endpoint.Id),
                        endpoint.AzureTenantId,
                        endpoint.AzureClientId).ConfigureAwait(false);
                    if (provider != null)
                    {
                        var token = await provider.GetTokenAsync(CancellationToken.None).ConfigureAwait(false);
                        AudioLabRouteAuditLog.Info(
                            $"TTS.Auth route='FoundryAadCustomDomain' endpointName='{AudioLabRouteAuditLog.Safe(endpoint.Name)}' sourceUrl='{AudioLabRouteAuditLog.Safe(endpoint.BaseUrl)}' baseUrl='{AudioLabRouteAuditLog.Safe(derived.ResourceEndpoint)}' auth='AAD'");

                        return new SpeechSynthesisService.TtsAuthContext
                        {
                            AadBearerValue = token,
                            BaseUrl = derived.ResourceEndpoint,
                            IsCustomDomainEndpoint = true,
                        };
                    }
                }

                throw new InvalidOperationException(string.IsNullOrWhiteSpace(resolveError)
                    ? "无法从当前 AAD Foundry 终结点推导 TTS 地址。请检查听析中心选择的 AAD 终结点。"
                    : resolveError);
            }

            // 传统 API Key 模式
            {
                var speechRes = ResolveSelectedSpeechResource(config);
                if (speechRes != null && !string.IsNullOrWhiteSpace(speechRes.SubscriptionKey))
                {
                    var region = !string.IsNullOrWhiteSpace(speechRes.ServiceRegion)
                        ? speechRes.ServiceRegion
                        : AzureSubscription.ParseRegionFromEndpoint(speechRes.Endpoint) ?? "";
                    if (string.IsNullOrWhiteSpace(region))
                    {
                        throw new InvalidOperationException($"传统 Speech TTS 配置错误：已选择“{speechRes.Name}”，但无法解析区域。请检查该 Speech 终结点。不会回退到其他资源。");
                    }

                    AudioLabRouteAuditLog.Info(
                        $"TTS.Auth route='SpeechKeySelectedEndpoint' endpointName='{AudioLabRouteAuditLog.Safe(speechRes.Name)}' endpoint='{AudioLabRouteAuditLog.Safe(speechRes.Endpoint)}' baseUrl='https://{AudioLabRouteAuditLog.Safe(region)}.tts.speech.microsoft.com' auth='Key'");

                    return new SpeechSynthesisService.TtsAuthContext
                    {
                        SubscriptionKey = speechRes.SubscriptionKey,
                        BaseUrl = $"https://{region}.tts.speech.microsoft.com",
                        IsCustomDomainEndpoint = false,
                    };
                }
            }

            throw new InvalidOperationException("传统 Speech TTS 配置错误：听析中心未选择可用的 Speech 终结点，或所选终结点缺少 Key。不会回退到其他资源。");
        }

        private static SpeechResource? ResolveSelectedSpeechResource(AzureSpeechConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.AudioLabSpeechEndpointId))
                return null;

            return config.GetEffectiveSpeechResources()
                .FirstOrDefault(r => r.IsEnabled
                    && r.ConnectorType == SpeechConnectorType.MicrosoftSpeech
                    && r.AuthMode == AzureAuthMode.ApiKey
                    && string.Equals(r.Id, config.AudioLabSpeechEndpointId, StringComparison.OrdinalIgnoreCase));
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
