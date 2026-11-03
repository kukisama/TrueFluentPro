#include "Common.h"
#include "NativeInstallPath.h"
#include <sddl.h>

namespace {
struct Handle {
    HANDLE h = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE value = INVALID_HANDLE_VALUE) : h(value) {}
    ~Handle() { if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h); }
    Handle(const Handle&) = delete;
};
bool Cancelled(const std::shared_ptr<PipeRun>& run) { return WaitForSingleObject(run->cancel, 0) == WAIT_OBJECT_0; }
bool Post(const std::shared_ptr<PipeRun>& run, char kind, std::wstring text = {}, uint64_t epoch = 0, uint64_t target = 0) {
    std::lock_guard<std::mutex> guard(run->delivery);
    if (!run->target || run->pending >= 64) return false;
    ++run->pending;
    auto* frame = new (std::nothrow) Frame{run, kind, std::move(text), epoch, target};
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
// Only the worker writes. An outstanding read has independent OVERLAPPED storage.
bool WritePacket(HANDLE pipe, char kind, uint64_t value, HANDLE cancel) noexcept try {
    const std::string payload = std::to_string(value);
    const DWORD size = static_cast<DWORD>(payload.size() + 1);
    std::vector<char> bytes{static_cast<char>(size), static_cast<char>(size >> 8),
        static_cast<char>(size >> 16), static_cast<char>(size >> 24), kind};
    bytes.insert(bytes.end(), payload.begin(), payload.end());
    return Transfer(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), true, cancel, 1000);
} catch (...) { return false; }
bool FlushCommands(HANDLE pipe, const std::shared_ptr<PipeRun>& run) {
    for (;;) {
        PipeRun::Command command{};
        {
            std::lock_guard<std::mutex> guard(run->commandMutex);
            if (Cancelled(run)) return false;
            if (run->commands.empty()) { ResetEvent(run->commandReady); return true; }
            command = run->commands.front(); run->commands.pop_front();
        }
        if (!WritePacket(pipe, command.kind, command.generation, run->cancel)) return false;
        if (command.kind == 'D') run->deselection = 0;
    }
}
bool ReadShared(HANDLE pipe, void* data, DWORD size, const std::shared_ptr<PipeRun>& run) {
    auto* bytes = static_cast<BYTE*>(data);
    while (size && !Cancelled(run)) {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event.h || !FlushCommands(pipe, run)) return false;
        OVERLAPPED ov{}; ov.hEvent = event.h;
        DWORD count = 0;
        if (!ReadFile(pipe, bytes, size, &count, &ov)) {
            if (GetLastError() != ERROR_IO_PENDING) return false;
            bool completed = false;
            while (!completed) {
                HANDLE waits[] = {run->cancel, event.h, run->commandReady};
                const DWORD ready = WaitForMultipleObjects(3, waits, FALSE, INFINITE);
                if (ready == WAIT_OBJECT_0 + 1) {
                    completed = true;
                    if (!GetOverlappedResult(pipe, &ov, &count, FALSE)) return false;
                } else if (ready != WAIT_OBJECT_0 + 2 || !FlushCommands(pipe, run)) {
                    CancelIoEx(pipe, &ov);
                    GetOverlappedResult(pipe, &ov, &count, TRUE);
                    return false;
                }
            }
        }
        if (!count) return false;
        bytes += count; size -= count;
    }
    return size == 0 && !Cancelled(run);
}
bool Identity(HANDLE process, DWORD& session, std::wstring& sid) {
    Handle token;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token.h)) return false;
    DWORD needed = 0;
    if (!GetTokenInformation(token.h, TokenSessionId, &session, sizeof(session), &needed)) return false;
    GetTokenInformation(token.h, TokenUser, nullptr, 0, &needed);
    if (!needed) return false;
    std::vector<BYTE> bytes(needed);
    if (!GetTokenInformation(token.h, TokenUser, bytes.data(), needed, &needed)) return false;
    LPWSTR value = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(bytes.data())->User.Sid, &value)) return false;
    sid = value; LocalFree(value); return true;
}
bool TrustedServer(HANDLE pipe, const std::wstring& host) {
    ULONG pid = 0;
    if (!GetNamedPipeServerProcessId(pipe, &pid)) return false;
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (!process.h) return false;
    wchar_t path[32768]{}; DWORD count = 32768;
    if (!QueryFullProcessImageNameW(process.h, 0, path, &count) || _wcsicmp(path, host.c_str())) return false;
    DWORD ours = 0, theirs = 0; std::wstring ownSid, serverSid;
    return Identity(GetCurrentProcess(), ours, ownSid) && Identity(process.h, theirs, serverSid) &&
        ours == theirs && ownSid == serverSid;
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
    std::wstring command = QuoteArgument(host) + L" --voice-ime-host --shared";
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
    Handle pipe;
    ULONGLONG deadline = GetTickCount64() + 10000;
    std::wstring path = L"\\\\.\\pipe\\" + run->pipeName;
    bool launched = false;
    while (!Cancelled(run) && !InstallationMaintenanceActive() && GetTickCount64() < deadline) {
        pipe.h = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe.h != INVALID_HANDLE_VALUE) break;
        DWORD error = GetLastError();
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PIPE_BUSY) break;
        if (!launched) {
            PROCESS_INFORMATION pi{};
            HRESULT launch = LaunchHost(run, host, pi);
            // Shared owner handles are only closed, never terminated or parent-bound.
            Handle process(pi.hProcess), thread(pi.hThread);
            if (FAILED(launch)) break;
            launched = true;
        }
        WaitForSingleObject(run->cancel, 40);
    }
    if (pipe.h != INVALID_HANDLE_VALUE) {
        if (!InstallationMaintenanceActive() && TrustedServer(pipe.h, host) && WritePacket(pipe.h, 'H', run->threadId, run->cancel)) {
            while (!Cancelled(run)) {
                BYTE prefix[4]{};
                if (!ReadShared(pipe.h, prefix, 4, run)) break;
                DWORD size = DWORD(prefix[0]) | (DWORD(prefix[1]) << 8) | (DWORD(prefix[2]) << 16) | (DWORD(prefix[3]) << 24);
                if (size < 1 || size > MaximumFrame) break;
                std::vector<char> frame(size);
                if (!ReadShared(pipe.h, frame.data(), size, run)) break;
                char kind = frame[0]; std::wstring payload, text; uint64_t epoch = 0, target = 0;
                if (!DecodeUtf8(frame.data() + 1, size - 1, payload) || !SharedPayload(kind, payload, epoch, target, text)) break;
                if (epoch > run->audioEpoch.load()) run->audioEpoch = epoch;
                // Never carry untrusted backend error strings into UI/logs.
                if (!Post(run, kind, std::move(text), epoch, target)) break;
            }
            // Only a previously queued, confirmed profile change survives teardown.
            // Ordinary EOF/focus loss still never stops the shared audio session.
            const auto deselection = run->deselection.exchange(0);
            if (deselection) WritePacket(pipe.h, 'D', deselection, nullptr);
        }
    }
    // EOF/cancel affects this connection only, never sends stop to the owner.
    if (!Cancelled(run)) Post(run, 'E');
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
    DWORD session = 0; std::wstring sid;
    if (!Identity(GetCurrentProcess(), session, sid)) return {};
    return L"TrueFluentPro.VoiceIme.Shared." + std::to_wstring(session) + L"." + sid;
}
bool SendSharedCommand(const std::shared_ptr<PipeRun>& run, char kind, uint64_t generation) try {
    if (!run || !SharedCommandKind(kind) || !run->cancel || !run->commandReady) return false;
    std::lock_guard<std::mutex> guard(run->commandMutex);
    if (Cancelled(run) || run->done) return false;
    if (kind == 'D') run->deselection = generation;
    if (run->commands.size() >= 64) { SetEvent(run->cancel); return false; }
    run->commands.push_back({kind, generation}); SetEvent(run->commandReady); return true;
} catch (...) {
    if (run && run->cancel) SetEvent(run->cancel);
    return false;
}
bool StartPipeWorker(const std::shared_ptr<PipeRun>& run) {
    HMODULE module{};
    if (!run->cancel || !run->commandReady || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(&StartPipeWorker), &module)) return false;
    auto* work = new (std::nothrow) Work{run, module};
    if (!work) { FreeLibrary(module); return false; }
    ++ModuleObjects;
    HANDLE thread = CreateThread(nullptr, 0, Entry, work, 0, nullptr);
    if (!thread) { --ModuleObjects; delete work; FreeLibrary(module); return false; }
    CloseHandle(thread); return true;
}