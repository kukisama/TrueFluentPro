#pragma once
// ═══════════════════════════════════════════════════════════════════════════
// TrueFluent Voice IME Standalone — 独立产品固定身份契约
// 与主程序内置版完全隔离（不同 CLSID / Profile / 注册表键 / 安装目录 / 管道前缀），
// 支持双产品在同一台 Windows 机器上安全共存且互不抢夺所有权。
// ═══════════════════════════════════════════════════════════════════════════

#include <windows.h>

namespace StandaloneIdentity {
    // 独立版 TSF COM CLSID: {7D4F62C1-8B34-4E2A-9C51-3F8A21D70E64}
    inline constexpr CLSID Clsid = {
        0x7d4f62c1, 0x8b34, 0x4e2a, {0x9c, 0x51, 0x3f, 0x8a, 0x21, 0xd7, 0x0e, 0x64}
    };

    // 独立版语言 Profile GUID: {C28419E3-5A76-4B18-8F29-6E03B5A94C82}
    inline constexpr GUID Profile = {
        0xc28419e3, 0x5a76, 0x4b18, {0x8f, 0x29, 0x6e, 0x03, 0xb5, 0xa9, 0x4c, 0x82}
    };

    // 显示名与所有权锚点
    inline constexpr wchar_t DisplayName[]      = L"译见pro语音输入法";
    inline constexpr wchar_t OwnerValue[]       = L"TrueFluentVoiceIme.Standalone.v1";
    inline constexpr wchar_t DllFileName[]      = L"VoiceImeStandalone.dll";
    inline constexpr wchar_t HelperFileName[]   = L"VoiceImeRegister.exe";
    inline constexpr wchar_t HostDaemonSwitch[] = L" --daemon";

    // 注册表与安装目录常量
    inline constexpr wchar_t ConfigKey[]        = L"Software\\TrueFluentVoiceIme";
    inline constexpr wchar_t UiRegKey[]         = L"Software\\TrueFluentVoiceIme\\UI";
    inline constexpr wchar_t InstallSubdir[]    = L"\\TrueFluentVoiceIme";
    inline constexpr wchar_t SharedPipePrefix[] = L"TrueFluentVoiceIme.Shared.";
    inline constexpr wchar_t InstallMutexName[] = L"Global\\TrueFluentVoiceIme.7D4F62C1.Install";
    inline constexpr wchar_t TraceEnvVar[]      = L"TRUEFLUENT_STANDALONE_IME_TRACE";
}
