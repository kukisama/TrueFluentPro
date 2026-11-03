#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <msctf.h>
#include <inputscope.h>
#include <wrl/client.h>
#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <vector>
#include <cstdint>
#include <new>
#include <deque>
#include <limits>

using Microsoft::WRL::ComPtr;
inline constexpr CLSID VoiceClsid = {0x5638e6e5,0xd18c,0x4fe8,{0xb7,0x78,0xe5,0x9a,0x51,0x70,0xb4,0x81}};
inline constexpr GUID VoiceProfile = {0xa91272e1,0x9973,0x43f9,{0x8e,0xb4,0x7d,0x54,0x94,0xec,0x92,0xb1}};
inline constexpr wchar_t ConfigKey[] = L"Software\\TrueFluentPro\\VoiceIme";
inline constexpr wchar_t DisplayName[] = L"译见pro输入法";
inline constexpr UINT FrameMessage = WM_APP + 0x581;
inline constexpr DWORD MaximumFrame = 65536;
inline std::wstring GuidString(REFGUID guid) {
    wchar_t text[40]{}; StringFromGUID2(guid, text, 40); return text;
}
inline bool IsSuppressedWindow(HWND hwnd) {
    if (!hwnd) return false;
    wchar_t cls[64]{};
    if (!GetClassNameW(hwnd, cls, 64)) return false;
    return _wcsicmp(cls, L"TscShellContainerClass") == 0 ||
           _wcsicmp(cls, L"IHWindowClass") == 0 ||
           _wcsicmp(cls, L"TaskManagerWindow") == 0;
}
inline bool AbsoluteFile(const std::wstring& path) {
    // Deliberately reject UNC/device paths: configuration must name a local executable/DLL.
    if (path.size() < 3 || path[1] != L':' || path[2] != L'\\' || path.find(L'"') != path.npos) return false;
    DWORD a = GetFileAttributesW(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}
inline std::wstring QuoteArgument(const std::wstring& value) {
    std::wstring result = L"\""; size_t slashes = 0;
    for (wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        result.append(slashes * (c == L'"' ? 2 : 1), L'\\'); slashes = 0;
        if (c == L'"') result += L'\\';
        result += c;
    }
    result.append(slashes * 2, L'\\'); return result + L'"';
}
inline bool DecodeUtf8(const char* data, size_t count, std::wstring& result) {
    result.clear(); if (!count) return true;
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, static_cast<int>(count), nullptr, 0);
    if (!n) return false;
    result.resize(n);
    return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, data, static_cast<int>(count), result.data(), n) == n;
}
inline bool ValidKind(char kind) { return kind == 'P' || kind == 'F' || kind == 'R' || kind == 'E' || kind == 'S'; }
inline bool ExtendedValidKind(char kind) { return ValidKind(kind) || kind == 'I' || kind == 'C'; }
inline bool SharedCommandKind(char kind) {
    return kind == 'L' || kind == 'B' || kind == 'U' || kind == 'T' || kind == 'D';
}
inline bool Decimal(const std::wstring& text, size_t first, size_t end, uint64_t& value) {
    value = 0;
    if (first == end) return false;
    for (size_t i = first; i < end; ++i) {
        const wchar_t c = text[i];
        if (c < L'0' || c > L'9') return false;
        const unsigned digit = c - L'0';
        if (value > (std::numeric_limits<uint64_t>::max() - digit) / 10) return false;
        value = value * 10 + digit;
    }
    return true;
}
inline bool SharedPayload(char kind, const std::wstring& payload, uint64_t& epoch, uint64_t& target, std::wstring& text) {
    if (!ExtendedValidKind(kind)) return false;
    const size_t first = payload.find(L':');
    const size_t second = first == payload.npos ? payload.npos : payload.find(L':', first + 1);
    if (second == payload.npos || !Decimal(payload, 0, first, epoch) || !Decimal(payload, first + 1, second, target)) return false;
    if ((kind == 'P' || kind == 'F') ? target == 0 : target != 0) return false;
    text = kind == 'P' || kind == 'F' ? payload.substr(second + 1) : std::wstring{};
    return true;
}
struct KeyLatch {
    bool down = false;
    bool TestDown(bool eligible, WPARAM key) const { return key == VK_F8 && (down || eligible); }
    bool Press(bool eligible, WPARAM key, LPARAM flags, bool& toggle) {
        toggle = false;
        if (!TestDown(eligible, key)) return false;
        if (!down) { down = true; toggle = (flags & (static_cast<LPARAM>(1) << 30)) == 0; }
        return true;
    }
    bool TestUp(WPARAM key) const { return key == VK_F8 && down; }
    bool Release(WPARAM key) { if (!TestUp(key)) return false; down = false; return true; }
};
struct PipeRun {
    uint64_t generation;
    HANDLE cancel = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE commandReady = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE flushed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    const DWORD threadId = GetCurrentThreadId();
    struct Command { char kind; uint64_t generation; };
    std::mutex commandMutex;
    std::deque<Command> commands;
    std::mutex delivery;
    HWND target;
    std::atomic<unsigned> pending{0};
    std::atomic<bool> done{false};
    std::atomic<bool> workerStarted{false};
    std::atomic<uint64_t> audioEpoch{0};
    std::atomic<uint64_t> deselection{0}; // Finish a confirmed D even if Service tears down.
    std::wstring pipeName;
    PipeRun(uint64_t g, HWND w, std::wstring name) : generation(g), target(w), pipeName(std::move(name)) {}
    ~PipeRun() {
        if (cancel) CloseHandle(cancel);
        if (commandReady) CloseHandle(commandReady);
        if (flushed) CloseHandle(flushed);
    }
};
struct Frame {
    std::shared_ptr<PipeRun> run;
    char kind;
    std::wstring text;
    uint64_t audioEpoch = 0, targetGeneration = 0;
    ~Frame() { --run->pending; }
};
bool StartPipeWorker(const std::shared_ptr<PipeRun>& run);
bool SendSharedCommand(const std::shared_ptr<PipeRun>& run, char kind, uint64_t generation);
bool InstallationMaintenanceActive();
std::wstring NewPipeName();
HRESULT RegisterCurrentUser(const std::wstring& dll, const std::wstring* host = nullptr);
HRESULT UnregisterCurrentUser(const std::wstring& dll);
// Historical export entry names retained for ABI compatibility. Registration is machine-wide;
// only HostExe and input-method enablement belong to the invoking user. No implicit elevation.
struct RegistrationStatus {
    const char* state = "unregistered";
    bool requiresAdmin = true;
    bool maintenance = false;
};
struct RegistrationFacts {
    unsigned profile = 0, enabled = 0, keyboard = 0, category = 0, otherProfiles = 0, otherCategories = 0;
    unsigned registeredProfile = 0, registeredKeyboard = 0;
    unsigned active = 0;
};
HRESULT QueryTsfRegistrationFacts(RegistrationFacts& facts);
HRESULT QueryTsfRegistrationDiagnostics(); // Read-only, detailed data ONLY to opt-in own trace.
struct RegistrationError {
    const char* stage = "None";
    HRESULT hr = S_OK;
    const char* rollbackStage = "None";
    HRESULT rollback = S_OK;
};
RegistrationError LastRegistrationError();
HRESULT QueryRegistration(const std::wstring& dll, RegistrationStatus& status);
HRESULT ProbeRegistration(bool& present, bool& enabled, bool& keyboard);
bool RegistrationAlreadyAbsent();
extern HINSTANCE ModuleInstance;
extern std::atomic<long> ModuleObjects;