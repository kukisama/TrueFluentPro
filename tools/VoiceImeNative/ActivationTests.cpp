// System activation acceptance host. No VoiceIme.cpp, factory, pipe worker or registration code.
// Only Windows creates/activates the registered TIP. Never dispatch KeyDown(F8).
#include "Common.h"
#include <textstor.h>
#include <olectl.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <gdiplus.h>
#include <dwmapi.h>
#include <cmath>
#include <climits>
#include <cstddef>
#include <cwctype>
#include <psapi.h>

namespace {
struct Failure {};
std::wstring ClassName(HWND hwnd) {
    wchar_t value[256]{}; GetClassNameW(hwnd, value, 256); return value;
}
std::wstring WindowText(HWND hwnd) {
    wchar_t value[256]{}; GetWindowTextW(hwnd, value, 256); return value;
}
std::wstring ModulePath(HMODULE module) {
    wchar_t value[32768]{};
    DWORD n = module ? GetModuleFileNameW(module, value, 32768) : 0;
    return n && n < 32768 ? value : L"";
}
bool Own(HWND hwnd) {
    DWORD pid = 0;
    return hwnd && IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, &pid) == GetCurrentThreadId() && pid == GetCurrentProcessId();
}
bool InputReleased() {
    for (int key : {VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2,
        VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN}) if (GetAsyncKeyState(key) < 0) return false;
    return true;
}
UINT Dpi(HWND hwnd) {
    using Function = UINT (WINAPI*)(HWND);
    auto fn = reinterpret_cast<Function>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForWindow"));
    return fn ? fn(hwnd) : 0;
}
bool Pmv2(HWND hwnd = nullptr) {
    using GetContext = HANDLE (WINAPI*)(HWND);
    using GetThreadContext = HANDLE (WINAPI*)();
    using Equal = BOOL (WINAPI*)(HANDLE, HANDLE);
    HMODULE user = GetModuleHandleW(L"user32.dll");
    auto windowContext = reinterpret_cast<GetContext>(GetProcAddress(user, "GetWindowDpiAwarenessContext"));
    auto threadContext = reinterpret_cast<GetThreadContext>(GetProcAddress(user, "GetThreadDpiAwarenessContext"));
    auto equal = reinterpret_cast<Equal>(GetProcAddress(user, "AreDpiAwarenessContextsEqual"));
    return equal && windowContext && threadContext && equal(hwnd ? windowContext(hwnd) : threadContext(), reinterpret_cast<HANDLE>(-4));
}
std::string Utf8(const std::wstring& value) {
    if (value.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), n, nullptr, nullptr);
    return result;
}
std::string Json(const std::string& value) {
    std::string result = "\"";
    for (unsigned char c : value) {
        if (c == '\\' || c == '"') { result += '\\'; result += static_cast<char>(c); }
        else if (c < 32) { char escaped[7]{}; sprintf_s(escaped, "\\u%04x", c); result += escaped; }
        else result += static_cast<char>(c);
    }
    return result + '"';
}
struct Report {
    std::map<std::string, std::string> values;
    unsigned failures = 0;
    void Text(const char* key, const std::string& value) { values[key] = Json(value); }
    void Number(const char* key, uint64_t value) { values[key] = std::to_string(value); }
    void Bool(const char* key, bool value) { values[key] = value ? "true" : "false"; }
    void Point(const char* key, POINT value) {
        values[key] = "{\"X\":" + std::to_string(value.x) + ",\"Y\":" + std::to_string(value.y) + "}";
    }
    void Rect(const char* key, RECT value) {
        values[key] = "{\"Left\":" + std::to_string(value.left) + ",\"Top\":" + std::to_string(value.top) +
            ",\"Right\":" + std::to_string(value.right) + ",\"Bottom\":" + std::to_string(value.bottom) + "}";
    }
    void Hr(const char* key, HRESULT hr) {
        char value[16]{}; sprintf_s(value, "0x%08lX", static_cast<unsigned long>(hr)); Text(key, value);
        std::printf("%s=%s\n", key, value); std::fflush(stdout);
    }
    void Check(const char* key, bool ok, bool stop = true) {
        Bool(key, ok); std::printf("%s %s\n", ok ? "PASS" : "FAIL", key); std::fflush(stdout);
        if (!ok) {
            ++failures;
            if (!values.count("FirstFailure")) Text("FirstFailure", key);
            if (stop) throw Failure{};
        }
    }
    void Require(const char* key, HRESULT hr) {
        Hr(key, hr);
        if (hr != S_OK) Check((std::string(key) + "Succeeded").c_str(), false);
    }
    void Profile(const char* key, const TF_INPUTPROCESSORPROFILE& p) {
        values[key] = "{\"Type\":" + std::to_string(p.dwProfileType) + ",\"LangId\":" + std::to_string(p.langid) +
            ",\"Clsid\":" + Json(Utf8(GuidString(p.clsid))) + ",\"Profile\":" + Json(Utf8(GuidString(p.guidProfile))) +
            ",\"Hkl\":" + std::to_string(reinterpret_cast<uintptr_t>(p.hkl)) + "}";
    }
    bool Save(const wchar_t* path) {
        std::ofstream file(std::filesystem::path(path), std::ios::binary);
        file << "{\n"; bool first = true;
        for (const auto& pair : values) {
            if (!first) file << ",\n"; first = false;
            file << "  " << Json(pair.first) << ": " << pair.second;
        }
        file << "\n}\n"; file.close(); return !file.fail();
    }
};
bool Same(IUnknown* a, IUnknown* b) {
    if (!a || !b) return a == b;
    ComPtr<IUnknown> x, y;
    return SUCCEEDED(a->QueryInterface(IID_PPV_ARGS(&x))) && SUCCEEDED(b->QueryInterface(IID_PPV_ARGS(&y))) && x.Get() == y.Get();
}
bool SameProfile(const TF_INPUTPROCESSORPROFILE& a, const TF_INPUTPROCESSORPROFILE& b) {
    return a.dwProfileType == b.dwProfileType && a.langid == b.langid && a.clsid == b.clsid &&
        a.guidProfile == b.guidProfile && a.hkl == b.hkl;
}
bool Voice(const TF_INPUTPROCESSORPROFILE& p) {
    return p.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && p.langid == 0x0804 &&
        p.clsid == VoiceClsid && p.guidProfile == VoiceProfile;
}

// Fixed-width metadata plus bounded UTF-16 paths, identical for x86 and x64. No remote object pointer.
#pragma pack(push, 1)
struct OwnerWindow {
    DWORD error = ERROR_INVALID_WINDOW_HANDLE, pid = 0, thread = 0;
    uint64_t module = 0, created = 0;
    wchar_t exe[32768]{}, path[32768]{};
};
#pragma pack(pop)
bool ToolbarCandidate(HWND hwnd) { return IsWindow(hwnd) && ClassName(hwnd).find(L"TrueFluentVoiceIme.Toolbar.") == 0; }
OwnerWindow QueryOwnerWindow(HWND hwnd, DWORD expectedPid, const wchar_t* expectedExe) {
    OwnerWindow value;
#ifdef _WIN64
    value.thread = GetWindowThreadProcessId(hwnd, &value.pid);
    if (!ToolbarCandidate(hwnd) || !value.thread || !value.pid || (expectedPid && value.pid != expectedPid)) return value;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, value.pid);
    if (!process) { value.error = GetLastError(); return value; }
    DWORD count = 32768, ownSession = 0, session = 0; BOOL wow = TRUE; FILETIME created{}, exited{}, kernel{}, user{};
    wchar_t standaloneExe[32768]{}; DWORD sBytes = sizeof(standaloneExe); HKEY sKey{};
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\TrueFluentPro\\VoiceImeUI", 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &sKey) == ERROR_SUCCESS) {
        RegGetValueW(sKey, nullptr, L"StandaloneHostExe", RRF_RT_REG_SZ, nullptr, standaloneExe, &sBytes);
        RegCloseKey(sKey);
    }
    const auto matchesExe = [&](const wchar_t* actualExe) {
        return !_wcsicmp(actualExe, expectedExe) || (standaloneExe[0] && !_wcsicmp(actualExe, standaloneExe));
    };
    if (!QueryFullProcessImageNameW(process, 0, value.exe, &count) || !count || count >= 32768 ||
        !IsWow64Process(process, &wow) || !GetProcessTimes(process, &created, &exited, &kernel, &user) ||
        !ProcessIdToSessionId(value.pid, &session) || !ProcessIdToSessionId(GetCurrentProcessId(), &ownSession)) {
        value.error = GetLastError(); if (!value.error) value.error = ERROR_INVALID_DATA;
    }
    else if (wow || session != ownSession || !matchesExe(value.exe)) value.error = ERROR_INVALID_DATA;
    else {
        value.created = (uint64_t(created.dwHighDateTime) << 32) | created.dwLowDateTime;
        HANDLE modules = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, value.pid);
        if (!modules) value.error = GetLastError();
        else {
            FILETIME checked{}, a{}, b{}, c{}; wchar_t checkedExe[32768]{}; count = 32768;
            const bool sameProcess = GetProcessTimes(modules, &checked, &a, &b, &c) &&
                checked.dwLowDateTime == created.dwLowDateTime && checked.dwHighDateTime == created.dwHighDateTime &&
                QueryFullProcessImageNameW(modules, 0, checkedExe, &count) && count < 32768 && matchesExe(checkedExe);
            SetLastError(0);
            const auto module = sameProcess ? reinterpret_cast<HMODULE>(GetClassLongPtrW(hwnd, GCLP_HMODULE)) : nullptr;
            value.module = reinterpret_cast<uintptr_t>(module);
            const DWORD length = module ? GetModuleFileNameExW(modules, module, value.path, 32768) : 0;
            DWORD pid = 0;
            value.error = length && length < 32768 && GetWindowThreadProcessId(hwnd, &pid) == value.thread &&
                pid == value.pid && ToolbarCandidate(hwnd) ? ERROR_SUCCESS : ERROR_INVALID_DATA;
            CloseHandle(modules);
        }
    }
    CloseHandle(process);
#else
    (void)hwnd; (void)expectedPid; (void)expectedExe;
    value.error = ERROR_NOT_SUPPORTED; // An x86 HMODULE cannot represent the x64 owner's class module.
#endif
    return value;
}

// Minimal test-only TextStore contract, adapted from EditTests.Store. No composition/range algorithm.
class Store final : public ITextStoreACP {
    ULONG refs = 1;
    ComPtr<ITextStoreACPSink> sink;
    DWORD lock = 0, pending = 0;
    TS_SELECTION_ACP selection{2, 2, {TS_AE_NONE, FALSE}};
    bool Valid(LONG start, LONG end) const { return start >= 0 && end >= start && end <= static_cast<LONG>(text.size()); }
public:
    HWND window{};
    bool readOnly = false;
    unsigned grants = 0, writes = 0;
    std::wstring text = L"ABTAIL";
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (id != IID_IUnknown && id != IID_ITextStoreACP) return E_NOINTERFACE;
        *out = static_cast<ITextStoreACP*>(this); AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    STDMETHODIMP AdviseSink(REFIID id, IUnknown* object, DWORD) override {
        if (id != IID_ITextStoreACPSink || !object) return E_INVALIDARG;
        if (sink) return Same(object, sink.Get()) ? S_OK : CONNECT_E_ADVISELIMIT;
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
            DWORD next = pending; pending = 0; lock = next; ++grants; sink->OnLockGranted(next); lock = 0;
        }
        return S_OK;
    }
    STDMETHODIMP GetStatus(TS_STATUS* value) override {
        if (!value) return E_POINTER;
        *value = {readOnly ? TS_SD_READONLY : 0u, TS_SS_NOHIDDENTEXT}; return S_OK;
    }
    HRESULT SetReadOnly(bool value) {
        readOnly = value;
        return sink ? sink->OnStatusChange(TS_SD_READONLY) : E_UNEXPECTED;
    }
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
        if (runCapacity && runs && n) { runs[0] = {n, TS_RT_PLAIN}; *runCount = 1; }
        return S_OK;
    }
    STDMETHODIMP SetText(DWORD, LONG start, LONG end, const WCHAR* value, ULONG size, TS_TEXTCHANGE* change) override {
        TS_SELECTION_ACP replace{start, end, {TS_AE_NONE, FALSE}};
        HRESULT hr = SetSelection(1, &replace);
        return FAILED(hr) ? hr : InsertTextAtSelection(TS_IAS_NOQUERY, value, size, nullptr, nullptr, change);
    }
    STDMETHODIMP InsertTextAtSelection(DWORD flags, const WCHAR* value, ULONG size, LONG* start, LONG* end, TS_TEXTCHANGE* change) override {
        if ((lock & TS_LF_READWRITE) != TS_LF_READWRITE) return TS_E_NOLOCK;
        if (readOnly) return TS_E_READONLY;
        LONG first = selection.acpStart, last = selection.acpEnd;
        if (start) *start = first;
        if (end) *end = first + size;
        if (flags & TS_IAS_QUERYONLY) return S_OK;
        if ((!value && size) || !change) return E_POINTER;
        text.replace(first, last - first, value ? value : L"", size);
        *change = {first, last, first + static_cast<LONG>(size)};
        selection = {change->acpNewEnd, change->acpNewEnd, {TS_AE_NONE, FALSE}};
        ++writes; return S_OK;
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
    STDMETHODIMP GetEndACP(LONG* value) override {
        if (!(lock & TS_LF_READ)) return TS_E_NOLOCK;
        if (!value) return E_POINTER;
        *value = static_cast<LONG>(text.size()); return S_OK;
    }
    STDMETHODIMP GetActiveView(TsViewCookie* value) override { if (!value) return E_POINTER; *value = 0; return S_OK; }
    STDMETHODIMP GetACPFromPoint(TsViewCookie, const POINT*, DWORD, LONG*) override { return E_NOTIMPL; }
    STDMETHODIMP GetTextExt(TsViewCookie, LONG, LONG, RECT* rect, BOOL* clipped) override {
        if (!rect || !clipped) return E_POINTER;
        if (!(lock & TS_LF_READ)) return TS_E_NOLOCK;
        GetWindowRect(window, rect); *clipped = FALSE; return S_OK;
    }
    STDMETHODIMP GetScreenExt(TsViewCookie, RECT* rect) override { return rect && GetWindowRect(window, rect) ? S_OK : E_INVALIDARG; }
    STDMETHODIMP GetWnd(TsViewCookie, HWND* value) override { if (!value) return E_POINTER; *value = window; return S_OK; }
};

class LockProbe final : public ITfEditSession {
    ULONG refs = 1;
    ComPtr<ITfContext> context;
public:
    bool done = false;
    HRESULT result = E_PENDING;
    explicit LockProbe(ITfContext* c) : context(c) {}
    STDMETHODIMP QueryInterface(REFIID id, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (id != IID_IUnknown && id != IID_ITfEditSession) return E_NOINTERFACE;
        *out = static_cast<ITfEditSession*>(this); AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG n = --refs; if (!n) delete this; return n; }
    STDMETHODIMP DoEditSession(TfEditCookie cookie) override {
        TF_SELECTION selection{}; ULONG count = 0;
        result = context->GetSelection(cookie, TF_DEFAULT_SELECTION, 1, &selection, &count);
        if (result == S_OK && count == 1 && selection.range) result = context->SetSelection(cookie, 1, &selection);
        else if (SUCCEEDED(result)) result = E_FAIL;
        if (selection.range) selection.range->Release();
        done = true; return result;
    }
};

class Host {
    Report& report;
    std::wstring reportPath, registeredDll, expectedDll;
    std::wstring expectedOwnerExe, expectedOwnerModule, ownerQueryExe;
    HWND ownerMain{}, ownerTip{}, ownerNotice{};
    DWORD ownerPid = 0, ownerThread = 0;
    uint64_t ownerCreated = 0;
    bool hoverSent = false;
    bool switchOnly = false;
    bool com = false, activated = false, associated = false, pushed = false, attempted = false, closed = false, cancelled = false;
    HWND window{};
    HWND originalForeground{}, originalFocus{}, serviceDispatcher{};
    DWORD originalForegroundPid = 0, originalForegroundThread = 0;
    bool temporaryTopmost = false;
    unsigned serviceTimerUpdates = 0, serviceFrameMessages = 0;
    unsigned serviceIdleFrames = 0, serviceNonIdleFrames = 0;
    ULONGLONG activationStarted = 0, firstServiceTimerElapsed = 0, lastServiceTimerAt = 0;
    ATOM windowClass = 0;
    ComPtr<ITfThreadMgr> manager;
    ComPtr<ITfInputProcessorProfileMgr> profiles;
    ComPtr<ITfKeystrokeMgr> keys;
    ComPtr<ITfDocumentMgr> doc, previous, emptyDoc, noDocumentPrevious;
    bool noDocumentFocusPending = false;
    ComPtr<ITfContext> context;
    ComPtr<Store> store;
    TF_INPUTPROCESSORPROFILE original{};
    TfClientId owner = TF_CLIENTID_NULL;
    POINT originalCursor{};
    bool cursorSaved = false, mouseAttempted = false;
    unsigned clickDown = 0, clickUp = 0;
    static constexpr ULONG_PTR MouseTag = 0x56494D45;
    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
        Host* self = reinterpret_cast<Host*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (message == WM_NCCREATE) {
            self = static_cast<Host*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        if (self && message == WM_CLOSE) { self->cancelled = true; return 0; }
        if (self && static_cast<ULONG_PTR>(GetMessageExtraInfo()) == MouseTag) {
            if (message == WM_LBUTTONDOWN) ++self->clickDown;
            if (message == WM_LBUTTONUP) ++self->clickUp;
        }
        if (message == WM_PAINT) {
            PAINTSTRUCT ps{}; HDC dc = BeginPaint(hwnd, &ps); RECT rect{}; GetClientRect(hwnd, &rect);
            DrawTextW(dc, L"TSF 真实激活验收（不启动录音）\n自有 TextStore：AB|TAIL\n仅查询 A / F8 资格，完成后自动关闭。", -1, &rect, DT_LEFT | DT_TOP);
            EndPaint(hwnd, &ps); return 0;
        }
        // Even physical keys cannot accidentally start the production voice backend in this host.
        if (message >= WM_KEYFIRST && message <= WM_KEYLAST) return 0;
        return DefWindowProcW(hwnd, message, w, l);
    }
    template<class Predicate> bool Wait(Predicate ready, DWORD milliseconds, bool cleanup = false) {
        ULONGLONG deadline = GetTickCount64() + milliseconds;
        do {
            MSG msg{};
            while (GetTickCount64() < deadline && PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                if (msg.message == WM_QUIT) { cancelled = true; break; }
                // No TranslateMessage / ITfKeystrokeMgr::KeyDown on real or synthetic keys.
                if (msg.message >= WM_KEYFIRST && msg.message <= WM_KEYLAST) continue;
                // Observe the actual production message-only dispatcher. No substituted timer or service.
                bool serviceMessage = activationStarted && (msg.message == WM_TIMER || msg.message == FrameMessage) &&
                    Own(msg.hwnd) && ClassName(msg.hwnd).find(L"TrueFluentVoiceIme.Dispatch.") == 0 &&
                    _wcsicmp(ModulePath(reinterpret_cast<HMODULE>(GetClassLongPtrW(msg.hwnd, GCLP_HMODULE))).c_str(), registeredDll.c_str()) == 0;
                bool serviceTimer = serviceMessage && msg.message == WM_TIMER && msg.wParam == 1 && !msg.lParam;
                if (serviceMessage && msg.message == FrameMessage) {
                    ++serviceFrameMessages;
                    const auto* frame = reinterpret_cast<const Frame*>(msg.lParam); // Only our real local dispatcher packet, before Dispatch deletes it.
                    if (frame && (frame->kind == 'I' || frame->kind == 'S') && frame->targetGeneration == 0) ++serviceIdleFrames;
                    else ++serviceNonIdleFrames;
                }
                DispatchMessageW(&msg);
                if (serviceTimer && GetTickCount64() - activationStarted >= 500) {
                    serviceDispatcher = msg.hwnd;
                    lastServiceTimerAt = GetTickCount64();
                    if (!serviceTimerUpdates) firstServiceTimerElapsed = GetTickCount64() - activationStarted;
                    ++serviceTimerUpdates;
                }
            }
            if (cancelled && !cleanup) return false;
            if (ready()) return true;
            ULONGLONG now = GetTickCount64();
            if (now >= deadline) break;
            DWORD result = MsgWaitForMultipleObjectsEx(0, nullptr,
                static_cast<DWORD>(std::min<ULONGLONG>(25, deadline - now)), QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (result == WAIT_FAILED) { report.Number("MessageWaitError", GetLastError()); return false; }
        } while (GetTickCount64() < deadline);
        return false;
    }
    void ObtainForegroundByOwnClick() {
        // Never switch desktops or attach input queues. Refuse an inaccessible/different input desktop.
        wchar_t threadName[256]{}, inputName[256]{}; DWORD needed = 0;
        BOOL threadNamed = GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), UOI_NAME,
            threadName, sizeof(threadName), &needed);
        report.Number("ThreadDesktopQueryError", threadNamed ? 0 : GetLastError());
        HDESK input = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
        report.Number("OpenInputDesktopError", input ? 0 : GetLastError());
        BOOL inputNamed = input && GetUserObjectInformationW(input, UOI_NAME, inputName, sizeof(inputName), &needed);
        report.Number("InputDesktopQueryError", inputNamed ? 0 : GetLastError());
        if (input) CloseDesktop(input);
        report.Text("ThreadDesktop", Utf8(threadName)); report.Text("InputDesktop", Utf8(inputName));
        report.Check("OnAccessibleInputDesktop", threadNamed && inputNamed && _wcsicmp(threadName, inputName) == 0);
        report.Check("PhysicalMouseCoordinateSpace", IsProcessDPIAware() != FALSE);

        cursorSaved = GetCursorPos(&originalCursor) != FALSE;
        report.Number("SaveCursorError", cursorSaved ? 0 : GetLastError());
        report.Check("OriginalCursorSaved", cursorSaved); report.Point("OriginalCursor", originalCursor);
        report.Number("MouseMoveSendInputRequested", 0); report.Number("MouseMoveSendInputSent", 0);
        report.Bool("MouseMoveInserted", false); // No move event is constructed or sent.
        // Complete normal showing before the final TOPMOST placement. Do not let a later ShowWindow
        // rewrite the Z order. Record the actual startup override rather than infer it from CreateNoWindow.
        STARTUPINFOW startup{sizeof(startup)}; GetStartupInfoW(&startup);
        report.Number("StartupFlags", startup.dwFlags); report.Number("StartupShowWindow", startup.wShowWindow);
        report.Bool("StartupOverridesFirstShow", (startup.dwFlags & STARTF_USESHOWWINDOW) != 0);
        MONITORINFO monitor{sizeof(monitor)}; RECT clip{}, area{};
        report.Check("OwnPlacementWorkAreaAvailable", GetMonitorInfoW(MonitorFromPoint(originalCursor, MONITOR_DEFAULTTONEAREST), &monitor) != FALSE);
        report.Check("OwnPlacementCursorClipAvailable", GetClipCursor(&clip) != FALSE);
        report.Check("OwnPlacementSafeAreaAvailable", IntersectRect(&area, &monitor.rcWork, &clip) &&
            area.right - area.left >= 240 && area.bottom - area.top >= 160);
        const UINT dpi = Dpi(window);
        const int w = std::min<int>(MulDiv(620, dpi, 96), area.right - area.left - 16);
        const int h = std::min<int>(MulDiv(220, dpi, 96), area.bottom - area.top - 16);
        report.Rect("OwnPlacementWorkArea", monitor.rcWork); report.Rect("OwnPlacementClipBounds", clip);
        report.Rect("OwnPlacementSafeArea", area);
        report.Check("OwnWindowBeforeShow", Own(window));
        ShowWindow(window, SW_SHOWNORMAL);
        if (startup.dwFlags & STARTF_USESHOWWINDOW) ShowWindow(window, SW_SHOWNOACTIVATE);
        report.Bool("OwnWindowForegroundAfterShow", GetForegroundWindow() == window);
        // Only our test editor is briefly topmost; never raise, move or subclass a user window.
        temporaryTopmost = true;
        report.Check("OwnWindowPlacedTemporarilyTopmost", Own(window) && SetWindowPos(window, HWND_TOPMOST,
            std::clamp<LONG>(originalCursor.x - w / 2, area.left, area.right - w),
            std::clamp<LONG>(originalCursor.y - h / 2, area.top, area.bottom - h),
            w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW));
        // Measure the actual nonclient/DPI geometry after sizing, then align the client center
        // to the saved cursor. Clamp our window, not the cursor; edge positions may be unsafe.
        RECT placed{}, client{};
        report.Check("OwnClientRectAvailable", GetClientRect(window, &client) && client.right > 20 && client.bottom > 20);
        POINT center{(client.left + client.right) / 2, (client.top + client.bottom) / 2};
        report.Check("OwnPlacementClientGeometryAvailable", GetWindowRect(window, &placed) && ClientToScreen(window, &center));
        report.Check("OwnWindowFitsSafeArea", placed.right > placed.left && placed.bottom > placed.top &&
            placed.right - placed.left <= area.right - area.left && placed.bottom - placed.top <= area.bottom - area.top);
        const LONG desiredLeft = placed.left + originalCursor.x - center.x;
        const LONG desiredTop = placed.top + originalCursor.y - center.y;
        const LONG alignedLeft = std::clamp(desiredLeft, area.left, area.right - (placed.right - placed.left));
        const LONG alignedTop = std::clamp(desiredTop, area.top, area.bottom - (placed.bottom - placed.top));
        report.Bool("OwnPlacementClamped", alignedLeft != desiredLeft || alignedTop != desiredTop);
        report.Check("OwnClientPlacedAroundExistingCursor", Own(window) && SetWindowPos(window, HWND_TOPMOST,
            alignedLeft, alignedTop, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW));
        UpdateWindow(window);
        report.Bool("OrdinarySetForegroundWindowReturned", SetForegroundWindow(window) != FALSE);
        report.Check("OwnWindowActuallyVisible", IsWindowVisible(window) && !IsIconic(window));
        const auto ownFocused = [&] { return Own(window) && GetForegroundWindow() == window && GetFocus() == window; };
        const auto safePoint = [&](POINT point) {
            RECT currentClient{}; POINT local = point;
            if (!Own(window) || !IsWindowVisible(window) || !IsWindowEnabled(window) || IsIconic(window) ||
                !GetClientRect(window, &currentClient) || !ScreenToClient(window, &local) ||
                local.x - 2 < currentClient.left || local.y - 2 < currentClient.top ||
                local.x + 2 >= currentClient.right || local.y + 2 >= currentClient.bottom) return false;
            bool safe = true;
            for (LONG dx : {-2L, 0L, 2L}) for (LONG dy : {-2L, 0L, 2L}) {
                HWND hit = WindowFromPoint({point.x + dx, point.y + dy});
                DWORD pid = 0; GetWindowThreadProcessId(hit, &pid);
                safe = safe && hit && GetAncestor(hit, GA_ROOT) == window && pid == GetCurrentProcessId();
            }
            return safe;
        };
        const auto inputAllowed = [&](POINT point) {
            return safePoint(point) && (ownFocused() ||
                (temporaryTopmost && (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOPMOST)));
        };
        const auto clipAllows = [&](POINT point) {
            return GetClipCursor(&clip) && point.x - 2 >= clip.left && point.y - 2 >= clip.top &&
                point.x + 2 < clip.right && point.y + 2 < clip.bottom;
        };
        // Only the current cursor can authorize a click. No alternative patch or mouse-move fallback.
        POINT target{}; bool cursorRead = false; DWORD cursorError = 0;
        const ULONGLONG hitStarted = GetTickCount64();
        bool exposed = Wait([&] {
            cursorRead = GetCursorPos(&target) != FALSE;
            cursorError = cursorRead ? 0 : GetLastError();
            return cursorRead && safePoint(target);
        }, 1500);
        report.Number("OwnHitWaitElapsedMs", GetTickCount64() - hitStarted);
        report.Bool("CurrentCursorAvailableAtHitTest", cursorRead); report.Number("CurrentCursorQueryError", cursorError);
        if (cursorRead) report.Point("CursorAtHitTest", target);
        report.Number("OwnWindow", reinterpret_cast<uintptr_t>(window));
        report.Bool("OwnWindowThreadAndPidVerified", Own(window));
        report.Bool("OwnWindowEnabledAtHitTest", IsWindowEnabled(window) != FALSE);
        report.Bool("OwnWindowTopmostAtHitTest", (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0);
        RECT actualWindow{}; GetWindowRect(window, &actualWindow); report.Rect("OwnWindowRectAtHitTest", actualWindow);
        if (!exposed && cursorRead) {
            std::string hits = "[";
            for (LONG dx : {-2L, 0L, 2L}) for (LONG dy : {-2L, 0L, 2L}) {
                POINT point{target.x + dx, target.y + dy};
                HWND hit = WindowFromPoint(point); DWORD pid = 0; GetWindowThreadProcessId(hit, &pid);
                if (hits.size() > 1) hits += ',';
                hits += "{\"X\":" + std::to_string(point.x) + ",\"Y\":" + std::to_string(point.y) +
                    ",\"Root\":" + std::to_string(reinterpret_cast<uintptr_t>(GetAncestor(hit, GA_ROOT))) +
                    ",\"Pid\":" + std::to_string(pid) + ",\"Class\":" + Json(Utf8(ClassName(hit))) + "}";
            }
            report.values["BlockedOwnCursorHits"] = hits + ']'; // Metadata only; never user text or screenshots.
        }
        report.Check("OwnClientExposedAfterPump", exposed);
        // A denied SetForegroundWindow alone never authorizes SendInput. An inactive window may only
        // receive one ordinary mouse-activation click at the current verified exposed owned client pixel.
        report.Bool("OwnWindowFocusedBeforeMouseInput", ownFocused());
        report.Check("OwnClientReadyForMouseActivation", inputAllowed(target));
        report.Text("MouseInputAuthority", "current cursor in verified exposed owned client only; one LEFTDOWN/LEFTUP pair (2 INPUT); no cursor move; ordinary WM_MOUSEACTIVATE; foreground verified after click");
        report.Point("ClickScreenPoint", target);
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        report.Check("ClickInsideVirtualDesktop", width > 1 && height > 1 && target.x >= left && target.y >= top &&
            target.x < left + width && target.y < top + height);
        report.Check("ClickNotConfinedElsewhere", clipAllows(target));
        INPUT events[2]{};
        for (auto& event : events) { event.type = INPUT_MOUSE; event.mi.dwExtraInfo = MouseTag; }
        events[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN; events[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;
        HWND initialHit = WindowFromPoint(target); DWORD initialHitPid = 0;
        GetWindowThreadProcessId(initialHit, &initialHitPid);
        report.Number("InitialClickHitWindow", reinterpret_cast<uintptr_t>(initialHit));
        report.Number("InitialClickHitPid", initialHitPid);
        GUITHREADINFO gui{sizeof(GUITHREADINFO)};
        report.Check("ForegroundGuiStateAvailable", GetGUIThreadInfo(0, &gui) != FALSE);
        report.Number("MouseCaptureBeforeClick", reinterpret_cast<uintptr_t>(gui.hwndCapture));
        report.Check("NoCapturedMouseOrActiveMenu", !gui.hwndCapture && !GetCapture() &&
            !(gui.flags & (GUI_INMENUMODE | GUI_SYSTEMMENUMODE | GUI_POPUPMENUMODE | GUI_INMOVESIZE)));
        report.Check("NoHeldMouseButtonsOrModifiers", InputReleased());
        // Last gate before SendInput: exact owned HWND/PID, including a guard around the current pixel.
        HWND hit = WindowFromPoint(target); DWORD hitPid = 0;
        GetWindowThreadProcessId(hit, &hitPid);
        report.Number("ClickHitWindow", reinterpret_cast<uintptr_t>(hit)); report.Number("ClickHitPid", hitPid);
        report.Number("OwnWindow", reinterpret_cast<uintptr_t>(window));
        report.Bool("HitWindowTopmost", (GetWindowLongPtrW(hit, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0);
        report.Bool("OwnWindowTopmost", (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0);
        report.Check("ClickTargetVerifiedOwnClient", inputAllowed(target));
        POINT actual{};
        // Re-read the cursor; any changed/unexposed target fails before either button event is sent.
        report.Check("MouseClickReverifiedOwnRootAndPid", GetCursorPos(&actual) && actual.x == target.x && actual.y == target.y &&
            inputAllowed(actual) && clipAllows(actual) && InputReleased() && GetGUIThreadInfo(0, &gui) &&
            !gui.hwndCapture && !GetCapture() && !(gui.flags & (GUI_INMENUMODE | GUI_SYSTEMMENUMODE | GUI_POPUPMENUMODE | GUI_INMOVESIZE)));
        report.Point("CursorAtClick", actual);
        mouseAttempted = true;
        SetLastError(ERROR_SUCCESS);
        UINT sent = SendInput(2, events, sizeof(INPUT)); DWORD error = GetLastError();
        report.Number("MouseSendInputRequested", 2); report.Number("MouseSendInputSent", sent);
        report.Number("MouseSendInputError", error);
        report.Text("MouseInputFailureInterpretation", "Raw SendInput count/error and desktop/observed messages only; UIPI cause is not identifiable from SendInput return/error.");
        bool received = Wait([&] { return clickDown == 1 && clickUp == 1 && GetForegroundWindow() == window; }, 2500);
        report.Number("OwnTaggedMouseDown", clickDown); report.Number("OwnTaggedMouseUp", clickUp);
        DWORD foregroundPid = 0; HWND foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, &foregroundPid);
        report.Number("ForegroundAfterClick", reinterpret_cast<uintptr_t>(foreground));
        report.Number("ForegroundPidAfterClick", foregroundPid);
        report.Check("MouseSequenceInserted", sent == 2);
        report.Check("OwnWindowReceivedNormalClick", received && clickDown == 1 && clickUp == 1);
        report.Check("OwnWindowObtainedForeground", foreground == window && foregroundPid == GetCurrentProcessId());
        report.Check("OwnWindowTopmostRemoved", SetWindowPos(window, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) && !(GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOPMOST));
        temporaryTopmost = false;
        report.Text("ForegroundAcquisition", "Verified current cursor in own client; one SendInput down/up pair without moving the cursor; ordinary WM_MOUSEACTIVATE; no foreground-policy bypass.");
    }
    std::vector<HWND> ToolbarWindows() {
        std::vector<HWND> found;
        EnumWindows([](HWND hwnd, LPARAM data) -> BOOL {
            if (ToolbarCandidate(hwnd)) reinterpret_cast<std::vector<HWND>*>(data)->push_back(hwnd);
            return TRUE;
        }, reinterpret_cast<LPARAM>(&found));
        return found;
    }
    OwnerWindow ReadOwner(HWND hwnd, DWORD pid = 0) {
#ifdef _WIN64
        return QueryOwnerWindow(hwnd, pid, expectedOwnerExe.c_str());
#else
        OwnerWindow value;
        // Same-source x64 observer queries only this candidate PID/class HMODULE. It does not activate COM/UI.
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE}; HANDLE read{}, write{};
        if (!CreatePipe(&read, &write, &security, 4096)) { value.error = GetLastError(); return value; }
        if (!SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0)) {
            value.error = GetLastError(); CloseHandle(read); CloseHandle(write); return value;
        }
        HANDLE input = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, 0, nullptr);
        STARTUPINFOW startup{sizeof(startup)}; startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdInput = input; startup.hStdOutput = startup.hStdError = write;
        PROCESS_INFORMATION process{};
        std::wstring command = QuoteArgument(ownerQueryExe) + L" --query-owner " +
            std::to_wstring(reinterpret_cast<uintptr_t>(hwnd)) + L" " + std::to_wstring(pid) + L" " + QuoteArgument(expectedOwnerExe);
        const bool started = CreateProcessW(ownerQueryExe.c_str(), command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
            nullptr, std::filesystem::path(reportPath).parent_path().c_str(), &startup, &process) != FALSE;
        const DWORD launchError = started ? 0 : GetLastError();
        CloseHandle(write); if (input != INVALID_HANDLE_VALUE) CloseHandle(input);
        std::vector<BYTE> reply; reply.reserve(4096); DWORD exit = STILL_ACTIVE;
        bool complete = started && Wait([&] {
            DWORD available = 0;
            while (PeekNamedPipe(read, nullptr, 0, nullptr, &available, nullptr) && available) {
                if (reply.size() + available > sizeof(value)) return true;
                const size_t before = reply.size(); reply.resize(before + available); DWORD count = 0;
                if (!ReadFile(read, reply.data() + before, available, &count, nullptr) || !count) return true;
                reply.resize(before + count);
            }
            GetExitCodeProcess(process.hProcess, &exit);
            return exit != STILL_ACTIVE;
        }, 1500, closed);
        if (started) {
            if (exit == STILL_ACTIVE) { TerminateProcess(process.hProcess, 2); WaitForSingleObject(process.hProcess, 500); }
            CloseHandle(process.hThread); CloseHandle(process.hProcess);
        }
        CloseHandle(read);
        constexpr size_t header = offsetof(OwnerWindow, exe);
        bool decoded = complete && !exit && reply.size() >= header + 2 * sizeof(wchar_t) && reply.size() <= sizeof(value);
        size_t offset = header;
        if (decoded) {
            std::memcpy(&value, reply.data(), header);
            for (wchar_t* path : {value.exe, value.path}) {
                size_t index = 0; bool terminated = false;
                while (offset + sizeof(wchar_t) <= reply.size() && index < 32768) {
                    std::memcpy(path + index, reply.data() + offset, sizeof(wchar_t)); offset += sizeof(wchar_t);
                    if (!path[index++]) { terminated = true; break; }
                }
                if (!terminated) { decoded = false; break; }
            }
        }
        if (!decoded || offset != reply.size()) {
            value = OwnerWindow{}; value.error = started ? ERROR_TIMEOUT : launchError;
        }
        return value;
#endif
    }
    bool OwnerModuleMatches(const wchar_t* actualModule) {
        if (!_wcsicmp(actualModule, expectedOwnerModule.c_str())) return true;
        wchar_t standaloneExe[32768]{}; DWORD sBytes = sizeof(standaloneExe); HKEY sKey{};
        if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\TrueFluentPro\\VoiceImeUI", 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &sKey) == ERROR_SUCCESS) {
            if (RegGetValueW(sKey, nullptr, L"StandaloneHostExe", RRF_RT_REG_SZ, nullptr, standaloneExe, &sBytes) == ERROR_SUCCESS && standaloneExe[0]) {
                const auto standaloneMod = (std::filesystem::path(standaloneExe).parent_path() / L"voice-ime" / L"win-x64" / L"TrueFluentVoiceIme.dll").wstring();
                if (!_wcsicmp(actualModule, standaloneMod.c_str())) { RegCloseKey(sKey); return true; }
            }
            RegCloseKey(sKey);
        }
        return false;
    }
    bool OwnerIdentity(HWND hwnd) {
        if (!ownerPid || !ToolbarCandidate(hwnd)) return false;
        const auto value = ReadOwner(hwnd, ownerPid);
        return !value.error && value.pid == ownerPid && value.thread == ownerThread && value.created == ownerCreated &&
            value.module && OwnerModuleMatches(value.path);
    }
    void VerifyOwner(const char* stage, bool visible, bool idle = true) {
        const auto windows = ToolbarWindows(); unsigned mains = 0, attached = 0;
        for (HWND hwnd : windows) {
            if (!GetWindow(hwnd, GW_OWNER) && WindowText(hwnd) == DisplayName) {
                const auto value = ReadOwner(hwnd);
                if (!value.error && OwnerModuleMatches(value.path)) ++mains;
            }
            if (hwnd != ownerMain && GetWindow(hwnd, GW_OWNER) == ownerMain) ++attached;
        }
        report.Number((std::string(stage) + "GlobalValidMainCount").c_str(), mains);
        report.Number((std::string(stage) + "AttachedHintCount").c_str(), attached);
        report.Number((std::string(stage) + "MainHwnd").c_str(), reinterpret_cast<uintptr_t>(ownerMain));
        report.Check((std::string(stage) + "OwnerStable").c_str(), mains == 1 && attached == 2 &&
            OwnerIdentity(ownerMain) && OwnerIdentity(ownerTip) && OwnerIdentity(ownerNotice) &&
            GetWindow(ownerTip, GW_OWNER) == ownerMain && GetWindow(ownerNotice, GW_OWNER) == ownerMain &&
            WindowText(ownerMain) == DisplayName);
        report.Check((std::string(stage) + "Visibility").c_str(), (IsWindowVisible(ownerMain) != FALSE) == visible &&
            !IsWindowVisible(ownerNotice) && (!idle || !IsWindowVisible(ownerTip)));
        report.Check((std::string(stage) + "Idle").c_str(), WindowText(ownerTip) == L"按下 F8 开始录音" && serviceNonIdleFrames == 0);
    }
    void HoverMessage(UINT message, WPARAM w = 0, LPARAM l = 0) {
        FocusGate("EditorFocusedBeforeOwnerHoverMessage");
        report.Check("HoverTargetIsVerifiedSharedOwner", OwnerIdentity(ownerMain));
        GUITHREADINFO gui{sizeof(gui)};
        report.Check("OwnerNotDraggingOrCapturing", GetGUIThreadInfo(ownerThread, &gui) && !gui.hwndCapture && InputReleased());
        DWORD_PTR result{};
        report.Check("OwnerHoverMessageCompleted", SendMessageTimeoutW(ownerMain, message, w, l,
            SMTO_ABORTIFHUNG | SMTO_BLOCK, 750, &result) != 0);
    }
    void VerifyServiceUi() {
        const bool updated = Wait([&] { return serviceTimerUpdates > 0; }, 2500);
        report.Number("Service500msTimerUpdates", serviceTimerUpdates);
        report.Number("FirstServiceTimerElapsedMs", firstServiceTimerElapsed);
        report.Number("ServiceDispatcherWindow", reinterpret_cast<uintptr_t>(serviceDispatcher));
        report.Check("Production500msTimerDispatched", updated && firstServiceTimerElapsed >= 500);
        FocusGate("EditorFocusedAfterProductionTimer");
        // Production's worker has a 10s idle-owner connection deadline. Do not create a substitute host/pipe.
        const bool appeared = Wait([&] {
            for (HWND hwnd : ToolbarWindows()) {
                if (GetWindow(hwnd, GW_OWNER) || WindowText(hwnd) != DisplayName) continue;
                const auto value = ReadOwner(hwnd);
                if (!value.error && OwnerModuleMatches(value.path) && IsWindowVisible(hwnd)) {
                    ownerMain = hwnd; ownerPid = value.pid; ownerThread = value.thread; ownerCreated = value.created; return serviceIdleFrames > 0;
                }
            }
            return false;
        }, 10500);
        report.Check("SharedOwnerShownByNativeSelection", appeared && serviceIdleFrames > 0 && serviceNonIdleFrames == 0);
        for (HWND hwnd : ToolbarWindows()) if (hwnd != ownerMain && GetWindow(hwnd, GW_OWNER) == ownerMain) {
            if (WindowText(hwnd) == L"按下 F8 开始录音") ownerTip = hwnd;
            else if (WindowText(hwnd).empty()) ownerNotice = hwnd;
        }
        report.Check("SharedOwnerMainAndTwoHintsExist", ownerMain && ownerTip && ownerNotice && ownerTip != ownerNotice);
        const auto actual = ReadOwner(ownerMain, ownerPid);
        report.Number("OwnerPid", ownerPid); report.Number("OwnerThread", ownerThread); report.Number("OwnerProcessCreated", ownerCreated);
        report.Number("OwnerClassHmodule", actual.module); report.Number("OwnerQueryError", actual.error);
        report.Text("ActualOwnerExe", Utf8(actual.exe)); report.Text("ActualOwnerModule", Utf8(actual.path));
        report.Text("ToolbarClassModule", Utf8(actual.path));
        report.Number("OwnerMainHwnd", reinterpret_cast<uintptr_t>(ownerMain));
        report.Number("OwnerTipHwnd", reinterpret_cast<uintptr_t>(ownerTip)); report.Number("OwnerNoticeHwnd", reinterpret_cast<uintptr_t>(ownerNotice));
        report.Check("SharedOwnerIndependentProcessAndUiThread", ownerPid != GetCurrentProcessId() && ownerThread != GetCurrentThreadId());
        VerifyOwner("Initial", true);
        bool styles = true;
        constexpr LONG_PTR required = WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        for (HWND hwnd : {ownerMain, ownerTip, ownerNotice}) styles = styles && (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & required) == required;
        report.Check("ToolbarNoActivateLayeredToolWindow", styles);
        report.Bool("OwnerUsesPMV2", Pmv2(ownerMain)); report.Number("EditorDpi", Dpi(window));
        report.Text("OwnerDpiAuthority", "Owner EXE manifest / actual GetDpiForWindow; no test-thread PMv2 equality assumption and no DPI change sent to owner.");
        RECT rect{}, client{}; const UINT dpi = Dpi(ownerMain);
        report.Check("ToolbarRectAvailable", dpi && GetWindowRect(ownerMain, &rect) && GetClientRect(ownerMain, &client));
        report.Number("ToolbarDpi", dpi); report.Rect("ToolbarMainRect", rect);
        report.Check("ToolbarSurface104x44Dip", rect.right - rect.left == MulDiv(144, dpi, 96) &&
            rect.bottom - rect.top == MulDiv(44, dpi, 96) && client.right == MulDiv(144, dpi, 96) && client.bottom == MulDiv(44, dpi, 96));
        FocusGate("EditorFocusedAtIdleUi");
        if (switchOnly) return; // Observe real Idle only; synthetic hover can trigger cross-process WM_MOUSELEAVE.
        hoverSent = true; // Unwind hover even after a timeout or failed assertion; no remote Close/Destroy.
        HoverMessage(WM_MOUSEMOVE, 0, MAKELPARAM(MulDiv(74, dpi, 96), MulDiv(22, dpi, 96)));
        HoverMessage(WM_TIMER, 2); // Actual production hover handler, never mic click/animation timer/T/KeyDown.
        report.Text("HoverTooltipText", Utf8(WindowText(ownerTip)));
        report.Check("HoverTooltipVisibleWithStartText", IsWindowVisible(ownerTip) && WindowText(ownerTip) == L"按下 F8 开始录音");
        VerifyOwner("Hover", true, false);
        FocusGate("EditorFocusedDuringHover");
        CaptureOwnMain();
        FocusGate("EditorFocusedAfterScreenshot");
        HoverMessage(WM_MOUSELEAVE); hoverSent = false;
        VerifyOwner("HoverCleared", true);
    }
    void CaptureOwnMain(bool noDocument = false) {
        // Separate exact HWND rectangles; never a union's gap, full desktop, PrintWindow fallback or external text.
        const auto field = [&](const char* name) { return std::string(noDocument ? "NoDocument" : "") + name; };
        std::vector<HWND> targets{ownerMain};
        if (!noDocument && IsWindowVisible(ownerTip)) targets.push_back(ownerTip);
        if (!noDocument && IsWindowVisible(ownerNotice)) targets.push_back(ownerNotice);
        std::vector<RECT> rects;
        for (HWND hwnd : targets) {
            RECT rect{};
            report.Check(field("CaptureRectsAvailable").c_str(), OwnerIdentity(hwnd) && GetWindowRect(hwnd, &rect) &&
                rect.right > rect.left && rect.bottom > rect.top && rect.right - rect.left <= 2048 && rect.bottom - rect.top <= 2048);
            rects.push_back(rect);
        }
        const auto allowedRoot = [&](HWND root) { return root == ownerMain || root == ownerTip || root == ownerNotice || (root == window && Own(window)); };
        const auto exposed = [&](size_t index) {
            const HWND target = targets[index]; const RECT rect = rects[index]; RECT now{};
            if (!OwnerIdentity(target) || !IsWindowVisible(target) || !GetWindowRect(target, &now) || !EqualRect(&rect, &now) ||
                GetForegroundWindow() != window || GetFocus() != window) return false;
            HWND above = GetWindow(target, GW_HWNDPREV);
            for (unsigned n = 0; above && n < 1024; ++n, above = GetWindow(above, GW_HWNDPREV)) {
                if (allowedRoot(above) || !IsWindowVisible(above) || IsIconic(above)) continue;
                RECT other{}, overlap{};
                if (!GetWindowRect(above, &other) || IntersectRect(&overlap, &rect, &other)) return false;
            }
            if (above) return false;
            for (LONG y = rect.top; y < rect.bottom; ++y) for (LONG x = rect.left; x < rect.right; ++x) {
                const HWND hit = WindowFromPoint({x, y});
                if (!hit || !allowedRoot(GetAncestor(hit, GA_ROOT))) return false;
            }
            return true;
        };
        bool safe = true;
        for (size_t i = 0; i < targets.size(); ++i) safe = safe && exposed(i);
        report.Bool(field("ScreenshotAreaFullyExposed").c_str(), safe);
        report.Text(field("CaptureStatus").c_str(), "Skipped");
        if (!safe) {
            report.Text(field("CaptureSkipReason").c_str(), "遮挡或透明边缘露出外部窗口；未截图、未移动浮窗或用户窗口。可见性/模块/HWND稳定独立验收，目视未通过。");
            std::printf("SKIP %s screenshot: unsafe exposure\n", noDocument ? "NoDocument" : "Owner"); return;
        }
        ULONG_PTR token{}; Gdiplus::GdiplusStartupInput startup;
        report.Check(field("ScreenshotGraphicsStarted").c_str(), Gdiplus::GdiplusStartup(&token, &startup, nullptr) == Gdiplus::Ok);
        bool captured = true, saved = true; unsigned bodyPixels = 0, brandPixels = 0, micPixels = 0;
        RECT body{LONG_MAX, LONG_MAX, 0, 0};
        for (size_t i = 0; i < targets.size(); ++i) {
            const RECT rect = rects[i]; const int width = rect.right - rect.left, height = rect.bottom - rect.top;
            report.Rect(field(i == 0 ? "MainScreenshotRect" : i == 1 ? "TipScreenshotRect" : "NoticeScreenshotRect").c_str(), rect);
            DwmFlush(); HDC screen = GetDC(nullptr), memory = screen ? CreateCompatibleDC(screen) : nullptr;
            HBITMAP bitmap = memory ? CreateCompatibleBitmap(screen, width, height) : nullptr;
            if (!bitmap) captured = false;
            else {
                HGDIOBJ old = SelectObject(memory, bitmap);
                const bool copied = exposed(i) && BitBlt(memory, 0, 0, width, height, screen, rect.left, rect.top, SRCCOPY | CAPTUREBLT) && exposed(i);
                captured = captured && copied;
                if (copied) {
                    Gdiplus::Bitmap image(bitmap, nullptr);
                    if (i == 0) for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
                        Gdiplus::Color c; if (image.GetPixel(x, y, &c) != Gdiplus::Ok) continue;
                        if (c.GetR() >= 180 && c.GetG() >= 200 && c.GetB() >= 225 && c.GetB() - c.GetR() >= 8) {
                            body.left = std::min(body.left, LONG(x)); body.top = std::min(body.top, LONG(y));
                            body.right = std::max(body.right, LONG(x + 1)); body.bottom = std::max(body.bottom, LONG(y + 1)); ++bodyPixels;
                        }
                        if (c.GetR() < 90 && c.GetG() < 180 && c.GetB() > 220) ++brandPixels;
                        if (std::abs(int(c.GetR()) - 83) <= 2 && std::abs(int(c.GetG()) - 105) <= 2 && std::abs(int(c.GetB()) - 145) <= 2) ++micPixels;
                    }
                    if (!noDocument) {
                        const auto path = (std::filesystem::path(reportPath).parent_path() /
                            (i == 0 ? L"OWNMAIN.png" : i == 1 ? L"OWNTIP.png" : L"OWNNOTICE.png")).wstring();
                        UINT count = 0, bytes = 0; Gdiplus::GetImageEncodersSize(&count, &bytes); std::vector<BYTE> storage(bytes); bool wrote = false;
                        auto encoders = reinterpret_cast<Gdiplus::ImageCodecInfo*>(storage.data());
                        if (bytes && Gdiplus::GetImageEncoders(count, bytes, encoders) == Gdiplus::Ok)
                            for (UINT j = 0; j < count; ++j) if (!wcscmp(encoders[j].MimeType, L"image/png")) wrote = image.Save(path.c_str(), &encoders[j].Clsid) == Gdiplus::Ok;
                        saved = saved && wrote;
                        if (wrote) report.Text(i == 0 ? "OwnMainScreenshot" : i == 1 ? "OwnTipScreenshot" : "OwnNoticeScreenshot", Utf8(path));
                    }
                }
                SelectObject(memory, old); DeleteObject(bitmap);
            }
            if (memory) DeleteDC(memory); if (screen) ReleaseDC(nullptr, screen);
        }
        Gdiplus::GdiplusShutdown(token);
        if (!captured) {
            report.Text(field("CaptureSkipReason").c_str(), "曝光在裁剪期间变化或读取失败；不以截图验收。已保存的安全裁剪不代表整组完成。"); return;
        }
        report.Check(field("ScreenshotsSaved").c_str(), saved);
        report.Text(field("CaptureStatus").c_str(), "Captured"); report.Rect(field("ObservedPanelPixelBounds").c_str(), body);
        const UINT dpi = Dpi(ownerMain);
        report.Check(field("ObservedBody96x36Dip").c_str(), bodyPixels && std::abs(body.right - body.left - MulDiv(136, dpi, 96)) <= 2 &&
            std::abs(body.bottom - body.top - MulDiv(36, dpi, 96)) <= 2);
        if (noDocument) report.Check("NoDocumentObservedIdleBrandMic", brandPixels && micPixels);
        report.Text("VisualAssessment", "仅实际窗口矩形安全裁剪；品牌、mic、中文字体待主agent目视，不用像素断言冒充目视通过。");
    }
    bool RestoreNoDocumentFocus() noexcept {
        if (!noDocumentFocusPending) return true;
        ComPtr<ITfDocumentMgr> unused, restored;
        // Attempt both restorations before reporting either failure; never throw during unwind.
        const HRESULT associationHr = manager->AssociateFocus(window, noDocumentPrevious.Get(), &unused);
        const HRESULT focusHr = manager->SetFocus(doc.Get());
        const HRESULT queryHr = manager->GetFocus(&restored);
        report.Hr("NoDocumentRestoreWindowAssociationHr", associationHr);
        report.Hr("NoDocumentRestoreDocumentFocusHr", focusHr);
        report.Hr("NoDocumentRestoredGetFocusHr", queryHr);
        report.Number("NoDocumentRestoredDocument", reinterpret_cast<uintptr_t>(restored.Get()));
        const bool associationRestored = associationHr == S_OK;
        const bool focusRestored = focusHr == S_OK && queryHr == S_OK && Same(restored.Get(), doc.Get());
        noDocumentFocusPending = !associationRestored || !focusRestored;
        report.Check("NoDocumentWindowAssociationRestored", associationRestored, false);
        report.Check("NoDocumentDocumentFocusRestored", focusRestored, false);
        return !noDocumentFocusPending;
    }
    void VerifyNoDocument() {
        // Compatibility prefix: NoDocument means no textContext, not a NULL document manager.
        report.Text("NoDocumentSemantics", "NoDocument compatibility fields mean no textContext: focused empty document manager is non-NULL, GetTop context is NULL; no Context is created or pushed on emptyDoc.");
        FocusGate("EditorFocusedBeforeNoDocument");
        BOOL originalThreadFocus = FALSE;
        report.Require("NoDocumentBaselineIsThreadFocusHr", manager->IsThreadFocus(&originalThreadFocus));
        report.Check("NoDocumentBaselineThreadFocus", originalThreadFocus != FALSE);
        VerifyOwner("NoDocumentBaseline", true);
        const auto gate = [&](const char* stage) {
            const auto field = [&](const char* name) { return std::string(stage) + name; };
            ComPtr<ITfDocumentMgr> current;
            HRESULT hr = manager->GetFocus(&current); report.Hr(field("GetFocusHr").c_str(), hr);
            report.Number(field("Document").c_str(), reinterpret_cast<uintptr_t>(current.Get()));
            report.Check(field("EmptyDocumentFocused").c_str(), hr == S_OK && current && Same(current.Get(), emptyDoc.Get()));
            ComPtr<ITfContext> top;
            hr = current->GetTop(&top); report.Hr(field("GetTopHr").c_str(), hr);
            report.Number(field("Context").c_str(), reinterpret_cast<uintptr_t>(top.Get()));
            report.Check(field("ContextNull").c_str(), (hr == S_OK || hr == S_FALSE) && !top);
            BOOL focused = FALSE; hr = manager->IsThreadFocus(&focused); report.Hr(field("IsThreadFocusHr").c_str(), hr);
            report.Bool(field("ThreadFocus").c_str(), focused != FALSE);
            report.Check(field("ThreadFocusUnchanged").c_str(), hr == S_OK && focused != FALSE &&
                (focused != FALSE) == (originalThreadFocus != FALSE));
            HWND front = GetForegroundWindow(); DWORD pid = 0; GetWindowThreadProcessId(front, &pid);
            report.Number(field("ForegroundWindow").c_str(), reinterpret_cast<uintptr_t>(front)); report.Number(field("ForegroundPid").c_str(), pid);
            report.Number(field("Win32FocusWindow").c_str(), reinterpret_cast<uintptr_t>(GetFocus()));
            report.Check(field("OwnForegroundAndFocus").c_str(), Own(window) && front == window && pid == GetCurrentProcessId() && GetFocus() == window);
            TF_INPUTPROCESSORPROFILE selected{}; hr = profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &selected);
            report.Hr(field("ActiveProfileHr").c_str(), hr); report.Profile(field("ActiveProfile").c_str(), selected);
            report.Check(field("SelectedVoice").c_str(), hr == S_OK && Voice(selected));
        };
        report.Require("NoDocumentCreateEmptyDocumentHr", manager->CreateDocumentMgr(&emptyDoc));
        report.Number("NoDocumentEmptyDocument", reinterpret_cast<uintptr_t>(emptyDoc.Get()));
        report.Check("NoDocumentEmptyDocumentCreated", emptyDoc && !Same(emptyDoc.Get(), doc.Get()));
        struct RestoreFocus {
            Host& host;
            ~RestoreFocus() { host.RestoreNoDocumentFocus(); }
        } restore{*this}; // Finally-equivalent on every gate/exception, before Host::Cleanup.
        const unsigned timersBefore = serviceTimerUpdates, framesBefore = serviceFrameMessages, nonIdleBefore = serviceNonIdleFrames, writesBefore = store->writes;
        const HWND dispatcherBefore = serviceDispatcher;
        // AssociateFocus does not AddRef emptyDoc: keep it alive through restoration/window teardown.
        // Save the actual prior association; doc is the unwind fallback if association itself fails.
        noDocumentPrevious = doc; noDocumentFocusPending = true;
        ComPtr<ITfDocumentMgr> previousAssociation;
        const HRESULT associationHr = manager->AssociateFocus(window, emptyDoc.Get(), &previousAssociation);
        if (associationHr == S_OK) noDocumentPrevious = previousAssociation;
        report.Number("NoDocumentPreviousWindowDocument", reinterpret_cast<uintptr_t>(previousAssociation.Get()));
        report.Require("NoDocumentAssociateEmptyDocumentHr", associationHr);
        report.Check("NoDocumentPreviousWindowAssociationIsOriginalDocument", Same(previousAssociation.Get(), doc.Get()));
        const ULONGLONG emptyFocusedAt = GetTickCount64();
        report.Require("NoDocumentSetFocusHr", manager->SetFocus(emptyDoc.Get()));
        gate("NoDocumentBeforeTimer");
        const bool updated = Wait([&] {
            return GetForegroundWindow() != window || GetFocus() != window || lastServiceTimerAt >= emptyFocusedAt + 500;
        }, 2500);
        gate("NoDocumentAfterTimer");
        report.Number("NoDocumentWaitElapsedMs", GetTickCount64() - emptyFocusedAt);
        report.Number("NoDocumentService500msTimerUpdates", serviceTimerUpdates - timersBefore);
        report.Number("NoDocumentServiceTimerElapsedMs", lastServiceTimerAt >= emptyFocusedAt ? lastServiceTimerAt - emptyFocusedAt : 0);
        report.Check("NoDocumentProduction500msTimerDispatched", updated && serviceTimerUpdates > timersBefore &&
            lastServiceTimerAt >= emptyFocusedAt + 500 && serviceDispatcher == dispatcherBefore);
        VerifyOwner("NoDocument", true);
        const auto actual = ReadOwner(ownerMain, ownerPid);
        report.Text("NoDocumentToolbarClassModule", Utf8(actual.path));
        report.Check("NoDocumentClientModuleIsRegisteredDll", _wcsicmp(ModulePath(GetModuleHandleW(L"TrueFluentVoiceIme.dll")).c_str(), registeredDll.c_str()) == 0);
        RECT rect{}, client{}; const UINT dpi = Dpi(ownerMain);
        report.Check("NoDocumentToolbarSurface104x44Dip", dpi && GetWindowRect(ownerMain, &rect) && GetClientRect(ownerMain, &client) &&
            rect.right - rect.left == MulDiv(144, dpi, 96) && rect.bottom - rect.top == MulDiv(44, dpi, 96) &&
            client.right == MulDiv(144, dpi, 96) && client.bottom == MulDiv(44, dpi, 96));
        CaptureOwnMain(true); // Optional exposed idle pixels; no hover/input/additional saved image.
        BOOL eaten = FALSE;
        const LPARAM flags = 1 | (static_cast<LPARAM>(MapVirtualKeyW(VK_F8, MAPVK_VK_TO_VSC)) << 16);
        const HRESULT hr = keys->TestKeyDown(VK_F8, flags, &eaten);
        report.Hr("F8_SelectedNoDocumentNotEatenHr", hr); report.Bool("F8_SelectedNoDocumentNotEatenEaten", eaten != FALSE);
        report.Check("F8_SelectedNoDocumentNotEaten", (hr == S_OK || hr == S_FALSE) && eaten == FALSE);
        gate("NoDocumentAfterF8");
        report.Check("NoDocumentF8KeyDownNotDispatched", report.values.at("F8KeyDownDispatched") == "false");
        report.Number("NoDocumentServiceFrameMessages", serviceFrameMessages - framesBefore);
        report.Number("NoDocumentNonIdleFrames", serviceNonIdleFrames - nonIdleBefore);
        report.Number("NoDocumentTextStoreWrites", store->writes - writesBefore);
        VerifyOwner("NoDocumentAfterKey", true);
        report.Check("NoDocumentNoAudioRequested", serviceNonIdleFrames == nonIdleBefore && store->writes == writesBefore &&
            report.values.at("F8KeyDownDispatched") == "false");
        report.Text("NoDocumentAudioEvidence", "TestKeyDown only; no T/KeyDown/mic click/frame injection; actual owner remains Idle with hidden status and no own writes. Idle I/previously-stopped S connection frames are normal, not audio; private audio hardware not inspected.");
        if (!RestoreNoDocumentFocus()) throw Failure{};
        FocusGate("EditorFocusedAfterNoDocumentRestore");
        TestKey("F8_AfterNoDocumentRestore", VK_F8, true);
        VerifyOwner("NoDocumentRestored", true);
    }
    void VerifyOtherImeHide() {
        FocusGate("EditorFocusedBeforeOtherIme");
        VerifyOwner("BeforeOtherIme", true);
        // Fresh enabled, same-language profiles only. Sogou wins over original Pinyin; never enable a TIP.
        ComPtr<IEnumTfInputProcessorProfiles> enumeration;
        report.Require("OtherImeEnumProfilesHr", profiles->EnumProfiles(0x0804, &enumeration));
        ComPtr<ITfInputProcessorProfiles> descriptions;
        report.Require("OtherImeDescriptionInterfaceHr", profiles.As(&descriptions));
        TF_INPUTPROCESSORPROFILE alternative{}, candidate{}; bool found = false, foundSogou = false, exhausted = false;
        std::wstring alternativeName; HRESULT alternativeDescriptionHr = E_NOTIMPL; unsigned descriptionFailures = 0;
        for (unsigned i = 0; i < 1024; ++i) {
            ULONG count = 0; const HRESULT hr = enumeration->Next(1, &candidate, &count);
            if (hr == S_FALSE && count == 0) { exhausted = true; break; }
            report.Check("OtherImeEnumerationValid", hr == S_OK && count == 1);
            const bool safe = candidate.langid == 0x0804 && (candidate.dwFlags & TF_IPP_FLAG_ENABLED) && !Voice(candidate) &&
                ((candidate.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && candidate.catid == GUID_TFCAT_TIP_KEYBOARD &&
                    candidate.clsid != CLSID_NULL && candidate.guidProfile != GUID_NULL) ||
                    (candidate.dwProfileType == TF_PROFILETYPE_KEYBOARDLAYOUT && candidate.hkl));
            if (!safe) continue;
            std::wstring name = L"已启用键盘布局（无 TIP 描述）"; HRESULT descriptionHr = E_NOTIMPL;
            if (candidate.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR) {
                BSTR raw = nullptr;
                descriptionHr = descriptions->GetLanguageProfileDescription(candidate.clsid, candidate.langid, candidate.guidProfile, &raw);
                const std::unique_ptr<wchar_t, decltype(&SysFreeString)> description(raw, &SysFreeString);
                if (descriptionHr == S_OK && description && SysStringLen(description.get())) name.assign(description.get(), SysStringLen(description.get()));
                else { name = L"已启用其他输入法（描述读取失败）"; ++descriptionFailures; }
            }
            std::wstring lower = name;
            std::transform(lower.begin(), lower.end(), lower.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
            const bool sogou = lower.find(L"搜狗") != lower.npos || lower.find(L"sogou") != lower.npos;
            if (!found || (sogou && !foundSogou) || (!foundSogou && SameProfile(candidate, original))) {
                alternative = candidate; alternativeName = name; alternativeDescriptionHr = descriptionHr;
                found = true; foundSogou = sogou;
            }
        }
        report.Check("OtherImeEnabledEnumerationComplete", exhausted);
        report.Check("OtherImeEnabledNonVoiceAvailable", found);
        report.Text("OtherImeName", Utf8(alternativeName)); report.Bool("OtherImeIsSogou", foundSogou);
        report.Hr("OtherImeDescriptionHr", alternativeDescriptionHr); report.Number("OtherImeDescriptionFailures", descriptionFailures);
        report.Text("OtherImeNameAuthority", alternative.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR ?
            "ITfInputProcessorProfiles::GetLanguageProfileDescription; failed/empty description is explicitly unnamed" : "Keyboard layout fallback; no TIP description");
        report.Text("OtherImeCoverage", foundSogou ? "选用已启用搜狗 Profile；实际切换/隐藏/恢复结果见断言。" :
            "未在已启用 Profile 的描述中找到搜狗/Sogou；仅验证报告所列替代 IME，不能声称搜狗已测。");
        report.Profile("OtherImeProfile", alternative); report.Number("OtherImeActivateFlags", TF_IPPMF_FORPROCESS);
        report.Require("OtherImeActivateHr", profiles->ActivateProfile(alternative.dwProfileType, alternative.langid, alternative.clsid,
            alternative.guidProfile, alternative.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR ? nullptr : alternative.hkl, TF_IPPMF_FORPROCESS));
        TF_INPUTPROCESSORPROFILE selected{};
        report.Require("OtherImeActiveProfileHr", profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &selected));
        report.Profile("OtherImeActiveProfile", selected);
        report.Check("OtherImeActuallySelected", SameProfile(selected, alternative) && !Voice(selected));
        CLSID foregroundClsid{}; HRESULT foregroundHr = E_PENDING;
        const bool released = Wait([&] {
            foregroundClsid = CLSID_NULL; foregroundHr = keys->GetForeground(&foregroundClsid);
            return (foregroundHr == S_OK || foregroundHr == S_FALSE) && foregroundClsid != VoiceClsid;
        }, 1500);
        report.Hr("OtherImeForegroundHr", foregroundHr); report.Text("OtherImeForegroundClsid", Utf8(GuidString(foregroundClsid)));
        report.Check("OtherImeForegroundNotVoice", released && (foregroundHr == S_OK || foregroundHr == S_FALSE) && foregroundClsid != VoiceClsid);
        report.Text("OtherImeKeyAuthority", "GetForeground CLSID is not Voice (S_FALSE means no foreground service); no global F8 eaten assertion because the other IME may own F8.");
        const bool hidden = Wait([&] { return !IsWindowVisible(ownerMain); }, 3500);
        FocusGate("EditorFocusedDuringOtherIme");
        report.Check("OtherImeOwnerActuallyHidden", hidden);
        VerifyOwner("OtherIme", false);
        report.Text("OtherImeHideAuthority", "Actual independent owner HWND hidden after Windows selected a fresh enabled other IME FORPROCESS; no direct hide/D/bridge update injected. Owner receipt alone does not instrument which activation sink delivered it.");
        report.Number("VoiceReselectFlags", TF_IPPMF_FORPROCESS);
        report.Require("VoiceReselectHr", profiles->ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, 0x0804, VoiceClsid, VoiceProfile, nullptr, TF_IPPMF_FORPROCESS));
        const bool visible = Wait([&] { return IsWindowVisible(ownerMain) && WindowText(ownerTip) == L"按下 F8 开始录音"; }, 3500);
        report.Check("VoiceReselectIdleSameHwnd", visible);
        report.Require("VoiceReselectedProfileHr", profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &selected));
        report.Check("VoiceReselectedActuallySelected", Voice(selected));
        VerifyOwner("VoiceReselected", true);
        TestKey("F8_AfterOtherImeRestore", VK_F8, true);
    }
    void FocusGate(const char* stage) {
        DWORD pid = 0; HWND fg = GetForegroundWindow(); GetWindowThreadProcessId(fg, &pid);
        report.Number("ForegroundWindow", reinterpret_cast<uintptr_t>(fg)); report.Number("ForegroundPid", pid);
        BOOL focused = FALSE;
        report.Require("IsThreadFocusHr", manager->IsThreadFocus(&focused));
        ComPtr<ITfDocumentMgr> current; ComPtr<ITfContext> top;
        report.Require("GetFocusHr", manager->GetFocus(&current));
        if (current) report.Require("GetTopHr", current->GetTop(&top));
        bool same = Same(current.Get(), doc.Get()) && Same(top.Get(), context.Get());
        report.Bool("FocusContextMatches", same);
        report.Check(stage, fg == window && pid == GetCurrentProcessId() && GetFocus() == window && focused && same);
    }
    void TestKey(const char* stage, WPARAM key, bool expected) {
        FocusGate("FocusAtKeyTest");
        BOOL eaten = FALSE;
        LPARAM flags = 1 | (static_cast<LPARAM>(MapVirtualKeyW(static_cast<UINT>(key), MAPVK_VK_TO_VSC)) << 16);
        HRESULT hr = keys->TestKeyDown(key, flags, &eaten);
        report.Hr((std::string(stage) + "Hr").c_str(), hr);
        report.Bool((std::string(stage) + "Eaten").c_str(), eaten != FALSE);
        report.Check(stage, hr == S_OK && (eaten != FALSE) == expected);
    }
public:
    Host(Report& r, const wchar_t* path, const wchar_t* expected, const wchar_t* host, const wchar_t* module, const wchar_t* query, bool onlySwitch)
        : report(r), reportPath(path), expectedDll(expected), expectedOwnerExe(host), expectedOwnerModule(module), ownerQueryExe(query), switchOnly(onlySwitch) {}
    ~Host() { Cleanup(); }
    void Run() {
        originalForeground = GetForegroundWindow();
        originalForegroundThread = GetWindowThreadProcessId(originalForeground, &originalForegroundPid);
        GUITHREADINFO originalGui{sizeof(originalGui)};
        report.Check("OriginalForegroundFocusSaved", originalForeground && originalForegroundThread &&
            GetGUIThreadInfo(originalForegroundThread, &originalGui) && GetForegroundWindow() == originalForeground);
        originalFocus = originalGui.hwndFocus;
        report.Number("OriginalForegroundWindow", reinterpret_cast<uintptr_t>(originalForeground));
        report.Number("OriginalForegroundFocus", reinterpret_cast<uintptr_t>(originalFocus));
        report.Number("OriginalForegroundThread", originalForegroundThread);
        // Before any TIP activation: only the exact registration approved by the script's fixed PF/hash/arch gate.
        const wchar_t* registration = L"CLSID\\{5638E6E5-D18C-4FE8-B778-E59A5170B481}\\InprocServer32";
        wchar_t registered[32768]{}; DWORD bytes = sizeof(registered);
        LSTATUS status = RegGetValueW(HKEY_CLASSES_ROOT, registration, nullptr, RRF_RT_REG_SZ, nullptr, registered, &bytes);
        report.Require("ReadRegisteredDllHr", HRESULT_FROM_WIN32(status)); registeredDll = registered;
        report.Text("RegisteredDll", Utf8(registeredDll)); report.Text("ExpectedRegisteredDll", Utf8(expectedDll));
        report.Check("RegistrationMatchesPreflight", !expectedDll.empty() && _wcsicmp(registeredDll.c_str(), expectedDll.c_str()) == 0);
        wchar_t host[32768]{}; bytes = sizeof(host);
        HKEY hostKey{};
        status = RegOpenKeyExW(HKEY_CURRENT_USER, ConfigKey, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &hostKey);
        if (status == ERROR_SUCCESS) {
            status = RegGetValueW(hostKey, nullptr, L"HostExe", RRF_RT_REG_SZ, nullptr, host, &bytes);
            RegCloseKey(hostKey);
        }
        HKEY uiKey{};
        if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\TrueFluentPro\\VoiceImeUI", 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &uiKey) == ERROR_SUCCESS) {
            wchar_t standalone[32768]{}; DWORD sBytes = sizeof(standalone);
            if (RegGetValueW(uiKey, nullptr, L"StandaloneHostExe", RRF_RT_REG_SZ, nullptr, standalone, &sBytes) == ERROR_SUCCESS && AbsoluteFile(standalone)) {
                wcscpy_s(host, standalone);
                status = ERROR_SUCCESS;
            }
            RegCloseKey(uiKey);
        }
        report.Require("ReadRegisteredOwnerExeHr", HRESULT_FROM_WIN32(status));
        report.Text("RegisteredOwnerExe", Utf8(host)); report.Text("ExpectedOwnerExe", Utf8(expectedOwnerExe));
        report.Text("ExpectedOwnerModule", Utf8(expectedOwnerModule)); report.Text("OwnerQueryExecutable", Utf8(ownerQueryExe));
        report.Check("OwnerRegistrationMatchesPreflight", AbsoluteFile(expectedOwnerExe) && !_wcsicmp(host, expectedOwnerExe.c_str()) &&
            AbsoluteFile(expectedOwnerModule) && !_wcsicmp(expectedOwnerModule.c_str(),
                (std::filesystem::path(expectedOwnerExe).parent_path() / L"voice-ime" / L"win-x64" / L"TrueFluentVoiceIme.dll").c_str()));
        report.Number("OwnerMainCountBeforeActivation", 0);
        unsigned initialOwners = 0;
        for (HWND hwnd : ToolbarWindows()) if (!GetWindow(hwnd, GW_OWNER) && WindowText(hwnd) == DisplayName) {
            const auto value = ReadOwner(hwnd);
            if (!value.error && OwnerModuleMatches(value.path)) ++initialOwners;
        }
        report.Number("OwnerMainCountBeforeActivation", initialOwners);
        report.Bool("OwnerExistedBeforeActivation", initialOwners != 0);
        HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        com = SUCCEEDED(hr); report.Hr("CoInitializeHr", hr); report.Check("ComInitialized", com);
        report.Require("CreateProfilesHr", CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&profiles)));
        report.Require("CreateThreadMgrHr", CoCreateInstance(CLSID_TF_ThreadMgr, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&manager)));
        hr = manager->Activate(&owner); activated = hr == S_OK; report.Require("ThreadActivateHr", hr);
        report.Require("OriginalActiveProfileHr", profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &original));
        report.Profile("OriginalProfile", original);
        report.Check("RestorableOriginalProfile", original.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR || original.dwProfileType == TF_PROFILETYPE_KEYBOARDLAYOUT);
        report.Check("OriginalLanguage0804NoLanguageSwitch", original.langid == 0x0804);
        report.Require("GetKeystrokeMgrHr", manager.As(&keys));

        WNDCLASSW cls{}; cls.hInstance = GetModuleHandleW(nullptr); cls.lpfnWndProc = WindowProc;
        cls.lpszClassName = L"TrueFluent.ActivationAcceptance"; cls.hCursor = LoadCursorW(nullptr, IDC_IBEAM);
        cls.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        windowClass = RegisterClassW(&cls); report.Check("WindowClassCreated", windowClass != 0);
        // Test editor only: keep a current cursor at the top work-area edge inside
        // its client, rather than under an overlapped window's caption.
        window = CreateWindowExW(0, cls.lpszClassName, L"TrueFluent TSF 激活验收（不录音）", WS_POPUP | WS_BORDER,
            CW_USEDEFAULT, CW_USEDEFAULT, 620, 200, nullptr, nullptr, cls.hInstance, this);
        report.Check("WindowCreated", window != nullptr);
        store.Attach(new Store); store->window = window;
        report.Require("CreateDocumentHr", manager->CreateDocumentMgr(&doc));
        TfEditCookie cookie = 0;
        report.Require("CreateContextHr", doc->CreateContext(owner, 0, static_cast<ITextStoreACP*>(store.Get()), &context, &cookie));
        hr = doc->Push(context.Get()); pushed = hr == S_OK; report.Require("PushContextHr", hr);
        hr = manager->AssociateFocus(window, doc.Get(), &previous); associated = hr == S_OK; report.Require("AssociateFocusHr", hr);
        ObtainForegroundByOwnClick();
        SetFocus(window); // Only our already-foreground window, never another thread/window.
        report.Require("SetDocumentFocusHr", manager->SetFocus(doc.Get()));
        Wait([&] { BOOL f = FALSE; return manager->IsThreadFocus(&f) == S_OK && f; }, 500);
        FocusGate("RealFocusBeforeActivation");

        attempted = true; // Restore even if ActivateProfile reports failure after partial activation.
        activationStarted = GetTickCount64();
        report.Number("ActivateFlags", TF_IPPMF_FORPROCESS);
        report.Require("ActivateProfileHr", profiles->ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, 0x0804,
            VoiceClsid, VoiceProfile, nullptr, TF_IPPMF_FORPROCESS));
        CLSID foregroundClsid{};
        bool ready = Wait([&] { return keys->GetForeground(&foregroundClsid) == S_OK && foregroundClsid == VoiceClsid; }, 1500);
        hr = keys->GetForeground(&foregroundClsid); report.Hr("GetForegroundHr", hr);
        report.Text("ForegroundClsid", Utf8(GuidString(foregroundClsid)));
        HMODULE module = GetModuleHandleW(L"TrueFluentVoiceIme.dll");
        wchar_t loaded[32768]{}; DWORD size = module ? GetModuleFileNameW(module, loaded, 32768) : 0;
        report.Text("LoadedModule", Utf8(loaded));
        report.Check("SystemForegroundServiceIsVoice", ready && hr == S_OK && foregroundClsid == VoiceClsid);
        TF_INPUTPROCESSORPROFILE selected{};
        report.Require("SelectedProfileHr", profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &selected));
        report.Profile("SelectedProfile", selected); report.Check("SelectedVoice0804", Voice(selected));
        report.Check("SystemLoadedRegisteredDll", size > 0 && size < 32768 && _wcsicmp(loaded, registered) == 0);
        report.Text("ActivationAuthority", "Windows TSF owns TIP creation/activation; no direct Activate/ActivateEx call or service pointer; ActivateEx entry not separately instrumented");
        FocusGate("RealFocusAfterActivation");
        VerifyServiceUi();
        if (!switchOnly) VerifyNoDocument();

        TF_STATUS state{}; report.Require("EditableStatusHr", context->GetStatus(&state));
        report.Number("EditableDynamicFlags", state.dwDynamicFlags); report.Number("EditableStaticFlags", state.dwStaticFlags);
        report.Check("ContextInitiallyEditable", (state.dwDynamicFlags & TF_SD_READONLY) == 0);
        ComPtr<LockProbe> probe; probe.Attach(new LockProbe(context.Get())); HRESULT session = E_PENDING;
        report.Require("RequestWriteLockHr", context->RequestEditSession(owner, probe.Get(), TF_ES_ASYNCDONTCARE | TF_ES_READWRITE, &session));
        report.Hr("WriteLockSessionHr", session);
        bool done = Wait([&] { return probe->done; }, 1500);
        report.Hr("WriteLockCallbackHr", probe->result);
        report.Check("RealEditLockSelectionRoundTrip", SUCCEEDED(session) && done && probe->result == S_OK);
        TestKey("A_Passthrough", 'A', false);
        TestKey("F8_SelectedEditable", VK_F8, true);

        if (!switchOnly) {
            report.Require("SetReadOnlyHr", store->SetReadOnly(true));
            report.Require("ReadOnlyStatusHr", context->GetStatus(&state));
            report.Number("ReadOnlyDynamicFlags", state.dwDynamicFlags); report.Number("ReadOnlyStaticFlags", state.dwStaticFlags);
            report.Check("TsfContextConfirmsReadOnly", (state.dwDynamicFlags & TF_SD_READONLY) != 0);
            TestKey("F8_SelectedReadOnlyNotEaten", VK_F8, false);
            report.Require("ClearReadOnlyHr", store->SetReadOnly(false));
        }
        VerifyOtherImeHide();
        report.Check("NoTextInjected", store->writes == 0 && store->text == L"ABTAIL");
    }
    void Cleanup() noexcept {
        if (closed) return;
        closed = true;
        // Unwind in the original STA, while its window/context and message queue are still alive.
        if (hoverSent && OwnerIdentity(ownerMain)) {
            DWORD_PTR result{};
            report.Check("CleanupOwnerHoverCleared", SendMessageTimeoutW(ownerMain, WM_MOUSELEAVE, 0, 0,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, 750, &result) != 0, false);
            hoverSent = false;
        }
        if (noDocumentFocusPending) RestoreNoDocumentFocus();
        if (store) {
            report.Number("TextStoreWrites", store->writes); report.Number("TextStoreLockGrants", store->grants);
            report.Check("TextUnchangedAtCleanup", store->writes == 0 && store->text == L"ABTAIL", false);
        }
        if (store && store->readOnly) report.Hr("CleanupClearReadOnlyHr", store->SetReadOnly(false));
        if (attempted && profiles) {
            HRESULT hr = profiles->ActivateProfile(original.dwProfileType, original.langid, original.clsid,
                original.guidProfile, original.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR ? nullptr : original.hkl, TF_IPPMF_FORPROCESS);
            report.Hr("RestoreProfileHr", hr);
            TF_INPUTPROCESSORPROFILE restored{};
            HRESULT query = profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &restored);
            report.Hr("RestoredProfileQueryHr", query); report.Profile("RestoredProfile", restored);
            report.Check("OriginalProcessProfileRestored", hr == S_OK && query == S_OK && SameProfile(original, restored), false);
        } else report.Text("ProfileRestoration", "not needed: target ActivateProfile never attempted");
        if (associated) {
            ComPtr<ITfDocumentMgr> unused;
            HRESULT hr = manager->AssociateFocus(window, previous.Get(), &unused);
            report.Hr("RestoreFocusAssociationHr", hr); report.Check("FocusAssociationRestored", hr == S_OK, false);
        }
        previous.Reset();
        if (pushed) { HRESULT hr = doc->Pop(TF_POPF_ALL); report.Hr("PopContextHr", hr); report.Check("ContextPopped", hr == S_OK, false); }
        context.Reset(); doc.Reset();
        if (activated) {
            HRESULT hr = manager->Deactivate(); report.Hr("ThreadDeactivateHr", hr);
            report.Check("ThreadDeactivated", hr == S_OK, false);
        }
        keys.Reset(); manager.Reset(); profiles.Reset(); store.Reset();
        if (window && temporaryTopmost) {
            report.Check("CleanupOwnTopmostRemoved", Own(window) && SetWindowPos(window, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE), false);
            temporaryTopmost = false;
        }
        if (window) {
            GUITHREADINFO gui{sizeof(gui)};
            const auto savedFocusRestored = [&] {
                DWORD currentPid = 0;
                return IsWindow(originalForeground) && GetWindowThreadProcessId(originalForeground, &currentPid) == originalForegroundThread &&
                    currentPid == originalForegroundPid && GetForegroundWindow() == originalForeground &&
                    GetGUIThreadInfo(originalForegroundThread, &gui) && gui.hwndFocus == originalFocus;
            };
            HWND before = GetForegroundWindow();
            report.Number("ForegroundBeforeRestoration", reinterpret_cast<uintptr_t>(before));
            bool complete = savedFocusRestored();
            report.Bool("OriginalForegroundAlreadyRestored", complete);
            if (before == window) {
                DWORD pid = 0;
                bool valid = IsWindow(originalForeground) && GetWindowThreadProcessId(originalForeground, &pid) == originalForegroundThread && pid == originalForegroundPid;
                BOOL restored = valid ? SetForegroundWindow(originalForeground) : FALSE;
                report.Bool("RestoreForegroundReturned", restored != FALSE);
                report.Number("RestoreForegroundImmediateWindow", reinterpret_cast<uintptr_t>(GetForegroundWindow()));
                // Cross-input-queue activation is asynchronous. Observe the exact saved focus;
                // no AttachThreadInput, repeated requests, or messages sent to a user window.
                const ULONGLONG started = GetTickCount64();
                complete = valid && Wait(savedFocusRestored, 1500, true);
                report.Number("RestoreForegroundWaitElapsedMs", GetTickCount64() - started);
            } else report.Text("ForegroundRestoration", complete ? "saved foreground and focus already restored; no activation request" :
                "not forced: user focus changed or own editor never obtained foreground");
            report.Number("RestoredForegroundWindow", reinterpret_cast<uintptr_t>(GetForegroundWindow()));
            report.Number("RestoredForegroundFocus", reinterpret_cast<uintptr_t>(gui.hwndFocus));
            report.Check("OriginalForegroundAndFocusRestored", complete, false);
        }
        if (window) { BOOL destroyed = DestroyWindow(window); report.Check("OwnWindowDestroyed", destroyed && !IsWindow(window), false); window = nullptr; }
        if (windowClass) UnregisterClassW(L"TrueFluent.ActivationAcceptance", GetModuleHandleW(nullptr));
        noDocumentPrevious.Reset(); emptyDoc.Reset();
        if (com) CoUninitialize();
        // Finally-equivalent cleanup also runs on every failed gate. Restore position only, never click another window.
        if (cursorSaved && mouseAttempted) {
            SetLastError(ERROR_SUCCESS);
            BOOL restored = SetCursorPos(originalCursor.x, originalCursor.y); DWORD error = GetLastError();
            POINT actual{}; BOOL read = GetCursorPos(&actual);
            report.Number("RestoreCursorError", error); report.Point("CursorAfterCleanup", actual);
            report.Check("OriginalCursorRestored", restored && read && actual.x == originalCursor.x && actual.y == originalCursor.y, false);
        } else report.Text("CursorRestoration", "not needed: mouse input never attempted");
        if (ownerMain) {
            report.Bool("OwnerRetainedAfterClientCleanup", OwnerIdentity(ownerMain));
            report.Bool("OwnerVisibleAfterClientCleanup", IsWindowVisible(ownerMain) != FALSE);
        }
        report.Bool("OwnerTerminationAttempted", false);
        report.Text("OwnerLifetime", "Shared owner is independent of test client exit and retained for normal residence (including newly started idle owner). No remote bridge Close pointer, process kill, maintenance gate or owner cancellation injected.");
        report.Number("ServiceIdleFrames", serviceIdleFrames); report.Number("ServiceNonIdleFrames", serviceNonIdleFrames);
        report.Bool("CleanupCompleted", true);
    }
};
}
int wmain(int argc, wchar_t** argv) {
    // Same-source x64 observer only. No COM activation, GUI, SendInput, owner launch or registry/config read.
    if (argc == 5 && !wcscmp(argv[1], L"--query-owner")) {
        uint64_t hwnd = 0, pid = 0;
        const std::wstring h = argv[2], p = argv[3];
        if (!Decimal(h, 0, h.size(), hwnd) || !Decimal(p, 0, p.size(), pid) || !hwnd || pid > MAXDWORD) return 2;
        const auto value = QueryOwnerWindow(reinterpret_cast<HWND>(static_cast<uintptr_t>(hwnd)), static_cast<DWORD>(pid), argv[4]);
        // Only used path characters cross the pipe; unused 128KiB array capacity must not delay every x86 gate.
        const auto send = [](const void* bytes, DWORD size) {
            DWORD written = 0;
            return WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), bytes, size, &written, nullptr) && written == size;
        };
        return send(&value, static_cast<DWORD>(offsetof(OwnerWindow, exe))) &&
            send(value.exe, static_cast<DWORD>((wcslen(value.exe) + 1) * sizeof(wchar_t))) &&
            send(value.path, static_cast<DWORD>((wcslen(value.path) + 1) * sizeof(wchar_t))) ? 0 : 3;
    }
    if (argc != 6 && (argc != 7 || wcscmp(argv[6], L"--switch-only"))) { std::fputs("Usage: VoiceImeActivationTests.exe <absolute-report.json> <client-dll> <registered-owner-exe> <owner-x64-dll> <same-source-x64-query-exe> [--switch-only]\n", stderr); return 2; }
    const bool switchOnly = argc == 7;
    Report report;
    report.Number("Pid", GetCurrentProcessId()); report.Number("Bitness", sizeof(void*) * 8);
    report.Bool("SwitchOnly", switchOnly);
    SetLastError(ERROR_SUCCESS);
    using SetDpi = BOOL (WINAPI*)(HANDLE);
    auto setDpi = reinterpret_cast<SetDpi>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetProcessDpiAwarenessContext"));
    BOOL dpiAware = setDpi && setDpi(reinterpret_cast<HANDLE>(-4)); // EXE only, before COM/any UI.
    report.Bool("ProcessDpiAware", dpiAware != FALSE); report.Number("SetProcessDpiAwareError", dpiAware ? 0 : GetLastError());
    report.Check("ProcessUsesPMV2", dpiAware && Pmv2(), false);
    report.Text("Scope", switchOnly ? "Windows TSF real client/owner identity and Idle; FORPROCESS enabled Sogou preferred, actual other Profile/foreground service release/owner HWND hide/Voice reselect; no hover/screenshot/empty Context/read-only/recording" :
        "Windows TSF client activation, actual shared x64 owner UI/Idle/hover, empty Context key eligibility and FORPROCESS other IME hide/reselect; no recording");
    if (switchOnly) {
        report.Text("CaptureStatus", "Skipped");
        report.Text("CaptureSkipReason", "SwitchOnly 不截图、不模拟悬停；用户已提供截图，本模式不要求强制目视 UI。");
        report.Bool("NoDocumentSkipped", true); report.Bool("ReadOnlySkipped", true);
    }
    report.Bool("F8KeyDownDispatched", false); report.Bool("SyntheticPartialFinalInjected", false);
    report.Text("PartialFinalAssessment", "No public frame injection contract: production Receive requires private active/listening/run state. A TSF edit lock alone cannot invoke production Replace; not attempted.");
    {
        Host host(report, argv[1], argv[2], argv[3], argv[4], argv[5], switchOnly);
        try { if (!report.failures) host.Run(); }
        catch (const Failure&) {}
        catch (...) { report.Check("UnexpectedException", false, false); }
        host.Cleanup();
    }
    report.Number("Failures", report.failures); report.Number("ExitCode", report.failures ? 1 : 0);
    report.Bool("Passed", report.failures == 0);
    if (!report.Save(argv[1])) { std::fputs("Cannot save report\n", stderr); return 3; }
    std::printf("FINAL RESULT failures=%u exit=%d; actual system ServiceUI/activation/key eligibility; no recording\n", report.failures, report.failures ? 1 : 0);
    return report.failures ? 1 : 0;
}