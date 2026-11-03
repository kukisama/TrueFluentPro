# VoiceImeNative：真实 Windows 核验交接

## 结论

**尚不能作为可用系统输入法交付。** HKCU-only 注册在本机真实 TSF 枚举中失败；修正了 helper 的假成功，但没有伪造出可见 profile。官方 API 非虚拟化对照也失败。20:37 的无注册诊断已将内存 TextStore 宿主 Activate 失败精确定位为 `ITfKeystrokeMgr::AdviseKeyEventSink=0x80070057`；没有证据支持生产修复，partial/final 仍未执行，详见文末补充。

所有源文件修改与生成日志仅在 `tools/VoiceImeNative`。未运行根构建、修改主程序/Docs/设置页、复制覆盖 bin 产物、提权、安装、启动真实后台/麦克风/云识别或使用 RegOverridePredefKey。没有读取真实应用配置/凭据；配置键快照仅记录存在性和值名。

环境实测：Windows `10.0.26300.0`、PowerShell `7.6.9`、64 位验证控制进程、`Elevated=false`；MSVC 双架构 Debug。

## 固定目标与基线

- CLSID：`{5638E6E5-D18C-4FE8-B778-E59A5170B481}`。
- Profile：`{A91272E1-9973-43F9-8EB4-7D5494EC92B1}`，语言 `0x0804`。
- 首轮 HKCU/HKLM、Registry64/Registry32 的 COM/TIP 均不存在，HKCU HostExe 配置键也不存在。
- 两架构 `EnumProfiles(0)`：HRESULT `0`、total `35`、matched `0`、Next 以 S_FALSE 正常结束。
- 两架构 `EnumLanguageProfiles(0x0804)`：HRESULT `0`、total `3`、matched `0`。
- 即使组件不存在，`IsEnabledLanguageProfile` 仍返回 HRESULT `0` / TRUE，所以它不是有效的独立安装判据。
- 默认覆盖项始终为 `0804:{E7EA138E-69F8-11D7-A6EA-00065B844310}{E7EA138F-69F8-11D7-A6EA-00065B844311}`。
- 语言列表：`zh-Hans-CN`（上述已有 tip）、`zh-Hant-HK`（空 tips）。原首轮语言投影曾将列表归为一项；已改为显式逐语言遍历，原始总语言/tip 与后续逐语言快照一致。
- Preload：`1=00000804`、`2=00000409`；Substitutes 为空。CTF SortOrder/Assemblies 的全部前后树保存在报告中，未写入这些默认项。

首轮证据：`artifacts/registration-20261102-195652-191/report.json`。

## 已执行命令及退出码

以下均为本机实际命令，不是建议用户执行的安装命令。各子进程的绝对 EXE、完整参数、输出、退出码同时保存在相应 `commands.jsonl` 和 `report.json`。

### 原交付注册链路

控制命令：`pwsh -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeNative\Test-Registration.ps1 -ExerciseRegistration`。

实际调用的原二进制：

| 命令 | 退出码 |
| --- | ---: |
| `C:\Scripts\TranslationToolUI\bin\Debug\net10.0\voice-ime\win-x64\VoiceImeRegister.exe --register C:\Scripts\TranslationToolUI\bin\Debug\net10.0\voice-ime\win-x64\TrueFluentVoiceIme.dll C:\Scripts\TranslationToolUI\bin\Debug\net10.0\TrueFluentPro.exe` | 0 |
| `C:\Scripts\TranslationToolUI\bin\Debug\net10.0\voice-ime\win-x86\VoiceImeRegister.exe --register C:\Scripts\TranslationToolUI\bin\Debug\net10.0\voice-ime\win-x86\TrueFluentVoiceIme.dll C:\Scripts\TranslationToolUI\bin\Debug\net10.0\TrueFluentPro.exe` | 0 |
| 上述两架构 helper 分别 `--status` 对应绝对 DLL | 各 1 |
| `artifacts/Live/win-x64/VoiceImeRegister.exe --probe`、对应 x86 命令 | 各 1，两个枚举 matched 均 0 |
| 原 x86 helper `--unregister` 对应绝对 DLL；随后原 x64 helper 同命令 | 各 0 |

注册期间 COM/HostExe 存在，但 TIP 键已不存在，用户语言列表没有新增 profile。**旧 helper 的退出码 0 是假成功。** 本控制命令的终端回执曾只报告中断，不能以该回执声称正常退出；实际完整执行和 finally 以落盘报告为证，报告 ExitCode=1、Restored=true、FilesRetained=true。

证据：`artifacts/registration-20261102-195737-212/report.json`、`artifacts/registration-live-first.log`。

### 官方 API 与虚拟化对照

先执行：`pwsh -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeNative\Run-LiveValidation.ps1 -Mode Api`，进程退出码 **1**。

该轮 `VoiceImeTests.exe --registration-api <对应绝对 DLL>`：x64 退出 1 / RegisterProfile `0x80004005`；旧 x86 退出 0 / RegisterProfile、RegisterCategory、UnregisterCategory、UnregisterProfile 均 `0`。但旧 x86 测试 EXE 实测 `manifest=0`、`TokenVirtualizationEnabled=1`，**不将其当成真实系统注册成功**。

该轮证据：`artifacts/Live/Api-exit.json`、`artifacts/Live/Api-latest.log`、`artifacts/registration-20261102-200156-607/report.json`。首轮恢复比较未包含 VirtualStore，因此不能用那份 Restored=true 宣称没有虚拟化残留。

随后加入嵌入式 asInvoker manifest，实测 x64/x86 均 `manifest=1`、虚拟化为 `0`。

VirtualStore 补查只读控制命令曾被安全门禁降为只读：`pwsh -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeNative\Test-Registration.ps1 -OfficialApi`；退出码 0 表示只读核对完成，**没有重新调用注册 API**。证据为 `artifacts/registration-20261102-201144-393/report.json`。

发现固定 CLSID 下 8 个全空目录，时间全部为 `2026-11-02T12:01:57Z`，在上面官方 API 对照窗口内；没有剩余 profile GUID/注册值。已执行：

- `pwsh -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeNative\Audit-RegistrationResidue.ps1`：退出 0，只读。
- `pwsh -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeNative\Audit-RegistrationResidue.ps1 -RemoveNewEmptyResidue`：退出 0，精确门控后逆序逐叶删除这 8 个全空节点，`ExistsAfter=false`。

只删除精确 CLSID 子树中的空节点，没有删除 VirtualStore/CTF 父目录、任何有值节点、其他输入法或文件。证据：`artifacts/virtual-residue-audit.json`、`artifacts/virtual-residue-cleanup.json`。最初基线没有采集这个额外位置；该证据缺口明确保留，不把后补采集说成原始快照。

清理后再次执行上述 `Test-Registration.ps1 -OfficialApi`，此次控制进程退出码 **1**，确实调用官方 API：

| 架构 | 测试 EXE 退出码 | RegisterProfile（flags=0） | RAII finally UnregisterProfile |
| --- | ---: | --- | --- |
| x64 | 1 | `0x80004005` | `0x80004005` |
| x86 | 1 | `0x80004005` | `0x80004005` |

两架构均已禁用虚拟化；注册失败后没有 profile 可注销，finally 仍实际调用并记录 HRESULT。前后包含 VirtualStore 的快照完全相同，Restored=true、FilesRetained=true，枚举保持 matched=0。

最终证据：`artifacts/registration-20261102-201457-698/report.json`、`commands.jsonl`、`exit-code.txt`。**E_FAIL 不提供权限细分；当前未取得免提权成功方案，不能将它冒充 E_ACCESSDENIED 或断言所有系统必须提权。没有尝试 UAC、机器级安装或 LOCALPROCESS/LOCALTHREAD 假冒系统注册。**

### 修正版 helper 的 fail-closed 验收

控制命令：`pwsh -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeNative\Test-Registration.ps1 -ExerciseRegistration -BinaryDirectory C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\Live`。实际进程退出码 **1**，因为输入法仍不能交付，不把负向验收包装为安装成功。

| 命令 | x64 | x86 |
| --- | --- | --- |
| `artifacts/Live/<架构>/VoiceImeRegister.exe --register <对应绝对 DLL> C:\Scripts\TranslationToolUI\bin\Debug\net10.0\TrueFluentPro.exe` | 退出 4 | 退出 4 |
| helper 内部 InstallLayoutOrTip enable | TRUE | TRUE |
| helper 内部两套真实枚举 matched | 0 / 0 | 0 / 0 |
| 输出 | `profile-postcheck-failed; new-component-removed` | 相同 |
| `--status <对应绝对 DLL>` | 退出 1 | 退出 1 |
| finally `--unregister <对应绝对 DLL>`（先 x86，后 x64） | 退出 0 | 退出 0 |
| 最终 `--probe` | 退出 1，matched=0 | 相同 |

证据：`artifacts/registration-20261102-201443-807/report.json`、`commands.jsonl`、`exit-code.txt`。完整绝对路径与参数以其中 Commands 为准。Restored=true、FilesRetained=true；包括所有本组件 COM/TIP/配置/VirtualStore 项再次不存在，语言/默认覆盖/Preload/Substitutes/SortOrder/Assemblies 完全恢复。文件保留以各次注册窗口内哈希比较为准，不把并行根构建可能更新产物的时间跨度当作不可变产物声明。

## 编译与离线编辑：通过和失败分开

实际控制命令：`pwsh -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeNative\Run-LiveValidation.ps1 -Mode Baseline`。

最新轮 `artifacts/Live/20261102-201038-220-Baseline-exit.json`：

| 阶段 | x64 | x86 |
| --- | ---: | ---: |
| Build.ps1 Debug（两个架构由同一命令构建） | 0 | 0 |
| `VoiceImeTests.exe <对应绝对 DLL>` | 0 | 0 |
| `VoiceImeEditTests.exe` | 1 | 1 |

控制进程退出码 **1**，没有放过编辑宿主失败。构建日志 `artifacts/Live/build-latest.log`。默认 COM 测试验证 DLL 导出、工厂、接口、未激活 F8 透传、独立 KeyLatch、协议基础及引用计数；不等于选中 profile 的实际按键链路。

编辑宿主两架构均：

- 真实 Windows TSF thread/document/context + 内存 ITextStoreACP 建立成功，HRESULT `0`。
- 调用生产 ITfTextInputProcessor Activate：`0x80070057`；应用 owner client `32`、从官方 ITfClientId 获取的独立 service client `24`。
- 未进入 partial/final 编辑请求，没有观察到任何文本插入。停止继续修改此可选宿主，保留失败供下一轮定位。
- workerStarts=0，原生 COM 对象计数最终归零，无实际后台、配置读取、麦克风或云调用。

证据：`artifacts/Live/20261102-201038-220-win-x64-edit.log` 与 x86 对应日志。该宿主仅在独立测试 EXE 中编译生产实现及 friend 注入入口；生产 DLL 不含测试注入能力。没有通过降低激活断言、伪造 TSF manager/range 或注册根劫持把失败改成通过。

## 主 agent 交接（20:15 历史状态；20:37 更新见文末）

- 新增 `--probe`；既有 `--status` 字符串/退出码不变。主程序如需要可独立调用 probe 作为 postcheck，原生 register 自身已 fail-closed。
- 当前修正产物在 `artifacts/Live/win-x64`、`win-x86`；主 agent 统一再次根构建。根构建成功只证明编译，不消除系统注册/激活失败。
- 不建议把当前 HKCU 手写 CTF 路径作为已可用方案发布。需要先决定经允许的安装/注册策略，或另行调查真实免提权路径；本轮不扩大权限。
- 可选宿主的 Activate 失败尚未定位到具体子调用，不能武断归因于权限或系统不支持，也不能声称跨应用正常。
- 未验证：Win+Space 可见性、选中 profile 的物理 F8、真实或内存宿主的 partial/final、光标映射、跨应用输入、真实语音、管道端到端、AppContainer/提权应用。
- 本轮日志与变更记录均放在本目录，没有编辑外部 Docs。

## 官方依据

README 的“机制依据”列出完整链接。关键结论来自微软 SampleIME `Register.cpp`、IME requirements 的 installation 段、RegisterProfile/EnumProfiles/EnumLanguageProfiles、InstallLayoutOrTip、Registry Virtualization、MSVC /MANIFEST 与 /MANIFESTUAC 文档。官方要求用 RegisterProfile 注册 IME，不直接写 TSF 注册项；InstallLayoutOrTip 是用户启用步骤。系统/API 失败保留为失败，不以 Wine 的持久化布局参考替代真实 Windows 证据。

## 2026-11-02 20:37:43：只读注册机制调查与激活阶段诊断

### 实现目标
- 本轮禁止任何注册/反注册/用户启用尝试、HKLM 写入、提权、安装和既有输入法变更。仅在本目录编辑测试和文档、构建独立产物；不运行根构建，不读真实配置、不链接实际 worker/云识别路径。
- 保留早先已恢复的登记状态，不使用 LOCALPROCESS/LOCALTHREAD 伪注册或 RegOverridePredefKey。

### 官方注册机制与权限证据
- 准确安装要求：[IME requirements / IME installation](https://learn.microsoft.com/en-us/windows/apps/develop/input/input-method-editor-requirements#ime-installation)。安装用 RegisterProfile，不直接写 TSF 登记；InstallLayoutOrTip 是另外的用户启用步骤，不保证组件已正式登记或被选中。
- [SampleIME Register.cpp](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/Register.cpp) 分为 COM server、profile、category 三部分。机器级 COM 信息为 CLSID、正确架构 DLL 的 InprocServer32 路径、Apartment；TSF 另需 service CLSID、LANGID、profile GUID、描述/图标信息和适用类别。本组件不冒充样例全部能力类别。
- [InprocServer32](https://learn.microsoft.com/en-us/windows/win32/com/inprocserver32) 明示机器级 COM 路径；[HKCR](https://learn.microsoft.com/en-us/windows/win32/sysinfo/hkey-classes-root-key) 是合并视图，样例向 HKCR 创建新 class key 的步骤不能解释成 HKCU-only 安装。TSF profile/category 的安装契约是官方 API，不以兼容实现的物理 schema 作为 Windows 官方保证。
- [微软 UAC 要求](https://learn.microsoft.com/en-us/previous-versions/aa905330(v=msdn.10)) 的 Installer Detection 段说明受保护机器位置通常仅管理员可写；[注册表安全/访问权限](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-key-security-and-access-rights) 说明实际按 ACL 检查。本机只读的父项 ACL 投影记录 Users=ReadKey、Administrators/System=FullControl，详见 `artifacts/ActivateDiagnosis/readonly-audit.json`。这是机器安装通常需管理员授权的官方机制与本机辅助证据，不是 RegisterProfile 内部失败点的追踪，也不是对完整 token 的 AccessCheck。
- [RegisterProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-registerprofile) 只定义 E_FAIL 为 unspecified error。此前 x64/x86、虚拟化关闭下 E_FAIL、枚举 matched=0 仍是实际注册阻断；没有本轮新注册结果，不将其解释为 E_ACCESSDENIED，也不承诺提权能成功。用户启用与机器级安装权限分开。

### 精确激活调用结果

两架构最终日志相同；下表 HRESULT 均为真实 Windows 返回值，测试转发器不改参数、不伪造成功：

| 阶段/API | 结果 |
| --- | --- |
| CoInitializeEx(STA)、创建 TF_ThreadMgr | S_OK |
| ThreadMgr ActivateEx（NOACTIVATETIP / NOACTIVATEKEYBOARDLAYOUT） | S_OK，app-client=32 |
| CreateDocumentMgr / CreateContext / Push | 均 S_OK |
| 独立预检 GetFocus | S_FALSE，空焦点文档；不是 E_INVALIDARG |
| 独立预检已创建文档 GetTop | S_OK，确为该 TextStore context |
| QI ITfClientId / GetClientId(VoiceClsid) | 均 S_OK，service-client=24 |
| QI ITfSource / AdviseSink(ITfThreadMgrEventSink) | 均 S_OK |
| QI ITfSource / AdviseSink(ITfThreadFocusSink) | 均 S_OK |
| QI ITfKeystrokeMgr | S_OK |
| **AdviseKeyEventSink(24, 本服务 sink, TRUE)** | **E_INVALIDARG / 0x80070057，首个失败** |
| 失败清理两种 thread sink | 均 S_OK |
| 不经转发的生产 Activate 对照 | 同样 0x80070057 |
| 本组件 FindClosestCategory（只查 keyboard） | S_OK，空 GUID；未找到 keyboard 类别 |
| service QI ITfKeyEventSink | S_OK |
| 直接 AdviseKeyEventSink(24, 同一 sink, FALSE) | 0x80070057 |
| 直接 AdviseKeyEventSink(32, 同一 sink, TRUE) | 0x80070057 |

生产路径在失败前 `GetFocus` 调用次数为 0，故其随后的 GetTop/文本 sink/编辑路径尚未到达。服务创建 profile manager、创建窗口及两种 thread sink 阶段已跨过，并非这次返回失败的阶段。

[GetClientId](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfclientid-getclientid) 官方区分 app ID、由 TSF 传给正式 service Activate 的 ID，以及其他 TSF 对象取得的 ID；GetClientId 成功不代表取得已注册/被 TSF 激活的键盘服务身份。[AdviseKeyEventSink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-advisekeyeventsink) 仅将 E_INVALIDARG 归为参数无效，没有公开全部内部验证条件。[SampleIME KeyEventSink](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/KeyEventSink.cpp) 与当前生产一样以 service ID 订阅 TRUE。[AdviseSink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfsource-advisesink)、[GetFocus](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-getfocus)、[GetTop](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfdocumentmgr-gettop)、[FindClosestCategory](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcategorymgr-findclosestcategory) 均按各自官方契约解释，没有把 S_FALSE 或空 GUID 当成成功 profile。

**结论分层**：已实测定位到 key-sink API；测试宿主没有正确登记/正式激活的本组件 profile/category，不能作为真实 service Activate 环境。它与生产 TSF 正式激活前提不同，但尚未证明“只补类别就一定成功”，也未验证正式 profile 下生产 Activate 是否另有问题。没有接口单步证据能支持修改生产，因此未进行生产修复，更没有换 FALSE、换 app ID、假成功或跳过门禁来报通过。

### 变更内容
- 只改 `EditTests.cpp`、README 和本报告。测试 EXE 内新增真实 ITfThreadMgr/ITfSource/ITfKeystrokeMgr 转发和首错记录、独立焦点预检、类别只读查询及直接参数对照。
- `VoiceIme.cpp`、`Common.h`、`Tests.cpp`、注册/helper/构建脚本均未修改；生产 DLL 不含新增诊断类。
- 仍以生产 Activate 成功为编辑前提；失败打印 `NOT-RUN partial/final`、writes=0，不伪造 range 或选中 profile。

### 命令、退出码与验证边界

最终从仓库根直接调用 `Build.ps1 -OutputDirectory C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\ActivateDiagnosis -Configuration Debug`，随后每种架构各运行一次无参数 `VoiceImeEditTests.exe` 和 `VoiceImeTests.exe <同目录绝对 DLL>`。可执行文件、参数、原生退出码及完整日志路径均在 `artifacts/ActivateDiagnosis/final-exits.json`，时间 `2026-11-02T12:37:43Z`。

| 项目 | x64 | x86 |
| --- | ---: | ---: |
| 最终 Debug 构建（同一 Build.ps1） | 0 | 0 |
| 默认无注册 COM 测试 | 0 | 0 |
| nativeEditTest | 1 | 1 |
| partial/final | NOT-RUN | NOT-RUN |
| workerStarts / 残留原生 COM 对象 | 0 / 0 | 0 / 0 |

完整输出为 `build-final.log`、`final-win-x64-edit.log`、`final-win-x86-edit.log`、对应 `-com.log`。首轮纯转发诊断为 `diagnostic-*.stdout.log` 和 `diagnostic-exits.json`，同样定位 key sink；最终轮补上类别/参数对照。过程中一次驱动参数输送卡住被中断、一次诊断的 GUID_NULL 未声明导致编译失败，不算原生测试通过；改用直接固定命令与零初始化 GUID 后才得到上表最终结果。

本轮未调用 RegisterProfile/RegisterCategory/InstallLayoutOrTip 或任何反注册、激活 profile API；没有启动真实后台或读取 HostExe 配置，没有改系统登记、ACL 或现有输入法，也未重新清理此前恢复过的登记项。只读固定 CLSID 的 HKCU/HKLM 双视图 COM/TIP 及 VirtualStore COM 存在性均为 false；不将这个小范围存在性检查冒充完整用户输入配置前后快照。未验证的边界仍包括正式安装后选中 profile、实际 F8、partial/final、光标映射、跨应用和真实语音。