# 译见pro · 独立语音输入法

本工具独立于 `TrueFluentPro`。它使用单独的 Windows TSF 输入法标识、本地配置和用量数据库，不需要主程序运行。

## 使用测试包

1. 在 Windows 10 / Windows 11 x64 上使用完整交付目录。保留两个 EXE、`TrueFluentVoiceImeSqlite.dll` 和 `voice-ime` 子目录，并确认已安装 WebView2 Runtime。
2. 普通权限运行 `TrueFluentVoiceImeTools.exe`。在设置页添加或主动导入自己的传统微软 Speech 订阅，先用「验证」确认区域与密钥可用。
3. 点击「安装 / 修复输入法」，由当前账户确认管理员授权。安装通过后，用 `Win+Space` 切换到「译见pro语音输入法」。
4. 在记事本的可编辑区域按 `F8`、说一句短句；确认文字实际写入后再按 `F8` 停止。切换到另一编辑器再试一次，检查停止后不再录音、切换时不串字。

工具窗可关闭；安装时会把私有 SQLite 一并复制到安装目录，之后不依赖 `dist`。卸载按钮只移除本组件的 Windows 输入法登记，保留程序文件、配置和统计。交付文件未签名，请仅使用来源可信的完整目录。不要以管理员身份运行来路不明的副本。

后台与用量报表共用一份随应用交付的 SQLite DLL，不依赖 Windows 自带的 `winsqlite3.dll`，不需要单独安装数据库，也不会迁移或合并已有数据库。旧版 Windows 和 Windows Server 的完整输入法流程尚未验收。

## Windows x64 Release 构建

从本目录执行 `./Pack-Release.ps1`，直接更新 `dist/` 中的交付文件，不创建时间目录或 ZIP。需要 Rust MSVC 工具链与 Visual Studio 的 C++ x64/x86 工具。交付包含 x64 工具/后台和 x64/x86 TSF 组件；ARM64 不在此交付的验证范围内。

`relink-source/native` 包含原生 DLL 的相关源码、libIME2 许可与构建材料。在其中运行 `Build.ps1`，指定绝对 `OutputDirectory`、`-Architectures x64,x86 -LinkMode Static -Version 1.0.0`，可用同一构建方式重编原生组件。