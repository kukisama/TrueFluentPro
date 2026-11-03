#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <string>

namespace Gdiplus { class Graphics; }
enum class VoiceToolbarState { Idle, Connecting, Recording };

// Thread-affine, presentation-only. The TSF service owns eligibility and the toggle.
class VoiceToolbar final {
public:
    using Toggle = void (*)(void*);
    using Settings = void (*)(void*);
    VoiceToolbar() = default;
    ~VoiceToolbar() { Destroy(); }
    VoiceToolbar(const VoiceToolbar&) = delete;
    VoiceToolbar& operator=(const VoiceToolbar&) = delete;
    bool Create(HINSTANCE instance, Toggle toggle, void* owner, Settings settings = nullptr);
    void Destroy();
    void SetEligible(bool eligible, HWND editor = nullptr, const RECT* editorRect = nullptr);
    void SetState(VoiceToolbarState state, bool stopped = false, bool error = false);
    HWND Handle() const { return window_; }
    const wchar_t* TooltipText() const;
private:
#ifdef VOICE_TOOLBAR_TEST
    friend struct VoiceToolbarTestAccess;
#endif
    HINSTANCE instance_{};
    HWND window_{}, tooltip_{}, notice_{};
    HICON icon_{};
    ULONG_PTR graphicsToken_{};
    std::wstring className_, noticeText_;
    Toggle toggle_{};
    Settings settings_{};
    void* owner_{};
    VoiceToolbarState state_ = VoiceToolbarState::Idle;
    UINT dpi_ = 96;
    bool eligible_ = false, anchored_ = false, hovered_ = false, pressed_ = false;
    bool gearHovered_ = false, gearPressed_ = false;
    bool dragging_ = false, placing_ = false, classRegistered_ = false;
    bool dpiPending_ = false, placingHints_ = false;
    bool tooltipReady_ = false, noticeError_ = false;
    float tooltipTail_ = 0; // Active button center in the positioned tooltip's DIPs.
    RECT dpiSuggested_{};
    POINT position_{}, dragPoint_{};
    struct { float x{}, y{}; } dragOffset_; // DIPs, not pixels from the previous monitor.
    HWND editor_{};
    HMONITOR editorMonitor_{};
    UINT editorDpi_{};
    unsigned animation_ = 0;
    static LRESULT CALLBACK WindowProc(HWND, UINT, WPARAM, LPARAM);
    LRESULT Message(HWND, UINT, WPARAM, LPARAM);
    bool MicAt(LPARAM point) const;
    bool GearAt(LPARAM point) const;
    bool BrandAt(LPARAM point) const;
    static SIZE ScaledSize(int width, int height, UINT dpi);
    static POINT ClampToWorkArea(POINT point, SIZE size, const RECT& work);
    bool FollowEditor(HMONITOR target, HMONITOR current, bool targetChanged) const;
    POINT DragPosition(POINT cursor, UINT dpi) const;
    int Pixels(int dip) const;
    void Place(POINT position);
    void Hover(bool hovered, bool gearHovered = false);
    void UpdateTimers();
    void PaintMain(Gdiplus::Graphics& graphics) const;
    SIZE HintSize(const wchar_t* text, bool status) const;
    void PaintHint(Gdiplus::Graphics& graphics, const wchar_t* text, bool status, int width, int height) const;
    void PaintSurface(Gdiplus::Graphics& graphics, int width, int height, const wchar_t* text, bool status) const;
    bool Render(HWND target, int width, int height, const wchar_t* text = nullptr, bool status = false);
    void RenderMain();
    void HideHints();
    void RenderHints();
};