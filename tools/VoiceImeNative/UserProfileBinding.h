#pragma once
#include "RegistrationQuery.h"
#include <wincrypt.h>
#pragma comment(lib, "Crypt32.lib")

namespace VoiceRegistration {
// Independently authored fixed-profile operation, not the GPL Weasel migration script.
// Fresh same-token writer: never initialize International/TSF enumeration before ILOT.
inline std::wstring UserProfileBindingScript(bool enable) {
    return std::wstring(enable ? L"$enable = $true\n" : L"$enable = $false\n") +
    L"$registryTip = '0804:" + GuidString(VoiceClsid) + GuidString(VoiceProfile) + L"'\n" + LR"PS(
$apiTip = '0x' + $registryTip
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version 2.0
$code = 10
$api = $null
$base = $null
function Registry-Values([string]$path, [string[]]$names, [bool]$language = $false) {
    $key = $base.OpenSubKey($path, $false)
    try {
        $tips = @()
        $rows = @(if ($null -ne $key) {
            foreach ($name in ($key.GetValueNames() | Sort-Object)) {
                if ($null -ne $names -and $names -cnotcontains $name) { continue }
                if ($language -and ($name -ieq 'CachedLanguageName' -or
                    ($path -ieq $targetPath -and $name -ieq $registryTip))) { continue }
                $row = [ordered]@{ Name = $name; Kind = $key.GetValueKind($name).ToString()
                    Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
                if ($language -and $row.Kind -eq 'DWord' -and
                    $name -match '^[0-9a-f]{4}:(?:[0-9a-f]{8}|\{[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}\{[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\})$') {
                    $tips += $row
                } else { $row }
            }
        })
        # Only TIP ranks are relative: retain every non-own TIP in rank order.
        # Unknown values, spelling/handwriting, defaults, Preload and Substitutes stay exact.
        return [ordered]@{ Exists = $null -ne $key; Values = $rows
            InputMethodTips = @($tips | Sort-Object Value | ForEach-Object { $_.Name }) }
    } finally { if ($null -ne $key) { $key.Dispose() } }
}
function Protected-Registry {
    $state = @((Registry-Values 'Control Panel\International\User Profile' @('Languages', 'Default')),
        (Registry-Values 'Keyboard Layout\Preload' $null), (Registry-Values 'Keyboard Layout\Substitutes' $null))
    foreach ($tag in $tags) {
        $state += [ordered]@{ LanguageTag = $tag
            Settings = (Registry-Values ('Control Panel\International\User Profile\' + $tag) $null $true) }
    }
    return Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $state -Depth 10 -Compress
}
try {
    if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or
        -not [Environment]::Is64BitProcess) { throw 'host' }
    $code = 11
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
    $root = $base.OpenSubKey('Control Panel\International\User Profile', $false)
    try {
        if ($null -eq $root -or $root.GetValueNames() -notcontains 'Languages' -or
            $root.GetValueKind('Languages') -ne [Microsoft.Win32.RegistryValueKind]::MultiString) { throw 'languages' }
        $tags = @($root.GetValue('Languages'))
        $targets = @(foreach ($tag in $tags) {
            $culture = [Globalization.CultureInfo]::GetCultureInfo($tag)
            if ($culture.IsNeutralCulture -and $culture.Name -ieq 'zh-Hans') {
                $culture = [Globalization.CultureInfo]::CreateSpecificCulture($culture.Name)
            }
            if (-not $culture.IsNeutralCulture -and $culture.LCID -eq 0x0804) { $tag }
        })
        if ($targets.Count -ne 1) { throw 'target-not-unique' }
    } finally { if ($null -ne $root) { $root.Dispose() } }
    $targetPath = 'Control Panel\International\User Profile\' + $targets[0]
    $target = $base.OpenSubKey($targetPath, $false)
    try {
        if ($null -eq $target) { throw 'target-language-missing' }
        $ownControlProfile = $target.GetValueNames() -icontains $registryTip
    } finally { if ($null -ne $target) { $target.Dispose() } }
    $code = 12
    $before = Protected-Registry
    if ((Protected-Registry) -cne $before) { throw 'concurrent-change' }
    if (-not $enable -and -not $ownControlProfile) {
        $code = 1 # Exact registry entry absent: read-only skip, not proof of TSF detachment.
    } else {
        $code = 15
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public sealed class VoiceImeTargetInputApi : IDisposable {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", ExactSpelling=true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    delegate bool Install([MarshalAs(UnmanagedType.LPWStr)] string psz, uint dwFlags);
    IntPtr module;
    Install install;
    public VoiceImeTargetInputApi() {
        module = LoadLibraryExW(Path.Combine(Environment.SystemDirectory, "input.dll"), IntPtr.Zero, 0x00000800);
        if (module == IntPtr.Zero) throw new InvalidOperationException("SystemInputUnavailable");
        IntPtr address = GetProcAddress(module, "InstallLayoutOrTip");
        if (address == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("TargetApiUnavailable"); }
        install = (Install)Marshal.GetDelegateForFunctionPointer(address, typeof(Install));
    }
    public bool Change(string tip, bool enable) { return install(tip, enable ? 0u : 1u); }
    public void Dispose() {
        if (module != IntPtr.Zero) { FreeLibrary(module); module = IntPtr.Zero; }
    }
}
'@
        $api = [VoiceImeTargetInputApi]::new()
        $code = 14
        # Exactly one fixed TIP with the documented 0x LangID; flags are 0 or ILOT_UNINSTALL.
        $changed = $api.Change($apiTip, $enable)
        $code = 16
        if ((Protected-Registry) -cne $before) { throw 'protected-registry-changed' }
        $code = if ($changed) { 0 } else { 13 } # FALSE is unspecified failure, not GetLastError.
    }
    # Disk guard only. External fresh-process input checks and native EnumProfiles/State/probe
    # remain mandatory; a successful BOOL is not a complete installation verdict.
} catch { # No broad compensation here: the outer native policy rolls back only this TIP.
} finally {
    if ($null -ne $base) { $base.Dispose() }
    if ($null -ne $api) { $api.Dispose() }
}
[Console]::WriteLine(('voice-target-tip:{0}' -f $code))
exit $code
)PS";
}
inline HRESULT ChangeUserProfileBinding(bool enable) {
    wchar_t windows[MAX_PATH]{};
    UINT size = GetWindowsDirectoryW(windows, ARRAYSIZE(windows));
    if (!size) return HRESULT_FROM_WIN32(GetLastError());
    if (size >= ARRAYSIZE(windows)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    std::wstring directory = windows;
#ifdef _WIN64
    directory += L"\\System32";
#else
    BOOL wow64 = FALSE;
    if (!IsWow64Process(GetCurrentProcess(), &wow64)) return HRESULT_FROM_WIN32(GetLastError());
    if (!wow64) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED); // This operation requires the native 64-bit host.
    directory += L"\\Sysnative";
#endif
    const auto exe = directory + L"\\WindowsPowerShell\\v1.0\\powershell.exe";
    const auto script = UserProfileBindingScript(enable);
    DWORD chars = 0;
    const auto bytes = reinterpret_cast<const BYTE*>(script.data());
    const auto count = static_cast<DWORD>(script.size() * sizeof(wchar_t)); // Windows wchar_t is UTF-16LE.
    constexpr DWORD flags = CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF;
    if (!CryptBinaryToStringW(bytes, count, flags, nullptr, &chars)) return HRESULT_FROM_WIN32(GetLastError());
    std::wstring encoded(chars, L'\0');
    if (!CryptBinaryToStringW(bytes, count, flags, encoded.data(), &chars)) return HRESULT_FROM_WIN32(GetLastError());
    encoded.resize(chars);
    std::wstring command = QuoteArgument(exe) + L" -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded;
    if (command.size() >= 32767) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    // Same process primary token, no runas/Explorer token, no PATH lookup, no temporary ps1.
    // NUL prevents either stream from filling a pipe; the script emits only a fixed safe code.
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    QueryHandle io, process, thread;
    io.value = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        &security, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (io.value == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = startup.hStdOutput = startup.hStdError = io.value;
    PROCESS_INFORMATION child{};
    // Sysnative selects the native executable from an x86 parent, but is not a
    // real directory in that x64 child's namespace (Add-Type/csc requires one).
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