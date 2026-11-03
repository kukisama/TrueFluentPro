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
    }
    static bool Stopped(Service* s, uint64_t previous) {
        return !s->active && !s->listening && !s->run && s->generation > previous;
    }
    static uint64_t Generation(Service* s) { return s->generation; }
};
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) { std::puts("usage: VoiceImeLifecycleTests <absentFixtureMarker> <existingFixtureMarker>"); return 2; }
    unsigned failed = 0;
    auto check = [&](bool ok, const char* name) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failed; };
    check(MaintenancePath(L"Q:\\Fixture\\Program Files") == L"Q:\\Fixture\\Program Files\\TrueFluentPro\\VoiceIme\\maintenance.lock",
        "every version/bitness resolves the same installation-root marker, not a hash folder");
    check(!MaintenanceActiveAtPath(argv[1]) && MaintenanceActiveAtPath(argv[2]) && MaintenanceActiveAtPath(L""),
        "injected paths distinguish absent/present marker and fail closed on unknown root");
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
        } else check(false, "offline message-only service window");
    }
    check(ModuleObjects == 0, "offline lifecycle releases all native objects; no audio/cloud/host/registration");
    std::printf("RESULT failures=%u; injected fixture paths only; no ProgramFiles marker/config/registration/host/audio/cloud\n", failed);
    return failed ? 1 : 0;
}