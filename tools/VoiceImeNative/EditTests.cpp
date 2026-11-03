// Test-only executable: the exact production Service/Replace implementation, not a second range algorithm.
#define VOICE_IME_OFFLINE_TEST
#include "VoiceIme.cpp"
#include <textstor.h>
#include <olectl.h>
#include <cstdio>
#include <cstring>

static unsigned workerStarts = 0;
bool StartPipeWorker(const std::shared_ptr<PipeRun>&) { ++workerStarts; return false; }
std::wstring NewPipeName() { return {}; } // No host/config/microphone path is linked into this EXE.

namespace {
HRESULT Stage(const char* api, HRESULT hr) {
    std::printf("stage %s HRESULT=0x%08lX\n", api, static_cast<unsigned long>(hr));
    return hr;
}
// Test-only observation: every method calls Windows and returns its HRESULT unchanged.
// No profile/category registration, fake success, or production diagnostics hook.
class TracedThreadMgr final : public ITfThreadMgr, public ITfSource, public ITfKeystrokeMgr {
    ULONG refs = 1;
    ComPtr<ITfThreadMgr> real;
    ComPtr<ITfSource> source;
    ComPtr<ITfKeystrokeMgr> keys;
    HRESULT Trace(const char* api, HRESULT hr) {
        if (FAILED(hr) && !firstFailure) { firstFailure = api; firstResult = hr; }
        return Stage(api, hr);
    }
public:
    const char* firstFailure = nullptr;
    HRESULT firstResult = S_OK;
    unsigned focusCalls = 0;
    explicit TracedThreadMgr(ITfThreadMgr* m) : real(m) {}
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id == IID_IUnknown || id == IID_ITfThreadMgr) *out = static_cast<ITfThreadMgr*>(this);
        else if (id == IID_ITfSource) {
            HRESULT hr = Trace("thread.QueryInterface(ITfSource)", real.As(&source));
            if (FAILED(hr)) return hr;
            *out = static_cast<ITfSource*>(this);
        } else if (id == IID_ITfKeystrokeMgr) {
            HRESULT hr = Trace("thread.QueryInterface(ITfKeystrokeMgr)", real.As(&keys));
            if (FAILED(hr)) return hr;
            *out = static_cast<ITfKeystrokeMgr*>(this);
        } else return Trace("thread.QueryInterface(other)", real->QueryInterface(id, out));
        AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    STDMETHODIMP Activate(TfClientId* id) override { return Trace("thread.Activate", real->Activate(id)); }
    STDMETHODIMP Deactivate() override { return Trace("thread.Deactivate", real->Deactivate()); }
    STDMETHODIMP CreateDocumentMgr(ITfDocumentMgr** doc) override { return real->CreateDocumentMgr(doc); }
    STDMETHODIMP EnumDocumentMgrs(IEnumTfDocumentMgrs** docs) override { return real->EnumDocumentMgrs(docs); }
    STDMETHODIMP GetFocus(ITfDocumentMgr** doc) override {
        ++focusCalls;
        HRESULT hr = Trace("thread.GetFocus", real->GetFocus(doc));
        std::printf("stage thread.GetFocus document=%d\n", doc && *doc != nullptr);
        return hr;
    }
    STDMETHODIMP SetFocus(ITfDocumentMgr* doc) override { return real->SetFocus(doc); }
    STDMETHODIMP AssociateFocus(HWND hwnd, ITfDocumentMgr* doc, ITfDocumentMgr** previous) override { return real->AssociateFocus(hwnd, doc, previous); }
    STDMETHODIMP IsThreadFocus(BOOL* focused) override { return Trace("thread.IsThreadFocus", real->IsThreadFocus(focused)); }
    STDMETHODIMP GetFunctionProvider(REFCLSID id, ITfFunctionProvider** provider) override { return real->GetFunctionProvider(id, provider); }
    STDMETHODIMP EnumFunctionProviders(IEnumTfFunctionProviders** providers) override { return real->EnumFunctionProviders(providers); }
    STDMETHODIMP GetGlobalCompartment(ITfCompartmentMgr** compartment) override { return real->GetGlobalCompartment(compartment); }
    STDMETHODIMP AdviseSink(REFIID id, IUnknown* sink, DWORD* cookie) override {
        const char* api = id == IID_ITfThreadMgrEventSink ? "source.AdviseSink(ITfThreadMgrEventSink)" :
            id == IID_ITfThreadFocusSink ? "source.AdviseSink(ITfThreadFocusSink)" : "source.AdviseSink(other)";
        return Trace(api, source->AdviseSink(id, sink, cookie));
    }
    STDMETHODIMP UnadviseSink(DWORD cookie) override { return Trace("source.UnadviseSink", source->UnadviseSink(cookie)); }
    STDMETHODIMP AdviseKeyEventSink(TfClientId id, ITfKeyEventSink* sink, BOOL foreground) override {
        std::printf("stage keys.AdviseKeyEventSink client=%lu sink=%d foreground=%d\n", id, sink != nullptr, foreground);
        return Trace("keys.AdviseKeyEventSink", keys->AdviseKeyEventSink(id, sink, foreground));
    }
    STDMETHODIMP UnadviseKeyEventSink(TfClientId id) override { return Trace("keys.UnadviseKeyEventSink", keys->UnadviseKeyEventSink(id)); }
    STDMETHODIMP GetForeground(CLSID* id) override { return keys->GetForeground(id); }
    STDMETHODIMP TestKeyDown(WPARAM key, LPARAM flags, BOOL* eaten) override { return keys->TestKeyDown(key, flags, eaten); }
    STDMETHODIMP TestKeyUp(WPARAM key, LPARAM flags, BOOL* eaten) override { return keys->TestKeyUp(key, flags, eaten); }
    STDMETHODIMP KeyDown(WPARAM key, LPARAM flags, BOOL* eaten) override { return keys->KeyDown(key, flags, eaten); }
    STDMETHODIMP KeyUp(WPARAM key, LPARAM flags, BOOL* eaten) override { return keys->KeyUp(key, flags, eaten); }
    STDMETHODIMP GetPreservedKey(ITfContext* context, const TF_PRESERVEDKEY* key, GUID* id) override { return keys->GetPreservedKey(context, key, id); }
    STDMETHODIMP IsPreservedKey(REFGUID id, const TF_PRESERVEDKEY* key, BOOL* registered) override { return keys->IsPreservedKey(id, key, registered); }
    STDMETHODIMP PreserveKey(TfClientId client, REFGUID id, const TF_PRESERVEDKEY* key, const WCHAR* description, ULONG size) override {
        return keys->PreserveKey(client, id, key, description, size);
    }
    STDMETHODIMP UnpreserveKey(REFGUID id, const TF_PRESERVEDKEY* key) override { return keys->UnpreserveKey(id, key); }
    STDMETHODIMP SetPreservedKeyDescription(REFGUID id, const WCHAR* description, ULONG size) override { return keys->SetPreservedKeyDescription(id, description, size); }
    STDMETHODIMP GetPreservedKeyDescription(REFGUID id, BSTR* description) override { return keys->GetPreservedKeyDescription(id, description); }
    STDMETHODIMP SimulatePreservedKey(ITfContext* context, REFGUID id, BOOL* eaten) override { return keys->SimulatePreservedKey(context, id, eaten); }
};
void InspectClient(ITfThreadMgr* manager, Service* service, TfClientId owner, TfClientId client) {
    ComPtr<ITfCategoryMgr> categories;
    HRESULT hr = Stage("readonly.CoCreateInstance(TF_CategoryMgr)",
        CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&categories)));
    if (SUCCEEDED(hr)) {
        const GUID* candidates[] = {&GUID_TFCAT_TIP_KEYBOARD}; GUID category{};
        Stage("readonly.category.FindClosestCategory(VoiceClsid,keyboard)", categories->FindClosestCategory(VoiceClsid, &category, candidates, 1));
        std::printf("readonly category keyboard=%d null=%d; no registration attempted\n", category == GUID_TFCAT_TIP_KEYBOARD, category == GUID{});
    }
    ComPtr<ITfKeyEventSink> sink; ComPtr<ITfKeystrokeMgr> keys;
    hr = Stage("control.service.QueryInterface(ITfKeyEventSink)", service->QueryInterface(IID_PPV_ARGS(&sink)));
    if (SUCCEEDED(hr)) hr = Stage("control.thread.QueryInterface(ITfKeystrokeMgr)", manager->QueryInterface(IID_PPV_ARGS(&keys)));
    if (FAILED(hr)) return;
    // Controlled parameter comparison only, not a substitute for production foreground activation.
    hr = Stage("control.keys.AdviseKeyEventSink(service-client,FALSE)", keys->AdviseKeyEventSink(client, sink.Get(), FALSE));
    if (SUCCEEDED(hr)) Stage("control.keys.UnadviseKeyEventSink(service-client)", keys->UnadviseKeyEventSink(client));
    hr = Stage("control.keys.AdviseKeyEventSink(owner-client,TRUE)", keys->AdviseKeyEventSink(owner, sink.Get(), TRUE));
    if (SUCCEEDED(hr)) Stage("control.keys.UnadviseKeyEventSink(owner-client)", keys->UnadviseKeyEventSink(owner));
}
struct OfflineEditAccess {
    static HRESULT ReplaceText(Service* service, ITfContext* context, TfEditCookie cookie, char kind, const std::wstring& text) {
        service->context = context;
        return service->Replace(cookie, kind, text);
    }
};
class Store final : public ITextStoreACP, public ITfContextOwnerCompositionSink {
    ULONG refs = 1;
    ComPtr<ITextStoreACPSink> sink;
    DWORD lock = 0, pending = 0;
    bool Valid(LONG start, LONG end) const { return start >= 0 && end >= start && end <= static_cast<LONG>(text.size()); }
public:
    std::wstring text = L"ABTAIL";
    TS_SELECTION_ACP selection{2, 2, {TS_AE_NONE, FALSE}};
    unsigned writes = 0, unlockedWrites = 0, starts = 0, ends = 0, grants = 0;
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id == IID_IUnknown || id == IID_ITextStoreACP) *out = static_cast<ITextStoreACP*>(this);
        else if (id == IID_ITfContextOwnerCompositionSink) *out = static_cast<ITfContextOwnerCompositionSink*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    STDMETHODIMP AdviseSink(REFIID id, IUnknown* object, DWORD) override {
        if (id != IID_ITextStoreACPSink || !object) return E_INVALIDARG;
        if (sink && Same(object, sink.Get())) return S_OK;
        if (sink) return CONNECT_E_ADVISELIMIT;
        return object->QueryInterface(IID_PPV_ARGS(&sink));
    }
    STDMETHODIMP UnadviseSink(IUnknown* object) override {
        if (!Same(object, sink.Get())) return CONNECT_E_NOCONNECTION;
        sink.Reset(); return S_OK;
    }
    STDMETHODIMP RequestLock(DWORD flags, HRESULT* result) override {
        if (!result || !sink) return E_INVALIDARG;
        if (lock) {
            if (flags & TS_LF_SYNC) { *result = TS_E_SYNCHRONOUS; return S_OK; }
            pending |= flags; *result = TS_S_ASYNC; return S_OK;
        }
        lock = flags; ++grants; *result = sink->OnLockGranted(flags); lock = 0;
        while (pending) {
            DWORD next = pending; pending = 0; lock = next; ++grants;
            sink->OnLockGranted(next); lock = 0;
        }
        return S_OK;
    }
    STDMETHODIMP GetStatus(TS_STATUS* value) override { if (!value) return E_POINTER; *value = {0, TS_SS_NOHIDDENTEXT}; return S_OK; }
    STDMETHODIMP QueryInsert(LONG start, LONG end, ULONG, LONG* first, LONG* last) override {
        if (!first || !last || !Valid(start, end)) return E_INVALIDARG;
        *first = start; *last = end; return S_OK;
    }
    STDMETHODIMP GetSelection(ULONG index, ULONG count, TS_SELECTION_ACP* value, ULONG* fetched) override {
        if (!(lock & TS_LF_READ)) return TS_E_NOLOCK;
        if (!value || !fetched || !count || (index != 0 && index != TS_DEFAULT_SELECTION)) return E_INVALIDARG;
        *value = selection; *fetched = 1; return S_OK;
    }
    STDMETHODIMP SetSelection(ULONG count, const TS_SELECTION_ACP* value) override {
        if ((lock & TS_LF_READWRITE) != TS_LF_READWRITE) return TS_E_NOLOCK;
        if (count != 1 || !value || !Valid(value->acpStart, value->acpEnd)) return E_INVALIDARG;
        selection = *value; return S_OK;
    }
    STDMETHODIMP GetText(LONG start, LONG end, WCHAR* buffer, ULONG capacity, ULONG* copied,
        TS_RUNINFO* runs, ULONG runCapacity, ULONG* runCount, LONG* next) override {
        if (!(lock & TS_LF_READ)) return TS_E_NOLOCK;
        if (end == -1) end = static_cast<LONG>(text.size());
        if (!copied || !runCount || !next || !Valid(start, end)) return E_INVALIDARG;
        ULONG n = std::min(static_cast<ULONG>(end - start), capacity);
        if (n && !buffer) return E_POINTER;
        if (n) std::memcpy(buffer, text.data() + start, n * sizeof(wchar_t));
        *copied = n; *runCount = 0; *next = start + n;
        if (runCapacity && runs) { runs[0] = {n, TS_RT_PLAIN}; *runCount = n ? 1 : 0; }
        return S_OK;
    }
    STDMETHODIMP SetText(DWORD, LONG start, LONG end, const WCHAR* value, ULONG size, TS_TEXTCHANGE* change) override {
        TS_SELECTION_ACP replace{start, end, {TS_AE_NONE, FALSE}};
        HRESULT hr = SetSelection(1, &replace);
        return FAILED(hr) ? hr : InsertTextAtSelection(TS_IAS_NOQUERY, value, size, nullptr, nullptr, change);
    }
    STDMETHODIMP InsertTextAtSelection(DWORD flags, const WCHAR* value, ULONG size, LONG* start, LONG* end, TS_TEXTCHANGE* change) override {
        if ((lock & TS_LF_READWRITE) != TS_LF_READWRITE) { ++unlockedWrites; return TS_E_NOLOCK; }
        LONG first = selection.acpStart, last = selection.acpEnd;
        if (start) *start = first;
        if (end) *end = first + size;
        if (flags & TS_IAS_QUERYONLY) return S_OK;
        if (!value && size) return E_POINTER;
        if (!change) return E_POINTER;
        text.replace(first, last - first, value ? value : L"", size);
        *change = {first, last, first + static_cast<LONG>(size)};
        selection = {change->acpNewEnd, change->acpNewEnd, {TS_AE_NONE, FALSE}};
        ++writes; return S_OK; // TSF updates its real live ranges; no nested OnTextChange notification.
    }
    void MoveCaret(LONG position) {
        selection = {position, position, {TS_AE_NONE, FALSE}};
        if (sink) sink->OnSelectionChange();
    }
    STDMETHODIMP GetFormattedText(LONG, LONG, IDataObject** out) override { if (out) *out = nullptr; return E_NOTIMPL; }
    STDMETHODIMP GetEmbedded(LONG, REFGUID, REFIID, IUnknown** out) override { if (out) *out = nullptr; return TS_E_NOOBJECT; }
    STDMETHODIMP QueryInsertEmbedded(const GUID*, const FORMATETC*, BOOL* value) override { if (!value) return E_POINTER; *value = FALSE; return S_OK; }
    STDMETHODIMP InsertEmbedded(DWORD, LONG, LONG, IDataObject*, TS_TEXTCHANGE*) override { return E_NOTIMPL; }
    STDMETHODIMP InsertEmbeddedAtSelection(DWORD, IDataObject*, LONG*, LONG*, TS_TEXTCHANGE*) override { return E_NOTIMPL; }
    STDMETHODIMP RequestSupportedAttrs(DWORD, ULONG, const TS_ATTRID*) override { return S_OK; }
    STDMETHODIMP RequestAttrsAtPosition(LONG, ULONG, const TS_ATTRID*, DWORD) override { return S_OK; }
    STDMETHODIMP RequestAttrsTransitioningAtPosition(LONG, ULONG, const TS_ATTRID*, DWORD) override { return S_OK; }
    STDMETHODIMP FindNextAttrTransition(LONG, LONG halt, ULONG, const TS_ATTRID*, DWORD, LONG* next, BOOL* found, LONG* offset) override {
        if (!next || !found || !offset) return E_POINTER;
        *next = halt; *found = FALSE; *offset = 0; return S_OK;
    }
    STDMETHODIMP RetrieveRequestedAttrs(ULONG, TS_ATTRVAL*, ULONG* fetched) override { if (!fetched) return E_POINTER; *fetched = 0; return S_OK; }
    STDMETHODIMP GetEndACP(LONG* value) override { if (!value) return E_POINTER; *value = static_cast<LONG>(text.size()); return S_OK; }
    STDMETHODIMP GetActiveView(TsViewCookie* value) override { if (!value) return E_POINTER; *value = 0; return S_OK; }
    STDMETHODIMP GetACPFromPoint(TsViewCookie, const POINT*, DWORD, LONG*) override { return E_NOTIMPL; }
    STDMETHODIMP GetTextExt(TsViewCookie, LONG, LONG, RECT* rect, BOOL* clipped) override {
        if (!rect || !clipped) return E_POINTER; *rect = {0, 0, 100, 20}; *clipped = FALSE; return S_OK;
    }
    STDMETHODIMP GetScreenExt(TsViewCookie, RECT* rect) override { if (!rect) return E_POINTER; *rect = {0, 0, 100, 20}; return S_OK; }
    STDMETHODIMP GetWnd(TsViewCookie, HWND* value) override { if (!value) return E_POINTER; *value = nullptr; return S_OK; }
    STDMETHODIMP OnStartComposition(ITfCompositionView*, BOOL* allowed) override { if (!allowed) return E_POINTER; *allowed = TRUE; ++starts; return S_OK; }
    STDMETHODIMP OnUpdateComposition(ITfCompositionView*, ITfRange*) override { return S_OK; }
    STDMETHODIMP OnEndComposition(ITfCompositionView*) override { ++ends; return S_OK; }
};
class Apply final : public ITfEditSession {
    ULONG refs = 1;
    Service* service;
    ComPtr<ITfContext> context;
    char kind;
    std::wstring text;
public:
    bool done = false; HRESULT result = E_PENDING;
    Apply(Service* s, ITfContext* c, char k, const wchar_t* t) : service(s), context(c), kind(k), text(t) { service->AddRef(); }
    ~Apply() { service->Release(); }
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER; *out = nullptr;
        if (id != IID_IUnknown && id != IID_ITfEditSession) return E_NOINTERFACE;
        *out = this; AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    STDMETHODIMP DoEditSession(TfEditCookie cookie) override {
        result = OfflineEditAccess::ReplaceText(service, context.Get(), cookie, kind, text); done = true; return result;
    }
};
bool Send(Service* service, ITfContext* context, TfClientId client, char kind, const wchar_t* text) {
    ComPtr<Apply> edit; edit.Attach(new Apply(service, context, kind, text));
    HRESULT session = E_PENDING;
    HRESULT hr = context->RequestEditSession(client, edit.Get(), TF_ES_ASYNC | TF_ES_READWRITE, &session);
    ULONGLONG deadline = GetTickCount64() + 2000;
    while (SUCCEEDED(hr) && SUCCEEDED(session) && !edit->done && GetTickCount64() < deadline) {
        MSG msg{}; while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        if (!edit->done) MsgWaitForMultipleObjects(0, nullptr, FALSE, 20, QS_ALLINPUT);
    }
    std::printf("edit frame=%c request=0x%08lX session=0x%08lX done=%d result=0x%08lX\n",
        kind, static_cast<unsigned long>(hr), static_cast<unsigned long>(session), edit->done, static_cast<unsigned long>(edit->result));
    return SUCCEEDED(hr) && SUCCEEDED(session) && edit->done && edit->result == S_OK;
}
}
int wmain() {
    if (FAILED(Stage("CoInitializeEx(STA)", CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)))) return 3;
    unsigned failures = 0;
    auto check = [&](bool ok, const char* name) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failures; return ok; };
    {
        ModuleInstance = GetModuleHandleW(nullptr);
        ComPtr<ITfThreadMgrEx> manager; ComPtr<ITfDocumentMgr> doc; ComPtr<ITfContext> context;
        ComPtr<Store> store; store.Attach(new Store);
        TfClientId client = TF_CLIENTID_NULL; TfEditCookie cookie = 0;
        HRESULT hr = Stage("CoCreateInstance(TF_ThreadMgr)", CoCreateInstance(CLSID_TF_ThreadMgr, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&manager)));
        if (SUCCEEDED(hr)) hr = Stage("thread.ActivateEx(NOACTIVATETIP|NOACTIVATEKEYBOARDLAYOUT)", manager->ActivateEx(&client, TF_TMAE_NOACTIVATETIP | TF_TMAE_NOACTIVATEKEYBOARDLAYOUT));
        bool activated = SUCCEEDED(hr);
        if (SUCCEEDED(hr)) hr = Stage("thread.CreateDocumentMgr", manager->CreateDocumentMgr(&doc));
        if (SUCCEEDED(hr)) hr = Stage("document.CreateContext(memory TextStore)", doc->CreateContext(client, 0, static_cast<ITextStoreACP*>(store.Get()), &context, &cookie));
        if (SUCCEEDED(hr)) hr = Stage("document.Push", doc->Push(context.Get()));
        std::printf("setup HRESULT=0x%08lX\n", static_cast<unsigned long>(hr));
        if (check(SUCCEEDED(hr), "real TSF thread/document/context with memory TextStore")) {
            ComPtr<Service> service; service.Attach(new Service);
            ComPtr<ITfClientId> ids; TfClientId serviceClient = TF_CLIENTID_NULL;
            ComPtr<ITfDocumentMgr> focused; ComPtr<ITfContext> top;
            Stage("preflight.thread.GetFocus", manager->GetFocus(&focused));
            Stage("preflight.created-document.GetTop", doc->GetTop(&top));
            std::printf("preflight focused-document=%d created-top-is-context=%d\n", focused != nullptr, Same(top.Get(), context.Get()));
            hr = Stage("thread.QueryInterface(ITfClientId)", manager.As(&ids));
            if (SUCCEEDED(hr)) hr = Stage("client.GetClientId(VoiceClsid)", ids->GetClientId(VoiceClsid, &serviceClient));
            ComPtr<TracedThreadMgr> traced; traced.Attach(new TracedThreadMgr(manager.Get()));
            if (SUCCEEDED(hr)) hr = Stage("service.Activate(traced real manager)", service->Activate(traced.Get(), serviceClient));
            std::printf("service Activate HRESULT=0x%08lX owner-client=%lu service-client=%lu\n",
                static_cast<unsigned long>(hr), client, serviceClient);
            std::printf("activation first-failure=%s HRESULT=0x%08lX production-GetFocus-calls=%u\n",
                traced->firstFailure ? traced->firstFailure : "none", static_cast<unsigned long>(traced->firstResult), traced->focusCalls);
            if (FAILED(hr) && serviceClient != TF_CLIENTID_NULL) {
                HRESULT direct = Stage("control.service.Activate(unwrapped real manager)", service->Activate(manager.Get(), serviceClient));
                check(direct == hr, "unwrapped control preserves activation failure");
                if (SUCCEEDED(direct)) service->Deactivate();
                InspectClient(manager.Get(), service.Get(), client, serviceClient);
            }
            if (check(SUCCEEDED(hr), "production ITfTextInputProcessor Activate on real TSF thread")) {
                BOOL eaten = TRUE;
                service->OnSetFocus(TRUE);
                check(service->OnTestKeyDown(context.Get(), VK_F8, 0, &eaten) == S_OK && !eaten &&
                    service->OnKeyDown(context.Get(), VK_F8, 0, &eaten) == S_OK && !eaten, "unselected profile F8 never starts host");
                std::puts("SCOPE production Replace under real async edit lock; profile eligibility/pipe intentionally bypassed");
                check(Send(service.Get(), context.Get(), serviceClient, 'P', L"你") && store->text == L"AB你TAIL" && store->selection.acpStart == 3, "partial inserts at selection, suffix intact");
                check(Send(service.Get(), context.Get(), serviceClient, 'P', L"你好") && store->text == L"AB你好TAIL" && store->selection.acpStart == 4, "partial replaces owned range without duplication");
                store->MoveCaret(static_cast<LONG>(store->text.size()));
                check(Send(service.Get(), context.Get(), serviceClient, 'F', L"你好。") && store->text == L"AB你好。TAIL" && store->selection.acpStart == 9, "final preserves moved selection across length delta");
                store->MoveCaret(0);
                check(Send(service.Get(), context.Get(), serviceClient, 'F', L"前") && store->text == L"前AB你好。TAIL" && store->selection.acpStart == 1, "next sentence reads new selection");
                check(store->starts == 2 && store->ends == 2 && store->writes == 4 && store->unlockedWrites == 0 && store->grants > 0, "composition lifecycle and real write locks");
            } else {
                std::printf("NOT-RUN partial/final: production activation gate failed; writes=%u\n", store->writes);
            }
            service->Deactivate(); service.Reset();
        }
        if (doc) doc->Pop(TF_POPF_ALL);
        context.Reset(); doc.Reset();
        if (activated) manager->Deactivate();
        manager.Reset(); store.Reset();
    }
    check(workerStarts == 0 && ModuleObjects == 0, "no host start and no retained native COM objects");
    CoUninitialize();
    std::printf("RESULT failures=%u; offline range scope only; no registry/config/cloud/microphone\n", failures);
    return failures ? 1 : 0;
}