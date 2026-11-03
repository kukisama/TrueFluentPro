using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static Program;

internal static class NativeEntry
{
    // CREATE_SUSPENDED: 在托管入口执行前安装精确 PID 的监听。不会隐藏 GUI 窗口。
    private const uint CreateSuspended = 4, ObjectCreate = 0x8000, ObjectShow = 0x8002;

    internal static void Verify(string executable)
    {
        Check(OperatingSystem.IsWindows(), "真实入口验证要求 Windows，不能用 SKIP 当作 PASS");
        Check(File.Exists(executable), "主项目 Debug apphost 不存在");
        VerifyObserverControl();
        var start = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        Check(CreateProcessW(executable, new StringBuilder('"' + executable + "\" --voice-ime-host"),
            0, 0, false, CreateSuspended, 0, Path.GetDirectoryName(executable)!, ref start, out var process),
            "CreateProcessW 失败，Win32=" + Marshal.GetLastWin32Error());
        var events = new List<object>();
        var unexpectedWindows = 0;
        var hiddenComWindows = 0;
        WinEvent callback = (_, kind, hwnd, objectId, childId, thread, _) =>
        {
            if (hwnd != 0 && objectId == 0 && childId == 0 && kind is ObjectCreate or ObjectShow)
            {
                var className = new StringBuilder(256);
                var caption = new StringBuilder(256);
                var knownClass = GetClassNameW(hwnd, className, className.Capacity) > 0;
                GetWindowTextW(hwnd, caption, caption.Capacity);
                var visible = IsWindowVisible(hwnd);
                // COM 官方 STA 文档明确指定此隐藏辅助类。只排除实测匹配且不可见的 CREATE；
                // SHOW、未知/已销毁类、Avalonia 或 splash 类一律仍算失败。
                var hiddenCom = knownClass && kind == ObjectCreate && !visible
                    && className.ToString() == "OleMainThreadWndClass" && caption.ToString() == "OleMainThreadWndName";
                if (hiddenCom) hiddenComWindows++; else unexpectedWindows++;
                events.Add(new { kind, hwnd = hwnd.ToInt64(), thread, knownClass, className = className.ToString(),
                    caption = caption.ToString(), visible, hiddenCom });
                Note($"ENTRY window kind=0x{kind:X}, class={className}, caption={caption}, visible={visible}, knownClass={knownClass}");
            }
        };
        var keepAlive = GCHandle.Alloc(callback);
        nint hook = 0;
        var exited = false;
        var watch = Stopwatch.StartNew();
        try
        {
            hook = SetWinEventHook(ObjectCreate, ObjectShow, 0, callback, process.ProcessId, 0, 0);
            Check(hook != 0, "无法安装目标进程的窗口监听");
            Check(ResumeThread(process.Thread) == 1, "子进程主线程未正常恢复");
            WaitWithMessages(process.Process, TimeSpan.FromSeconds(10));
            exited = true;
            Pump();
            Check(GetExitCodeProcess(process.Process, out var exit), "不能读取真实入口退出码");
            File.WriteAllText(Path.Combine(Artifacts, "entry.json"), JsonSerializer.Serialize(new
            {
                executable, executableSha256 = Digest(executable), assemblySha256 = Digest(Path.ChangeExtension(executable, ".dll")),
                arguments = new[] { "--voice-ime-host" }, processId = process.ProcessId, exitCode = exit,
                elapsedMs = watch.ElapsedMilliseconds, windowEvents = events, hookInstalledBeforeResume = true,
                unexpectedWindows, hiddenComWindows,
                argumentValidationPrecedesConfigurationRead = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            Check(exit == 2, $"真实无效参数分支 exit={exit}，应为 2");
            Check(unexpectedWindows == 0, "无效参数入口创建/显示了非 COM 辅助窗口，可能启动了 splash/mainwindow");
            Note($"ENTRY actual apphost pid={process.ProcessId}, exit={exit}, guiWindowCreateShow={unexpectedWindows}, hiddenCOM={hiddenComWindows}, elapsedMs={watch.ElapsedMilliseconds}");
        }
        finally
        {
            if (!exited)
            {
                // 只终止本测试以句柄持有的子进程，不按名称清理用户程序。
                if (WaitForSingleObject(process.Process, 0) != 0)
                {
                    Check(TerminateProcess(process.Process, 99), "测试子进程强制收尾失败");
                    Check(WaitForSingleObject(process.Process, 2000) == 0, "测试子进程未在 2s 内退出");
                }
            }
            if (hook != 0) UnhookWinEvent(hook);
            keepAlive.Free();
            CloseHandle(process.Thread);
            CloseHandle(process.Process);
        }
    }

    private static void VerifyObserverControl()
    {
        var creates = 0;
        var shows = 0;
        WinEvent callback = (_, kind, hwnd, objectId, childId, _, _) =>
        {
            if (hwnd == 0 || objectId != 0 || childId != 0) return;
            if (kind == ObjectCreate) creates++;
            if (kind == ObjectShow) shows++;
        };
        var keepAlive = GCHandle.Alloc(callback);
        var hook = SetWinEventHook(ObjectCreate, ObjectShow, 0, callback, (uint)Environment.ProcessId, 0, 0);
        nint window = 0;
        try
        {
            Check(hook != 0, "窗口监听对照组安装失败");
            // 系统内置 STATIC 类，无类注册；1px、屏外、不激活，不创建 Avalonia/主程序窗口。
            window = CreateWindowExW(0x08000000, "STATIC", "VoiceImeRegression observer control", 0x80000000,
                -32000, -32000, 1, 1, 0, 0, 0, 0);
            Check(window != 0, "窗口监听对照组创建失败");
            ShowWindow(window, 4);
            var watch = Stopwatch.StartNew();
            while (creates == 0 || shows == 0)
            {
                Pump();
                if (creates > 0 && shows > 0) break;
                var remaining = 2000 - (int)watch.ElapsedMilliseconds;
                Check(remaining > 0, "窗口监听未观察到对照窗口的真实 CREATE/SHOW");
                var result = MsgWaitForMultipleObjectsEx(0, Array.Empty<nint>(), (uint)remaining, 0x04FF, 4);
                Check(result != uint.MaxValue, "对照组消息等待失败");
            }
            Note($"ENTRY observer control actual CREATE={creates}, SHOW={shows}");
        }
        finally
        {
            if (window != 0) DestroyWindow(window);
            Pump();
            if (hook != 0) UnhookWinEvent(hook);
            keepAlive.Free();
        }
    }

    private static void WaitWithMessages(nint process, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Pump();
            var remaining = (int)(timeout.TotalMilliseconds - watch.Elapsed.TotalMilliseconds);
            Check(remaining > 0, "真实入口未在 10s 内退出");
            var result = MsgWaitForMultipleObjectsEx(1, new[] { process }, (uint)remaining, 0x04FF, 4);
            if (result == 0) return;
            if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            Check(result == 1, "真实入口等待超时/异常");
        }
    }

    private static void Pump()
    {
        while (PeekMessageW(out var message, 0, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEvent(nint hook, uint kind, nint hwnd, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort Show, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes,
        nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint exit);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint exit);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[] handles, uint milliseconds, uint mask, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extended, string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetClassNameW(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetWindowTextW(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}