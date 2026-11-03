#include "VoiceToolbar.h"
#include "VoiceBrand.h"
#include <windowsx.h>
#include <algorithm>
#include <cmath>
#include <cstring>

namespace {
constexpr int Margin = 4, Width = 96, Height = 36;
constexpr int HintTail = 5;
constexpr UINT_PTR AnimationTimer = 1, HoverTimer = 2, ToastTimer = 3;
UINT WindowDpi(HWND hwnd) {
    using GetDpi = UINT (WINAPI*)(HWND);
    const auto fn = reinterpret_cast<GetDpi>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForWindow"));
    if (fn && hwnd) { const UINT dpi = fn(hwnd); if (dpi) return dpi; }
    HDC dc = GetDC(hwnd); const int dpi = dc ? GetDeviceCaps(dc, LOGPIXELSX) : 96;
    if (dc) ReleaseDC(hwnd, dc);
    return dpi > 0 ? static_cast<UINT>(dpi) : 96;
}
struct PlacementGuard {
    bool& flag;
    explicit PlacementGuard(bool& value) : flag(value) { flag = true; }
    ~PlacementGuard() { flag = false; }
};
void HintFormat(Gdiplus::StringFormat& format) {
    format.SetFormatFlags(Gdiplus::StringFormatFlagsNoWrap);
    format.SetTrimming(Gdiplus::StringTrimmingNone);
    format.SetAlignment(Gdiplus::StringAlignmentCenter);
    format.SetLineAlignment(Gdiplus::StringAlignmentCenter);
}
void Setup(Gdiplus::Graphics& g) {
    g.SetPageUnit(Gdiplus::UnitPixel);
    g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
    g.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHighQuality);
    // ClearType assumes an opaque RGB background and is unsuitable for alpha windows.
    g.SetTextRenderingHint(Gdiplus::TextRenderingHintAntiAliasGridFit);
}
void Panel(Gdiplus::Graphics& g, float width, float height, bool status) {
    using namespace Gdiplus;
    for (int i = 3; i > 0; --i) {
        GraphicsPath shadow; VoiceBrand::RoundRect(shadow, 4.0f - i, 5.0f - i, width + i * 2, height + i * 2, 8.0f + i);
        SolidBrush shade(Color(4, 68, 111, 166)); g.FillPath(&shade, &shadow);
    }
    GraphicsPath body; VoiceBrand::RoundRect(body, 4.5f, 4.5f, width - 1, height - 1, 8.0f);
    LinearGradientBrush background(PointF(4, 4), PointF(4, height + 4),
        status ? Color(255, 234, 246, 255) : Color(255, 248, 251, 255),
        status ? Color(255, 220, 239, 255) : Color(255, 235, 243, 255));
    g.FillPath(&background, &body);
    Pen border(Color(255, 184, 207, 240), 1); g.DrawPath(&border, &body);
}
void PanelHint(Gdiplus::Graphics& g, float width, float height, bool status, bool error, bool active, float tail) {
    using namespace Gdiplus;
    const float x = 4.5f, y = 4.5f, w = width - 1, h = height - 1, radius = 8, diameter = radius * 2;
    GraphicsPath body;
    if (status) VoiceBrand::RoundRect(body, x, y, w, h, radius);
    else {
        // One closed outline avoids an opaque seam between the bubble and its alpha tail.
        tail = std::clamp(tail, x + radius + 4, x + w - radius - 4);
        body.AddArc(x, y, diameter, diameter, 180, 90);
        body.AddArc(x + w - diameter, y, diameter, diameter, 270, 90);
        body.AddArc(x + w - diameter, y + h - diameter, diameter, diameter, 0, 90);
        body.AddLine(x + w - radius, y + h, tail + 4, y + h);
        body.AddLine(tail + 4, y + h, tail, y + h + HintTail);
        body.AddLine(tail, y + h + HintTail, tail - 4, y + h);
        body.AddArc(x, y + h - diameter, diameter, diameter, 90, 90);
        body.CloseFigure();
    }
    const auto saved = g.Save(); g.TranslateTransform(0, 1);
    SolidBrush shade(Color(status ? 8 : 16, 68, 111, 166)); g.FillPath(&shade, &body); g.Restore(saved);
    const Color top = !status ? Color(255, 248, 251, 255) : error ? Color(255, 255, 248, 238) :
        active ? Color(255, 234, 246, 255) : Color(255, 243, 247, 253);
    const Color bottom = !status ? Color(255, 239, 245, 255) : error ? Color(255, 253, 241, 221) :
        active ? Color(255, 220, 239, 255) : Color(255, 238, 244, 251);
    LinearGradientBrush background(PointF(x, y), PointF(x, y + h), top, bottom);
    g.FillPath(&background, &body);
    if (!status || active || error) {
        Pen border(error ? Color(255, 232, 205, 162) : Color(255, 184, 207, 240), .8f);
        g.DrawPath(&border, &body);
    }
}
}

bool VoiceToolbar::Create(HINSTANCE instance, Toggle toggle, void* owner) {
    if (window_ || graphicsToken_) return false;
    instance_ = instance; toggle_ = toggle; owner_ = owner;
    Gdiplus::GdiplusStartupInput startup;
    if (Gdiplus::GdiplusStartup(&graphicsToken_, &startup, nullptr) != Gdiplus::Ok) { graphicsToken_ = 0; return false; }
    className_ = L"TrueFluentVoiceIme.Toolbar." + std::to_wstring(reinterpret_cast<UINT_PTR>(this));
    WNDCLASSEXW cls{sizeof(cls)}; cls.hInstance = instance; cls.lpfnWndProc = WindowProc;
    cls.lpszClassName = className_.c_str(); cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    if (!RegisterClassExW(&cls)) { Destroy(); return false; }
    classRegistered_ = true;
    constexpr DWORD style = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED;
    window_ = CreateWindowExW(style, className_.c_str(), L"译见pro输入法", WS_POPUP,
        0, 0, Width + Margin * 2, Height + Margin * 2, nullptr, nullptr, instance, this);
    if (window_) {
        tooltip_ = CreateWindowExW(style | WS_EX_TRANSPARENT, className_.c_str(), L"", WS_POPUP,
            0, 0, 1, 1, window_, nullptr, instance, this);
        notice_ = CreateWindowExW(style | WS_EX_TRANSPARENT, className_.c_str(), L"", WS_POPUP,
            0, 0, 1, 1, window_, nullptr, instance, this);
    }
    icon_ = static_cast<HICON>(LoadImageW(instance, MAKEINTRESOURCEW(101), IMAGE_ICON, 32, 32, 0));
    if (!window_ || !tooltip_ || !notice_ || !icon_) { Destroy(); return false; }
    dpi_ = WindowDpi(window_);
    if (!Render(window_, Width + Margin * 2, Height + Margin * 2)) { Destroy(); return false; }
    return true;
}
void VoiceToolbar::Destroy() {
    eligible_ = false; toggle_ = nullptr; owner_ = nullptr;
    if (window_) {
        for (UINT_PTR timer : {AnimationTimer, HoverTimer, ToastTimer}) KillTimer(window_, timer);
        if (GetCapture() == window_) ReleaseCapture();
    }
    if (tooltip_) DestroyWindow(tooltip_);
    if (notice_) DestroyWindow(notice_);
    if (window_) DestroyWindow(window_);
    window_ = tooltip_ = notice_ = nullptr;
    if (icon_) DestroyIcon(icon_); icon_ = nullptr;
    if (classRegistered_) UnregisterClassW(className_.c_str(), instance_);
    classRegistered_ = false; className_.clear();
    if (graphicsToken_) Gdiplus::GdiplusShutdown(graphicsToken_); graphicsToken_ = 0;
    instance_ = nullptr; anchored_ = hovered_ = pressed_ = dragging_ = placing_ = false;
    state_ = VoiceToolbarState::Idle; noticeText_.clear();
    tooltipReady_ = noticeError_ = false; tooltipTail_ = 0;
    dpiPending_ = placingHints_ = false; editor_ = nullptr; editorMonitor_ = nullptr; editorDpi_ = 0;
}
SIZE VoiceToolbar::ScaledSize(int width, int height, UINT dpi) {
    return {MulDiv(width, static_cast<int>(dpi), 96), MulDiv(height, static_cast<int>(dpi), 96)};
}
POINT VoiceToolbar::ClampToWorkArea(POINT point, SIZE size, const RECT& work) {
    point.x = std::clamp(point.x, work.left, std::max(work.left, work.right - size.cx));
    point.y = std::clamp(point.y, work.top, std::max(work.top, work.bottom - size.cy));
    return point;
}
bool VoiceToolbar::FollowEditor(HMONITOR target, HMONITOR current, bool targetChanged) const {
    return !dragging_ && target && target != current && targetChanged;
}
POINT VoiceToolbar::DragPosition(POINT cursor, UINT dpi) const {
    return {cursor.x - static_cast<LONG>(std::lround(dragOffset_.x * dpi / 96.0f)),
        cursor.y - static_cast<LONG>(std::lround(dragOffset_.y * dpi / 96.0f))};
}
int VoiceToolbar::Pixels(int dip) const { return MulDiv(dip, static_cast<int>(dpi_), 96); }
const wchar_t* VoiceToolbar::TooltipText() const {
    return state_ == VoiceToolbarState::Idle ? L"按下 F8 开始录音" : L"按下 F8 停止录音";
}
void VoiceToolbar::SetEligible(bool eligible, HWND editor, const RECT* rect) {
    if (!window_) return;
    const bool changed = eligible_ != eligible; eligible_ = eligible;
    if (!eligible) {
        tooltipReady_ = false;
        hovered_ = pressed_ = dragging_ = false;
        if (GetCapture() == window_) ReleaseCapture();
        for (UINT_PTR timer : {AnimationTimer, HoverTimer, ToastTimer}) KillTimer(window_, timer);
        HideHints(); ShowWindow(window_, SW_HIDE);
        // Do not replay a previous session's transient toast after switching back.
        if (state_ == VoiceToolbarState::Idle) noticeText_.clear();
        return;
    }
    RECT area{};
    if (rect && rect->right > rect->left && rect->bottom > rect->top) area = *rect;
    else if (editor) GetWindowRect(editor, &area);
    const bool hasArea = area.right > area.left && area.bottom > area.top;
    const HMONITOR target = hasArea ? MonitorFromRect(&area, MONITOR_DEFAULTTONEAREST) :
        editor ? MonitorFromWindow(editor, MONITOR_DEFAULTTONEAREST) : nullptr;
    const UINT editorDpi = WindowDpi(editor ? editor : window_);
    const bool targetChanged = changed || editor != editor_ || target != editorMonitor_ || editorDpi != editorDpi_;
    if (!anchored_ || FollowEditor(target, MonitorFromWindow(window_, MONITOR_DEFAULTTONEAREST), targetChanged)) {
        POINT point{};
        if (hasArea) {
            point = {area.left + MulDiv(20, static_cast<int>(editorDpi), 96),
                area.bottom - MulDiv(Height + Margin * 2 + 16, static_cast<int>(editorDpi), 96)};
            // An editor can straddle displays while its left edge is still on the
            // old one. Anchor inside the selected display, using the editor's DPI.
            MONITORINFO info{sizeof(info)};
            if (GetMonitorInfoW(target, &info))
                point = ClampToWorkArea(point, ScaledSize(Width + Margin * 2, Height + Margin * 2, editorDpi), info.rcWork);
        } else { GetCursorPos(&point); point.x += Pixels(12); point.y += Pixels(24); }
        anchored_ = true; Place(point);
    } else if (!dragging_ && (changed || editorDpi != editorDpi_ || dpi_ != WindowDpi(window_))) Place(position_);
    // Polls do not re-anchor to the mouse or undo a deliberate drag. A monitor/editor
    // change is followed once; changes observed during a drag are handled after release.
    if (!dragging_) { editor_ = editor; editorMonitor_ = target; editorDpi_ = editorDpi; }
    // Eligibility owns idle visibility too; a window hidden by the host must
    // recover without waiting for a recording transition or another key press.
    if (changed || !IsWindowVisible(window_)) {
        RenderMain(); SetWindowPos(window_, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        UpdateTimers(); RenderHints();
    }
}
void VoiceToolbar::SetState(VoiceToolbarState state, bool stopped, bool error) {
    if (state_ == state && !stopped && !error) return;
    state_ = state; animation_ = 0;
    noticeError_ = error && state == VoiceToolbarState::Idle;
    if (window_) KillTimer(window_, ToastTimer);
    noticeText_ = state == VoiceToolbarState::Connecting ? L"正在连接 · F8 停止" :
        state == VoiceToolbarState::Recording ? L"正在录音 · F8 停止" :
        error ? L"语音连接失败，请按 F8 重试" : stopped ? L"录音已停止" : L"";
    if (!eligible_ && state == VoiceToolbarState::Idle) noticeText_.clear();
    if (window_ && eligible_ && state == VoiceToolbarState::Idle && !noticeText_.empty())
        SetTimer(window_, ToastTimer, error ? 3500 : 1600, nullptr);
    UpdateTimers(); RenderMain(); RenderHints();
}
void VoiceToolbar::UpdateTimers() {
    if (!window_) return;
    if (eligible_ && state_ == VoiceToolbarState::Recording) SetTimer(window_, AnimationTimer, 80, nullptr);
    else KillTimer(window_, AnimationTimer);
}
void VoiceToolbar::Place(POINT point) {
    if (!window_ || placing_) return;
    {
        PlacementGuard guard(placing_);
        SIZE size = ScaledSize(Width + Margin * 2, Height + Margin * 2, dpi_);
        // Move before clamping with the old DPI. Synchronous WM_DPICHANGED queues
        // its whole suggested rect even while placing_; never discard that location.
        for (unsigned step = 0; step < 3; ++step) {
            if (dpiPending_) {
                point = {dpiSuggested_.left, dpiSuggested_.top};
                size = {dpiSuggested_.right - dpiSuggested_.left, dpiSuggested_.bottom - dpiSuggested_.top};
                dpiPending_ = false;
            }
            if (!SetWindowPos(window_, HWND_TOPMOST, point.x, point.y, size.cx, size.cy, SWP_NOACTIVATE)) break;
            if (dpiPending_) continue;
            // A synthetic DPI message does not change HWND DPI. Respect the actual
            // host/window result instead of forcing the message's number indefinitely.
            dpi_ = WindowDpi(window_);
            RECT actual{}; if (!GetWindowRect(window_, &actual)) break;
            point = dragging_ ? DragPosition(dragPoint_, dpi_) : POINT{actual.left, actual.top};
            size = ScaledSize(Width + Margin * 2, Height + Margin * 2, dpi_);
            MONITORINFO info{sizeof(info)};
            const HMONITOR monitor = dragging_ ? MonitorFromPoint(dragPoint_, MONITOR_DEFAULTTONEAREST) :
                MonitorFromWindow(window_, MONITOR_DEFAULTTONEAREST);
            if (GetMonitorInfoW(monitor, &info)) point = ClampToWorkArea(point, size, info.rcWork);
            if (point.x == actual.left && point.y == actual.top &&
                size.cx == actual.right - actual.left && size.cy == actual.bottom - actual.top) break;
        }
        dpi_ = WindowDpi(window_);
        RECT actual{}; if (GetWindowRect(window_, &actual)) position_ = {actual.left, actual.top};
    }
    RenderMain(); RenderHints();
}
bool VoiceToolbar::MicAt(LPARAM point) const {
    const int x = GET_X_LPARAM(point), y = GET_Y_LPARAM(point);
    return x >= Pixels(Margin + 50) && x < Pixels(Margin + Width) && y >= Pixels(Margin) && y < Pixels(Margin + Height);
}
bool VoiceToolbar::BrandAt(LPARAM point) const {
    const int x = GET_X_LPARAM(point), y = GET_Y_LPARAM(point);
    return x >= Pixels(Margin) && x < Pixels(Margin + 43) && y >= Pixels(Margin) && y < Pixels(Margin + Height);
}
void VoiceToolbar::Hover(bool hovered) {
    if (hovered_ == hovered) return;
    hovered_ = hovered; tooltipReady_ = false;
    KillTimer(window_, HoverTimer);
    if (hovered && eligible_ && IsWindowVisible(window_)) SetTimer(window_, HoverTimer, 400, nullptr);
    else ShowWindow(tooltip_, SW_HIDE);
    RenderMain();
}
LRESULT CALLBACK VoiceToolbar::WindowProc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    auto* self = reinterpret_cast<VoiceToolbar*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        self = static_cast<VoiceToolbar*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    if (self) {
        try { return self->Message(hwnd, message, w, l); }
        catch (...) { if (self->window_) self->SetEligible(false); return 0; }
    }
    return DefWindowProcW(hwnd, message, w, l);
}
LRESULT VoiceToolbar::Message(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (message == WM_NCHITTEST && hwnd != window_) return HTTRANSPARENT;
    if (message == WM_PAINT) { PAINTSTRUCT ps{}; BeginPaint(hwnd, &ps); EndPaint(hwnd, &ps); return 0; }
    if (message == WM_ERASEBKGND) return 1;
    if (message == WM_NCDESTROY) { SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0); return DefWindowProcW(hwnd, message, w, l); }
    if (message == WM_DPICHANGED) {
        if (!l || !LOWORD(w) || !HIWORD(w)) return 0;
        const RECT& suggested = *reinterpret_cast<const RECT*>(l);
        if (suggested.right <= suggested.left || suggested.bottom <= suggested.top) return 0;
        if (hwnd == window_) {
            dpi_ = LOWORD(w); dpiSuggested_ = suggested; dpiPending_ = true;
            if (!placing_) Place({suggested.left, suggested.top});
        } else if ((hwnd == tooltip_ || hwnd == notice_) && !placing_ && !placingHints_) {
            // Owned hints use the main window's DPI and stay in its work area,
            // rather than independently scaling or recursively relaying suggestions.
            RenderHints();
        }
        return 0;
    }
    if (hwnd != window_) return DefWindowProcW(hwnd, message, w, l);
    switch (message) {
    case WM_SHOWWINDOW:
        // This message precedes visibility; never show hints from its TRUE branch.
        if (!w) { tooltipReady_ = false; HideHints(); }
        break;
    case WM_WINDOWPOSCHANGED:
        if (l) {
            const UINT flags = reinterpret_cast<const WINDOWPOS*>(l)->flags;
            if (flags & SWP_HIDEWINDOW) { tooltipReady_ = false; HideHints(); }
            else if (flags & SWP_SHOWWINDOW) RenderHints();
        }
        break;
    case WM_DISPLAYCHANGE: Place(position_); return 0;
    case WM_TIMER:
        if (w == AnimationTimer && eligible_ && state_ == VoiceToolbarState::Recording) { ++animation_; RenderMain(); }
        if (w == HoverTimer) {
            KillTimer(window_, HoverTimer);
            if (eligible_ && hovered_ && IsWindowVisible(window_)) tooltipReady_ = true;
            RenderHints();
        }
        if (w == ToastTimer && state_ == VoiceToolbarState::Idle) {
            KillTimer(window_, ToastTimer); noticeText_.clear(); ShowWindow(notice_, SW_HIDE);
        }
        return 0;
    case WM_MOUSEMOVE:
        if (dragging_) {
            POINT point{GET_X_LPARAM(l), GET_Y_LPARAM(l)}; ClientToScreen(hwnd, &point);
            dragPoint_ = point; Place(DragPosition(point, dpi_));
        } else {
            TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE, hwnd, 0}; TrackMouseEvent(&track);
            Hover(eligible_ && IsWindowVisible(window_) && MicAt(l));
        }
        return 0;
    case WM_MOUSELEAVE: Hover(false); return 0;
    case WM_LBUTTONDOWN:
        if (!eligible_ || !IsWindowVisible(window_)) return 0;
        if (BrandAt(l)) {
            dragging_ = true;
            dragOffset_ = {GET_X_LPARAM(l) * 96.0f / dpi_, GET_Y_LPARAM(l) * 96.0f / dpi_};
            dragPoint_ = {GET_X_LPARAM(l), GET_Y_LPARAM(l)}; ClientToScreen(hwnd, &dragPoint_);
            Hover(false); SetCapture(hwnd);
        } else if (MicAt(l)) { pressed_ = true; SetCapture(hwnd); RenderMain(); }
        return 0;
    case WM_LBUTTONUP: {
        const bool invoke = eligible_ && IsWindowVisible(window_) && pressed_ && MicAt(l);
        pressed_ = dragging_ = false;
        if (GetCapture() == hwnd) ReleaseCapture();
        RenderMain();
        // The callback may deactivate/destroy the TSF service. Do not touch this afterwards.
        const auto callback = toggle_; void* owner = owner_;
        if (invoke && callback) callback(owner);
        return 0;
    }
    case WM_CAPTURECHANGED:
    case WM_CANCELMODE:
        pressed_ = dragging_ = false;
        if (GetCapture() == hwnd) ReleaseCapture();
        RenderMain(); return 0;
    case WM_SETCURSOR:
        if (dragging_) { SetCursor(LoadCursorW(nullptr, IDC_SIZEALL)); return TRUE; }
        break;
    }
    return DefWindowProcW(hwnd, message, w, l);
}

void VoiceToolbar::PaintMain(Gdiplus::Graphics& g) const {
    using namespace Gdiplus;
    Setup(g); Panel(g, Width, Height, false);
    VoiceBrand::Draw(g, 14, 10, 24);
    Pen divider(Color(255, 168, 194, 235), 1); g.DrawLine(&divider, 50.0f, 13.0f, 50.0f, 31.0f);
    const bool lit = hovered_ || state_ != VoiceToolbarState::Idle;
    if (lit) {
        SolidBrush halo(pressed_ ? Color(255, 22, 95, 228) : Color(255, 39, 119, 255));
        g.FillEllipse(&halo, 62.0f, 10.0f, 24.0f, 24.0f);
    }
    const Color mic = lit ? Color(255, 255, 255, 255) : Color(255, 83, 105, 145);
    SolidBrush ink(mic); GraphicsPath capsule; VoiceBrand::RoundRect(capsule, 70.5f, 13, 7, 11, 3.5f); g.FillPath(&ink, &capsule);
    Pen line(mic, 1.6f); line.SetStartCap(LineCapRound); line.SetEndCap(LineCapRound);
    g.DrawLine(&line, 67.5f, 21.0f, 67.5f, 23.0f); g.DrawLine(&line, 80.5f, 21.0f, 80.5f, 23.0f);
    g.DrawArc(&line, 67.5f, 16.5f, 13.0f, 13.0f, 0.0f, 180.0f); g.DrawLine(&line, 74.0f, 29.5f, 74.0f, 32.0f);
    if (state_ == VoiceToolbarState::Recording) {
        Pen wave(Color(255, 39, 119, 255), 1.5f); wave.SetStartCap(LineCapRound); wave.SetEndCap(LineCapRound);
        for (int i = 0; i < 3; ++i) {
            const float h = 2.0f + 5.0f * (.5f + .5f * std::sin(animation_ * .45f + i * 1.8f));
            g.DrawLine(&wave, 90.0f + i * 3, 22 - h * .5f, 90.0f + i * 3, 22 + h * .5f);
        }
    }
}
SIZE VoiceToolbar::HintSize(const wchar_t* text, bool status) const {
    using namespace Gdiplus;
    Bitmap image(1, 1, PixelFormat32bppPARGB); Graphics g(&image);
    // Measure in DIPs; only PaintSurface/Render applies the HWND's DPI scale.
    Setup(g);
    const int fontSize = status ? 10 : 12, padding = status ? 16 : 20;
    Font font(L"Microsoft YaHei UI", static_cast<REAL>(fontSize), FontStyleRegular, UnitPixel);
    StringFormat format; HintFormat(format); RectF bounds;
    if (g.MeasureString(text, -1, &font, PointF(0, 0), &format, &bounds) != Ok)
        bounds = RectF(0, 0, static_cast<REAL>(wcslen(text) * fontSize), static_cast<REAL>(fontSize + 4));
    return {std::max(status ? 0 : Width, static_cast<int>(std::ceil(bounds.Width)) + padding) + Margin * 2,
        std::max(status ? 22 : 30, static_cast<int>(std::ceil(bounds.Height)) + (status ? 4 : 10)) + Margin * 2 + (status ? 0 : HintTail)};
}
void VoiceToolbar::PaintHint(Gdiplus::Graphics& g, const wchar_t* text, bool status, int width, int height) const {
    using namespace Gdiplus;
    Setup(g);
    const float bodyHeight = static_cast<float>(height - Margin * 2 - (status ? 0 : HintTail));
    PanelHint(g, static_cast<float>(width - Margin * 2), bodyHeight, status, noticeError_,
        state_ != VoiceToolbarState::Idle, tooltipTail_ > 0 ? tooltipTail_ : width / 2.0f);
    Font font(L"Microsoft YaHei UI", status ? 10.0f : 12.0f, FontStyleRegular, UnitPixel);
    StringFormat format; HintFormat(format);
    SolidBrush ink(!status ? Color(255, 46, 64, 100) : noticeError_ ? Color(255, 134, 85, 36) :
        state_ != VoiceToolbarState::Idle ? Color(255, 44, 114, 185) : Color(255, 83, 105, 145));
    const float inset = status ? 12.0f : 14.0f;
    g.DrawString(text, -1, &font, RectF(inset, 4, width - inset * 2, bodyHeight), &format, &ink);
}
void VoiceToolbar::PaintSurface(Gdiplus::Graphics& g, int width, int height, const wchar_t* text, bool status) const {
    g.ScaleTransform(dpi_ / 96.0f, dpi_ / 96.0f);
    if (text) PaintHint(g, text, status, width, height); else PaintMain(g);
}
bool VoiceToolbar::Render(HWND target, int width, int height, const wchar_t* text, bool status) {
    if (!target || !graphicsToken_) return false;
    const SIZE scaled = ScaledSize(width, height, dpi_);
    const int w = scaled.cx, h = scaled.cy;
    BITMAPINFO info{}; info.bmiHeader = {sizeof(BITMAPINFOHEADER), w, -h, 1, 32, BI_RGB};
    void* bits{}; HDC dc = CreateCompatibleDC(nullptr);
    if (!dc) return false;
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bitmap) { DeleteDC(dc); return false; }
    const HGDIOBJ old = SelectObject(dc, bitmap);
    std::memset(bits, 0, static_cast<size_t>(w) * h * 4);
    {
        // GDI+ writes premultiplied BGRA directly into the top-down DIB used by ULW_ALPHA.
        Gdiplus::Bitmap image(w, h, w * 4, PixelFormat32bppPARGB, static_cast<BYTE*>(bits));
        Gdiplus::Graphics graphics(&image); PaintSurface(graphics, width, height, text, status);
        graphics.Flush(Gdiplus::FlushIntentionSync);
    }
    POINT source{}; SIZE size{w, h}; BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
    const bool result = UpdateLayeredWindow(target, nullptr, nullptr, &size, dc, &source, 0, &blend, ULW_ALPHA) != FALSE;
    SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc);
    return result;
}
void VoiceToolbar::RenderMain() { if (eligible_) Render(window_, Width + Margin * 2, Height + Margin * 2); }
void VoiceToolbar::HideHints() {
    if (tooltip_) ShowWindow(tooltip_, SW_HIDE);
    if (notice_) ShowWindow(notice_, SW_HIDE);
}
void VoiceToolbar::RenderHints() {
    if (!eligible_ || !window_ || !IsWindowVisible(window_)) { HideHints(); return; }
    if (placing_ || placingHints_) return;
    PlacementGuard guard(placingHints_);
    MONITORINFO info{sizeof(info)};
    const bool hasWork = GetMonitorInfoW(MonitorFromWindow(window_, MONITOR_DEFAULTTONEAREST), &info) != FALSE;
    const SIZE tip = HintSize(TooltipText(), false), tipPixels = ScaledSize(tip.cx, tip.cy, dpi_);
    POINT p{position_.x + Pixels(74) - tipPixels.cx / 2, position_.y - tipPixels.cy + Pixels(Margin)};
    if (hasWork) p = ClampToWorkArea(p, tipPixels, info.rcWork);
    tooltipTail_ = (position_.x + Pixels(74) - p.x) * 96.0f / dpi_;
    SetWindowTextW(tooltip_, TooltipText()); Render(tooltip_, tip.cx, tip.cy, TooltipText());
    SetWindowPos(tooltip_, HWND_TOPMOST, p.x, p.y, tipPixels.cx, tipPixels.cy,
        SWP_NOACTIVATE | (tooltipReady_ && hovered_ ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    if (noticeText_.empty()) { ShowWindow(notice_, SW_HIDE); return; }
    const SIZE hint = HintSize(noticeText_.c_str(), true), hintPixels = ScaledSize(hint.cx, hint.cy, dpi_);
    p = {position_.x + (Pixels(Width + Margin * 2) - hintPixels.cx) / 2,
        position_.y + Pixels(Height + Margin * 2 + 4)};
    if (hasWork && p.y + hintPixels.cy > info.rcWork.bottom) p.y = position_.y - hintPixels.cy - Pixels(2);
    if (hasWork) p = ClampToWorkArea(p, hintPixels, info.rcWork);
    SetWindowTextW(notice_, noticeText_.c_str()); Render(notice_, hint.cx, hint.cy, noticeText_.c_str(), true);
    SetWindowPos(notice_, HWND_TOPMOST, p.x, p.y, hintPixels.cx, hintPixels.cy, SWP_NOACTIVATE | SWP_SHOWWINDOW);
}