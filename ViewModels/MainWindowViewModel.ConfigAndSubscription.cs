using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TrueFluentPro.Models;

namespace TrueFluentPro.ViewModels
{
    public partial class MainWindowViewModel
    {
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
                if (value >= 0 && value < _config.Subscriptions.Count)
                {
                    if (SetProperty(ref _activeSubscriptionIndex, value))
                    {
                        _config.ActiveSubscriptionIndex = value;
                        OnPropertyChanged(nameof(ActiveSubscriptionStatus));
                        ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
                        ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();

                        if (_translationService != null)
                        {
                            _ = _translationService.UpdateConfigAsync(_config);
                        }

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
                        ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
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
                    ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();

                    if (_translationService != null)
                    {
                        _ = _translationService.UpdateConfigAsync(_config);
                    }

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
                    ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();

                    if (_translationService != null)
                    {
                        _ = _translationService.UpdateConfigAsync(_config);
                    }

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

        private async Task LoadConfigAsync()
        {
            try
            {
                _config = await _configService.LoadConfigAsync();

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateSubscriptionNames();

                    if (_config.Subscriptions.Count > 0 && _config.ActiveSubscriptionIndex >= _config.Subscriptions.Count)
                    {
                        _config.ActiveSubscriptionIndex = _config.Subscriptions.Count - 1;
                    }
                    else if (_config.Subscriptions.Count == 0 && _config.ActiveSubscriptionIndex != -1)
                    {
                        _config.ActiveSubscriptionIndex = -1;
                    }

                    _sourceLanguage = _config.SourceLanguage;
                    _targetLanguage = _config.TargetLanguage;

                    _activeSubscriptionIndex = _config.ActiveSubscriptionIndex;

                    _audioSourceModeIndex = AudioSourceModeToIndex(_config.AudioSourceMode);
                    NormalizeRecognitionToggles();

                    OnPropertyChanged(nameof(Config));
                    OnPropertyChanged(nameof(SubscriptionNames));
                    OnPropertyChanged(nameof(SourceLanguage));
                    OnPropertyChanged(nameof(TargetLanguage));
                    OnPropertyChanged(nameof(SourceLanguageIndex));
                    OnPropertyChanged(nameof(TargetLanguageIndex));
                    OnPropertyChanged(nameof(ActiveSubscriptionStatus));
                    OnPropertyChanged(nameof(AudioSourceModeIndex));
                    OnPropertyChanged(nameof(AudioDevices));
                    OnPropertyChanged(nameof(SelectedAudioDevice));
                    OnPropertyChanged(nameof(IsAudioSourceSelectionEnabled));
                    OnPropertyChanged(nameof(IsAudioDeviceSelectionEnabled));
                    OnPropertyChanged(nameof(IsAudioDeviceRefreshEnabled));
                    OnPropertyChanged(nameof(OutputDevices));
                    OnPropertyChanged(nameof(SelectedOutputDevice));
                    OnPropertyChanged(nameof(IsOutputDeviceSelectionEnabled));
                    OnPropertyChanged(nameof(IsRecordingLoopbackOnly));
                    OnPropertyChanged(nameof(IsRecordingLoopbackMix));
                    OnPropertyChanged(nameof(IsInputRecognitionEnabled));
                    OnPropertyChanged(nameof(IsOutputRecognitionEnabled));
                    OnPropertyChanged(nameof(IsInputDeviceUiEnabled));
                    OnPropertyChanged(nameof(IsOutputDeviceUiEnabled));

                    ForceUpdateComboBoxSelection();

                    ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();

                    // Apply default font size from config
                    Controls.AdvancedRichTextBox.DefaultFontSizeValue = _config.DefaultFontSize;

                    NormalizeSpeechSubtitleOption();
                    OnPropertyChanged(nameof(IsSpeechSubtitleOptionEnabled));
                    OnPropertyChanged(nameof(UseSpeechSubtitleForReview));
                    OnPropertyChanged(nameof(SpeechSubtitleOptionStatusText));
                    OnPropertyChanged(nameof(BatchStartButtonText));
                    RebuildReviewSheets();
                    AiInsight.UpdateConfig();
                    if (GenerateSpeechSubtitleCommand is RelayCommand speechCmd)
                    {
                        speechCmd.RaiseCanExecuteChanged();
                    }
                    if (GenerateBatchSpeechSubtitleCommand is RelayCommand batchCmd)
                    {
                        batchCmd.RaiseCanExecuteChanged();
                    }

                    StatusMessage = $"配置已加载，文件位置: {_configService.GetConfigFilePath()}";
                });

                MarkConfigLoaded();
            }
            catch (Exception ex)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusMessage = $"加载配置失败: {ex.Message}";
                });

                MarkConfigLoaded();
            }
        }

        private void UpdateSubscriptionNames()
        {
            _subscriptionNames.Clear();

            foreach (var subscription in _config.Subscriptions)
            {
                var displayName = $"{subscription.Name} ({subscription.ServiceRegion})";
                _subscriptionNames.Add(displayName);
            }
        }

        private void TriggerSubscriptionValidation()
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

        private void QueueConfigSave(string reason)
        {
            var configPath = _configService.GetConfigFilePath();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _configService.SaveConfigAsync(_config).ConfigureAwait(false);
                    AppendBatchDebugLog("ConfigSaved", $"reason='{reason}' path='{configPath}'");
                }
                catch (Exception ex)
                {
                    AppendBatchDebugLog("ConfigSaveFailed",
                        $"reason='{reason}' path='{configPath}' error='{ex.Message}'", isSuccess: false);
                }
            });
        }

        private void OnConfigurationUpdated(object? sender, AzureSpeechConfig updatedConfig)
        {
            _config = updatedConfig;

            UpdateSubscriptionNames();

            if (_config.Subscriptions.Count > 0 && _config.ActiveSubscriptionIndex >= _config.Subscriptions.Count)
            {
                _config.ActiveSubscriptionIndex = _config.Subscriptions.Count - 1;
            }
            else if (_config.Subscriptions.Count == 0 && _config.ActiveSubscriptionIndex != -1)
            {
                _config.ActiveSubscriptionIndex = -1;
            }

            _activeSubscriptionIndex = _config.ActiveSubscriptionIndex;

            _audioSourceModeIndex = AudioSourceModeToIndex(_config.AudioSourceMode);
            NormalizeRecognitionToggles();
            RefreshAudioDevices(persistSelection: false);

            OnPropertyChanged(nameof(SubscriptionNames));
            OnPropertyChanged(nameof(ActiveSubscriptionStatus));
            OnPropertyChanged(nameof(AudioSourceModeIndex));
            OnPropertyChanged(nameof(AudioDevices));
            OnPropertyChanged(nameof(SelectedAudioDevice));
            OnPropertyChanged(nameof(IsAudioSourceSelectionEnabled));
            OnPropertyChanged(nameof(IsAudioDeviceSelectionEnabled));
            OnPropertyChanged(nameof(IsAudioDeviceRefreshEnabled));
            OnPropertyChanged(nameof(OutputDevices));
            OnPropertyChanged(nameof(SelectedOutputDevice));
            OnPropertyChanged(nameof(IsOutputDeviceSelectionEnabled));
            OnPropertyChanged(nameof(IsRecordingLoopbackOnly));
            OnPropertyChanged(nameof(IsRecordingLoopbackMix));
            OnPropertyChanged(nameof(IsInputRecognitionEnabled));
            OnPropertyChanged(nameof(IsOutputRecognitionEnabled));
            OnPropertyChanged(nameof(IsInputDeviceUiEnabled));
            OnPropertyChanged(nameof(IsOutputDeviceUiEnabled));
            NormalizeSpeechSubtitleOption();
            OnPropertyChanged(nameof(IsSpeechSubtitleOptionEnabled));
            OnPropertyChanged(nameof(UseSpeechSubtitleForReview));
            OnPropertyChanged(nameof(SpeechSubtitleOptionStatusText));
            OnPropertyChanged(nameof(BatchStartButtonText));
            RebuildReviewSheets();

            ForceUpdateComboBoxSelection();
            ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
            AiInsight.UpdateConfig();
            ((RelayCommand)GenerateReviewSummaryCommand).RaiseCanExecuteChanged();
            ((RelayCommand)GenerateAllReviewSheetsCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StartBatchCommand).RaiseCanExecuteChanged();
            if (GenerateSpeechSubtitleCommand is RelayCommand speechCmd)
            {
                speechCmd.RaiseCanExecuteChanged();
            }
            if (GenerateBatchSpeechSubtitleCommand is RelayCommand batchCmd)
            {
                batchCmd.RaiseCanExecuteChanged();
            }

            TriggerSubscriptionValidation();

            // AAD 模式下尝试静默登录（不弹窗）
            _ = AiInsight.TrySilentLoginForAiAsync();

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

        public void ForceUpdateComboBoxSelection()
        {
            if (_mainWindow == null) return;

            var comboBox = _mainWindow.FindControl<ComboBox>("SubscriptionComboBox");
            if (comboBox != null && _activeSubscriptionIndex >= 0 && _activeSubscriptionIndex < _subscriptionNames.Count)
            {
                comboBox.SelectedIndex = -1;
                comboBox.SelectedIndex = _activeSubscriptionIndex;
            }
        }
    }
}
