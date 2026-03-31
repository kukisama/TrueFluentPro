using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.EndpointTesting;

namespace TrueFluentPro.ViewModels.Settings
{
    public class EndpointsSectionVM : SettingsSectionBase
    {
        private readonly IAiEndpointModelDiscoveryService _modelDiscoveryService;
        private readonly IEndpointTemplateService _endpointTemplateService;
        private readonly IEndpointBatchTestService _endpointBatchTestService;
        private readonly AzureSubscriptionValidator _speechValidator;
        private ObservableCollection<AiEndpoint> _endpoints = new();
        private int _pendingRemovalCount;
        private ObservableCollection<string> _discoveredModelIds = new();
        private AiEndpoint? _selectedEndpoint;
        private string _endpointDiscoveryStatus = "";
        private bool _isDiscoveringModels;
        private string _speechTestResult = "";

        public EndpointsSectionVM(
            IAiEndpointModelDiscoveryService modelDiscoveryService,
            IEndpointTemplateService endpointTemplateService,
            IEndpointBatchTestService endpointBatchTestService,
            AzureSubscriptionValidator speechValidator)
        {
            _modelDiscoveryService = modelDiscoveryService;
            _endpointTemplateService = endpointTemplateService;
            _endpointBatchTestService = endpointBatchTestService;
            _speechValidator = speechValidator;
            AddEndpointCommand = new RelayCommand(_ => AddEndpoint());
            RemoveEndpointCommand = new RelayCommand(_ => RemoveEndpoint(), _ => SelectedEndpoint != null);
            DiscoverModelsCommand = new RelayCommand(async _ => await DiscoverModelsAsync(), _ => SelectedEndpoint != null && !IsDiscoveringModels);
            TestSpeechCommand = new RelayCommand(async _ => await TestSpeechEndpointAsync(), _ => SelectedEndpoint?.IsSpeechEndpoint == true);
        }

        public ObservableCollection<AiEndpoint> Endpoints { get => _endpoints; set => SetProperty(ref _endpoints, value); }
        public ObservableCollection<string> DiscoveredModelIds { get => _discoveredModelIds; set => SetProperty(ref _discoveredModelIds, value); }
        public IReadOnlyList<EndpointTemplateDefinition> EndpointTypeOptions => _endpointTemplateService.GetTemplates();

        public AiEndpoint? SelectedEndpoint
        {
            get => _selectedEndpoint;
            set
            {
                if (SetProperty(ref _selectedEndpoint, value))
                {
                    ((RelayCommand)RemoveEndpointCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)DiscoverModelsCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)TestSpeechCommand).RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(SelectedEndpointModels));
                    OnPropertyChanged(nameof(SelectedEndpointAuthMode));
                    OnPropertyChanged(nameof(SelectedEndpointApiKeyHeaderMode));
                    OnPropertyChanged(nameof(SelectedEndpointTextApiProtocolMode));
                    OnPropertyChanged(nameof(SelectedEndpointImageApiRouteMode));
                    OnPropertyChanged(nameof(SelectedEndpointApiVersionWatermark));
                    OnPropertyChanged(nameof(SelectedEndpointApiVersionNote));
                    OnPropertyChanged(nameof(SelectedEndpointTypeSummary));
                    OnPropertyChanged(nameof(CanSelectedEndpointUseAad));
                    OnPropertyChanged(nameof(IsSelectedEndpointAad));
                    OnPropertyChanged(nameof(HasSelectedEndpoint));
                    OnPropertyChanged(nameof(ShowEmptyState));
                    OnPropertyChanged(nameof(IsSelectedEndpointAzure));
                    OnPropertyChanged(nameof(IsSelectedEndpointSpeech));
                    SpeechTestResult = "";
                    ClearDiscoveredModels();
                }
            }
        }

        public List<AiModelEntry>? SelectedEndpointModels =>
            SelectedEndpoint?.Models?.ToList();

        public bool HasEndpoints => Endpoints.Count > 0;
        public bool HasSelectedEndpoint => SelectedEndpoint != null;
        public bool ShowEmptyState => !HasSelectedEndpoint;
        public bool HasDiscoveredModels => DiscoveredModelIds.Count > 0;
        public bool IsSelectedEndpointAzure => SelectedEndpoint?.IsAzureEndpoint == true;
        public bool IsSelectedEndpointSpeech => SelectedEndpoint?.IsSpeechEndpoint == true;
        public string SpeechTestResult { get => _speechTestResult; set => SetProperty(ref _speechTestResult, value); }
        public bool CanSelectedEndpointUseAad => SelectedEndpoint != null
            && _endpointTemplateService.GetTemplate(SelectedEndpoint).SupportsAad;
        public string SelectedEndpointTypeSummary => SelectedEndpoint == null
            ? ""
            : _endpointTemplateService.BuildBehaviorSummary(SelectedEndpoint);
        public string SelectedEndpointApiVersionWatermark => SelectedEndpoint == null
            ? string.Empty
            : _endpointTemplateService.GetTemplate(SelectedEndpoint).DefaultApiVersion;
        public string SelectedEndpointApiVersionNote => SelectedEndpoint switch
        {
            null => string.Empty,
            { EndpointType: EndpointApiType.AzureOpenAi } => "说明：此字段主要影响 AOAI 文本 deployments 路线；图片 / 视频是否带版本取决于资料包候选，视频候选当前可能包含无版本与 preview。",
            { EndpointType: EndpointApiType.ApiManagementGateway } => "说明：APIM 是否带 api-version 取决于资料包候选；文本、图片、视频三类路线可能各不相同。",
            _ => string.IsNullOrWhiteSpace(_endpointTemplateService.GetTemplate(SelectedEndpoint).DefaultApiVersion)
                ? "说明：当前模板默认不要求 api-version；留空时业务侧通常会按不传版本处理。"
                : "说明：界面可留空，业务会优先按模板推荐值解析。"
        };
        public string EndpointDiscoveryStatus { get => _endpointDiscoveryStatus; set => SetProperty(ref _endpointDiscoveryStatus, value); }
        public bool IsDiscoveringModels { get => _isDiscoveringModels; set => SetProperty(ref _isDiscoveringModels, value); }

        public int SelectedEndpointAuthMode
        {
            get => SelectedEndpoint?.AuthMode == AzureAuthMode.AAD ? 1 : 0;
            set
            {
                if (SelectedEndpoint == null) return;
                if (!CanSelectedEndpointUseAad && value == 1)
                {
                    value = 0;
                }

                SelectedEndpoint.AuthMode = value == 1 ? AzureAuthMode.AAD : AzureAuthMode.ApiKey;
                OnPropertyChanged(nameof(SelectedEndpointAuthMode));
                OnPropertyChanged(nameof(IsSelectedEndpointAad));
                OnPropertyChanged(nameof(SelectedEndpointTypeSummary));
                SyncEndpointsToConfig();
                OnChanged();
            }
        }

        public bool IsSelectedEndpointAad => SelectedEndpointAuthMode == 1;

        public int SelectedEndpointApiKeyHeaderMode
        {
            get => (int)(SelectedEndpoint?.ApiKeyHeaderMode ?? ApiKeyHeaderMode.Auto);
            set
            {
                if (SelectedEndpoint == null) return;

                var mode = value switch
                {
                    1 => ApiKeyHeaderMode.ApiKeyHeader,
                    2 => ApiKeyHeaderMode.Bearer,
                    _ => ApiKeyHeaderMode.Auto,
                };

                if (SelectedEndpoint.ApiKeyHeaderMode == mode)
                    return;

                SelectedEndpoint.ApiKeyHeaderMode = mode;
                OnPropertyChanged(nameof(SelectedEndpointApiKeyHeaderMode));
                OnPropertyChanged(nameof(SelectedEndpointTypeSummary));
                SyncEndpointsToConfig();
                OnChanged();
            }
        }

        public int SelectedEndpointTextApiProtocolMode
        {
            get => (int)(SelectedEndpoint?.TextApiProtocolMode ?? TextApiProtocolMode.Auto);
            set
            {
                if (SelectedEndpoint == null) return;

                var mode = value switch
                {
                    1 => TextApiProtocolMode.ChatCompletionsV1,
                    2 => TextApiProtocolMode.ChatCompletionsRaw,
                    3 => TextApiProtocolMode.Responses,
                    _ => TextApiProtocolMode.Auto,
                };

                if (SelectedEndpoint.TextApiProtocolMode == mode)
                    return;

                SelectedEndpoint.TextApiProtocolMode = mode;
                OnPropertyChanged(nameof(SelectedEndpointTextApiProtocolMode));
                OnPropertyChanged(nameof(SelectedEndpointTypeSummary));
                SyncEndpointsToConfig();
                OnChanged();
            }
        }

        public int SelectedEndpointImageApiRouteMode
        {
            get => (int)(SelectedEndpoint?.ImageApiRouteMode ?? ImageApiRouteMode.Auto);
            set
            {
                if (SelectedEndpoint == null) return;

                var mode = value switch
                {
                    1 => ImageApiRouteMode.V1Images,
                    2 => ImageApiRouteMode.ImagesRaw,
                    _ => ImageApiRouteMode.Auto,
                };

                if (SelectedEndpoint.ImageApiRouteMode == mode)
                    return;

                SelectedEndpoint.ImageApiRouteMode = mode;
                OnPropertyChanged(nameof(SelectedEndpointImageApiRouteMode));
                OnPropertyChanged(nameof(SelectedEndpointTypeSummary));
                SyncEndpointsToConfig();
                OnChanged();
            }
        }

        public ICommand AddEndpointCommand { get; }
        public ICommand RemoveEndpointCommand { get; }
        public ICommand DiscoverModelsCommand { get; }
        public ICommand TestSpeechCommand { get; }
        public event Action<string>? StatusRequested;

        /// <summary>内部访问配置，由宿主注入</summary>
        internal AzureSpeechConfig Config { get; set; } = new();

        /// <summary>终结点变更后触发（宿主可用此刷新模型列表）</summary>
        public event Action? EndpointsChanged;

        public override void LoadFrom(AzureSpeechConfig config)
        {
            UnsubscribeEndpoints(_endpoints);
            Config = config;
            _endpoints = new ObservableCollection<AiEndpoint>(config.Endpoints);
            SubscribeEndpoints(_endpoints);
            OnPropertyChanged(nameof(Endpoints));
            OnPropertyChanged(nameof(HasEndpoints));
            OnPropertyChanged(nameof(ShowEmptyState));
            if (_endpoints.Count > 0)
            {
                SelectedEndpoint = _endpoints[0];
            }
            else
            {
                SelectedEndpoint = null;
            }
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            SyncEndpointsToConfig();
        }

        private void AddEndpoint()
            => CreateEndpoint(string.Empty, EndpointApiType.OpenAiCompatible);

        public AiEndpoint CreateEndpoint(string? name, EndpointApiType type)
        {
            var ep = new AiEndpoint
            {
                Id = Guid.NewGuid().ToString(),
                IsEnabled = true,
            };

            _endpointTemplateService.ApplyTemplate(ep, type);
            ep.Name = string.IsNullOrWhiteSpace(name)
                ? BuildDefaultEndpointName(type)
                : name.Trim();

            SubscribeEndpoint(ep);
            Endpoints.Add(ep);
            SelectedEndpoint = ep;
            OnPropertyChanged(nameof(HasEndpoints));
            OnPropertyChanged(nameof(ShowEmptyState));
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
            return ep;
        }

        private void RemoveEndpoint()
        {
            if (SelectedEndpoint == null) return;
            UnsubscribeEndpoint(SelectedEndpoint);
            Endpoints.Remove(SelectedEndpoint);
            SelectedEndpoint = Endpoints.FirstOrDefault();
            OnPropertyChanged(nameof(HasEndpoints));
            OnPropertyChanged(nameof(ShowEmptyState));
            _pendingRemovalCount = 1;
            SyncEndpointsToConfig();
            _pendingRemovalCount = 0;
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        public void RemoveEndpoint(AiEndpoint endpoint)
        {
            if (endpoint == null)
                return;

            if (!Endpoints.Contains(endpoint))
                return;

            var shouldMoveSelection = ReferenceEquals(SelectedEndpoint, endpoint);
            UnsubscribeEndpoint(endpoint);
            Endpoints.Remove(endpoint);

            if (shouldMoveSelection)
            {
                SelectedEndpoint = Endpoints.FirstOrDefault();
            }

            OnPropertyChanged(nameof(HasEndpoints));
            OnPropertyChanged(nameof(ShowEmptyState));
            _pendingRemovalCount = 1;
            SyncEndpointsToConfig();
            _pendingRemovalCount = 0;
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        public AiModelEntry? AddModelToSelectedEndpoint(string modelId, string displayName, string groupName, string deploymentName, ModelCapability capabilities)
        {
            if (SelectedEndpoint == null) return null;
            var model = new AiModelEntry
            {
                ModelId = modelId,
                DisplayName = displayName,
                GroupName = groupName,
                DeploymentName = deploymentName,
                Capabilities = capabilities
            };
            SelectedEndpoint.Models.Add(model);
            SubscribeModel(model);
            DiscoveredModelIds.Remove(modelId);
            OnPropertyChanged(nameof(SelectedEndpointModels));
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
            return model;
        }

        public AiModelEntry? AddBlankModelToSelectedEndpoint()
            => AddModelToSelectedEndpoint("", "", "", "", ModelCapability.None);

        public AiModelEntry? AddDiscoveredModelToSelectedEndpoint(string modelId)
        {
            if (SelectedEndpoint == null)
                return null;

            var existing = SelectedEndpoint.Models.FirstOrDefault(m =>
                string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return existing;

            return AddModelToSelectedEndpoint(
                modelId,
                "",
                "",
                "",
                ModelCapability.None);
        }

        public void RemoveModelFromSelectedEndpoint(AiModelEntry model)
        {
            if (SelectedEndpoint == null) return;
            UnsubscribeModel(model);
            SelectedEndpoint.Models.Remove(model);
            OnPropertyChanged(nameof(SelectedEndpointModels));
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        public void NotifyModelChanged()
        {
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        public void NotifyEndpointChanged()
        {
            if (SelectedEndpoint == null)
                return;

            OnPropertyChanged(nameof(SelectedEndpointTypeSummary));
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        public string GetSelectedEndpointInspectionDetails()
            => SelectedEndpoint == null
                ? "当前未选择终结点。"
                : _endpointTemplateService.BuildInspectionDetails(SelectedEndpoint);

        public EndpointInspectionDetails? GetSelectedEndpointInspectionModel()
            => SelectedEndpoint == null
                ? null
                : _endpointTemplateService.BuildInspectionDetailsModel(SelectedEndpoint);

        public void NotifyStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            StatusRequested?.Invoke(message);
        }

        public Task<EndpointBatchTestReport> RunSelectedEndpointTestAsync(
            IProgress<EndpointBatchTestProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (SelectedEndpoint == null)
                throw new InvalidOperationException("当前未选择终结点。");

            return _endpointBatchTestService.TestSelectedEndpointAsync(Config, SelectedEndpoint, progress, cancellationToken);
        }

        public void ChangeEndpointType(AiEndpoint endpoint, EndpointApiType type)
        {
            if (endpoint == null || endpoint.EndpointType == type)
            {
                return;
            }

            _endpointTemplateService.ApplyTemplate(endpoint, type);

            if (ReferenceEquals(endpoint, SelectedEndpoint))
            {
                OnPropertyChanged(nameof(SelectedEndpointAuthMode));
                OnPropertyChanged(nameof(SelectedEndpointApiKeyHeaderMode));
                OnPropertyChanged(nameof(SelectedEndpointTextApiProtocolMode));
                OnPropertyChanged(nameof(SelectedEndpointImageApiRouteMode));
                OnPropertyChanged(nameof(CanSelectedEndpointUseAad));
                OnPropertyChanged(nameof(IsSelectedEndpointAad));
                OnPropertyChanged(nameof(IsSelectedEndpointAzure));
                OnPropertyChanged(nameof(SelectedEndpointTypeSummary));
            }

            ClearDiscoveredModels();
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        public async Task DiscoverModelsAsync()
        {
            if (SelectedEndpoint == null)
            {
                EndpointDiscoveryStatus = "请先选择一个终结点。";
                return;
            }

            IsDiscoveringModels = true;
            ((RelayCommand)DiscoverModelsCommand).RaiseCanExecuteChanged();
            EndpointDiscoveryStatus = "正在读取终结点模型列表...";

            try
            {
                var result = await _modelDiscoveryService.DiscoverModelsAsync(SelectedEndpoint);
                if (!result.Success)
                {
                    ClearDiscoveredModels(result.Message);
                    return;
                }

                var existing = new HashSet<string>(
                    SelectedEndpoint.Models.Select(m => m.ModelId),
                    StringComparer.OrdinalIgnoreCase);

                DiscoveredModelIds = new ObservableCollection<string>(
                    result.ModelIds.Where(id => !existing.Contains(id)));

                OnPropertyChanged(nameof(HasDiscoveredModels));
                EndpointDiscoveryStatus = DiscoveredModelIds.Count > 0
                    ? result.Message
                    : "模型都已经加进来了，没有新的可添加项。";
            }
            finally
            {
                IsDiscoveringModels = false;
                ((RelayCommand)DiscoverModelsCommand).RaiseCanExecuteChanged();
            }
        }

        private async Task TestSpeechEndpointAsync()
        {
            if (SelectedEndpoint is not { IsSpeechEndpoint: true } ep)
                return;

            var key = ep.SpeechSubscriptionKey?.Trim() ?? "";
            var endpoint = ep.SpeechEndpoint?.Trim() ?? "";
            var region = AzureSubscription.ParseRegionFromEndpoint(endpoint);

            if (string.IsNullOrWhiteSpace(key))
            {
                SpeechTestResult = "✗ 请输入订阅密钥";
                return;
            }

            if (string.IsNullOrWhiteSpace(region))
            {
                SpeechTestResult = "✗ 无法从终结点解析区域";
                return;
            }

            SpeechTestResult = "测试中...";

            var sub = new AzureSubscription
            {
                Name = ep.Name,
                SubscriptionKey = key,
                ServiceRegion = region,
                Endpoint = endpoint
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (isValid, message) = await _speechValidator.ValidateAsync(sub, CancellationToken.None);
            sw.Stop();

            SpeechTestResult = isValid
                ? $"✓ {message} — {sw.ElapsedMilliseconds}ms"
                : $"✗ {message}";
        }

        internal void SyncEndpointsToConfig()
        {
            var prevCount = Config.Endpoints?.Count ?? 0;
            var newCount = Endpoints.Count;

            // 安全护栏：终结点数量减少时，必须来自显式删除操作且每次只能减 1
            if (newCount < prevCount)
            {
                var delta = prevCount - newCount;
                if (_pendingRemovalCount == 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[EndpointGuard] 阻止非删除路径的终结点丢失: {prevCount} → {newCount}");
                    return;
                }
                if (delta > 1)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[EndpointGuard] 阻止批量终结点丢失: {prevCount} → {newCount} (允许减少 {_pendingRemovalCount})");
                    return;
                }
            }

            Config.Endpoints = Endpoints.ToList();
            Config.SyncSpeechResourcesFromEndpoints();
        }

        /// <summary>
        /// 按模板默认 API 版本基线清洗配置中的终结点版本：
        /// 若用户填写版本的日期部分（yyyy-MM-dd）早于模板默认版本日期，则清空该字段。
        /// 比较仅基于日期，忽略 -preview 等后缀。
        /// </summary>
        public bool NormalizeApiVersionsInConfig(AzureSpeechConfig config)
        {
            if (config.Endpoints == null || config.Endpoints.Count == 0)
            {
                return false;
            }

            var changed = false;
            foreach (var endpoint in config.Endpoints)
            {
                if (NormalizeEndpointApiVersion(endpoint))
                {
                    changed = true;
                }
            }

            return changed;
        }

        private void SubscribeEndpoints(IEnumerable<AiEndpoint> endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                SubscribeEndpoint(endpoint);
            }
        }

        private void UnsubscribeEndpoints(IEnumerable<AiEndpoint> endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                UnsubscribeEndpoint(endpoint);
            }
        }

        private void SubscribeEndpoint(AiEndpoint endpoint)
        {
            endpoint.PropertyChanged += Endpoint_PropertyChanged;
            foreach (var model in endpoint.Models)
            {
                SubscribeModel(model);
            }
        }

        private void UnsubscribeEndpoint(AiEndpoint endpoint)
        {
            endpoint.PropertyChanged -= Endpoint_PropertyChanged;
            foreach (var model in endpoint.Models)
            {
                UnsubscribeModel(model);
            }
        }

        private void SubscribeModel(AiModelEntry model)
        {
            model.PropertyChanged += Model_PropertyChanged;
        }

        private void UnsubscribeModel(AiModelEntry model)
        {
            model.PropertyChanged -= Model_PropertyChanged;
        }

        private void Endpoint_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender == SelectedEndpoint &&
                e.PropertyName is nameof(AiEndpoint.BaseUrl)
                    or nameof(AiEndpoint.ApiKey)
                    or nameof(AiEndpoint.ApiVersion)
                    or nameof(AiEndpoint.EndpointType)
                    or nameof(AiEndpoint.AuthMode)
                    or nameof(AiEndpoint.ApiKeyHeaderMode)
                    or nameof(AiEndpoint.TextApiProtocolMode)
                    or nameof(AiEndpoint.ImageApiRouteMode)
                    or nameof(AiEndpoint.AzureTenantId)
                    or nameof(AiEndpoint.AzureClientId))
            {
                ClearDiscoveredModels();
            }

            if (sender == SelectedEndpoint && e.PropertyName is nameof(AiEndpoint.IsAzureEndpoint)
                or nameof(AiEndpoint.EndpointType)
                or nameof(AiEndpoint.AuthMode)
                or nameof(AiEndpoint.ApiKeyHeaderMode)
                or nameof(AiEndpoint.TextApiProtocolMode)
                or nameof(AiEndpoint.ImageApiRouteMode)
                or nameof(AiEndpoint.ApiVersion))
            {
                OnPropertyChanged(nameof(IsSelectedEndpointAzure));
                OnPropertyChanged(nameof(IsSelectedEndpointSpeech));
                OnPropertyChanged(nameof(CanSelectedEndpointUseAad));
                OnPropertyChanged(nameof(SelectedEndpointAuthMode));
                OnPropertyChanged(nameof(IsSelectedEndpointAad));
                OnPropertyChanged(nameof(SelectedEndpointApiKeyHeaderMode));
                OnPropertyChanged(nameof(SelectedEndpointTextApiProtocolMode));
                OnPropertyChanged(nameof(SelectedEndpointImageApiRouteMode));
                OnPropertyChanged(nameof(SelectedEndpointApiVersionWatermark));
                OnPropertyChanged(nameof(SelectedEndpointApiVersionNote));
                OnPropertyChanged(nameof(SelectedEndpointTypeSummary));
            }

            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        private void ClearDiscoveredModels(string message = "")
        {
            if (DiscoveredModelIds.Count > 0)
            {
                DiscoveredModelIds.Clear();
            }

            EndpointDiscoveryStatus = message;
            OnPropertyChanged(nameof(HasDiscoveredModels));
        }

        private string BuildDefaultEndpointName(EndpointApiType type)
        {
            var prefix = _endpointTemplateService.GetTemplate(type).DefaultNamePrefix;
            var used = Endpoints
                .Count(endpoint => endpoint.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return $"{prefix} {used + 1}";
        }

        private bool NormalizeEndpointApiVersion(AiEndpoint endpoint)
        {
            if (endpoint == null)
            {
                return false;
            }

            var current = endpoint.ApiVersion?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(current))
            {
                return false;
            }

            var baseline = _endpointTemplateService.GetTemplate(endpoint).DefaultApiVersion?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(baseline))
            {
                return false;
            }

            if (!TryParseApiVersionDate(current, out var currentDate)
                || !TryParseApiVersionDate(baseline, out var baselineDate))
            {
                return false;
            }

            if (currentDate >= baselineDate)
            {
                return false;
            }

            endpoint.ApiVersion = string.Empty;
            return true;
        }

        private static bool TryParseApiVersionDate(string input, out DateOnly date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var value = input.Trim();
            var parts = value.Split('-', 4, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out var year)
                || !int.TryParse(parts[1], out var month)
                || !TryParseLeadingInt(parts[2], out var day))
            {
                return false;
            }

            try
            {
                date = new DateOnly(year, month, day);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseLeadingInt(string input, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var i = 0;
            while (i < input.Length && char.IsDigit(input[i]))
            {
                i++;
            }

            if (i == 0)
            {
                return false;
            }

            return int.TryParse(input[..i], out value);
        }

    }
}
