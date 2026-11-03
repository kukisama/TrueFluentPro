#include "Common.h"
#include "NativeInstallPath.h"

namespace {
struct Handle {
    HANDLE h = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE value = INVALID_HANDLE_VALUE) : h(value) {}
    ~Handle() { if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h); }
    Handle(const Handle&) = delete;
};
struct ChildProcess : Handle {
    explicit ChildProcess(HANDLE value) : Handle(value) {}
    ~ChildProcess() {
        // Runs on the worker, including exception paths. Never terminate unrelated host processes.
        if (h && h != INVALID_HANDLE_VALUE && WaitForSingleObject(h, 2000) == WAIT_TIMEOUT)
            TerminateProcess(h, ERROR_CANCELLED);
    }
};
bool Cancelled(const std::shared_ptr<PipeRun>& run) { return WaitForSingleObject(run->cancel, 0) == WAIT_OBJECT_0; }
bool Post(const std::shared_ptr<PipeRun>& run, char kind, std::wstring text = {}) {
    std::lock_guard<std::mutex> guard(run->delivery);
    if (!run->target || run->pending >= 64) return false;
    ++run->pending;
    auto* frame = new (std::nothrow) Frame{run, kind, std::move(text)};
    if (!frame) { --run->pending; return false; }
    if (!PostMessageW(run->target, FrameMessage, 0, reinterpret_cast<LPARAM>(frame))) { delete frame; return false; }
    return true;
}
bool Transfer(HANDLE pipe, void* data, DWORD size, bool write, HANDLE cancel, DWORD timeout) {
    auto* bytes = static_cast<BYTE*>(data);
    while (size) {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.h) return false;
        OVERLAPPED ov{}; ov.hEvent = event.h;
        DWORD count = 0;
        BOOL ok = write ? WriteFile(pipe, bytes, size, &count, &ov) : ReadFile(pipe, bytes, size, &count, &ov);
        if (!ok) {
            if (GetLastError() != ERROR_IO_PENDING) return false;
            HANDLE waits[] = {event.h, cancel};
            DWORD ready = WaitForMultipleObjects(cancel ? 2 : 1, waits, FALSE, timeout);
            if (ready != WAIT_OBJECT_0) {
                CancelIoEx(pipe, &ov);
                // Drain this exact OVERLAPPED before its stack/event storage is destroyed.
                GetOverlappedResult(pipe, &ov, &count, TRUE);
                return false;
            }
            if (!GetOverlappedResult(pipe, &ov, &count, FALSE)) return false;
        }
        if (!count) return false;
        bytes += count; size -= count;
    }
    return true;
}
std::wstring ReadHost() {
    // This function is never used by isolated self-tests.
    for (REGSAM view : {KEY_WOW64_64KEY, KEY_WOW64_32KEY}) {
        HKEY key{};
        if (RegOpenKeyExW(HKEY_CURRENT_USER, ConfigKey, 0, KEY_QUERY_VALUE | view, &key)) continue;
        wchar_t value[32768]{}; DWORD size = sizeof(value), type = 0;
        LONG result = RegQueryValueExW(key, L"HostExe", nullptr, &type, reinterpret_cast<BYTE*>(value), &size);
        RegCloseKey(key);
        if (!result && type == REG_SZ && size >= 2 && size <= sizeof(value) && value[size / 2 - 1] == 0 && AbsoluteFile(value)) return value;
    }
    return {};
}
HRESULT LaunchHost(const std::shared_ptr<PipeRun>& run, const std::wstring& host, PROCESS_INFORMATION& pi) {
    std::wstring command = QuoteArgument(host) + L" --voice-ime-host --pipe " + QuoteArgument(run->pipeName)
        + L" --parent " + std::to_wstring(GetCurrentProcessId());
    STARTUPINFOW si{}; si.cb = sizeof(si); pi = {};
    // Recheck immediately before CreateProcess, not just before reading the registered host.
    if (Cancelled(run) || InstallationMaintenanceActive()) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
    if (!CreateProcessW(host.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
        nullptr, nullptr, &si, &pi)) return HRESULT_FROM_WIN32(GetLastError());
    return S_OK;
}
void Run(const std::shared_ptr<PipeRun>& run) {
    if (Cancelled(run) || InstallationMaintenanceActive()) { Post(run, 'S'); return; }
    std::wstring host = ReadHost();
    if (host.empty()) { Post(run, 'E'); return; }
    PROCESS_INFORMATION pi{};
    HRESULT launch = LaunchHost(run, host, pi);
    if (FAILED(launch)) { Post(run, launch == HRESULT_FROM_WIN32(ERROR_CANCELLED) ? 'S' : 'E'); return; }
    ChildProcess process(pi.hProcess);
    Handle thread(pi.hThread), pipe;
    ULONGLONG deadline = GetTickCount64() + 10000;
    std::wstring path = L"\\\\.\\pipe\\" + run->pipeName;
    while (!Cancelled(run) && GetTickCount64() < deadline && WaitForSingleObject(process.h, 0) == WAIT_TIMEOUT) {
        pipe.h = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe.h != INVALID_HANDLE_VALUE) break;
        DWORD error = GetLastError();
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PIPE_BUSY) break;
        WaitForSingleObject(run->cancel, 40);
    }
    bool normalStop = false;
    if (pipe.h != INVALID_HANDLE_VALUE) {
        // Connect only to the per-run process we started, even if another local process races the pipe name.
        ULONG serverPid = 0;
        if (GetNamedPipeServerProcessId(pipe.h, &serverPid) && serverPid == pi.dwProcessId) {
            bool ready = false;
            while (!Cancelled(run)) {
                BYTE prefix[4]{};
                if (!Transfer(pipe.h, prefix, 4, false, run->cancel, ready ? INFINITE : 10000)) break;
                DWORD size = DWORD(prefix[0]) | (DWORD(prefix[1]) << 8) | (DWORD(prefix[2]) << 16) | (DWORD(prefix[3]) << 24);
                if (size < 1 || size > MaximumFrame) break;
                std::vector<char> frame(size);
                if (!Transfer(pipe.h, frame.data(), size, false, run->cancel, ready ? INFINITE : 10000)) break;
                char kind = frame[0]; std::wstring text;
                if (!ValidKind(kind) || !DecodeUtf8(frame.data() + 1, size - 1, text)) break;
                if ((kind == 'P' || kind == 'F') && !ready) break;
                if (kind == 'R') ready = true;
                // Never carry untrusted backend error strings into UI/logs.
                if (!Post(run, kind, kind == 'P' || kind == 'F' ? std::move(text) : std::wstring{})) break;
                if (kind == 'S' || kind == 'E') { normalStop = true; break; }
            }
            char stop[] = "stop\n";
            Transfer(pipe.h, stop, 5, true, nullptr, 500);
        }
        CloseHandle(pipe.h); pipe.h = INVALID_HANDLE_VALUE;
    }
    if (!Cancelled(run) && !normalStop) Post(run, 'E');
}
struct Work { std::shared_ptr<PipeRun> run; HMODULE module; };
DWORD WINAPI Entry(void* data) {
    HMODULE module;
    {
        std::unique_ptr<Work> work(static_cast<Work*>(data)); module = work->module;
        try { Run(work->run); } catch (...) { Post(work->run, 'E'); }
        work->run->done = true;
    }
    --ModuleObjects;
    FreeLibraryAndExitThread(module, 0);
}
}
std::wstring NewPipeName() {
    GUID id{}; if (FAILED(CoCreateGuid(&id))) return {};
    auto hex = GuidString(id);
    std::wstring compact;
    for (wchar_t c : hex) if (c != L'{' && c != L'}' && c != L'-') compact += c;
    return L"TrueFluentPro.VoiceIme." + std::to_wstring(GetCurrentProcessId()) + L"."
        + std::to_wstring(GetCurrentThreadId()) + L"." + compact;
}
bool StartPipeWorker(const std::shared_ptr<PipeRun>& run) {
    HMODULE module{};
    if (!run->cancel || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(&StartPipeWorker), &module)) return false;
    auto* work = new (std::nothrow) Work{run, module};
    if (!work) { FreeLibrary(module); return false; }
    ++ModuleObjects;
    HANDLE thread = CreateThread(nullptr, 0, Entry, work, 0, nullptr);
    if (!thread) { --ModuleObjects; delete work; FreeLibrary(module); return false; }
    CloseHandle(thread); return true;
}