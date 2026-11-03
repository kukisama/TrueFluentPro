#pragma once
#include "Common.h"
#include "RegistrationTrace.h"
#include <cstdio>

namespace VoiceRegistration {
inline std::string FormatStatus(const RegistrationStatus& status) {
    char text[256]{};
    std::snprintf(text, sizeof(text), "{\"registered\":%s,\"state\":\"%s\",\"requiresAdmin\":%s,\"maintenance\":%s}",
        std::string(status.state) == "registered" ? "true" : "false", status.state,
        status.requiresAdmin ? "true" : "false", status.maintenance ? "true" : "false");
    return text;
}
inline std::string FormatFacts(const RegistrationFacts& f) {
    char text[256]{};
    std::snprintf(text, sizeof(text), "tsf-facts profile=%u enabled=%u keyboard=%u category=%u otherProfiles=%u otherCategories=%u registeredProfile=%u registeredKeyboard=%u active=%u",
        f.profile, f.enabled, f.keyboard, f.category, f.otherProfiles, f.otherCategories, f.registeredProfile, f.registeredKeyboard, f.active);
    return text;
}
inline bool ParseFacts(std::string text, RegistrationFacts& f) {
    auto last = text.find_last_not_of("\r\n");
    if (last == text.npos) return false;
    text.resize(last + 1);
    RegistrationFacts value;
    if (sscanf_s(text.c_str(), "tsf-facts profile=%u enabled=%u keyboard=%u category=%u otherProfiles=%u otherCategories=%u registeredProfile=%u registeredKeyboard=%u active=%u",
        &value.profile, &value.enabled, &value.keyboard, &value.category, &value.otherProfiles, &value.otherCategories,
        &value.registeredProfile, &value.registeredKeyboard, &value.active) != 9 ||
        value.profile > 1 || value.enabled > 1 || value.keyboard > 1 || value.category > 1 ||
        value.otherProfiles > 1 || value.otherCategories > 1 || value.registeredProfile > 1 || value.registeredKeyboard > 1 || value.active > 1 ||
        FormatFacts(value) != text) return false;
    f = value; return true;
}
struct QueryHandle {
    HANDLE value = nullptr;
    ~QueryHandle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
inline HRESULT FreshTsfProcess(const wchar_t* verb, std::string& output) {
    output.clear();
    // Same executable, same architecture/token, explicit read-only verb. No shell/elevation.
    wchar_t image[32768]{}; DWORD size = GetModuleFileNameW(nullptr, image, ARRAYSIZE(image));
    if (!size || size >= ARRAYSIZE(image)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    std::wstring exe(image, size);
    auto slash = exe.find_last_of(L'\\');
    if (slash == exe.npos || _wcsicmp(exe.c_str() + slash + 1, L"VoiceImeRegister.exe")) {
        // DLL registration exports can use their adjacent packaged helper; never relaunch the host.
        HMODULE module{};
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&QueryTsfRegistrationFacts), &module)) return HRESULT_FROM_WIN32(GetLastError());
        size = GetModuleFileNameW(module, image, ARRAYSIZE(image));
        if (!size || size >= ARRAYSIZE(image)) return E_FAIL;
        exe.assign(image, size); slash = exe.find_last_of(L'\\');
        if (slash == exe.npos) return E_FAIL;
        exe.resize(slash + 1); exe += L"VoiceImeRegister.exe";
        if (!AbsoluteFile(exe)) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    QueryHandle read, write, input, process, thread;
    if (!CreatePipe(&read.value, &write.value, &security, 0) ||
        !SetHandleInformation(read.value, HANDLE_FLAG_INHERIT, 0)) return HRESULT_FROM_WIN32(GetLastError());
    input.value = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &security,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (input.value == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = input.value; startup.hStdOutput = startup.hStdError = write.value;
    PROCESS_INFORMATION child{};
    std::wstring command = QuoteArgument(exe) + L" " + verb;
    if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
        nullptr, nullptr, &startup, &child)) return HRESULT_FROM_WIN32(GetLastError());
    process.value = child.hProcess; thread.value = child.hThread;
    CloseHandle(write.value); write.value = nullptr;
    DWORD wait = WaitForSingleObject(process.value, 5000);
    if (wait != WAIT_OBJECT_0) {
        TerminateProcess(process.value, ERROR_TIMEOUT); WaitForSingleObject(process.value, 1000);
        return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    }
    DWORD exit = 0;
    if (!GetExitCodeProcess(process.value, &exit)) return HRESULT_FROM_WIN32(GetLastError());
    if (exit != 0) return E_FAIL;
    char buffer[512]{}; DWORD count = 0;
    if (!ReadFile(read.value, buffer, sizeof(buffer), &count, nullptr) || !count || count == sizeof(buffer))
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    output.assign(buffer, count); return S_OK;
}
inline HRESULT FreshTsfFacts(RegistrationFacts& facts) {
    std::string output; HRESULT hr = FreshTsfProcess(L"--tsf-facts", output);
    return FAILED(hr) ? hr : ParseFacts(output, facts) ? S_OK : HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
}
inline void TraceFreshTsfDiagnostics(const char* checkpoint) noexcept {
    try {
        std::wstring path; if (!TraceFile(path)) return;
        Trace(checkpoint, S_OK);
        std::string output; HRESULT hr = FreshTsfProcess(L"--tsf-diagnostics", output);
        if (hr == S_OK && output != "tsf-diagnostics-complete\r\n" && output != "tsf-diagnostics-complete\n")
            hr = HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        Trace("FreshTsfDiagnostics", hr);
    } catch (...) { Trace("FreshTsfDiagnostics", E_OUTOFMEMORY); }
}
}