#include "Common.h"
#include <cstdio>

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
    check(QuoteArgument(L"C:\\folder name\\host.exe") == L"\"C:\\folder name\\host.exe\"" &&
        QuoteArgument(L"x\\") == L"\"x\\\\\"", "process argument escaping");
    HMODULE dll = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    check(dll != nullptr, "DLL load without registration");
    if (dll) {
        using FactoryFn = HRESULT (WINAPI*)(REFCLSID, REFIID, void**);
        using UnloadFn = HRESULT (WINAPI*)();
        auto get = reinterpret_cast<FactoryFn>(GetProcAddress(dll, "DllGetClassObject"));
        auto unload = reinterpret_cast<UnloadFn>(GetProcAddress(dll, "DllCanUnloadNow"));
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
        }
        FreeLibrary(dll);
    }
    CoUninitialize();
    std::printf("RESULT failures=%u; no registration/config/cloud; selected TSF edits not exercised\n", failures);
    return failures ? 1 : 0;
}