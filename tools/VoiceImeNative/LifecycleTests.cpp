// Exact production service/worker code with a compile-time, EXE-only path injection.
// No registration, HKCU host/config read, audio, cloud, or actual host creation is exercised.
#include <initguid.h> // Define inputscope GUIDs before the first Common.h inclusion.
#include "NativeInstallPath.h"
#include <cstdio>

static std::wstring fixtureMarker;
static bool FixtureMaintenanceActive() { return MaintenanceActiveAtPath(fixtureMarker); }
#define InstallationMaintenanceActive FixtureMaintenanceActive
#define VOICE_IME_OFFLINE_TEST
#include "VoiceIme.cpp"
#include "PipeClient.cpp"
#undef InstallationMaintenanceActive

namespace {
struct OfflineEditAccess {
    static HWND Window(Service* s) { return s->window; }
    static bool Windows(Service* s) { return s->CreateWindows(); }
    static bool Allowed(Service* s) { return s->CheapAllowed(nullptr); }
    static void Arm(Service* s, const std::shared_ptr<PipeRun>& run) {
        s->active = true; s->listening = true; s->foreground = true;
        s->generation = run->generation; s->run = run;
        s->targetBound = true;
    }
    static bool Stopped(Service* s, uint64_t previous) {
        return !s->active && !s->listening && !s->run && s->generation > previous;
    }
    static uint64_t Generation(Service* s) { return s->generation; }
    static bool Listening(Service* s) { return s->active && s->listening; }
    static bool Unbound(Service* s) { return !s->targetBound; }
    static void Drop(Service* s) { s->DropTarget(); }
    static void ReceiveFrame(Service* s, const Frame& frame) { s->Receive(frame); }
};
struct ReadFixture {
    HANDLE pipe;
    std::shared_ptr<PipeRun> run;
    bool result = true;
};
DWORD WINAPI ReadFixtureEntry(void* data) {
    auto* fixture = static_cast<ReadFixture*>(data); BYTE prefix[4]{};
    fixture->result = ReadShared(fixture->pipe, prefix, sizeof(prefix), fixture->run); return 0;
}
bool DuplexFixture() {
    const auto name = L"\\\\.\\pipe\\TrueFluentVoiceIme.Offline." + std::to_wstring(GetCurrentProcessId());
    Handle server(CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 0, nullptr));
    if (server.h == INVALID_HANDLE_VALUE) return false;
    Handle peer(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr));
    if (peer.h == INVALID_HANDLE_VALUE) return false;
    wchar_t ownExe[32768]{};
    if (!GetModuleFileNameW(nullptr, ownExe, 32768) || !TrustedServer(peer.h, ownExe) ||
        TrustedServer(peer.h, L"C:\\fixture\\not-the-server.exe")) return false;
    OVERLAPPED connected{};
    if (!ConnectNamedPipe(server.h, &connected) && GetLastError() != ERROR_PIPE_CONNECTED) return false;
    auto run = std::make_shared<PipeRun>(1, nullptr, L"fixture");
    ReadFixture fixture{peer.h, run};
    Handle thread(CreateThread(nullptr, 0, ReadFixtureEntry, &fixture, 0, nullptr));
    if (!thread.h) return false;
    bool ok = SendSharedCommand(run, 'B', 19);
    BYTE packet[7]{};
    ok = ok && Transfer(server.h, packet, sizeof(packet), false, run->cancel, 2000) &&
        packet[0] == 3 && packet[1] == 0 && packet[2] == 0 && packet[3] == 0 &&
        packet[4] == 'B' && packet[5] == '1' && packet[6] == '9';
    SetEvent(run->cancel);
    // Join before either handle or the reader's OVERLAPPED stack can be destroyed.
    const DWORD joined = WaitForSingleObject(thread.h, 2000);
    if (joined != WAIT_OBJECT_0) { CancelIoEx(peer.h, nullptr); WaitForSingleObject(thread.h, INFINITE); }
    return ok && joined == WAIT_OBJECT_0 && !fixture.result;
}
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) { std::puts("usage: VoiceImeLifecycleTests <absentFixtureMarker> <existingFixtureMarker>"); return 2; }
    unsigned failed = 0;
    auto check = [&](bool ok, const char* name) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failed; };
    check(MaintenancePath(L"Q:\\Fixture\\Program Files") == L"Q:\\Fixture\\Program Files\\TrueFluentPro\\VoiceIme\\maintenance.lock",
        "every version/bitness resolves the same installation-root marker, not a hash folder");
    check(!MaintenanceActiveAtPath(argv[1]) && MaintenanceActiveAtPath(argv[2]) && MaintenanceActiveAtPath(L""),
        "injected paths distinguish absent/present marker and fail closed on unknown root");
    const auto sharedName = NewPipeName();
    check(sharedName == NewPipeName() && sharedName.find(L"TrueFluentPro.VoiceIme.Shared.") == 0 &&
        sharedName.find(L".S-1-") != sharedName.npos, "deterministic current SID/session name; no registry/config read");
    auto queue = std::make_shared<PipeRun>(1, nullptr, L"fixture");
    bool queued = true;
    for (unsigned i = 0; i < 64; ++i) queued = SendSharedCommand(queue, 'B', i + 1) && queued;
    check(queued && queue->commands.size() == 64 && !SendSharedCommand(queue, 'T', 1) &&
        queue->commands.size() == 64 && WaitForSingleObject(queue->cancel, 0) == WAIT_OBJECT_0,
        "64-command bound: overflow cancels only this peer, without blocking or growing queue");
    check(DuplexFixture(), "pending shared read still sends framed command; cancellation drains exact OVERLAPPED");
    fixtureMarker = argv[2]; ModuleInstance = GetModuleHandleW(nullptr);
    {
        ComPtr<Service> service; service.Attach(new Service);
        BOOL eaten = TRUE;
        check(!OfflineEditAccess::Allowed(service.Get()) &&
            service->OnKeyDown(nullptr, VK_F8, 0, &eaten) == S_OK && !eaten &&
            OfflineEditAccess::Generation(service.Get()) == 0,
            "maintenance rejects CheapAllowed and F8 without creating a run or consuming another IME's key");
        if (OfflineEditAccess::Windows(service.Get())) {
            HWND window = OfflineEditAccess::Window(service.Get());
            auto run = std::make_shared<PipeRun>(17, window, L"isolated-never-started");
            PROCESS_INFORMATION pi{};
            check(LaunchHost(run, L"C:\\fixture\\must-not-execute.exe", pi) == HRESULT_FROM_WIN32(ERROR_CANCELLED) &&
                !pi.hProcess && !pi.hThread,
                "worker rechecks maintenance immediately before CreateProcess and creates no child");
            Run(run); // First production worker guard must return before ReadHost().
            check(run->pending == 1, "worker early maintenance exit posts stopped before any HKCU/config access");
            OfflineEditAccess::Arm(service.Get(), run);
            SendMessageW(window, WM_TIMER, 1, 0); // Actual production 500ms timer handler.
            check(OfflineEditAccess::Stopped(service.Get(), 17) &&
                WaitForSingleObject(run->cancel, 0) == WAIT_OBJECT_0 && !run->target,
                "existing WM_TIMER stops active run, advances generation and cancels/disconnects delivery");
            const uint64_t stopped = OfflineEditAccess::Generation(service.Get());
            MSG msg{};
            while (PeekMessageW(&msg, window, FrameMessage, FrameMessage, PM_REMOVE)) DispatchMessageW(&msg);
            check(run->pending == 0 && OfflineEditAccess::Generation(service.Get()) == stopped,
                "queued stale stopped frame is drained without reviving or mutating the stopped generation");
            service->Deactivate();
            fixtureMarker = argv[1];
            auto shared = std::make_shared<PipeRun>(51, window, L"never-started");
            OfflineEditAccess::Arm(service.Get(), shared);
            OfflineEditAccess::Drop(service.Get());
            check(OfflineEditAccess::Listening(service.Get()) && OfflineEditAccess::Unbound(service.Get()) &&
                shared->commands.size() == 1 && shared->commands.front().kind == 'U' && shared->commands.front().generation == 51 &&
                WaitForSingleObject(shared->cancel, 0) == WAIT_TIMEOUT,
                "context DropTarget sends old-generation U and preserves shared audio/connection");
            ++shared->pending;
            { Frame state{shared, 'R', L"", 7, 0}; OfflineEditAccess::ReceiveFrame(service.Get(), state); }
            ++shared->pending;
            { Frame stale{shared, 'P', L"must-not-insert", 7, 51}; OfflineEditAccess::ReceiveFrame(service.Get(), stale); }
            check(OfflineEditAccess::Listening(service.Get()) && OfflineEditAccess::Unbound(service.Get()),
                "background R broadcasts synchronize state; old target partial cannot request an edit");
            service->onKillFocus();
            check(OfflineEditAccess::Listening(service.Get()) && OfflineEditAccess::Unbound(service.Get()) &&
                WaitForSingleObject(shared->cancel, 0) == WAIT_TIMEOUT &&
                shared->commands.size() == 1 && shared->target == window,
                "ordinary kill-focus unbinds target locally while retaining connection, never T/D or audio stop");
        } else check(false, "offline message-only service window");
    }
    check(ModuleObjects == 0, "offline lifecycle releases all native objects; no audio/cloud/host/registration");
    std::printf("RESULT failures=%u; injected fixture paths only; no ProgramFiles marker/config/registration/host/audio/cloud\n", failed);
    return failed ? 1 : 0;
}