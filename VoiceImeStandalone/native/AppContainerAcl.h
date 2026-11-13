#pragma once
#include "Common.h"
#include <aclapi.h>
#include <sddl.h>

// Ensures installed TSF DLL directory has explicit read+execute permissions for:
// - SYSTEM (SY), Administrators (BA): Full Control
// - Authenticated Users (AU), Users (BU): Read & Execute
// - ALL APPLICATION PACKAGES (AC / S-1-15-2-1) & ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2): Read & Execute
// Required by Microsoft IME guidelines when GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT is registered.
namespace AppContainerAcl {
    inline constexpr wchar_t InstallDirSddl[] =
        L"D:PAI"
        L"(A;OICI;FA;;;SY)"
        L"(A;OICI;FA;;;BA)"
        L"(A;OICI;0x1200a9;;;BU)"
        L"(A;OICI;0x1200a9;;;AC)"
        L"(A;OICI;0x1200a9;;;S-1-15-2-2)";

    inline HRESULT EnsureInstallDirectoryReadableByAppContainer(const std::wstring& directory) {
        if (directory.empty()) return E_INVALIDARG;
        DWORD attrs = GetFileAttributesW(directory.c_str());
        if (attrs == INVALID_FILE_ATTRIBUTES) return HRESULT_FROM_WIN32(GetLastError());
        if ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) || !(attrs & FILE_ATTRIBUTE_DIRECTORY)) return E_ACCESSDENIED;

        PSECURITY_DESCRIPTOR sd = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                InstallDirSddl, SDDL_REVISION_1, &sd, nullptr)) {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        PACL dacl = nullptr;
        BOOL daclPresent = FALSE, daclDefaulted = FALSE;
        HRESULT hr = S_OK;
        if (!GetSecurityDescriptorDacl(sd, &daclPresent, &dacl, &daclDefaulted) || !daclPresent) {
            hr = HRESULT_FROM_WIN32(GetLastError());
        } else {
            DWORD status = SetNamedSecurityInfoW(
                const_cast<LPWSTR>(directory.c_str()),
                SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                nullptr, nullptr, dacl, nullptr);
            if (status != ERROR_SUCCESS) hr = HRESULT_FROM_WIN32(status);
        }
        LocalFree(sd);
        return hr;
    }
}
