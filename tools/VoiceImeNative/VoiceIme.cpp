#include <initguid.h> // inputscope.h DEFINE_GUID values need exactly one defining translation unit.
#include "Common.h"
#include "NativeInstallPath.h"
#include <algorithm>
#include "ThirdParty/libIME2/src/ImeModule.h"
#include "ThirdParty/libIME2/src/TextService.h"

HINSTANCE ModuleInstance{};
std::atomic<long> ModuleObjects{0};
namespace {
class Service;
// Runtime only. The existing factory remains the sole exported creation path.
// Count the module itself: an escaped framework display-attribute enumerator may
// outlive Service while still executing code in this DLL.
class VoiceModule final : public Ime::ImeModule {
public:
    VoiceModule() : Ime::ImeModule(ModuleInstance, VoiceClsid, {}) { ++ModuleObjects; }
    ~VoiceModule() override { --ModuleObjects; }
    Ime::TextService* createTextService() override;
    bool onConfigure(HWND, LANGID, REFGUID) override { return false; }
};
enum class EditKind { Start, Result, Probe };
class Edit final : public ITfEditSession {
    std::atomic<ULONG> refs{1};
    Service* owner;
    ComPtr<ITfContext> context;
    uint64_t generation;
    EditKind kind;
    char frame;
    std::wstring text;
    uint64_t audioEpoch;
    std::shared_ptr<PipeRun> connection;
public:
    Edit(Service*, ITfContext*, uint64_t, EditKind, char, std::wstring);
    ~Edit();
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id != IID_IUnknown && id != IID_ITfEditSession) return E_NOINTERFACE;
        *out = static_cast<ITfEditSession*>(this); AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    STDMETHODIMP DoEditSession(TfEditCookie cookie) override;
};
class Finish final : public ITfEditSession {
    std::atomic<ULONG> refs{1};
    ComPtr<ITfComposition> composition;
public:
    explicit Finish(ITfComposition* c) : composition(c) { ++ModuleObjects; }
    ~Finish() { --ModuleObjects; }
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id != IID_IUnknown && id != IID_ITfEditSession) return E_NOINTERFACE;
        *out = static_cast<ITfEditSession*>(this); AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    STDMETHODIMP DoEditSession(TfEditCookie cookie) override { return composition->EndComposition(cookie); }
};
bool Same(IUnknown* a, IUnknown* b) {
    if (!a || !b) return a == b;
    ComPtr<IUnknown> x, y;
    return SUCCEEDED(a->QueryInterface(IID_PPV_ARGS(&x))) && SUCCEEDED(b->QueryInterface(IID_PPV_ARGS(&y))) && x.Get() == y.Get();
}
HRESULT Advise(IUnknown* source, REFIID id, IUnknown* sink, DWORD& cookie) {
    ComPtr<ITfSource> s; cookie = TF_INVALID_COOKIE;
    HRESULT hr = source->QueryInterface(IID_PPV_ARGS(&s));
    return FAILED(hr) ? hr : s->AdviseSink(id, sink, &cookie);
}
void Unadvise(IUnknown* source, DWORD& cookie) {
    if (source && cookie != TF_INVALID_COOKIE) {
        ComPtr<ITfSource> s;
        if (SUCCEEDED(source->QueryInterface(IID_PPV_ARGS(&s)))) s->UnadviseSink(cookie);
    }
    cookie = TF_INVALID_COOKIE;
}
bool CompartmentDisabled(IUnknown* object, REFGUID id) {
    ComPtr<ITfCompartmentMgr> mgr; ComPtr<ITfCompartment> c;
    if (!object || FAILED(object->QueryInterface(IID_PPV_ARGS(&mgr))) || FAILED(mgr->GetCompartment(id, &c))) return false;
    VARIANT value{}; bool result = false;
    if (SUCCEEDED(c->GetValue(&value))) result = (value.vt == VT_I4 && value.lVal != 0) || (value.vt == VT_BOOL && value.boolVal != VARIANT_FALSE);
    VariantClear(&value); return result;
}
bool Text(ITfRange* range, TfEditCookie cookie, std::wstring& result) {
    ComPtr<ITfRange> copy;
    if (FAILED(range->Clone(&copy))) return false;
    result.clear();
    for (;;) {
        wchar_t buffer[512]; ULONG count = 0;
        if (FAILED(copy->GetText(cookie, TF_TF_MOVESTART, buffer, 512, &count))) return false;
        result.append(buffer, count);
        if (result.size() > MaximumFrame) return false;
        if (count < 512) return true;
    }
}
class Service final : public Ime::TextService, public ITfThreadFocusSink, public ITfStatusSink,
    public ITfInputProcessorProfileActivationSink {
    friend class Edit;
#ifdef VOICE_IME_OFFLINE_TEST
    friend struct OfflineEditAccess; // Only the standalone test EXE can inject offline text under a real TSF lock.
#endif
    ComPtr<ITfThreadMgr> manager;
    ComPtr<ITfInputProcessorProfileMgr> profiles;
    ComPtr<ITfContext> context;
    ComPtr<ITfComposition> composition;
    ComPtr<ITfRange> owned;
    TfClientId client = TF_CLIENTID_NULL;
    DWORD focusCookie = TF_INVALID_COOKIE;
    DWORD profileCookie = TF_INVALID_COOKIE;
    DWORD editCookie = TF_INVALID_COOKIE, statusCookie = TF_INVALID_COOKIE;
    bool foreground = false, active = false, listening = false;
    HRESULT activationResult = S_OK;
    ITfContext* keyContext = nullptr; // Borrowed only for a synchronous framework key dispatch.
    bool suppressSentence = false, ownEdit = false, probePending = false;
    bool targetBound = false, selectedAnnounced = false;
    uint64_t audioEpoch = 0;
    KeyLatch latch;
    uint64_t generation = 0;
    HWND window{};
    bool dispatcherClassRegistered = false;
    std::wstring windowClass;
    std::wstring lastText;
    std::shared_ptr<PipeRun> run;

    void EnsureConnection() {
        if (run || !window || InstallationMaintenanceActive()) return;
        auto pipe = NewPipeName();
        if (pipe.empty()) return;
        ++generation; audioEpoch = 0; selectedAnnounced = false;
        run = std::make_shared<PipeRun>(generation, window, std::move(pipe));
        if (!StartPipeWorker(run)) run.reset();
    }
    void Disconnect() {
        if (run) {
            const bool waitFlush = run->workerStarted.load() && run->deselection.load() != 0;
            SetEvent(run->cancel);
            if (waitFlush && run->flushed) WaitForSingleObject(run->flushed, 1500);
            { std::lock_guard<std::mutex> guard(run->delivery); run->target = nullptr; }
            run.reset();
        }
        selectedAnnounced = false;
    }

    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
        Service* self = reinterpret_cast<Service*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (msg == WM_NCCREATE) {
            self = static_cast<Service*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        if (self && msg == FrameMessage) {
            std::unique_ptr<Frame> frame(reinterpret_cast<Frame*>(l));
            try { self->Receive(*frame); } catch (...) { self->Stop(true); }
            return 0;
        }
        if (self && msg == WM_TIMER) {
            if (InstallationMaintenanceActive()) { self->Stop(false); return 0; }
            if (self->run && self->run->done && self->run->pending == 0) self->Stop(true);
            self->RefreshContext();
            return 0;
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }
    bool CreateWindows() try {
        WNDCLASSW cls{}; cls.hInstance = ModuleInstance; cls.lpfnWndProc = WindowProc;
        windowClass = L"TrueFluentVoiceIme.Dispatch." + std::to_wstring(reinterpret_cast<uintptr_t>(this));
        cls.lpszClassName = windowClass.c_str();
        if (!RegisterClassW(&cls)) return false;
        dispatcherClassRegistered = true;
        window = CreateWindowExW(0, cls.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, ModuleInstance, this);
        if (!window || !SetTimer(window, 1, 500, nullptr)) {
            DestroyWindows(); return false;
        }
        return true;
    } catch (...) {
        DestroyWindows(); return false;
    }
    void DestroyWindows() {
        if (window) {
            KillTimer(window, 1);
            MSG msg{}; while (PeekMessageW(&msg, window, FrameMessage, FrameMessage, PM_REMOVE)) delete reinterpret_cast<Frame*>(msg.lParam);
            DestroyWindow(window); window = nullptr;
        }
        if (dispatcherClassRegistered) UnregisterClassW(windowClass.c_str(), ModuleInstance);
        dispatcherClassRegistered = false; windowClass.clear();
    }
    bool ForegroundProcess() {
        const HWND front = GetForegroundWindow();
        DWORD frontProcess = 0;
        if (front) GetWindowThreadProcessId(front, &frontProcess);
        if (InstallationMaintenanceActive() || !front || IsSuppressedWindow(front) ||
            frontProcess != GetCurrentProcessId()) return false;
        return true;
    }
    bool ActualForeground() {
        if (!ForegroundProcess()) return false;
        BOOL focused = FALSE;
        if (!manager || FAILED(manager->IsThreadFocus(&focused)) || !focused) return false;
        return true;
    }
    bool IsVoiceProfileSelected() {
        TF_INPUTPROCESSORPROFILE selected{};
        return profiles && profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &selected) == S_OK &&
            selected.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && selected.clsid == VoiceClsid &&
            selected.guidProfile == VoiceProfile && selected.langid == 0x0804;
    }
    bool SelectedForeground() {
        return ActualForeground() && IsVoiceProfileSelected();
    }
    void UpdateToolbar(bool stopped = false, bool error = false) {
        (void)stopped; (void)error;
        // Shared owner alone presents UI. L is independent of context/editability.
        if (!selectedAnnounced && run && SelectedForeground())
            selectedAnnounced = SendSharedCommand(run, 'L', generation);
    }
    void ToggleRecording() {
        if (active) {
            if (SelectedForeground()) SendSharedCommand(run, 'T', generation);
            return; // Stop never requires an editable target.
        }
        RefreshContext();
        if (!CheapAllowed(context.Get())) { UpdateToolbar(); return; }
        UpdateToolbar(); Request(EditKind::Start);
    }
    bool Current(ITfContext* candidate) {
        if (!manager || !candidate) return false;
        BOOL focused = FALSE; ComPtr<ITfDocumentMgr> doc; ComPtr<ITfContext> top;
        return SUCCEEDED(manager->IsThreadFocus(&focused)) && focused &&
            SUCCEEDED(manager->GetFocus(&doc)) && doc && SUCCEEDED(doc->GetTop(&top)) && Same(top.Get(), candidate);
    }
    bool CheapAllowed(ITfContext* candidate) {
        if (!SelectedForeground() || !Current(candidate)) return false;
        TF_STATUS state{};
        if (FAILED(candidate->GetStatus(&state)) || (state.dwDynamicFlags & TF_SD_READONLY)) return false;
        for (IUnknown* object : {static_cast<IUnknown*>(candidate), static_cast<IUnknown*>(manager.Get())})
            if (CompartmentDisabled(object, GUID_COMPARTMENT_KEYBOARD_DISABLED) || CompartmentDisabled(object, GUID_COMPARTMENT_EMPTYCONTEXT)) return false;
        return true;
    }
    bool Allowed(TfEditCookie cookie) {
        if (!CheapAllowed(context.Get())) return false;
        TF_SELECTION selection{}; ULONG count = 0;
        if (FAILED(context->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &count)) || count != 1) return false;
        ComPtr<ITfRange> selectionRange; selectionRange.Attach(selection.range);
        ComPtr<ITfReadOnlyProperty> property;
        // Providers may not expose InputScope. Known password/private scopes fail closed.
        if (SUCCEEDED(context->GetAppProperty(GUID_PROP_INPUTSCOPE, &property))) {
            VARIANT value{};
            HRESULT hr = property->GetValue(cookie, selection.range, &value);
            bool denied = false;
            if (SUCCEEDED(hr) && value.vt == VT_UNKNOWN && value.punkVal) {
                ComPtr<ITfInputScope> scope;
                if (SUCCEEDED(value.punkVal->QueryInterface(IID_PPV_ARGS(&scope)))) {
                    InputScope* scopes = nullptr; UINT n = 0;
                    if (FAILED(scope->GetInputScopes(&scopes, &n))) denied = true;
                    else for (UINT i = 0; i < n; ++i)
                        if (scopes[i] == IS_PASSWORD || scopes[i] == IS_NUMERIC_PASSWORD || scopes[i] == IS_PRIVATE ||
                            scopes[i] == IS_NUMERIC_PIN || scopes[i] == IS_ALPHANUMERIC_PIN) denied = true;
                    CoTaskMemFree(scopes);
                } else denied = true;
            }
            VariantClear(&value);
            if (denied) return false;
        }
        // Classic EDIT password fields offer an additional observable guard.
        ComPtr<ITfContextView> view; HWND hwnd{};
        if (SUCCEEDED(context->GetActiveView(&view)) && view && SUCCEEDED(view->GetWnd(&hwnd)) && hwnd) {
            wchar_t name[32]{}; GetClassNameW(hwnd, name, 32);
            if (_wcsicmp(name, L"Edit") == 0 && (GetWindowLongPtrW(hwnd, GWL_STYLE) & ES_PASSWORD)) return false;
        }
        return true;
    }
    void RefreshContext() {
        if (selectedAnnounced && ((profiles && !IsVoiceProfileSelected()) || IsSuppressedWindow(GetForegroundWindow()))) {
            selectedAnnounced = false; latch.down = false;
            DropTarget();
            SendSharedCommand(run, 'D', generation);
            return;
        }
        if (SelectedForeground()) EnsureConnection();
        ComPtr<ITfDocumentMgr> doc; ComPtr<ITfContext> next;
        if (manager && SUCCEEDED(manager->GetFocus(&doc)) && doc) doc->GetTop(&next);
        if (!Same(context.Get(), next.Get())) {
            DropTarget();
            Unadvise(context.Get(), editCookie); Unadvise(context.Get(), statusCookie);
            context = next;
            if (context) {
                if (FAILED(Advise(context.Get(), IID_ITfTextEditSink, static_cast<ITfTextEditSink*>(this), editCookie))) {
                    context.Reset(); UpdateToolbar(); return;
                }
                Advise(context.Get(), IID_ITfStatusSink, static_cast<ITfStatusSink*>(this), statusCookie);
            }
        }
        UpdateToolbar();
        if (!CheapAllowed(context.Get())) {
            if (targetBound || probePending || composition) DropTarget();
            UpdateToolbar();
        } else if (run && !probePending) {
            probePending = true; Request(EditKind::Probe);
        }
    }
    void FinishComposition() {
        auto old = composition; composition.Reset(); owned.Reset(); lastText.clear();
        if (old && context) {
            auto* edit = new (std::nothrow) Finish(old.Get());
            if (edit) { HRESULT result{}; context->RequestEditSession(client, edit, TF_ES_ASYNC | TF_ES_READWRITE, &result); edit->Release(); }
        }
    }
    void DropTarget() {
        const uint64_t old = generation;
        const bool bound = targetBound;
        ++generation; targetBound = false; probePending = false; selectedAnnounced = false;
        FinishComposition(); suppressSentence = false;
        if (bound) SendSharedCommand(run, 'U', old);
    }
    void Stop(bool error) {
        (void)error;
        DropTarget();
        Disconnect();
        active = false; listening = false; selectedAnnounced = false;
    }
    HRESULT Request(EditKind kind, char frame = 0, std::wstring text = {}) {
        if (!context) { DropTarget(); return E_FAIL; }
        auto* edit = new (std::nothrow) Edit(this, context.Get(), generation, kind, frame, std::move(text));
        if (!edit) { DropTarget(); return E_OUTOFMEMORY; }
        HRESULT result{};
        HRESULT hr = context->RequestEditSession(client, edit, TF_ES_ASYNC | TF_ES_READWRITE, &result);
        edit->Release();
        if (FAILED(hr) || FAILED(result)) { DropTarget(); return FAILED(hr) ? hr : result; }
        return S_OK;
    }
    void Receive(const Frame& frame) {
        if (frame.run != run) return;
        if (InstallationMaintenanceActive()) { Stop(false); return; }
        if (!ExtendedValidKind(frame.kind) || frame.audioEpoch < audioEpoch) return;
        if (frame.kind != 'P' && frame.kind != 'F') {
            if (frame.targetGeneration != 0) return;
            if (frame.audioEpoch != audioEpoch) { FinishComposition(); suppressSentence = false; }
            audioEpoch = frame.audioEpoch;
            active = frame.kind == 'C' || frame.kind == 'R'; listening = frame.kind == 'R';
            // Status synchronizes even background peers, never changes shared target/audio.
            UpdateToolbar(); return;
        }
        if (!targetBound || frame.targetGeneration != generation || frame.audioEpoch != audioEpoch ||
            frame.audioEpoch != run->audioEpoch.load() || !listening) return;
        if (!CheapAllowed(context.Get())) { DropTarget(); return; }
        Request(EditKind::Result, frame.kind, frame.text);
    }
    HRESULT Replace(TfEditCookie cookie, char frame, const std::wstring& text) {
        bool final = frame == 'F';
        if (suppressSentence) { if (final) suppressSentence = false; return S_OK; }
        TF_SELECTION selection{}; ULONG count = 0;
        HRESULT hr = context->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &count);
        if (FAILED(hr) || count != 1) return E_FAIL;
        ComPtr<ITfRange> saved; saved.Attach(selection.range);
        bool follow = false;
        if (owned) {
            std::wstring actual;
            if (!Text(owned.Get(), cookie, actual) || actual != lastText) {
                suppressSentence = !final; FinishComposition(); return S_OK;
            }
            BOOL empty = FALSE; LONG at = 1;
            follow = SUCCEEDED(saved->IsEmpty(cookie, &empty)) && empty &&
                SUCCEEDED(saved->CompareStart(cookie, owned.Get(), TF_ANCHOR_END, &at)) && at == 0;
        } else {
            if (text.empty()) return S_OK;
            ComPtr<ITfContextComposition> cc;
            hr = context.As(&cc);
            if (FAILED(hr)) return hr;
            // Start at *current* selection, not the end of the previous sentence.
            hr = cc->StartComposition(cookie, saved.Get(), this, &composition);
            if (FAILED(hr) || !composition) return FAILED(hr) ? hr : E_FAIL;
            hr = composition->GetRange(&owned);
            if (FAILED(hr)) return hr;
            follow = true;
        }
        ownEdit = true;
        hr = owned->SetText(cookie, 0, text.data(), static_cast<LONG>(text.size()));
        if (SUCCEEDED(hr)) {
            lastText = text;
            if (follow) {
                ComPtr<ITfRange> end;
                if (SUCCEEDED(owned->Clone(&end)) && SUCCEEDED(end->Collapse(cookie, TF_ANCHOR_END))) {
                    TF_SELECTION caret{end.Get(), {TF_AE_NONE, FALSE}};
                    hr = context->SetSelection(cookie, 1, &caret);
                }
            } else {
                // TSF live ranges map the saved selection across the replacement's length change.
                selection.range = saved.Get(); hr = context->SetSelection(cookie, 1, &selection);
            }
        }
        if (final && composition) {
            auto old = composition; composition.Reset(); owned.Reset(); lastText.clear();
            HRESULT end = old->EndComposition(cookie); if (SUCCEEDED(hr)) hr = end;
        }
        ownEdit = false;
        return hr;
    }
public:
    explicit Service(Ime::ComPtr<VoiceModule> module = Ime::ComPtr<VoiceModule>::make())
        : Ime::TextService(module) { ++ModuleObjects; }
    ~Service() override { Stop(false); DestroyWindows(); --ModuleObjects; }
    using Ime::TextService::OnSetFocus;
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id == IID_ITfThreadFocusSink) *out = static_cast<ITfThreadFocusSink*>(this);
        else if (id == IID_ITfStatusSink) *out = static_cast<ITfStatusSink*>(this);
        else if (id == IID_ITfInputProcessorProfileActivationSink) *out = static_cast<ITfInputProcessorProfileActivationSink*>(this);
        else return Ime::TextService::QueryInterface(id, out);
        AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return Ime::TextService::AddRef(); }
    STDMETHODIMP_(ULONG) Release() override { return Ime::TextService::Release(); }
    STDMETHODIMP Activate(ITfThreadMgr* m, TfClientId id) override { return ActivateEx(m, id, 0); }
    STDMETHODIMP ActivateEx(ITfThreadMgr* m, TfClientId id, DWORD flags) override {
        if (!m || isActivated()) return E_INVALIDARG;
        if (flags & TF_TMF_SECUREMODE) return E_ACCESSDENIED;
        activationResult = S_OK;
        HRESULT hr = Ime::TextService::Activate(m, id);
        if (FAILED(hr)) return hr;
        if (FAILED(activationResult)) { hr = activationResult; Deactivate(); }
        return hr;
    }
    void onActivate() override {
        manager = threadMgr(); client = clientId();
        activationResult = CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&profiles));
        if (SUCCEEDED(activationResult)) activationResult = CreateWindows() ? S_OK : E_FAIL;
        // libIME2 owns the thread/key/compartment sinks. Only bridge the missing
        // thread-focus notification; context edit/status sinks have one owner below.
        if (SUCCEEDED(activationResult)) activationResult = Advise(manager.Get(), IID_ITfThreadFocusSink, static_cast<ITfThreadFocusSink*>(this), focusCookie);
        if (SUCCEEDED(activationResult)) activationResult = Advise(manager.Get(), IID_ITfInputProcessorProfileActivationSink,
            static_cast<ITfInputProcessorProfileActivationSink*>(this), profileCookie);
        if (FAILED(activationResult)) return;
        BOOL focused = FALSE; manager->IsThreadFocus(&focused); foreground = focused != FALSE;
        // Connection exists while idle. Never wait for F8 or a document context.
        EnsureConnection();
        RefreshContext();
    }
    void onDeactivate() override {
        foreground = false; latch.down = false;
        DropTarget();
        SendSharedCommand(run, 'D', generation);
        Stop(false);
        Unadvise(context.Get(), editCookie); Unadvise(context.Get(), statusCookie); context.Reset();
        Unadvise(manager.Get(), focusCookie);
        Unadvise(manager.Get(), profileCookie);
        manager.Reset(); profiles.Reset(); client = TF_CLIENTID_NULL; keyContext = nullptr;
        DestroyWindows();
    }
    void onSetFocus() override { foreground = true; selectedAnnounced = false; EnsureConnection(); RefreshContext(); }
    void onKillFocus() override { foreground = false; latch.down = false; DropTarget(); }
    void onLangProfileActivated(REFGUID profile) override {
        if (profile == VoiceProfile && manager) {
            BOOL focused = FALSE; manager->IsThreadFocus(&focused); foreground = focused != FALSE;
            selectedAnnounced = false; EnsureConnection(); RefreshContext();
        }
    }
    void onLangProfileDeactivated(REFGUID profile) override {
        if (profile != VoiceProfile) return;
        selectedAnnounced = false; latch.down = false;
        DropTarget();
        SendSharedCommand(run, 'D', generation);
    }
    void onCompartmentChanged(REFGUID compartment) override {
        Ime::TextService::onCompartmentChanged(compartment);
        if (!CheapAllowed(context.Get())) DropTarget();
        UpdateToolbar();
    }
    bool keyEventsNeedEditSession() const override { return false; }
    bool KeyEligible() { return active ? SelectedForeground() : CheapAllowed(keyContext); }
    bool filterKeyDown(Ime::KeyEvent& key) override { return latch.TestDown(KeyEligible(), key.keyCode()); }
    bool onKeyDown(Ime::KeyEvent& key, Ime::EditSession*) override {
        bool toggle = false;
        bool eaten = latch.Press(KeyEligible(), key.keyCode(), key.lParam(), toggle);
        if (toggle) ToggleRecording();
        return eaten;
    }
    bool filterKeyUp(Ime::KeyEvent& key) override { return latch.TestUp(key.keyCode()); }
    bool onKeyUp(Ime::KeyEvent& key, Ime::EditSession*) override { return latch.Release(key.keyCode()); }
    STDMETHODIMP OnTestKeyDown(ITfContext* c, WPARAM key, LPARAM flags, BOOL* eaten) override {
        ITfContext* previous = keyContext; keyContext = c;
        HRESULT hr = Ime::TextService::OnTestKeyDown(c, key, flags, eaten); keyContext = previous; return hr;
    }
    STDMETHODIMP OnKeyDown(ITfContext* c, WPARAM key, LPARAM flags, BOOL* eaten) override {
        ITfContext* previous = keyContext; keyContext = c;
        HRESULT hr = Ime::TextService::OnKeyDown(c, key, flags, eaten); keyContext = previous; return hr;
    }
    STDMETHODIMP OnUninitDocumentMgr(ITfDocumentMgr*) override { RefreshContext(); return S_OK; }
    STDMETHODIMP OnSetFocus(ITfDocumentMgr*, ITfDocumentMgr*) override { RefreshContext(); return S_OK; }
    STDMETHODIMP OnPushContext(ITfContext*) override { RefreshContext(); return S_OK; }
    STDMETHODIMP OnPopContext(ITfContext*) override { RefreshContext(); return S_OK; }
    STDMETHODIMP OnSetThreadFocus() override { onSetFocus(); return S_OK; }
    STDMETHODIMP OnKillThreadFocus() override { onKillFocus(); return S_OK; }
    STDMETHODIMP OnStatusChange(ITfContext* c, DWORD) override {
        if (Same(c, context.Get())) RefreshContext();
        return S_OK;
    }
    using Ime::TextService::OnActivated;
    STDMETHODIMP OnActivated(DWORD type, LANGID language, REFCLSID clsid, REFGUID category,
        REFGUID profile, HKL hkl, DWORD flags) override {
        AddRef(); // Advice/callback teardown cannot delete the current call's receiver.
        if ((flags & TF_IPSINK_FLAG_ACTIVE) &&
            ((type == TF_PROFILETYPE_INPUTPROCESSOR && (category == GUID_TFCAT_TIP_KEYBOARD || category == GUID{}) &&
                clsid != GUID{} && profile != GUID{} && language != 0) ||
                (type == TF_PROFILETYPE_KEYBOARDLAYOUT && hkl))) {
            if (type == TF_PROFILETYPE_INPUTPROCESSOR && clsid == VoiceClsid && profile == VoiceProfile && language == 0x0804) {
                selectedAnnounced = false; EnsureConnection(); RefreshContext();
            } else {
                selectedAnnounced = false; latch.down = false;
                DropTarget();
                SendSharedCommand(run, 'D', generation);
            }
        }
        Release(); return S_OK;
    }
    STDMETHODIMP OnCompositionTerminated(TfEditCookie, ITfComposition* c) override {
        if (Same(c, composition.Get())) { composition.Reset(); owned.Reset(); lastText.clear(); suppressSentence = true; }
        return S_OK;
    }
    STDMETHODIMP OnEndEdit(ITfContext* c, TfEditCookie cookie, ITfEditRecord* record) override {
        if (!Same(c, context.Get())) return S_OK;
        if (targetBound && !Allowed(cookie)) { DropTarget(); return S_OK; }
        BOOL mine = FALSE;
        if (ownEdit || (SUCCEEDED(c->InWriteSession(client, &mine)) && mine) || !owned) return S_OK;
        bool touched = false;
        ComPtr<IEnumTfRanges> updates;
        if (record && SUCCEEDED(record->GetTextAndPropertyUpdates(TF_GTP_INCL_TEXT, nullptr, 0, &updates)) && updates) {
            ComPtr<ITfRange> changed; ULONG count = 0;
            while (updates->Next(1, &changed, &count) == S_OK && count == 1) {
                LONG start = 1, end = -1;
                if (SUCCEEDED(changed->CompareStart(cookie, owned.Get(), TF_ANCHOR_END, &start)) &&
                    SUCCEEDED(changed->CompareEnd(cookie, owned.Get(), TF_ANCHOR_START, &end)) && start <= 0 && end >= 0) touched = true;
                changed.Reset();
            }
        }
        std::wstring actual;
        if (touched || !Text(owned.Get(), cookie, actual) || actual != lastText) {
            suppressSentence = true; FinishComposition();
        }
        // Selection changes alone intentionally do not stop dictation or move the caret.
        return S_OK;
    }
    HRESULT Execute(ITfContext* c, uint64_t g, EditKind kind, char frame, const std::wstring& text,
        TfEditCookie cookie, uint64_t epoch, const std::shared_ptr<PipeRun>& connection) {
        if (g != generation || !Same(c, context.Get()) || connection != run) return S_OK;
        if (kind == EditKind::Probe) probePending = false;
        if (kind == EditKind::Result && (!targetBound || !listening || !run || epoch != audioEpoch ||
            epoch != run->audioEpoch.load() || (frame != 'P' && frame != 'F'))) return S_OK;
        if (!Allowed(cookie)) { DropTarget(); return S_OK; }
        if (kind == EditKind::Start || kind == EditKind::Probe) {
            if (!run) return S_OK;
            // Reassert the safe foreground target before every start toggle.
            // Repeated B for the same peer/generation is a host-side no-op.
            if (!targetBound || kind == EditKind::Start) {
                targetBound = SendSharedCommand(run, 'B', generation);
                if (!targetBound) return E_FAIL;
            }
            if (kind == EditKind::Start) SendSharedCommand(run, 'T', generation);
            return S_OK;
        }
        HRESULT hr = Replace(cookie, frame, text);
        if (FAILED(hr)) DropTarget();
        return hr;
    }
};
Ime::TextService* VoiceModule::createTextService() { return new Service(Ime::ComPtr<VoiceModule>(this)); }
Edit::Edit(Service* s, ITfContext* c, uint64_t g, EditKind k, char f, std::wstring t)
    : owner(s), context(c), generation(g), kind(k), frame(f), text(std::move(t)), audioEpoch(s->audioEpoch), connection(s->run) { owner->AddRef(); ++ModuleObjects; }
Edit::~Edit() { owner->Release(); --ModuleObjects; }
HRESULT Edit::DoEditSession(TfEditCookie cookie) {
    try { return owner->Execute(context.Get(), generation, kind, frame, text, cookie, audioEpoch, connection); }
    catch (...) { return E_OUTOFMEMORY; }
}
class Factory final : public IClassFactory {
    std::atomic<ULONG> refs{1};
public:
    Factory() { ++ModuleObjects; }
    ~Factory() { --ModuleObjects; }
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id != IID_IUnknown && id != IID_IClassFactory) return E_NOINTERFACE;
        *out = this; AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        try {
            auto* service = new Service;
            HRESULT hr = service->QueryInterface(id, out); service->Release(); return hr;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    STDMETHODIMP LockServer(BOOL lock) override { if (lock) ++ModuleObjects; else --ModuleObjects; return S_OK; }
};
}
extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID id, void** out) {
    if (!out) return E_POINTER; *out = nullptr;
    if (clsid != VoiceClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) Factory;
    if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(id, out); factory->Release(); return hr;
}
extern "C" HRESULT WINAPI DllCanUnloadNow() { return ModuleObjects == 0 ? S_OK : S_FALSE; }
extern "C" HRESULT WINAPI DllRegisterServer() {
    wchar_t path[32768]{}; DWORD size = GetModuleFileNameW(ModuleInstance, path, 32768);
    if (!size || size >= 32768) return E_FAIL;
    return RegisterCurrentUser(path);
}
extern "C" HRESULT WINAPI DllUnregisterServer() {
    wchar_t path[32768]{}; DWORD size = GetModuleFileNameW(ModuleInstance, path, 32768);
    if (!size || size >= 32768) return E_FAIL;
    return UnregisterCurrentUser(path);
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) ModuleInstance = instance;
    return TRUE; // No COM, registry access or worker joins while the loader lock is held.
}