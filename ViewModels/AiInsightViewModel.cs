using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    public class AiInsightViewModel : ViewModelBase
    {
        private readonly AzureTokenProvider _azureTokenProvider;
        private readonly AiInsightService _aiInsightService;
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly Func<AzureSpeechConfig> _configProvider;
        private readonly Func<ObservableCollection<TranslationItem>> _historyProvider;
        private readonly Action<string> _statusSetter;
        private readonly Func<Task> _showConfigAction;

        private string _insightMarkdown = "";
        private string _insightUserInput = "";
        private bool _isInsightLoading;
        private CancellationTokenSource? _insightCts;

        private bool _isAutoInsightEnabled;
        private int _autoInsightIntervalSeconds = 30;
        private int _autoInsightModeIndex; // 0=定时, 1=新数据触发
        private string _autoInsightPrompt = "请对以上翻译记录进行会议摘要。总结会议的主要议题、关键讨论内容和结论。";
        private DispatcherTimer? _autoInsightTimer;
        private int _lastAutoInsightHistoryCount;

        public AiInsightViewModel(
            AiInsightService aiInsightService,
            AzureTokenProvider azureTokenProvider,
            IModelRuntimeResolver modelRuntimeResolver,
            Func<AzureSpeechConfig> configProvider,
            Func<ObservableCollection<TranslationItem>> historyProvider,
            Action<string> statusSetter,
            Func<Task> showConfigAction)
        {
            _aiInsightService = aiInsightService;
            _azureTokenProvider = azureTokenProvider;
            _modelRuntimeResolver = modelRuntimeResolver;
            _configProvider = configProvider;
            _historyProvider = historyProvider;
            _statusSetter = statusSetter;
            _showConfigAction = showConfigAction;

            SendInsightCommand = new RelayCommand(
                execute: _ => SendInsight(InsightUserInput),
                canExecute: _ => !IsInsightLoading && IsAiConfigured
                                 && !string.IsNullOrWhiteSpace(InsightUserInput)
            );

            StopInsightCommand = new RelayCommand(
                execute: _ => StopInsight(),
                canExecute: _ => IsInsightLoading
            );

            ClearInsightCommand = new RelayCommand(
                execute: _ => { InsightMarkdown = ""; InsightUserInput = ""; },
                canExecute: _ => true
            );

            ShowAiConfigCommand = new RelayCommand(
                execute: async _ => await ShowAiConfig(),
                canExecute: _ => true
            );

            SendPresetInsightCommand = new RelayCommand(
                execute: param => SendInsight(param?.ToString() ?? ""),
                canExecute: _ => !IsInsightLoading && IsAiConfigured
            );

            ToggleAutoInsightCommand = new RelayCommand(
                execute: _ => ToggleAutoInsight(),
                canExecute: _ => IsAiConfigured
            );
        }

        public ICommand SendInsightCommand { get; }
        public ICommand StopInsightCommand { get; }
        public ICommand ClearInsightCommand { get; }
        public ICommand ShowAiConfigCommand { get; }
        public ICommand SendPresetInsightCommand { get; }
        public ICommand ToggleAutoInsightCommand { get; }

        public string InsightMarkdown
        {
            get => _insightMarkdown;
            set
            {
                if (SetProperty(ref _insightMarkdown, value))
                {
                    OnPropertyChanged(nameof(IsInsightEmpty));
                }
            }
        }

        public string InsightUserInput
        {
            get => _insightUserInput;
            set
            {
                if (SetProperty(ref _insightUserInput, value))
                {
                    ((RelayCommand)SendInsightCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsInsightLoading
        {
            get => _isInsightLoading;
            set
            {
                if (SetProperty(ref _isInsightLoading, value))
                {
                    OnPropertyChanged(nameof(IsInsightEmpty));
                    ((RelayCommand)SendInsightCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)StopInsightCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)SendPresetInsightCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsAiConfigured => TryBuildInsightRuntimeConfig(_configProvider(), out _, out _, out _);

        public List<InsightPresetButton> InsightPresetButtons =>
            _configProvider().AiConfig?.PresetButtons ?? new List<InsightPresetButton>();

        public bool IsInsightEmpty => string.IsNullOrEmpty(InsightMarkdown) && !IsInsightLoading;

        public bool IsAutoInsightEnabled
        {
            get => _isAutoInsightEnabled;
            private set
            {
                if (SetProperty(ref _isAutoInsightEnabled, value))
                {
                    OnPropertyChanged(nameof(AutoInsightToggleText));
                    ((RelayCommand)ToggleAutoInsightCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public string AutoInsightToggleText => IsAutoInsightEnabled ? "停止自动洞察" : "启动自动洞察";

        public int AutoInsightIntervalSeconds
        {
            get => _autoInsightIntervalSeconds;
            set => SetProperty(ref _autoInsightIntervalSeconds, Math.Max(10, value));
        }

        public int AutoInsightModeIndex
        {
            get => _autoInsightModeIndex;
            set => SetProperty(ref _autoInsightModeIndex, value);
        }

        public string AutoInsightPrompt
        {
            get => _autoInsightPrompt;
            set => SetProperty(ref _autoInsightPrompt, value);
        }

        public async Task TrySilentLoginForAiAsync()
        {
            try
            {
                var config = _configProvider();
                if (!TryBuildInsightRuntimeConfig(config, out var runtimeRequest, out var endpoint, out _))
                    return;

                if (!runtimeRequest.IsAzureEndpoint
                    || runtimeRequest.AzureAuthMode != AzureAuthMode.AAD)
                    return;

                if (endpoint != null)
                {
                    var endpointProvider = new AzureTokenProvider(GetEndpointProfileKey(endpoint));
                    await endpointProvider.TrySilentLoginAsync(runtimeRequest.AzureTenantId, runtimeRequest.AzureClientId);
                }
                else
                {
                    await _azureTokenProvider.TrySilentLoginAsync(runtimeRequest.AzureTenantId, runtimeRequest.AzureClientId);
                }
            }
            catch
            {
                // 静默失败不影响功能，后续用户可手动登录
            }
        }

        public void UpdateConfig()
        {
            OnPropertyChanged(nameof(IsAiConfigured));
            OnPropertyChanged(nameof(InsightPresetButtons));
            ((RelayCommand)SendInsightCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SendPresetInsightCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleAutoInsightCommand).RaiseCanExecuteChanged();
        }

        public void OnNewDataAutoInsight()
        {
            if (!IsAutoInsightEnabled || AutoInsightModeIndex != 1)
            {
                return;
            }

            if (IsInsightLoading)
            {
                return;
            }

            var history = _historyProvider();
            if (history.Count <= _lastAutoInsightHistoryCount)
            {
                return;
            }

            _lastAutoInsightHistoryCount = history.Count;
            var bufferOutput = _configProvider().AiConfig?.AutoInsightBufferOutput == true;
            SendInsight(AutoInsightPrompt, bufferOutput);
        }

        private async void SendInsight(string userQuestion, bool bufferOutput = false)
        {
            var config = _configProvider();
            if (!TryBuildInsightRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
            {
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    _statusSetter(errorMessage);
                }
                return;
            }

            AzureTokenProvider? tokenProvider = null;
            if (runtimeRequest.AzureAuthMode == AzureAuthMode.AAD)
            {
                tokenProvider = endpoint != null
                    ? new AzureTokenProvider(GetEndpointProfileKey(endpoint))
                    : _azureTokenProvider;

                await tokenProvider.TrySilentLoginAsync(runtimeRequest.AzureTenantId, runtimeRequest.AzureClientId);
            }

            var runtimeService = new AiInsightService(tokenProvider);

            _insightCts?.Cancel();
            _insightCts = new CancellationTokenSource();
            var token = _insightCts.Token;

            IsInsightLoading = true;
            if (!bufferOutput || string.IsNullOrWhiteSpace(InsightMarkdown))
            {
                InsightMarkdown = "";
            }

            var aiConfig = config.AiConfig;
            var systemPrompt = aiConfig?.InsightSystemPrompt;
            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                systemPrompt = new AiConfig().InsightSystemPrompt;
            }
            var historyText = FormatHistoryForAi();
            var userTemplate = aiConfig?.InsightUserContentTemplate;
            if (string.IsNullOrWhiteSpace(userTemplate))
            {
                userTemplate = new AiConfig().InsightUserContentTemplate;
            }
            var fullUserContent = userTemplate
                .Replace("{history}", historyText)
                .Replace("{question}", userQuestion ?? string.Empty);

            var sb = new System.Text.StringBuilder();
            try
            {
                await runtimeService.StreamChatAsync(
                    runtimeRequest,
                    systemPrompt,
                    fullUserContent,
                    chunk =>
                    {
                        if (bufferOutput)
                        {
                            sb.Append(chunk);
                            return;
                        }

                        Dispatcher.UIThread.Post(() =>
                        {
                            sb.Append(chunk);
                            InsightMarkdown = sb.ToString();
                        });
                    },
                    token,
                    AiChatProfile.Quick,
                    enableReasoning: false);

                if (bufferOutput)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        InsightMarkdown = sb.ToString();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // user stopped
            }
            catch (HttpRequestException ex)
            {
                var message = $"\n\n---\n**错误**: {ex.Message}";
                Dispatcher.UIThread.Post(() =>
                {
                    InsightMarkdown = bufferOutput
                        ? sb.ToString() + message
                        : InsightMarkdown + message;
                });
            }
            catch (Exception ex)
            {
                var message = $"\n\n---\n**错误**: {ex.Message}";
                Dispatcher.UIThread.Post(() =>
                {
                    InsightMarkdown = bufferOutput
                        ? sb.ToString() + message
                        : InsightMarkdown + message;
                });
            }
            finally
            {
                IsInsightLoading = false;
            }
        }

        private static string GetEndpointProfileKey(AiEndpoint endpoint) => $"endpoint_{endpoint.Id}";

        private static ModelReference? SelectInsightReference(AiConfig ai)
            => ai.QuickModelRef ?? ai.InsightModelRef ?? ai.SummaryModelRef ?? ai.ReviewModelRef;

        private bool TryBuildInsightRuntimeConfig(
            AzureSpeechConfig config,
            out AiChatRequestConfig runtimeRequest,
            out AiEndpoint? endpoint,
            out string errorMessage)
        {
            runtimeRequest = new AiChatRequestConfig();
            endpoint = null;
            errorMessage = "";

            var ai = config.AiConfig;
            if (ai == null)
            {
                errorMessage = "AI 配置不存在，请先在设置中选择洞察模型。";
                return false;
            }

            var reference = SelectInsightReference(ai);
            if (_modelRuntimeResolver.TryResolve(config, reference, ModelCapability.Text, out var runtime, out var resolveError)
                && runtime != null)
            {
                endpoint = runtime.Endpoint;
                runtimeRequest = runtime.CreateChatRequest(ai.SummaryEnableReasoning);
                return true;
            }

            errorMessage = resolveError;
            return false;
        }

        private void StopInsight()
        {
            _insightCts?.Cancel();
        }

        private string FormatHistoryForAi()
        {
            var history = _historyProvider();
            if (history.Count == 0)
            {
                return "(暂无翻译记录)";
            }

            var sb = new System.Text.StringBuilder();
            var ordered = history.Reverse().ToList();
            foreach (var item in ordered)
            {
                sb.AppendLine($"[{item.Timestamp:HH:mm:ss}]");
                sb.AppendLine($"  原文: {item.OriginalText}");
                sb.AppendLine($"  译文: {item.TranslatedText}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private async Task ShowAiConfig()
        {
            await _showConfigAction();
        }

        private void ToggleAutoInsight()
        {
            if (IsAutoInsightEnabled)
            {
                StopAutoInsight();
                return;
            }

            if (string.IsNullOrWhiteSpace(AutoInsightPrompt))
            {
                return;
            }

            var history = _historyProvider();
            IsAutoInsightEnabled = true;
            _lastAutoInsightHistoryCount = history.Count;

            if (AutoInsightModeIndex == 0)
            {
                _autoInsightTimer = new DispatcherTimer(
                    TimeSpan.FromSeconds(AutoInsightIntervalSeconds),
                    DispatcherPriority.Background,
                    (_, _) => AutoInsightTick());
                _autoInsightTimer.Start();
                _statusSetter($"自动洞察已启动，每 {AutoInsightIntervalSeconds} 秒分析一次");
            }
            else
            {
                _statusSetter("自动洞察已启动，每收到新翻译数据时自动分析");
            }
        }

        private void StopAutoInsight()
        {
            IsAutoInsightEnabled = false;
            _autoInsightTimer?.Stop();
            _autoInsightTimer = null;
            _statusSetter("自动洞察已停止");
        }

        private void AutoInsightTick()
        {
            if (!IsAutoInsightEnabled || IsInsightLoading)
            {
                return;
            }

            var history = _historyProvider();
            if (history.Count == 0)
            {
                return;
            }

            var bufferOutput = _configProvider().AiConfig?.AutoInsightBufferOutput == true;
            SendInsight(AutoInsightPrompt, bufferOutput);
        }

        public void Dispose()
        {
            _insightCts?.Cancel();
            _insightCts?.Dispose();
            _autoInsightTimer?.Stop();
        }
    }
}
