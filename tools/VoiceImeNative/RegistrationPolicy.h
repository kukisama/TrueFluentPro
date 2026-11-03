#pragma once
#include "Common.h"

// Testable installation policy. The offline backend never calls Windows registration APIs.
namespace VoiceRegistration {
inline constexpr wchar_t Owner[] = L"TrueFluentPro.VoiceIme.v1";
struct Record {
    bool exists = false, owned = false, apartment = false;
    bool settingsOnly = false, settingsDisabled = false;
    bool settingsExactDisabled = false; // Full fixed profile with REG_DWORD Enable=0, not an empty/partial tree.
    std::wstring path, binding;
};
struct Snapshot {
    Record machine[2], user[2], legacyTip[2], host[2]; // native view, then other view
    bool machineTip[2]{};
    bool profile = false, enabled = false, keyboard = false, category = false;
    bool profileKeys = false, otherProfiles = false, otherCategories = false;
    bool registeredProfile = false, registeredKeyboard = false;
    bool active = false;
    bool HasProfile() const { return profile || registeredProfile; }
    bool UserEntry() const { return enabled || active || legacyTip[0].exists || legacyTip[1].exists; }
    bool UserDetached() const {
        if (enabled || active) return false;
        for (const auto& tip : legacyTip)
            if (tip.exists && (!tip.settingsOnly || !tip.settingsDisabled)) return false;
        return true;
    }
    bool Machine() const { return machine[0].exists || machine[1].exists || machineTip[0] || machineTip[1]; }
    bool Legacy() const {
        return user[0].exists || user[1].exists || (legacyTip[0].exists && !legacyTip[0].settingsOnly) ||
            (legacyTip[1].exists && !legacyTip[1].settingsOnly);
    }
    bool Empty() const {
        return !Machine() && !Legacy() && !UserEntry() && !HasProfile() && !category && !profileKeys && !otherProfiles &&
            !otherCategories && !host[0].exists && !host[1].exists;
    }
};
struct Result {
    HRESULT hr = S_OK;
    const char* stage = "Complete";
    HRESULT rollback = S_OK;
    const char* rollbackStage = "None";
    bool needsAdmin = false, already = false;
    int ExitCode() const {
        if (needsAdmin) return 10; // Only the pre-write elevation gate may set this.
        if (FAILED(rollback)) return 5;
        return FAILED(hr) ? 4 : 0;
    }
};
inline Result Failure(const char* stage, HRESULT hr) { Result r; r.stage = stage; r.hr = hr; return r; }
inline bool Equal(const std::wstring& a, const std::wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) == 0; }
inline std::wstring InstallRoot(const std::wstring& dll) {
    auto slash = dll.find_last_of(L'\\');
    return slash == dll.npos ? L"" : dll.substr(0, dll.find_last_of(L'\\', slash - 1));
}
inline bool SameInstall(const std::wstring& a, const std::wstring& b) {
    return !InstallRoot(a).empty() && Equal(InstallRoot(a), InstallRoot(b));
}
inline bool OwnEnableValue(const std::wstring& name, DWORD type, DWORD bytes, DWORD data) {
    return Equal(name, L"Enable") && type == REG_DWORD && bytes == sizeof(DWORD) && data <= 1;
}
inline const char* State(const Snapshot& s, const std::wstring& dll) {
    if (s.Legacy()) return "legacy-user";
    if (s.machine[0].exists && (!s.machine[0].owned || !Equal(s.machine[0].path, dll))) return "different-path";
    if (s.machine[1].exists && (!s.machine[1].owned || !SameInstall(s.machine[1].path, dll))) return "different-path";
    for (const auto& h : s.host) if (h.owned && !h.binding.empty() && !Equal(h.binding, InstallRoot(dll))) return "different-path";
    if (s.Empty()) return "unregistered";
    if (s.machine[0].owned && s.machine[0].apartment && s.profile && s.enabled && s.keyboard && s.category &&
        s.host[0].owned && s.host[1].owned && !s.host[0].path.empty() && Equal(s.host[0].path, s.host[1].path)) return "registered";
    return "partial";
}
inline RegistrationStatus Status(const Snapshot& s, const std::wstring& dll, bool maintenance) {
    RegistrationStatus result;
    result.state = State(s, dll);
    result.maintenance = maintenance;
    // 登记完整性和运行门禁是独立事实；安装器在维护期间也必须能校验真实登记。
    // F8/worker/host 仍检查 maintenance.lock，registered 并不表示维护期间可采音。
    result.requiresAdmin = !(s.Legacy() && !s.Machine());
    return result;
}
enum NewKeys : unsigned { ComKey = 1, HostNative = 2, HostOther = 4 };
inline HRESULT ExactSuccess(HRESULT hr) { return hr == S_OK ? S_OK : FAILED(hr) ? hr : E_FAIL; }
struct UserBindingResult {
    HRESULT language = E_PENDING, layout = E_PENDING;
    HRESULT preferences = S_OK; // Existing offline backends remain source-compatible.
    HRESULT Status() const {
        if (preferences != S_OK) return ExactSuccess(preferences);
        return language == S_OK ? ExactSuccess(layout) : ExactSuccess(language);
    }
};
template<class Language, class Layout>
UserBindingResult ChangeUserBinding(bool enable, Language language, Layout layout) {
    UserBindingResult result;
    if (enable) {
        result.language = language(true);
        result.layout = layout(true);
    } else {
        // Initialize/remove the user binding before disabling its TSF profile.
        // A fresh writer failure must not additionally change the profile state.
        result.layout = layout(false);
        if (result.layout == S_OK) result.language = language(false);
    }
    return result;
}
inline bool OwnProfile(const TF_INPUTPROCESSORPROFILE& p) {
    return p.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && p.clsid == VoiceClsid &&
        p.guidProfile == VoiceProfile && p.langid == 0x0804;
}
inline bool SafeAlternative(const TF_INPUTPROCESSORPROFILE& p, LANGID language) {
    return p.dwProfileType == TF_PROFILETYPE_INPUTPROCESSOR && p.clsid != VoiceClsid && p.langid == language &&
        p.catid == GUID_TFCAT_TIP_KEYBOARD &&
        (p.dwFlags & TF_IPP_FLAG_ENABLED) != 0;
}
inline bool SafeDefault(const TF_INPUTPROCESSORPROFILE& p, LANGID language, REFCLSID clsid, REFGUID profile) {
    return SafeAlternative(p, language) && p.clsid == clsid && p.guidProfile == profile;
}
struct Backend {
    virtual ~Backend() = default;
    virtual HRESULT Elevated(bool&) = 0;
    virtual HRESULT Lock() = 0;
    virtual HRESULT Read(Snapshot&) = 0;
    virtual HRESULT RecoveryMaintenance() = 0; // Positive, known maintenance.lock; unknown is failure.
    virtual HRESULT ValidateInstall(const std::wstring&, const std::wstring*) = 0;
    // Registry changes in each method are transactional; failures commit nothing.
    virtual HRESULT AddKeys(const std::wstring&, const std::wstring*, unsigned&) = 0;
    virtual HRESULT RemoveKeys(const std::wstring&, const std::wstring*, unsigned) = 0;
    virtual HRESULT RemoveLegacy(const std::wstring&, bool last, bool removeHost) = 0;
    virtual HRESULT RegisterProfile(const std::wstring&) = 0;
    virtual HRESULT RegisterCategory() = 0;
    virtual HRESULT UnregisterProfile() = 0;
    virtual HRESULT UnregisterCategory() = 0;
    virtual HRESULT UnregisterServiceClass(const std::wstring&) = 0;
    virtual UserBindingResult Enable(bool) = 0;
    virtual HRESULT SwitchAway() = 0;
    virtual HRESULT RemoveDisabledUserSettings(const std::wstring&) = 0;
};
inline bool EmptyOwnedClass(const Snapshot& s, const std::wstring& dll) {
    const auto& c = s.machine[0];
    return c.exists && c.owned && c.apartment && Equal(c.path, dll) && !s.machine[1].exists &&
        !s.Legacy() && !s.HasProfile() && !s.profileKeys && !s.otherProfiles && !s.category && !s.otherCategories;
}
inline bool RemovableUserSettings(const Snapshot& s, const std::wstring& dll) {
    if (s.machineTip[0] || s.machineTip[1] || !s.UserDetached()) return false;
    if (!s.HasProfile()) return EmptyOwnedClass(s, dll);
    // Windows may still enumerate this disabled user override after the machine
    // service/profile/category has gone. This is ownership eligibility only, NOT
    // an absence verdict: final fresh-process enumeration must still become empty.
    bool exactDisabled = false;
    for (const auto& tip : s.legacyTip) if (tip.exists) {
        if (!tip.settingsExactDisabled) return false;
        exactDisabled = true;
    }
    if (!exactDisabled) return false;
    Snapshot machine = s;
    machine.profile = machine.registeredProfile = false;
    return EmptyOwnedClass(machine, dll);
}
inline bool OrphanDisabledUserSettings(const Snapshot& s) {
    if (s.Machine() || s.user[0].exists || s.user[1].exists || s.HasProfile() || s.profileKeys ||
        s.otherProfiles || s.category || s.otherCategories || s.enabled || s.active || s.keyboard ||
        s.registeredKeyboard || s.host[0].exists || s.host[1].exists) return false;
    bool found = false;
    for (const auto& tip : s.legacyTip) if (tip.exists) {
        found = true;
        if (tip.owned || !tip.settingsOnly || !tip.settingsDisabled || !tip.settingsExactDisabled) return false;
    }
    return found;
}
inline Result CleanupDisabledUserSettings(Backend& b, const std::wstring& dll, Snapshot& s) {
    if (!s.legacyTip[0].exists && !s.legacyTip[1].exists) return {};
    if (!RemovableUserSettings(s, dll)) return Failure("UserSettingsOwnership", E_ACCESSDENIED);
    HRESULT hr = b.RemoveDisabledUserSettings(dll);
    if (hr != S_OK) return Failure("RemoveDisabledUserSettings", ExactSuccess(hr));
    hr = b.Read(s);
    return FAILED(hr) || s.legacyTip[0].exists || s.legacyTip[1].exists ?
        Failure("UserSettingsPostcheck", FAILED(hr) ? hr : E_FAIL) : Result{};
}
inline Result Admin(Backend& b) {
    bool elevated = false;
    HRESULT hr = b.Elevated(elevated);
    if (FAILED(hr)) return Failure("QueryElevation", hr);
    if (!elevated) {
        auto r = Failure("RequiresAdmin", HRESULT_FROM_WIN32(ERROR_ELEVATION_REQUIRED)); r.needsAdmin = true; return r;
    }
    return {};
}
inline Result Register(Backend& b, const std::wstring& dll, const std::wstring* host) {
    auto result = Admin(b); if (FAILED(result.hr)) return result;
    HRESULT hr = b.Lock(); if (FAILED(hr)) return Failure("RegistrationLock", hr);
    Snapshot before;
    hr = b.Read(before); if (FAILED(hr)) return Failure("QueryRegistration", hr);
    if (before.Legacy()) return Failure("LegacyUserCleanupRequired", HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS));
    for (unsigned i = 0; i != 2; ++i) {
        const auto& c = before.machine[i];
        if (c.exists && (!c.owned || (i == 0 ? !Equal(c.path, dll) : !SameInstall(c.path, dll))))
            return Failure("DifferentPath", HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH));
        if (c.exists && !c.apartment) return Failure("ComOwnership", E_ACCESSDENIED);
        const auto& h = before.host[i];
        if (host && h.exists && (!h.owned || !Equal(h.path, *host) ||
            (!h.binding.empty() && !Equal(h.binding, InstallRoot(dll)))))
            return Failure("HostOwnership", HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH));
    }
    if (!before.machine[0].exists && !before.machine[1].exists &&
        (before.Machine() || before.profile || before.category)) return Failure("UnownedTsfRegistration", E_ACCESSDENIED);
    hr = b.ValidateInstall(dll, host); if (FAILED(hr)) return Failure("ValidateInstall", hr);
    unsigned added = 0;
    hr = b.AddKeys(dll, host, added);
    if (FAILED(hr)) return Failure("WriteComAndHost", hr);
    bool profileAttempt = false, profileSucceeded = false, categoryAttempt = false, enableAttempt = false;
    auto rollback = [&](const char* stage, HRESULT failed) {
        auto r = Failure(stage, failed);
        auto undo = [&](const char* where, HRESULT value) {
            if (FAILED(value) && SUCCEEDED(r.rollback)) { r.rollback = value; r.rollbackStage = where; }
        };
        try {
        if (enableAttempt) undo("RollbackUserEnable", b.Enable(false).Status());
        Snapshot after;
        HRESULT query = b.Read(after); undo("RollbackQuery", query);
        if (SUCCEEDED(query)) {
            if (profileAttempt && (after.HasProfile() || profileSucceeded))
                undo("RollbackProfile", ExactSuccess(b.UnregisterProfile()));
            query = b.Read(after); undo("RollbackQuery", query);
            if (SUCCEEDED(query) && categoryAttempt && after.category) undo("RollbackCategory", ExactSuccess(b.UnregisterCategory()));
            query = b.Read(after); undo("RollbackQuery", query);
            // Only compensate a class root created by this attempt, never a pre-existing/shared class.
            if (SUCCEEDED(query) && SUCCEEDED(r.rollback) && !before.machineTip[0] && !before.machineTip[1] &&
                (after.machineTip[0] || after.machineTip[1]) && EmptyOwnedClass(after, dll)) {
                undo("RollbackServiceClass", ExactSuccess(b.UnregisterServiceClass(dll)));
                query = b.Read(after); undo("RollbackQuery", query);
            }
            if (SUCCEEDED(query) && ((!before.profile && after.profile) || (!before.category && after.category) ||
                (!before.registeredProfile && after.registeredProfile) || (!before.profileKeys && after.profileKeys) ||
                (!before.machineTip[0] && after.machineTip[0]) || (!before.machineTip[1] && after.machineTip[1])))
                undo("RollbackTsfResidue", E_FAIL);
            // Same strict cleanup and readback as full last-architecture uninstall, BEFORE
            // dropping the new COM anchor. Class APIs may leave HKCU Enable=0 behind.
            if (SUCCEEDED(query) && SUCCEEDED(r.rollback) && (added & ComKey) && !before.machine[1].exists) {
                auto cleaned = CleanupDisabledUserSettings(b, dll, after);
                undo("RollbackDisabledUserSettings", cleaned.hr);
                if (SUCCEEDED(cleaned.hr) && (after.UserEntry() || after.legacyTip[0].exists || after.legacyTip[1].exists))
                    undo("RollbackUserSettingsPostcheck", E_FAIL);
            }
        }
        // Keep ownership evidence if official compensation failed, so explicit uninstall can retry.
        unsigned remove = FAILED(r.rollback) ? (added & ~ComKey) : added;
        undo("RollbackComAndHost", b.RemoveKeys(dll, host, remove));
        } catch (...) {
            undo("RollbackException", E_OUTOFMEMORY);
        }
        return r;
    };
    try {
    // SampleIME DllRegisterServer: RegisterProfiles before RegisterCategories.
    // RegisterProfile owns the service registration; category registration follows it.
    if (!before.HasProfile()) {
        profileAttempt = true; hr = b.RegisterProfile(dll);
        profileSucceeded = hr == S_OK;
        if (hr != S_OK) return rollback("RegisterProfile", FAILED(hr) ? hr : E_FAIL);
    }
    if (!before.category) {
        categoryAttempt = true; hr = b.RegisterCategory();
        if (hr != S_OK) return rollback("RegisterCategory", FAILED(hr) ? hr : E_FAIL);
    }
    Snapshot installed;
    hr = b.Read(installed);
    // IME installation: RegisterProfile, then InstallLayoutOrTip, then user-visible enumeration.
    // On this host even GetProfile omits a not-yet-user-enabled profile. A successful new
    // registration must persist its exact profile key (Read rejects other identities) before enable.
    // This is NOT a successful installation: final EnumProfiles/keyboard/enabled remain mandatory.
    bool machineProfile = installed.HasProfile() ||
        (profileSucceeded && installed.profileKeys && !installed.otherProfiles);
    if (FAILED(hr) || !machineProfile || !installed.category)
        return rollback(FAILED(hr) ? "MachineProfilePostcheck.Query" :
            !machineProfile ? "MachineProfilePostcheck.ProfileMissing" : "MachineProfilePostcheck.CategoryMissing",
            FAILED(hr) ? hr : E_FAIL);
    // The DLL exports may register the machine service without configuring a user host.
    if (host && !before.enabled) {
        enableAttempt = true;
        const auto binding = b.Enable(true); hr = binding.Status();
        if (FAILED(hr)) return rollback(binding.preferences != S_OK ? "UserLanguageList.Enable" : "InstallLayoutOrTip", hr);
    }
    hr = b.Read(installed);
    if (FAILED(hr) || !installed.profile || !installed.keyboard || !installed.category ||
        (host && (std::string(State(installed, dll)) != "registered")))
        return rollback("RegistrationPostcheck", FAILED(hr) ? hr : E_FAIL);
    return {};
    } catch (...) { return rollback("RegistrationException", E_OUTOFMEMORY); }
}
inline Result Unregister(Backend& b, const std::wstring& dll) {
    Snapshot s;
    HRESULT hr = b.Read(s); if (FAILED(hr)) return Failure("QueryRegistration", hr);
    if (s.Empty()) { Result r; r.already = true; return r; }
    if (s.Machine() || (!s.Legacy() && (s.HasProfile() || s.profileKeys || s.category || s.UserEntry()))) {
        auto r = Admin(b); if (FAILED(r.hr)) return r;
    }
    hr = b.Lock(); if (FAILED(hr)) return Failure("RegistrationLock", hr);
    hr = b.Read(s); if (FAILED(hr)) return Failure("QueryRegistration", hr);
    if (s.Empty()) { Result r; r.already = true; return r; }
    // Recheck after the cross-process lock: another installer may have changed the initial snapshot.
    if (s.Machine() || (!s.Legacy() && (s.HasProfile() || s.profileKeys || s.category || s.UserEntry()))) {
        auto r = Admin(b); if (FAILED(r.hr)) return r;
    }
    if (s.Legacy()) {
        const auto& c = s.user[0];
        if (c.exists && (!c.owned || !Equal(c.path, dll))) return Failure("LegacyOwnership", E_ACCESSDENIED);
        bool last = !s.user[1].exists;
        if (!c.exists && (!last || !((s.legacyTip[0].owned && Equal(s.legacyTip[0].path, dll)) ||
            (s.legacyTip[1].owned && Equal(s.legacyTip[1].path, dll))))) return Failure("LegacyOwnership", E_ACCESSDENIED);
        if (last) for (const auto& tip : s.legacyTip)
            if (tip.exists && (!tip.owned || !Equal(tip.path, dll))) return Failure("LegacyTipOwnership", E_ACCESSDENIED);
        if (!last) for (const auto& tip : s.legacyTip)
            if (tip.owned && Equal(tip.path, dll)) return Failure("LegacyCleanupOrder", HRESULT_FROM_WIN32(ERROR_BUSY));
        // Never call TSF unregister/InstallLayoutOrTip for legacy cleanup: they could affect HKLM.
        hr = b.RemoveLegacy(dll, last, last && !s.Machine());
        return FAILED(hr) ? Failure("RemoveLegacyUser", hr) : Result{};
    }
    const auto& c = s.machine[0];
    if (!c.exists) {
        if (s.machine[1].exists) return {}; // Requested view absent; not globally already-unregistered.
        // Recovery for the precise old rollback defect, not general orphan ownership.
        // Caller supplies the actual receipt-verified package DLL; native validation is mandatory.
        if (OrphanDisabledUserSettings(s)) {
            auto admin = Admin(b); if (FAILED(admin.hr)) return admin;
            hr = b.RecoveryMaintenance();
            if (hr != S_OK) return Failure("RecoveryMaintenance", ExactSuccess(hr));
            hr = b.ValidateInstall(dll, nullptr);
            if (hr != S_OK) return Failure("RecoveryValidateInstall", ExactSuccess(hr));
            hr = b.RemoveDisabledUserSettings(dll);
            if (hr != S_OK) return Failure("RecoveryDisabledUserSettings", ExactSuccess(hr));
            Snapshot final; hr = b.Read(final);
            return FAILED(hr) || !final.Empty() ? Failure("RecoveryFinalPostcheck", FAILED(hr) ? hr : E_FAIL) : Result{};
        }
        return Failure("ComOwnership", E_ACCESSDENIED); // No owner for orphan API registration.
    }
    if (!c.owned || !Equal(c.path, dll)) return Failure("DifferentPath", HRESULT_FROM_WIN32(ERROR_REVISION_MISMATCH));
    if (s.machine[1].exists) {
        if (!s.machine[1].owned || !s.machine[1].apartment || !SameInstall(s.machine[1].path, dll))
            return Failure("OtherArchitectureOwnership", E_ACCESSDENIED);
        if (s.otherProfiles || s.otherCategories) return Failure("ServiceClassNotExclusive", E_ACCESSDENIED);
        for (const auto& host : s.host) if (host.exists && (!host.owned || !Equal(host.binding, InstallRoot(dll))))
            return Failure("HostOwnership", E_ACCESSDENIED);
        hr = b.SwitchAway(); if (hr != S_OK) return Failure("SwitchAway", ExactSuccess(hr));
        hr = b.RemoveKeys(dll, nullptr, ComKey);
        if (hr != S_OK) return Failure("RemoveCom", ExactSuccess(hr));
        Snapshot after; hr = b.Read(after);
        return FAILED(hr) || after.machine[0].exists ? Failure("RemoveComPostcheck", FAILED(hr) ? hr : E_FAIL) : Result{};
    }
    if (s.otherProfiles || s.otherCategories) return Failure("ServiceClassNotExclusive", E_ACCESSDENIED);
    for (const auto& host : s.host) if (host.exists && (!host.owned || !Equal(host.binding, InstallRoot(dll))))
        return Failure("HostOwnership", E_ACCESSDENIED);
    // Switch only if this exact profile is active. Never change the user's default or enable another IME.
    hr = b.SwitchAway(); if (hr != S_OK) return Failure("SwitchAway", ExactSuccess(hr));
    {
        // A failed preference rollback may leave only the COM anchor visible to TSF.
        // Always remove the exact language-list tip on last-architecture uninstall.
        const auto unbind = b.Enable(false);
        if (unbind.preferences != S_OK) return Failure("UserLanguageList.Disable", ExactSuccess(unbind.preferences));
        Snapshot detached; hr = b.Read(detached);
        if (FAILED(hr)) return Failure("UserDetachPostcheck.Query", hr);
        // A failed API is retained in trace, not reinterpreted as success. Continue only on
        // observed disabled/absent user state; full system/profile/category/COM absence is still mandatory.
        if (!detached.UserDetached()) return Failure("UserDetachPostcheck", FAILED(unbind.Status()) ? unbind.Status() : E_FAIL);
        s = detached;
    }
    if (s.HasProfile() || s.profileKeys) {
        hr = b.UnregisterProfile(); if (hr != S_OK) return Failure("UnregisterProfile", ExactSuccess(hr));
    }
    Snapshot after;
    hr = b.Read(after); if (FAILED(hr)) return Failure("UnregisterPostcheck", hr);
    if (after.category) {
        hr = b.UnregisterCategory(); if (hr != S_OK) return Failure("UnregisterCategory", ExactSuccess(hr));
    }
    hr = b.Read(after);
    if (FAILED(hr)) return Failure("UnregisterPostcheck", hr);
    // UnregisterProfile normally removes the service too. Class-level Unregister is only for
    // an observed empty residual class, anchored to this last architecture's exact COM owner/path.
    if ((after.machineTip[0] || after.machineTip[1]) && EmptyOwnedClass(after, dll)) {
        hr = b.UnregisterServiceClass(dll);
        if (hr != S_OK) return Failure("UnregisterServiceClass", ExactSuccess(hr));
        hr = b.Read(after);
    }
    if (FAILED(hr)) return Failure("UnregisterPostcheck.Query", hr);
    auto cleaned = CleanupDisabledUserSettings(b, dll, after);
    if (FAILED(cleaned.hr)) return cleaned;
    if (FAILED(hr) || after.HasProfile() || after.UserEntry() || after.profileKeys || after.otherProfiles || after.category || after.otherCategories ||
        after.machineTip[0] || after.machineTip[1] || after.legacyTip[0].exists || after.legacyTip[1].exists)
        return Failure("UnregisterPostcheck", FAILED(hr) ? hr : E_FAIL); // 未清净则保留 COM 所有权供重试。
    unsigned remove = ComKey;
    for (unsigned i = 0; i != 2; ++i) if (s.host[i].owned && Equal(s.host[i].binding, InstallRoot(dll))) remove |= (HostNative << i);
    hr = b.RemoveKeys(dll, nullptr, remove);
    if (hr != S_OK) return Failure("RemoveComAndHost", ExactSuccess(hr));
    Snapshot final; hr = b.Read(final);
    return FAILED(hr) || !final.Empty() ? Failure("UnregisterFinalPostcheck", FAILED(hr) ? hr : E_FAIL) : Result{};
}
} // namespace VoiceRegistration