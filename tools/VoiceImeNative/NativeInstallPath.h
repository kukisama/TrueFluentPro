#pragma once
#include "Common.h"

// Machine installation fact only. Never consult environment variables/HKCU/voice credentials.
inline HRESULT NativeProgramFiles(std::wstring& path) {
    path.clear(); HKEY key{};
    LONG error = RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion",
        0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key);
    if (error) return HRESULT_FROM_WIN32(error);
    wchar_t text[32768]{}; DWORD bytes = sizeof(text), type = 0;
    error = RegQueryValueExW(key, L"ProgramFilesDir", nullptr, &type, reinterpret_cast<BYTE*>(text), &bytes);
    RegCloseKey(key);
    if (error) return HRESULT_FROM_WIN32(error);
    if (type != REG_SZ || bytes < 4 * sizeof(wchar_t) || bytes > sizeof(text) || bytes % sizeof(wchar_t) ||
        text[bytes / sizeof(wchar_t) - 1]) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    path.assign(text, bytes / sizeof(wchar_t) - 1);
    if (path.find(L'\0') != path.npos || !((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) ||
        path[1] != L':' || path[2] != L'\\' || path.find_first_of(L"\"/") != path.npos)
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    while (!path.empty() && path.back() == L'\\') path.pop_back();
    return S_OK;
}
inline std::wstring MaintenancePath(const std::wstring& programFiles) {
    return programFiles.empty() ? L"" : programFiles + L"\\TrueFluentPro\\VoiceIme\\maintenance.lock";
}
inline bool MaintenanceActiveAtPath(const std::wstring& path) {
    if (path.empty()) return true; // Unknown root/state is never usable.
    DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes != INVALID_FILE_ATTRIBUTES) return true; // Any existing node blocks; no content is read.
    DWORD error = GetLastError();
    return error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND;
}
inline bool InstallationMaintenanceActive() {
    try {
        // All hash versions and both bitnesses share one protected installation-root marker.
        static const std::wstring path = [] {
            std::wstring root;
            return NativeProgramFiles(root) == S_OK ? MaintenancePath(root) : std::wstring{};
        }();
        return MaintenanceActiveAtPath(path);
    } catch (...) { return true; }
}