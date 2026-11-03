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
    bool com = false, activated = false, associated = false, pushed = false, attempted = false, closed = false, cancelled = false;
    HWND window{};
    HWND originalForeground{}, originalFocus{}, serviceDispatcher{};
    DWORD originalForegroundPid = 0, originalForegroundThread = 0;
    bool temporaryTopmost = false;
    unsigned serviceTimerUpdates = 0;
    ULONGLONG activationStarted = 0, firstServiceTimerElapsed = 0;
    ATOM windowClass = 0;
    ComPtr<ITfThreadMgr> manager;
    ComPtr<ITfInputProcessorProfileMgr> profiles;
    ComPtr<ITfKeystrokeMgr> keys;
    ComPtr<ITfDocumentMgr> doc, previous;
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
                bool serviceTimer = activationStarted && msg.message == WM_TIMER && msg.wParam == 1 && !msg.lParam &&
                    Own(msg.hwnd) && ClassName(msg.hwnd).find(L"TrueFluentVoiceIme.Dispatch.") == 0 &&
                    _wcsicmp(ModulePath(reinterpret_cast<HMODULE>(GetClassLongPtrW(msg.hwnd, GCLP_HMODULE))).c_str(), registeredDll.c_str()) == 0;
                DispatchMessageW(&msg);
                if (serviceTimer && GetTickCount64() - activationStarted >= 500) {
                    serviceDispatcher = msg.hwnd;
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
        // Complete normal showing before the final TOPMOST placement. Do not let a later ShowWindow
        // rewrite the Z order. Record the actual startup override rather than infer it from CreateNoWindow.
        STARTUPINFOW startup{sizeof(startup)}; GetStartupInfoW(&startup);
        report.Number("StartupFlags", startup.dwFlags); report.Number("StartupShowWindow", startup.wShowWindow);
        report.Bool("StartupOverridesFirstShow", (startup.dwFlags & STARTF_USESHOWWINDOW) != 0);
        MONITORINFO monitor{sizeof(monitor)}; RECT clip{}, area{};
        report.Check("OwnPlacementWorkAreaAvailable", GetMonitorInfoW(MonitorFromWindow(originalForeground, MONITOR_DEFAULTTOPRIMARY), &monitor) != FALSE);
        report.Check("OwnPlacementCursorClipAvailable", GetClipCursor(&clip) != FALSE);
        report.Check("OwnPlacementSafeAreaAvailable", IntersectRect(&area, &monitor.rcWork, &clip) &&
            area.right - area.left >= 240 && area.bottom - area.top >= 160);
        const UINT dpi = Dpi(window);
        const int w = std::min<int>(MulDiv(620, dpi, 96), area.right - area.left - 16);
        const int h = std::min<int>(MulDiv(220, dpi, 96), area.bottom - area.top - 16);
        report.Rect("OwnPlacementSafeArea", area);
        report.Check("OwnWindowBeforeShow", Own(window));
        ShowWindow(window, SW_SHOWNORMAL);
        if (startup.dwFlags & STARTF_USESHOWWINDOW) ShowWindow(window, SW_SHOWNOACTIVATE);
        report.Bool("OwnWindowForegroundAfterShow", GetForegroundWindow() == window);
        // Only our test editor is briefly topmost; never raise, move or subclass a user window.
        temporaryTopmost = true;
        report.Check("OwnWindowPlacedTemporarilyTopmost", Own(window) && SetWindowPos(window, HWND_TOPMOST,
            area.left + (area.right - area.left - w) / 2, area.top + (area.bottom - area.top - h) / 2,
            w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW));
        UpdateWindow(window);
        report.Bool("OrdinarySetForegroundWindowReturned", SetForegroundWindow(window) != FALSE);
        report.Check("OwnWindowActuallyVisible", IsWindowVisible(window) && !IsIconic(window));
        const auto ownFocused = [&] { return Own(window) && GetForegroundWindow() == window && GetFocus() == window; };
        RECT client{}; POINT target{};
        report.Check("OwnClientRectAvailable", GetClientRect(window, &client) && client.right > 20 && client.bottom > 20);
        const auto safePoint = [&](POINT point) {
            bool safe = Own(window) && IsWindowVisible(window) && IsWindowEnabled(window);
            for (LONG dx : {-2L, 0L, 2L}) for (LONG dy : {-2L, 0L, 2L}) {
                HWND hit = WindowFromPoint({point.x + dx, point.y + dy});
                DWORD pid = 0; GetWindowThreadProcessId(hit, &pid);
                safe = safe && hit && GetAncestor(hit, GA_ROOT) == window && pid == GetCurrentProcessId();
            }
            return safe;
        };
        const auto inputAllowed = [&] {
            return ownFocused() || (temporaryTopmost && (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOPMOST) && safePoint(target));
        };
        // Find an exposed client patch after the compositor/message queue has settled, not fixed desktop pixels.
        const ULONGLONG hitStarted = GetTickCount64();
        bool exposed = Wait([&] {
            for (int y : {2, 1, 3}) for (int x : {2, 1, 3}) {
                POINT candidate{client.right * x / 4, client.bottom * y / 4};
                if (ClientToScreen(window, &candidate) && safePoint(candidate)) { target = candidate; return true; }
            }
            return false;
        }, 1500);
        report.Number("OwnHitWaitElapsedMs", GetTickCount64() - hitStarted);
        report.Number("OwnWindow", reinterpret_cast<uintptr_t>(window));
        report.Bool("OwnWindowThreadAndPidVerified", Own(window));
        report.Bool("OwnWindowEnabledAtHitTest", IsWindowEnabled(window) != FALSE);
        report.Bool("OwnWindowTopmostAtHitTest", (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0);
        RECT actualWindow{}; GetWindowRect(window, &actualWindow); report.Rect("OwnWindowRectAtHitTest", actualWindow);
        if (!exposed) {
            std::string hits = "[";
            for (int y : {2, 1, 3}) for (int x : {2, 1, 3}) {
                POINT point{client.right * x / 4, client.bottom * y / 4}; ClientToScreen(window, &point);
                HWND hit = WindowFromPoint(point); DWORD pid = 0; GetWindowThreadProcessId(hit, &pid);
                if (hits.size() > 1) hits += ',';
                hits += "{\"X\":" + std::to_string(point.x) + ",\"Y\":" + std::to_string(point.y) +
                    ",\"Root\":" + std::to_string(reinterpret_cast<uintptr_t>(GetAncestor(hit, GA_ROOT))) +
                    ",\"Pid\":" + std::to_string(pid) + ",\"Class\":" + Json(Utf8(ClassName(hit))) + "}";
            }
            report.values["BlockedOwnCandidateHits"] = hits + ']'; // Metadata only; never user text or screenshots.
        }
        report.Check("OwnClientExposedAfterPump", exposed);
        // A denied SetForegroundWindow alone never authorizes SendInput. An inactive window may only
        // receive one ordinary mouse-activation click in its verified exposed, still-topmost client patch.
        report.Bool("OwnWindowFocusedBeforeMouseInput", ownFocused());
        report.Check("OwnClientReadyForMouseActivation", inputAllowed());
        report.Text("MouseInputAuthority", ownFocused() ? "already focused owned editor" :
            "verified exposed topmost owned client only; ordinary WM_MOUSEACTIVATE; foreground verified after click");
        report.Point("ClickScreenPoint", target);
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        report.Check("ClickInsideVirtualDesktop", width > 1 && height > 1 && target.x >= left && target.y >= top &&
            target.x < left + width && target.y < top + height);
        report.Check("ClickNotConfinedElsewhere", GetClipCursor(&clip) &&
            target.x - 2 >= clip.left && target.y - 2 >= clip.top && target.x + 2 < clip.right && target.y + 2 < clip.bottom);
        INPUT events[3]{};
        for (auto& event : events) { event.type = INPUT_MOUSE; event.mi.dwExtraInfo = MouseTag; }
        events[0].mi.dx = MulDiv(target.x - left, 65535, width - 1);
        events[0].mi.dy = MulDiv(target.y - top, 65535, height - 1);
        events[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        events[1].mi.dwFlags = MOUSEEVENTF_LEFTDOWN; events[2].mi.dwFlags = MOUSEEVENTF_LEFTUP;
        HWND initialHit = WindowFromPoint(target); DWORD initialHitPid = 0;
        GetWindowThreadProcessId(initialHit, &initialHitPid);
        report.Number("InitialClickHitWindow", reinterpret_cast<uintptr_t>(initialHit));
        report.Number("InitialClickHitPid", initialHitPid);
        GUITHREADINFO gui{sizeof(GUITHREADINFO)};
        report.Check("ForegroundGuiStateAvailable", GetGUIThreadInfo(0, &gui) != FALSE);
        report.Number("MouseCaptureBeforeClick", reinterpret_cast<uintptr_t>(gui.hwndCapture));
        report.Check("NoCapturedMouseOrActiveMenu", !gui.hwndCapture &&
            !(gui.flags & (GUI_INMENUMODE | GUI_SYSTEMMENUMODE | GUI_POPUPMENUMODE | GUI_INMOVESIZE)));
        report.Check("NoHeldMouseButtonsOrModifiers", InputReleased());
        // Last gate before SendInput: exact owned HWND/PID, including a rounding guard around the target pixel.
        HWND hit = WindowFromPoint(target); DWORD hitPid = 0;
        GetWindowThreadProcessId(hit, &hitPid);
        report.Number("ClickHitWindow", reinterpret_cast<uintptr_t>(hit)); report.Number("ClickHitPid", hitPid);
        report.Number("OwnWindow", reinterpret_cast<uintptr_t>(window));
        report.Bool("HitWindowTopmost", (GetWindowLongPtrW(hit, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0);
        report.Bool("OwnWindowTopmost", (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0);
        report.Check("ClickTargetVerifiedOwnClient", inputAllowed() && safePoint(target));
        mouseAttempted = true;
        SetLastError(ERROR_SUCCESS);
        UINT moved = SendInput(1, events, sizeof(INPUT));
        report.Number("MouseMoveSendInputSent", moved); report.Number("MouseMoveSendInputError", GetLastError());
        report.Check("MouseMoveInserted", moved == 1);
        POINT actual{};
        report.Check("MouseMoveReachedOwnClient", Wait([&] {
            return GetCursorPos(&actual) && std::abs(actual.x - target.x) <= 2 && std::abs(actual.y - target.y) <= 2;
        }, 500));
        report.Point("CursorAtClick", actual);
        // Recheck the actual destination immediately before the second (down/up only) SendInput call.
        report.Check("MouseClickReverifiedOwnRootAndPid", inputAllowed() && InputReleased() && safePoint(target) && safePoint(actual) &&
            GetGUIThreadInfo(0, &gui) && !gui.hwndCapture && !(gui.flags & (GUI_INMENUMODE | GUI_SYSTEMMENUMODE | GUI_POPUPMENUMODE | GUI_INMOVESIZE)));
        SetLastError(ERROR_SUCCESS);
        UINT clicked = SendInput(2, events + 1, sizeof(INPUT)); DWORD error = GetLastError();
        UINT sent = moved + clicked;
        report.Number("MouseSendInputRequested", 3); report.Number("MouseSendInputSent", sent);
        report.Number("MouseSendInputError", error);
        report.Text("MouseInputFailureInterpretation", "Raw SendInput count/error and desktop/observed messages only; UIPI cause is not identifiable from SendInput return/error.");
        bool received = Wait([&] { return clickDown == 1 && clickUp == 1 && GetForegroundWindow() == window; }, 2500);
        report.Number("OwnTaggedMouseDown", clickDown); report.Number("OwnTaggedMouseUp", clickUp);
        DWORD foregroundPid = 0; HWND foreground = GetForegroundWindow(); GetWindowThreadProcessId(foreground, &foregroundPid);
        report.Number("ForegroundAfterClick", reinterpret_cast<uintptr_t>(foreground));
        report.Number("ForegroundPidAfterClick", foregroundPid);
        report.Check("MouseSequenceInserted", sent == 3);
        report.Check("OwnWindowReceivedNormalClick", received && clickDown == 1 && clickUp == 1);
        report.Check("OwnWindowObtainedForeground", foreground == window && foregroundPid == GetCurrentProcessId());
        report.Check("OwnWindowTopmostRemoved", SetWindowPos(window, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) && !(GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOPMOST));
        temporaryTopmost = false;
        report.Text("ForegroundAcquisition", "Verified own client; one SendInput mouse move/down/up; ordinary WM_MOUSEACTIVATE; no foreground-policy bypass.");
    }
    std::vector<HWND> ToolbarWindows() {
        std::vector<HWND> found;
        EnumWindows([](HWND hwnd, LPARAM data) -> BOOL {
            if (Own(hwnd) && ClassName(hwnd).find(L"TrueFluentVoiceIme.Toolbar.") == 0)
                reinterpret_cast<std::vector<HWND>*>(data)->push_back(hwnd);
            return TRUE;
        }, reinterpret_cast<LPARAM>(&found));
        return found;
    }
    void VerifyServiceUi() {
        const bool updated = Wait([&] { return serviceTimerUpdates > 0; }, 2500);
        report.Number("Service500msTimerUpdates", serviceTimerUpdates);
        report.Number("FirstServiceTimerElapsedMs", firstServiceTimerElapsed);
        report.Number("ServiceDispatcherWindow", reinterpret_cast<uintptr_t>(serviceDispatcher));
        report.Check("Production500msTimerDispatched", updated && firstServiceTimerElapsed >= 500);
        FocusGate("EditorFocusedAfterProductionTimer");
        const auto windows = ToolbarWindows();
        report.Number("OwnVoiceToolbarWindowCount", windows.size());
        HWND main{}, tip{}, notice{};
        std::string inventory = "[";
        for (HWND hwnd : windows) {
            RECT rect{}; GetWindowRect(hwnd, &rect);
            const auto text = WindowText(hwnd);
            const auto module = ModulePath(reinterpret_cast<HMODULE>(GetClassLongPtrW(hwnd, GCLP_HMODULE)));
            if (inventory.size() > 1) inventory += ',';
            inventory += "{\"Hwnd\":" + std::to_string(reinterpret_cast<uintptr_t>(hwnd)) +
                ",\"Class\":" + Json(Utf8(ClassName(hwnd))) + ",\"Text\":" + Json(Utf8(text)) +
                ",\"Visible\":" + (IsWindowVisible(hwnd) ? "true" : "false") + ",\"Module\":" + Json(Utf8(module)) +
                ",\"Width\":" + std::to_string(rect.right - rect.left) + ",\"Height\":" + std::to_string(rect.bottom - rect.top) + "}";
            if (text == L"译见pro输入法" && !GetWindow(hwnd, GW_OWNER)) main = hwnd;
        }
        report.values["OwnToolbarWindows"] = inventory + ']';
        report.Check("OwnMainAndTwoHintsExist", windows.size() == 3 && main);
        for (HWND hwnd : windows) if (hwnd != main && GetWindow(hwnd, GW_OWNER) == main) {
            if (WindowText(hwnd) == L"按下 F8 开始录音") tip = hwnd;
            else if (WindowText(hwnd).empty()) notice = hwnd;
        }
        report.Check("OwnTooltipAndNoticeIdentified", tip && notice && tip != notice);
        bool styles = true, modules = true, awareness = true;
        constexpr LONG_PTR required = WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        for (HWND hwnd : windows) {
            styles = styles && (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & required) == required;
            modules = modules && _wcsicmp(ModulePath(reinterpret_cast<HMODULE>(GetClassLongPtrW(hwnd, GCLP_HMODULE))).c_str(), registeredDll.c_str()) == 0;
            awareness = awareness && Pmv2(hwnd) && Dpi(hwnd) == Dpi(window);
        }
        report.Text("ToolbarClassModule", Utf8(ModulePath(reinterpret_cast<HMODULE>(GetClassLongPtrW(main, GCLP_HMODULE)))));
        report.Check("ToolbarClassModuleIsRegisteredDll", modules);
        report.Check("ToolbarNoActivateLayeredToolWindow", styles);
        report.Check("ProductionRetainsHostPMV2", awareness && Pmv2(window) && Pmv2());
        RECT rect{}, client{}; const UINT dpi = Dpi(main);
        report.Check("ToolbarRectAvailable", dpi && GetWindowRect(main, &rect) && GetClientRect(main, &client));
        report.Number("ToolbarDpi", dpi); report.Rect("ToolbarMainRect", rect);
        report.Number("ToolbarBodyExpectedWidthDip", 96); report.Number("ToolbarBodyExpectedHeightDip", 36);
        report.Check("ToolbarSurface104x44Dip", rect.right - rect.left == MulDiv(104, dpi, 96) &&
            rect.bottom - rect.top == MulDiv(44, dpi, 96) && client.right == MulDiv(104, dpi, 96) && client.bottom == MulDiv(44, dpi, 96));
        report.Check("IdleMainVisibleHintsHidden", IsWindowVisible(main) && !IsWindowVisible(tip) && !IsWindowVisible(notice));
        FocusGate("EditorFocusedAtIdleUi");
        report.Check("OwnMainBeforeHoverMessage", Own(main));
        SendMessageW(main, WM_MOUSEMOVE, 0, MAKELPARAM(MulDiv(74, dpi, 96), MulDiv(22, dpi, 96)));
        report.Check("OwnMainBeforeHoverTimerMessage", Own(main));
        SendMessageW(main, WM_TIMER, 2, 0); // Real production hover handler only. Never a mic click/F8/recording timer.
        report.Text("HoverTooltipText", Utf8(WindowText(tip)));
        report.Check("HoverTooltipVisibleWithStartText", IsWindowVisible(tip) && WindowText(tip) == L"按下 F8 开始录音");
        report.Check("HoverMainVisibleNoticeHidden", IsWindowVisible(main) && !IsWindowVisible(notice));
        FocusGate("EditorFocusedDuringHover");
        CaptureOwnMain(main, tip, rect);
        FocusGate("EditorFocusedAfterScreenshot");
        report.Check("OwnMainBeforeHoverCleanup", Own(main));
        SendMessageW(main, WM_MOUSELEAVE, 0, 0);
        report.Check("HoverClearedMainStillVisible", !IsWindowVisible(tip) && !IsWindowVisible(notice) && IsWindowVisible(main));
    }
    void CaptureOwnMain(HWND main, HWND tip, RECT mainRect) {
        RECT tipRect{}, capture{}, editor{};
        report.Check("OwnCaptureRectsAvailable", GetWindowRect(tip, &tipRect) && UnionRect(&capture, &mainRect, &tipRect) && GetClientRect(window, &editor));
        MapWindowPoints(window, nullptr, reinterpret_cast<POINT*>(&editor), 2);
        report.Rect("OwnMainScreenshotRect", capture);
        report.Check("CaptureOnlyWithinOwnEditor", capture.left >= editor.left && capture.top >= editor.top &&
            capture.right <= editor.right && capture.bottom <= editor.bottom);
        const auto exposed = [&] {
            if (!Own(main) || !Own(tip) || GetForegroundWindow() != window || GetFocus() != window) return false;
            // WindowFromPoint skips transparent windows. Also reject every foreign overlay above our editor.
            HWND above = GetWindow(window, GW_HWNDPREV);
            for (unsigned n = 0; above && n < 512; ++n, above = GetWindow(above, GW_HWNDPREV)) {
                if (Own(above) || !IsWindowVisible(above) || IsIconic(above)) continue;
                RECT other{}, overlap{};
                if (!GetWindowRect(above, &other) || IntersectRect(&overlap, &capture, &other)) return false;
            }
            if (above) return false; // Bounded traversal exhausted: unknown is not safe.
            for (LONG y = capture.top; y < capture.bottom; ++y) for (LONG x = capture.left; x < capture.right; ++x) {
                HWND hit = WindowFromPoint({x, y}); HWND root = hit ? GetAncestor(hit, GA_ROOT) : nullptr;
                if (!Own(hit) || (root != window && root != main && root != tip)) return false;
            }
            return true;
        };
        report.Check("OwnScreenshotAreaFullyExposed", exposed());
        ULONG_PTR token{}; Gdiplus::GdiplusStartupInput startup;
        report.Check("ScreenshotGraphicsStarted", Gdiplus::GdiplusStartup(&token, &startup, nullptr) == Gdiplus::Ok);
        const auto path = (std::filesystem::path(reportPath).parent_path() / L"OWNMAIN.png").wstring();
        bool saved = false; unsigned bodyPixels = 0;
        RECT body{LONG_MAX, LONG_MAX, 0, 0};
        {
            // The only screenshot is the real composed main + own Unicode hover hint, not a re-rendered fixture.
            DwmFlush();
            HDC screen = GetDC(nullptr), memory = screen ? CreateCompatibleDC(screen) : nullptr;
            const int width = capture.right - capture.left, height = capture.bottom - capture.top;
            HBITMAP bitmap = memory ? CreateCompatibleBitmap(screen, width, height) : nullptr;
            if (bitmap) {
                HGDIOBJ old = SelectObject(memory, bitmap);
                if (exposed() && BitBlt(memory, 0, 0, width, height, screen, capture.left, capture.top, SRCCOPY | CAPTUREBLT)) {
                    Gdiplus::Bitmap image(bitmap, nullptr);
                    UINT count = 0, bytes = 0; Gdiplus::GetImageEncodersSize(&count, &bytes);
                    std::vector<BYTE> storage(bytes);
                    auto encoders = reinterpret_cast<Gdiplus::ImageCodecInfo*>(storage.data());
                    if (bytes && Gdiplus::GetImageEncoders(count, bytes, encoders) == Gdiplus::Ok)
                        for (UINT i = 0; i < count; ++i) if (wcscmp(encoders[i].MimeType, L"image/png") == 0)
                            saved = image.Save(path.c_str(), &encoders[i].Clsid) == Gdiplus::Ok;
                    // Observe the opaque blue panel bounds against our white editor; alpha-shadow margins excluded.
                    for (int y = 0; y < mainRect.bottom - mainRect.top; ++y) for (int x = 0; x < mainRect.right - mainRect.left; ++x) {
                        Gdiplus::Color c;
                        if (image.GetPixel(mainRect.left - capture.left + x, mainRect.top - capture.top + y, &c) == Gdiplus::Ok &&
                            c.GetR() >= 180 && c.GetG() >= 200 && c.GetB() >= 225 && c.GetB() - c.GetR() >= 8) {
                            body.left = std::min(body.left, static_cast<LONG>(x)); body.top = std::min(body.top, static_cast<LONG>(y));
                            body.right = std::max(body.right, static_cast<LONG>(x + 1)); body.bottom = std::max(body.bottom, static_cast<LONG>(y + 1)); ++bodyPixels;
                        }
                    }
                }
                SelectObject(memory, old); DeleteObject(bitmap);
            }
            if (memory) DeleteDC(memory);
            if (screen) ReleaseDC(nullptr, screen);
        }
        Gdiplus::GdiplusShutdown(token);
        report.Text("OwnMainScreenshot", Utf8(path)); report.Number("ScreenshotWidth", capture.right - capture.left);
        report.Number("ScreenshotHeight", capture.bottom - capture.top); report.Rect("ObservedPanelPixelBounds", body);
        report.Text("VisualAssessment", "Review this single actual OWNMAIN.png for brand, microphone and Chinese hover font; WM_GETFONT is not evidence for custom GDI+ text.");
        report.Check("OwnMainScreenshotSaved", saved);
        const UINT dpi = Dpi(main);
        report.Check("ObservedBody96x36Dip", bodyPixels && std::abs(body.right - body.left - MulDiv(96, dpi, 96)) <= 2 &&
            std::abs(body.bottom - body.top - MulDiv(36, dpi, 96)) <= 2);
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
    Host(Report& r, const wchar_t* path, const wchar_t* expected) : report(r), reportPath(path), expectedDll(expected) {}
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
        window = CreateWindowExW(0, cls.lpszClassName, L"TrueFluent TSF 激活验收（不录音）", WS_OVERLAPPEDWINDOW,
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

        report.Require("SetReadOnlyHr", store->SetReadOnly(true));
        report.Require("ReadOnlyStatusHr", context->GetStatus(&state));
        report.Number("ReadOnlyDynamicFlags", state.dwDynamicFlags); report.Number("ReadOnlyStaticFlags", state.dwStaticFlags);
        report.Check("TsfContextConfirmsReadOnly", (state.dwDynamicFlags & TF_SD_READONLY) != 0);
        TestKey("F8_SelectedReadOnlyNotEaten", VK_F8, false);
        report.Require("ClearReadOnlyHr", store->SetReadOnly(false));
        report.Check("NoTextInjected", store->writes == 0 && store->text == L"ABTAIL");
    }
    void Cleanup() noexcept {
        if (closed) return;
        closed = true;
        // Unwind in the original STA, while its window/context and message queue are still alive.
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
        if (com) CoUninitialize();
        // Finally-equivalent cleanup also runs on every failed gate. Restore position only, never click another window.
        if (cursorSaved && mouseAttempted) {
            SetLastError(ERROR_SUCCESS);
            BOOL restored = SetCursorPos(originalCursor.x, originalCursor.y); DWORD error = GetLastError();
            POINT actual{}; BOOL read = GetCursorPos(&actual);
            report.Number("RestoreCursorError", error); report.Point("CursorAfterCleanup", actual);
            report.Check("OriginalCursorRestored", restored && read && actual.x == originalCursor.x && actual.y == originalCursor.y, false);
        } else report.Text("CursorRestoration", "not needed: mouse input never attempted");
        report.Bool("CleanupCompleted", true);
    }
};
}
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) { std::fputs("Usage: VoiceImeActivationTests.exe <absolute-report.json> <preflight-registered-dll>\n", stderr); return 2; }
    Report report;
    report.Number("Pid", GetCurrentProcessId()); report.Number("Bitness", sizeof(void*) * 8);
    SetLastError(ERROR_SUCCESS);
    using SetDpi = BOOL (WINAPI*)(HANDLE);
    auto setDpi = reinterpret_cast<SetDpi>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetProcessDpiAwarenessContext"));
    BOOL dpiAware = setDpi && setDpi(reinterpret_cast<HANDLE>(-4)); // EXE only, before COM/any UI.
    report.Bool("ProcessDpiAware", dpiAware != FALSE); report.Number("SetProcessDpiAwareError", dpiAware ? 0 : GetLastError());
    report.Check("ProcessUsesPMV2", dpiAware && Pmv2(), false);
    report.Text("Scope", "real system TIP activation, production ServiceUI and key eligibility; not speech input");
    report.Bool("F8KeyDownDispatched", false); report.Bool("SyntheticPartialFinalInjected", false);
    report.Text("PartialFinalAssessment", "No public frame injection contract: production Receive requires private active/listening/run state. A TSF edit lock alone cannot invoke production Replace; not attempted.");
    {
        Host host(report, argv[1], argv[2]);
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