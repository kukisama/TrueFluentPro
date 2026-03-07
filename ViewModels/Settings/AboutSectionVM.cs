using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Input;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.ViewModels.Settings
{
    public class AboutSectionVM : SettingsSectionBase
    {
        private readonly IAboutSectionService _aboutService;
        private readonly Action<string> _reportStatus;
        private bool _isAutoUpdateEnabled = true;

        public AboutSectionVM(IAboutSectionService aboutService, Action<string> reportStatus)
        {
            _aboutService = aboutService;
            _reportStatus = reportStatus;

            _aboutService.PropertyChanged += OnAboutServicePropertyChanged;

            ShowHelpCommand = new RelayCommand(async _ => await _aboutService.ShowHelpAsync(_reportStatus));
            ShowAboutCommand = new RelayCommand(async _ => await _aboutService.ShowAboutAsync(_reportStatus));
            OpenProjectGitHubCommand = new RelayCommand(_ => _aboutService.OpenProjectGitHub(_reportStatus));
            OpenAzureSpeechPortalCommand = new RelayCommand(_ => _aboutService.OpenAzureSpeechPortal(_reportStatus));
            Open21vAzureSpeechPortalCommand = new RelayCommand(_ => _aboutService.Open21vAzureSpeechPortal(_reportStatus));
            OpenStoragePortalCommand = new RelayCommand(_ => _aboutService.OpenStoragePortal(_reportStatus));
            Open21vStoragePortalCommand = new RelayCommand(_ => _aboutService.Open21vStoragePortal(_reportStatus));
            OpenFoundryPortalCommand = new RelayCommand(_ => _aboutService.OpenFoundryPortal(_reportStatus));
            CheckForUpdateCommand = new RelayCommand(
                async _ => await _aboutService.CheckForUpdateAsync(silent: false, IsAutoUpdateEnabled, _reportStatus),
                _ => !IsDownloading);
            DownloadAndApplyUpdateCommand = new RelayCommand(
                async _ => await _aboutService.DownloadAndApplyUpdateAsync(_reportStatus),
                _ => IsUpdateAvailable && !IsDownloading);
        }

        public bool IsAutoUpdateEnabled { get => _isAutoUpdateEnabled; set => Set(ref _isAutoUpdateEnabled, value); }
        public string AppVersion => _aboutService.AppVersion;
        public bool IsUpdateAvailable => _aboutService.IsUpdateAvailable;
        public string UpdateVersionText => _aboutService.UpdateVersionText;
        public bool IsDownloading => _aboutService.IsDownloading;
        public double DownloadProgress => _aboutService.DownloadProgress;

        public ICommand ShowHelpCommand { get; }
        public ICommand ShowAboutCommand { get; }
        public ICommand OpenProjectGitHubCommand { get; }
        public ICommand OpenAzureSpeechPortalCommand { get; }
        public ICommand Open21vAzureSpeechPortalCommand { get; }
        public ICommand OpenStoragePortalCommand { get; }
        public ICommand Open21vStoragePortalCommand { get; }
        public ICommand OpenFoundryPortalCommand { get; }
        public ICommand CheckForUpdateCommand { get; }
        public ICommand DownloadAndApplyUpdateCommand { get; }

        public override void LoadFrom(AzureSpeechConfig config)
        {
            IsAutoUpdateEnabled = config.IsAutoUpdateEnabled;
        }

        public override void ApplyTo(AzureSpeechConfig config)
        {
            config.IsAutoUpdateEnabled = IsAutoUpdateEnabled;
        }

        public Task CheckForUpdateOnStartupAsync()
            => _aboutService.CheckForUpdateAsync(silent: true, IsAutoUpdateEnabled, _reportStatus);

        private void OnAboutServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName))
            {
                return;
            }

            OnPropertyChanged(e.PropertyName);

            if (e.PropertyName is nameof(IsDownloading) or nameof(IsUpdateAvailable))
            {
                (CheckForUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DownloadAndApplyUpdateCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
}
