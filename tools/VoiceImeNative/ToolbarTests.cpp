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
ULONGLONG WaitVisible(HWND window, bool visible, ULONGLONG limit) {
    const ULONGLONG start = GetTickCount64();
    while ((IsWindowVisible(window) != FALSE) != visible && GetTickCount64() - start < limit) {
        Pump();
        if ((IsWindowVisible(window) != FALSE) == visible) break;
        MsgWaitForMultipleObjects(0, nullptr, FALSE, 25, QS_ALLINPUT);
    }
    return GetTickCount64() - start;
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
    static LONG TailPixel(const VoiceToolbar& t) { return static_cast<LONG>(std::lround(t.tooltipTail_ * t.dpi_ / 96.0f)); }
    static void Place(VoiceToolbar& t, POINT point) { t.Place(point); }
    static void DpiChecks(VoiceToolbar& t, unsigned& failures);
    static void RecordingChecks(VoiceToolbar& t, unsigned& failures);
    static void HistoryChecks(HINSTANCE instance, const RECT& editor, unsigned& failures);
    static bool CaptureWindow(const VoiceToolbar& t, const std::wstring& path, bool hover = false) {
        CLSID encoder{}; if (!PngEncoder(encoder)) return false;
        RECT rect{}; if (!GetWindowRect(t.window_, &rect)) return false;
        const HWND hint = hover ? t.tooltip_ : t.notice_;
        RECT attached{};
        if (!IsWindowVisible(t.window_) || !IsWindowVisible(hint) || !GetWindowRect(hint, &attached)) return false;
        std::printf("CAPTURE %s main=%ldx%ld hint-surface=%ldx%ld dpi=%u\n", hover ? "hover" : "recording",
            rect.right - rect.left, rect.bottom - rect.top, attached.right - attached.left, attached.bottom - attached.top, t.dpi_);
        UnionRect(&rect, &rect, &attached); // Actual own HWNDs, including the chip/tail, not a synthetic composition.
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
    HWND main{};
    bool orphanHint = false;
};
PositionTrace* trace{}; // Own test HWND only; never subclasses the host/editor.
LRESULT CALLBACK TracePosition(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_WINDOWPOSCHANGING) {
        const auto& pos = *reinterpret_cast<WINDOWPOS*>(l);
        if (trace->main && (pos.flags & SWP_SHOWWINDOW) && !IsWindowVisible(trace->main)) trace->orphanHint = true;
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
struct Harness {
    VoiceToolbar* toolbar; unsigned toggles = 0; unsigned settingsClicks = 0; bool destroyOnToggle = false;
    unsigned historyClicks = 0; bool lastHistory = false, destroyOnHistory = false;
    unsigned usageClicks = 0, languageClicks = 0; bool destroyOnNavigation = false;
};
void Toggle(void* owner) {
    auto& h = *static_cast<Harness*>(owner); ++h.toggles;
    if (h.destroyOnToggle) { h.toolbar->Destroy(); return; }
    const bool stopping = VoiceToolbarTestAccess::State(*h.toolbar) != VoiceToolbarState::Idle;
    h.toolbar->SetState(stopping ? VoiceToolbarState::Idle : VoiceToolbarState::Connecting, stopping);
}
void OpenSettings(void* owner) {
    auto& h = *static_cast<Harness*>(owner); ++h.settingsClicks;
}
void History(void* owner, bool enabled) {
    auto& h = *static_cast<Harness*>(owner); ++h.historyClicks; h.lastHistory = enabled;
    if (h.destroyOnHistory) h.toolbar->Destroy();
}
void OpenUsage(void* owner) {
    auto& h = *static_cast<Harness*>(owner); ++h.usageClicks;
    if (h.destroyOnNavigation) h.toolbar->Destroy();
}
void OpenLanguage(void* owner) {
    auto& h = *static_cast<Harness*>(owner); ++h.languageClicks;
    if (h.destroyOnNavigation) h.toolbar->Destroy();
}
void ClickAt(VoiceToolbar& t, int x, int y = 22) {
    const auto point = VoiceToolbarTestAccess::Point(t, x, y);
    SendMessageW(t.Handle(), WM_LBUTTONDOWN, MK_LBUTTON, point);
    SendMessageW(t.Handle(), WM_LBUTTONUP, 0, point);
}
void Click(VoiceToolbar& t) {
    const auto point = VoiceToolbarTestAccess::Point(t, 74, 22);
    SendMessageW(t.Handle(), WM_LBUTTONDOWN, MK_LBUTTON, point);
    SendMessageW(t.Handle(), WM_LBUTTONUP, 0, point);
}
void ClickGear(VoiceToolbar& t) {
    const auto point = VoiceToolbarTestAccess::Point(t, 240, 22);
    SendMessageW(t.Handle(), WM_LBUTTONDOWN, MK_LBUTTON, point);
    SendMessageW(t.Handle(), WM_LBUTTONUP, 0, point);
}
}
void VoiceToolbarTestAccess::DpiChecks(VoiceToolbar& t, unsigned& failures) {
    const auto check = [&](bool ok, const char* name) { std::printf("%s DPI %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failures; };
    const UINT nativeDpi = ActualDpi(t.window_);
    const POINT original = t.position_;
    const auto state = t.state_; const bool hovered = t.hovered_, gearHovered = t.gearHovered_;
    const std::wstring notice = t.noticeText_;
    const bool noticeError = t.noticeError_, tooltipReady = t.tooltipReady_; const float tooltipTail = t.tooltipTail_;
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
    const Scale scales[] = {{96,264,44,256,36}, {120,330,55,320,45}, {144,396,66,384,54}, {192,528,88,512,72}};
    const RECT negativeWork{-1920, -1080, 0, 0};
    t.state_ = VoiceToolbarState::Idle; t.hovered_ = t.gearHovered_ = false; t.tooltipTail_ = 0;
    SIZE logicalHints[5]{};
    for (const auto& s : scales) {
        t.dpi_ = s.dpi;
        const SIZE size = t.ScaledSize(264, 44, s.dpi);
        check(size.cx == s.width && size.cy == s.height, "100/125/150/200 percent pure pixel size");
        Gdiplus::Bitmap image(s.width, s.height, PixelFormat32bppPARGB);
        Gdiplus::REAL matrix[6]{};
        {
            Gdiplus::Graphics g(&image); g.Clear(Gdiplus::Color(0, 0, 0, 0));
            t.PaintSurface(g, 264, 44, nullptr, false);
            Gdiplus::Matrix transform; g.GetTransform(&transform); transform.GetElements(matrix);
        }
        const float factor = s.dpi / 96.0f;
        check(std::abs(matrix[0] - factor) < .001f && std::abs(matrix[3] - factor) < .001f,
            "production surface applies one scale, not DPI squared");
        RECT body{s.width,s.height,0,0}, brand = body, mic = body, language = body;
        unsigned languageInk = 0;
        const auto include = [](RECT& r, int x, int y) {
            r.left = std::min(r.left, static_cast<LONG>(x)); r.top = std::min(r.top, static_cast<LONG>(y));
            r.right = std::max(r.right, static_cast<LONG>(x + 1)); r.bottom = std::max(r.bottom, static_cast<LONG>(y + 1));
        };
        for (int y = 0; y < s.height; ++y) for (int x = 0; x < s.width; ++x) {
            Gdiplus::Color color; image.GetPixel(x, y, &color);
            if (color.GetA() < 128) continue;
            include(body, x, y);
            if (color.GetR() < 90 && color.GetG() < 180 && color.GetB() > 220) include(brand, x, y);
            if (x < 96 * factor && std::abs(static_cast<int>(color.GetR()) - 83) <= 2 && std::abs(static_cast<int>(color.GetG()) - 105) <= 2 &&
                std::abs(static_cast<int>(color.GetB()) - 145) <= 2) include(mic, x, y);
            if (x >= 188 * factor && x < 212 * factor && y >= 10 * factor && y < 34 * factor &&
                color.GetR() >= 83 && color.GetR() < 170 && color.GetG() >= 105 && color.GetG() < 180 &&
                color.GetB() >= 145 && color.GetB() < 220) { include(language, x, y); ++languageInk; }
        }
        check(std::abs(body.right - body.left - s.bodyWidth) <= 2 && std::abs(body.bottom - body.top - s.bodyHeight) <= 2,
            "rasterized opaque body remains 256x36 DIPs");
        check(languageInk > 30 * factor * factor && language.right - language.left >= 12 * factor &&
            language.bottom - language.top >= 12 * factor,
            "language glyph has substantial legible ink within its 24-DIP icon at all four scales");
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
        check(t.HistoryAt(at(MulDiv(100,s.dpi,96),top)) && !t.MicAt(at(MulDiv(100,s.dpi,96),top)) &&
            t.HistoryAt(at(MulDiv(140,s.dpi,96)-1,bottom-1)) && !t.HistoryAt(at(MulDiv(140,s.dpi,96),top)) &&
            t.UsageAt(at(MulDiv(140,s.dpi,96),top)) && t.UsageAt(at(MulDiv(180,s.dpi,96)-1,bottom-1)) &&
            !t.UsageAt(at(MulDiv(180,s.dpi,96),top)) && t.LanguageAt(at(MulDiv(180,s.dpi,96),top)) &&
            t.LanguageAt(at(MulDiv(220,s.dpi,96)-1,bottom-1)) && !t.LanguageAt(at(MulDiv(220,s.dpi,96),top)) &&
            t.GearAt(at(MulDiv(220,s.dpi,96),top)) && t.GearAt(at(MulDiv(260,s.dpi,96)-1,bottom-1)) &&
            !t.GearAt(at(MulDiv(260,s.dpi,96),top)) && !t.LanguageAt(at(MulDiv(180,s.dpi,96)-1,top)) &&
            !t.HistoryAt(at(MulDiv(120,s.dpi,96),bottom)) && !t.UsageAt(at(MulDiv(160,s.dpi,96),top-1)) &&
            !t.LanguageAt(at(MulDiv(200,s.dpi,96),bottom)) && !t.LanguageAt(at(MulDiv(200,s.dpi,96),top-1)),
            "four right buttons have independent 40x36 DIP hitboxes with no microphone overlap");
        check(t.Render(t.window_, 264, 44) && ClientSize(t.window_, {s.width, s.height}), "actual layered main HWND size at the requested render scale");
        const wchar_t* texts[] = {L"打开输入法设置", L"正在连接", L"开始录音", L"已停止", L"语音连接失败，请按 F8 重试"};
        for (unsigned i = 0; i < 5; ++i) {
            const bool status = i >= 1; const SIZE logical = t.HintSize(texts[i], status);
            if (s.dpi == 96) logicalHints[i] = logical;
            check(logical.cx == logicalHints[i].cx && logical.cy == logicalHints[i].cy,
                "hint logical measurement stays identical at all four scales");
            const int bodyHeight = logical.cy - 8 - (status ? 0 : 5), inset = status ? 12 : 14;
            if (status) check(bodyHeight == 22 && (i == 4 ? logical.cx - 8 < 180 : logical.cx - 8 <= 124) &&
                (i != 3 || logical.cx - 8 < 96), "22-DIP chip body; short notices below old 124x30, full error below old 188px surface");
            t.state_ = i == 1 ? VoiceToolbarState::Connecting : i == 2 ? VoiceToolbarState::Recording : VoiceToolbarState::Idle;
            t.noticeError_ = i == 4;
            Gdiplus::Bitmap hint(MulDiv(logical.cx, s.dpi, 96), MulDiv(logical.cy, s.dpi, 96), PixelFormat32bppPARGB);
            Gdiplus::Graphics g(&hint); t.PaintSurface(g, logical.cx, logical.cy, texts[i], status); g.Flush(Gdiplus::FlushIntentionSync);
            Gdiplus::Font font(L"Microsoft YaHei UI", status ? 10.0f : 12.0f, Gdiplus::FontStyleRegular, Gdiplus::UnitPixel);
            Gdiplus::StringFormat format(Gdiplus::StringFormatFlagsNoWrap);
            format.SetTrimming(Gdiplus::StringTrimmingNone);
            format.SetAlignment(Gdiplus::StringAlignmentCenter); format.SetLineAlignment(Gdiplus::StringAlignmentCenter);
            Gdiplus::RectF measured; int fitted = 0, lines = 0;
            const auto result = g.MeasureString(texts[i], -1, &font,
                Gdiplus::RectF(static_cast<float>(inset), 4, static_cast<float>(logical.cx - inset * 2), static_cast<float>(bodyHeight)), &format, &measured, &fitted, &lines);
            check(result == Gdiplus::Ok && fitted == static_cast<int>(wcslen(texts[i])) && lines == 1 &&
                measured.Width <= logical.cx - inset * 2 && measured.Height <= bodyHeight, "actual 12/10-DIP font fits every full tooltip/status text on one line");
            if (i == 0 || i == 2) {
                Gdiplus::BitmapData data{}; Gdiplus::Rect pixels(0, 0, static_cast<INT>(hint.GetWidth()), static_cast<INT>(hint.GetHeight()));
                const bool locked = hint.LockBits(&pixels, Gdiplus::ImageLockModeRead, PixelFormat32bppPARGB, &data) == Gdiplus::Ok;
                bool premultiplied = locked, antialiased = false;
                RECT opaque{pixels.Width, pixels.Height, 0, 0};
                if (locked) {
                    for (int y = 0; y < pixels.Height; ++y) for (int x = 0; x < pixels.Width; ++x) {
                        const BYTE* p = static_cast<const BYTE*>(data.Scan0) + y * data.Stride + x * 4;
                        premultiplied = premultiplied && p[0] <= p[3] && p[1] <= p[3] && p[2] <= p[3];
                        antialiased = antialiased || (p[3] > 0 && p[3] < 255);
                        if (p[3] >= 128) include(opaque, x, y);
                    }
                    hint.UnlockBits(&data);
                }
                Gdiplus::Color corner, tailCenter, tailSide;
                hint.GetPixel(0, 0, &corner);
                hint.GetPixel(MulDiv(logical.cx / 2, s.dpi, 96), MulDiv(4 + bodyHeight + 2, s.dpi, 96), &tailCenter);
                hint.GetPixel(MulDiv(logical.cx / 2 + 8, s.dpi, 96), MulDiv(4 + bodyHeight + 2, s.dpi, 96), &tailSide);
                check(premultiplied && antialiased && corner.GetA() == 0 &&
                    (status || (tailCenter.GetA() >= 128 && tailSide.GetA() < 128)),
                    "premultiplied antialiased alpha; tooltip has one small downward tail outside text/body");
                if (status) check(std::abs(opaque.right - opaque.left - (logical.cx - 8) * factor) <= 2 &&
                    std::abs(opaque.bottom - opaque.top - 22 * factor) <= 2, "rasterized recording chip body is actually 22 DIPs tall, not just a logical size");
            }
            HWND hwnd = status ? t.notice_ : t.tooltip_;
            check(t.Render(hwnd, logical.cx, logical.cy, texts[i], status) &&
                ClientSize(hwnd, {MulDiv(logical.cx, s.dpi, 96), MulDiv(logical.cy, s.dpi, 96)}), "actual tooltip/status HWND size, independently scaled");
            std::printf("HINT dpi=%u kind=%u body=%ldx%d logical=%ldx%ld pixels=%dx%d font=%d measured=%.2fx%.2f fitted=%d/%zu\n",
                s.dpi, i, logical.cx - 8, bodyHeight, logical.cx, logical.cy, MulDiv(logical.cx, s.dpi, 96), MulDiv(logical.cy, s.dpi, 96),
                status ? 10 : 12, measured.Width, measured.Height, fitted, wcslen(texts[i]));
        }
        t.state_ = VoiceToolbarState::Idle; t.noticeError_ = false;
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
            t.dpi_ == ActualDpi(t.window_) && ClientSize(t.window_, {MulDiv(264,nativeDpi,96), MulDiv(44,nativeDpi,96)}),
            "native SetWindowPos applies suggestion then converges to actual GetDpiForWindow");
        // Also exercise real message dispatch without pre-setting placing_.
        SendMessageW(t.window_, WM_DPICHANGED, MAKELONG(s.dpi,s.dpi), reinterpret_cast<LPARAM>(&suggested));
        check(!t.placing_ && !t.dpiPending_ && t.dpi_ == ActualDpi(t.window_), "direct native DPI-message dispatch returns actual host DPI, not virtual DPI");
        std::printf("MESSAGE requested=%u actual=%u final-main=%ldx%ld suggested-applied=%d\n",
            s.dpi, ActualDpi(t.window_), current.right-current.left, current.bottom-current.top, observation.sawSuggested);
    }
    PositionTrace reentry; reentry.suggested = {testOrigin.x,testOrigin.y,testOrigin.x+528,testOrigin.y+88};
    reentry.dpi = 192; reentry.inject = true; trace = &reentry;
    reentry.previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(t.window_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(TracePosition)));
    t.Place({testOrigin.x+5,testOrigin.y+5});
    SetWindowLongPtrW(t.window_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(reentry.previous)); trace = nullptr;
    check(!reentry.inject && reentry.sawSuggested && !t.dpiPending_ && !t.placing_, "synchronous WM_DPICHANGED inside actual Place is consumed, not dropped");
    t.gearHovered_ = true; t.noticeText_ = L"正在连接"; t.RenderHints();
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
    t.state_ = state; t.hovered_ = hovered; t.gearHovered_ = gearHovered; t.noticeText_ = notice; t.noticeError_ = noticeError;
    t.tooltipReady_ = tooltipReady; t.tooltipTail_ = tooltipTail; t.Place(original);
    t.editor_ = oldEditor; t.editorMonitor_ = oldMonitor; t.editorDpi_ = oldEditorDpi;
    std::puts("LIMIT cross-physical-monitor drag/DPI transition not exercised; no display settings changed");
}
void VoiceToolbarTestAccess::RecordingChecks(VoiceToolbar& t, unsigned& failures) {
    const auto check = [&](bool ok, const char* name) { std::printf("%s PIXELS %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failures; };
    struct Frame {
        std::vector<DWORD> pixels;
        unsigned red = 0, white = 0, gray = 0, halo = 0, leftWave = 0, rightWave = 0;
    };
    const SIZE size = t.ScaledSize(264, 44, t.dpi_);
    const float scale = t.dpi_ / 96.0f;
    const auto capture = [&]() {
        Frame frame;
        Gdiplus::Bitmap image(size.cx, size.cy, PixelFormat32bppPARGB);
        {
            Gdiplus::Graphics g(&image); g.Clear(Gdiplus::Color(0, 0, 0, 0));
            t.PaintSurface(g, 264, 44, nullptr, false); g.Flush(Gdiplus::FlushIntentionSync);
        }
        for (int y = 0; y < size.cy; ++y) for (int x = 0; x < size.cx; ++x) {
            Gdiplus::Color c; image.GetPixel(x, y, &c); frame.pixels.push_back(c.GetValue());
            const float dx = x / scale, dy = y / scale;
            if (dx < 54 || dx >= 98 || dy < 5 || dy >= 39 || c.GetA() < 128) continue;
            const bool red = c.GetR() >= 180 && c.GetG() >= 60 && c.GetG() < 180 &&
                c.GetR() > c.GetG() + 50 && c.GetR() > c.GetB() + 50;
            frame.red += red;
            frame.white += dx >= 70 && dx < 78 && dy >= 13 && dy < 24 &&
                c.GetR() > 245 && c.GetG() > 245 && c.GetB() > 245;
            frame.gray += c.GetR() == 83 && c.GetG() == 105 && c.GetB() == 145;
            const float distance = (dx - 74) * (dx - 74) + (dy - 22) * (dy - 22);
            frame.halo += distance > 169 && (dy < 14 || dy > 30) &&
                c.GetR() > c.GetG() + 15 && c.GetR() > c.GetB() + 15;
            frame.leftWave += red && dx < 61;
            frame.rightWave += red && dx >= 88;
        }
        return frame;
    };
    t.Hover(false); t.SetState(VoiceToolbarState::Recording);
    const Frame low = capture();
    for (int i = 0; i < 9; ++i) SendMessageW(t.window_, WM_TIMER, 1, 0);
    const Frame high = capture();
    unsigned changed = 0, waveChanged = 0, outsideChanged = 0;
    for (int y = 0; y < size.cy; ++y) for (int x = 0; x < size.cx; ++x) {
        const size_t index = static_cast<size_t>(y) * size.cx + x;
        if (low.pixels[index] == high.pixels[index]) continue;
        ++changed;
        const float dx = x / scale, dy = y / scale;
        waveChanged += ((dx >= 54 && dx < 61) || (dx >= 88 && dx < 98)) && dy >= 16 && dy < 28;
        outsideChanged += dx < 53 || dx >= 99;
    }
    check(low.red > 200 && high.red > 200 && low.white > 15 && high.white > 15,
        "production PARGB surface contains coral-red solid disc and white microphone");
    check(low.leftWave > 8 && low.rightWave > 8 && high.leftWave > 8 && high.rightWave > 8 && waveChanged > 8,
        "both small red sound waves are present and move between real animation timer frames");
    check(low.halo > 10 && high.halo > low.halo && changed > 100 && outsideChanged == 0,
        "soft outer red halo visibly breathes; animation stays inside the microphone region");
    t.SetState(VoiceToolbarState::Idle, true);
    const Frame idle = capture(); const unsigned stoppedFrame = t.animation_;
    for (int i = 0; i < 9; ++i) SendMessageW(t.window_, WM_TIMER, 1, 0);
    const Frame idleLater = capture();
    check(idle.red == 0 && idle.gray > 20 && idle.pixels == idleLater.pixels && t.animation_ == stoppedFrame,
        "stopped microphone is gray-blue with no red/waves; queued animation ticks leave identical pixels");
    std::printf("RASTER dpi=%u red=%u/%u white=%u/%u halo=%u/%u waves=%u,%u/%u,%u changed=%u wave-changed=%u outside-changed=%u stopped-gray=%u static=%d\n",
        t.dpi_, low.red, high.red, low.white, high.white, low.halo, high.halo, low.leftWave, low.rightWave,
        high.leftWave, high.rightWave, changed, waveChanged, outsideChanged, idle.gray, idle.pixels == idleLater.pixels);
}
void VoiceToolbarTestAccess::HistoryChecks(HINSTANCE instance, const RECT& editor, unsigned& failures) {
    const auto check = [&](bool ok, const char* name) { std::printf("%s HISTORY %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failures; };
    VoiceToolbar t; Harness h{&t};
    if (!t.Create(instance, Toggle, &h, OpenSettings, History, OpenUsage, OpenLanguage)) { check(false, "six-button fixture creation"); return; }
    t.SetEligible(true, nullptr, &editor);
    const HWND front = GetForegroundWindow(), active = GetActiveWindow(), focus = GetFocus(), confirm = t.confirmation_;
    const auto answer = [&](bool yes) {
        const LPARAM p = Point(t, yes ? 310 : 200, 190);
        SendMessageW(confirm, WM_LBUTTONDOWN, MK_LBUTTON, p); SendMessageW(confirm, WM_LBUTTONUP, 0, p);
    };
    const auto bluePixels = [&](UINT dpi) {
        const UINT old = t.dpi_; t.dpi_ = dpi;
        Gdiplus::Bitmap image(MulDiv(264,dpi,96), MulDiv(44,dpi,96), PixelFormat32bppPARGB);
        { Gdiplus::Graphics g(&image); g.Clear(Gdiplus::Color(0,0,0,0)); t.PaintSurface(g,264,44,nullptr,false); }
        unsigned blue = 0, white = 0, red = 0, gray = 0;
        for (int y = MulDiv(10,dpi,96); y < MulDiv(34,dpi,96); ++y)
            for (int x = MulDiv(108,dpi,96); x < MulDiv(132,dpi,96); ++x) {
                Gdiplus::Color c; image.GetPixel(x,y,&c);
                blue += c.GetB() > 220 && c.GetR() < 70 && c.GetG() < 160;
                white += c.GetR() > 245 && c.GetG() > 245 && c.GetB() > 245;
                red += c.GetR() > c.GetB() + 50;
                // Include antialiased gray-blue ink blended into the light panel,
                // not only fully covered (83,105,145) pixels on 18-DIP curves.
                gray += c.GetR() >= 83 && c.GetR() < 170 && c.GetG() >= 105 && c.GetG() < 180 &&
                    c.GetB() >= 145 && c.GetB() < 220;
            }
        t.dpi_ = old;
        std::printf("HISTORY-RASTER dpi=%u enabled=%d blue=%u white=%u red=%u gray=%u\n",dpi,t.historyEnabled_,blue,white,red,gray);
        check(red == 0 && (t.historyEnabled_ ? white > 20 && blue > 100 : gray > 20 && blue == 0), "feedback raster is blue/white only when enabled; otherwise gray-blue, never recording red");
        return blue;
    };
    const auto hover = [&](int x, const wchar_t* expected, const char* name) {
        SendMessageW(t.window_,WM_MOUSEMOVE,0,Point(t,x,22));
        t.tooltipDeadline_ = GetTickCount64(); SendMessageW(t.window_,WM_TIMER,2,0);
        wchar_t text[64]{}; GetWindowTextW(t.tooltip_,text,64);
        RECT r{}; GetWindowRect(t.tooltip_,&r);
        const SIZE logical = t.HintSize(expected,false);
        Gdiplus::Bitmap image(1,1,PixelFormat32bppPARGB); Gdiplus::Graphics g(&image);
        g.SetPageUnit(Gdiplus::UnitPixel);
        g.SetTextRenderingHint(Gdiplus::TextRenderingHintAntiAliasGridFit);
        Gdiplus::Font font(L"Microsoft YaHei UI",12,Gdiplus::FontStyleRegular,Gdiplus::UnitPixel);
        Gdiplus::StringFormat format(Gdiplus::StringFormatFlagsNoWrap);
        format.SetTrimming(Gdiplus::StringTrimmingNone);
        format.SetAlignment(Gdiplus::StringAlignmentCenter); format.SetLineAlignment(Gdiplus::StringAlignmentCenter);
        Gdiplus::RectF measured; int fitted=0, lines=0;
        const auto result = g.MeasureString(expected,-1,&font,
            Gdiplus::RectF(14,4,static_cast<float>(logical.cx-28),static_cast<float>(logical.cy-13)),
            &format,&measured,&fitted,&lines);
        check(IsWindowVisible(t.tooltip_) && wcscmp(text,expected) == 0 &&
            ClientSize(t.tooltip_,t.ScaledSize(logical.cx,logical.cy,t.dpi_)) &&
            std::abs(r.left + static_cast<LONG>(std::lround(t.tooltipTail_ * t.dpi_/96.0f)) - t.position_.x - t.Pixels(x)) <= 1 &&
            result == Gdiplus::Ok && fitted == static_cast<int>(wcslen(expected)) && lines == 1 &&
            measured.Width <= logical.cx-28 && measured.Height <= logical.cy-13, name);
        std::printf("HOVER-FIT x=%d dpi=%u logical=%ldx%ld measured=%.2fx%.2f fitted=%d/%zu lines=%d\n",
            x,t.dpi_,logical.cx,logical.cy,measured.Width,measured.Height,fitted,wcslen(expected),lines);
        t.tooltipDeadline_ = GetTickCount64(); SendMessageW(t.window_,WM_TIMER,2,0);
        SendMessageW(t.window_,WM_MOUSEMOVE,0,Point(t,x,22));
        check(!IsWindowVisible(t.tooltip_) && !t.tooltipDeadline_, "new button hover expires and stays hidden until the pointer leaves");
        t.Hover(false);
    };
    const auto fitConfirmation = [&]() {
        const UINT nativeDpi = t.dpi_;
        for (UINT dpi : {96u,120u,144u,192u}) {
            t.dpi_ = dpi;
            Gdiplus::Bitmap image(MulDiv(392,dpi,96),MulDiv(224,dpi,96),PixelFormat32bppPARGB);
            Gdiplus::Graphics g(&image); g.Clear(Gdiplus::Color(0,0,0,0));
            g.ScaleTransform(dpi/96.0f,dpi/96.0f); t.PaintConfirmation(g); g.Flush(Gdiplus::FlushIntentionSync);
            Gdiplus::Font title(L"Microsoft YaHei UI",16,Gdiplus::FontStyleBold,Gdiplus::UnitPixel);
            Gdiplus::Font text(L"Microsoft YaHei UI",12,Gdiplus::FontStyleRegular,Gdiplus::UnitPixel);
            Gdiplus::StringFormat wrap; wrap.SetTrimming(Gdiplus::StringTrimmingNone);
            Gdiplus::StringFormat centered(Gdiplus::StringFormatFlagsNoWrap);
            centered.SetTrimming(Gdiplus::StringTrimmingNone);
            centered.SetAlignment(Gdiplus::StringAlignmentCenter); centered.SetLineAlignment(Gdiplus::StringAlignmentCenter);
            Gdiplus::RectF bounds; int fitted=0, lines=0;
            const auto titleResult = g.MeasureString(t.ConfirmationTitle(),-1,&title,Gdiplus::PointF(20,20),&bounds);
            check(titleResult == Gdiplus::Ok && bounds.GetRight() <= 348 && bounds.GetBottom() <= 58,
                "both production titles fit before the close button and body at 100/125/150/200 percent");
            for (int slot : {0,1,2}) {
                const wchar_t* value = slot == 0 ? t.ConfirmationText() : t.ConfirmationButtonText(slot == 2);
                const Gdiplus::RectF layout = slot == 0 ? Gdiplus::RectF(20,58,352,104) :
                    Gdiplus::RectF(slot == 1 ? 152.0f : 268.0f,176,104,32);
                const auto result = g.MeasureString(value,-1,&text,layout,slot == 0 ? &wrap : &centered,&bounds,&fitted,&lines);
                check(result == Gdiplus::Ok && fitted == static_cast<int>(wcslen(value)) &&
                    bounds.Width <= layout.Width && bounds.Height <= layout.Height && (slot == 0 ? lines > 1 : lines == 1),
                    "full production confirmation body/buttons fit without trimming at all four render scales");
                std::printf("CONFIRM-FIT target=%d dpi=%u slot=%d measured=%.2fx%.2f fitted=%d/%zu lines=%d\n",
                    t.confirmationTarget_,dpi,slot,bounds.Width,bounds.Height,fitted,wcslen(value),lines);
            }
            t.historyHovered_ = true;
            const std::wstring tip = t.TooltipText(); t.historyHovered_ = false;
            const SIZE hint = t.HintSize(tip.c_str(),false);
            Gdiplus::Bitmap bubble(MulDiv(hint.cx,dpi,96),MulDiv(hint.cy,dpi,96),PixelFormat32bppPARGB);
            Gdiplus::Graphics tipGraphics(&bubble); t.PaintSurface(tipGraphics,hint.cx,hint.cy,tip.c_str(),false);
            const auto tipResult = tipGraphics.MeasureString(tip.c_str(),-1,&text,
                Gdiplus::RectF(14,4,static_cast<float>(hint.cx-28),static_cast<float>(hint.cy-13)),&centered,&bounds,&fitted,&lines);
            check(tipResult == Gdiplus::Ok && fitted == static_cast<int>(tip.size()) && lines == 1 &&
                bounds.Width <= hint.cx-28 && bounds.Height <= hint.cy-13,
                "enable/disable confirmation tooltip fits completely at all four render scales");
            std::printf("CONFIRM-TOOLTIP-FIT target=%d dpi=%u fitted=%d/%zu lines=%d\n",t.confirmationTarget_,dpi,fitted,tip.size(),lines);
            check(t.Render(confirm,392,224) && ClientSize(confirm,t.ScaledSize(392,224,dpi)) &&
                t.ConfirmationAt(Point(t,200,190)) == 1 && t.ConfirmationAt(Point(t,310,190)) == 2 &&
                t.ConfirmationAt(Point(t,360,30)) == 1,
                "actual confirmation HWND and cancel/accept/close hitboxes use the same render scale");
        }
        t.dpi_ = nativeDpi; t.PlaceConfirmation();
        wchar_t title[64]{}; GetWindowTextW(confirm,title,64);
        check(wcscmp(title,t.ConfirmationTitle()) == 0 && IsWindowVisible(confirm), "actual native window title matches the visible target-specific title");
    };
    check(!t.historyEnabled_ && !t.historyAvailable_ && !t.historyPending_ && !IsWindowVisible(confirm), "default off and unavailable until storage feedback");
    const unsigned offBlue = bluePixels(96);
    t.SetHistory(false,false);
    check(!t.historyPending_ && !t.historyEnabled_ && !t.historyAvailable_ && t.noticeText_.empty() && !IsWindowVisible(t.notice_),
        "initial unavailable feedback without a pending save stays silent and off");
    t.SetHistory(true,false);
    check(!t.historyEnabled_ && !t.historyAvailable_ && !t.historyPending_ && t.noticeText_.empty() && bluePixels(96) == offBlue,
        "enabled=true available=false cannot light the icon or announce success");
    ClickAt(t,120);
    check(h.historyClicks == 0 && h.toggles == 0 && !t.confirming_ && t.noticeError_ &&
        t.noticeText_ == L"字数与原文存储暂不可用，请重启输入法后台后重试", "unavailable click gives a clear storage error without confirm/recording/callback");
    t.SetHistory(false,true); ClickAt(t,120);
    check(t.confirming_ && t.confirmationTarget_ && IsWindowVisible(confirm) && h.historyClicks == 0 && !t.historyPending_,
        "enable snapshots true and opens actual native modeless confirmation without a callback or pending save");
    t.SetHistory(false,true);
    check(t.confirming_ && t.confirmationTarget_ && IsWindowVisible(confirm) && h.historyClicks == 0,
        "unchanged off feedback preserves the enable confirmation and its target snapshot");
    check((GetWindowLongPtrW(confirm,GWL_EXSTYLE) & WS_EX_NOACTIVATE) &&
        SendMessageW(confirm,WM_MOUSEACTIVATE,0,MAKELPARAM(HTCLIENT,WM_LBUTTONDOWN)) == MA_NOACTIVATE &&
        SendMessageW(confirm,WM_NCHITTEST,0,0) != HTTRANSPARENT, "confirmation receives clicks without activating or changing input target");
    check(wcscmp(t.ConfirmationTitle(),L"开启原文保存？") == 0 && wcscmp(t.ConfirmationButtonText(false),L"暂不开启") == 0 &&
        wcscmp(t.ConfirmationButtonText(true),L"确认开启") == 0 &&
        wcscmp(t.ConfirmationText(),L"开启后，之后的定稿原文将保存在本机数据库。能够访问数据库文件的人可能读到内容。关闭原文保存不会删除已有记录。字数、句数和录音时长统计不受此开关影响。") == 0,
        "enable title/buttons disclose local original-text privacy separately from counts and recording duration");
    fitConfirmation();
    Click(t);
    check(h.toggles == 0 && t.state_ == VoiceToolbarState::Idle, "confirmation cannot start a new recording");
    answer(false);
    check(!t.confirming_ && !IsWindowVisible(confirm) && h.historyClicks == 0, "normal cancel never sends enable");
    for (UINT message : {WM_CLOSE, WM_KEYDOWN}) {
        ClickAt(t,120); SendMessageW(confirm,message,message == WM_KEYDOWN ? VK_RETURN : 0,0);
        check(!t.confirming_ && h.historyClicks == 0, "window close/default Enter cancels without enable");
    }
    ClickAt(t,120); SendMessageW(confirm,WM_KEYDOWN,VK_ESCAPE,0);
    check(!t.confirming_ && h.historyClicks == 0, "Escape cancels without enable");
    ClickAt(t,120);
    SendMessageW(confirm,WM_LBUTTONDOWN,MK_LBUTTON,Point(t,310,190));
    t.SetHistory(true,true);
    SendMessageW(confirm,WM_LBUTTONUP,0,Point(t,310,190));
    t.SetHistory(false,true); answer(true);
    check(!t.confirming_ && !IsWindowVisible(confirm) && !t.confirmationPressed_ && h.historyClicks == 0,
        "external enable invalidates the pressed consent; returning to off cannot revive its stale mouse-up");
    ClickAt(t,120); t.SetHistory(false,false); answer(true);
    check(!t.confirming_ && h.historyClicks == 0, "availability loss invalidates a late consent click");
    t.SetHistory(false,true); ClickAt(t,120); t.SetEligible(false); answer(true);
    check(!IsWindowVisible(confirm) && h.historyClicks == 0, "eligibility loss cancels consent without emitting or stealing focus");
    t.SetEligible(true,nullptr,&editor); ClickAt(t,120);
    SendMessageW(t.window_,WM_SHOWWINDOW,FALSE,0);
    check(!t.confirming_ && !IsWindowVisible(confirm) && h.historyClicks == 0, "host hide also cancels the confirmation");
    ClickAt(t,120); answer(true); answer(true); ClickAt(t,120);
    check(h.historyClicks == 1 && h.lastHistory && t.historyPending_ && t.historyRequested_ && !t.historyEnabled_ && !t.confirming_ &&
        t.noticeText_ == L"正在保存原文设置" && bluePixels(96) == offBlue, "explicit consent emits exactly one enable and does not optimistically light the icon");
    t.SetHistory(false,false);
    check(!t.historyPending_ && !t.historyAvailable_ && !t.historyEnabled_ && t.noticeError_ && IsWindowVisible(t.notice_) &&
        t.noticeText_ == L"字数与原文存储暂不可用，请重启输入法后台后重试" && bluePixels(96) == offBlue,
        "final unavailable feedback completes the failed save, reports a storage error and never lights the icon");
    ClickAt(t,120);
    check(h.historyClicks == 1 && !t.historyPending_ && !t.confirming_ && t.noticeError_ &&
        t.noticeText_ == L"字数与原文存储暂不可用，请重启输入法后台后重试", "failed storage cannot resubmit or reopen consent and keeps the restart guidance");
    hover(120,L"字数与原文存储暂不可用", "unavailable hover explicitly reports storage failure with full measured text and anchored tail");
    t.SetHistory(false,true); ClickAt(t,120); answer(true);
    t.SetHistory(false,true);
    check(!t.historyPending_ && !t.historyEnabled_ && t.noticeError_ && t.noticeText_ == L"原文设置保存失败，请重试", "failed save keeps off and offers friendly retry feedback");
    ClickAt(t,120); answer(true); t.SetHistory(true,true);
    check(h.historyClicks == 3 && t.historyEnabled_ && !t.historyPending_ && !t.noticeError_ && t.noticeText_ == L"已开启原文保存", "only successful feedback enables the saved-original indicator");
    hover(120,L"关闭原文保存（需确认，保留已有记录）", "enabled hover explains confirmation and retained records with full measured text and anchored tail");
    for (UINT dpi : {96u,120u,144u,192u}) bluePixels(dpi);
    const std::wstring enabledNotice = t.noticeText_;
    ClickAt(t,120);
    check(t.confirming_ && !t.confirmationTarget_ && IsWindowVisible(confirm) && h.historyClicks == 3 &&
        !t.historyPending_ && t.historyEnabled_ && t.noticeText_ == enabledNotice,
        "disable snapshots false and requires confirmation; no callback, save notice or premature unlighting");
    for (int i = 0; i < 3; ++i) t.SetHistory(true,true);
    check(t.confirming_ && !t.confirmationTarget_ && IsWindowVisible(confirm) && h.historyClicks == 3 && t.noticeText_ == enabledNotice,
        "repeated enabled=true feedback keeps the disable confirmation open without a new success notice");
    check(wcscmp(t.ConfirmationTitle(),L"关闭原文保存？") == 0 && wcscmp(t.ConfirmationButtonText(false),L"保持开启") == 0 &&
        wcscmp(t.ConfirmationButtonText(true),L"确认关闭") == 0 &&
        wcscmp(t.ConfirmationText(),L"关闭后，之后的定稿原文将不再保存。已有原文记录不会删除。字数、句数和录音时长仍会继续统计，但不会因此保存原文。") == 0,
        "disable title/buttons explicitly distinguish future originals, retained records and continuing statistics");
    fitConfirmation(); answer(false);
    check(!t.confirming_ && !IsWindowVisible(confirm) && h.historyClicks == 3 && t.historyEnabled_, "disable cancel keeps original saving enabled without submission");
    for (UINT key : {0u,static_cast<UINT>(VK_RETURN),static_cast<UINT>(VK_ESCAPE)}) {
        ClickAt(t,120); SendMessageW(confirm,key ? WM_KEYDOWN : WM_CLOSE,key,0);
        check(!t.confirming_ && h.historyClicks == 3 && t.historyEnabled_, "disable close/default Enter/Escape cancels and preserves saving");
    }
    ClickAt(t,120); answer(true); answer(true); ClickAt(t,120);
    check(h.historyClicks == 4 && !h.lastHistory && t.historyPending_ && !t.historyRequested_ && t.historyEnabled_ &&
        !t.confirming_ && t.noticeText_ == L"正在保存原文设置", "explicit disable emits the snapshotted false once; pending repeated consent/click cannot submit again");
    t.SetHistory(true,true);
    check(!t.historyPending_ && t.historyEnabled_ && t.noticeError_ && t.noticeText_ == L"原文设置保存失败，请重试",
        "failed disable feedback keeps the icon enabled and reports failure, never success");
    ClickAt(t,120); t.SetHistory(false,true); answer(true);
    check(!t.confirming_ && !IsWindowVisible(confirm) && h.historyClicks == 4 && !t.historyEnabled_,
        "external disable invalidates a late disable consent instead of inferring a new opposite target");
    t.SetHistory(true,true); ClickAt(t,120); t.SetHistory(true,false); answer(true);
    check(!t.confirming_ && h.historyClicks == 4 && !t.historyAvailable_, "availability loss also invalidates the disable confirmation");
    t.SetHistory(true,true); ClickAt(t,120); t.SetEligible(false); answer(true);
    check(!t.confirming_ && !IsWindowVisible(confirm) && h.historyClicks == 4, "disable eligibility loss cancels without a callback");
    t.SetEligible(true,nullptr,&editor); ClickAt(t,120); ShowWindow(t.window_,SW_HIDE); answer(true);
    check(!t.confirming_ && !IsWindowVisible(confirm) && h.historyClicks == 4, "actual main-window hide invalidates disable consent");
    t.SetEligible(true,nullptr,&editor);
    for (bool positionHide : {false,true}) {
        ClickAt(t,120);
        if (positionHide) SetWindowPos(confirm,nullptr,0,0,0,0,SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        else ShowWindow(confirm,SW_HIDE);
        t.SetHistory(true,true); answer(true);
        check(!t.confirming_ && !IsWindowVisible(confirm) && h.historyClicks == 4,
            "hiding the confirmation itself invalidates consent and unchanged feedback cannot reopen it");
    }
    ClickAt(t,120); ClickAt(t,120); answer(true);
    check(!t.confirming_ && h.historyClicks == 4, "second history-button click dismisses disable confirmation without saving");
    ClickAt(t,120); answer(true);
    check(h.historyClicks == 5 && !h.lastHistory && t.historyPending_ && t.historyEnabled_, "disable retry waits for storage feedback before changing the indicator");
    t.SetHistory(false,true);
    check(!t.historyEnabled_ && !t.historyPending_ && !t.noticeError_ && t.noticeText_ == L"已关闭原文保存", "only successful disable feedback turns the icon off and reports success");
    const unsigned historySaves = h.historyClicks;
    for (UINT dpi : {96u,120u,144u,192u}) bluePixels(dpi);
    for (const auto state : {VoiceToolbarState::Idle, VoiceToolbarState::Connecting, VoiceToolbarState::Recording}) {
        t.SetState(state); const std::wstring notice = t.noticeText_;
        const unsigned usage = h.usageClicks, language = h.languageClicks;
        ClickAt(t,160); ClickAt(t,200);
        check(h.usageClicks == usage + 1 && h.languageClicks == language + 1 && h.historyClicks == historySaves &&
            h.toggles == 0 && h.settingsClicks == 0 && t.state_ == state && t.noticeText_ == notice,
            "usage/language each invoke once in idle/connecting/recording without toggling or changing status/history/settings");
    }
    t.SetState(VoiceToolbarState::Idle);
    for (int x : {160,200}) {
        SendMessageW(t.window_,WM_LBUTTONDOWN,MK_LBUTTON,Point(t,x,22));
        SendMessageW(t.window_,WM_LBUTTONUP,0,Point(t,74,22));
        SendMessageW(t.window_,WM_LBUTTONDOWN,MK_LBUTTON,Point(t,74,22));
        SendMessageW(t.window_,WM_LBUTTONUP,0,Point(t,x,22));
        SendMessageW(t.window_,WM_LBUTTONDOWN,MK_LBUTTON,Point(t,x,22));
        SendMessageW(t.window_,WM_CANCELMODE,0,0);
        SendMessageW(t.window_,WM_LBUTTONUP,0,Point(t,x,22));
    }
    check(h.usageClicks == 3 && h.languageClicks == 3 && h.toggles == 0 && t.state_ == VoiceToolbarState::Idle,
        "cross-button releases and cancelled navigation presses never invoke navigation or microphone");
    t.SetEligible(false); ClickAt(t,160); ClickAt(t,200);
    check(h.usageClicks == 3 && h.languageClicks == 3 && h.toggles == 0 && !t.languagePressed_ && !t.usagePressed_,
        "hidden/ineligible new buttons ignore clicks and clear pressed state");
    t.SetEligible(true,nullptr,&editor);
    hover(120,L"开启原文保存（需确认，本机数据库）", "disabled hover names confirmation and local database storage with full measured text and anchored tail");
    hover(160,L"查看语音输入用量", "usage hover names the real interface with full measured text and anchored tail");
    hover(200,L"调整识别语言和翻译", "language hover names recognition and translation with full measured text and anchored tail");
    for (bool enabled : {false,true}) {
        t.SetHistory(enabled,true); t.Hover(false); t.SetState(VoiceToolbarState::Recording);
        const unsigned toggles = h.toggles;
        ClickAt(t,120);
        const unsigned frame = t.animation_; SendMessageW(t.window_,WM_TIMER,1,0);
        check(t.confirming_ && t.confirmationTarget_ == !enabled && t.state_ == VoiceToolbarState::Recording &&
            t.animation_ == frame + 1 && h.toggles == toggles,
            "either confirmation preserves recording and its animation dispatcher");
        Click(t);
        check(h.toggles == toggles + 1 && t.state_ == VoiceToolbarState::Idle && t.confirming_, "mic stop remains dispatchable during either confirmation");
        Click(t); check(h.toggles == toggles + 1, "idle mic cannot restart recording behind either open confirmation");
        PostMessageW(confirm,WM_CLOSE,0,0); Pump();
        check(!t.confirming_ && h.historyClicks == historySaves, "posted close remains dispatchable and cannot consent to either target");
    }
    check(GetForegroundWindow() == front && GetActiveWindow() == active && GetFocus() == focus && !t.editor_,
        "all new-button/confirmation/feedback operations preserve foreground, focus and editor identity");
    t.SetHistory(false,true);
    const HWND main = t.window_; h.destroyOnHistory = true;
    ClickAt(t,120); answer(true);
    check(h.historyClicks == historySaves + 1 && !IsWindow(main) && !IsWindow(confirm) && !t.Handle(), "history callback may destroy all UI safely including consent window");
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
    if (!toolbar.Create(instance, Toggle, &harness, OpenSettings, History, OpenUsage, OpenLanguage)) { check(false, "toolbar creation"); return 1; }
    check(dpiSet && Awareness(toolbar.Handle()) == (unaware ? 0 : system ? 1 : 2), "test EXE actually uses its requested DPI-awareness mode");
    const HWND window = toolbar.Handle(), tip = VoiceToolbarTestAccess::Tooltip(toolbar), notice = VoiceToolbarTestAccess::Notice(toolbar);
    const auto className = VoiceToolbarTestAccess::ClassName(toolbar);
    check(!IsWindowVisible(window) && !IsWindowVisible(tip) && !IsWindowVisible(notice),
        "created toolbar and all attached windows stay hidden until eligibility");
    check(ClientSize(window, {MulDiv(264,ActualDpi(window),96), MulDiv(44,ActualDpi(window),96)}),
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
        !*toolbar.TooltipText(), "selected idle has no microphone teaching tooltip, without a recording session");
    RECT rect{}; GetClientRect(window, &rect);
    std::printf("SIZE client=%ldx%ld body=256x36DIP plus 4DIP alpha-shadow margin\n", rect.right, rect.bottom);
    for (HWND hwnd : {window, tip, notice}) {
        const LONG_PTR ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        check((ex & (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) == (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) &&
            SendMessageW(hwnd, WM_MOUSEACTIVATE, 0, MAKELPARAM(HTCLIENT, WM_LBUTTONDOWN)) == MA_NOACTIVATE,
            "toolbar and both tooltip/status windows are nonactivating tool windows");
        if (hwnd != window) check((ex & WS_EX_TRANSPARENT) &&
            SendMessageW(hwnd, WM_NCHITTEST, 0, 0) == HTTRANSPARENT, "chip and bubble/tail do not intercept clicks or enlarge the main hitbox");
    }
    SendMessageW(window, WM_MOUSEMOVE, 0, VoiceToolbarTestAccess::Point(toolbar, 74, 22));
    SendMessageW(window, WM_TIMER, 2, 0);
    wchar_t text[128]{}; GetWindowTextW(tip, text, 128);
    check(VoiceToolbarTestAccess::Hovered(toolbar) && !IsWindowVisible(tip) && !*toolbar.TooltipText(),
        "mic hover and late hover timer never show teaching text");
    SendMessageW(window, WM_MOUSEMOVE, 0, VoiceToolbarTestAccess::Point(toolbar, 240, 22));
    WaitVisible(tip, true, 1200);
    GetWindowTextW(tip, text, 128);
    check(IsWindowVisible(tip) && wcscmp(text, L"打开输入法设置") == 0,
        "real hover timer shows the Unicode settings tooltip");
    if (!output.empty()) check(VoiceToolbarTestAccess::CaptureWindow(toolbar, output + L"\\02_hover.png", true),
        "one actual own-HWND settings hover screenshot includes the downward bubble tail");
    const ULONGLONG gearMs = WaitVisible(tip, false, 2400);
    SendMessageW(window, WM_MOUSEMOVE, 0, VoiceToolbarTestAccess::Point(toolbar, 240, 22));
    SendMessageW(window, WM_TIMER, 2, 0);
    std::printf("GEAR-TOOLTIP elapsed-ms=%llu\n", gearMs);
    check(gearMs >= 1300 && gearMs < 2400 && !IsWindowVisible(tip), "settings tooltip expires even while pointer remains over gear");
    ClickGear(toolbar);
    check(harness.settingsClicks == 1 && harness.toggles == 0, "gear click invokes settings callback without toggling recording");
    SendMessageW(window, WM_MOUSEMOVE, 0, VoiceToolbarTestAccess::Point(toolbar, 74, 22));
    SendMessageW(window, WM_TIMER, 2, 0);
    Click(toolbar);
    check(harness.toggles == 1 && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Connecting &&
        VoiceToolbarTestAccess::NoticeText(toolbar) == L"正在连接" && IsWindowVisible(notice) && !IsWindowVisible(tip),
        "mic click reaches shared toggle and connecting stop state");
    const ULONGLONG connectingMs = WaitVisible(notice, false, 2000);
    std::printf("CONNECTING-TOAST elapsed-ms=%llu\n", connectingMs);
    check(connectingMs >= 850 && connectingMs < 2000 && !IsWindowVisible(notice) &&
        VoiceToolbarTestAccess::NoticeText(toolbar).empty() && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Connecting,
        "real one-second connecting toast hides without ending the connection state");
    Click(toolbar);
    check(harness.toggles == 2 && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Idle && IsWindowVisible(window),
        "click stops connecting synchronously and keeps idle toolbar visible");
    Click(toolbar); toolbar.SetState(VoiceToolbarState::Recording);
    const unsigned animation = VoiceToolbarTestAccess::Animation(toolbar);
    SendMessageW(window, WM_TIMER, 1, 0);
    SendMessageW(window, WM_TIMER, 3, 0); // Queued old idle-toast message must not clear the new recording hint.
    check(VoiceToolbarTestAccess::Animation(toolbar) == animation + 1 && VoiceToolbarTestAccess::NoticeText(toolbar) == L"开始录音" && IsWindowVisible(notice),
        "recording animation is presentation-only; stale toast cannot clear fresh start toast");
    SendMessageW(window, WM_MOUSELEAVE, 0, 0);
    if (!output.empty()) check(VoiceToolbarTestAccess::CaptureWindow(toolbar, output + L"\\04_recording-window.png"),
        "one actual own-HWND recording screenshot includes the compact full-text chip");
    const ULONGLONG recordingMs = WaitVisible(notice, false, 2000);
    std::printf("RECORDING-TOAST elapsed-ms=%llu\n", recordingMs);
    check(recordingMs >= 850 && recordingMs < 2000 && !IsWindowVisible(notice) &&
        VoiceToolbarTestAccess::NoticeText(toolbar).empty() && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Recording &&
        VoiceToolbarTestAccess::Animation(toolbar) > animation + 1, "real one-second start toast hides while recording animation continues");
    PostMessageW(window, WM_TIMER, 3, 0);
    Click(toolbar);
    Pump();
    check(harness.toggles == 4 && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Idle &&
        VoiceToolbarTestAccess::NoticeText(toolbar) == L"已停止" && IsWindowVisible(window) && IsWindowVisible(notice),
        "stop returns to idle with a fresh toast protected from the posted old recording timer");
    PostMessageW(window, WM_TIMER, 3, 0);
    toolbar.SetState(VoiceToolbarState::Idle, true); Pump();
    check(VoiceToolbarTestAccess::NoticeText(toolbar) == L"已停止" && IsWindowVisible(notice),
        "posted old Idle timer cannot clear an Idle-to-Idle replacement toast");
    const ULONGLONG stoppedMs = WaitVisible(notice, false, 2000);
    std::printf("STOP-TOAST elapsed-ms=%llu\n", stoppedMs);
    check(stoppedMs >= 850 && stoppedMs < 2000 && !IsWindowVisible(notice) && IsWindowVisible(window),
        "real one-second stop timer expires the lightweight chip, not the main toolbar");
    VoiceToolbarTestAccess::RecordingChecks(toolbar, failures);
    RECT before{}, after{}; GetWindowRect(window, &before);
    SendMessageW(window, WM_LBUTTONDOWN, MK_LBUTTON, VoiceToolbarTestAccess::Point(toolbar, 20, 20));
    SendMessageW(window, WM_MOUSEMOVE, MK_LBUTTON, VoiceToolbarTestAccess::Point(toolbar, 44, 34));
    SendMessageW(window, WM_LBUTTONUP, 0, VoiceToolbarTestAccess::Point(toolbar, 20, 20));
    GetWindowRect(window, &after);
    check(after.left > before.left && after.top > before.top && harness.toggles == 4 && harness.settingsClicks == 1 &&
        harness.historyClicks == 0 && harness.usageClicks == 0 && harness.languageClicks == 0,
        "brand drag moves toolbar without triggering any of the six buttons");
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
    VoiceToolbarTestAccess::HistoryChecks(instance, editor, failures);
    toolbar.SetEligible(false); toolbar.SetState(VoiceToolbarState::Recording);
    check(!IsWindowVisible(window) && !IsWindowVisible(tip) && !IsWindowVisible(notice), "late recording callback cannot show an ineligible notice alone");
    PositionTrace visibility; visibility.main = window; trace = &visibility;
    visibility.previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(tip, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(TracePosition)));
    SetWindowLongPtrW(notice, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(TracePosition));
    toolbar.SetEligible(true, nullptr, &editor);
    SetWindowLongPtrW(tip, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(visibility.previous));
    SetWindowLongPtrW(notice, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(visibility.previous)); trace = nullptr;
    check(!visibility.orphanHint && IsWindowVisible(window) && IsWindowVisible(notice) && !IsWindowVisible(tip),
        "pre-existing recording appears only after the main HWND is actually visible");
    SendMessageW(window, WM_MOUSEMOVE, 0, VoiceToolbarTestAccess::Point(toolbar, 74, 22)); SendMessageW(window, WM_TIMER, 2, 0);
    check(!IsWindowVisible(tip), "recording microphone still has no hover teaching bubble");
    ShowWindow(window, SW_HIDE);
    toolbar.SetState(VoiceToolbarState::Connecting); SendMessageW(window, WM_TIMER, 2, 0); Click(toolbar);
    check(!IsWindowVisible(window) && !IsWindowVisible(tip) && !IsWindowVisible(notice) && harness.toggles == 4,
        "actual ShowWindow hide also hides attached windows; late status/hover/click cannot resurrect them");
    GetWindowRect(window, &before);
    visibility.orphanHint = false; trace = &visibility;
    visibility.previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(tip, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(TracePosition)));
    SetWindowLongPtrW(notice, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(TracePosition));
    toolbar.SetEligible(true, nullptr, &editor); // Eligibility alone must repair the hidden main before showing its status.
    GetWindowRect(window, &after);
    check(!visibility.orphanHint && IsWindowVisible(window) && IsWindowVisible(notice) && !IsWindowVisible(tip) &&
        VoiceToolbarTestAccess::NoticeText(toolbar) == L"正在连接" && EqualRect(&before, &after),
        "eligible refresh restores hidden main before attached status at the same position without stale hover");
    for (int i = 0; i < 2; ++i) toolbar.SetEligible(true, nullptr, &editor);
    GetWindowRect(window, &after);
    SetWindowLongPtrW(tip, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(visibility.previous));
    SetWindowLongPtrW(notice, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(visibility.previous)); trace = nullptr;
    check(!visibility.orphanHint && IsWindowVisible(window) && IsWindowVisible(notice) && !IsWindowVisible(tip) && EqualRect(&before, &after),
        "subsequent eligible polls retain restored main and status without reanchoring or stale hover");
    SendMessageW(window, WM_MOUSELEAVE, 0, 0);
    SendMessageW(window, WM_MOUSEMOVE, 0, VoiceToolbarTestAccess::Point(toolbar, 240, 22)); WaitVisible(tip, true, 1200);
    RECT bubble{}; GetWindowRect(tip, &bubble); GetWindowRect(window, &after);
    const LONG tailPixel = VoiceToolbarTestAccess::TailPixel(toolbar);
    std::printf("TAIL bubble-left=%ld offset=%ld gear-x=%ld\n", bubble.left, tailPixel, after.left + MulDiv(240, ActualDpi(window), 96));
    check(IsWindowVisible(tip) && tailPixel >= MulDiv(17, ActualDpi(window), 96) &&
        tailPixel <= bubble.right - bubble.left - MulDiv(17, ActualDpi(window), 96) &&
        std::abs(bubble.left + tailPixel - after.left - MulDiv(240, ActualDpi(window), 96)) <= 1,
        "clamped settings bubble's downward tail, not its window center, points to the gear");
    SetWindowPos(window, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_HIDEWINDOW);
    check(!IsWindowVisible(tip) && !IsWindowVisible(notice), "native SetWindowPos hide synchronously hides both attachments");
    toolbar.SetEligible(false); toolbar.SetState(VoiceToolbarState::Idle); toolbar.SetEligible(true, nullptr, &editor);
    toolbar.SetState(VoiceToolbarState::Idle, false, true); GetWindowTextW(notice, text, 128);
    check(IsWindowVisible(window) && IsWindowVisible(notice) && wcscmp(text, L"语音连接失败，请按 F8 重试") == 0,
        "friendly error preserves full Chinese text in its measured chip");
    SendMessageW(window, WM_TIMER, 3, 0);
    check(IsWindowVisible(notice), "early queued timer does not dismiss a fresh error");
    const ULONGLONG errorMs = WaitVisible(notice, false, 4500);
    std::printf("ERROR-TOAST elapsed-ms=%llu\n", errorMs);
    check(errorMs >= 3300 && errorMs < 4500 && !IsWindowVisible(notice) && VoiceToolbarTestAccess::NoticeText(toolbar).empty(),
        "real error toast retains its separate 3.5-second lifetime then hides");
    toolbar.SetState(VoiceToolbarState::Recording); Toggle(&harness); // Simulated F8 invokes the exact callback used by Click.
    check(harness.toggles == 5 && VoiceToolbarTestAccess::State(toolbar) == VoiceToolbarState::Idle &&
        VoiceToolbarTestAccess::NoticeText(toolbar) == L"已停止", "shared F8 callback stops synchronously just like the mic click");
    VoiceToolbar disposable; Harness closing{&disposable, 0, 0, true};
    check(disposable.Create(instance, Toggle, &closing), "callback teardown fixture created");
    disposable.SetEligible(true, nullptr, &editor); const HWND closingWindow = disposable.Handle(); Click(disposable);
    check(closing.toggles == 1 && !IsWindow(closingWindow) && !disposable.Handle(), "mic callback can destroy its toolbar without later access");
    for (int x : {160,200}) {
        VoiceToolbar navigation; Harness h{&navigation}; h.destroyOnNavigation = true;
        check(navigation.Create(instance,Toggle,&h,OpenSettings,History,OpenUsage,OpenLanguage), "navigation teardown fixture created");
        navigation.SetEligible(true,nullptr,&editor); const HWND hwnd = navigation.Handle(); ClickAt(navigation,x);
        check(h.usageClicks == (x == 160 ? 1u : 0u) && h.languageClicks == (x == 200 ? 1u : 0u) && h.toggles == 0 &&
            !IsWindow(hwnd) && !navigation.Handle(), "usage/language callback can destroy toolbar safely without a recording callback");
    }
    check(GetForegroundWindow() == front && GetActiveWindow() == active && GetFocus() == focus,
        "show/hide/hover/click/stop/drag/late callback/teardown leave foreground, active window and keyboard focus unchanged");
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