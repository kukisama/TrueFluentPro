#include "RegistrationPolicy.h"
#include "RegistrationTrace.h"
#include "RegistrationDiagnostics.h"
#include "RegistrationQuery.h"
#include "UserProfileBinding.h"
#include "NativeInstallPath.h"
#include <ktmw32.h>
#pragma comment(lib, "KtmW32.lib")

namespace {
using namespace VoiceRegistration;
#ifdef _WIN64
constexpr REGSAM Views[] = {KEY_WOW64_64KEY, KEY_WOW64_32KEY};
constexpr wchar_t ArchFolder[] = L"win-x64";
#else
constexpr REGSAM Views[] = {KEY_WOW64_32KEY, KEY_WOW64_64KEY};
constexpr wchar_t ArchFolder[] = L"win-x86";
#endif
thread_local RegistrationError lastError;
thread_local bool alreadyAbsent = false;
// libIME2 registers both Windows 8+ shell categories. This desktop-only adapter
// declares just the supported tray capability, not AppContainer/secure/COM-less.
const GUID SupportedCategories[] = {GUID_TFCAT_TIP_KEYBOARD, GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT};
bool SupportedCategory(REFGUID category) {
    for (const auto& own : SupportedCategories) if (category == own) return true;
    return false;
}
struct Key {
    HKEY value{};
    ~Key() { if (value) RegCloseKey(value); }
};
struct Transaction {
    HANDLE value = CreateTransaction(nullptr, nullptr, 0, 0, 0, 30000, nullptr);
    ~Transaction() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    HRESULT Commit() { return CommitTransaction(value) ? S_OK : HRESULT_FROM_WIN32(GetLastError()); }
};
bool Missing(LONG error) { return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND; }
std::wstring ComPath() { return L"Software\\Classes\\CLSID\\" + GuidString(VoiceClsid); }
std::wstring TipPath() { return L"Software\\Microsoft\\CTF\\TIP\\" + GuidString(VoiceClsid); }
// Used to identify the fixed profile. CTF keys are never created or rewritten here.
std::wstring LegacyProfilePath() { return TipPath() + L"\\LanguageProfile\\0x00000804\\" + GuidString(VoiceProfile); }
LONG Open(HKEY root, const std::wstring& path, REGSAM view, REGSAM access, HKEY* out, HANDLE tx = nullptr) {
    return tx ? RegOpenKeyTransactedW(root, path.c_str(), 0, access | view, out, tx, nullptr) :
        RegOpenKeyExW(root, path.c_str(), 0, access | view, out);
}
HRESULT Text(HKEY key, const wchar_t* name, std::wstring& out) {
    out.clear(); wchar_t text[32768]{}; DWORD size = sizeof(text), type = 0;
    LONG error = RegQueryValueExW(key, name, nullptr, &type, reinterpret_cast<BYTE*>(text), &size);
    if (Missing(error)) return S_OK;
    if (error) return HRESULT_FROM_WIN32(error);
    if (type != REG_SZ || size < sizeof(wchar_t) || size > sizeof(text) || size % sizeof(wchar_t) || text[size / sizeof(wchar_t) - 1])
        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    out.assign(text, size / sizeof(wchar_t) - 1);
    if (out.find(L'\0') != out.npos) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    return S_OK;
}
HRESULT ValueAt(HKEY root, const std::wstring& path, REGSAM view, const wchar_t* name, std::wstring& out, HANDLE tx = nullptr) {
    Key key; out.clear(); LONG error = Open(root, path, view, KEY_READ, &key.value, tx);
    return Missing(error) ? S_OK : error ? HRESULT_FROM_WIN32(error) : Text(key.value, name, out);
}
HRESULT ReadRecord(HKEY root, const std::wstring& path, REGSAM view, Record& record, HANDLE tx = nullptr) {
    record = {}; Key key; LONG error = Open(root, path, view, KEY_READ, &key.value, tx);
    if (Missing(error)) return S_OK;
    if (error) return HRESULT_FROM_WIN32(error);
    record.exists = true; std::wstring owner;
    HRESULT hr = Text(key.value, L"VoiceImeOwner", owner);
    record.owned = owner == Owner;
    return hr;
}
HRESULT ReadCom(HKEY root, REGSAM view, Record& record, HANDLE tx = nullptr) {
    HRESULT hr = ReadRecord(root, ComPath(), view, record, tx);
    if (FAILED(hr) || !record.exists) return hr;
    hr = ValueAt(root, ComPath() + L"\\InprocServer32", view, nullptr, record.path, tx);
    std::wstring model;
    if (SUCCEEDED(hr)) hr = ValueAt(root, ComPath() + L"\\InprocServer32", view, L"ThreadingModel", model, tx);
    record.apartment = model == L"Apartment";
    return hr;
}
HRESULT ReadHost(REGSAM view, Record& record, HANDLE tx = nullptr) {
    HRESULT hr = ReadRecord(HKEY_CURRENT_USER, ConfigKey, view, record, tx);
    if (FAILED(hr) || !record.owned) return hr; // Do not inspect foreign application settings.
    hr = ValueAt(HKEY_CURRENT_USER, ConfigKey, view, L"HostExe", record.path, tx);
    if (SUCCEEDED(hr)) hr = ValueAt(HKEY_CURRENT_USER, ConfigKey, view, L"VoiceImeInstall", record.binding, tx);
    return hr;
}
HRESULT Write(HANDLE tx, HKEY root, const std::wstring& path, REGSAM view, const wchar_t* name, const std::wstring& value) {
    // Callers pass only the fixed machine CLSID path or fixed HKCU ConfigKey; no TSF write path.
    Key key; LONG error = RegCreateKeyTransactedW(root, path.c_str(), 0, nullptr, 0,
        KEY_READ | KEY_WRITE | view, nullptr, &key.value, nullptr, tx, nullptr);
    if (!error) error = RegSetValueExW(key.value, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    return HRESULT_FROM_WIN32(error);
}
HRESULT Remove(HANDLE tx, HKEY root, const std::wstring& path, REGSAM view) {
    Key key; LONG error = Open(root, path, view, KEY_READ | KEY_WRITE | DELETE, &key.value, tx);
    if (Missing(error)) return S_OK;
    if (error) return HRESULT_FROM_WIN32(error);
    error = RegDeleteTreeW(key.value, nullptr);
    if (error) return HRESULT_FROM_WIN32(error);
    RegCloseKey(key.value); key.value = nullptr;
    return HRESULT_FROM_WIN32(RegDeleteKeyTransactedW(root, path.c_str(), view, 0, tx, nullptr));
}
bool LocalPath(const std::wstring& path) {
    return path.size() > 3 && ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) &&
        path[1] == L':' && path[2] == L'\\' && path.find_first_of(L"\"/", 3) == path.npos &&
        path.find(L':', 2) == path.npos && path.find(L"\\..") == path.npos && path.find(L"\\.\\") == path.npos;
}
HRESULT ReadProfileKeys(REGSAM view, Snapshot& s, HANDLE tx = nullptr) {
    // Read every language/profile name under this fixed CLSID only. Hidden/non-enumerable
    // profiles must also prevent class-level deletion. Machine CTF is never manually removed.
    Key languages;
    LONG error = Open(HKEY_LOCAL_MACHINE, TipPath() + L"\\LanguageProfile", view, KEY_READ, &languages.value, tx);
    if (Missing(error)) return S_OK;
    if (error) return HRESULT_FROM_WIN32(error);
    for (DWORD i = 0;; ++i) {
        wchar_t language[256]{}; DWORD size = ARRAYSIZE(language);
        error = RegEnumKeyExW(languages.value, i, language, &size, nullptr, nullptr, nullptr, nullptr);
        if (error == ERROR_NO_MORE_ITEMS) break;
        if (error) return HRESULT_FROM_WIN32(error);
        Key profiles;
        error = Open(languages.value, language, view, KEY_READ, &profiles.value, tx);
        if (error) return HRESULT_FROM_WIN32(error);
        for (DWORD j = 0;; ++j) {
            wchar_t profile[256]{}; size = ARRAYSIZE(profile);
            error = RegEnumKeyExW(profiles.value, j, profile, &size, nullptr, nullptr, nullptr, nullptr);
            if (error == ERROR_NO_MORE_ITEMS) break;
            if (error) return HRESULT_FROM_WIN32(error);
            s.profileKeys = true;
            if (_wcsicmp(language, L"0x00000804") || _wcsicmp(profile, GuidString(VoiceProfile).c_str()))
                s.otherProfiles = true;
        }
    }
    return S_OK;
}
HRESULT UserEnableSettings(HKEY key, unsigned depth, bool& only, bool& disabled, bool& exactDisabled, HANDLE tx = nullptr) {
    // Measured InstallLayoutOrTip output: fixed class/LanguageProfile/lang/profile/Enable.
    // Do not mistake this official per-user override for the old owner-marked HKCU prototype.
    only = disabled = exactDisabled = false; DWORD children = 0, values = 0;
    LONG error = RegQueryInfoKeyW(key, nullptr, nullptr, nullptr, &children, nullptr, nullptr, &values,
        nullptr, nullptr, nullptr, nullptr);
    if (error) return HRESULT_FROM_WIN32(error);
    if (depth < 3) {
        if (values || children > 1) return S_OK;
        if (!children) { only = disabled = true; return S_OK; }
        wchar_t name[256]{}; DWORD size = ARRAYSIZE(name);
        error = RegEnumKeyExW(key, 0, name, &size, nullptr, nullptr, nullptr, nullptr);
        if (error) return HRESULT_FROM_WIN32(error);
        std::wstring expected = depth == 0 ? L"LanguageProfile" : depth == 1 ? L"0x00000804" : GuidString(VoiceProfile);
        if (_wcsicmp(name, expected.c_str())) return S_OK;
        Key child; error = Open(key, name, 0, KEY_READ, &child.value, tx);
        return error ? HRESULT_FROM_WIN32(error) : UserEnableSettings(child.value, depth + 1, only, disabled, exactDisabled, tx);
    }
    if (children || values > 1) return S_OK;
    if (!values) { only = disabled = true; return S_OK; }
    wchar_t name[256]{}; DWORD size = ARRAYSIZE(name), type = 0, data = 0, bytes = sizeof(data);
    error = RegEnumValueW(key, 0, name, &size, nullptr, &type, reinterpret_cast<BYTE*>(&data), &bytes);
    if (error) return HRESULT_FROM_WIN32(error);
    only = OwnEnableValue(name, type, bytes, data);
    disabled = only && data == 0;
    exactDisabled = disabled;
    return S_OK;
}
HRESULT ReadUserTip(REGSAM view, Record& tip, HANDLE tx = nullptr) {
    HRESULT hr = ReadRecord(HKEY_CURRENT_USER, TipPath(), view, tip, tx);
    if (FAILED(hr) || !tip.exists) return hr;
    if (tip.owned) return ValueAt(HKEY_CURRENT_USER, LegacyProfilePath(), view, L"IconFile", tip.path, tx);
    Key settings; LONG error = Open(HKEY_CURRENT_USER, TipPath(), view, KEY_READ, &settings.value, tx);
    return error ? HRESULT_FROM_WIN32(error) : UserEnableSettings(settings.value, 0,
        tip.settingsOnly, tip.settingsDisabled, tip.settingsExactDisabled, tx);
}
HRESULT Enumerate(ITfInputProcessorProfileMgr* manager, ITfCategoryMgr* categories, Snapshot& s) {
    s.profile = s.enabled = s.keyboard = s.category = s.active = false;
    s.registeredProfile = s.registeredKeyboard = false;
    TF_INPUTPROCESSORPROFILE registered{};
    HRESULT lookup = manager->GetProfile(TF_PROFILETYPE_INPUTPROCESSOR, 0x0804, VoiceClsid, VoiceProfile, nullptr, &registered);
    Trace("GetProfile", lookup);
    if (lookup == S_OK && registered.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && registered.clsid == VoiceClsid &&
        registered.guidProfile == VoiceProfile && registered.langid == 0x0804) {
        s.registeredProfile = true;
        s.registeredKeyboard = registered.catid == GUID_TFCAT_TIP_KEYBOARD;
    }
    ComPtr<IEnumTfInputProcessorProfiles> profiles;
    HRESULT hr = manager->EnumProfiles(0, &profiles); // all languages; exact target match below
    if (FAILED(hr) || !profiles) return FAILED(hr) ? hr : E_UNEXPECTED;
    unsigned matches = 0;
    for (;;) {
        TF_INPUTPROCESSORPROFILE p{}; ULONG count = 0;
        hr = profiles->Next(1, &p, &count);
        if (hr == S_FALSE && count == 0) break;
        if (hr != S_OK || count != 1) return FAILED(hr) ? hr : E_UNEXPECTED;
        if (p.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && p.clsid == VoiceClsid &&
            p.guidProfile == VoiceProfile && p.langid == 0x0804) {
            ++matches; s.profile = true; s.enabled = (p.dwFlags & TF_IPP_FLAG_ENABLED) != 0;
            s.keyboard = p.catid == GUID_TFCAT_TIP_KEYBOARD;
        }
        else if (p.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && p.clsid == VoiceClsid) s.otherProfiles = true;
    }
    if (matches > 1) return HRESULT_FROM_WIN32(ERROR_DUP_NAME);
    ComPtr<IEnumGUID> guids;
    hr = categories->EnumCategoriesInItem(VoiceClsid, &guids);
    if (FAILED(hr) || !guids) return FAILED(hr) ? hr : E_UNEXPECTED;
    for (;;) {
        GUID category{}; ULONG count = 0;
        hr = guids->Next(1, &category, &count);
        if (hr == S_FALSE && count == 0) break;
        if (hr != S_OK || count != 1) return FAILED(hr) ? hr : E_UNEXPECTED;
        if (SupportedCategory(category)) s.category = true;
        else s.otherCategories = true;
    }
    TF_INPUTPROCESSORPROFILE active{};
    hr = manager->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &active);
    Trace("GetActiveProfile", hr);
    if (hr != S_OK && hr != S_FALSE) return FAILED(hr) ? hr : E_UNEXPECTED;
    s.active = hr == S_OK && OwnProfile(active);
    return S_OK; // IsEnabledLanguageProfile is deliberately not an existence test.
}
class WindowsBackend final : public Backend {
    HANDLE mutex{};
    bool locked = false, initialized = false, uninitialize = false, changedTsf = false;
    ComPtr<ITfInputProcessorProfileMgr> profiles;
    ComPtr<ITfCategoryMgr> categories;
    HRESULT Managers() {
        if (!initialized) {
            HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
            if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) return hr;
            initialized = true; uninitialize = SUCCEEDED(hr);
        }
        // Fresh objects for each postcheck: do not trust a cached pre-registration enumeration.
        profiles.Reset(); categories.Reset();
        HRESULT hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&profiles));
        return FAILED(hr) ? hr : CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&categories));
    }
    HRESULT WriteManagers() { return profiles && categories ? S_OK : Managers(); }
public:
    HRESULT Diagnostics() {
        std::wstring trace; if (!TraceFile(trace)) return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        HRESULT hr = Managers();
        return hr == S_OK ? RunTsfDiagnostics(profiles.Get()) : ExactSuccess(hr);
    }
    HRESULT ReadTsf(Snapshot& s) {
        HRESULT hr = Managers();
        return FAILED(hr) ? hr : Enumerate(profiles.Get(), categories.Get(), s);
    }
    ~WindowsBackend() {
        categories.Reset(); profiles.Reset();
        if (uninitialize) CoUninitialize();
        if (locked) ReleaseMutex(mutex);
        if (mutex) CloseHandle(mutex);
    }
    HRESULT Elevated(bool& elevated) override {
        elevated = false; HANDLE token{};
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return HRESULT_FROM_WIN32(GetLastError());
        TOKEN_ELEVATION info{}; DWORD size = 0;
        BOOL ok = GetTokenInformation(token, TokenElevation, &info, sizeof(info), &size);
        DWORD error = ok ? ERROR_SUCCESS : GetLastError(); CloseHandle(token);
        if (ok) elevated = info.TokenIsElevated != 0;
        return HRESULT_FROM_WIN32(error);
    }
    HRESULT Lock() override {
        // Serializes the two helper processes before deciding which architecture is last.
        mutex = CreateMutexW(nullptr, FALSE, L"Global\\TrueFluentPro.VoiceIme.5638E6E5.Install");
        if (!mutex) return HRESULT_FROM_WIN32(GetLastError());
        DWORD wait = WaitForSingleObject(mutex, 10000);
        locked = wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED;
        return locked ? S_OK : HRESULT_FROM_WIN32(wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError());
    }
    HRESULT RecoveryMaintenance() override {
        if (!locked) return E_ACCESSDENIED;
        bool elevated = false;
        HRESULT hr = Elevated(elevated);
        if (hr != S_OK || !elevated) return FAILED(hr) ? hr : E_ACCESSDENIED;
        std::wstring root; hr = NativeProgramFiles(root);
        if (hr != S_OK) return ExactSuccess(hr);
        const DWORD attributes = GetFileAttributesW(MaintenancePath(root).c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES) return HRESULT_FROM_WIN32(GetLastError());
        // Unlike the listening guard, unknown/missing/directory/reparse is NOT positive proof.
        return attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT) ? E_ACCESSDENIED : S_OK;
    }
    HRESULT Read(Snapshot& s) override {
        s = {};
        for (unsigned i = 0; i != 2; ++i) {
            HRESULT hr = ReadCom(HKEY_LOCAL_MACHINE, Views[i], s.machine[i]); if (FAILED(hr)) return hr;
            hr = ReadCom(HKEY_CURRENT_USER, Views[i], s.user[i]); if (FAILED(hr)) return hr;
            Record tip;
            hr = ReadRecord(HKEY_LOCAL_MACHINE, TipPath(), Views[i], tip); if (FAILED(hr)) return hr;
            s.machineTip[i] = tip.exists;
            hr = ReadProfileKeys(Views[i], s); if (FAILED(hr)) return hr;
            hr = ReadUserTip(Views[i], s.legacyTip[i]); if (FAILED(hr)) return hr;
            hr = ReadHost(Views[i], s.host[i]); if (FAILED(hr)) return hr;
        }
        // Keep preflight and postchecks out of the writer's MSCTF process cache.
        // The --tsf-facts child calls ReadTsf directly (not Read), so this is not recursive.
        // A new installation initializes its first writer manager only after AddKeys commits COM.
        RegistrationFacts facts;
        HRESULT hr = FreshTsfFacts(facts);
        Trace("FreshTsfFacts", hr);
        // Measured: exact per-user Enable=0 was already persisted, but fresh
        // EnumProfiles briefly still returned ENABLED before becoming disabled.
        // Await read visibility only; do not repeat ILOT or relax the verdict.
        if (hr == S_OK && changedTsf && facts.enabled && !facts.active &&
            s.machine[0].owned && !s.machine[1].exists &&
            !s.otherProfiles && !facts.otherProfiles && !facts.otherCategories &&
            (s.legacyTip[0].settingsExactDisabled || s.legacyTip[1].settingsExactDisabled) &&
            (!s.legacyTip[0].exists || s.legacyTip[0].settingsExactDisabled) &&
            (!s.legacyTip[1].exists || s.legacyTip[1].settingsExactDisabled)) {
            const auto deadline = GetTickCount64() + 3000;
            while (hr == S_OK && facts.enabled && !facts.active && GetTickCount64() < deadline) {
                const DWORD wait = MsgWaitForMultipleObjectsEx(0, nullptr, 100, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                if (wait == WAIT_FAILED) { hr = HRESULT_FROM_WIN32(GetLastError()); break; }
                MSG message{};
                while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
                    TranslateMessage(&message); DispatchMessageW(&message);
                }
                hr = FreshTsfFacts(facts);
                Trace("AwaitUserDisableVisibility", hr);
            }
        }
        // Observed after successful teardown: disk registration/preferences are
        // gone, while a new TSF process briefly still returns the disabled item.
        // Await visibility only for this exact state; never retry a write API or
        // reinterpret cached presence as absence. Keep the COM anchor on timeout.
        if (hr == S_OK && changedTsf && !s.machineTip[0] && !s.machineTip[1] && !s.profileKeys &&
            !s.legacyTip[0].exists && !s.legacyTip[1].exists && s.machine[0].owned && !s.machine[1].exists) {
            const auto deadline = GetTickCount64() + 3000;
            while (hr == S_OK && (facts.profile || facts.registeredProfile) && !facts.enabled && !facts.active &&
                !facts.category && !facts.otherProfiles && !facts.otherCategories && GetTickCount64() < deadline) {
                const DWORD wait = MsgWaitForMultipleObjectsEx(0, nullptr, 100, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                if (wait == WAIT_FAILED) { hr = HRESULT_FROM_WIN32(GetLastError()); break; }
                MSG message{};
                while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
                    TranslateMessage(&message); DispatchMessageW(&message);
                }
                hr = FreshTsfFacts(facts);
                Trace("AwaitUnregisterVisibility", hr);
            }
        }
        if (SUCCEEDED(hr)) {
            s.profile = facts.profile != 0; s.enabled = facts.enabled != 0;
            s.keyboard = facts.keyboard != 0; s.category = facts.category != 0;
            s.otherProfiles = s.otherProfiles || facts.otherProfiles != 0;
            s.otherCategories = facts.otherCategories != 0;
            s.registeredProfile = facts.registeredProfile != 0;
            s.registeredKeyboard = facts.registeredKeyboard != 0;
            s.active = facts.active != 0;
        }
        Trace("Read", hr, &s);
        std::wstring tracePath;
        if (SUCCEEDED(hr) && profiles && s.profileKeys && TraceFile(tracePath)) {
            ComPtr<ITfInputProcessorProfiles> legacy;
            HRESULT diagnostic = profiles.As(&legacy);
            ComPtr<IEnumTfLanguageProfiles> items;
            if (SUCCEEDED(diagnostic)) diagnostic = legacy->EnumLanguageProfiles(0x0804, &items);
            Trace("EnumLanguageProfiles", diagnostic);
            if (SUCCEEDED(diagnostic) && items) {
                TF_LANGUAGEPROFILE p{}; ULONG count = 0; unsigned matches = 0;
                while (items->Next(1, &p, &count) == S_OK && count == 1)
                    if (p.clsid == VoiceClsid && p.guidProfile == VoiceProfile && p.langid == 0x0804) ++matches;
                Trace("EnumLanguageProfiles.MatchCount", static_cast<HRESULT>(matches));
            }
        }
        return hr;
    }
    HRESULT ValidateInstall(const std::wstring& dll, const std::wstring* host) override {
        if (!LocalPath(dll) || !AbsoluteFile(dll) || (host && (!LocalPath(*host) || !AbsoluteFile(*host)))) return E_INVALIDARG;
        // Read the native ProgramFilesDir in the explicit 64-bit view, including in an x86 helper.
        // FOLDERID_ProgramFilesX64 is NOT supported in a 32-bit process (Microsoft KNOWNFOLDERID docs).
        std::wstring programFiles;
        HRESULT hr = NativeProgramFiles(programFiles);
        if (FAILED(hr) || programFiles.empty()) return FAILED(hr) ? hr : E_FAIL;
        std::wstring prefix = programFiles + L"\\TrueFluentPro\\VoiceIme\\";
        std::wstring suffix = std::wstring(L"\\") + ArchFolder + L"\\TrueFluentVoiceIme.dll";
        if (dll.size() != prefix.size() + 64 + suffix.size() ||
            !Equal(dll.substr(0, prefix.size()), prefix) || !Equal(dll.substr(prefix.size() + 64), suffix)) return E_ACCESSDENIED;
        for (size_t i = prefix.size(); i < prefix.size() + 64; ++i)
            if (!((dll[i] >= L'0' && dll[i] <= L'9') || (dll[i] >= L'a' && dll[i] <= L'f') || (dll[i] >= L'A' && dll[i] <= L'F'))) return E_INVALIDARG;
        // Reject reparse-point redirection at the file or any install directory. No ACL changes.
        std::wstring part = dll;
        while (part.size() >= programFiles.size()) {
            DWORD a = GetFileAttributesW(part.c_str());
            if (a == INVALID_FILE_ATTRIBUTES) return HRESULT_FROM_WIN32(GetLastError());
            if (a & FILE_ATTRIBUTE_REPARSE_POINT) return E_ACCESSDENIED;
            auto slash = part.find_last_of(L'\\'); if (slash == part.npos) break; part.resize(slash);
        }
        // Only this fixed class is inspected. No registration export or host process is executed.
        HMODULE module = LoadLibraryExW(dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) return HRESULT_FROM_WIN32(GetLastError());
        using Factory = HRESULT (WINAPI*)(REFCLSID, REFIID, void**);
        auto get = reinterpret_cast<Factory>(GetProcAddress(module, "DllGetClassObject"));
        ComPtr<IClassFactory> factory;
        hr = get ? get(VoiceClsid, IID_PPV_ARGS(&factory)) : E_NOINTERFACE;
        if (hr == S_OK && !factory) hr = E_NOINTERFACE;
        factory.Reset(); FreeLibrary(module); return hr;
    }
    HRESULT AddKeys(const std::wstring& dll, const std::wstring* host, unsigned& added) override {
        added = 0; unsigned pending = 0; Transaction tx;
        if (tx.value == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        Record c; HRESULT hr = ReadCom(HKEY_LOCAL_MACHINE, Views[0], c, tx.value);
        if (FAILED(hr)) return hr;
        if (c.exists && (!c.owned || !Equal(c.path, dll) || !c.apartment)) return E_ACCESSDENIED;
        if (!c.exists) {
            const auto path = ComPath();
            hr = Write(tx.value, HKEY_LOCAL_MACHINE, path, Views[0], L"VoiceImeOwner", Owner);
            if (SUCCEEDED(hr)) hr = Write(tx.value, HKEY_LOCAL_MACHINE, path, Views[0], nullptr, DisplayName);
            if (SUCCEEDED(hr)) hr = Write(tx.value, HKEY_LOCAL_MACHINE, path + L"\\InprocServer32", Views[0], nullptr, dll);
            if (SUCCEEDED(hr)) hr = Write(tx.value, HKEY_LOCAL_MACHINE, path + L"\\InprocServer32", Views[0], L"ThreadingModel", L"Apartment");
            if (FAILED(hr)) return hr;
            pending |= ComKey;
        }
        if (host) for (unsigned i = 0; i != 2; ++i) {
            Record h; hr = ReadHost(Views[i], h, tx.value); if (FAILED(hr)) return hr;
            if (h.exists) {
                if (!h.owned || !Equal(h.path, *host) || (!h.binding.empty() && !Equal(h.binding, InstallRoot(dll)))) return E_ACCESSDENIED;
                continue; // Do not overwrite pre-existing HostExe, even if owned.
            }
            hr = Write(tx.value, HKEY_CURRENT_USER, ConfigKey, Views[i], L"VoiceImeOwner", Owner);
            if (SUCCEEDED(hr)) hr = Write(tx.value, HKEY_CURRENT_USER, ConfigKey, Views[i], L"HostExe", *host);
            if (SUCCEEDED(hr)) hr = Write(tx.value, HKEY_CURRENT_USER, ConfigKey, Views[i], L"VoiceImeInstall", InstallRoot(dll));
            if (FAILED(hr)) return hr;
            pending |= HostNative << i;
        }
        hr = tx.Commit(); if (SUCCEEDED(hr)) added = pending; return hr;
    }
    HRESULT RemoveKeys(const std::wstring& dll, const std::wstring* host, unsigned mask) override {
        if (!mask) return S_OK;
        Transaction tx; if (tx.value == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        HRESULT hr;
        if (mask & ComKey) {
            Record c; hr = ReadCom(HKEY_LOCAL_MACHINE, Views[0], c, tx.value); if (FAILED(hr)) return hr;
            if (c.exists && (!c.owned || !Equal(c.path, dll))) return E_ACCESSDENIED;
            hr = Remove(tx.value, HKEY_LOCAL_MACHINE, ComPath(), Views[0]); if (FAILED(hr)) return hr;
        }
        for (unsigned i = 0; i != 2; ++i) if (mask & (HostNative << i)) {
            Record h; hr = ReadHost(Views[i], h, tx.value); if (FAILED(hr)) return hr;
            if (!h.exists) continue; // HKCU Software is shared on supported Windows; delete at most once.
            if (!h.owned || !Equal(h.binding, InstallRoot(dll)) || (host && !Equal(h.path, *host))) return E_ACCESSDENIED;
            hr = Remove(tx.value, HKEY_CURRENT_USER, ConfigKey, Views[i]); if (FAILED(hr)) return hr;
        }
        return tx.Commit();
    }
    HRESULT RemoveLegacy(const std::wstring& dll, bool last, bool removeHost) override {
        Transaction tx; if (tx.value == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        Record c; HRESULT hr = ReadCom(HKEY_CURRENT_USER, Views[0], c, tx.value); if (FAILED(hr)) return hr;
        if (c.exists && (!c.owned || !Equal(c.path, dll))) return E_ACCESSDENIED;
        bool anchored = c.owned && Equal(c.path, dll);
        if (last) for (REGSAM view : Views) {
            Record other; hr = ReadCom(HKEY_CURRENT_USER, view, other, tx.value); if (FAILED(hr)) return hr;
            if (view != Views[0] && other.exists) return E_ACCESSDENIED;
            Record tip; hr = ReadRecord(HKEY_CURRENT_USER, TipPath(), view, tip, tx.value); if (FAILED(hr)) return hr;
            if (tip.exists) {
                hr = ValueAt(HKEY_CURRENT_USER, LegacyProfilePath(), view, L"IconFile", tip.path, tx.value);
                if (FAILED(hr) || !tip.owned || !Equal(tip.path, dll)) return FAILED(hr) ? hr : E_ACCESSDENIED;
                anchored = true;
                hr = Remove(tx.value, HKEY_CURRENT_USER, TipPath(), view); if (FAILED(hr)) return hr;
            }
        }
        if (!anchored) return E_ACCESSDENIED;
        hr = Remove(tx.value, HKEY_CURRENT_USER, ComPath(), Views[0]); if (FAILED(hr)) return hr;
        if (removeHost) for (REGSAM view : Views) {
            Record h; hr = ReadHost(view, h, tx.value); if (FAILED(hr)) return hr;
            if (!h.exists || !h.owned) continue;
            // Legacy ownership is anchored to the exact COM path verified above. New bindings must also match.
            if (!h.binding.empty() && !Equal(h.binding, InstallRoot(dll))) return E_ACCESSDENIED;
            hr = Remove(tx.value, HKEY_CURRENT_USER, ConfigKey, view); if (FAILED(hr)) return hr;
        }
        return tx.Commit();
    }
    HRESULT RegisterProfile(const std::wstring& dll) override {
        // Use the selected libIME2/Mozc registration recipe. Create managers only
        // after COM has been committed; preflight query objects are not writers.
        HRESULT hr = Managers();
        if (hr != S_OK) return ExactSuccess(hr);
        ComPtr<ITfInputProcessorProfiles> registration;
        hr = profiles.As(&registration);
        if (hr != S_OK) return ExactSuccess(hr);
        changedTsf = true;
        hr = registration->Register(VoiceClsid);
        Trace("RegisterTextService", hr);
        if (hr == S_OK) {
            hr = registration->AddLanguageProfile(VoiceClsid, 0x0804, VoiceProfile, DisplayName,
                static_cast<ULONG>(wcslen(DisplayName)), dll.c_str(), static_cast<ULONG>(dll.size()), 0);
            Trace("AddLanguageProfile", hr);
        }
        TraceFreshTsfDiagnostics("Diagnostics.AfterRegisterProfile");
        return hr;
    }
    HRESULT RegisterCategory() override {
        HRESULT ready = WriteManagers(); if (ready != S_OK) return ExactSuccess(ready);
        changedTsf = true;
        for (const auto& category : SupportedCategories) {
            HRESULT hr = categories->RegisterCategory(VoiceClsid, category, VoiceClsid);
            Trace(category == GUID_TFCAT_TIP_KEYBOARD ? "RegisterCategory.Keyboard" : "RegisterCategory.SysTray", hr);
            if (hr != S_OK) return ExactSuccess(hr);
        }
        return S_OK;
    }
    HRESULT UnregisterProfile() override {
        HRESULT hr = WriteManagers(); if (hr != S_OK) return ExactSuccess(hr);
        changedTsf = true;
        // Match libIME2/Mozc's Register/AddLanguageProfile with class Unregister.
        // Policy calls this only for the last owned architecture with no foreign
        // profile/category, or to undo a class/profile created by this attempt.
        ComPtr<ITfInputProcessorProfiles> registration;
        hr = profiles.As(&registration);
        if (hr == S_OK) hr = registration->Unregister(VoiceClsid);
        Trace("UnregisterTextService", hr);
        return hr;
    }
    HRESULT UnregisterCategory() override {
        HRESULT ready = WriteManagers(); if (ready != S_OK) return ExactSuccess(ready);
        changedTsf = true;
        ComPtr<IEnumGUID> items;
        HRESULT hr = categories->EnumCategoriesInItem(VoiceClsid, &items);
        if (hr != S_OK || !items) return hr == S_OK ? E_UNEXPECTED : ExactSuccess(hr);
        std::vector<GUID> present;
        for (;;) {
            GUID category{}; ULONG count = 0;
            hr = items->Next(1, &category, &count);
            if (hr == S_FALSE && !count) break;
            if (hr != S_OK || count != 1) return FAILED(hr) ? hr : E_UNEXPECTED;
            if (SupportedCategory(category)) present.push_back(category);
        }
        items.Reset();
        for (const auto& category : present) {
            hr = categories->UnregisterCategory(VoiceClsid, category, VoiceClsid);
            Trace(category == GUID_TFCAT_TIP_KEYBOARD ? "UnregisterCategory.Keyboard" : "UnregisterCategory.SysTray", hr);
            if (hr != S_OK) return ExactSuccess(hr);
        }
        return S_OK;
    }
    HRESULT UnregisterServiceClass(const std::wstring& dll) override {
        HRESULT ready = WriteManagers(); if (ready != S_OK) return ExactSuccess(ready);
        Snapshot current;
        HRESULT hr = Read(current);
        if (FAILED(hr)) return hr;
        if (!EmptyOwnedClass(current, dll)) return E_ACCESSDENIED;
        ComPtr<ITfInputProcessorProfiles> service;
        hr = profiles.As(&service);
        changedTsf = true;
        if (SUCCEEDED(hr) && (current.legacyTip[0].settingsOnly || current.legacyTip[1].settingsOnly)) {
            // EmptyOwnedClass above proved last owned architecture and no other profile/category.
            // Do not repeatedly remove a nonexistent individual language profile.
            hr = profiles->UnregisterProfile(VoiceClsid, 0x0804, VoiceProfile, TF_URP_ALLPROFILES);
            Trace("UnregisterProfile.AllOwnedProfiles", hr);
        }
        if (SUCCEEDED(hr)) hr = service->Unregister(VoiceClsid);
        Trace("UnregisterServiceClass", hr);
        return hr;
    }
    HRESULT SwitchAway() override {
        HRESULT ready = WriteManagers(); if (ready != S_OK) return ExactSuccess(ready);
        TF_INPUTPROCESSORPROFILE active{};
        HRESULT hr = profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &active);
        Trace("SwitchAway.GetActiveProfile", hr);
        if (hr == S_FALSE || (hr == S_OK && !OwnProfile(active))) return S_OK;
        if (hr != S_OK) return ExactSuccess(hr);
        ComPtr<ITfInputProcessorProfiles> legacy;
        hr = profiles.As(&legacy);
        CLSID defaultClass{}; GUID defaultProfile{};
        if (hr == S_OK) hr = legacy->GetDefaultLanguageProfile(active.langid, GUID_TFCAT_TIP_KEYBOARD, &defaultClass, &defaultProfile);
        Trace("SwitchAway.GetDefaultLanguageProfile", hr);
        if (hr != S_OK && hr != S_FALSE) return ExactSuccess(hr);
        ComPtr<IEnumTfInputProcessorProfiles> items;
        hr = profiles->EnumProfiles(active.langid, &items);
        if (hr != S_OK || !items) return hr == S_OK ? E_UNEXPECTED : ExactSuccess(hr);
        TF_INPUTPROCESSORPROFILE fallback{}; bool found = false;
        for (;;) {
            TF_INPUTPROCESSORPROFILE p{}; ULONG count = 0;
            hr = items->Next(1, &p, &count);
            if (hr == S_FALSE && !count) break;
            if (hr != S_OK || count != 1) return FAILED(hr) ? hr : E_UNEXPECTED;
            // A legacy default may itself be the retiring TIP, even though the
            // shell override is another IME. Use only an already-enabled sibling;
            // ActivateProfile(FORSESSION) changes selection, never the default.
            if (!found && SafeAlternative(p, active.langid)) { fallback = p; found = true; }
            if (SafeDefault(p, active.langid, defaultClass, defaultProfile)) { fallback = p; found = true; }
        }
        if (!found) return HRESULT_FROM_WIN32(ERROR_BUSY);
        // Avoid stealing focus if the user already changed input method while we queried.
        hr = profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &active);
        if (hr == S_FALSE || (hr == S_OK && !OwnProfile(active))) return S_OK;
        if (hr != S_OK) return ExactSuccess(hr);
        hr = profiles->ActivateProfile(fallback.dwProfileType, fallback.langid, fallback.clsid,
            fallback.guidProfile, nullptr, TF_IPPMF_FORSESSION); // no ENABLEPROFILE/default/language setters
        Trace("SwitchAway.ActivateExistingDefault", hr);
        if (hr != S_OK) return ExactSuccess(hr); // S_FALSE means not enabled, never success.
        hr = profiles->GetActiveProfile(GUID_TFCAT_TIP_KEYBOARD, &active);
        Trace("SwitchAway.Postcheck", hr);
        return hr == S_OK && !OwnProfile(active) ? S_OK : FAILED(hr) ? hr : E_FAIL;
    }
    HRESULT RemoveDisabledUserSettings(const std::wstring& dll) override {
        if (!locked) return E_ACCESSDENIED;
        Snapshot s;
        HRESULT hr = Read(s);
        if (FAILED(hr)) return hr;
        const bool recovery = OrphanDisabledUserSettings(s);
        if (recovery) {
            hr = RecoveryMaintenance(); if (hr != S_OK) return ExactSuccess(hr);
            hr = ValidateInstall(dll, nullptr); if (hr != S_OK) return ExactSuccess(hr);
        } else if (!RemovableUserSettings(s, dll)) return E_ACCESSDENIED;
        Transaction tx;
        if (tx.value == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        // Recheck the exact COM anchor and BOTH machine views inside the delete transaction.
        Snapshot current;
        for (unsigned i = 0; i != 2; ++i) {
            hr = ReadCom(HKEY_LOCAL_MACHINE, Views[i], current.machine[i], tx.value); if (FAILED(hr)) return hr;
            hr = ReadCom(HKEY_CURRENT_USER, Views[i], current.user[i], tx.value); if (FAILED(hr)) return hr;
            Record tip; hr = ReadRecord(HKEY_LOCAL_MACHINE, TipPath(), Views[i], tip, tx.value); if (FAILED(hr)) return hr;
            current.machineTip[i] = tip.exists;
            hr = ReadProfileKeys(Views[i], current, tx.value); if (FAILED(hr)) return hr;
            hr = ReadUserTip(Views[i], current.legacyTip[i], tx.value); if (FAILED(hr)) return hr;
            hr = ReadHost(Views[i], current.host[i], tx.value); if (FAILED(hr)) return hr;
        }
        // s supplies freshly queried profile/category/active facts; transaction supplies key ownership.
        current.profile = s.profile; current.registeredProfile = s.registeredProfile;
        current.keyboard = s.keyboard; current.registeredKeyboard = s.registeredKeyboard;
        current.category = s.category; current.otherCategories = s.otherCategories;
        current.otherProfiles = current.otherProfiles || s.otherProfiles; current.enabled = s.enabled; current.active = s.active;
        if (recovery ? !OrphanDisabledUserSettings(current) : !RemovableUserSettings(current, dll)) return E_ACCESSDENIED;
        if (recovery) { hr = RecoveryMaintenance(); if (hr != S_OK) return ExactSuccess(hr); }
        for (REGSAM view : Views) {
            Key key;
            LONG error = Open(HKEY_CURRENT_USER, TipPath(), view, KEY_READ, &key.value, tx.value);
            if (Missing(error)) continue; // shared HKCU path: a previous view may have removed it.
            if (error) return HRESULT_FROM_WIN32(error);
            bool only = false, disabled = false, exactDisabled = false;
            hr = UserEnableSettings(key.value, 0, only, disabled, exactDisabled, tx.value);
            if (FAILED(hr) || !only || !disabled || (recovery && !exactDisabled)) return FAILED(hr) ? hr : E_ACCESSDENIED;
            RegCloseKey(key.value); key.value = nullptr;
            // Delete ONLY this fixed CLSID's proven fixed-profile/Enable=0 (or empty) subtree.
            // No CTF/TIP parent, machine registration, foreign profile/category or new TSF values.
            hr = Remove(tx.value, HKEY_CURRENT_USER, TipPath(), view); if (FAILED(hr)) return hr;
        }
        hr = tx.Commit(); Trace("RemoveDisabledUserSettings.Commit", hr); return hr;
    }
    UserBindingResult Enable(bool enable) override {
        HRESULT ready = WriteManagers();
        changedTsf = true;
        HRESULT preferences = S_OK;
        auto result = ChangeUserBinding(enable, [&](bool value) {
            ComPtr<ITfInputProcessorProfiles> userProfiles;
            HRESULT hr = ready == S_OK ? profiles.As(&userProfiles) : ExactSuccess(ready);
            if (hr == S_OK) hr = userProfiles->EnableLanguageProfile(VoiceClsid, 0x0804, VoiceProfile, value ? TRUE : FALSE);
            Trace(value ? "EnableLanguageProfile.True" : "EnableLanguageProfile.False", hr);
            return hr;
        }, [&](bool value) {
            // One same-token fresh ILOT only; legacy TSF enablement is independent.
            preferences = ChangeUserProfileBinding(value);
            Trace(value ? "UserLanguageList.Enable" : "UserLanguageList.Disable", preferences);
            return preferences;
        });
        // Policy may tolerate a legacy failure on observed detachment, never a fresh ILOT failure.
        result.preferences = preferences;
        if (enable) TraceFreshTsfDiagnostics("Diagnostics.AfterEnable.BeforePostcheckOrRollback");
        return result;
    }
};
HRESULT Save(const Result& r) {
    Trace(r.stage, r.hr);
    lastError = {r.stage, r.hr, r.rollbackStage, r.rollback}; alreadyAbsent = r.already;
    return r.hr;
}
}
RegistrationError LastRegistrationError() { return lastError; }
bool RegistrationAlreadyAbsent() { return alreadyAbsent; }
HRESULT QueryTsfRegistrationDiagnostics() {
    WindowsBackend backend;
    HRESULT hr = backend.Diagnostics();
    return hr == S_OK ? S_OK : Save(Failure("TsfDiagnostics", ExactSuccess(hr)));
}
HRESULT QueryTsfRegistrationFacts(RegistrationFacts& facts) {
    WindowsBackend backend; Snapshot s;
    HRESULT hr = backend.ReadTsf(s);
    Trace("ReadTsfFacts", hr, &s);
    if (FAILED(hr)) return Save(Failure("EnumTsfFacts", hr));
    facts = {s.profile, s.enabled, s.keyboard, s.category, s.otherProfiles, s.otherCategories, s.registeredProfile, s.registeredKeyboard, s.active};
    return S_OK;
}
HRESULT RegisterCurrentUser(const std::wstring& dll, const std::wstring* host) {
    // ABI name retained; performs explicit machine installation, never requests UAC.
    WindowsBackend backend; return Save(VoiceRegistration::Register(backend, dll, host));
}
HRESULT UnregisterCurrentUser(const std::wstring& dll) {
    if (!LocalPath(dll)) return Save(Failure("ValidatePath", E_INVALIDARG));
    WindowsBackend backend; return Save(VoiceRegistration::Unregister(backend, dll));
}
HRESULT QueryRegistration(const std::wstring& dll, RegistrationStatus& status) {
    if (!LocalPath(dll)) return Save(Failure("ValidatePath", E_INVALIDARG));
    WindowsBackend backend; Snapshot s; HRESULT hr = backend.Read(s);
    if (FAILED(hr)) return Save(Failure("QueryRegistration", hr));
    // Missing files are incomplete installation, not a successful registry-only status.
    if (s.machine[0].owned && !AbsoluteFile(s.machine[0].path)) s.machine[0].apartment = false;
    for (auto& h : s.host) if (h.owned && !AbsoluteFile(h.path)) h.path.clear();
    status = Status(s, dll, InstallationMaintenanceActive());
    return S_OK;
}
HRESULT ProbeRegistration(bool& present, bool& enabled, bool& keyboard) {
    WindowsBackend backend; Snapshot s; HRESULT hr = backend.Read(s);
    if (FAILED(hr)) return Save(Failure("EnumProfiles", hr));
    present = s.profile; enabled = s.enabled; keyboard = s.keyboard && s.category;
    return S_OK;
}