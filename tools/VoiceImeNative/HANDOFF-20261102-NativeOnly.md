# 原生生命周期交接 · 2026-11-02

本轮只修改 `tools/VoiceImeNative`。未执行真实注册、卸载、语言 setter、提权、真实配置/凭据读取或系统项删除；无采音/云调用，未执行主工程/C#测试。主 agent 负责 live。

## 已实现

- 读取指定 23:14:58 live trace，确认类级两项注销成功不代表 HKCU 用户入口已消失。不可枚举但有 `settingsOnly` 的自有项现在仍解绑。
- `ChangeUserBinding` / `UserBindingResult`：EnableLanguageProfile 与 InstallLayoutOrTip 独立调用、独立结果；前项失败不跳过 ILOT_UNINSTALL。失败 HRESULT 留在各自 trace；必须观察到用户入口 disabled/absent 才继续，不把任意非零转成功。
- 末架构 `SwitchAway()` → 两项用户解绑 → 机器 profile/category/空类 → 精确禁用偏好收尾 → COM/owned HostExe → 最终 Empty。非末架构只移除对应 COM，保留共享项；旧 legacy 顺序规则保留。
- `RemoveDisabledUserSettings()`：只在精确 owned 最后架构机器 COM 路径/Apartment 锚点一致、系统 profile/category/类均无、无外来项/legacy COM、用户未 active/enabled 时，事务验证并删除本固定 CLSID 下仅固定 profile 的 Enable=0 或空树。Enable=1、额外值/子键、其他语言/profile/category 均拒绝；不删 CTF/TIP 父树，不写 TSF 登记。类/偏好清理失败保留 COM。
- `bool InstallationMaintenanceActive()`：从 HKLM registry64 的原生 ProgramFilesDir 解析统一 `TrueFluentPro\VoiceIme\maintenance.lock`，不在 hash 目录，不用 HKCU/env 配置。存在/未知均阻止新听写；`CheapAllowed`、收帧、实际 500ms WM_TIMER 停止当前 run 并隔离 generation；worker 读取 HostExe 前及 `LaunchHost` 的 CreateProcess 前各检查一次。C# host 继续共用此路径；Native 不创建/移除 marker，不新增服务/broker/daemon。

## 最终 helper 协议

- ABI 和 CLI verbs、退出码保持兼容。`--status <dllAbsolute>` 仍是一行 JSON，保留 `registered`、`state`、`requiresAdmin`，仅新增布尔 `maintenance`。
- 完整登记且无维护：`{"registered":true,"state":"registered","requiresAdmin":true,"maintenance":false}`。
- 相同登记但维护中：`{"registered":false,"state":"partial","requiresAdmin":true,"maintenance":true}`。state 集合不新增值；查询失败仍退出 6，不伪装未安装。
- 注册/卸载原有固定 stdout 标签不变。内部 `--tsf-facts` 增加第九字段 `active`，必须使用本次配套 DLL/helper；旧八字段严格拒绝。
- 新 trace 追加 `registeredProfile`、`registeredKeyboard`、`userTIP`、`settingsOnly`、`settingsDisabled`、`active`、`ownedCOM`；沿用调用方预建文件及 `TRUEFLUENT_VOICE_IME_TRACE`，不记录路径/识别正文/配置凭据。

## 最终构建/离线验证

有效报告：`artifacts/Final-20261102-NativeOnly/validation.json`，GeneratedUtc `2026-11-02T15:44:06.9808358Z`。实际进程退出 0，报告完整性及源码时间匹配均通过。

| 配置 | 架构 | 构建 | COM | 内存安装/卸载策略 | 注入路径生命周期 |
| --- | --- | --- | --- | --- | --- |
| Debug | x64 | 0 | 0 | 0 | 0 |
| Debug | x86 | 0 | 0 | 0 | 0 |
| Release | x64 | 0 | 0 | 0 | 0 |
| Release | x86 | 0 | 0 | 0 | 0 |

覆盖：不可枚举+settingsOnly、语言解绑失败但 ILOT 仍执行、API 成功但状态未解绑仍失败、末架构 Empty/幂等、非末架构保留共享、外来项拒绝、类/偏好失败保留锚点、无安全默认候选阻断、最终 COM 残留失败、维护 JSON 不误报可用、marker 存在/不存在对照、创建子进程前门禁、timer 取消及旧帧隔离。

产物在该目录的 `Debug|Release/win-x64|win-x86`，不要使用默认 `artifacts/validation.json` 的中断/空矩阵报告。`Verify.ps1` 已修复中断误报，且支持只限本目录 artifacts 下的独立输出根。

Release SHA-256：

| 文件 | SHA-256 |
| --- | --- |
| win-x64/TrueFluentVoiceIme.dll | eced3dcb1996caa63f8788364db6a327b595a5b35d3e7d0a6885bb8fd72aa51b |
| win-x64/VoiceImeRegister.exe | e78ebc9f0305e248f34367ee8d5d48cebc9031ca0b19add4bd8a0dfd022addb9 |
| win-x86/TrueFluentVoiceIme.dll | 05b06393460336182d60bd6c97b2efc92b012574f108407159682460393729c7 |
| win-x86/VoiceImeRegister.exe | acaedf0885d7b2e2f2527a4336455be0d585512ceeb5aa219174386165513f35 |

## live 剩余边界

- 官方 InstallLayoutOrTip 只承诺禁用，不承诺删除失效偏好；精确事务收尾是有 ownership 门禁的本组件策略，尚未对真实残留执行。
- SwitchAway 只选当前语言已启用的非本组件默认 text-service profile，不设置默认、不启用候选；未找到安全候选就 ERROR_BUSY，保留 COM，请先切到既有输入法再重试。本轮没有证明 previous-profile API，也未实测 FORSESSION 的桌面效果。
- WM_TIMER 依赖应用消息循环，不是硬实时承诺；检查文件与 CreateProcess 不能原子化，host 文件监视/timer 收口仍需 live。已发布但不包含门禁的旧 DLL 不能追溯修复；不遍历/清理其他账户 HKCU。
- STOP/EOF 的 C# host 真正退出、真实设备释放、标准安装→切换→听写→维护→卸载、其他输入法/默认项不被抢改、跨应用编辑均留给主 agent；本轮生命周期测试未启动真实 host，未冒充采音/云/跨应用通过。

依据见 README 的机制证据：[InstallLayoutOrTip](https://learn.microsoft.com/en-us/windows/win32/tsf/installlayoutortip)（UNINSTALL=禁用）、[EnableLanguageProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofiles-enablelanguageprofile)、[SampleIME Server.cpp](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/Server.cpp)（profile→category→COM）、[ActivateProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-activateprofile)（S_FALSE 未启用不能当成功）。