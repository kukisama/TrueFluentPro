using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TrueFluentPro.ViewModels;

public partial class MainWindowViewModel
{
    private bool _songScrollConnecting, _songScrollStopping, _songScrollCancel;
    private bool _songScrollFrameGuard, _songScrollIgnoreFrames;
    // 正文会话独立于浮动字幕的重连/配置刷新；不能因浮窗重置丢掉当前服务的正常识别。
    private long _songScrollFrameEpoch;
    private ICommand? _songScrollToggle;
    public bool IsSongScrollConnecting => _songScrollConnecting;
    public string SongScrollToggleText => _songScrollStopping ? "正在停止" : _songScrollConnecting
        ? (_songScrollCancel ? "正在取消" : "取消连接") : TranslationToggleButtonText;
    public ICommand SongScrollToggleCommand => _songScrollToggle ??= new RelayCommand(
        async _ => await ToggleSongScrollTranslationAsync(),
        _ => !_songScrollStopping && (!_songScrollCancel || !_songScrollConnecting));
    private void NotifySongScrollState()
    {
        OnPropertyChanged(nameof(IsSongScrollConnecting)); OnPropertyChanged(nameof(SongScrollToggleText));
        (_songScrollToggle as RelayCommand)?.RaiseCanExecuteChanged();
    }
    public async Task ToggleSongScrollTranslationAsync()
    {
        if (_songScrollStopping) return;
        if (_songScrollConnecting)
        {
            _songScrollCancel = true;
            _songScrollIgnoreFrames = true;
            StatusMessage = "已请求取消连接，等待语音服务启动收尾后停止";
            NotifySongScrollState();
            return;
        }
        if (IsTranslating)
        {
            _songScrollIgnoreFrames = true;
            _songScrollStopping = true; NotifySongScrollState();
            try
            {
                if (_translationService != null) await _translationService.StopTranslationAsync();
                IsTranslating = false; ConfigVM.IsConfigurationEnabled = true;
                AudioDevices.SetAudioLevel(0); AudioDevices.ResetAudioLevelHistory(); StatusMessage = "已停止翻译";
            }
            catch (Exception ex) { StatusMessage = $"停止翻译未完成，请重试：{ex.Message}"; }
            finally { _songScrollStopping = false; NotifySongScrollState(); }
            return;
        }
        if (!EnsureRealtimeTranslationService(out var error) || _translationService == null)
        { StatusMessage = error; return; }
        var service = _translationService;
        _songScrollConnecting = true; _songScrollCancel = false;
        System.Threading.Interlocked.Increment(ref _songScrollFrameEpoch);
        _songScrollFrameGuard = true; _songScrollIgnoreFrames = false;
        ConfigVM.IsConfigurationEnabled = false; StatusMessage = "正在连接语音服务…"; NotifySongScrollState();
        try
        {
            ResetFloatingSubtitleSession();
            var started = await service.StartTranslationAsync();
            if (_songScrollCancel)
            {
                // 服务没有启动 CT 契约：必须等待真实 Start 返回，再停止，不能假称已即时取消。
                await service.StopTranslationAsync();
                IsTranslating = false; ResetFloatingSubtitleSession();
                StatusMessage = "连接已取消，语音服务已停止";
            }
            else { IsTranslating = started; if (started) StatusMessage = "正在翻译…"; }
        }
        catch (Exception ex) { IsTranslating = false; StatusMessage = $"连接未完成，请重试：{ex.Message}"; }
        finally
        {
            _songScrollConnecting = false; _songScrollCancel = false;
            ConfigVM.IsConfigurationEnabled = !IsTranslating; NotifySongScrollState();
        }
    }
}