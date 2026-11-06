#include "VoiceToolbar.h"
#include "VoiceBrand.h"
#include <windowsx.h>
#include <algorithm>
#include <cmath>
#include <cstring>

namespace {
constexpr int Margin = 4, Width = 256, Height = 36;
constexpr int ConfirmationWidth = 392, ConfirmationHeight = 224;
constexpr wchar_t PrivacyText[] = L"开启后，之后的定稿原文将保存在本机数据库。能够访问数据库文件的人可能读到内容。关闭原文保存不会删除已有记录。字数、句数和录音时长统计不受此开关影响。";
constexpr wchar_t DisableHistoryText[] = L"关闭后，之后的定稿原文将不再保存。已有原文记录不会删除。字数、句数和录音时长仍会继续统计，但不会因此保存原文。";
constexpr wchar_t HistoryUnavailableText[] = L"字数与原文存储暂不可用，请重启输入法后台后重试";
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
constexpr wchar_t ToolbarRegKey[] = L"Software\\TrueFluentPro\\VoiceImeUI";
bool LoadSavedToolbarPosition(POINT& out) {
#ifdef VOICE_TOOLBAR_TEST
    (void)out;
    return false;
#else
    HKEY key{};
    if (RegOpenKeyExW(HKEY_CURRENT_USER, ToolbarRegKey, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS) return false;
    DWORD x = 0, y = 0, sx = sizeof(x), sy = sizeof(y), tx = 0, ty = 0;
    const bool ok = RegQueryValueExW(key, L"ToolbarX", nullptr, &tx, reinterpret_cast<BYTE*>(&x), &sx) == ERROR_SUCCESS &&
                    RegQueryValueExW(key, L"ToolbarY", nullptr, &ty, reinterpret_cast<BYTE*>(&y), &sy) == ERROR_SUCCESS &&
                    tx == REG_DWORD && ty == REG_DWORD && sx == sizeof(DWORD) && sy == sizeof(DWORD);
    RegCloseKey(key);
    if (!ok) return false;
    out = {static_cast<LONG>(static_cast<int32_t>(x)), static_cast<LONG>(static_cast<int32_t>(y))};
    return true;
#endif
}
void SaveToolbarPosition(POINT pt) {
#ifdef VOICE_TOOLBAR_TEST
    (void)pt;
#else
    HKEY key{};
    if (RegCreateKeyExW(HKEY_CURRENT_USER, ToolbarRegKey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS) return;
    const DWORD x = static_cast<DWORD>(static_cast<int32_t>(pt.x));
    const DWORD y = static_cast<DWORD>(static_cast<int32_t>(pt.y));
    RegSetValueExW(key, L"ToolbarX", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&x), sizeof(x));
    RegSetValueExW(key, L"ToolbarY", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&y), sizeof(y));
    RegCloseKey(key);
#endif
}
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

bool VoiceToolbar::Create(HINSTANCE instance, Toggle toggle, void* owner, Settings settings, History history, Usage usage, Language language) {
    if (window_ || graphicsToken_) return false;
    instance_ = instance; toggle_ = toggle; settings_ = settings; history_ = history; owner_ = owner;
    usage_ = usage; language_ = language;
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
        // Modeless and nonactivating: no nested modal loop, foreground restoration,
        // editor change or recording callback. Stop/bridge Close remain dispatchable.
        confirmation_ = CreateWindowExW(style, className_.c_str(), L"原文保存确认", WS_POPUP,
            0, 0, 1, 1, window_, nullptr, instance, this);
    }
    icon_ = static_cast<HICON>(LoadImageW(instance, MAKEINTRESOURCEW(101), IMAGE_ICON, 32, 32, 0));
    if (!window_ || !tooltip_ || !notice_ || !confirmation_ || !icon_) { Destroy(); return false; }
    dpi_ = WindowDpi(window_);
    if (!Render(window_, Width + Margin * 2, Height + Margin * 2)) { Destroy(); return false; }
    return true;
}
void VoiceToolbar::Destroy() {
    eligible_ = false; toggle_ = nullptr; settings_ = nullptr; history_ = nullptr; owner_ = nullptr;
    usage_ = nullptr; language_ = nullptr;
    DismissConfirmation();
    if (window_) {
        for (UINT_PTR timer : {AnimationTimer, HoverTimer, ToastTimer}) KillTimer(window_, timer);
        if (GetCapture() == window_) ReleaseCapture();
    }
    if (tooltip_) DestroyWindow(tooltip_);
    if (notice_) DestroyWindow(notice_);
    if (confirmation_) DestroyWindow(confirmation_);
    if (window_) DestroyWindow(window_);
    window_ = tooltip_ = notice_ = confirmation_ = nullptr;
    if (icon_) DestroyIcon(icon_); icon_ = nullptr;
    if (classRegistered_) UnregisterClassW(className_.c_str(), instance_);
    classRegistered_ = false; className_.clear();
    if (graphicsToken_) Gdiplus::GdiplusShutdown(graphicsToken_); graphicsToken_ = 0;
    instance_ = nullptr; anchored_ = hovered_ = pressed_ = gearHovered_ = gearPressed_ = dragging_ = placing_ = false;
    historyHovered_ = historyPressed_ = usageHovered_ = usagePressed_ = false;
    languageHovered_ = languagePressed_ = false;
    historyEnabled_ = historyAvailable_ = historyPending_ = historyRequested_ = false;
    state_ = VoiceToolbarState::Idle; noticeText_.clear();
    tooltipReady_ = noticeError_ = false; tooltipTail_ = 0;
    noticeDeadline_ = tooltipDeadline_ = 0;
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
    if (historyHovered_) return historyPending_ ? L"正在保存原文设置" : !historyAvailable_ ? L"字数与原文存储暂不可用" :
        historyEnabled_ ? L"关闭原文保存（需确认，保留已有记录）" : L"开启原文保存（需确认，本机数据库）";
    return usageHovered_ ? L"查看语音输入用量" : languageHovered_ ? L"调整识别语言和翻译" : gearHovered_ ? L"打开输入法设置" : L"";
}
void VoiceToolbar::SetEligible(bool eligible, HWND editor, const RECT* rect) {
    if (!window_) return;
    const bool changed = eligible_ != eligible; eligible_ = eligible;
    if (!eligible) {
        tooltipReady_ = false;
        noticeDeadline_ = tooltipDeadline_ = 0;
        hovered_ = pressed_ = gearHovered_ = gearPressed_ = dragging_ = false;
        historyHovered_ = historyPressed_ = usageHovered_ = usagePressed_ = false;
        languageHovered_ = languagePressed_ = false;
        DismissConfirmation();
        if (GetCapture() == window_) ReleaseCapture();
        for (UINT_PTR timer : {AnimationTimer, HoverTimer, ToastTimer}) KillTimer(window_, timer);
        HideHints(); ShowWindow(window_, SW_HIDE);
        // Do not replay a previous session's transient toast after switching back.
        noticeText_.clear();
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
        if (!anchored_ && LoadSavedToolbarPosition(point)) {
            const HMONITOR savedMon = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            const UINT savedDpi = WindowDpi(window_);
            MONITORINFO info{sizeof(info)};
            if (GetMonitorInfoW(savedMon, &info))
                point = ClampToWorkArea(point, ScaledSize(Width + Margin * 2, Height + Margin * 2, savedDpi), info.rcWork);
        } else if (hasArea) {
            point = {area.left + MulDiv(20, static_cast<int>(editorDpi), 96),
                area.bottom - MulDiv(Height + Margin * 2 + 16, static_cast<int>(editorDpi), 96)};
            // An editor can straddle displays while its left edge is still on the
            // old one. Anchor inside the selected display, using the editor's DPI.
            MONITORINFO info{sizeof(info)};
            if (GetMonitorInfoW(target, &info))
                point = ClampToWorkArea(point, ScaledSize(Width + Margin * 2, Height + Margin * 2, editorDpi), info.rcWork);
        } else {
            POINT cursor{}; GetCursorPos(&cursor);
            const HMONITOR mon = MonitorFromPoint(cursor, MONITOR_DEFAULTTOPRIMARY);
            MONITORINFO info{sizeof(info)};
            const SIZE barSize = ScaledSize(Width + Margin * 2, Height + Margin * 2, dpi_);
            if (GetMonitorInfoW(mon, &info)) {
                point = {info.rcWork.right - barSize.cx - Pixels(48),
                         info.rcWork.bottom - barSize.cy - Pixels(16)};
                point = ClampToWorkArea(point, barSize, info.rcWork);
            } else {
                point = {cursor.x + Pixels(12), cursor.y + Pixels(24)};
            }
        }
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
    noticeText_ = state == VoiceToolbarState::Connecting ? L"正在连接" :
        state == VoiceToolbarState::Recording ? L"开始录音" :
        error ? L"语音连接失败，请按 F8 重试" : stopped ? L"已停止" : L"";
    if (!eligible_ && state == VoiceToolbarState::Idle) noticeText_.clear();
    noticeDeadline_ = noticeText_.empty() ? 0 : GetTickCount64() + (noticeError_ ? 3500 : 1000);
    UpdateTimers(); RenderMain(); RenderHints();
}
void VoiceToolbar::SetHistory(bool enabled, bool available) {
    // Saving sends no bool feedback; unavailable is loading or a final storage fault.
    const bool finished = historyPending_;
    const bool failed = finished && (!available || enabled != historyRequested_);
    // Repeated authoritative feedback is not a change. In particular, enabled
    // polling must not dismiss a pending confirmation to turn saving off.
    if (!available || historyEnabled_ != (enabled && available) || historyAvailable_ != available)
        DismissConfirmation();
    historyEnabled_ = enabled && available; historyAvailable_ = available;
    if (finished) {
        historyPending_ = false;
        ShowNotice(!available ? HistoryUnavailableText : failed ? L"原文设置保存失败，请重试" :
            enabled ? L"已开启原文保存" : L"已关闭原文保存", failed);
    }
    RenderMain(); RenderHints();
}
void VoiceToolbar::ShowNotice(const wchar_t* text, bool error) {
    noticeText_ = text; noticeError_ = error;
    noticeDeadline_ = GetTickCount64() + (error ? 3500 : 1500);
    UpdateTimers(); RenderHints();
}
void VoiceToolbar::DismissConfirmation() {
    confirming_ = false; confirmationTarget_ = false; confirmationPressed_ = 0;
    if (confirmation_) {
        if (GetCapture() == confirmation_) ReleaseCapture();
        ShowWindow(confirmation_, SW_HIDE);
    }
}
void VoiceToolbar::SubmitHistory(bool enabled) {
    if (!eligible_ || !IsWindowVisible(window_) || !historyAvailable_ || historyPending_ || !history_) return;
    historyPending_ = true; historyRequested_ = enabled;
    ShowNotice(L"正在保存原文设置"); RenderMain();
    // May destroy the toolbar. Do not touch this after the callback.
    const auto callback = history_; void* owner = owner_;
    callback(owner, enabled);
}
void VoiceToolbar::AnswerConfirmation(bool accepted) {
    const bool target = confirmationTarget_;
    const bool submit = confirming_ && accepted && historyEnabled_ != target;
    DismissConfirmation();
    if (submit) SubmitHistory(target);
}
void VoiceToolbar::ClickHistory() {
    if (historyPending_) { ShowNotice(L"正在保存原文设置"); return; }
    if (!historyAvailable_ || !history_) { ShowNotice(HistoryUnavailableText, true); return; }
    if (confirming_) { DismissConfirmation(); return; }
    Hover(false); confirmationTarget_ = !historyEnabled_; confirming_ = true; PlaceConfirmation();
}
const wchar_t* VoiceToolbar::ConfirmationTitle() const {
    return confirmationTarget_ ? L"开启原文保存？" : L"关闭原文保存？";
}
const wchar_t* VoiceToolbar::ConfirmationText() const {
    return confirmationTarget_ ? PrivacyText : DisableHistoryText;
}
const wchar_t* VoiceToolbar::ConfirmationButtonText(bool accepted) const {
    return accepted ? (confirmationTarget_ ? L"确认开启" : L"确认关闭") :
        (confirmationTarget_ ? L"暂不开启" : L"保持开启");
}
int VoiceToolbar::ConfirmationAt(LPARAM point) const {
    const int x = GET_X_LPARAM(point), y = GET_Y_LPARAM(point);
    if (y >= Pixels(176) && y < Pixels(208)) {
        if (x >= Pixels(152) && x < Pixels(256)) return 1;
        if (x >= Pixels(268) && x < Pixels(372)) return 2;
    }
    if (x >= Pixels(348) && x < Pixels(376) && y >= Pixels(16) && y < Pixels(44)) return 1;
    return 0;
}
void VoiceToolbar::PlaceConfirmation() {
    if (!confirming_ || !eligible_ || !IsWindowVisible(window_)) return;
    const SIZE size = ScaledSize(ConfirmationWidth, ConfirmationHeight, dpi_);
    POINT p{position_.x, position_.y - size.cy - Pixels(4)};
    MONITORINFO info{sizeof(info)};
    if (GetMonitorInfoW(MonitorFromWindow(window_, MONITOR_DEFAULTTONEAREST), &info))
        p = ClampToWorkArea(p, size, info.rcWork);
    SetWindowTextW(confirmation_, ConfirmationTitle());
    if (!Render(confirmation_, ConfirmationWidth, ConfirmationHeight)) { DismissConfirmation(); ShowNotice(L"无法显示原文确认，请重试", true); return; }
    SetWindowPos(confirmation_, HWND_TOPMOST, p.x, p.y, size.cx, size.cy, SWP_NOACTIVATE | SWP_SHOWWINDOW);
}
void VoiceToolbar::UpdateTimers() {
    if (!window_) return;
    if (eligible_ && state_ == VoiceToolbarState::Recording) SetTimer(window_, AnimationTimer, 80, nullptr);
    else KillTimer(window_, AnimationTimer);
    KillTimer(window_, ToastTimer);
    ExpireNotice();
    if (eligible_ && noticeDeadline_) {
        const ULONGLONG now = GetTickCount64();
        SetTimer(window_, ToastTimer, static_cast<UINT>(noticeDeadline_ > now ? noticeDeadline_ - now : 1), nullptr);
    }
}
void VoiceToolbar::ExpireNotice() {
    // KillTimer does not remove queued WM_TIMER messages. Only the current
    // monotonic deadline may dismiss a toast, including Idle -> Idle replacements.
    if (noticeDeadline_ && GetTickCount64() >= noticeDeadline_) {
        KillTimer(window_, ToastTimer); noticeDeadline_ = 0; noticeText_.clear();
        ShowWindow(notice_, SW_HIDE);
    }
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
    return x >= Pixels(Margin + 50) && x < Pixels(Margin + 96) && y >= Pixels(Margin) && y < Pixels(Margin + Height);
}
bool VoiceToolbar::GearAt(LPARAM point) const {
    const int x = GET_X_LPARAM(point), y = GET_Y_LPARAM(point);
    return x >= Pixels(220) && x < Pixels(260) && y >= Pixels(Margin) && y < Pixels(Margin + Height);
}
bool VoiceToolbar::HistoryAt(LPARAM point) const {
    const int x = GET_X_LPARAM(point), y = GET_Y_LPARAM(point);
    return x >= Pixels(100) && x < Pixels(140) && y >= Pixels(Margin) && y < Pixels(Margin + Height);
}
bool VoiceToolbar::UsageAt(LPARAM point) const {
    const int x = GET_X_LPARAM(point), y = GET_Y_LPARAM(point);
    return x >= Pixels(140) && x < Pixels(180) && y >= Pixels(Margin) && y < Pixels(Margin + Height);
}
bool VoiceToolbar::LanguageAt(LPARAM point) const {
    const int x = GET_X_LPARAM(point), y = GET_Y_LPARAM(point);
    return x >= Pixels(180) && x < Pixels(220) && y >= Pixels(Margin) && y < Pixels(Margin + Height);
}
bool VoiceToolbar::BrandAt(LPARAM point) const {
    const int x = GET_X_LPARAM(point), y = GET_Y_LPARAM(point);
    return x >= Pixels(Margin) && x < Pixels(Margin + 43) && y >= Pixels(Margin) && y < Pixels(Margin + Height);
}
void VoiceToolbar::Hover(bool hovered, bool gearHovered, bool historyHovered, bool usageHovered, bool languageHovered) {
    if (hovered_ == hovered && gearHovered_ == gearHovered && historyHovered_ == historyHovered && usageHovered_ == usageHovered &&
        languageHovered_ == languageHovered) return;
    hovered_ = hovered; gearHovered_ = gearHovered; tooltipReady_ = false;
    historyHovered_ = historyHovered; usageHovered_ = usageHovered;
    languageHovered_ = languageHovered;
    tooltipDeadline_ = 0;
    KillTimer(window_, HoverTimer);
    ShowWindow(tooltip_, SW_HIDE);
    if ((gearHovered || historyHovered || usageHovered || languageHovered) && eligible_ && IsWindowVisible(window_)) {
        tooltipDeadline_ = GetTickCount64() + 400;
        SetTimer(window_, HoverTimer, 400, nullptr);
    }
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
    if (message == WM_NCHITTEST && hwnd != window_ && hwnd != confirmation_) return HTTRANSPARENT;
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
        } else if ((hwnd == tooltip_ || hwnd == notice_ || hwnd == confirmation_) && !placing_ && !placingHints_) {
            // Owned hints use the main window's DPI and stay in its work area,
            // rather than independently scaling or recursively relaying suggestions.
            RenderHints();
        }
        return 0;
    }
    if (hwnd == confirmation_) {
        switch (message) {
        case WM_SHOWWINDOW:
            if (!w && confirming_) DismissConfirmation();
            return 0;
        case WM_WINDOWPOSCHANGED:
            if (l && (reinterpret_cast<const WINDOWPOS*>(l)->flags & SWP_HIDEWINDOW) && confirming_)
                DismissConfirmation();
            break;
        case WM_CLOSE: AnswerConfirmation(false); return 0;
        case WM_KEYDOWN:
            // No default-Yes or keyboard hook: typing in the editor cannot consent.
            if (w == VK_ESCAPE || w == VK_RETURN) AnswerConfirmation(false);
            return 0;
        case WM_LBUTTONDOWN:
            if (confirming_ && IsWindowVisible(hwnd)) { confirmationPressed_ = ConfirmationAt(l); SetCapture(hwnd); }
            return 0;
        case WM_LBUTTONUP: {
            const int button = confirmationPressed_; confirmationPressed_ = 0;
            if (GetCapture() == hwnd) ReleaseCapture();
            if (button && button == ConfirmationAt(l) && IsWindowVisible(hwnd)) AnswerConfirmation(button == 2);
            return 0;
        }
        case WM_CAPTURECHANGED: case WM_CANCELMODE:
            confirmationPressed_ = 0; return 0;
        }
        return DefWindowProcW(hwnd, message, w, l);
    }
    if (hwnd != window_) return DefWindowProcW(hwnd, message, w, l);
    switch (message) {
    case WM_SHOWWINDOW:
        // This message precedes visibility; never show hints from its TRUE branch.
        if (!w) { tooltipReady_ = false; tooltipDeadline_ = 0; KillTimer(window_, HoverTimer); HideHints(); }
        break;
    case WM_WINDOWPOSCHANGED:
        if (l) {
            const UINT flags = reinterpret_cast<const WINDOWPOS*>(l)->flags;
            if (flags & SWP_HIDEWINDOW) { tooltipReady_ = false; tooltipDeadline_ = 0; KillTimer(window_, HoverTimer); HideHints(); }
            else if (flags & SWP_SHOWWINDOW) RenderHints();
        }
        break;
    case WM_DISPLAYCHANGE: Place(position_); return 0;
    case WM_TIMER:
        if (w == AnimationTimer && eligible_ && state_ == VoiceToolbarState::Recording) { ++animation_; RenderMain(); }
        if (w == HoverTimer) {
            if (tooltipDeadline_ && GetTickCount64() >= tooltipDeadline_ &&
                eligible_ && (gearHovered_ || historyHovered_ || usageHovered_ || languageHovered_) && IsWindowVisible(window_)) {
                tooltipReady_ = !tooltipReady_;
                tooltipDeadline_ = tooltipReady_ ? GetTickCount64() + 1500 : 0;
                if (tooltipReady_) SetTimer(window_, HoverTimer, 1500, nullptr);
                else KillTimer(window_, HoverTimer);
            }
            RenderHints();
        }
        if (w == ToastTimer) ExpireNotice();
        return 0;
    case WM_MOUSEMOVE:
        if (dragging_) {
            POINT point{GET_X_LPARAM(l), GET_Y_LPARAM(l)}; ClientToScreen(hwnd, &point);
            dragPoint_ = point; Place(DragPosition(point, dpi_));
        } else {
            RECT bounds{}; POINT cursor{};
            if (GetWindowRect(hwnd, &bounds) && GetCursorPos(&cursor) && PtInRect(&bounds, cursor)) {
                TRACKMOUSEEVENT track{sizeof(track), TME_LEAVE, hwnd, 0}; TrackMouseEvent(&track);
            }
            const bool active = eligible_ && IsWindowVisible(window_);
            Hover(active && MicAt(l), active && GearAt(l), active && HistoryAt(l), active && UsageAt(l), active && LanguageAt(l));
        }
        return 0;
    case WM_MOUSELEAVE: Hover(false, false); return 0;
    case WM_LBUTTONDOWN:
        if (!eligible_ || !IsWindowVisible(window_)) return 0;
        if (BrandAt(l)) {
            dragging_ = true;
            dragOffset_ = {GET_X_LPARAM(l) * 96.0f / dpi_, GET_Y_LPARAM(l) * 96.0f / dpi_};
            dragPoint_ = {GET_X_LPARAM(l), GET_Y_LPARAM(l)}; ClientToScreen(hwnd, &dragPoint_);
            Hover(false, false); SetCapture(hwnd);
        } else if (MicAt(l)) { pressed_ = true; SetCapture(hwnd); RenderMain(); }
        else if (GearAt(l)) { gearPressed_ = true; SetCapture(hwnd); RenderMain(); }
        else if (HistoryAt(l)) { historyPressed_ = true; SetCapture(hwnd); RenderMain(); }
        else if (UsageAt(l)) { usagePressed_ = true; SetCapture(hwnd); RenderMain(); }
        else if (LanguageAt(l)) { languagePressed_ = true; SetCapture(hwnd); RenderMain(); }
        return 0;
    case WM_LBUTTONUP: {
        const bool wasDragging = dragging_;
        const bool invoke = eligible_ && IsWindowVisible(window_) && pressed_ && MicAt(l) &&
            (!confirming_ || state_ != VoiceToolbarState::Idle);
        const bool invokeGear = eligible_ && IsWindowVisible(window_) && gearPressed_ && GearAt(l);
        const bool invokeHistory = eligible_ && IsWindowVisible(window_) && historyPressed_ && HistoryAt(l);
        const bool invokeUsage = eligible_ && IsWindowVisible(window_) && usagePressed_ && UsageAt(l);
        const bool invokeLanguage = eligible_ && IsWindowVisible(window_) && languagePressed_ && LanguageAt(l);
        pressed_ = gearPressed_ = dragging_ = false;
        historyPressed_ = usagePressed_ = false;
        languagePressed_ = false;
        if (GetCapture() == hwnd) ReleaseCapture();
        if (wasDragging) SaveToolbarPosition(position_);
        RenderMain();
        // The callback may deactivate/destroy the TSF service. Do not touch this afterwards.
        const auto callback = toggle_;
        const auto settings = settings_;
        const auto usage = usage_;
        const auto language = language_;
        void* owner = owner_;
        if (invoke && callback) callback(owner);
        else if (invokeGear && settings) settings(owner);
        else if (invokeHistory) ClickHistory();
        else if (invokeUsage && usage) usage(owner);
        else if (invokeLanguage && language) language(owner);
        return 0;
    }
    case WM_CAPTURECHANGED:
    case WM_CANCELMODE:
        pressed_ = gearPressed_ = dragging_ = false;
        historyPressed_ = usagePressed_ = false;
        languagePressed_ = false;
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
    Pen divider(Color(255, 168, 194, 235), 1);
    g.DrawLine(&divider, 50.0f, 13.0f, 50.0f, 31.0f);
    g.DrawLine(&divider, 100.0f, 13.0f, 100.0f, 31.0f);
    const bool recording = state_ == VoiceToolbarState::Recording;
    const bool connecting = state_ == VoiceToolbarState::Connecting;
    // 18 x 80 ms = 1.44 s, a smooth soft pulse rather than an on/off alarm.
    const float breath = .5f - .5f * std::cos(animation_ * (6.2831853f / 18));
    if (recording) {
        const float radius = 14.0f + 1.5f * breath;
        SolidBrush outer(Color(static_cast<BYTE>(18 + 30 * breath), 239, 106, 106));
        SolidBrush inner(Color(static_cast<BYTE>(36 + 44 * breath), 239, 106, 106));
        g.FillEllipse(&outer, 74 - radius - 1, 22 - radius - 1, (radius + 1) * 2, (radius + 1) * 2);
        g.FillEllipse(&inner, 74 - radius, 22 - radius, radius * 2, radius * 2);
        Pen rim(Color(static_cast<BYTE>(90 + 95 * breath), 232, 99, 99), 1.0f);
        g.DrawEllipse(&rim, 74 - radius, 22 - radius, radius * 2, radius * 2);
    }
    if (recording || connecting || hovered_) {
        const BYTE red = static_cast<BYTE>(235 + 8 * breath), other = static_cast<BYTE>(99 + 10 * breath);
        SolidBrush halo(recording ? (pressed_ ? Color(255, 213, 82, 86) : Color(255, red, other, other)) :
            connecting ? Color(255, 39, 119, 255) : pressed_ ? Color(255, 210, 221, 237) : Color(255, 230, 237, 247));
        g.FillEllipse(&halo, 62.0f, 10.0f, 24.0f, 24.0f);
    }
    const Color mic = recording || connecting ? Color(255, 255, 255, 255) : Color(255, 83, 105, 145);
    SolidBrush ink(mic); GraphicsPath capsule; VoiceBrand::RoundRect(capsule, 70.5f, 13, 7, 11, 3.5f); g.FillPath(&ink, &capsule);
    Pen line(mic, 1.6f); line.SetStartCap(LineCapRound); line.SetEndCap(LineCapRound);
    g.DrawLine(&line, 67.5f, 21.0f, 67.5f, 23.0f); g.DrawLine(&line, 80.5f, 21.0f, 80.5f, 23.0f);
    g.DrawArc(&line, 67.5f, 16.5f, 13.0f, 13.0f, 0.0f, 180.0f); g.DrawLine(&line, 74.0f, 29.5f, 74.0f, 32.0f);
    if (recording) {
        Pen wave(Color(255, 232, 99, 99), 1.5f); wave.SetStartCap(LineCapRound); wave.SetEndCap(LineCapRound);
        for (int i = 0; i < 3; ++i) {
            const float h = 2.0f + 5.0f * (.5f + .5f * std::sin(animation_ * .45f + i * 1.8f));
            for (float x : {55.0f + i * 3, 93.0f - i * 3})
                g.DrawLine(&wave, x, 22 - h * .5f, x, 22 + h * .5f);
        }
    }
    const bool gearLit = gearHovered_ || gearPressed_;
    if (gearLit) {
        SolidBrush gearHalo(gearPressed_ ? Color(255, 22, 95, 228) : Color(255, 39, 119, 255));
        g.FillEllipse(&gearHalo, 228.0f, 10.0f, 24.0f, 24.0f);
    }
    const Color gearColor = gearLit ? Color(255, 255, 255, 255) : Color(255, 83, 105, 145);
    Pen gearPen(gearColor, 1.6f); gearPen.SetStartCap(LineCapRound); gearPen.SetEndCap(LineCapRound);
    constexpr float gx = 240.0f, gy = 22.0f;
    for (int i = 0; i < 8; ++i) {
        const float angle = i * 0.78539816f;
        const float c = std::cos(angle), s = std::sin(angle);
        g.DrawLine(&gearPen, gx + c * 5.2f, gy + s * 5.2f, gx + c * 7.6f, gy + s * 7.6f);
    }
    g.DrawEllipse(&gearPen, gx - 5.2f, gy - 5.2f, 10.4f, 10.4f);
    g.DrawEllipse(&gearPen, gx - 2.1f, gy - 2.1f, 4.2f, 4.2f);
    if (historyEnabled_) {
        SolidBrush blue(Color(255, 39, 119, 255)); g.FillEllipse(&blue, 108.0f, 10.0f, 24.0f, 24.0f);
    } else if (historyHovered_ || historyPressed_) {
        SolidBrush hover(Color(255, 222, 233, 247)); g.FillEllipse(&hover, 108.0f, 10.0f, 24.0f, 24.0f);
    }
    if (usageHovered_ || usagePressed_) {
        SolidBrush hover(Color(255, 222, 233, 247)); g.FillEllipse(&hover, 148.0f, 10.0f, 24.0f, 24.0f);
    }
    if (languageHovered_ || languagePressed_) {
        SolidBrush hover(Color(255, 222, 233, 247)); g.FillEllipse(&hover, 188.0f, 10.0f, 24.0f, 24.0f);
    }
    // A real Chinese glyph keeps “言” recognizable at small sizes, without icon-font dependencies.
    Font languageFont(L"Microsoft YaHei UI", 18, FontStyleBold, UnitPixel);
    StringFormat languageFormat; HintFormat(languageFormat);
    SolidBrush languageInk(Color(255, 83, 105, 145));
    g.DrawString(L"言", 1, &languageFont, RectF(188, 10, 24, 24), &languageFormat, &languageInk);
    // Exact Lucide file-text / chart-column geometry, 24-unit viewBox -> 18 DIPs.
    // https://github.com/lucide-icons/lucide/tree/ee8e10d746a10907205366cfd984922f0c58939a/icons
    // ISC License, Copyright (c) 2026 Lucide Icons and Contributors
    // Permission to use, copy, modify, and/or distribute this software for any
    // purpose with or without fee is hereby granted, provided that the above
    // copyright notice and this permission notice appear in all copies.
    // THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
    // WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
    // MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
    // ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
    // WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
    // ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
    // OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
    for (bool file : {true, false}) {
        const auto saved = g.Save(); g.TranslateTransform(file ? 111.0f : 151.0f, 13.0f); g.ScaleTransform(.75f, .75f);
        Pen pen(file && historyEnabled_ ? Color(255, 255, 255, 255) : Color(255, 83, 105, 145), 2);
        pen.SetStartCap(LineCapRound); pen.SetEndCap(LineCapRound); pen.SetLineJoin(LineJoinRound);
        GraphicsPath path;
        if (file) {
            // M6 22 a2 2 0 0 1-2-2 V4 a2 2 0 0 1 2-2 h8
            // a2.4 2.4 0 0 1 1.704.706 l3.588 3.588 A2.4 2.4 0 0 1 20 8 v12 a2 2 0 0 1-2 2 z
            path.AddArc(4, 18, 4, 4, 90, 90); path.AddLine(4, 20, 4, 4);
            path.AddArc(4, 2, 4, 4, 180, 90); path.AddLine(6, 2, 14, 2);
            path.AddArc(11.6f, 2.0f, 4.8f, 4.8f, 270.0f, 45.0f);
            path.AddLine(15.704f, 2.706f, 19.292f, 6.294f);
            path.AddArc(15.2f, 5.6f, 4.8f, 4.8f, 315, 45); path.AddLine(20, 8, 20, 20);
            path.AddArc(16, 18, 4, 4, 0, 90); path.CloseFigure();
            g.DrawPath(&pen, &path); path.Reset();
            path.AddLine(14, 2, 14, 7); path.AddArc(14, 6, 2, 2, 180, -90); path.AddLine(15, 8, 20, 8);
            g.DrawPath(&pen, &path);
            g.DrawLine(&pen, 10, 9, 8, 9); g.DrawLine(&pen, 16, 13, 8, 13); g.DrawLine(&pen, 16, 17, 8, 17);
        } else {
            // M3 3v16 a2 2 0 0 0 2 2 h16; M18 17V9; M13 17V5; M8 17v-3
            path.AddLine(3, 3, 3, 19); path.AddArc(3, 17, 4, 4, 180, -90); path.AddLine(5, 21, 21, 21);
            g.DrawPath(&pen, &path);
            g.DrawLine(&pen, 18, 17, 18, 9); g.DrawLine(&pen, 13, 17, 13, 5); g.DrawLine(&pen, 8, 17, 8, 14);
        }
        g.Restore(saved);
    }
    if (historyEnabled_) {
        SolidBrush rim(Color(255, 248, 251, 255)), blue(Color(255, 39, 119, 255));
        g.FillEllipse(&rim, 126.0f, 27.0f, 7.0f, 7.0f); g.FillEllipse(&blue, 127.0f, 28.0f, 5.0f, 5.0f);
    }
}
void VoiceToolbar::PaintConfirmation(Gdiplus::Graphics& g) const {
    using namespace Gdiplus;
    Setup(g); Panel(g, ConfirmationWidth - 8, ConfirmationHeight - 8, false);
    SolidBrush ink(Color(255, 46, 64, 100));
    Font title(L"Microsoft YaHei UI", 16, FontStyleBold, UnitPixel), text(L"Microsoft YaHei UI", 12, FontStyleRegular, UnitPixel);
    g.DrawString(ConfirmationTitle(), -1, &title, PointF(20, 20), &ink);
    g.DrawString(L"×", -1, &title, PointF(354, 20), &ink);
    StringFormat wrap; wrap.SetTrimming(StringTrimmingNone);
    g.DrawString(ConfirmationText(), -1, &text, RectF(20, 58, 352, 104), &wrap, &ink);
    StringFormat centered; HintFormat(centered);
    for (int button : {1, 2}) {
        const float x = button == 1 ? 152.0f : 268.0f;
        GraphicsPath shape; VoiceBrand::RoundRect(shape, x, 176, 104, 32, 6);
        SolidBrush fill(button == 1 ? Color(255, 226, 237, 252) : Color(255, 248, 251, 255));
        Pen border(Color(255, 83, 105, 145), button == 1 ? 2.0f : 1.0f); // Cancel is the default, never Yes.
        g.FillPath(&fill, &shape); g.DrawPath(&border, &shape);
        g.DrawString(ConfirmationButtonText(button == 2), -1, &text, RectF(x, 176, 104, 32), &centered, &ink);
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
    return {std::max(status ? 0 : 136, static_cast<int>(std::ceil(bounds.Width)) + padding) + Margin * 2,
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
        Gdiplus::Graphics graphics(&image);
        if (target == confirmation_) { graphics.ScaleTransform(dpi_ / 96.0f, dpi_ / 96.0f); PaintConfirmation(graphics); }
        else PaintSurface(graphics, width, height, text, status);
        graphics.Flush(Gdiplus::FlushIntentionSync);
    }
    POINT source{}; SIZE size{w, h}; BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
    const bool result = UpdateLayeredWindow(target, nullptr, nullptr, &size, dc, &source, 0, &blend, ULW_ALPHA) != FALSE;
    SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc);
    return result;
}
void VoiceToolbar::RenderMain() { if (eligible_) Render(window_, Width + Margin * 2, Height + Margin * 2); }
void VoiceToolbar::HideHints() {
    DismissConfirmation();
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
    const int anchorDip = historyHovered_ ? 120 : usageHovered_ ? 160 : languageHovered_ ? 200 : 240;
    POINT p{position_.x + Pixels(anchorDip) - tipPixels.cx / 2, position_.y - tipPixels.cy + Pixels(Margin)};
    if (hasWork) p = ClampToWorkArea(p, tipPixels, info.rcWork);
    tooltipTail_ = (position_.x + Pixels(anchorDip) - p.x) * 96.0f / dpi_;
    SetWindowTextW(tooltip_, TooltipText()); Render(tooltip_, tip.cx, tip.cy, TooltipText());
    SetWindowPos(tooltip_, HWND_TOPMOST, p.x, p.y, tipPixels.cx, tipPixels.cy,
        SWP_NOACTIVATE | (tooltipReady_ && *TooltipText() ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    PlaceConfirmation();
    ExpireNotice();
    if (noticeText_.empty()) { ShowWindow(notice_, SW_HIDE); return; }
    const SIZE hint = HintSize(noticeText_.c_str(), true), hintPixels = ScaledSize(hint.cx, hint.cy, dpi_);
    p = {position_.x + (Pixels(Width + Margin * 2) - hintPixels.cx) / 2,
        position_.y + Pixels(Height + Margin * 2 + 4)};
    if (hasWork && p.y + hintPixels.cy > info.rcWork.bottom) p.y = position_.y - hintPixels.cy - Pixels(2);
    if (hasWork) p = ClampToWorkArea(p, hintPixels, info.rcWork);
    SetWindowTextW(notice_, noticeText_.c_str()); Render(notice_, hint.cx, hint.cy, noticeText_.c_str(), true);
    SetWindowPos(notice_, HWND_TOPMOST, p.x, p.y, hintPixels.cx, hintPixels.cy, SWP_NOACTIVATE | SWP_SHOWWINDOW);
}