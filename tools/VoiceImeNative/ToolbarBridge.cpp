#include "Common.h"
#include "VoiceToolbar.h"
#include <algorithm>

namespace {
using ToolbarCallback = void (WINAPI*)(int, int, void*);
// Callback (kind, value, context): 1 toggle, 2 selection (0/1), 3 settings,
// 4 history consent (0/1), 5 open usage, 6 open language/translation. Navigation values are 0.
constexpr UINT BridgeUpdate = WM_APP + 0x591;
constexpr UINT BridgeClose = WM_APP + 0x592;
constexpr UINT BridgeHistory = WM_APP + 0x593;
class ToolbarBridge;
std::mutex bridgesMutex;
std::vector<ToolbarBridge*> bridges;

// The handle remains registered until Run exits. Update/Close acquire a reference
// under the registry lock; only the creating STA releases COM/windows.
class ToolbarBridge final : public ITfInputProcessorProfileActivationSink {
    std::atomic<ULONG> refs{1};
    HMODULE module{};
    DWORD thread = GetCurrentThreadId();
    HWND dispatcher{};
    std::wstring windowClass;
    bool registered = false, closing = false, running = false, suppressedByWindow = false;
    VoiceToolbar toolbar;
    ToolbarCallback callback;
    void* context;
    ComPtr<ITfThreadMgrEx> manager;
    ComPtr<ITfInputProcessorProfileMgr> profiles;
    ComPtr<ITfSource> source;
    DWORD cookie = TF_INVALID_COOKIE;
    TfClientId client = TF_CLIENTID_NULL;
    std::mutex updateMutex;
    struct { bool visible = false; int state = 0; bool stopped = false, error = false; } update;
    bool updatePosted = false;
    struct { bool enabled = false, available = false; } history;
    bool historyPosted = false;
    void Notify(int event, int value) noexcept {
        if (closing || !callback) return;
        AddRef();
        try { callback(event, value, context); } catch (...) { /* Never cross the native ABI. */ }
        Release();
    }
    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
        auto* self = reinterpret_cast<ToolbarBridge*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (msg == WM_NCCREATE) {
            self = static_cast<ToolbarBridge*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        if (self && msg == BridgeClose) { self->closing = true; return 0; }
        if (self && msg == BridgeHistory) {
            decltype(self->history) next;
            { std::lock_guard<std::mutex> guard(self->updateMutex); next = self->history; self->historyPosted = false; }
            if (!self->closing) self->toolbar.SetHistory(next.enabled, next.available);
            return 0;
        }
        if (self && msg == WM_TIMER && w == 1) {
            bool visible = false;
            { std::lock_guard<std::mutex> guard(self->updateMutex); visible = self->update.visible; }
            if (visible && !self->closing && IsSuppressedWindow(GetForegroundWindow())) {
                self->suppressedByWindow = true;
                self->Notify(2, 0);
            } else if (!visible && self->suppressedByWindow && !self->closing && !IsSuppressedWindow(GetForegroundWindow())) {
                self->suppressedByWindow = false;
                if (self->profiles) {
                    TF_INPUTPROCESSORPROFILE active{};
                    if (self->profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &active) == S_OK &&
                        active.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && active.langid == 0x0804 &&
                        active.clsid == VoiceClsid && active.guidProfile == VoiceProfile) {
                        self->Notify(2, 1);
                    }
                }
            }
            return 0;
        }
        if (self && msg == WM_TIMER && w == 2) {
            if (!self->closing && self->profiles) {
                if (IsSuppressedWindow(GetForegroundWindow())) {
                    self->suppressedByWindow = true;
                } else {
                    KillTimer(hwnd, 2);
                    TF_INPUTPROCESSORPROFILE active{};
                    if (self->profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &active) == S_OK &&
                        active.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && active.langid == 0x0804 &&
                        active.clsid == VoiceClsid && active.guidProfile == VoiceProfile) {
                        self->Notify(2, 1);
                    }
                }
            } else {
                KillTimer(hwnd, 2);
            }
            return 0;
        }
        if (self && msg == BridgeUpdate) {
            decltype(self->update) next;
            { std::lock_guard<std::mutex> guard(self->updateMutex); next = self->update; self->updatePosted = false; }
            if (!self->closing) {
                self->toolbar.SetEligible(next.visible);
                self->toolbar.SetState(static_cast<VoiceToolbarState>(next.state), next.stopped, next.error);
            }
            return 0;
        }
        if (msg == WM_NCDESTROY) SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
        return DefWindowProcW(hwnd, msg, w, l);
    }
public:
    ToolbarBridge(ToolbarCallback cb, void* ctx, HMODULE m) : module(m), callback(cb), context(ctx) { ++ModuleObjects; }
    ~ToolbarBridge() { --ModuleObjects; }
    HMODULE Module() const { return module; }
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id != IID_IUnknown && id != IID_ITfInputProcessorProfileActivationSink) return E_NOINTERFACE;
        *out = static_cast<ITfInputProcessorProfileActivationSink*>(this); AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    STDMETHODIMP OnActivated(DWORD type, LANGID language, REFCLSID clsid, REFGUID category,
        REFGUID profile, HKL hkl, DWORD flags) override {
        AddRef();
        if ((flags & TF_IPSINK_FLAG_ACTIVE) &&
            ((type == TF_PROFILETYPE_INPUTPROCESSOR && (category == GUID_TFCAT_TIP_KEYBOARD || category == GUID{}) &&
                clsid != GUID{} && profile != GUID{} && language != 0) ||
                (type == TF_PROFILETYPE_KEYBOARDLAYOUT && hkl))) {
            // ACTIVE is the actual TSF notification. The observer's background
            // thread profile is not the foreground application's selection.
            const bool own = type == TF_PROFILETYPE_INPUTPROCESSOR && language == 0x0804 &&
                clsid == VoiceClsid && profile == VoiceProfile;
            Notify(2, own ? 1 : 0);
        }
        Release(); return S_OK;
    }
    bool Create() {
        // Caller owns COM initialization and the dedicated STA; never change application DPI.
        APTTYPE apartment{}; APTTYPEQUALIFIER qualifier{};
        if (FAILED(CoGetApartmentType(&apartment, &qualifier)) ||
            (apartment != APTTYPE_STA && apartment != APTTYPE_MAINSTA)) return false;
        windowClass = L"TrueFluentVoiceIme.ToolbarBridge." + std::to_wstring(reinterpret_cast<uintptr_t>(this));
        WNDCLASSW cls{}; cls.hInstance = ModuleInstance; cls.lpfnWndProc = WindowProc; cls.lpszClassName = windowClass.c_str();
        if (!RegisterClassW(&cls)) return false;
        registered = true;
        dispatcher = CreateWindowExW(0, cls.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, ModuleInstance, this);
        if (!dispatcher || !SetTimer(dispatcher, 1, 500, nullptr) || !toolbar.Create(ModuleInstance,
            [](void* ctx) { auto* self = static_cast<ToolbarBridge*>(ctx); self->Notify(1, 0); },
            this,
            [](void* ctx) { auto* self = static_cast<ToolbarBridge*>(ctx); self->Notify(3, 0); },
            [](void* ctx, bool enabled) { auto* self = static_cast<ToolbarBridge*>(ctx); self->Notify(4, enabled ? 1 : 0); },
            [](void* ctx) { auto* self = static_cast<ToolbarBridge*>(ctx); self->Notify(5, 0); },
            [](void* ctx) { auto* self = static_cast<ToolbarBridge*>(ctx); self->Notify(6, 0); })) return false;
        HRESULT hr = CoCreateInstance(CLSID_TF_ThreadMgr, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&manager));
        // These flags forbid selecting a TIP/layout/default in the dedicated owner thread.
        if (SUCCEEDED(hr)) hr = manager->ActivateEx(&client, TF_TMAE_NOACTIVATETIP | TF_TMAE_NOACTIVATEKEYBOARDLAYOUT);
        if (SUCCEEDED(hr)) hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&profiles));
        if (SUCCEEDED(hr)) hr = manager.As(&source);
        if (SUCCEEDED(hr)) hr = source->AdviseSink(IID_ITfInputProcessorProfileActivationSink,
            static_cast<ITfInputProcessorProfileActivationSink*>(this), &cookie);
        if (SUCCEEDED(hr)) SetTimer(dispatcher, 2, 150, nullptr);
        return SUCCEEDED(hr);
    }
    void Cleanup() {
        closing = true; callback = nullptr;
        if (source && cookie != TF_INVALID_COOKIE) source->UnadviseSink(cookie);
        cookie = TF_INVALID_COOKIE; source.Reset();
        if (manager && client != TF_CLIENTID_NULL) manager->Deactivate();
        client = TF_CLIENTID_NULL; manager.Reset(); profiles.Reset();
        toolbar.Destroy();
        // Serialize handle users with final dispatcher destruction; no cross-thread DestroyWindow.
        { std::lock_guard<std::mutex> guard(updateMutex);
            if (dispatcher) DestroyWindow(dispatcher);
            dispatcher = nullptr;
        }
        if (registered) UnregisterClassW(windowClass.c_str(), ModuleInstance);
        registered = false;
    }
    void Update(BOOL visible, int state, BOOL stopped, BOOL error) {
        if (state < 0 || state > 2) return;
        std::lock_guard<std::mutex> guard(updateMutex);
        update = {visible != FALSE, state, stopped != FALSE, error != FALSE};
        if (dispatcher && !updatePosted) updatePosted = PostMessageW(dispatcher, BridgeUpdate, 0, 0) != FALSE;
    }
    void Close() {
        std::lock_guard<std::mutex> guard(updateMutex);
        if (dispatcher) PostMessageW(dispatcher, BridgeClose, 0, 0);
    }
    void SetHistory(BOOL enabled, BOOL available) {
        std::lock_guard<std::mutex> guard(updateMutex);
        history = {enabled != FALSE, available != FALSE};
        if (dispatcher && !historyPosted) historyPosted = PostMessageW(dispatcher, BridgeHistory, 0, 0) != FALSE;
    }
    int Run() {
        if (GetCurrentThreadId() != thread || running) return -1;
        running = true;
        MSG msg{}; int result = 0;
        while (!closing) {
            const BOOL received = GetMessageW(&msg, nullptr, 0, 0);
            if (received <= 0) { result = received < 0 ? -1 : static_cast<int>(msg.wParam); break; }
            TranslateMessage(&msg); DispatchMessageW(&msg);
        }
        return result;
    }
    bool CanRun() const { return thread == GetCurrentThreadId() && !running; }
};
ToolbarBridge* Acquire(void* handle) {
    std::lock_guard<std::mutex> guard(bridgesMutex);
    auto found = std::find(bridges.begin(), bridges.end(), handle);
    if (found == bridges.end()) return nullptr;
    (*found)->AddRef(); return *found;
}
}
extern "C" void* WINAPI VoiceImeToolbarCreate(ToolbarCallback callback, void* context) noexcept {
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(&VoiceImeToolbarCreate), &module)) return nullptr;
    ToolbarBridge* bridge = nullptr;
    try {
        bridge = new ToolbarBridge(callback, context, module);
        if (bridge->Create()) {
            std::lock_guard<std::mutex> guard(bridgesMutex); bridges.push_back(bridge); return bridge;
        }
    } catch (...) { }
    if (bridge) { bridge->Cleanup(); bridge->Release(); }
    FreeLibrary(module); return nullptr;
}
extern "C" void WINAPI VoiceImeToolbarUpdate(void* handle, BOOL visible, int state, BOOL stopped, BOOL error) noexcept {
    auto* bridge = Acquire(handle); if (!bridge) return;
    try { bridge->Update(visible, state, stopped, error); } catch (...) { }
    bridge->Release();
}
extern "C" int WINAPI VoiceImeToolbarRun(void* handle) noexcept {
    auto* bridge = Acquire(handle); if (!bridge) return -1;
    if (!bridge->CanRun()) { bridge->Release(); return -1; }
    int result = -1;
    try { result = bridge->Run(); } catch (...) { }
    // Callback Close only posts. Run unwinds it before releasing any UI or COM object.
    bridge->Cleanup();
    { std::lock_guard<std::mutex> guard(bridgesMutex);
        auto found = std::find(bridges.begin(), bridges.end(), bridge);
        if (found != bridges.end()) { bridges.erase(found); bridge->Release(); }
    }
    HMODULE module = bridge->Module(); bridge->Release();
    // The owner's LoadLibrary reference remains until this ABI call has returned.
    FreeLibrary(module); return result;
}
extern "C" void WINAPI VoiceImeToolbarSetHistory(void* handle, BOOL enabled, BOOL available) noexcept {
    auto* bridge = Acquire(handle); if (!bridge) return;
    try { bridge->SetHistory(enabled, available); } catch (...) { }
    bridge->Release();
}
extern "C" void WINAPI VoiceImeToolbarClose(void* handle) noexcept {
    auto* bridge = Acquire(handle); if (!bridge) return;
    bridge->Close(); bridge->Release();
}