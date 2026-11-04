#pragma once
#include "RegistrationQuery.h"
#include <algorithm>
#include <cstdio>
#include <cstring>

namespace VoiceRegistration {

inline std::wstring RegistryTipValueName() {
    return L"0804:" + GuidString(VoiceClsid) + GuidString(VoiceProfile);
}

inline std::wstring ApiTipArgument() {
    return L"0x" + RegistryTipValueName();
}

inline std::wstring LowercaseAscii(const std::wstring& s) {
    std::wstring out = s;
    for (wchar_t& c : out) {
        if (c >= L'A' && c <= L'Z') c = static_cast<wchar_t>(c - L'A' + L'a');
    }
    return out;
}

inline bool IsTipLayoutPattern(const std::wstring& name) {
    // Matches ^[0-9a-f]{4}:(?:[0-9a-f]{8}|\{[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}\{[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\})$
    auto hex = [](const std::wstring& s, size_t pos, size_t len) {
        if (pos + len > s.size()) return false;
        for (size_t i = 0; i < len; ++i) {
            wchar_t c = s[pos + i];
            if (!((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F'))) return false;
        }
        return true;
    };
    auto guid = [&](const std::wstring& s, size_t pos) {
        return pos + 38 <= s.size() && s[pos] == L'{' && hex(s, pos + 1, 8) && s[pos + 9] == L'-' &&
            hex(s, pos + 10, 4) && s[pos + 14] == L'-' && hex(s, pos + 15, 4) && s[pos + 19] == L'-' &&
            hex(s, pos + 20, 4) && s[pos + 24] == L'-' && hex(s, pos + 25, 12) && s[pos + 37] == L'}';
    };
    if (!hex(name, 0, 4) || name.size() < 5 || name[4] != L':') return false;
    if (name.size() == 13) return hex(name, 5, 8);
    if (name.size() == 81) return guid(name, 5) && guid(name, 43);
    return false;
}

inline bool IsSimplifiedChineseTag(const std::wstring& tag) {
    if (_wcsicmp(tag.c_str(), L"zh-Hans") == 0) return true;
    LCID lcid = LocaleNameToLCID(tag.c_str(), 0);
    return lcid == 0x0804;
}

inline bool ReadMultiSzLanguages(HKEY key, std::vector<std::wstring>& tags) {
    tags.clear();
    DWORD type = 0, bytes = 0;
    LONG err = RegQueryValueExW(key, L"Languages", nullptr, &type, nullptr, &bytes);
    if (err != ERROR_SUCCESS || type != REG_MULTI_SZ || bytes < sizeof(wchar_t) || bytes % sizeof(wchar_t) != 0)
        return false;
    std::vector<wchar_t> buf(bytes / sizeof(wchar_t));
    err = RegQueryValueExW(key, L"Languages", nullptr, &type, reinterpret_cast<BYTE*>(buf.data()), &bytes);
    if (err != ERROR_SUCCESS || type != REG_MULTI_SZ) return false;
    size_t count = bytes / sizeof(wchar_t);
    size_t start = 0;
    while (start < count && buf[start] != L'\0') {
        size_t end = start;
        while (end < count && buf[end] != L'\0') ++end;
        tags.emplace_back(buf.data() + start, end - start);
        start = end + 1;
    }
    return !tags.empty();
}

inline bool AppendKeySnapshot(const std::wstring& subPath, const std::vector<std::wstring>* filterNames,
    bool languageMode, const std::wstring& targetPath, const std::wstring& ownRegistryTip, std::wstring& out) {
    HKEY key{};
    LONG err = RegOpenKeyExW(HKEY_CURRENT_USER, subPath.c_str(), 0, KEY_READ | KEY_WOW64_64KEY, &key);
    out += L"[Key:" + subPath + L"|Exists:";
    if (err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND) {
        out += L"0]";
        return true;
    }
    if (err != ERROR_SUCCESS) return false;
    out += L"1|";

    struct ValEntry {
        std::wstring sortKey;
        std::wstring name;
        DWORD type = 0;
        std::vector<BYTE> data;
    };
    std::vector<ValEntry> entries;
    for (DWORD idx = 0;; ++idx) {
        wchar_t nameBuf[16384]{};
        DWORD nameLen = ARRAYSIZE(nameBuf), type = 0, dataLen = 0;
        err = RegEnumValueW(key, idx, nameBuf, &nameLen, nullptr, &type, nullptr, &dataLen);
        if (err == ERROR_NO_MORE_ITEMS) break;
        if (err != ERROR_SUCCESS) { RegCloseKey(key); return false; }
        std::wstring name(nameBuf, nameLen);
        if (filterNames) {
            bool match = false;
            for (const auto& f : *filterNames) if (name == f) { match = true; break; }
            if (!match) continue;
        }
        if (languageMode) {
            if (_wcsicmp(name.c_str(), L"CachedLanguageName") == 0) continue;
            if (_wcsicmp(subPath.c_str(), targetPath.c_str()) == 0 &&
                _wcsicmp(name.c_str(), ownRegistryTip.c_str()) == 0) continue;
        }
        std::vector<BYTE> data(dataLen);
        nameLen = ARRAYSIZE(nameBuf);
        err = RegEnumValueW(key, idx, nameBuf, &nameLen, nullptr, &type,
            dataLen ? data.data() : nullptr, &dataLen);
        if (err != ERROR_SUCCESS) { RegCloseKey(key); return false; }
        data.resize(dataLen);
        entries.push_back({LowercaseAscii(name), name, type, std::move(data)});
    }
    RegCloseKey(key);

    std::sort(entries.begin(), entries.end(), [](const ValEntry& a, const ValEntry& b) {
        return a.sortKey != b.sortKey ? a.sortKey < b.sortKey : a.name < b.name;
    });

    struct TipRank { DWORD rank; std::wstring sortKey; std::wstring name; };
    std::vector<TipRank> tips;
    for (const auto& e : entries) {
        if (languageMode && e.type == REG_DWORD && e.data.size() == sizeof(DWORD) && IsTipLayoutPattern(e.name)) {
            DWORD val = 0;
            std::memcpy(&val, e.data.data(), sizeof(DWORD));
            tips.push_back({val, e.sortKey, e.name});
        } else {
            wchar_t hdr[64]{};
            std::swprintf(hdr, ARRAYSIZE(hdr), L"%lu:%zu:", static_cast<unsigned long>(e.type), e.data.size());
            out += e.name + L"=" + hdr;
            for (BYTE b : e.data) {
                wchar_t hx[4]{};
                std::swprintf(hx, ARRAYSIZE(hx), L"%02X", static_cast<unsigned>(b));
                out += hx;
            }
            out += L";";
        }
    }
    std::sort(tips.begin(), tips.end(), [](const TipRank& a, const TipRank& b) {
        return a.rank != b.rank ? a.rank < b.rank : a.sortKey < b.sortKey;
    });
    out += L"|Tips:";
    for (const auto& t : tips) out += t.name + L",";
    out += L"]";
    return true;
}

inline bool SnapshotProtectedRegistry(const std::vector<std::wstring>& tags, const std::wstring& targetPath,
    const std::wstring& ownRegistryTip, std::wstring& snapshot) {
    snapshot.clear();
    const std::vector<std::wstring> rootFilter = {L"Languages", L"Default"};
    if (!AppendKeySnapshot(L"Control Panel\\International\\User Profile", &rootFilter, false, targetPath, ownRegistryTip, snapshot))
        return false;
    if (!AppendKeySnapshot(L"Keyboard Layout\\Preload", nullptr, false, targetPath, ownRegistryTip, snapshot))
        return false;
    if (!AppendKeySnapshot(L"Keyboard Layout\\Substitutes", nullptr, false, targetPath, ownRegistryTip, snapshot))
        return false;
    for (const auto& tag : tags) {
        if (!AppendKeySnapshot(L"Control Panel\\International\\User Profile\\" + tag, nullptr, true, targetPath, ownRegistryTip, snapshot))
            return false;
    }
    return true;
}

// Fresh 64-bit native process worker: never initializes TSF COM before calling input.dll!InstallLayoutOrTip.
inline int ExecuteNativeUserProfileBindingProcess(bool enable) {
#ifndef _WIN64
    (void)enable;
    return 10;
#else
    const std::wstring ownRegistryTip = RegistryTipValueName();
    const std::wstring apiTip = ApiTipArgument();

    HKEY rootKey{};
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Control Panel\\International\\User Profile",
        0, KEY_READ | KEY_WOW64_64KEY, &rootKey) != ERROR_SUCCESS) {
        return 11;
    }
    std::vector<std::wstring> tags;
    bool okLanguages = ReadMultiSzLanguages(rootKey, tags);
    RegCloseKey(rootKey);
    if (!okLanguages) return 11;

    std::vector<std::wstring> targets;
    for (const auto& tag : tags) {
        if (IsSimplifiedChineseTag(tag)) targets.push_back(tag);
    }
    if (targets.size() != 1) return 11;

    const std::wstring targetPath = L"Control Panel\\International\\User Profile\\" + targets[0];
    HKEY targetKey{};
    if (RegOpenKeyExW(HKEY_CURRENT_USER, targetPath.c_str(), 0, KEY_READ | KEY_WOW64_64KEY, &targetKey) != ERROR_SUCCESS) {
        return 11;
    }
    DWORD tipType = 0, tipBytes = 0;
    bool ownControlProfile = RegQueryValueExW(targetKey, ownRegistryTip.c_str(), nullptr, &tipType, nullptr, &tipBytes) == ERROR_SUCCESS;
    RegCloseKey(targetKey);

    std::wstring before, checkBefore;
    if (!SnapshotProtectedRegistry(tags, targetPath, ownRegistryTip, before) ||
        !SnapshotProtectedRegistry(tags, targetPath, ownRegistryTip, checkBefore) ||
        before != checkBefore) {
        return 12;
    }

    if (!enable && !ownControlProfile) {
        // Exact registry entry absent: read-only skip, not proof of TSF detachment.
        return 1;
    }

    HMODULE inputModule = LoadLibraryExW(L"input.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!inputModule) return 15;
    using InstallLayoutOrTipFn = BOOL(WINAPI*)(LPCWSTR psz, DWORD dwFlags);
    auto installFn = reinterpret_cast<InstallLayoutOrTipFn>(GetProcAddress(inputModule, "InstallLayoutOrTip"));
    if (!installFn) {
        FreeLibrary(inputModule);
        return 15;
    }

    BOOL changed = FALSE;
    try {
        // Flags: 0 (enable) or 1 (ILOT_UNINSTALL).
        changed = installFn(apiTip.c_str(), enable ? 0u : 1u);
    } catch (...) {
        FreeLibrary(inputModule);
        return 14;
    }
    FreeLibrary(inputModule);

    std::wstring after;
    if (!SnapshotProtectedRegistry(tags, targetPath, ownRegistryTip, after) || after != before) {
        return 16;
    }
    return changed ? 0 : 13;
#endif
}

inline HRESULT ResolveNative64BitRegisterHelper(std::wstring& exePath) {
    exePath.clear();
    wchar_t image[32768]{};
    DWORD size = GetModuleFileNameW(nullptr, image, ARRAYSIZE(image));
    if (!size || size >= ARRAYSIZE(image)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    std::wstring current(image, size);
#ifdef _WIN64
    auto slash = current.find_last_of(L'\\');
    if (slash != current.npos && _wcsicmp(current.c_str() + slash + 1, L"VoiceImeRegister.exe") == 0 && AbsoluteFile(current)) {
        exePath = current;
        return S_OK;
    }
#endif
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&QueryTsfRegistrationFacts), &module)) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    size = GetModuleFileNameW(module, image, ARRAYSIZE(image));
    if (!size || size >= ARRAYSIZE(image)) return E_FAIL;
    std::wstring modPath(image, size);

    auto findX64Helper = [](const std::wstring& baseRoot, std::wstring& out) -> bool {
        const std::wstring x64Dir = baseRoot + L"\\win-x64";
        const std::wstring direct = x64Dir + L"\\VoiceImeRegister.exe";
        if (AbsoluteFile(direct)) { out = direct; return true; }
        WIN32_FIND_DATAW fd{};
        HANDLE hFind = FindFirstFileW((x64Dir + L"\\*").c_str(), &fd);
        if (hFind == INVALID_HANDLE_VALUE) return false;
        do {
            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) &&
                wcscmp(fd.cFileName, L".") != 0 && wcscmp(fd.cFileName, L"..") != 0) {
                std::wstring candidate = x64Dir + L"\\" + fd.cFileName + L"\\VoiceImeRegister.exe";
                if (AbsoluteFile(candidate)) {
                    FindClose(hFind);
                    out = candidate;
                    return true;
                }
            }
        } while (FindNextFileW(hFind, &fd));
        FindClose(hFind);
        return false;
    };

    std::wstring cur = modPath;
    for (int depth = 0; depth < 4; ++depth) {
        auto pos = cur.find_last_of(L'\\');
        if (pos == cur.npos) break;
        cur.resize(pos);
        auto parentPos = cur.find_last_of(L'\\');
        if (parentPos == cur.npos) break;
        std::wstring folder = cur.substr(parentPos + 1);
        if (_wcsicmp(folder.c_str(), L"win-x64") == 0 || _wcsicmp(folder.c_str(), L"win-x86") == 0) {
            std::wstring pkgRoot = cur.substr(0, parentPos);
            if (findX64Helper(pkgRoot, exePath)) return S_OK;
        }
    }
    return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
}

inline HRESULT ChangeUserProfileBinding(bool enable) {
    wchar_t windows[MAX_PATH]{};
    UINT size = GetWindowsDirectoryW(windows, ARRAYSIZE(windows));
    if (!size) return HRESULT_FROM_WIN32(GetLastError());
    if (size >= ARRAYSIZE(windows)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
#ifndef _WIN64
    BOOL wow64 = FALSE;
    if (!IsWow64Process(GetCurrentProcess(), &wow64)) return HRESULT_FROM_WIN32(GetLastError());
    if (!wow64) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
#endif
    std::wstring exe;
    HRESULT resolveHr = ResolveNative64BitRegisterHelper(exe);
    if (FAILED(resolveHr)) return resolveHr;

    std::wstring command = QuoteArgument(exe) + (enable ? L" --bind-user-tip enable" : L" --bind-user-tip disable");
    if (command.size() >= 32767) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);

    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    QueryHandle io, process, thread;
    io.value = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (io.value == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = startup.hStdOutput = startup.hStdError = io.value;
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
        nullptr, windows, &startup, &child)) return HRESULT_FROM_WIN32(GetLastError());
    process.value = child.hProcess; thread.value = child.hThread;
    const DWORD wait = WaitForSingleObject(process.value, 8000);
    if (wait != WAIT_OBJECT_0) {
        const DWORD error = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
        const BOOL terminated = TerminateProcess(process.value, ERROR_TIMEOUT);
        const DWORD terminateError = terminated ? ERROR_SUCCESS : GetLastError();
        const DWORD stopped = WaitForSingleObject(process.value, 1000);
        Trace("UserProfileBinding.TerminationConfirmed", stopped == WAIT_OBJECT_0 ? S_OK : E_FAIL);
        if (stopped != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(terminated ? ERROR_PROCESS_ABORTED : terminateError);
        return HRESULT_FROM_WIN32(error); // No timeout retry; a killed ILOT has unknown completion.
    }
    DWORD exit = 0;
    if (!GetExitCodeProcess(process.value, &exit)) return HRESULT_FROM_WIN32(GetLastError());
    HRESULT hr;
    const char* phase;
    switch (exit) {
    case 0: hr = S_OK; phase = "Applied"; break;
    case 1: hr = enable ? HRESULT_FROM_WIN32(ERROR_INVALID_DATA) : S_OK; phase = "AbsentSkip"; break;
    case 10: hr = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED); phase = "Host"; break;
    case 11: hr = HRESULT_FROM_WIN32(ERROR_NOT_FOUND); phase = "TargetPreflight"; break;
    case 12: hr = HRESULT_FROM_WIN32(ERROR_RETRY); phase = "ProtectedPreflight"; break;
    case 13: hr = E_FAIL; phase = "ApiFalse"; break;
    case 14: hr = E_FAIL; phase = "ApiException"; break;
    case 15: hr = E_FAIL; phase = "ApiInitialize"; break;
    case 16: hr = HRESULT_FROM_WIN32(ERROR_RETRY); phase = "ProtectedPostcheck"; break;
    default: hr = HRESULT_FROM_WIN32(ERROR_INVALID_DATA); phase = "UnexpectedExit"; break;
    }
    TraceDetail(std::string(enable ? "UserProfileBinding.Enable" : "UserProfileBinding.Disable") +
        " phase=FreshTargetTip." + phase + " code=" + std::to_string(exit) + " otherInputPostcheck=external-required");
    return hr;
}
} // namespace VoiceRegistration