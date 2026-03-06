using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels.Settings
{
    public class EndpointsSectionVM : SettingsSectionBase
    {
        private readonly IAiEndpointModelDiscoveryService _modelDiscoveryService;
        private ObservableCollection<AiEndpoint> _endpoints = new();
        private ObservableCollection<string> _discoveredModelIds = new();
        private AiEndpoint? _selectedEndpoint;
        private string _endpointDiscoveryStatus = "";
        private bool _isDiscoveringModels;

        public EndpointsSectionVM(IAiEndpointModelDiscoveryService modelDiscoveryService)
        {
            _modelDiscoveryService = modelDiscoveryService;
            AddEndpointCommand = new RelayCommand(_ => AddEndpoint());
            RemoveEndpointCommand = new RelayCommand(_ => RemoveEndpoint(), _ => SelectedEndpoint != null);
            DiscoverModelsCommand = new RelayCommand(async _ => await DiscoverModelsAsync(), _ => SelectedEndpoint != null && !IsDiscoveringModels);
        }

        public ObservableCollection<AiEndpoint> Endpoints { get => _endpoints; set => SetProperty(ref _endpoints, value); }
        public ObservableCollection<string> DiscoveredModelIds { get => _discoveredModelIds; set => SetProperty(ref _discoveredModelIds, value); }

        public AiEndpoint? SelectedEndpoint
        {
            get => _selectedEndpoint;
            set
            {
                if (SetProperty(ref _selectedEndpoint, value))
                {
                    ((RelayCommand)RemoveEndpointCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)DiscoverModelsCommand).RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(SelectedEndpointModels));
                    OnPropertyChanged(nameof(SelectedEndpointAuthMode));
                    OnPropertyChanged(nameof(IsSelectedEndpointAad));
                    OnPropertyChanged(nameof(HasSelectedEndpoint));
                    OnPropertyChanged(nameof(ShowEmptyState));
                    OnPropertyChanged(nameof(IsSelectedEndpointAzure));
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
        public string EndpointDiscoveryStatus { get => _endpointDiscoveryStatus; set => SetProperty(ref _endpointDiscoveryStatus, value); }
        public bool IsDiscoveringModels { get => _isDiscoveringModels; set => SetProperty(ref _isDiscoveringModels, value); }

        public int SelectedEndpointAuthMode
        {
            get => SelectedEndpoint?.AuthMode == AzureAuthMode.AAD ? 1 : 0;
            set
            {
                if (SelectedEndpoint == null) return;
                SelectedEndpoint.AuthMode = value == 1 ? AzureAuthMode.AAD : AzureAuthMode.ApiKey;
                OnPropertyChanged(nameof(SelectedEndpointAuthMode));
                OnPropertyChanged(nameof(IsSelectedEndpointAad));
                SyncEndpointsToConfig();
                OnChanged();
            }
        }

        public bool IsSelectedEndpointAad => SelectedEndpointAuthMode == 1;

        public ICommand AddEndpointCommand { get; }
        public ICommand RemoveEndpointCommand { get; }
        public ICommand DiscoverModelsCommand { get; }

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
        {
            var ep = new AiEndpoint
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"终结点 {Endpoints.Count + 1}",
                IsEnabled = true,
            };
            SubscribeEndpoint(ep);
            Endpoints.Add(ep);
            SelectedEndpoint = ep;
            OnPropertyChanged(nameof(HasEndpoints));
            OnPropertyChanged(nameof(ShowEmptyState));
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        private void RemoveEndpoint()
        {
            if (SelectedEndpoint == null) return;
            UnsubscribeEndpoint(SelectedEndpoint);
            Endpoints.Remove(SelectedEndpoint);
            SelectedEndpoint = Endpoints.FirstOrDefault();
            OnPropertyChanged(nameof(HasEndpoints));
            OnPropertyChanged(nameof(ShowEmptyState));
            SyncEndpointsToConfig();
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
            => AddModelToSelectedEndpoint("", "", "", "", ModelCapability.Text);

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
                InferCapabilityFromModelId(modelId));
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

        internal void SyncEndpointsToConfig()
        {
            Config.Endpoints = Endpoints.ToList();
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
                    or nameof(AiEndpoint.AuthMode)
                    or nameof(AiEndpoint.AzureTenantId)
                    or nameof(AiEndpoint.AzureClientId))
            {
                ClearDiscoveredModels();
            }

            if (sender == SelectedEndpoint && e.PropertyName == nameof(AiEndpoint.IsAzureEndpoint))
            {
                OnPropertyChanged(nameof(IsSelectedEndpointAzure));
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

        private static ModelCapability InferCapabilityFromModelId(string modelId)
        {
            var normalized = modelId?.Trim().ToLowerInvariant() ?? "";
            if (normalized.Contains("sora") || normalized.Contains("video"))
                return ModelCapability.Video;
            if (normalized.Contains("image") || normalized.Contains("dall"))
                return ModelCapability.Image;
            return ModelCapability.Text;
        }
    }
}
