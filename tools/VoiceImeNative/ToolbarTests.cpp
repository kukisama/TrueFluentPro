// Actual toolbar only. No TSF, Registration, host, configuration, audio or cloud.
#include "VoiceToolbar.h"
#include "VoiceBrand.h"
#include <dwmapi.h>
#include <shellapi.h>
#include <windowsx.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <vector>
#include <string>

namespace {
bool PngEncoder(CLSID& encoder) {
    UINT count = 0, bytes = 0; Gdiplus::GetImageEncodersSize(&count, &bytes);
    if (!bytes) return false;
    std::vector<BYTE> storage(bytes);
    auto* encoders = reinterpret_cast<Gdiplus::ImageCodecInfo*>(storage.data());
    if (Gdiplus::GetImageEncoders(count, bytes, encoders) != Gdiplus::Ok) return false;
    for (UINT i = 0; i < count; ++i) if (wcscmp(encoders[i].MimeType, L"image/png") == 0) { encoder = encoders[i].Clsid; return true; }
    return false;
}
void Pump() {
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
}
}
struct VoiceToolbarTestAccess {
    static VoiceToolbarState State(const VoiceToolbar& t) { return t.state_; }
    static bool Hovered(const VoiceToolbar& t) { return t.hovered_; }
    static unsigned Animation(const VoiceToolbar& t) { return t.animation_; }
    static HWND Tooltip(const VoiceToolbar& t) { return t.tooltip_; }
    static HWND Notice(const VoiceToolbar& t) { return t.notice_; }
    static const std::wstring& NoticeText(const VoiceToolbar& t) { return t.noticeText_; }
    static const std::wstring& ClassName(const VoiceToolbar& t) { return t.className_; }
    static LPARAM Point(const VoiceToolbar& t, int x, int y) { return MAKELPARAM(t.Pixels(x), t.Pixels(y)); }
    static void Place(VoiceToolbar& t, POINT point) { t.Place(point); }
    static void DpiChecks(VoiceToolbar& t, unsigned& failures);
    static bool Save(const VoiceToolbar& t, const std::wstring& path, UINT dpi = 96) {
        CLSID encoder{}; if (!PngEncoder(encoder)) return false;
        Gdiplus::Bitmap image(MulDiv(260, dpi, 96), MulDiv(152, dpi, 96), PixelFormat32bppARGB);
        {
            Gdiplus::Graphics g(&image); g.Clear(Gdiplus::Color(255, 239, 245, 253));
            g.ScaleTransform(dpi / 96.0f, dpi / 96.0f);
            auto saved = g.Save(); g.TranslateTransform(78, 56); t.PaintMain(g); g.Restore(saved);
            if (t.hovered_) {
                const SIZE size = t.HintSize(t.TooltipText(), false);
                saved = g.Save(); g.TranslateTransform((260 - size.cx) / 2.0f, 10);
                t.PaintHint(g, t.TooltipText(), false, size.cx, size.cy); g.Restore(saved);
            }
            if (!t.noticeText_.empty()) {
                const SIZE size = t.HintSize(t.noticeText_.c_str(), true);
                saved = g.Save(); g.TranslateTransform((260 - size.cx) / 2.0f, 106);
                t.PaintHint(g, t.noticeText_.c_str(), true, size.cx, size.cy); g.Restore(saved);
            }
        }
        const bool ok = image.Save(path.c_str(), &encoder) == Gdiplus::Ok;
        std::printf("PNG production-render dpi=%u success=%d\n", dpi, ok); return ok;
    }
    static bool CaptureWindow(const VoiceToolbar& t, const std::wstring& path) {
        CLSID encoder{}; if (!PngEncoder(encoder)) return false;
        RECT rect{}; if (!GetWindowRect(t.window_, &rect)) return false;
        DwmFlush(); // Wait for our just-updated alpha surface before this one screen crop.
        HDC screen = GetDC(nullptr), memory = screen ? CreateCompatibleDC(screen) : nullptr;
        HBITMAP bitmap = memory ? CreateCompatibleBitmap(screen, rect.right - rect.left, rect.bottom - rect.top) : nullptr;
        bool ok = false;
        if (bitmap) {
            HGDIOBJ old = SelectObject(memory, bitmap);
            if (BitBlt(memory, 0, 0, rect.right - rect.left, rect.bottom - rect.top, screen, rect.left, rect.top, SRCCOPY | CAPTUREBLT)) {
                Gdiplus::Bitmap image(bitmap, nullptr); ok = image.Save(path.c_str(), &encoder) == Gdiplus::Ok;
            }
            SelectObject(memory, old); DeleteObject(bitmap);
        }
        if (memory) DeleteDC(memory); if (screen) ReleaseDC(nullptr, screen);
        std::printf("PNG actual-window width=%ld height=%ld success=%d\n", rect.right - rect.left, rect.bottom - rect.top, ok); return ok;
    }
};
namespace {
struct PositionTrace {
    WNDPROC previous{};
    RECT suggested{};
    UINT dpi{};
    bool inject = false, sawSuggested = false;
};
PositionTrace* trace{}; // Own test HWND only; never subclasses the host/editor.
LRESULT CALLBACK TracePosition(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_WINDOWPOSCHANGING) {
        const auto& pos = *reinterpret_cast<WINDOWPOS*>(l);
        if (!(pos.flags & (SWP_NOMOVE | SWP_NOSIZE)) && pos.x == trace->suggested.left && pos.y == trace->suggested.top &&
            pos.cx == trace->suggested.right - trace->suggested.left && pos.cy == trace->suggested.bottom - trace->suggested.top)
            trace->sawSuggested = true;
        if (trace->inject) {
            trace->inject = false;
            SendMessageW(hwnd, WM_DPICHANGED, MAKELONG(trace->dpi, trace->dpi), reinterpret_cast<LPARAM>(&trace->suggested));
        }
    }
    return CallWindowProcW(trace->previous, hwnd, message, w, l);
}
UINT ActualDpi(HWND hwnd) {
    using GetDpi = UINT (WINAPI*)(HWND);
    const auto get = reinterpret_cast<GetDpi>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForWindow"));
    return get ? get(hwnd) : 96;
}
int Awareness(HWND hwnd) {
    using GetContext = HANDLE (WINAPI*)(HWND);
    using GetAwareness = int (WINAPI*)(HANDLE);
    const auto context = reinterpret_cast<GetContext>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetWindowDpiAwarenessContext"));
    const auto awareness = reinterpret_cast<GetAwareness>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetAwarenessFromDpiAwarenessContext"));
    return context && awareness ? awareness(context(hwnd)) : -1;
}
BOOL CALLBACK CountMonitor(HMONITOR, HDC, LPRECT, LPARAM data) { ++*reinterpret_cast<unsigned*>(data); return TRUE; }
bool ClientSize(HWND hwnd, SIZE size) {
    RECT r{}; return GetClientRect(hwnd, &r) && r.right == size.cx && r.bottom == size.cy;
}
struct Harness { VoiceToolbar* toolbar; unsigned toggles = 0; };
void Toggle(void* owner) {
    auto& h = *static_cast<Harness*>(owner); ++h.toggles;
    const bool stopping = VoiceToolbarTestAccess::State(*h.toolbar) != VoiceToolbarState::Idle;
    h.toolbar->SetState(stopping ? VoiceToolbarState::Idle : VoiceToolbarState::Connecting, stopping);
}
void Click(VoiceToolbar& t) {
    const auto point = VoiceToolbarTestAccess::Point(t, 74, 22);
    SendMessageW(t.Handle(), WM_LBUTTONDOWN, MK_LBUTTON, point);
    SendMessageW(t.Handle(), WM_LBUTTONUP, 0, point);
}
}
void VoiceToolbarTestAccess::DpiChecks(VoiceToolbar& t, unsigned& failures) {
    const auto check = [&](bool ok, const char* name) { std::printf("%s DPI %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failures; };
    const UINT nativeDpi = ActualDpi(t.window_);
    const POINT original = t.position_;
    const auto state = t.state_; const bool hovered = t.hovered_;
    const std::wstring notice = t.noticeText_;
    const HWND oldEditor = t.editor_; const HMONITOR oldMonitor = t.editorMonitor_; const UINT oldEditorDpi = t.editorDpi_;
    MONITORINFO nativeWork{sizeof(nativeWork)}; GetMonitorInfoW(MonitorFromWindow(t.window_,MONITOR_DEFAULTTONEAREST), &nativeWork);
    t.Place({nativeWork.rcWork.left + 20, nativeWork.rcWork.top + 60});
    const POINT testOrigin = t.position_;
    unsigned monitors = 0; EnumDisplayMonitors(nullptr, nullptr, CountMonitor, reinterpret_cast<LPARAM>(&monitors));
    std::printf("ENV awareness=%d native-main-dpi=%u hint-dpi=%u/%u monitors=%u; virtual scales below are not monitor changes\n",
        Awareness(t.window_), nativeDpi, ActualDpi(t.tooltip_), ActualDpi(t.notice_), monitors);
    check(nativeDpi > 0 && ActualDpi(t.tooltip_) == nativeDpi && ActualDpi(t.notice_) == nativeDpi &&
        Awareness(t.window_) == Awareness(t.tooltip_) && Awareness(t.window_) == Awareness(t.notice_), "owned HWNDs inherit the same native DPI/awareness");
    struct Scale { UINT dpi; int width, height, bodyWidth, bodyHeight; };
    // Independent expectations, not computed with the production size function.
    const Scale scales[] = {{96,104,44,96,36}, {120,130,55,120,45}, {144,156,66,144,54}, {192,208,88,192,72}};
    const RECT negativeWork{-1920, -1080, 0, 0};
    t.state_ = VoiceToolbarState::Idle; t.hovered_ = false;
    for (const auto& s : scales) {
        t.dpi_ = s.dpi;
        const SIZE size = t.ScaledSize(104, 44, s.dpi);
        check(size.cx == s.width && size.cy == s.height, "100/125/150/200 percent pure pixel size");
        Gdiplus::Bitmap image(s.width, s.height, PixelFormat32bppPARGB);
        Gdiplus::REAL matrix[6]{};
        {
            Gdiplus::Graphics g(&image); g.Clear(Gdiplus::Color(0, 0, 0, 0));
            t.PaintSurface(g, 104, 44, nullptr, false);
            Gdiplus::Matrix transform; g.GetTransform(&transform); transform.GetElements(matrix);
        }
        const float factor = s.dpi / 96.0f;
        check(std::abs(matrix[0] - factor) < .001f && std::abs(matrix[3] - factor) < .001f,
            "production surface applies one scale, not DPI squared");
        RECT body{s.width,s.height,0,0}, brand = body, mic = body;
        const auto include = [](RECT& r, int x, int y) {
            r.left = std::min(r.left, static_cast<LONG>(x)); r.top = std::min(r.top, static_cast<LONG>(y));
            r.right = std::max(r.right, static_cast<LONG>(x + 1)); r.bottom = std::max(r.bottom, static_cast<LONG>(y + 1));
        };
        for (int y = 0; y < s.height; ++y) for (int x = 0; x < s.width; ++x) {
            Gdiplus::Color color; image.GetPixel(x, y, &color);
            if (color.GetA() < 128) continue;
            include(body, x, y);
            if (color.GetR() < 90 && color.GetG() < 180 && color.GetB() > 220) include(brand, x, y);
            if (std::abs(static_cast<int>(color.GetR()) - 83) <= 2 && std::abs(static_cast<int>(color.GetG()) - 105) <= 2 &&
                std::abs(static_cast<int>(color.GetB()) - 145) <= 2) include(mic, x, y);
        }
        check(std::abs(body.right - body.left - s.bodyWidth) <= 2 && std::abs(body.bottom - body.top - s.bodyHeight) <= 2,
            "rasterized opaque body remains 96x36 DIPs");
        check(std::abs(brand.left - 14 * factor) <= 2 && std::abs(brand.top - 10 * factor) <= 2 &&
            std::abs(brand.right - brand.left - 24 * factor) <= 2 && std::abs(brand.bottom - brand.top - 24 * factor) <= 2 &&
            mic.left > brand.right && mic.right <= 83 * factor && mic.top >= 12 * factor && mic.bottom <= 34 * factor,
            "brand and microphone raster geometry scales independently");
        const int bLeft = MulDiv(4, s.dpi, 96), bRight = MulDiv(47, s.dpi, 96);
        const int mLeft = MulDiv(54, s.dpi, 96), mRight = MulDiv(100, s.dpi, 96);
        const int top = MulDiv(4, s.dpi, 96), bottom = MulDiv(40, s.dpi, 96);
        const auto at = [](int x, int y) { return MAKELPARAM(x, y); };
        check(t.BrandAt(at(bLeft, top)) && t.BrandAt(at(bRight - 1, bottom - 1)) &&
            !t.BrandAt(at(bLeft - 1, top)) && !t.BrandAt(at(bRight, top)) && !t.BrandAt(at(bLeft, bottom)), "brand hitbox exact independent pixel edges");
        check(t.MicAt(at(mLeft, top)) && t.MicAt(at(mRight - 1, bottom - 1)) &&
            !t.MicAt(at(mLeft - 1, top)) && !t.MicAt(at(mRight, top)) && !t.MicAt(at(mLeft, top - 1)) && !t.MicAt(at(mLeft, bottom)),
            "microphone hitbox exact independent pixel edges");
        check(t.Render(t.window_, 104, 44) && ClientSize(t.window_, {s.width, s.height}), "actual layered main HWND size at the requested render scale");
        const wchar_t* texts[] = {L"按下 F8 开始录音", L"按下 F8 停止录音", L"正在连接 · F8 停止", L"正在录音 · F8 停止", L"录音已停止", L"语音连接失败，请按 F8 重试"};
        for (unsigned i = 0; i < 6; ++i) {
            const bool status = i >= 2; const SIZE logical = t.HintSize(texts[i], status);
            Gdiplus::Bitmap hint(MulDiv(logical.cx, s.dpi, 96), MulDiv(logical.cy, s.dpi, 96), PixelFormat32bppPARGB);
            Gdiplus::Graphics g(&hint); t.PaintSurface(g, logical.cx, logical.cy, texts[i], status);
            Gdiplus::Font font(L"Microsoft YaHei UI", 12, Gdiplus::FontStyleRegular, Gdiplus::UnitPixel);
            Gdiplus::StringFormat format(Gdiplus::StringFormatFlagsNoWrap);
            format.SetAlignment(Gdiplus::StringAlignmentCenter); format.SetLineAlignment(Gdiplus::StringAlignmentCenter);
            Gdiplus::RectF measured; int fitted = 0, lines = 0;
            const auto result = g.MeasureString(texts[i], -1, &font,
                Gdiplus::RectF(12, 4, static_cast<float>(logical.cx - 24), static_cast<float>(logical.cy - 8)), &format, &measured, &fitted, &lines);
            check(result == Gdiplus::Ok && fitted == static_cast<int>(wcslen(texts[i])) && lines == 1 &&
                measured.Width <= logical.cx - 24 && measured.Height <= logical.cy - 8, "actual font measurement fits every production hint on one line");
            HWND hwnd = status ? t.notice_ : t.tooltip_;
            check(t.Render(hwnd, logical.cx, logical.cy, texts[i], status) &&
                ClientSize(hwnd, {MulDiv(logical.cx, s.dpi, 96), MulDiv(logical.cy, s.dpi, 96)}), "actual tooltip/status HWND size, independently scaled");
            if (i == 0 || i == 2 || i == 5) std::printf("HINT dpi=%u kind=%u logical=%ldx%ld pixels=%dx%d measured=%.2fx%.2f fitted=%d/%zu\n",
                s.dpi, i, logical.cx, logical.cy, MulDiv(logical.cx, s.dpi, 96), MulDiv(logical.cy, s.dpi, 96), measured.Width, measured.Height, fitted, wcslen(texts[i]));
        }
        const POINT low = t.ClampToWorkArea({-5000,-5000}, size, negativeWork), high = t.ClampToWorkArea({100,100}, size, negativeWork);
        check(low.x == -1920 && low.y == -1080 && high.x == -s.width && high.y == -s.height, "negative-coordinate work area clamp (pure geometry, not another monitor)");
        SendMessageW(t.window_, WM_LBUTTONDOWN, MK_LBUTTON, at(MulDiv(20, s.dpi, 96), MulDiv(20, s.dpi, 96)));
        const POINT drag = t.DragPosition({1000,500}, 192);
        check(t.dragging_ && std::abs(t.dragOffset_.x - 20) < .01f && drag.x == 960 && drag.y == 460,
            "drag grip stored in DIPs preserves its relative point across DPI");
        SendMessageW(t.window_, WM_LBUTTONUP, 0, at(bLeft, top));
        std::printf("SCALE dpi=%u surface=%dx%d opaque=%ldx%ld brand=%ldx%ld mic=[%ld,%ld,%ld,%ld]\n",
            s.dpi, s.width, s.height, body.right-body.left, body.bottom-body.top, brand.right-brand.left, brand.bottom-brand.top,
            mic.left, mic.top, mic.right, mic.bottom);
    }
    t.dpi_ = nativeDpi; t.Place(testOrigin);
    RECT current{}; GetWindowRect(t.window_, &current);
    for (const auto& s : scales) {
        RECT suggested{testOrigin.x + 10, testOrigin.y + 10, testOrigin.x + 10 + s.width, testOrigin.y + 10 + s.height};
        t.placing_ = true;
        SendMessageW(t.window_, WM_DPICHANGED, MAKELONG(s.dpi, s.dpi), reinterpret_cast<LPARAM>(&suggested));
        check(t.dpiPending_ && t.dpi_ == s.dpi && EqualRect(&t.dpiSuggested_, &suggested), "placing reentry retains the entire suggested rect/new DPI");
        for (HWND hwnd : {t.window_, t.tooltip_, t.notice_}) {
            SendMessageW(hwnd, WM_DPICHANGED, MAKELONG(120,120), 0);
            SendMessageW(hwnd, WM_DPICHANGED, MAKELONG(0,120), reinterpret_cast<LPARAM>(&suggested));
            SendMessageW(hwnd, WM_DPICHANGED, MAKELONG(120,0), reinterpret_cast<LPARAM>(&suggested));
        }
        check(t.dpi_ == s.dpi && t.dpiPending_ && EqualRect(&t.dpiSuggested_, &suggested), "null rect/zero-axis DPI ignored without state corruption");
        t.placing_ = false;
        PositionTrace observation; observation.suggested = suggested; trace = &observation;
        observation.previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(t.window_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(TracePosition)));
        t.Place(testOrigin);
        SetWindowLongPtrW(t.window_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(observation.previous)); trace = nullptr;
        GetWindowRect(t.window_, &current);
        check(observation.sawSuggested && current.left == suggested.left && current.top == suggested.top && !t.dpiPending_ &&
            t.dpi_ == ActualDpi(t.window_) && ClientSize(t.window_, {MulDiv(104,nativeDpi,96), MulDiv(44,nativeDpi,96)}),
            "native SetWindowPos applies suggestion then converges to actual GetDpiForWindow");
        // Also exercise real message dispatch without pre-setting placing_.
        SendMessageW(t.window_, WM_DPICHANGED, MAKELONG(s.dpi,s.dpi), reinterpret_cast<LPARAM>(&suggested));
        check(!t.placing_ && !t.dpiPending_ && t.dpi_ == ActualDpi(t.window_), "direct native DPI-message dispatch returns actual host DPI, not virtual DPI");
        std::printf("MESSAGE requested=%u actual=%u final-main=%ldx%ld suggested-applied=%d\n",
            s.dpi, ActualDpi(t.window_), current.right-current.left, current.bottom-current.top, observation.sawSuggested);
    }
    PositionTrace reentry; reentry.suggested = {testOrigin.x,testOrigin.y,testOrigin.x+208,testOrigin.y+88};
    reentry.dpi = 192; reentry.inject = true; trace = &reentry;
    reentry.previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(t.window_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(TracePosition)));
    t.Place({testOrigin.x+5,testOrigin.y+5});
    SetWindowLongPtrW(t.window_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(reentry.previous)); trace = nullptr;
    check(!reentry.inject && reentry.sawSuggested && !t.dpiPending_ && !t.placing_, "synchronous WM_DPICHANGED inside actual Place is consumed, not dropped");
    t.hovered_ = true; t.noticeText_ = L"正在连接 · F8 停止"; t.RenderHints();
    for (HWND hwnd : {t.tooltip_, t.notice_}) {
        SetWindowPos(hwnd, nullptr, 0, 0, 1, 1, SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOZORDER);
        SendMessageW(hwnd, WM_DPICHANGED, MAKELONG(192,192), reinterpret_cast<LPARAM>(&reentry.suggested));
        const SIZE logical = t.HintSize(hwnd == t.tooltip_ ? t.TooltipText() : t.noticeText_.c_str(), hwnd == t.notice_);
        check(ClientSize(hwnd, {MulDiv(logical.cx,nativeDpi,96), MulDiv(logical.cy,nativeDpi,96)}) && t.dpi_ == nativeDpi,
            "hint DPI message relayout uses main DPI; no independent double scale");
    }
    const HMONITOR a = reinterpret_cast<HMONITOR>(1), b = reinterpret_cast<HMONITOR>(2);
    check(t.FollowEditor(b,a,true) && !t.FollowEditor(b,a,false) && !t.FollowEditor(a,a,true),
        "different active editor/monitor follows once, same-monitor session position is retained (pure policy)");
    t.dragging_ = true; check(!t.FollowEditor(b,a,true), "active drag is not undone by editor polling"); t.dragging_ = false;
    VoiceToolbar editorA, editorB;
    check(editorA.Create(t.instance_, nullptr, nullptr) && editorB.Create(t.instance_, nullptr, nullptr), "own editor-window fixtures created without TSF/host");
    RECT area{}; GetWindowRect(t.window_, &area);
    t.SetEligible(true, editorA.Handle(), &area); const POINT retained = t.position_;
    t.dpi_ = nativeDpi == 192 ? 96 : 192; // stale cache only, not a production injection API.
    t.SetEligible(true, editorB.Handle(), &area);
    for (int i = 0; i < 3; ++i) t.SetEligible(true, editorB.Handle(), &area);
    check(t.position_.x == retained.x && t.position_.y == retained.y && t.dpi_ == ActualDpi(t.window_),
        "same-monitor editor switch/polls retain drag location and repair stale DPI cache");
    // Bounds of real main/hints are checked against the actual main monitor.
    t.Place({100000,100000}); MONITORINFO work{sizeof(work)};
    GetMonitorInfoW(MonitorFromWindow(t.window_,MONITOR_DEFAULTTONEAREST), &work);
    for (HWND hwnd : {t.window_,t.tooltip_,t.notice_}) {
        RECT r{}; GetWindowRect(hwnd,&r);
        check(r.left >= work.rcWork.left && r.top >= work.rcWork.top && r.right <= work.rcWork.right && r.bottom <= work.rcWork.bottom &&
            MonitorFromWindow(hwnd,MONITOR_DEFAULTTONEAREST) == MonitorFromWindow(t.window_,MONITOR_DEFAULTTONEAREST), "real main/hint bounds stay within the same monitor work area");
    }
    t.state_ = state; t.hovered_ = hovered; t.noticeText_ = notice; t.Place(original);
    t.editor_ = oldEditor; t.editorMonitor_ = oldMonitor; t.editorDpi_ = oldEditorDpi;
    std::puts("LIMIT cross-physical-monitor drag/DPI transition not exercised; no display settings changed");
}
int wmain(int argc, wchar_t** argv) {
    bool preview = argc == 2 && wcscmp(argv[1], L"--preview") == 0;
    const bool unaware = argc == 2 && wcscmp(argv[1], L"--dpi-unaware") == 0;
    const bool system = argc == 2 && wcscmp(argv[1], L"--dpi-system") == 0;
    std::wstring output;
    if (argc == 3 && wcscmp(argv[1], L"--screenshots") == 0) output = argv[2];
    else if (argc != 1 && !preview && !unaware && !system) {
        std::puts("usage: VoiceToolbarTests [--screenshots <absolute existing directory> | --preview | --dpi-unaware | --dpi-system]"); return 2;
    }
    const DWORD attributes = output.empty() ? 0 : GetFileAttributesW(output.c_str());
    if (!output.empty() && (output.size() < 3 || output[1] != L':' || attributes == INVALID_FILE_ATTRIBUTES ||
        !(attributes & FILE_ATTRIBUTE_DIRECTORY))) return 2;
    // EXE-only DPI awareness; the injected TSF DLL never changes host/thread DPI policy.
    using SetDpi = BOOL (WINAPI*)(HANDLE);
    const auto setDpi = reinterpret_cast<SetDpi>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetProcessDpiAwarenessContext"));
    const INT_PTR context = unaware ? -1 : system ? -2 : -4;
    const BOOL dpiSet = setDpi ? setDpi(reinterpret_cast<HANDLE>(context)) : FALSE;
    std::printf("EXE-DPI requested=%s set=%d; process-local only\n", unaware ? "unaware" : system ? "system" : "PMv2", dpiSet);
    unsigned failures = 0;
    auto check = [&](bool ok, const char* name) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failures; };
    const HINSTANCE instance = GetModuleHandleW(nullptr);
    // Warm the OS font/graphics caches before checking the toolbar's own resources.
    { VoiceToolbar warm; check(warm.Create(instance, nullptr, nullptr), "warmup native creation"); }
    const DWORD gdi = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS), user = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
    const HWND front = GetForegroundWindow(), active = GetActiveWindow(), focus = GetFocus();
    VoiceToolbar toolbar; Harness harness{&toolbar};
    if (!toolbar.Create(instance, Toggle, &harness)) { check(false, "toolbar creation"); return 1; }
    check(dpiSet && Awareness(toolbar.Handle()) == (unaware ? 0 : system ? 1 : 2), "test EXE actually uses its requested DPI-awareness mode");
    const HWND window = toolbar.Handle(), tip = VoiceToolbarTestAccess::Tooltip(toolbar), notice = VoiceToolbarTestAccess::Notice(toolbar);
    const auto className = VoiceToolbarTestAccess::ClassName(toolbar);
    check(!IsWindowVisible(window), "created toolbar remains hidden until selected/foreground eligibility");
    check(ClientSize(window, {MulDiv(104,ActualDpi(window),96), MulDiv(44,ActualDpi(window),96)}),
        "hidden creation already has the actual DPI-scaled size before eligibility");
    HICON largeIcon{}, smallIcon{}; wchar_t executable[32768]{}; GetModuleFileNameW(instance, executable, 32768);
    SetLastError(ERROR_SUCCESS);
    const UINT extracted = ExtractIconExW(executable, 0, &largeIcon, &smallIcon, 1);
    const DWORD iconError = GetLastError();
    const UINT groups = ExtractIconExW(executable, -1, nullptr, nullptr, 0);
    std::printf("ICON first-index=0 requested-groups=1 extracted=%u groups=%u large=%d small=%d last-error=%lu\n",
        extracted, groups, largeIcon != nullptr, smallIcon != nullptr, iconError);
    // Both requested sizes must actually exist. The API returns extracted icons,
    // not necessarily the input group count (one group can yield two handles).
    check(groups == 1 && extracted > 0 && extracted != UINT_MAX && largeIcon && smallIcon,
        "embedded first group ICO available at iconIndex=0 in both shell sizes");
    if (largeIcon) DestroyIcon(largeIcon); if (smallIcon) DestroyIcon(smallIcon);
    MONITORINFO monitor{sizeof(monitor)}; GetMonitorInfoW(MonitorFromWindow(front, MONITOR_DEFAULTTOPRIMARY), &monitor);
    RECT editor{monitor.rcWork.left + 120, monitor.rcWork.top + 120, monitor.rcWork.left + 580, monitor.rcWork.top + 390};
    toolbar.SetEligible(true, nullptr, &editor);
    check(IsWindowVisible(window) && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Idle &&
        wcscmp(toolbar.TooltipText(), L"按下 F8 开始录音") == 0, "selected idle has start tooltip, without a recording session");
    RECT rect{}; GetClientRect(window, &rect);
    std::printf("SIZE client=%ldx%ld body=96x36DIP plus 4DIP alpha-shadow margin\n", rect.right, rect.bottom);
    for (HWND hwnd : {window, tip, notice}) {
        const LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        check((ex & (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) == (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) &&
            SendMessageW(hwnd, WM_MOUSEACTIVATE, 0, MAKELPARAM(HTCLIENT, WM_LBUTTONDOWN)) == MA_NOACTIVATE,
            "toolbar and both tooltip/status windows are nonactivating tool windows");
    }
    const auto save = [&](const wchar_t* name, UINT dpi = 96) {
        if (!output.empty()) check(VoiceToolbarTestAccess::Save(toolbar, output + L"\\" + name, dpi), "state image written with actual production renderer");
    };
    save(L"01_idle.png");
    SendMessageW(window, WM_MOUSEMOVE, 0, VoiceToolbarTestAccess::Point(toolbar, 74, 22));
    SendMessageW(window, WM_TIMER, 2, 0);
    wchar_t text[128]{}; GetWindowTextW(tip, text, 128);
    check(VoiceToolbarTestAccess::Hovered(toolbar) && IsWindowVisible(tip) && wcscmp(text, L"按下 F8 开始录音") == 0,
        "mic hover highlights and shows the real Unicode start tooltip");
    save(L"02_hover.png");
    Click(toolbar);
    check(harness.toggles == 1 && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Connecting &&
        VoiceToolbarTestAccess::NoticeText(toolbar) == L"正在连接 · F8 停止" && wcscmp(toolbar.TooltipText(), L"按下 F8 停止录音") == 0,
        "mic click reaches shared toggle and connecting stop state");
    save(L"03_connecting.png");
    Click(toolbar);
    check(harness.toggles == 2 && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Idle && IsWindowVisible(window),
        "click stops connecting synchronously and keeps idle toolbar visible");
    Click(toolbar); toolbar.SetState(VoiceToolbarState::Recording);
    const unsigned animation = VoiceToolbarTestAccess::Animation(toolbar);
    SendMessageW(window, WM_TIMER, 1, 0);
    SendMessageW(window, WM_TIMER, 3, 0); // Queued old idle-toast message must not clear the new recording hint.
    check(VoiceToolbarTestAccess::Animation(toolbar) == animation + 1 && VoiceToolbarTestAccess::NoticeText(toolbar) == L"正在录音 · F8 停止",
        "recording animation is presentation-only; stale toast cannot clear recording status");
    save(L"04_recording.png"); save(L"04_recording_125pct.png", 120);
    if (!output.empty()) check(VoiceToolbarTestAccess::CaptureWindow(toolbar, output + L"\\04_recording-window.png"), "one actual own-toolbar window screenshot");
    Click(toolbar);
    check(harness.toggles == 4 && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Idle &&
        VoiceToolbarTestAccess::NoticeText(toolbar) == L"录音已停止" && IsWindowVisible(window), "stop returns to idle with short stopped toast");
    SendMessageW(window, WM_MOUSELEAVE, 0, 0); save(L"05_stopped.png");
    SendMessageW(window, WM_TIMER, 3, 0);
    check(!IsWindowVisible(notice) && IsWindowVisible(window), "toast expiry never permanently hides the toolbar");
    RECT before{}, after{}; GetWindowRect(window, &before);
    SendMessageW(window, WM_LBUTTONDOWN, MK_LBUTTON, VoiceToolbarTestAccess::Point(toolbar, 20, 20));
    SendMessageW(window, WM_MOUSEMOVE, MK_LBUTTON, VoiceToolbarTestAccess::Point(toolbar, 44, 34));
    SendMessageW(window, WM_LBUTTONUP, 0, VoiceToolbarTestAccess::Point(toolbar, 20, 20));
    GetWindowRect(window, &after);
    check(after.left > before.left && after.top > before.top && harness.toggles == 4, "brand drag moves toolbar without a third button or toggling recording");
    toolbar.SetEligible(false);
    Click(toolbar); SendMessageW(window, WM_TIMER, 2, 0);
    check(!IsWindowVisible(window) && !IsWindowVisible(tip) && !IsWindowVisible(notice) && harness.toggles == 4,
        "ineligible toolbar/tooltip/status hide and ignore click/late hover timer");
    toolbar.SetEligible(true, nullptr, &editor); GetWindowRect(window, &before);
    check(before.left == after.left && before.top == after.top, "drag position retained only for this component session");
    VoiceToolbarTestAccess::Place(toolbar, {100000, 100000}); GetWindowRect(window, &after);
    GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &monitor);
    check(after.left >= monitor.rcWork.left && after.top >= monitor.rcWork.top && after.right <= monitor.rcWork.right && after.bottom <= monitor.rcWork.bottom,
        "placement clamped to monitor working area");
    VoiceToolbarTestAccess::DpiChecks(toolbar, failures);
    check(GetForegroundWindow() == front && GetActiveWindow() == active && GetFocus() == focus,
        "show/hover/click/drag/animation leave foreground, active window and keyboard focus unchanged");
    if (preview) {
        VoiceToolbarTestAccess::Place(toolbar, {editor.left + 20, editor.bottom - 80});
        std::puts("PREVIEW 10 seconds, simulated UI states only; no registration/audio/cloud; no focus requests");
        const ULONGLONG start = GetTickCount64(); int previous = -1;
        while (GetTickCount64() - start < 10000) {
            const int phase = static_cast<int>((GetTickCount64() - start) / 2000);
            if (phase != previous) {
                previous = phase; SendMessageW(window, WM_MOUSELEAVE, 0, 0);
                toolbar.SetState(phase == 2 ? VoiceToolbarState::Connecting : phase == 3 ? VoiceToolbarState::Recording : VoiceToolbarState::Idle, phase == 4);
                if (phase == 1 || phase == 3) {
                    SendMessageW(window, WM_MOUSEMOVE, 0, VoiceToolbarTestAccess::Point(toolbar, 74, 22));
                    SendMessageW(window, WM_TIMER, 2, 0);
                }
            }
            Pump(); MsgWaitForMultipleObjects(0, nullptr, FALSE, 50, QS_ALLINPUT);
        }
    }
    toolbar.Destroy(); Pump();
    WNDCLASSEXW cls{sizeof(cls)};
    check(!IsWindow(window) && !IsWindow(tip) && !IsWindow(notice) && !GetClassInfoExW(instance, className.c_str(), &cls),
        "destroy releases every owned window, timer and registered class");
    std::printf("RESOURCES gdi-before=%lu after=%lu user-before=%lu after=%lu\n", gdi, GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS), user, GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS));
    check(GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS) <= gdi && GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS) <= user,
        "fonts, bitmaps, DCs, icons and windows do not retain native GUI resources");
    std::printf("FINAL RESULT failures=%u; actual toolbar only; no TSF/config/registration/microphone/cloud\n", failures);
    return failures ? 1 : 0;
}