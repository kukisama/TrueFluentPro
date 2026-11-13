#include "Common.h"
#include <cstdio>

using BridgeUpdateFn = void (WINAPI*)(void*, BOOL, int, BOOL, BOOL);
using BridgeCloseFn = void (WINAPI*)(void*);
struct BridgeWork { void* handle; BridgeUpdateFn update; BridgeCloseFn close; };
DWORD WINAPI CloseBridge(void* value) {
    auto* work = static_cast<BridgeWork*>(value);
    work->update(work->handle, TRUE, 2, FALSE, FALSE);
    work->close(work->handle); return 0;
}

int wmain(int argc, wchar_t** argv) {
    HANDLE token{}; DWORD enabled = 0, bytes = 0;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        BOOL ok = GetTokenInformation(token, TokenVirtualizationEnabled, &enabled, sizeof(enabled), &bytes);
        std::printf("process bits=%u virtualization-query=%d enabled=%lu manifest=%d\n",
            static_cast<unsigned>(sizeof(void*) * 8), ok, enabled,
            FindResourceW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(1), RT_MANIFEST) != nullptr);
        CloseHandle(token);
    }
    if (argc > 1 && std::wstring(argv[1]) == L"--registration-api") {
        std::puts("disabled: use offline VoiceImeRegistrationTests; administrator installation is a separate user action"); return 2;
    }
    if (argc != 2 || !AbsoluteFile(argv[1])) { std::puts("usage: VoiceImeTests <dllAbsolute>"); return 2; }
    HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(init)) return 3;
    unsigned failures = 0;
    auto check = [&](bool ok, const char* name) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failures; };
    KeyLatch latch; bool toggle = false;
    check(!latch.TestDown(false, VK_F8) && !latch.Press(false, VK_F8, 0, toggle) && !latch.Release(VK_F8), "inactive F8 untouched");
    check(latch.TestDown(true, VK_F8) && !latch.down, "test-key has no side effects");
    check(latch.Press(true, VK_F8, 0, toggle) && toggle, "selected first F8 toggles");
    check(latch.Press(true, VK_F8, static_cast<LPARAM>(1) << 30, toggle) && !toggle, "held F8 never repeats");
    check(!latch.Release('A') && latch.Release(VK_F8) && !latch.Release(VK_F8), "keyup only belongs to captured F8");
    check(latch.Press(true, VK_F8, 0, toggle) && toggle && latch.Release(VK_F8), "second F8 toggles stop while connecting");
    std::wstring text;
    const char valid[] = "\xe4\xbd\xa0\xe5\xa5\xbd";
    check(DecodeUtf8(valid, 6, text) && text == L"你好" && !DecodeUtf8("\xc0\xaf", 2, text), "strict UTF8");
    check(ValidKind('P') && ValidKind('F') && ValidKind('R') && ValidKind('E') && ValidKind('S') && !ValidKind('X'), "wire frame kinds");
    uint64_t epoch = 0, target = 0;
    check(ExtendedValidKind('I') && ExtendedValidKind('C') && !ExtendedValidKind('H') && !ExtendedValidKind('T') &&
        SharedPayload('P', L"7:18:正文:冒号", epoch, target, text) && epoch == 7 && target == 18 && text == L"正文:冒号" &&
        SharedPayload('S', L"8:0:", epoch, target, text) && text.empty() &&
        !SharedPayload('P', L"7:0:text", epoch, target, text) && !SharedPayload('R', L"7:18:", epoch, target, text) &&
        !SharedPayload('P', L"18446744073709551616:18:x", epoch, target, text) &&
        !SharedPayload('P', L"+7:18:x", epoch, target, text) && !SharedPayload('P', L"7:18", epoch, target, text),
        "shared epoch/generation envelope, strict decimal and first-two-colon split");
    check(QuoteArgument(L"C:\\folder name\\host.exe") == L"\"C:\\folder name\\host.exe\"" &&
        QuoteArgument(L"x\\") == L"\"x\\\\\"", "process argument escaping");
    HMODULE dll = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    check(dll != nullptr, "DLL load without registration");
    if (dll) {
        using FactoryFn = HRESULT (WINAPI*)(REFCLSID, REFIID, void**);
        using UnloadFn = HRESULT (WINAPI*)();
        auto get = reinterpret_cast<FactoryFn>(GetProcAddress(dll, "DllGetClassObject"));
        auto unload = reinterpret_cast<UnloadFn>(GetProcAddress(dll, "DllCanUnloadNow"));
        using BridgeCreateFn = void* (WINAPI*)(void (*)(int, int, void*), void*);
        using BridgeRunFn = int (WINAPI*)(void*);
        auto createBridge = reinterpret_cast<BridgeCreateFn>(GetProcAddress(dll, "VoiceImeToolbarCreate"));
        auto updateBridge = reinterpret_cast<BridgeUpdateFn>(GetProcAddress(dll, "VoiceImeToolbarUpdate"));
        auto runBridge = reinterpret_cast<BridgeRunFn>(GetProcAddress(dll, "VoiceImeToolbarRun"));
        auto closeBridge = reinterpret_cast<BridgeCloseFn>(GetProcAddress(dll, "VoiceImeToolbarClose"));
        check(createBridge && updateBridge && runBridge && closeBridge, "four WINAPI toolbar exports undecorated on this architecture");
        check(get && unload && GetProcAddress(dll, "DllRegisterServer") && GetProcAddress(dll, "DllUnregisterServer"), "undecorated exports");
        if (get && unload) {
            check(unload() == S_OK, "initial module count");
            ComPtr<IClassFactory> factory;
            check(SUCCEEDED(get(VoiceClsid, IID_PPV_ARGS(&factory))), "class factory");
            if (factory) {
                ComPtr<ITfTextInputProcessorEx> service;
                check(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&service))), "COM create Ex");
                if (service) {
                    ComPtr<ITfTextInputProcessor> base; ComPtr<ITfKeyEventSink> keys;
                    ComPtr<ITfThreadMgrEventSink> focus; ComPtr<ITfThreadFocusSink> thread;
                    ComPtr<ITfTextEditSink> edits; ComPtr<ITfCompositionSink> composition;
                    check(SUCCEEDED(service.As(&base)) && SUCCEEDED(service.As(&keys)) && SUCCEEDED(service.As(&focus)) &&
                        SUCCEEDED(service.As(&thread)) && SUCCEEDED(service.As(&edits)) && SUCCEEDED(service.As(&composition)), "required TSF sinks");
                    BOOL eaten = TRUE;
                    check(keys && SUCCEEDED(keys->OnTestKeyDown(nullptr, VK_F8, 0, &eaten)) && !eaten &&
                        SUCCEEDED(keys->OnKeyDown(nullptr, VK_F8, 0, &eaten)) && !eaten &&
                        SUCCEEDED(keys->OnKeyUp(nullptr, VK_F8, 0, &eaten)) && !eaten, "real COM inactive F8 pass through");
                    check(unload() == S_FALSE, "live COM prevents unload");
                }
            }
            factory.Reset();
            check(unload() == S_OK, "COM release balances references");
            if (createBridge && updateBridge && runBridge && closeBridge) {
                void* handle = createBridge(nullptr, nullptr);
                check(handle && unload() == S_FALSE, "owner STA bridge creates, advises TSF and prevents module unload");
                if (handle) {
                    BridgeWork work{handle, updateBridge, closeBridge};
                    HANDLE thread = CreateThread(nullptr, 0, CloseBridge, &work, 0, nullptr);
                    if (!thread) closeBridge(handle);
                    check(runBridge(handle) == 0, "cross-thread update/close, STA Run drains then releases windows/COM");
                    if (thread) { WaitForSingleObject(thread, INFINITE); CloseHandle(thread); }
                    updateBridge(handle, TRUE, 0, FALSE, FALSE); closeBridge(handle);
                    check(unload() == S_OK, "closed bridge ignores stale handle and balances module objects");
                }
            }
        }
        FreeLibrary(dll);
    }
    CoUninitialize();
    std::printf("RESULT failures=%u; no registration/config/cloud; selected TSF edits not exercised\n", failures);
    return failures ? 1 : 0;
}