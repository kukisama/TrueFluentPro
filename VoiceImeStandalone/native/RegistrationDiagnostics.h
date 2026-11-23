#pragma once
#include "RegistrationTrace.h"
#include <cstddef>
#include <cstring>

namespace VoiceRegistration {
// msctf.h from the installed SDK owns the ABI; no hand-packed structure/vtable.
inline std::string ProfileFields(const TF_INPUTPROCESSORPROFILE& p) {
    char numbers[256]{};
    std::snprintf(numbers, sizeof(numbers), "type=%lu langid=0x%04X hklSubstitute=%p dwCaps=0x%08lX hkl=%p dwFlags=0x%08lX exact=%d",
        p.dwProfileType, static_cast<unsigned>(p.langid), static_cast<void*>(p.hklSubstitute), p.dwCaps,
        static_cast<void*>(p.hkl), p.dwFlags, OwnProfile(p));
    return std::string(numbers) + " clsid=" + TraceText(GuidString(p.clsid)) +
        " guidProfile=" + TraceText(GuidString(p.guidProfile)) + " catid=" + TraceText(GuidString(p.catid));
}
inline HRESULT DiagnoseProfiles(ITfInputProcessorProfileMgr* manager, LANGID language) {
    char scope[48]{}; std::snprintf(scope, sizeof(scope), "EnumProfiles(langid=0x%04X)", static_cast<unsigned>(language));
    ComPtr<IEnumTfInputProcessorProfiles> items;
    HRESULT hr = manager->EnumProfiles(language, &items);
    Trace(scope, hr);
    unsigned total = 0, clsid = 0, profile = 0, lang = 0, exact = 0;
    if (hr == S_OK && !items) hr = E_UNEXPECTED;
    if (hr == S_OK) for (;;) {
        TF_INPUTPROCESSORPROFILE p{}; ULONG count = 0;
        hr = items->Next(1, &p, &count);
        if (hr == S_FALSE && !count) break;
        if (hr != S_OK || count != 1) { hr = FAILED(hr) ? hr : E_UNEXPECTED; break; }
        ++total; clsid += p.clsid == VoiceClsid; profile += p.guidProfile == VoiceProfile;
        lang += p.langid == 0x0804; exact += OwnProfile(p);
        if (p.clsid == VoiceClsid || p.guidProfile == VoiceProfile)
            TraceDetail(std::string(scope) + " ordinal=" + std::to_string(total - 1) + " " + ProfileFields(p));
    }
    char counts[240]{};
    std::snprintf(counts, sizeof(counts), " total=%u clsidMatches=%u profileGuidMatches=%u lang0804=%u exacttarget=%u terminalHRESULT=0x%08lX complete=%d",
        total, clsid, profile, lang, exact, static_cast<unsigned long>(hr), hr == S_FALSE);
    TraceDetail(std::string(scope) + counts);
    return hr == S_FALSE ? S_OK : ExactSuccess(hr);
}
inline HRESULT DiagnoseLanguages(ITfInputProcessorProfileMgr* manager) {
    ComPtr<ITfInputProcessorProfiles> legacy;
    HRESULT hr = manager->QueryInterface(IID_PPV_ARGS(&legacy));
    Trace("Diagnostics.ITfInputProcessorProfiles", hr);
    if (hr != S_OK || !legacy) return hr == S_OK ? E_UNEXPECTED : ExactSuccess(hr);
    LANGID current = 0;
    HRESULT currentHr = legacy->GetCurrentLanguage(&current);
    Trace("Diagnostics.GetCurrentLanguage", currentHr);
    char line[160]{};
    std::snprintf(line, sizeof(line), "GetCurrentLanguage langid=0x%04X dataValid=%d", static_cast<unsigned>(current), currentHr == S_OK);
    TraceDetail(line);
    LANGID* languages = nullptr; ULONG count = 0;
    hr = legacy->GetLanguageList(&languages, &count);
    // GetLanguageList allocates with the COM task allocator (Microsoft Learn).
    struct Release { LANGID* value; ~Release() { CoTaskMemFree(value); } } release{languages};
    Trace("Diagnostics.GetLanguageList", hr);
    bool contains = false;
    if (hr == S_OK && count && !languages) hr = E_UNEXPECTED;
    if (hr == S_OK) for (ULONG i = 0; i != count; ++i) {
        contains = contains || languages[i] == 0x0804;
        std::snprintf(line, sizeof(line), "GetLanguageList index=%lu langid=0x%04X", i, static_cast<unsigned>(languages[i]));
        TraceDetail(line);
    }
    std::snprintf(line, sizeof(line), "GetLanguageList count=%lu contains0804=%d dataValid=%d", count, contains, hr == S_OK);
    TraceDetail(line);
    return hr != S_OK ? ExactSuccess(hr) : ExactSuccess(currentHr);
}
inline const char* RegistryType(DWORD type) {
    switch (type) {
    case REG_NONE: return "REG_NONE";
    case REG_SZ: return "REG_SZ";
    case REG_EXPAND_SZ: return "REG_EXPAND_SZ";
    case REG_BINARY: return "REG_BINARY";
    case REG_DWORD: return "REG_DWORD";
    case REG_DWORD_BIG_ENDIAN: return "REG_DWORD_BIG_ENDIAN";
    case REG_LINK: return "REG_LINK";
    case REG_MULTI_SZ: return "REG_MULTI_SZ";
    case REG_QWORD: return "REG_QWORD";
    default: return "OTHER";
    }
}
inline std::string ProfileValueData(HKEY key, const wchar_t* name, DWORD type, DWORD bytes) {
    // Never read arbitrary value data, tokens, signatures, or application configuration.
    if (!_wcsicmp(name, L"Enable") && type == REG_DWORD && bytes == sizeof(DWORD)) {
        DWORD data = 0, actualType = 0, actualBytes = sizeof(data);
        LONG error = RegQueryValueExW(key, name, nullptr, &actualType, reinterpret_cast<BYTE*>(&data), &actualBytes);
        if (!error && actualType == REG_DWORD && actualBytes == sizeof(data) && data <= 1) return std::to_string(data);
        return "<unavailable-or-nonboolean>";
    }
    // The own description is safe only when it EXACTLY equals our compiled-in display name.
    if (type == REG_SZ && bytes == sizeof(DisplayName) &&
        (!*name || !_wcsicmp(name, L"Description"))) {
        wchar_t data[ARRAYSIZE(DisplayName)]{}; DWORD actualType = 0, actualBytes = sizeof(data);
        LONG error = RegQueryValueExW(key, name, nullptr, &actualType, reinterpret_cast<BYTE*>(data), &actualBytes);
        if (!error && actualType == REG_SZ && actualBytes == sizeof(data) &&
            !std::memcmp(data, DisplayName, sizeof(data))) return TraceText(DisplayName);
    }
    return "<redacted>"; // DLL paths and all other data are omitted, including stdout.
}
inline HRESULT DumpProfileTree(HKEY root, REGSAM view, const std::wstring& path, unsigned depth, unsigned& budget) {
    const auto location = std::string(root == HKEY_LOCAL_MACHINE ? "HKLM" : "HKCU") +
        " view=" + std::to_string(view == KEY_WOW64_64KEY ? 64 : 32) + " path=" + TraceText(path);
    HKEY opened{};
    LONG error = RegOpenKeyExW(root, path.c_str(), 0, KEY_READ | view, &opened);
    TraceDetail("ProfileKey " + location + " openError=" + std::to_string(error));
    if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) return S_OK;
    if (error) return HRESULT_FROM_WIN32(error);
    struct Close { HKEY key; ~Close() { RegCloseKey(key); } } close{opened};
    DWORD children = 0, values = 0;
    error = RegQueryInfoKeyW(opened, nullptr, nullptr, nullptr, &children, nullptr, nullptr, &values, nullptr, nullptr, nullptr, nullptr);
    if (error) return HRESULT_FROM_WIN32(error);
    TraceDetail("ProfileKey " + location + " values=" + std::to_string(values) + " children=" + std::to_string(children));
    for (DWORD i = 0;; ++i) {
        wchar_t name[16384]{}; DWORD length = ARRAYSIZE(name), type = 0, bytes = 0;
        error = RegEnumValueW(opened, i, name, &length, nullptr, &type, nullptr, &bytes);
        if (error == ERROR_NO_MORE_ITEMS) break;
        if (error) return HRESULT_FROM_WIN32(error);
        if (!budget) return HRESULT_FROM_WIN32(ERROR_MORE_DATA);
        --budget;
        TraceDetail("ProfileValue " + location + " name=" + TraceText(std::wstring(name, length)) +
            " type=" + RegistryType(type) + "(" + std::to_string(type) + ") bytes=" + std::to_string(bytes) +
            " data=" + ProfileValueData(opened, name, type, bytes));
    }
    if (depth >= 6 && children) return HRESULT_FROM_WIN32(ERROR_MORE_DATA);
    for (DWORD i = 0;; ++i) {
        wchar_t name[256]{}; DWORD length = ARRAYSIZE(name);
        error = RegEnumKeyExW(opened, i, name, &length, nullptr, nullptr, nullptr, nullptr);
        if (error == ERROR_NO_MORE_ITEMS) break;
        if (error) return HRESULT_FROM_WIN32(error);
        if (!budget) return HRESULT_FROM_WIN32(ERROR_MORE_DATA);
        --budget;
        HRESULT hr = DumpProfileTree(root, view, path + L"\\" + std::wstring(name, length), depth + 1, budget);
        if (FAILED(hr)) return hr;
    }
    return S_OK;
}
inline HRESULT RunTsfDiagnostics(ITfInputProcessorProfileMgr* manager) {
    std::wstring trace; if (!TraceFile(trace)) return HRESULT_FROM_WIN32(ERROR_NOT_READY);
    char abi[256]{};
    std::snprintf(abi, sizeof(abi), "SDK TF_INPUTPROCESSORPROFILE sizeof=%zu offsets:type=%zu,langid=%zu,clsid=%zu,profile=%zu,catid=%zu,hklSubstitute=%zu,dwCaps=%zu,hkl=%zu,dwFlags=%zu",
        sizeof(TF_INPUTPROCESSORPROFILE), offsetof(TF_INPUTPROCESSORPROFILE, dwProfileType), offsetof(TF_INPUTPROCESSORPROFILE, langid),
        offsetof(TF_INPUTPROCESSORPROFILE, clsid), offsetof(TF_INPUTPROCESSORPROFILE, guidProfile), offsetof(TF_INPUTPROCESSORPROFILE, catid),
        offsetof(TF_INPUTPROCESSORPROFILE, hklSubstitute), offsetof(TF_INPUTPROCESSORPROFILE, dwCaps),
        offsetof(TF_INPUTPROCESSORPROFILE, hkl), offsetof(TF_INPUTPROCESSORPROFILE, dwFlags));
    TraceDetail(abi);
    HRESULT result = S_OK;
    auto keep = [&](HRESULT hr) { if (hr != S_OK && result == S_OK) result = ExactSuccess(hr); };
    keep(DiagnoseLanguages(manager));
    keep(DiagnoseProfiles(manager, 0));
    keep(DiagnoseProfiles(manager, 0x0804));
    TF_INPUTPROCESSORPROFILE p{};
    HRESULT hr = manager->GetProfile(TF_PROFILETYPE_INPUTPROCESSOR, 0x0804, VoiceClsid, VoiceProfile, nullptr, &p);
    Trace("Diagnostics.GetProfile.Independent", hr);
    TraceDetail(std::string("GetProfile dataValid=") + (hr == S_OK ? "1 " : "0 ") + ProfileFields(p));
    // GetProfile E_FAIL is a diagnostic fact, not a reason to skip the registry dump.
    const auto fixedRoot = L"Software\\Microsoft\\CTF\\TIP\\" + GuidString(VoiceClsid) + L"\\LanguageProfile";
    for (HKEY root : {HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER}) for (REGSAM view : {KEY_WOW64_64KEY, KEY_WOW64_32KEY}) {
        unsigned budget = 4096;
        hr = DumpProfileTree(root, view, fixedRoot, 0, budget);
        Trace("Diagnostics.ProfileKeyDump", hr); keep(hr);
    }
    return result;
}
} // namespace VoiceRegistration