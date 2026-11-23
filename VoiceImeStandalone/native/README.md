# 阶段 1：C++ 高性能进程内 TSF 输入法与原生悬浮条（`VoiceImeStandalone/native`）

本目录承载独立版语音输入法的**阶段 1（进程内 C++17 原生层）**，负责直接注入任意 Windows 桌面与 UWP/WinUI 目标进程，提供零托管开销、零 GC 停顿的原生 `ITfComposition` 流式组合区输入与 GDI+ 桌面悬浮条。

## 核心职责与增强点

1. **完全独立的组件身份（`StandaloneIdentity.h`）**
   - CLSID：`{7D4F62C1-8B34-4E2A-9C51-3F8A21D70E64}`
   - Profile GUID：`{C28419E3-5A76-4B18-8F29-6E03B5A94C82}`
   - 安装根路径：`%ProgramFiles%\TrueFluentVoiceIme\<64位包SHA256>\<win-x64|win-x86|win-arm64x>\VoiceImeStandalone.dll`
   - 命名管道前缀：`TrueFluentVoiceIme.Shared.<SessionId>.<SID>`
   - 与主程序 `TrueFluentPro` 内置输入法物理隔离，支持同机并存互不干扰。

2. **多架构与 Windows 11 on ARM (`ARM64X`) 支持（`Build.ps1`）**
   - 支持一键编译 `win-x64`、`win-x86`、`win-arm64` 以及双 ABI 混合二进制 `win-arm64x`（同时兼容 ARM64 原生进程与 x64 仿真进程共用单一 64 位 COM 槽位）。
   - 支持 `-LinkMode DynamicLibIme`（动态编译独立 `libIME2.dll`，便于商业发行履行 LGPL v2.1 义务）与 `-LinkMode Static`（单 DLL 静态链接）。

3. **UWP / AppContainer 沙盒兼容（`AppContainerAcl.h` + `Registration.cpp`）**
   - 注册 `GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT` 与 `GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT`。
   - 安装校验阶段自动为包目录写入显式 DACL，授予 `ALL APPLICATION PACKAGES` (`S-1-15-2-1`) 与受限包读取执行权限。

4. **无缝对接阶段 2 Rust 守护进程（`PipeClient.cpp` + `ToolbarBridge.cpp`）**
   - 未检测到共享管道时自动按需拉起 `TrueFluentVoiceImeHost.exe --daemon`（Rust 纯原生后台，无 GUI 框架开销）。
   - 导出 5 个无名称修饰 C ABI 函数（`VoiceImeToolbarCreate/Update/SetHistory/Run/Close`），由 Rust 守护进程的专用 STA 线程直接驱动原生悬浮条。
