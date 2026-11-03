#include "Common.h"
#include "RegistrationQuery.h"
#include <cstdio>

namespace {
int Failed(int fallback = 4) {
    auto error = LastRegistrationError();
    std::printf("failed stage=%s HRESULT=0x%08lX", error.stage, static_cast<unsigned long>(error.hr));
    if (FAILED(error.rollback)) std::printf(" rollbackStage=%s rollbackHRESULT=0x%08lX",
        error.rollbackStage, static_cast<unsigned long>(error.rollback));
    std::puts("");
    if (FAILED(error.rollback)) return 5;
    return std::string(error.stage) == "RequiresAdmin" ? 10 : fallback;
}
int Probe() {
    bool present = false, enabled = false, keyboard = false;
    if (FAILED(ProbeRegistration(present, enabled, keyboard))) return Failed(6);
    std::printf("probe enumerated=%d enabled=%d keyboard=%d; activation-and-editing-not-probed\n", present, enabled, keyboard);
    return present && enabled && keyboard ? 0 : 1;
}
}

int wmain(int argc, wchar_t** argv) {
    // No implicit command, elevation, shell execution, regsvr32, or credential output.
    if (argc < 3 && !(argc == 2 && (std::wstring(argv[1]) == L"--probe" || std::wstring(argv[1]) == L"--tsf-facts" ||
        std::wstring(argv[1]) == L"--tsf-diagnostics"))) {
        std::puts("usage: --register <dllAbsolute> <hostExeAbsolute> | --unregister <dllAbsolute> | --status <dllAbsolute> | --probe"); return 2;
    }
    try {
        if (argc == 2 && std::wstring(argv[1]) == L"--probe") return Probe();
        if (argc == 2 && std::wstring(argv[1]) == L"--tsf-diagnostics") {
            if (FAILED(QueryTsfRegistrationDiagnostics())) return Failed(6);
            std::puts("tsf-diagnostics-complete"); return 0;
        }
        if (argc == 2 && std::wstring(argv[1]) == L"--tsf-facts") {
            RegistrationFacts facts;
            if (FAILED(QueryTsfRegistrationFacts(facts))) return Failed(6);
            std::puts(VoiceRegistration::FormatFacts(facts).c_str()); return 0;
        }
        std::wstring command = argv[1], dll = argv[2]; HRESULT hr = E_INVALIDARG;
        if (command == L"--register" && argc == 4) {
            // Permission gate is inside RegisterCurrentUser, before LoadLibrary or any system write.
            std::wstring host = argv[3]; hr = RegisterCurrentUser(dll, &host);
        } else if (command == L"--unregister" && argc == 3) {
            hr = UnregisterCurrentUser(dll);
        }
        else if (command == L"--status" && argc == 3) {
            RegistrationStatus status;
            if (FAILED(QueryRegistration(dll, status))) return Failed(6);
            bool registered = std::string(status.state) == "registered";
            std::puts(VoiceRegistration::FormatStatus(status).c_str());
            return registered ? 0 : 1;
        } else { std::puts("invalid-arguments"); return 2; }
        if (FAILED(hr)) return Failed();
        std::puts(command == L"--register" ? "registered; default-input-unchanged" :
            RegistrationAlreadyAbsent() ? "already-unregistered" : "unregistered; files-retained");
        return 0;
    } catch (...) { std::puts("failed stage=Exception HRESULT=0x80004005"); return 6; }
}