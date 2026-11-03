using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services;

internal interface IVoiceImeToolbar : IAsyncDisposable
{
    Task Ready { get; }
    Task Completion { get; }
    void Update(bool visible, int state, bool stopped, bool error);
    void Close();
}

/// <summary>唯一 owner 的原生浮窗；所有窗口/TSF 观察在独立 STA，不使用 Avalonia dispatcher。</summary>
internal sealed class VoiceImeToolbar : IVoiceImeToolbar
{
    private readonly object _gate = new();
    private readonly Action<int, int> _notify;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    // 函数指针和 callback 必须在 Run 返回、DLL 卸载之前一直存活。x86 callback 同样是 WINAPI。
    private CreateCallback? _callback;
    private Create? _create;
    private UpdateWindow? _update;
    private Run? _run;
    private CloseWindow? _close;
    private IntPtr _handle;
    private bool _closing;
    private (bool Visible, int State, bool Stopped, bool Error) _latest;

    public VoiceImeToolbar(Action<int, int> notify)
    {
        _notify = notify;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "VoiceIme owner toolbar" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task Ready => _ready.Task;
    public Task Completion => _completion.Task;

    public void Update(bool visible, int state, bool stopped, bool error)
    {
        lock (_gate)
        {
            _latest = (visible, state, stopped, error);
            if (!_closing && _handle != IntPtr.Zero)
                _update!(_handle, visible, state, stopped, error); // bridge 仅 Post 到自己的 dispatcher
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            if (_handle != IntPtr.Zero) _close!(_handle);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        await Completion.ConfigureAwait(false); // 不能在线程仍执行 DLL 时 FreeLibrary
        GC.KeepAlive(_thread);
        GC.KeepAlive(_callback);
    }

    private void ThreadMain()
    {
        IntPtr library = IntPtr.Zero;
        var initialized = false;
        try
        {
            if (CoInitializeEx(IntPtr.Zero, 2) < 0) throw new IOException(); // COINIT_APARTMENTTHREADED
            initialized = true; // S_FALSE 也必须配对 CoUninitialize
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "voice-ime",
                Environment.Is64BitProcess ? "win-x64" : "win-x86", "TrueFluentVoiceIme.dll"));
            VoiceImeMaintenance.GuardPath(path);
            library = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x800); // DLL_LOAD_DIR + SYSTEM32，绝对路径
            if (library == IntPtr.Zero) throw new IOException();
            var actual = new StringBuilder(32768);
            if (GetModuleFileNameW(library, actual, actual.Capacity) == 0
                || !string.Equals(Path.GetFullPath(actual.ToString()), path, StringComparison.OrdinalIgnoreCase))
                throw new IOException();
            _create = Export<Create>("VoiceImeToolbarCreate");
            _update = Export<UpdateWindow>("VoiceImeToolbarUpdate");
            _run = Export<Run>("VoiceImeToolbarRun");
            _close = Export<CloseWindow>("VoiceImeToolbarClose");
            _callback = (kind, value, _) => { try { _notify(kind, value); } catch { } };
            var handle = _create(_callback, IntPtr.Zero);
            if (handle == IntPtr.Zero) throw new IOException();
            lock (_gate)
            {
                _handle = handle;
                if (_closing) _close(handle);
                else _update(handle, _latest.Visible, _latest.State, _latest.Stopped, _latest.Error);
            }
            _ready.TrySetResult();
            _run(handle); // bridge Run 负责销毁窗口、撤销 TSF sink 和回收 handle

            T Export<T>(string name) where T : Delegate
            {
                var address = GetProcAddress(library, name);
                if (address == IntPtr.Zero) throw new IOException();
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }
        }
        catch { _ready.TrySetException(new IOException("语音输入浮窗无法启动，请检查输入法组件。")); }
        finally
        {
            lock (_gate) { _closing = true; _handle = IntPtr.Zero; }
            if (initialized) CoUninitialize(); // native Run 已先撤 sink/销毁窗口
            if (library != IntPtr.Zero) FreeLibrary(library);
            _completion.TrySetResult();
            GC.KeepAlive(_callback);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CreateCallback(int kind, int value, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr Create(CreateCallback callback, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void UpdateWindow(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] bool visible,
        int state, [MarshalAs(UnmanagedType.Bool)] bool stopped, [MarshalAs(UnmanagedType.Bool)] bool error);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Run(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CloseWindow(IntPtr handle);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int size);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}