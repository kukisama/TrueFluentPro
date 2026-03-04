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
        private ObservableCollection<string> _subscriptionNames;
        private int _activeSubscriptionIndex;
        private SubscriptionValidationState _subscriptionValidationState = SubscriptionValidationState.Unknown;
        private string _subscriptionValidationStatusMessage = "";
        private CancellationTokenSource? _subscriptionValidationCts;
        private int _subscriptionValidationVersion;
        private bool _subscriptionLampBlinkOn = true;
        private readonly DispatcherTimer _subscriptionLampTimer;
        private bool _reviewLampBlinkOn = true;
        private string _sourceLanguage = "auto";
        private string _targetLanguage = "zh-CN";
        private readonly string[] _sourceLanguages = { "auto", "en", "zh-CN", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES" };
        private readonly string[] _targetLanguages = { "en", "zh-CN", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES" };
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
            _subscriptionNames = new ObservableCollection<string>();

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

        public ObservableCollection<string> SubscriptionNames
        {
            get => _subscriptionNames;
            set => SetProperty(ref _subscriptionNames, value);
        }

        public int ActiveSubscriptionIndex
        {
            get => _activeSubscriptionIndex;
            set
            {
                if (_suppressIndexPersistence)
                {
                    SetProperty(ref _activeSubscriptionIndex, value);
                    return;
                }

                if (value >= 0 && value < _config.Subscriptions.Count)
                {
                    if (SetProperty(ref _activeSubscriptionIndex, value))
                    {
                        _config.ActiveSubscriptionIndex = value;
                        OnPropertyChanged(nameof(ActiveSubscriptionStatus));
                        _translationCommandsRefresh();
                        _translationServiceUpdater?.Invoke(_config);
                        TriggerSubscriptionValidation();
                        _ = Task.Run(async () => await _configService.SaveConfigAsync(_config));
                    }
                }
                else if (value == -1)
                {
                    if (SetProperty(ref _activeSubscriptionIndex, value))
                    {
                        _config.ActiveSubscriptionIndex = value;
                        OnPropertyChanged(nameof(ActiveSubscriptionStatus));
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

        public string ActiveSubscriptionStatus
        {
            get
            {
                var subscription = _config.GetActiveSubscription();
                if (subscription != null && subscription.IsValid())
                {
                    return $"{subscription.Name} ({subscription.ServiceRegion})";
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

            var savedIndex = _config.ActiveSubscriptionIndex;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _suppressIndexPersistence = true;
                try
                {
                    UpdateSubscriptionNames();
                }
                finally
                {
                    _suppressIndexPersistence = false;
                }

                if (_config.Subscriptions.Count > 0 && savedIndex >= _config.Subscriptions.Count)
                {
                    savedIndex = _config.Subscriptions.Count - 1;
                }
                else if (_config.Subscriptions.Count == 0)
                {
                    savedIndex = -1;
                }

                _config.ActiveSubscriptionIndex = savedIndex;
                _sourceLanguage = _config.SourceLanguage;
                _targetLanguage = _config.TargetLanguage;
                _activeSubscriptionIndex = savedIndex;

                OnPropertyChanged(nameof(Config));
                OnPropertyChanged(nameof(SubscriptionNames));
                OnPropertyChanged(nameof(SourceLanguage));
                OnPropertyChanged(nameof(TargetLanguage));
                OnPropertyChanged(nameof(SourceLanguageIndex));
                OnPropertyChanged(nameof(TargetLanguageIndex));
                OnPropertyChanged(nameof(ActiveSubscriptionStatus));

                ForceUpdateComboBoxSelection();
                _translationCommandsRefresh();

                ConfigLoaded?.Invoke(_config);
            });
        }

        public void HandleExternalConfigUpdate(AzureSpeechConfig updatedConfig)
        {
            _config = updatedConfig;

            var savedIndex = _config.ActiveSubscriptionIndex;

            _suppressIndexPersistence = true;
            try
            {
                UpdateSubscriptionNames();
            }
            finally
            {
                _suppressIndexPersistence = false;
            }

            if (_config.Subscriptions.Count > 0 && savedIndex >= _config.Subscriptions.Count)
            {
                savedIndex = _config.Subscriptions.Count - 1;
            }
            else if (_config.Subscriptions.Count == 0)
            {
                savedIndex = -1;
            }

            _config.ActiveSubscriptionIndex = savedIndex;
            _activeSubscriptionIndex = savedIndex;

            OnPropertyChanged(nameof(SubscriptionNames));
            OnPropertyChanged(nameof(ActiveSubscriptionStatus));

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
            var subscription = _config.GetActiveSubscription();
            if (subscription == null || !subscription.IsValid())
            {
                SubscriptionValidationState = SubscriptionValidationState.Unknown;
                SubscriptionValidationStatusMessage = "未配置有效订阅";
                return;
            }

            var version = Interlocked.Increment(ref _subscriptionValidationVersion);
            _subscriptionValidationCts?.Cancel();
            _subscriptionValidationCts?.Dispose();
            _subscriptionValidationCts = new CancellationTokenSource();
            var token = _subscriptionValidationCts.Token;

            SubscriptionValidationState = SubscriptionValidationState.Validating;
            SubscriptionValidationStatusMessage = $"正在验证订阅：{subscription.Name} ({subscription.ServiceRegion}) ...";

            _ = Task.Run(async () =>
            {
                var result = await _subscriptionValidator.ValidateAsync(subscription, token).ConfigureAwait(false);

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
                        ? $"✓ 订阅可用：{subscription.Name} ({subscription.ServiceRegion})"
                        : $"✗ 订阅不可用：{subscription.Name} ({subscription.ServiceRegion}) - {result.Message}";
                });
            });
        }

        public void UpdateSubscriptionNames()
        {
            _subscriptionNames.Clear();

            foreach (var subscription in _config.Subscriptions)
            {
                var displayName = $"{subscription.Name} ({subscription.ServiceRegion})";
                _subscriptionNames.Add(displayName);
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
            OnPropertyChanged(nameof(Config));
        }

        public void ForceUpdateComboBoxSelection()
        {
            // Force binding refresh by bouncing the value through OnPropertyChanged
            var idx = _activeSubscriptionIndex;
            _suppressIndexPersistence = true;
            try
            {
                _activeSubscriptionIndex = -1;
                OnPropertyChanged(nameof(ActiveSubscriptionIndex));
                _activeSubscriptionIndex = idx;
                OnPropertyChanged(nameof(ActiveSubscriptionIndex));
            }
            finally
            {
                _suppressIndexPersistence = false;
            }
        }

        public string GetConfigFilePath() => _configService.GetConfigFilePath();
    }
}
