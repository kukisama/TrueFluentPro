using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using TrueFluentPro.Helpers;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels.Settings
{
    public class EndpointsSectionVM : SettingsSectionBase
    {
        private ObservableCollection<AiEndpoint> _endpoints = new();
        private AiEndpoint? _selectedEndpoint;

        public EndpointsSectionVM()
        {
            AddEndpointCommand = new RelayCommand(_ => AddEndpoint());
            RemoveEndpointCommand = new RelayCommand(_ => RemoveEndpoint(), _ => SelectedEndpoint != null);
        }

        public ObservableCollection<AiEndpoint> Endpoints { get => _endpoints; set => SetProperty(ref _endpoints, value); }

        public AiEndpoint? SelectedEndpoint
        {
            get => _selectedEndpoint;
            set
            {
                if (SetProperty(ref _selectedEndpoint, value))
                {
                    ((RelayCommand)RemoveEndpointCommand).RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(SelectedEndpointModels));
                    OnPropertyChanged(nameof(SelectedEndpointAuthMode));
                    OnPropertyChanged(nameof(IsSelectedEndpointAad));
                }
            }
        }

        public List<AiModelEntry>? SelectedEndpointModels =>
            SelectedEndpoint?.Models?.ToList();

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

        /// <summary>内部访问配置，由宿主注入</summary>
        internal AzureSpeechConfig Config { get; set; } = new();

        /// <summary>终结点变更后触发（宿主可用此刷新模型列表）</summary>
        public event Action? EndpointsChanged;

        public override void LoadFrom(AzureSpeechConfig config)
        {
            Config = config;
            _endpoints = new ObservableCollection<AiEndpoint>(config.Endpoints);
            OnPropertyChanged(nameof(Endpoints));
            if (_endpoints.Count > 0)
                SelectedEndpoint = _endpoints[0];
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
            Endpoints.Add(ep);
            SelectedEndpoint = ep;
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        private void RemoveEndpoint()
        {
            if (SelectedEndpoint == null) return;
            Endpoints.Remove(SelectedEndpoint);
            SelectedEndpoint = Endpoints.FirstOrDefault();
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        public void AddModelToSelectedEndpoint(string modelId, string displayName, string groupName, string deploymentName, ModelCapability capabilities)
        {
            if (SelectedEndpoint == null) return;
            SelectedEndpoint.Models.Add(new AiModelEntry
            {
                ModelId = modelId,
                DisplayName = displayName,
                GroupName = groupName,
                DeploymentName = deploymentName,
                Capabilities = capabilities
            });
            OnPropertyChanged(nameof(SelectedEndpointModels));
            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        public void RemoveModelFromSelectedEndpoint(AiModelEntry model)
        {
            if (SelectedEndpoint == null) return;
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

            var selectedId = SelectedEndpoint.Id;
            Endpoints = new ObservableCollection<AiEndpoint>(Endpoints);
            SelectedEndpoint = Endpoints.FirstOrDefault(e => e.Id == selectedId);

            SyncEndpointsToConfig();
            EndpointsChanged?.Invoke();
            OnChanged();
        }

        internal void SyncEndpointsToConfig()
        {
            Config.Endpoints = Endpoints.ToList();
        }
    }
}
