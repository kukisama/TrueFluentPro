using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels.Settings
{
    public class InsightSectionVM : SettingsSectionBase
    {
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private string _insightSystemPrompt = "";
        private string _insightUserContentTemplate = "";
        private bool _autoInsightBufferOutput = true;
        private bool _summaryEnableReasoning;
        private ObservableCollection<InsightPresetButton> _presetButtons = new();

        private ModelOption? _selectedInsightModel;
        private ModelOption? _selectedSummaryModel;
        private ModelOption? _selectedQuickModel;

        private List<ModelOption> _textModels = new();

        private string _aiTestStatus = "";
        private string _aiTestReasoning = "";
        private string _aiAuthStatus = "认证状态：未检测";

        public InsightSectionVM(IModelRuntimeResolver modelRuntimeResolver)
        {
            _modelRuntimeResolver = modelRuntimeResolver;
            TestEndpointCommand = new RelayCommand(async _ => await TestAiConnection());
        }

        public string InsightSystemPrompt { get => _insightSystemPrompt; set => Set(ref _insightSystemPrompt, value); }
        public string InsightUserContentTemplate { get => _insightUserContentTemplate; set => Set(ref _insightUserContentTemplate, value); }
        public bool AutoInsightBufferOutput { get => _autoInsightBufferOutput; set => Set(ref _autoInsightBufferOutput, value); }
        public bool SummaryEnableReasoning { get => _summaryEnableReasoning; set => Set(ref _summaryEnableReasoning, value); }
        public ObservableCollection<InsightPresetButton> PresetButtons { get => _presetButtons; set => SetProperty(ref _presetButtons, value); }

        public List<ModelOption> TextModels { get => _textModels; set => SetProperty(ref _textModels, value); }
        public ModelOption? SelectedInsightModel { get => _selectedInsightModel;
            set => Set(ref _selectedInsightModel, value, then: () => _ = RefreshAiAuthStatusAsync()); }
        public ModelOption? SelectedSummaryModel { get => _selectedSummaryModel;
            set => Set(ref _selectedSummaryModel, value, then: () => _ = RefreshAiAuthStatusAsync()); }
        public ModelOption? SelectedQuickModel { get => _selectedQuickModel;
            set => Set(ref _selectedQuickModel, value, then: () => _ = RefreshAiAuthStatusAsync()); }

        public string AiTestStatus { get => _aiTestStatus; set => SetProperty(ref _aiTestStatus, value); }
        public string AiTestReasoning { get => _aiTestReasoning; set => SetProperty(ref _aiTestReasoning, value); }
        public string AiAuthStatus { get => _aiAuthStatus; set => SetProperty(ref _aiAuthStatus, value); }

        public ICommand TestEndpointCommand { get; }

        /// <summary>内部访问配置，由宿主注入</summary>
        internal AzureSpeechConfig Config { get; set; } = new();

        public void NotifyPresetButtonsChanged()
        {
            OnPropertyChanged(nameof(PresetButtons));
            OnChanged();
        }

        public override void LoadFrom(AzureSpeechConfig config)
        {
            Config = config;
            var ai = config.AiConfig ?? new AiConfig();
            SummaryEnableReasoning = ai.SummaryEnableReasoning;
            InsightSystemPrompt = ai.InsightSystemPrompt;
            InsightUserContentTemplate = ai.InsightUserContentTemplate;
            AutoInsightBufferOutput = ai.AutoInsightBufferOutput;
            _presetButtons = new ObservableCollection<InsightPresetButton>(ai.PresetButtons);
            OnPropertyChanged(nameof(PresetButtons));
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            var ai = config.AiConfig ?? new AiConfig();
            ai.SummaryEnableReasoning = SummaryEnableReasoning;
            ai.InsightSystemPrompt = InsightSystemPrompt?.Trim() ?? "";
            ai.InsightUserContentTemplate = InsightUserContentTemplate?.Trim() ?? "";
            ai.AutoInsightBufferOutput = AutoInsightBufferOutput;
            ai.PresetButtons = PresetButtons.Where(b => !string.IsNullOrWhiteSpace(b.Name)).ToList();

            ai.InsightModelRef = SelectedInsightModel?.Reference;
            ai.SummaryModelRef = SelectedSummaryModel?.Reference;
            ai.QuickModelRef = SelectedQuickModel?.Reference;
            config.AiConfig = ai;
        }

        public void SelectModels(AiConfig ai, List<ModelOption> textModels)
        {
            TextModels = textModels;
            SelectModelOption(ai.InsightModelRef, textModels, v => _selectedInsightModel = v, nameof(SelectedInsightModel));
            SelectModelOption(ai.SummaryModelRef, textModels, v => _selectedSummaryModel = v, nameof(SelectedSummaryModel));
            SelectModelOption(ai.QuickModelRef, textModels, v => _selectedQuickModel = v, nameof(SelectedQuickModel));
        }

        public void RefreshModels(List<ModelOption> textModels)
        {
            var insightRef = SelectedInsightModel?.Reference;
            var summaryRef = SelectedSummaryModel?.Reference;
            var quickRef = SelectedQuickModel?.Reference;

            TextModels = textModels;
            SelectModelOption(insightRef, textModels, v => _selectedInsightModel = v, nameof(SelectedInsightModel));
            SelectModelOption(summaryRef, textModels, v => _selectedSummaryModel = v, nameof(SelectedSummaryModel));
            SelectModelOption(quickRef, textModels, v => _selectedQuickModel = v, nameof(SelectedQuickModel));
        }

        private void SelectModelOption(ModelReference? reference, List<ModelOption> options, Action<ModelOption?> setter, string propertyName)
        {
            var match = reference == null ? null
                : options.FirstOrDefault(o => o.Reference.EndpointId == reference.EndpointId && o.Reference.ModelId == reference.ModelId);
            setter(match);
            OnPropertyChanged(propertyName);
        }

        private static string GetEndpointProfileKey(AiEndpoint endpoint) => $"endpoint_{endpoint.Id}";

        public async Task RefreshAiAuthStatusAsync()
        {
            try
            {
                var selected = SelectedSummaryModel ?? SelectedInsightModel ?? SelectedQuickModel;
                var reference = selected?.Reference;
                if (reference == null)
                {
                    AiAuthStatus = "认证状态：未选择模型";
                    return;
                }

                if (!_modelRuntimeResolver.TryResolve(Config, reference, ModelCapability.Text, out var runtime, out var errorMessage)
                    || runtime == null)
                {
                    AiAuthStatus = $"认证状态：{errorMessage}";
                    return;
                }

                var endpoint = runtime.Endpoint;

                if (endpoint.ProviderType != AiProviderType.AzureOpenAi)
                {
                    AiAuthStatus = string.IsNullOrWhiteSpace(endpoint.ApiKey)
                        ? "认证状态：OpenAI 兼容 API Key 未配置"
                        : "认证状态：OpenAI 兼容 API Key 已配置";
                    return;
                }

                if (endpoint.AuthMode != AzureAuthMode.AAD)
                {
                    AiAuthStatus = string.IsNullOrWhiteSpace(endpoint.ApiKey)
                        ? "认证状态：API Key 未配置"
                        : "认证状态：API Key 已配置";
                    return;
                }

                var provider = new AzureTokenProvider(GetEndpointProfileKey(endpoint));
                var loggedIn = await provider.TrySilentLoginAsync(endpoint.AzureTenantId, endpoint.AzureClientId);
                AiAuthStatus = loggedIn
                    ? $"认证状态：AAD 已登录（{(string.IsNullOrWhiteSpace(provider.Username) ? "已认证" : provider.Username)}）"
                    : "认证状态：AAD 未登录（请先在 AI 终结点中点击\u201C登录\u201D）";
            }
            catch (Exception ex)
            {
                AiAuthStatus = $"认证状态检测失败：{ex.Message}";
            }
        }

        private async Task TestAiConnection()
        {
            var testInfo = await BuildAiTestContextAsync();
            if (testInfo == null)
            {
                AiTestStatus = "请先在\u201C洞察模型选择\u201D中选择一个可用模型";
                return;
            }

            var (testRequest, endpoint, tokenProvider) = testInfo.Value;

            if (!testRequest.IsValid)
            {
                AiTestStatus = "请填写必要的配置信息";
                return;
            }

            AiTestStatus = "正在连接...";
            AiTestReasoning = "";

            try
            {
                var service = new AiInsightService(tokenProvider);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var received = false;
                var reasoningBuilder = new System.Text.StringBuilder();
                var reasoningReceived = false;

                await service.StreamChatAsync(
                    testRequest,
                    "You are a helpful assistant.",
                    "Provide one short answer and think step-by-step.",
                    chunk => { received = true; },
                    cts.Token,
                    AiChatProfile.Summary,
                    enableReasoning: testRequest.SummaryEnableReasoning,
                    onOutcome: null,
                    onReasoningChunk: chunk =>
                    {
                        reasoningReceived = true;
                        reasoningBuilder.Append(chunk);
                    });

                if (reasoningReceived)
                    AiTestReasoning = reasoningBuilder.ToString();
                else if (testRequest.SummaryEnableReasoning)
                    AiTestReasoning = "未收到思考内容。";

                AiTestStatus = received ? "连接成功！AI 服务可用。" : "连接成功但未收到响应，请检查模型配置。";
            }
            catch (OperationCanceledException)
            {
                AiTestStatus = "连接超时，请检查 API 端点是否正确。";
            }
            catch (Exception ex)
            {
                if (endpoint.ProviderType == AiProviderType.AzureOpenAi
                    && endpoint.AuthMode == AzureAuthMode.AAD
                    && ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase))
                {
                    AiTestStatus = $"AAD 鉴权失败(401)：请确认当前账号已授予 Azure OpenAI 访问权限（如 Cognitive Services OpenAI User），并检查终结点/部署名/租户是否匹配。原始错误: {ex.Message}";
                }
                else
                {
                    AiTestStatus = $"连接失败: {ex.Message}";
                }
            }
        }

        private async Task<(AiChatRequestConfig request, AiEndpoint endpoint, AzureTokenProvider? tokenProvider)?> BuildAiTestContextAsync()
        {
            var selected = SelectedSummaryModel ?? SelectedInsightModel ?? SelectedQuickModel;
            var reference = selected?.Reference;
            if (reference == null)
                return null;

            if (!_modelRuntimeResolver.TryResolve(Config, reference, ModelCapability.Text, out var runtime, out var errorMessage)
                || runtime == null)
            {
                AiTestStatus = errorMessage;
                return null;
            }

            var endpoint = runtime.Endpoint;

            AzureTokenProvider? tokenProvider = null;
            var request = runtime.CreateChatRequest(SummaryEnableReasoning);

            if (endpoint.AuthMode == AzureAuthMode.AAD)
            {
                tokenProvider = new AzureTokenProvider(runtime.ProfileKey);
                var silentLoggedIn = await tokenProvider.TrySilentLoginAsync(
                    runtime.AzureTenantId,
                    runtime.AzureClientId);

                if (!silentLoggedIn)
                {
                    AiAuthStatus = "认证状态：AAD 未登录（请先在 AI 终结点中点击\u201C登录\u201D）";
                    return null;
                }

                var username = string.IsNullOrWhiteSpace(tokenProvider.Username) ? "已认证" : tokenProvider.Username;
                AiAuthStatus = $"认证状态：AAD 已登录（{username}）";
            }
            else if (endpoint.IsAzureEndpoint)
            {
                AiAuthStatus = string.IsNullOrWhiteSpace(endpoint.ApiKey)
                    ? "认证状态：API Key 未配置"
                    : "认证状态：API Key 已配置";
            }
            else
            {
                AiAuthStatus = string.IsNullOrWhiteSpace(endpoint.ApiKey)
                    ? "认证状态：OpenAI 兼容 API Key 未配置"
                    : "认证状态：OpenAI 兼容 API Key 已配置";
            }

            return (request, endpoint, tokenProvider);
        }
    }
}
