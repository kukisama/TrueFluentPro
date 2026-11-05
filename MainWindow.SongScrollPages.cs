using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.ViewModels;
using TrueFluentPro.Views;

namespace TrueFluentPro;

public partial class MainWindow
{
    // 业务归本窗口所有，不归某一棵外观树所有。切主题只创建呈现，绝不复制业务。
    private MediaStudioViewModel? _mediaStudioBusiness;
    private MediaCenterV2ViewModel? _mediaCenterBusiness;
    private AudioLabViewModel? _audioLabBusiness;
    private TaskQueueMonitorViewModel? _taskMonitorBusiness;
    private bool _isClosingBusinessPages;

    private string GetPresentationCacheKey(string tag)
        => IsSongScrollAppearance && IsSongScrollBusinessPage(tag) ? $"song-scroll:{tag}" : tag;

    private static bool IsSongScrollBusinessPage(string tag) => tag is
        MainWindowViewModel.NavTagMedia or MainWindowViewModel.NavTagMediaV2 or
        MainWindowViewModel.NavTagAudioLab or MainWindowViewModel.NavTagTaskMonitor or
        MainWindowViewModel.NavTagSettings;

    private bool TryGetOrCreateSongScrollPage(string tag, out Control page)
    {
        page = null!;
        if (!IsSongScrollAppearance || !IsSongScrollBusinessPage(tag)) return false;

        var key = GetPresentationCacheKey(tag);
        if (!_pageCache.TryGetValue(key, out var cached))
        {
            cached = tag switch
            {
                MainWindowViewModel.NavTagMedia => new SongScrollMediaStudioView(),
                MainWindowViewModel.NavTagMediaV2 => new SongScrollMediaCenterV2View(),
                MainWindowViewModel.NavTagAudioLab => new SongScrollAudioLabView(),
                MainWindowViewModel.NavTagTaskMonitor => new SongScrollTaskQueueMonitorView(),
                _ => new SongScrollSettingsView()
            };
            var shell = ((ISongScrollPage)cached).Shell;
            shell.NavigationRequested += ShowPage;
            shell.ThemeRequested += ShowAppearanceMenu;
            _pageCache[key] = cached;
        }

        UpdatePageDataContext(cached);
        page = cached;
        return true;
    }

    private void BindBusinessPage(Control page)
    {
        switch (page)
        {
            case MediaStudioView studio when _mediaStudioBusiness != null:
                if (!ReferenceEquals(studio.ViewModel, _mediaStudioBusiness))
                    studio.BindViewModel(_mediaStudioBusiness);
                break;
            case MediaCenterV2View media when _mediaCenterBusiness != null:
                if (!ReferenceEquals(media.ViewModel, _mediaCenterBusiness))
                    media.BindViewModel(_mediaCenterBusiness);
                break;
            case AudioLabView audio:
                audio.OpenTranscriptChatAsync = OpenTranscriptChatAsync;
                if (_audioLabBusiness != null && !ReferenceEquals(audio.ViewModel, _audioLabBusiness))
                    audio.BindViewModel(_audioLabBusiness);
                break;
            case TaskQueueMonitorView task when _taskMonitorBusiness != null:
                if (!ReferenceEquals(task.DataContext, _taskMonitorBusiness))
                    task.DataContext = _taskMonitorBusiness;
                break;
        }
    }

    private void ScheduleBusinessPageInitialization(Control page)
    {
        if (page is not (MediaStudioView or MediaCenterV2View or AudioLabView or TaskQueueMonitorView))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            // 快速切主题/导航后，不在已隐藏的旧树上为了借 VM 执行 Initialize。
            if (_isClosingBusinessPages || !ReferenceEquals(_currentPage, page) || !page.IsVisible)
                return;
            switch (page)
            {
                case MediaStudioView studio:
                    EnsureMediaStudioInitialized(studio);
                    break;
                case MediaCenterV2View media:
                    EnsureMediaCenterInitialized(media);
                    break;
                case AudioLabView audio:
                    EnsureAudioLabInitialized(audio);
                    break;
                case TaskQueueMonitorView task:
                    EnsureTaskMonitorInitialized(task);
                    break;
            }
        }, DispatcherPriority.Background);
    }

    private void EnsureMediaStudioInitialized(MediaStudioView studio)
    {
        if (_isClosingBusinessPages || _viewModel == null) return;
        if (!_mediaStudioInitialized)
        {
            var config = _viewModel.ConfigVM.Config;
            studio.Initialize(
                config.AiConfig ?? new AiConfig(), config.MediaGenConfig, config.Endpoints.ToList(),
                App.Services.GetRequiredService<IModelRuntimeResolver>(),
                App.Services.GetRequiredService<IAzureTokenProviderStore>(),
                App.Services.GetRequiredService<ConfigurationService>(),
                () => _viewModel.ConfigVM.Config,
                OnBusinessConfigUpdated);
            _mediaStudioBusiness = studio.ViewModel
                ?? throw new InvalidOperationException("创作工坊初始化未完成，请稍后重试。");
            _mediaStudioInitialized = true;
        }
        BindBusinessPage(studio);
    }

    private void EnsureMediaCenterInitialized(MediaCenterV2View media)
    {
        if (_isClosingBusinessPages || _viewModel == null) return;
        if (!_mediaCenterV2Initialized)
        {
            var config = _viewModel.ConfigVM.Config;
            media.Initialize(
                config.AiConfig ?? new AiConfig(), config.MediaGenConfig, config.Endpoints.ToList(),
                App.Services.GetRequiredService<IModelRuntimeResolver>(),
                App.Services.GetRequiredService<IAzureTokenProviderStore>(),
                App.Services.GetRequiredService<ConfigurationService>(),
                () => _viewModel.ConfigVM.Config,
                OnBusinessConfigUpdated);
            _mediaCenterBusiness = media.ViewModel
                ?? throw new InvalidOperationException("媒体中心初始化未完成，请稍后重试。");
            _mediaCenterV2Initialized = true;
        }
        BindBusinessPage(media);
    }

    private void EnsureAudioLabInitialized(AudioLabView audio)
    {
        if (_isClosingBusinessPages || _viewModel == null) return;
        if (!_audioLabInitialized)
        {
            var pipeline = App.Services.GetRequiredService<AudioLifecyclePipelineService>();
            var tokenStore = App.Services.GetRequiredService<IAzureTokenProviderStore>();
            var configService = App.Services.GetRequiredService<ConfigurationService>();
            Func<AzureSpeechConfig> configProvider = () => _viewModel.ConfigVM.Config ?? new AzureSpeechConfig();
            var controlPanel = new AudioLabControlPanelViewModel(
                pipeline, App.Services.GetRequiredService<Services.Speech.SpeechSynthesisService>(),
                configProvider, tokenStore, configService);
            audio.Initialize(
                App.Services.GetRequiredService<IAiInsightService>(), tokenStore,
                App.Services.GetRequiredService<IModelRuntimeResolver>(),
                App.Services.GetRequiredService<ISpeechResourceRuntimeResolver>(),
                App.Services.GetRequiredService<IAiAudioTranscriptionService>(),
                configProvider, configService, pipeline, controlPanel,
                App.Services.GetRequiredService<IAudioLabExportService>(),
                App.Services.GetRequiredService<IDesktopFolderLauncher>(),
                App.Services.GetRequiredService<IAudioTaskQueueService>(),
                App.Services.GetRequiredService<ITaskEventBus>());
            _audioLabBusiness = audio.ViewModel
                ?? throw new InvalidOperationException("听析中心初始化未完成，请稍后重试。");
            _audioLabInitialized = true;
        }
        BindBusinessPage(audio);
    }

    private void EnsureTaskMonitorInitialized(TaskQueueMonitorView task)
    {
        if (_isClosingBusinessPages || _viewModel == null) return;
        if (!_taskMonitorInitialized)
        {
            _taskMonitorBusiness = new TaskQueueMonitorViewModel(
                App.Services.GetRequiredService<IAudioTaskQueueService>(),
                App.Services.GetRequiredService<ITaskEventBus>(),
                App.Services.GetRequiredService<IAudioLibraryRepository>(),
                App.Services.GetRequiredService<IAudioTaskExecutor>(),
                App.Services.GetRequiredService<ITaskExecutionRepository>());
            _taskMonitorInitialized = true;
        }
        BindBusinessPage(task);
    }

    private void OnBusinessConfigUpdated(AzureSpeechConfig config)
    {
        if (_isClosingBusinessPages || _viewModel == null) return;
        _viewModel.ConfigVM.SetConfig(config);
        _viewModel.Settings.Initialize(config);
    }

    private static void RemovePageFromHost(Control page)
    {
        // 工坊可能在 PageHost、SongScrollHost 或字幕问答窗口中。
        switch (page.Parent)
        {
            case Panel panel:
                panel.Children.Remove(page);
                break;
            case ContentControl content when ReferenceEquals(content.Content, page):
                content.Content = null;
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, page):
                decorator.Child = null;
                break;
        }
    }

    private void DisposeBusinessPages()
    {
        foreach (var page in _pageCache.Values)
        {
            if (page is ISongScrollPage scroll)
            {
                scroll.Shell.NavigationRequested -= ShowPage;
                scroll.Shell.ThemeRequested -= ShowAppearanceMenu;
            }

            // 不调用 Cleanup：它属于旧的“呈现 + 业务”联合释放入口。
            // UnbindViewModel 只解除 UI 订阅与热键，不能 Dispose VM。
            switch (page)
            {
                case MediaStudioView studio:
                    studio.UnbindViewModel();
                    break;
                case MediaCenterV2View media:
                    media.UnbindViewModel();
                    break;
                case AudioLabView audio:
                    audio.OpenTranscriptChatAsync = null;
                    // AudioLab 在 DataContextChanged / Unloaded 中解除 FilePanelStateChanged。
                    audio.DataContext = null;
                    break;
            }
            if (page is MediaStudioView or MediaCenterV2View or AudioLabView or TaskQueueMonitorView or SettingsView)
            {
                page.IsVisible = false;
                RemovePageFromHost(page);
                page.DataContext = null;
            }
        }

        // AudioLab.Dispose 释放两个播放器，TaskMonitor.Dispose 停计时器并退订事件总线。
        // 这些接口已经由业务实现提供；不根据外观缓存的数量重复调用。
        _taskMonitorBusiness?.Dispose();
        _taskMonitorBusiness = null;
        _audioLabBusiness?.Dispose();
        _audioLabBusiness = null;
        _mediaStudioBusiness?.Dispose();
        _mediaStudioBusiness = null;
        _mediaCenterBusiness?.Dispose();
        _mediaCenterBusiness = null;
    }
}