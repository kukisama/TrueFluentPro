using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TrueFluentPro.Services;

/// <summary>聊天可见期间注册 F8，全局 WM_HOTKEY，不使用键盘钩子。</summary>
public sealed class ChatVoiceInputHotkey : IDisposable
{
    private const int HotkeyId = 0x5F08;
    private const uint WmHotkey = 0x0312;
    private const uint NoRepeat = 0x4000;
    private const uint VkF8 = 0x77;
    private readonly Window _window;
    private readonly Action _toggle;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private IntPtr _handle;
    private bool _disposed;
    public bool IsRegistered { get; private set; }
    public event Action<string>? HintChanged;

    public ChatVoiceInputHotkey(Window window, Action toggle)
    {
        _window = window;
        _toggle = toggle;
        _hook = OnWindowMessage;
        VoiceImeRegistrationService.RegistrationChanged += OnImeRegistrationChanged;
    }

    public bool TryRegister(out string hint)
    {
        if (VoiceImeRegistrationService.HasNativeRegistration())
        {
            ReleaseNativeHotkey();
            hint = "F8 由系统语音输入法处理；聊天听写请使用麦克风按钮";
            return false;
        }
        if (IsRegistered)
        {
            hint = "F8 全局热键 · 开始/停止语音输入";
            return true;
        }
        if (_disposed || !OperatingSystem.IsWindows())
        {
            hint = "F8 开始/停止语音输入（聊天窗口内）";
            return false;
        }
        _handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_handle == IntPtr.Zero)
        {
            hint = "F8 暂未就绪，可点击麦克风输入";
            return false;
        }
        Win32Properties.AddWndProcHookCallback(_window, _hook);
        if (!RegisterHotKey(_handle, HotkeyId, NoRepeat, VkF8))
        {
            Win32Properties.RemoveWndProcHookCallback(_window, _hook);
            hint = "F8 全局注册失败（可能被占用）；可点麦克风或在聊天内按 F8";
            return false;
        }
        IsRegistered = true;
        hint = "F8 全局热键 · 开始/停止语音输入";
        return true;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId && IsRegistered && !_disposed)
        {
            handled = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && IsRegistered) _toggle();
            });
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        VoiceImeRegistrationService.RegistrationChanged -= OnImeRegistrationChanged;
        ReleaseNativeHotkey();
    }

    private void OnImeRegistrationChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        TryRegister(out var hint);
        HintChanged?.Invoke(hint);
    });

    private void ReleaseNativeHotkey()
    {
        if (IsRegistered)
        {
            UnregisterHotKey(_handle, HotkeyId);
            Win32Properties.RemoveWndProcHookCallback(_window, _hook);
            IsRegistered = false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}