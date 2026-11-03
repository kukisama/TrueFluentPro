#pragma once
#include "RegistrationPolicy.h"
#include <cstdio>

namespace VoiceRegistration {
// Optional test-driver journal: append only to an already-created local file.
// Contains API facts and component-only diagnostics, never application config/credentials.
inline bool TraceFile(std::wstring& path) {
    wchar_t buffer[32768]{};
    DWORD size = GetEnvironmentVariableW(L"TRUEFLUENT_VOICE_IME_TRACE", buffer, ARRAYSIZE(buffer));
    if (!size || size >= ARRAYSIZE(buffer)) return false;
    path.assign(buffer, size);
    return path.size() > 3 && path[1] == L':' && path[2] == L'\\';
}
inline void Trace(const char* api, HRESULT hr, const Snapshot* s = nullptr) {
    std::wstring path; if (!TraceFile(path)) return;
    HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    char line[768]{};
    int size = std::snprintf(line, sizeof(line), "pid=%lu bits=%zu stage=%s HRESULT=0x%08lX",
        GetCurrentProcessId(), sizeof(void*) * 8, api, static_cast<unsigned long>(hr));
    if (s) size += std::snprintf(line + size, sizeof(line) - size,
        " com=%d%d tip=%d%d keys=%d otherProfiles=%d profile=%d enabled=%d keyboard=%d category=%d otherCategories=%d registeredProfile=%d registeredKeyboard=%d userTIP=%d%d settingsOnly=%d%d settingsDisabled=%d%d active=%d ownedCOM=%d%d",
        s->machine[0].exists, s->machine[1].exists, s->machineTip[0], s->machineTip[1],
        s->profileKeys, s->otherProfiles, s->profile, s->enabled, s->keyboard, s->category, s->otherCategories,
        s->registeredProfile, s->registeredKeyboard, s->legacyTip[0].exists, s->legacyTip[1].exists,
        s->legacyTip[0].settingsOnly, s->legacyTip[1].settingsOnly, s->legacyTip[0].settingsDisabled,
        s->legacyTip[1].settingsDisabled, s->active, s->machine[0].owned, s->machine[1].owned);
    if (size > 0 && size < static_cast<int>(sizeof(line) - 2)) {
        line[size++] = '\r'; line[size++] = '\n'; DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(size), &written, nullptr);
    }
    CloseHandle(file);
}
inline std::string TraceText(const std::wstring& text) {
    std::string result = "\"";
    for (wchar_t c : text) {
        if (c >= 32 && c < 127 && c != L'"' && c != L'\\') result += static_cast<char>(c);
        else { char escaped[7]{}; std::snprintf(escaped, sizeof(escaped), "\\u%04X", static_cast<unsigned>(c)); result += escaped; }
    }
    return result + '"'; // One physical log line even for hostile registry names.
}
inline void TraceDetail(const std::string& detail) {
    std::wstring path; if (!TraceFile(path)) return;
    HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    BY_HANDLE_FILE_INFORMATION info{};
    if (GetFileInformationByHandle(file, &info) && !(info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))) {
        char prefix[96]{};
        std::snprintf(prefix, sizeof(prefix), "pid=%lu bits=%zu diagnostic ", GetCurrentProcessId(), sizeof(void*) * 8);
        const auto line = std::string(prefix) + detail + "\r\n";
        DWORD written = 0;
        if (line.size() <= 262144) WriteFile(file, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    }
    CloseHandle(file);
}
inline void TraceIndependentProbe() {
    std::wstring path; if (!TraceFile(path)) return;
    wchar_t image[32768]{}; DWORD size = GetModuleFileNameW(nullptr, image, ARRAYSIZE(image));
    if (!size || size >= ARRAYSIZE(image)) return;
    std::wstring exe(image, size);
    auto slash = exe.find_last_of(L'\\');
    if (slash == exe.npos || !Equal(exe.substr(slash + 1), L"VoiceImeRegister.exe")) return;
    std::wstring command = QuoteArgument(exe) + L" --probe";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION process{};
    if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
        nullptr, nullptr, &startup, &process)) { Trace("IndependentProbe.Start", HRESULT_FROM_WIN32(GetLastError())); return; }
    DWORD wait = WaitForSingleObject(process.hProcess, 5000), code = STILL_ACTIVE;
    if (wait == WAIT_OBJECT_0) GetExitCodeProcess(process.hProcess, &code);
    else { TerminateProcess(process.hProcess, ERROR_TIMEOUT); WaitForSingleObject(process.hProcess, 1000); }
    Trace("IndependentProbe.ExitCode", static_cast<HRESULT>(code));
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
}
}