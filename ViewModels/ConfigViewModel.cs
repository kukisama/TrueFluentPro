using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels
{
    public class ConfigViewModel : ViewModelBase
    {
        private readonly ConfigurationService _configService;
        private readonly AzureSubscriptionValidator _subscriptionValidator;
        private readonly Action _translationCommandsRefresh;
        private readonly Action<AzureSpeechConfig>? _translationServiceUpdater;
        private readonly Action<string, string, bool> _logger;
        private readonly Func<bool> _isReviewSummaryLoadingProvider;
        private readonly Func<Window?> _mainWindowProvider;

        private AzureSpeechConfig _config;
        private ObservableCollection<string> _speechResourceNames;
        private int _activeSpeechResourceIndex;
        private SubscriptionValidationState _subscriptionValidationState = SubscriptionValidationState.Unknown;
        private string _subscriptionValidationStatusMessage = "";
        private CancellationTokenSource? _subscriptionValidationCts;
        private int _subscriptionValidationVersion;
        private bool _subscriptionLampBlinkOn = true;
        private readonly DispatcherTimer _subscriptionLampTimer;
        private bool _reviewLampBlinkOn = true;
        private string _sourceLanguage = "auto";
        private string _targetLanguage = "zh-Hans";
        private readonly string[] _sourceLanguages = { "auto", "en-US", "zh-CN", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES" };
        private readonly string[] _targetLanguages = { "en", "zh-Hans", "ja", "ko", "fr", "de", "es" };
        private bool _isConfigurationEnabled = true;
        private bool _suppressIndexPersistence;

        public event Action<AzureSpeechConfig>? ConfigLoaded;
        public event Action<AzureSpeechConfig>? ConfigUpdatedFromExternal;

        public ConfigViewModel(
            ConfigurationService configService,
            AzureSubscriptionValidator subscriptionValidator,
            AzureSpeechConfig initialConfig,
            Action translationCommandsRefresh,
            Action<AzureSpeechConfig>? translationServiceUpdater,
            Action<string, string, bool> logger,
            Func<bool> isReviewSummaryLoadingProvider,
            Func<Window?> mainWindowProvider)
        {
            _configService = configService;
            _subscriptionValidator = subscriptionValidator;
            _config = initialConfig;
            _translationCommandsRefresh = translationCommandsRefresh;
            _translationServiceUpdater = translationServiceUpdater;
            _logger = logger;
            _isReviewSummaryLoadingProvider = isReviewSummaryLoadingProvider;
            _mainWindowProvider = mainWindowProvider;
            _speechResourceNames = new ObservableCollection<string>();

            _subscriptionLampTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
            {
                if (_subscriptionValidationState == SubscriptionValidationState.Validating)
                {
                    _subscriptionLampBlinkOn = !_subscriptionLampBlinkOn;
                    OnPropertyChanged(nameof(SubscriptionLampOpacity));
                }
                if (_isReviewSummaryLoadingProvider())
                {
                    _reviewLampBlinkOn = !_reviewLampBlinkOn;
                    OnPropertyChanged(nameof(ReviewSummaryLampOpacity));
                }
                else if (ReviewSummaryLampOpacity != 1)
                {
                    _reviewLampBlinkOn = true;
                    OnPropertyChanged(nameof(ReviewSummaryLampOpacity));
                }
                else if (SubscriptionLampOpacity != 1)
                {
                    _subscriptionLampBlinkOn = true;
                    OnPropertyChanged(nameof(SubscriptionLampOpacity));
                }
            });
            _subscriptionLampTimer.Start();
        }

        public AzureSpeechConfig Config
        {
            get => _config;
            set => SetProperty(ref _config, value);
        }

        public ObservableCollection<string> SpeechResourceNames
        {
            get => _speechResourceNames;
            set => SetProperty(ref _speechResourceNames, value);
        }

        public int ActiveSpeechResourceIndex
        {
            get => _activeSpeechResourceIndex;
            set
            {
                var resources = _config.GetEffectiveSpeechResources();
                if (_suppressIndexPersistence)
                {
                    SetProperty(ref _activeSpeechResourceIndex, value);
                    return;
                }

                if (value >= 0 && value < resources.Count)
                {
                    if (SetProperty(ref _activeSpeechResourceIndex, value))
                    {
                        var selectedResource = resources[value];
                        _config.ActiveSpeechResourceId = selectedResource.Id;
                        _config.ActiveSubscriptionIndex = TryResolveLegacyMicrosoftSubscriptionIndex(selectedResource.Id);
                        OnPropertyChanged(nameof(ActiveSpeechResourceStatus));
                        _translationCommandsRefresh();
                        _translationServiceUpdater?.Invoke(_config);
                        TriggerSubscriptionValidation();
                        _ = Task.Run(async () => await _configService.SaveConfigAsync(_config));
                    }
                }
                else if (value == -1)
                {
                    if (SetProperty(ref _activeSpeechResourceIndex, value))
                    {
                        _config.ActiveSpeechResourceId = "";
                        _config.ActiveSubscriptionIndex = value;
                        OnPropertyChanged(nameof(ActiveSpeechResourceStatus));
                        _translationCommandsRefresh();
                        TriggerSubscriptionValidation();
                        _ = Task.Run(async () => await _configService.SaveConfigAsync(_config));
                    }
                }
            }
        }

        public SubscriptionValidationState SubscriptionValidationState
        {
            get => _subscriptionValidationState;
            private set
            {
                if (SetProperty(ref _subscriptionValidationState, value))
                {
                    OnPropertyChanged(nameof(SubscriptionLampFill));
                    OnPropertyChanged(nameof(SubscriptionLampStroke));
                    OnPropertyChanged(nameof(SubscriptionLampOpacity));
                }
            }
        }

        public string SubscriptionValidationStatusMessage
        {
            get => _subscriptionValidationStatusMessage;
            private set => SetProperty(ref _subscriptionValidationStatusMessage, value);
        }

        public IBrush SubscriptionLampFill
        {
            get
            {
                return _subscriptionValidationState switch
                {
                    SubscriptionValidationState.Valid => Brushes.LimeGreen,
                    SubscriptionValidationState.Invalid => Brushes.Red,
                    _ => Brushes.Transparent
                };
            }
        }

        public IBrush SubscriptionLampStroke
        {
            get
            {
                return _subscriptionValidationState switch
                {
                    SubscriptionValidationState.Valid => Brushes.LimeGreen,
                    SubscriptionValidationState.Invalid => Brushes.Red,
                    SubscriptionValidationState.Validating => Brushes.Gray,
                    _ => Brushes.Gray
                };
            }
        }

        public double SubscriptionLampOpacity
        {
            get
            {
                if (_subscriptionValidationState != SubscriptionValidationState.Validating)
                {
                    return 1;
                }

                return _subscriptionLampBlinkOn ? 1 : 0.3;
            }
        }

        public string SourceLanguage
        {
            get => _sourceLanguage;
            set
            {
                if (SetProperty(ref _sourceLanguage, value))
                {
                    _config.SourceLanguage = value;
                    OnPropertyChanged(nameof(SourceLanguageIndex));
                    _translationCommandsRefresh();
                    _translationServiceUpdater?.Invoke(_config);
                    _ = Task.Run(async () => await _configService.SaveConfigAsync(_config));
                }
            }
        }

        public string TargetLanguage
        {
            get => _targetLanguage;
            set
            {
                if (SetProperty(ref _targetLanguage, value))
                {
                    _config.TargetLanguage = value;
                    OnPropertyChanged(nameof(TargetLanguageIndex));
                    _translationCommandsRefresh();
                    _translationServiceUpdater?.Invoke(_config);
                    _ = Task.Run(async () => await _configService.SaveConfigAsync(_config));
                }
            }
        }

        public string ActiveSpeechResourceStatus
        {
            get
            {
                var resource = _config.GetActiveSpeechResource();
                if (resource != null)
                {
                    return resource.GetDisplayName();
                }
                return "未配置";
            }
        }

        public bool IsConfigurationEnabled
        {
            get => _isConfigurationEnabled;
            set => SetProperty(ref _isConfigurationEnabled, value);
        }

        public int SourceLanguageIndex
        {
            get
            {
                var index = Array.IndexOf(_sourceLanguages, _sourceLanguage);
                if (index >= 0)
                {
                    return index;
                }

                SourceLanguage = _sourceLanguages[0];
                return 0;
            }
            set
            {
                if (value >= 0 && value < _sourceLanguages.Length)
                {
                    SourceLanguage = _sourceLanguages[value];
                }
            }
        }

        public int TargetLanguageIndex
        {
            get
            {
                var index = Array.IndexOf(_targetLanguages, _targetLanguage);
                return index >= 0 ? index : 0;
            }
            set
            {
                if (value >= 0 && value < _targetLanguages.Length)
                {
                    TargetLanguage = _targetLanguages[value];
                }
            }
        }

        public double ReviewSummaryLampOpacity => _isReviewSummaryLoadingProvider()
            ? (_reviewLampBlinkOn ? 1.0 : 0.35)
            : 1.0;

        public void NotifyReviewLampChanged()
        {
            OnPropertyChanged(nameof(ReviewSummaryLampOpacity));
        }

        public async Task LoadConfigAsync()
        {
            _config = await _configService.LoadConfigAsync();

            var savedIndex = _config.GetEffectiveActiveSpeechResourceIndex();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _suppressIndexPersistence = true;
                try
                {
                    UpdateSpeechResourceNames();
                }
                finally
                {
                    _suppressIndexPersistence = false;
                }

                var resources = _config.GetEffectiveSpeechResources();
                if (resources.Count > 0 && savedIndex >= resources.Count)
                {
                    savedIndex = resources.Count - 1;
                }
                else if (resources.Count == 0)
                {
                    savedIndex = -1;
                }

                ApplyActiveSpeechResourceSelection(savedIndex, resources);
                _sourceLanguage = NormalizeToKnown(_config.SourceLanguage, _sourceLanguages);
                _config.SourceLanguage = _sourceLanguage;
                _targetLanguage = NormalizeToKnown(_config.TargetLanguage, _targetLanguages);
                _config.TargetLanguage = _targetLanguage;
                _activeSpeechResourceIndex = savedIndex;

                OnPropertyChanged(nameof(Config));
                OnPropertyChanged(nameof(SpeechResourceNames));
                OnPropertyChanged(nameof(SourceLanguage));
                OnPropertyChanged(nameof(TargetLanguage));
                OnPropertyChanged(nameof(SourceLanguageIndex));
                OnPropertyChanged(nameof(TargetLanguageIndex));
                OnPropertyChanged(nameof(ActiveSpeechResourceStatus));

                ForceUpdateComboBoxSelection();
                _translationCommandsRefresh();

                ConfigLoaded?.Invoke(_config);
            });
        }

        public void HandleExternalConfigUpdate(AzureSpeechConfig updatedConfig)
        {
            _config = updatedConfig;

            var savedIndex = _config.GetEffectiveActiveSpeechResourceIndex();

            _suppressIndexPersistence = true;
            try
            {
                UpdateSpeechResourceNames();
            }
            finally
            {
                _suppressIndexPersistence = false;
            }

            var resources = _config.GetEffectiveSpeechResources();
            if (resources.Count > 0 && savedIndex >= resources.Count)
            {
                savedIndex = resources.Count - 1;
            }
            else if (resources.Count == 0)
            {
                savedIndex = -1;
            }

            ApplyActiveSpeechResourceSelection(savedIndex, resources);
            _activeSpeechResourceIndex = savedIndex;

            OnPropertyChanged(nameof(SpeechResourceNames));
            OnPropertyChanged(nameof(ActiveSpeechResourceStatus));

            ForceUpdateComboBoxSelection();
            _translationCommandsRefresh();
            TriggerSubscriptionValidation();

            ConfigUpdatedFromExternal?.Invoke(_config);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _configService.SaveConfigAsync(_config);
                }
                catch
                {
                }
            });
        }

        public void TriggerSubscriptionValidation()
        {
            var activeResource = _config.GetActiveSpeechResource();
            if (!_config.TryGetActiveRealtimeSpeechResource(out _, out var resourceReadinessMessage))
            {
                SubscriptionValidationState = activeResource == null
                    ? SubscriptionValidationState.Unknown
                    : SubscriptionValidationState.Invalid;
                SubscriptionValidationStatusMessage = resourceReadinessMessage;
                return;
            }

            if (activeResource?.ConnectorType != SpeechConnectorType.MicrosoftSpeech)
            {
                SubscriptionValidationState = SubscriptionValidationState.Valid;
                SubscriptionValidationStatusMessage = $"✓ 实时语音资源可用：{activeResource!.GetDisplayName()}";
                return;
            }

            if (!_config.TryGetActiveMicrosoftSpeechSubscriptionForRealtime(out var subscription, out var readinessMessage))
            {
                SubscriptionValidationState = activeResource == null
                    ? SubscriptionValidationState.Unknown
                    : SubscriptionValidationState.Invalid;
                SubscriptionValidationStatusMessage = readinessMessage;
                return;
            }

            var version = Interlocked.Increment(ref _subscriptionValidationVersion);
            _subscriptionValidationCts?.Cancel();
            _subscriptionValidationCts?.Dispose();
            _subscriptionValidationCts = new CancellationTokenSource();
            var token = _subscriptionValidationCts.Token;
            var validatedSubscription = subscription;

            SubscriptionValidationState = SubscriptionValidationState.Validating;
            SubscriptionValidationStatusMessage = $"正在验证语音资源：{activeResource!.GetDisplayName()} ...";

            _ = Task.Run(async () =>
            {
                var result = await _subscriptionValidator.ValidateAsync(validatedSubscription!, token).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (version != _subscriptionValidationVersion)
                    {
                        return;
                    }

                    SubscriptionValidationState = result.IsValid
                        ? SubscriptionValidationState.Valid
                        : SubscriptionValidationState.Invalid;

                    SubscriptionValidationStatusMessage = result.IsValid
                        ? $"✓ 语音资源可用：{activeResource.GetDisplayName()}"
                        : $"✗ 语音资源不可用：{activeResource.GetDisplayName()} - {result.Message}";
                });
            });
        }

        public void UpdateSpeechResourceNames()
        {
            _speechResourceNames.Clear();

            foreach (var resource in _config.GetEffectiveSpeechResources())
            {
                _speechResourceNames.Add(resource.GetDisplayName());
            }
        }

        public void QueueConfigSave(string reason)
        {
            var configPath = _configService.GetConfigFilePath();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _configService.SaveConfigAsync(_config).ConfigureAwait(false);
                    _logger("ConfigSaved", $"reason='{reason}' path='{configPath}'", true);
                }
                catch (Exception ex)
                {
                    _logger("ConfigSaveFailed",
                        $"reason='{reason}' path='{configPath}' error='{ex.Message}'", false);
                }
            });
        }

        public void SetConfig(AzureSpeechConfig config)
        {
            _config = config;

            var savedIndex = _config.GetEffectiveActiveSpeechResourceIndex();

            _suppressIndexPersistence = true;
            try
            {
                UpdateSpeechResourceNames();
            }
            finally
            {
                _suppressIndexPersistence = false;
            }

            var resources = _config.GetEffectiveSpeechResources();
            if (resources.Count > 0 && savedIndex >= resources.Count)
            {
                savedIndex = resources.Count - 1;
            }
            else if (resources.Count == 0)
            {
                savedIndex = -1;
            }

            ApplyActiveSpeechResourceSelection(savedIndex, resources);
            _activeSpeechResourceIndex = savedIndex;

            OnPropertyChanged(nameof(Config));
            OnPropertyChanged(nameof(SpeechResourceNames));
            OnPropertyChanged(nameof(ActiveSpeechResourceStatus));

            ForceUpdateComboBoxSelection();
            _translationCommandsRefresh();
            TriggerSubscriptionValidation();
        }

        /// <summary>配置值不在已知选项中时，回退到第一项。</summary>
        private static string NormalizeToKnown(string value, string[] known)
            => Array.IndexOf(known, value) >= 0 ? value : known[0];

        public void ForceUpdateComboBoxSelection()
        {
            // Force binding refresh by bouncing the value through OnPropertyChanged
            var idx = _activeSpeechResourceIndex;
            _suppressIndexPersistence = true;
            try
            {
                _activeSpeechResourceIndex = -1;
                OnPropertyChanged(nameof(ActiveSpeechResourceIndex));
                _activeSpeechResourceIndex = idx;
                OnPropertyChanged(nameof(ActiveSpeechResourceIndex));
            }
            finally
            {
                _suppressIndexPersistence = false;
            }
        }

        private void ApplyActiveSpeechResourceSelection(int index, System.Collections.Generic.IReadOnlyList<SpeechResource> resources)
        {
            if (index < 0 || index >= resources.Count)
            {
                _config.ActiveSpeechResourceId = "";
                _config.ActiveSubscriptionIndex = -1;
                return;
            }

            var selectedResource = resources[index];
            _config.ActiveSpeechResourceId = selectedResource.Id;
            _config.ActiveSubscriptionIndex = TryResolveLegacyMicrosoftSubscriptionIndex(selectedResource.Id);
        }

        private static int TryResolveLegacyMicrosoftSubscriptionIndex(string? resourceId)
        {
            if (string.IsNullOrWhiteSpace(resourceId) ||
                !resourceId.StartsWith("legacy-microsoft-", StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }

            var suffix = resourceId["legacy-microsoft-".Length..];
            return int.TryParse(suffix, out var index) ? index : -1;
        }

        public string GetConfigFilePath() => _configService.GetConfigFilePath();
    }
}
