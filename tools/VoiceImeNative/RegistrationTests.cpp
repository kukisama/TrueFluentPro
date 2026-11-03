#include "RegistrationPolicy.h"
#include "RegistrationQuery.h"
#include "RegistrationDiagnostics.h"
#include "UserProfileBinding.h"
#include <cstdio>
#include <algorithm>

using namespace VoiceRegistration;
namespace {
const std::wstring Dll = L"C:\\Program Files\\TrueFluentPro\\VoiceIme\\test-hash\\win-x64\\TrueFluentVoiceIme.dll";
const std::wstring Other = L"C:\\Program Files\\TrueFluentPro\\VoiceIme\\test-hash\\win-x86\\TrueFluentVoiceIme.dll";
const std::wstring Host = L"C:\\fixture\\host.exe";
Record Owned(const std::wstring& path) { Record r; r.exists = r.owned = r.apartment = true; r.path = path; return r; }
struct Memory final : Backend {
    Snapshot s;
    bool admin = true, hideProfile = false, leaveClass = true, failedProfileLeavesClass = false, userEnumerationNeedsEnable = false;
    bool hideUserEnumeration = false;
    bool keepUserSettings = false;
    bool disabledProfileEcho = false;
    bool maintenance = true, keepDisabledSettings = false, settingsOnDisable = false;
    bool languageFailure = false, layoutFailure = false, keepEnabled = false, keepCom = false;
    bool freshLayout = false; // Opt-in production result contract; existing fake defaults stay unchanged.
    const char* fail = "", *failUndo = "";
    std::vector<std::string> calls;
    HRESULT Call(const char* name) {
        calls.emplace_back(name);
        return std::string(name) == fail || std::string(name) == failUndo ? E_FAIL : S_OK;
    }
    bool Called(const char* name) const { return std::find(calls.begin(), calls.end(), name) != calls.end(); }
    HRESULT Elevated(bool& value) override { value = admin; return Call("Elevation"); }
    HRESULT Lock() override { return Call("Lock"); }
    HRESULT RecoveryMaintenance() override {
        HRESULT hr = Call("RecoveryMaintenance"); return FAILED(hr) ? hr : maintenance ? S_OK : E_ACCESSDENIED;
    }
    HRESULT Read(Snapshot& value) override {
        value = s;
        if (userEnumerationNeedsEnable && !value.enabled) value.profile = false;
        if (hideProfile) value.profile = value.profileKeys = value.registeredProfile = value.registeredKeyboard = false;
        if (hideUserEnumeration) value.profile = false;
        return Call("Read");
    }
    HRESULT ValidateInstall(const std::wstring&, const std::wstring*) override { return Call("Validate"); }
    HRESULT AddKeys(const std::wstring& dll, const std::wstring* host, unsigned& added) override {
        HRESULT hr = Call("AddKeys"); if (FAILED(hr)) return hr;
        added = 0;
        if (!s.machine[0].exists) { s.machine[0] = Owned(dll); added |= ComKey; }
        if (host) for (unsigned i = 0; i != 2; ++i) if (!s.host[i].exists) {
            s.host[i] = Owned(*host); s.host[i].binding = InstallRoot(dll); added |= HostNative << i;
        }
        return S_OK;
    }
    HRESULT RemoveKeys(const std::wstring&, const std::wstring*, unsigned remove) override {
        HRESULT hr = Call("RemoveKeys"); if (FAILED(hr)) return hr;
        if ((remove & ComKey) && !keepCom) s.machine[0] = {};
        for (unsigned i = 0; i != 2; ++i) if (remove & (HostNative << i)) s.host[i] = {};
        return S_OK;
    }
    HRESULT RemoveLegacy(const std::wstring&, bool last, bool removeHost) override {
        HRESULT hr = Call("RemoveLegacy"); if (FAILED(hr)) return hr;
        s.user[0] = {};
        if (last) for (auto& tip : s.legacyTip) tip = {};
        if (removeHost) for (auto& host : s.host) host = {};
        return S_OK;
    }
    HRESULT RegisterProfile(const std::wstring&) override {
        HRESULT hr = Call("RegisterProfile");
        if (FAILED(hr)) { if (failedProfileLeavesClass) s.machineTip[0] = true; return hr; }
        s.profile = s.keyboard = s.profileKeys = s.machineTip[0] = true; return S_OK;
    }
    HRESULT RegisterCategory() override {
        HRESULT hr = Call("RegisterCategory"); if (FAILED(hr)) return hr;
        if (!s.profile) return E_UNEXPECTED;
        s.category = s.machineTip[0] = true; return S_OK;
    }
    HRESULT UnregisterProfile() override {
        HRESULT hr = Call("UnregisterProfile"); if (FAILED(hr)) return hr;
        s.profile = s.enabled = s.keyboard = s.profileKeys = s.registeredProfile = s.registeredKeyboard = false;
        if (disabledProfileEcho) s.profile = s.registeredProfile = s.keyboard = s.registeredKeyboard = true;
        if (!leaveClass) s.machineTip[0] = s.machineTip[1] = false;
        return S_OK;
    }
    HRESULT UnregisterCategory() override {
        HRESULT hr = Call("UnregisterCategory"); if (FAILED(hr)) return hr;
        s.category = false; return S_OK;
    }
    HRESULT UnregisterServiceClass(const std::wstring& dll) override {
        HRESULT hr = Call("UnregisterServiceClass"); if (FAILED(hr)) return hr;
        if (!EmptyOwnedClass(s, dll)) return E_ACCESSDENIED;
        if (!keepUserSettings) for (auto& tip : s.legacyTip) if (tip.settingsOnly) tip = {};
        s.machineTip[0] = s.machineTip[1] = false; return S_OK;
    }
    HRESULT SwitchAway() override {
        HRESULT hr = Call("SwitchAway"); if (hr == S_OK) s.active = false; return hr;
    }
    HRESULT RemoveDisabledUserSettings(const std::wstring& dll) override {
        HRESULT hr = Call("RemoveDisabledUserSettings"); if (FAILED(hr)) return hr;
        if (!RemovableUserSettings(s, dll) && !(admin && maintenance && OrphanDisabledUserSettings(s))) return E_ACCESSDENIED;
        if (!keepDisabledSettings) for (auto& tip : s.legacyTip) tip = {};
        if (disabledProfileEcho && !keepDisabledSettings)
            s.profile = s.registeredProfile = s.keyboard = s.registeredKeyboard = false;
        return S_OK;
    }
    UserBindingResult Enable(bool enable) override {
        const HRESULT request = Call(enable ? "Enable" : "Disable");
        auto result = ChangeUserBinding(enable, [&](bool value) {
            HRESULT hr = Call("EnableLanguageProfile");
            if (languageFailure || FAILED(request) || FAILED(hr)) return E_FAIL;
            if (!keepEnabled) s.enabled = value;
            return S_OK;
        }, [&](bool value) {
            HRESULT hr = Call("InstallLayoutOrTip");
            if (layoutFailure || FAILED(request) || FAILED(hr)) return E_FAIL;
            if (!keepEnabled) {
                s.enabled = value;
                if (!value && settingsOnDisable) for (auto& tip : s.legacyTip)
                    tip.exists = tip.settingsOnly = tip.settingsDisabled = tip.settingsExactDisabled = true;
                for (auto& tip : s.legacyTip) if (tip.settingsOnly) tip.settingsDisabled = !value;
            }
            return S_OK;
        });
        if (freshLayout) result.preferences = result.layout;
        else if (!enable || result.Status() == S_OK)
            result.preferences = Call(enable ? "UserLanguageList.Enable" : "UserLanguageList.Disable");
        return result;
    }
};
}
int wmain() {
    unsigned failed = 0;
    auto check = [&](bool ok, const char* name) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failed; };
    {
        UserBindingResult original{S_OK, S_OK};
        check(original.preferences == S_OK && original.Status() == S_OK,
            "two-field fake binding result retains default successful preferences");
        const auto script = UserProfileBindingScript(true);
        const auto call = script.find(L"$api.Change($apiTip, $enable)");
        const auto skip = script.find(L"if (-not $enable -and -not $ownControlProfile)");
        check(script.find(L"$registryTip = '0804:" + GuidString(VoiceClsid) + GuidString(VoiceProfile) + L"'") != script.npos &&
            script.find(L"$apiTip = '0x' + $registryTip") != script.npos && call != script.npos &&
            script.find(L"$api.Change(", call + 1) == script.npos && skip != script.npos &&
            skip < script.find(L"$api = [VoiceImeTargetInputApi]::new()") &&
            script.find(L"Set-WinUserLanguageList") == script.npos && script.find(L"Get-WinUserLanguageList") == script.npos,
            "fresh writer uses one documented API TIP; only disable may skip before loading input.dll");
        const auto guardCode = script.find(L"$code = 16", call);
        const auto guard = script.find(L"if ((Protected-Registry) -cne $before)", call);
        const auto outcome = script.find(L"$code = if ($changed) { 0 } else { 13 }", call);
        check(script.find(L"$path -ieq $targetPath -and $name -ieq $registryTip") != script.npos &&
            script.find(L"InputMethodTips = @($tips | Sort-Object Value | ForEach-Object { $_.Name })") != script.npos &&
            guardCode != script.npos && guard != script.npos && outcome != script.npos &&
            call < guardCode && guardCode < guard && guard < outcome,
            "fixed script guards non-own TIP order and postchecks even a FALSE API result");
    }
    {
        Memory m; m.freshLayout = true;
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 0 && std::count(m.calls.begin(), m.calls.end(), "EnableLanguageProfile") == 1 &&
            std::count(m.calls.begin(), m.calls.end(), "InstallLayoutOrTip") == 1,
            "fresh layout contract calls legacy and fresh binding once for enable");
        m.layoutFailure = true; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 4 && std::string(r.stage) == "UserLanguageList.Disable" && !m.s.UserDetached() &&
            m.s.machine[0].owned && m.s.host[0].owned && !m.Called("UnregisterProfile") && !m.Called("RemoveKeys") &&
            !m.Called("EnableLanguageProfile") &&
            std::count(m.calls.begin(), m.calls.end(), "InstallLayoutOrTip") == 1,
            "fresh uninstall failure preserves profile state and ownership before legacy detachment");
        m.layoutFailure = false; m.languageFailure = true; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && std::count(m.calls.begin(), m.calls.end(), "EnableLanguageProfile") == 1 &&
            std::count(m.calls.begin(), m.calls.end(), "InstallLayoutOrTip") == 1,
            "legacy uninstall failure still calls fresh binding once and requires final empty state");
    }
    {
        std::vector<std::string> calls;
        const auto success = ChangeUserBinding(false,
            [&](bool) { calls.push_back("language"); return S_OK; },
            [&](bool) { calls.push_back("layout"); return S_OK; });
        check(success.Status() == S_OK && calls == std::vector<std::string>{"layout", "language"},
            "disable removes user binding before legacy profile disable");
        calls.clear();
        const auto failure = ChangeUserBinding(false,
            [&](bool) { calls.push_back("language"); return S_OK; },
            [&](bool) { calls.push_back("layout"); return E_FAIL; });
        check(failure.Status() != S_OK && calls == std::vector<std::string>{"layout"},
            "fresh failure never proactively disables the legacy profile");
    }
    {
        RegistrationFacts before{1, 0, 1, 1, 0, 0}, after;
        auto text = FormatFacts(before);
        check(ParseFacts(text + "\r\n", after) && FormatFacts(after) == text,
            "fresh process facts preserve disabled profile and every independent field");
        check(!ParseFacts(text + " extra", after) && !ParseFacts("tsf-facts profile=2", after) &&
            !ParseFacts("", after), "incomplete or noncanonical fresh-process facts fail closed");
        TF_INPUTPROCESSORPROFILE p{}; p.dwProfileType = TF_PROFILETYPE_INPUTPROCESSOR;
        p.langid = 0x0804; p.clsid = VoiceClsid; p.guidProfile = VoiceProfile;
        p.catid = GUID_TFCAT_TIP_KEYBOARD; p.dwCaps = 0x1234; p.dwFlags = TF_IPP_FLAG_ENABLED;
        const auto fields = ProfileFields(p);
        check(fields.find("langid=0x0804") != fields.npos && fields.find("exact=1") != fields.npos &&
            fields.find("dwCaps=0x00001234") != fields.npos && fields.find("dwFlags=") != fields.npos &&
            fields.find("hklSubstitute=") != fields.npos && fields.find("guidProfile=") != fields.npos && fields.find("catid=") != fields.npos,
            "diagnostics format real SDK fields without hand-packed ABI assumptions");
        check(TraceText(L"line\n\"\\") == "\"line\\u000A\\u0022\\u005C\"" &&
            ProfileValueData(nullptr, L"Token", REG_SZ, 128) == "<redacted>" &&
            ProfileValueData(nullptr, L"Signature", REG_BINARY, 32) == "<redacted>" &&
            ProfileValueData(nullptr, L"IconFile", REG_SZ, 128) == "<redacted>" &&
            ProfileValueData(nullptr, L"Enable", REG_SZ, 4) == "<redacted>",
            "diagnostics escape injected names and do not read token/signature/DLL/unknown or wrong-type data");
    }
    {
        Memory m; m.admin = false;
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 10 && m.calls.size() == 1 && m.s.Empty(), "non-admin refuses before all reads/load/write APIs");
        m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && r.already && m.calls.size() == 1, "absent uninstall is read-only and needs no elevation");
    }
    {
        Memory m; m.fail = "Read";
        auto r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && !r.already && !m.Called("RemoveKeys"), "unknown query never becomes already-unregistered");
    }
    {
        Memory m;
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 0 && std::string(State(m.s, Dll)) == "registered", "first install requires profile/category enumeration and current-user enablement");
        check(FormatStatus(Status(m.s, Dll, false)) ==
            "{\"registered\":true,\"state\":\"registered\",\"requiresAdmin\":true,\"maintenance\":false}" &&
            FormatStatus(Status(m.s, Dll, true)) ==
            "{\"registered\":true,\"state\":\"registered\",\"requiresAdmin\":true,\"maintenance\":true}",
            "installed registration remains verifiable while independent maintenance gate blocks listening");
        check(std::find(m.calls.begin(), m.calls.end(), "RegisterProfile") < std::find(m.calls.begin(), m.calls.end(), "RegisterCategory"),
            "official SampleIME order registers profile before categories");
        m.calls.clear(); r = Register(m, Dll, &Host);
        check(r.ExitCode() == 0 && !m.Called("RegisterProfile") && !m.Called("RegisterCategory") && !m.Called("Enable"), "existing shared profile and enablement are not rewritten");
        m.s.machine[1] = Owned(Other); m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && !m.s.machine[0].exists && m.s.machine[1].exists && m.s.profile &&
            m.s.category && m.s.host[0].owned && !m.Called("Disable") && !m.Called("UnregisterProfile") && !m.Called("UnregisterCategory") && !m.Called("UnregisterServiceClass"),
            "one architecture uninstall preserves shared profile/category/HostExe");
        m.s.machine[0] = m.s.machine[1]; m.s.machine[1] = {}; m.calls.clear(); r = Unregister(m, Other);
        check(r.ExitCode() == 0 && m.s.Empty() && m.Called("Disable") && m.Called("UnregisterProfile") && m.Called("UnregisterCategory"),
            "last architecture removes official profile/category and owned bound host");
        check(std::find(m.calls.begin(), m.calls.end(), "SwitchAway") < std::find(m.calls.begin(), m.calls.end(), "Disable") &&
            std::find(m.calls.begin(), m.calls.end(), "Disable") < std::find(m.calls.begin(), m.calls.end(), "UnregisterProfile"),
            "last architecture switches away and unbinds user before machine teardown");
        m.calls.clear(); r = Unregister(m, Other);
        check(r.ExitCode() == 0 && r.already && m.calls.size() == 1, "repeated completed uninstall is read-only and idempotent");
    }
    {
        Memory m; m.s.machine[0] = Owned(L"C:\\another-install\\win-x64\\ime.dll");
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 4 && !m.Called("AddKeys"), "different registered path is refused before writes");
        r = Unregister(m, Dll);
        check(r.ExitCode() == 4 && !m.Called("RemoveKeys"), "stale uninstall cannot delete a different installation");
    }
    {
        Memory m; m.s.user[0] = Owned(Dll); m.s.legacyTip[0] = Owned(Dll);
        auto r = Register(m, Dll, &Host);
        check(std::string(r.stage) == "LegacyUserCleanupRequired" && !m.Called("AddKeys"), "HKCU override must be explicitly cleaned first");
        m.admin = false; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && m.Called("RemoveLegacy") && !m.Called("Disable") && !m.Called("UnregisterProfile"),
            "owned path-matched legacy cleanup uses no machine/user-input API");
        m.s.user[0] = Owned(Dll); m.s.user[0].owned = false; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 4 && !m.Called("RemoveLegacy"), "foreign legacy key is never removed");
    }
    {
        Memory m; m.fail = "RegisterProfile"; m.failedProfileLeavesClass = true;
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 4 && std::string(r.stage) == "RegisterProfile" && m.s.Empty(), "first API failure compensates only new COM/category/host");
    }
    {
        Memory m; m.s.machine[1] = Owned(Other); m.s.profile = m.s.keyboard = m.s.category = true;
        m.s.host[0] = m.s.host[1] = Owned(Host);
        m.s.host[0].binding = m.s.host[1].binding = InstallRoot(Dll);
        m.fail = "Enable";
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 4 && !m.s.machine[0].exists && m.s.machine[1].owned && m.s.profile && m.s.category &&
            m.s.host[0].owned && m.s.host[1].owned && !m.Called("UnregisterProfile") && !m.Called("UnregisterCategory"),
            "second architecture failure removes only new COM; existing shared registration/host retained");
    }
    {
        Memory m; m.admin = false; m.s.legacyTip[0] = Owned(Dll);
        auto r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && !m.Called("UnregisterProfile"), "owned path-matched orphan legacy TIP can be cleaned without machine APIs");
        m.s.user[0] = Owned(Dll); m.s.user[1] = Owned(Other); m.s.legacyTip[0] = Owned(Dll); m.calls.clear();
        r = Unregister(m, Dll);
        check(std::string(r.stage) == "LegacyCleanupOrder" && !m.Called("RemoveLegacy"),
            "keep legacy COM anchor for shared TIP until other legacy architecture is cleaned");
    }
    {
        Memory m; m.s.machine[0] = Owned(Dll); m.s.category = true;
        auto r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && !m.Called("UnregisterProfile") && m.Called("UnregisterCategory"),
            "category-only partial installation can finish uninstall without nonexistent profile API");
    }
    {
        Memory m; m.fail = "Enable"; m.failUndo = "UnregisterProfile";
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 5 && FAILED(r.rollback) && m.s.machine[0].owned && !m.s.host[0].exists,
            "compensation failure reported separately; COM ownership retained for retry");
    }
    {
        Memory m; m.s.machine[0] = Owned(Dll); m.s.machineTip[0] = true;
        auto r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && m.Called("UnregisterServiceClass"),
            "last owned empty TSF class is removed through official class API");
        m.s.machine[0] = Owned(Dll); m.s.machineTip[0] = true; m.calls.clear(); m.fail = "UnregisterServiceClass";
        r = Unregister(m, Dll);
        check(r.ExitCode() == 4 && std::string(r.stage) == "UnregisterServiceClass" && m.s.machine[0].owned &&
            m.s.machineTip[0] && !m.Called("RemoveKeys"), "class API failure retains exact COM owner for retry");
        m.fail = ""; r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty(), "retry completes interrupted empty class uninstall");
    }
    {
        Memory m; m.fail = "RegisterProfile"; m.failedProfileLeavesClass = true; m.failUndo = "UnregisterServiceClass";
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 5 && std::string(r.rollbackStage) == "RollbackServiceClass" && m.s.machine[0].owned &&
            m.s.machineTip[0] && !m.s.host[0].exists, "new class compensation failure keeps owner but removes new host");
        m.fail = m.failUndo = ""; r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty(), "explicit uninstall recovers failed first-install class compensation");
    }
    {
        Memory m; m.s.machine[0] = Owned(Dll); m.s.machineTip[0] = true; m.fail = "RegisterProfile";
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 4 && m.s.machine[0].owned && m.s.machineTip[0] && !m.Called("UnregisterServiceClass"),
            "registration rollback never removes a pre-existing class root");
    }
    {
        Memory m; m.s.machineTip[0] = true;
        auto r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && !m.Called("UnregisterServiceClass"), "class without COM owner is never removed");
        m.s.machine[0] = Owned(Dll); m.s.profileKeys = true; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && m.Called("UnregisterProfile"),
            "exact hidden machine profile key takes official profile uninstall, never manual machine deletion");
        m.s.machine[0] = Owned(Dll);
        m.s.profileKeys = false; m.s.otherProfiles = true; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && !m.Called("UnregisterServiceClass") && !m.Called("UnregisterProfile"),
            "other language or profile under same CLSID blocks shared class teardown");
        m.s.otherProfiles = false; m.s.otherCategories = true; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && !m.Called("UnregisterServiceClass"), "other category blocks class teardown");
    }
    {
        Memory m; m.s.machine[0] = Owned(Dll); m.s.machineTip[0] = true;
        m.s.legacyTip[0].exists = m.s.legacyTip[0].settingsOnly = true;
        auto r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && m.Called("Disable") && m.Called("UnregisterServiceClass") && !m.Called("RemoveLegacy"),
            "non-enumerable profile with official settingsOnly still unbinds, never treated as legacy");
        m.s.machine[0] = Owned(Dll); m.s.legacyTip[0].exists = m.s.legacyTip[0].settingsOnly = true;
        m.keepUserSettings = true; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && m.Called("RemoveDisabledUserSettings") && !m.Called("RemoveLegacy"),
            "API-retained disabled preferences clean only after exact owned machine class is absent");
    }
    {
        Memory m; m.s.machine[0] = Owned(Dll); m.s.machineTip[0] = true;
        m.s.legacyTip[0].exists = m.s.legacyTip[0].settingsOnly = true;
        m.languageFailure = m.keepUserSettings = true;
        auto r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && m.Called("EnableLanguageProfile") && m.Called("InstallLayoutOrTip") &&
            m.Called("RemoveDisabledUserSettings"), "failed EnableLanguageProfile still attempts ILOT_UNINSTALL and requires observed disabled state");
        m.s.machine[0] = Owned(Dll); m.s.legacyTip[0].exists = m.s.legacyTip[0].settingsOnly = true;
        m.layoutFailure = true; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && m.s.machine[0].owned && m.Called("InstallLayoutOrTip") && !m.Called("RemoveKeys") &&
            !m.Called("RemoveDisabledUserSettings"), "both unbind APIs fail with enabled preferences: never false success or delete ownership");
    }
    {
        Memory m; m.s.machine[0] = Owned(Dll); m.s.enabled = true; m.keepEnabled = true;
        auto r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && m.s.machine[0].owned && !m.Called("UnregisterProfile"),
            "API success without observed user detachment cannot tear down the machine class");
        m.keepEnabled = false; m.s.enabled = false; m.s.active = true; m.fail = "SwitchAway"; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && m.s.machine[0].owned && !m.Called("Disable") && !m.Called("RemoveKeys"),
            "no safe active-profile fallback preserves COM and stops before unbinding or deletion");
    }
    {
        Memory m; m.s.machine[0] = Owned(Dll); m.keepCom = true;
        auto r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && std::string(r.stage) == "UnregisterFinalPostcheck" && m.s.machine[0].owned,
            "COM removal success without final absence is still a failed uninstall");
        m.keepCom = false; m.s.legacyTip[0].exists = m.s.legacyTip[0].settingsOnly = m.s.legacyTip[0].settingsDisabled = true;
        m.fail = "RemoveDisabledUserSettings"; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && m.s.machine[0].owned && !m.Called("RemoveKeys"),
            "preference cleanup failure retains COM anchor for retry");
        m.fail = ""; r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty(), "retry cleans interrupted exact disabled preference uninstall to Empty");
        Snapshot foreign; foreign.machine[0] = Owned(Dll); foreign.legacyTip[0].exists = true;
        check(!RemovableUserSettings(foreign, Dll), "unknown user profiles/categories/values forbid exact preference cleanup");
        foreign.legacyTip[0].settingsOnly = true;
        check(!RemovableUserSettings(foreign, Dll), "Enable=1 preferences are never eligible for direct deletion");
        foreign.legacyTip[0].settingsDisabled = true; foreign.otherCategories = true;
        check(!RemovableUserSettings(foreign, Dll), "foreign category forbids disabled preference cleanup");
        foreign.otherCategories = false; foreign.otherProfiles = true;
        check(!RemovableUserSettings(foreign, Dll), "foreign language/profile forbids disabled preference cleanup");
        foreign.otherProfiles = false; foreign.machine[1] = Owned(Other);
        check(!RemovableUserSettings(foreign, Dll), "other architecture forbids shared disabled preference cleanup");
    }
    {
        Memory m; auto r = Register(m, Dll, &Host);
        m.leaveClass = false; m.settingsOnDisable = true; m.disabledProfileEcho = true;
        r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && m.Called("RemoveDisabledUserSettings"),
            "disabled exact user profile echo after machine removal cleans under COM anchor then confirms empty");
        Snapshot blocked; blocked.machine[0] = Owned(Dll); blocked.profile = blocked.registeredProfile = true;
        blocked.legacyTip[0].exists = blocked.legacyTip[0].settingsOnly = blocked.legacyTip[0].settingsDisabled = true;
        check(!RemovableUserSettings(blocked, Dll), "profile echo without exact Enable=0 evidence cannot authorize cleanup");
        blocked.legacyTip[0].settingsExactDisabled = true; blocked.active = true;
        check(!RemovableUserSettings(blocked, Dll), "active profile echo cannot authorize preference deletion");
        blocked.active = false; blocked.machineTip[0] = true;
        check(!RemovableUserSettings(blocked, Dll), "machine registration must be absent before profile echo cleanup");
    }
    {
        TF_INPUTPROCESSORPROFILE p{}; p.dwProfileType = TF_PROFILETYPE_INPUTPROCESSOR; p.langid = 0x0804;
        p.catid = GUID_TFCAT_TIP_KEYBOARD; p.dwFlags = TF_IPP_FLAG_ENABLED;
        p.clsid = CLSID_TF_ThreadMgr; p.guidProfile = IID_ITfContext;
        check(SafeDefault(p, 0x0804, p.clsid, p.guidProfile), "only existing enabled non-component default is eligible for activation");
        p.dwFlags = 0;
        check(!SafeDefault(p, 0x0804, p.clsid, p.guidProfile) && ExactSuccess(S_FALSE) == E_FAIL,
            "disabled fallback and S_FALSE activation are not success");
        p.dwFlags = TF_IPP_FLAG_ENABLED; p.clsid = VoiceClsid;
        check(!SafeDefault(p, 0x0804, p.clsid, p.guidProfile), "no fallback can activate any profile belonging to own class");
        p.clsid = CLSID_TF_ThreadMgr;
        check(SafeAlternative(p, 0x0804) && !SafeDefault(p, 0x0804, VoiceClsid, VoiceProfile),
            "retiring legacy default may switch only to an existing enabled alternative without resetting default");
        p.dwFlags = 0;
        check(!SafeAlternative(p, 0x0804), "alternative fallback never enables a disabled profile");
    }
    {
        Memory m; m.leaveClass = false; auto r = Register(m, Dll, &Host);
        m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && m.s.Empty() && !m.Called("UnregisterServiceClass"),
            "no redundant class API when UnregisterProfile already removed the service");
    }
    {
        Memory m; m.hideProfile = true;
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() != 0 && !m.Called("Enable"), "API success without enumeration is never install success");
    }
    {
        Memory m; m.userEnumerationNeedsEnable = true;
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 0 && m.Called("Enable") && std::string(State(m.s, Dll)) == "registered",
            "exact machine profile is enabled before mandatory current-user enumeration");
    }
    {
        Memory m; m.hideUserEnumeration = true;
        m.keepUserSettings = m.settingsOnDisable = true;
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() == 4 && r.rollback == S_OK && m.Called("Enable") && m.Called("Disable") && m.s.Empty() &&
            m.Called("UnregisterServiceClass") && m.Called("RemoveDisabledUserSettings") &&
            std::find(m.calls.begin(), m.calls.end(), "RemoveDisabledUserSettings") < std::find(m.calls.begin(), m.calls.end(), "RemoveKeys"),
            "final enum failure with class API retaining HKCU Enable=0 cleans settings BEFORE dropping COM");
        for (bool retained : {false, true}) {
            Memory broken; broken.hideUserEnumeration = broken.keepUserSettings = broken.settingsOnDisable = true;
            broken.keepDisabledSettings = retained;
            if (!retained) broken.failUndo = "RemoveDisabledUserSettings";
            r = Register(broken, Dll, &Host);
            check(r.ExitCode() == 5 && FAILED(r.rollback) && broken.s.machine[0].owned && broken.s.legacyTip[0].exists &&
                !broken.s.host[0].exists, retained ?
                "cleanup S_OK but residue remains: rollback error retains COM, never reports clean" :
                "cleanup failure: rollback error retains COM for normal uninstall retry");
            broken.failUndo = ""; broken.keepDisabledSettings = false; broken.hideUserEnumeration = false;
            r = Unregister(broken, Dll);
            check(r.ExitCode() == 0 && broken.s.Empty(), "normal uninstall converges failed preference rollback using retained COM");
        }
    }
    {
        auto orphan = [] {
            Memory m; m.keepUserSettings = true;
            for (auto& tip : m.s.legacyTip)
                tip.exists = tip.settingsOnly = tip.settingsDisabled = tip.settingsExactDisabled = true;
            return m;
        };
        Memory m = orphan(); auto r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && !r.already && m.s.Empty() && m.Called("RecoveryMaintenance") && m.Called("Validate") &&
            m.Called("RemoveDisabledUserSettings") && !m.Called("RemoveKeys") && !m.Called("Disable") &&
            !m.Called("UnregisterProfile") && !m.Called("UnregisterServiceClass"),
            "admin + maintenance + validated package recovers ONLY exact orphan Enable=0 without TSF/COM writes");
        m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 0 && r.already && m.calls.size() == 1, "recovered orphan uninstall is read-only idempotent");
        for (unsigned scenario = 0; scenario != 18; ++scenario) {
            m = orphan();
            switch (scenario) {
            case 0: m.admin = false; break;
            case 1: m.maintenance = false; break;
            case 2: m.fail = "RecoveryMaintenance"; break;
            case 3: m.fail = "Validate"; break; // missing/untrusted DLL is NEVER bypassed
            case 4: m.s.machineTip[1] = true; break;
            case 5: m.s.profileKeys = true; break;
            case 6: m.s.registeredProfile = true; break;
            case 7: m.s.category = true; break;
            case 8: m.s.otherProfiles = true; break;
            case 9: m.s.otherCategories = true; break;
            case 10: m.s.user[1].exists = true; break;
            case 11: m.s.legacyTip[1].settingsOnly = false; break; // extra value/type/profile
            case 12: m.s.legacyTip[1].settingsDisabled = false; break;
            case 13: m.s.legacyTip[1].settingsExactDisabled = false; break; // empty partial tree
            case 14: m.s.active = true; break;
            case 15: m.s.host[1].exists = true; break;
            case 16: m.fail = "Read"; break;
            case 17: m.fail = "Lock"; break;
            }
            r = Unregister(m, Dll);
            check(r.ExitCode() != 0 && m.s.legacyTip[0].exists && !m.Called("RemoveDisabledUserSettings") &&
                !m.Called("RemoveKeys") && !m.Called("Disable"), "orphan recovery denies missing authority/unknown/foreign/nonexact state");
        }
        for (bool retained : {false, true}) {
            m = orphan(); m.keepDisabledSettings = retained;
            if (!retained) m.fail = "RemoveDisabledUserSettings";
            r = Unregister(m, Dll);
            check(r.ExitCode() != 0 && m.s.legacyTip[0].exists && !r.already,
                "orphan deletion failure or nonempty readback cannot report uninstall success");
        }
        m = orphan(); m.fail = "Validate"; m.s.otherProfiles = true;
        r = Unregister(m, Dll);
        check(r.ExitCode() != 0 && !m.Called("Validate") && !m.Called("RemoveDisabledUserSettings"),
            "lost package plus foreign unknown never enters an orphan cleanup bypass");
        check(OwnEnableValue(L"Enable", REG_DWORD, 4, 0) && OwnEnableValue(L"Enable", REG_DWORD, 4, 1) &&
            !OwnEnableValue(L"Other", REG_DWORD, 4, 0) && !OwnEnableValue(L"Enable", REG_SZ, 4, 0) &&
            !OwnEnableValue(L"Enable", REG_DWORD, 8, 0) && !OwnEnableValue(L"Enable", REG_DWORD, 4, 2),
            "production leaf parser refuses other value names/types/lengths/data; only DWORD zero qualifies as disabled");
    }
    {
        Memory m; m.s.machine[1] = Owned(Other); m.s.profile = m.s.category = m.s.keyboard = m.s.enabled = true;
        m.s.host[0] = m.s.host[1] = Owned(Host); m.fail = "Read";
        auto r = Register(m, Dll, &Host);
        check(r.ExitCode() != 0 && m.s.machine[1].owned && m.s.host[0].owned && !m.Called("UnregisterProfile"),
            "pre-existing other architecture and host survive query failure");
        m.fail = ""; m.admin = false; m.calls.clear(); r = Unregister(m, Dll);
        check(r.ExitCode() == 10 && !m.Called("RemoveKeys") && !m.Called("Disable"), "partial machine uninstall requires admin before writes");
    }
    {
        Memory m; auto r = Register(m, Dll, &Host);
        m.languageFailure = true; m.fail = "UserLanguageList.Disable"; m.calls.clear();
        r = Unregister(m, Dll);
        const bool retained = r.ExitCode() == 4 && std::string(r.stage) == "UserLanguageList.Disable" &&
            m.s.UserDetached() && m.s.machine[0].owned && m.s.host[0].owned &&
            !m.Called("UnregisterProfile") && !m.Called("RemoveKeys");
        m.fail = ""; r = Unregister(m, Dll);
        check(retained && r.ExitCode() == 0 && m.s.Empty(),
            "language-list disable error is never tolerated as legacy unbind failure; COM retained and retry succeeds");
    }
    {
        Memory m; m.hideUserEnumeration = true;
        m.fail = "UserLanguageList.Enable"; m.failUndo = "UserLanguageList.Disable";
        auto r = Register(m, Dll, &Host);
        const bool retained = r.ExitCode() == 5 && std::string(r.stage) == "UserLanguageList.Enable" &&
            std::string(r.rollbackStage) == "RollbackUserEnable" && FAILED(r.rollback) &&
            m.s.machine[0].owned && !m.s.host[0].exists && !m.s.UserEntry() && !m.s.HasProfile();
        m.fail = m.failUndo = ""; m.calls.clear(); r = Unregister(m, Dll);
        check(retained && r.ExitCode() == 0 && m.s.Empty() && m.Called("UserLanguageList.Disable"),
            "language-list rollback error retains COM; retry unbinds even with no visible TSF user entry");
    }
    std::printf("RESULT failures=%u; memory backend only; no registry/TSF registration/config/cloud\n", failed);
    return failed ? 1 : 0;
}