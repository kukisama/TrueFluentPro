# 管理员实际登记执行交接（2026-11-02）

## 结论

**未安装到可用，当前 Installed=False。** 已在 Elevated=True 的独立 PowerShell 进程中加载真正的 Release DLL，构造生产 `TrueFluentPro.Services.VoiceImeRegistrationService`，实际调用 `RegisterAsync()` 一次，返回 `Succeeded=False`。阻断是本轮执行前就存在的另一包机器级 partial 登记；未绕过预检、未手写注册表、未执行真实卸载或清理。

- 主程序 Release 构建退出 0；实际登记验收脚本退出 1，不能将构建成功当成安装成功。
- x64/x86 实际 Program Files helper 的 status 均为 `registered=false,state=partial,requiresAdmin=true`，退出 1。
- 两架构 probe 均为 `enumerated=0 enabled=0 keyboard=0`，退出 1。
- 默认覆盖项、语言布局、组件 footprint 和安装清单前后均未变化。
- 由于实际 footprint 存在，**不能说“无需反注册”或“当前未登记”**；仅能说没有完整可用登记。
- 本轮不做卸载。需要主 agent 安排既有 partial 安装的恢复/清理与原生根因处理，之后才能再次验收安装。

## 执行入口、环境及证据

执行目录：`C:\Scripts\TranslationToolUI`。

测试脚本：`tools\VoiceImeRegression\Invoke-SystemRegistration.ps1`。默认只读；只有显式 `-InstallSystem` 调用生产 `RegisterAsync()`；不运行默认回归或旧 Test-Registration exercise。

| 环境项 | 实测 |
| --- | --- |
| TokenElevation | True（OpenProcessToken + GetTokenInformation(TokenElevation)，4 字节实际查询） |
| WindowsPrincipal 管理员角色 | True |
| 解释器 | `C:\Program Files\PowerShell\7\pwsh.exe`，7.6.9，64 位 |
| .NET | 10.0.14 |
| 生产程序集 | `C:\Scripts\TranslationToolUI\bin\Release\net10.0\TrueFluentPro.dll` |
| 构造服务时 BaseDirectory | `C:\Scripts\TranslationToolUI\bin\Release\net10.0\` |
| 生产程序集 SHA256 | `60b42340c854f04043eb045fca5f64cdc37e1a81753876844f0e9a72e8bc3279` |

测试进程使用公开进程局部 AppContext 数据槽设置/校验基目录，再 `Assembly.LoadFrom`、构造服务；不修改生产字段、不运行应用 Main、不启动 UI/语音 host、不读真实应用配置。

本轮两次驱动的全部命令、参数、时间、stdout/stderr 和子进程退出码均保留在各目录的 `commands.json`、`run.log`、独立日志及 `exit-code.txt` 中：

- 准备/构建及保护停止：`C:\Scripts\TranslationToolUI\tools\VoiceImeRegression\artifacts\20261102-222758-system-registration-f46a88b7dfbb4c1ab9e85a93c9dbef4f`。
- **实际服务调用及最终状态**：`C:\Scripts\TranslationToolUI\tools\VoiceImeRegression\artifacts\20261102-223212-system-registration-4116ccbda397431f90b64e64aa01287c`。

后者开始于 `2026-11-02T22:32:12.1444966+08:00`，结束于 `2026-11-02T22:32:14.1735884+08:00`。`result.json`、原始 `run.log` 和 `exit-code.txt` 已直接读回核对；没有因终端回执不完整重跑登记。

| 实际进程命令 | 退出码 / 服务调用 |
| --- | --- |
| `pwsh.exe -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeRegression\Invoke-SystemRegistration.ps1 -BuildRelease -InstallSystem` | 1；RegisterCalls=0，保护停止 |
| 其中 `dotnet.exe build TrueFluentPro.csproj -c Release -v quiet --nologo` | 0；双架构 helper 同时构建，不安装 |
| `pwsh.exe -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeRegression\Invoke-SystemRegistration.ps1 -InstallSystem` | 1；RegisterCalls=1、UnregisterCalls=0 |

第一轮使用当前 Release DLL 参数查询既有旧路径，native 回答 `different-path`；这不是对旧 manifest 路径的状态结论。第二轮改为验证 manifest 的真实 Program Files DLL/helper 路径与缓存哈希身份，确认真正状态是 `partial` 后才让生产服务自身预检返回拒绝。不是连续重试系统写入。

首轮语言快照将整个 List 的 LanguageTag 投影为数组，不能用它确定逐语言对应关系；第二轮改为显式 foreach，两个语言分别保存，原首轮证据不改写。先前环境执行摘要称 native 文件缺失也已由精确路径实测纠正：文件实际存在，完整哈希在两轮 package 快照中。

## C# 服务真实返回值

`RefreshStatusAsync()` 前后均 `Succeeded=True`，完整消息相同：

> win-x64：存在不完整登记或残留（不是未注册）  
> win-x86：存在不完整登记或残留（不是未注册）；记录丢失，无法安全确定原安装路径。  
> 当前已以管理员身份运行；查询及日常听写不需要管理员。

这里 `Succeeded=True` 仅说明状态查询完成，不表示已安装。

`RegisterAsync()` 实际调用一次，返回 `Succeeded=False`，完整安全消息：

> win-x64：存在不完整登记或残留（不是未注册）；请先反注册原安装，再注册。旧用户级 scheme 未通过 Windows 枚举，不能仅归因于权限。

阻断位于 `Services/VoiceImeRegistrationService.cs` 的 `RegisterCoreAsync`：读取既有 footprint、恢复实际 entry 后，`QueryStatusAsync(existingEntry)` 不为 Registered，预检拒绝。**本轮没有进入 native `--register` 阶段，没有新的 native stage/HRESULT；不能借用历史 E_FAIL 充当本轮 HRESULT，也不能判为权限不足。**

服务返回消息中的“旧用户级 scheme”不符合本轮实测 hive：本轮 HKCU 两视图 COM/TIP 均不存在，实际存在的是 HKLM partial。x86 的“记录丢失”也不表示 manifest 条目丢失：条目存在，但该架构 COM 实际路径不存在，`FindActualEntry` 无法从 footprint 得到真实 entry。文案问题交主 agent，不修改生产代码。

## 双架构实际 native 结果与路径

现存包根：`C:\Program Files\TrueFluentPro\VoiceIme\b4016ed55cc47c54793a703ee8f1bb2a35a3bba90521d1a0623cdc907f29f599`。

| 架构 | manifest DLL 绝对路径 |
| --- | --- |
| win-x64 | `C:\Program Files\TrueFluentPro\VoiceIme\b4016ed55cc47c54793a703ee8f1bb2a35a3bba90521d1a0623cdc907f29f599\win-x64\TrueFluentVoiceIme.dll` |
| win-x86 | `C:\Program Files\TrueFluentPro\VoiceIme\b4016ed55cc47c54793a703ee8f1bb2a35a3bba90521d1a0623cdc907f29f599\win-x86\TrueFluentVoiceIme.dll` |

| 架构 | 实际执行的 manifest helper 绝对路径 |
| --- | --- |
| win-x64 | `C:\Program Files\TrueFluentPro\VoiceIme\b4016ed55cc47c54793a703ee8f1bb2a35a3bba90521d1a0623cdc907f29f599\win-x64\513954fdba116e738c43df25008f28b65cc0fbb598269e4f96962f2eedcf0898\VoiceImeRegister.exe` |
| win-x86 | `C:\Program Files\TrueFluentPro\VoiceIme\b4016ed55cc47c54793a703ee8f1bb2a35a3bba90521d1a0623cdc907f29f599\win-x86\b17f52e3f560b4db61cd7c36d3e06cb97b76a31b1d517f8e68a1576580ed4b94\VoiceImeRegister.exe` |

| 调用（前后各一次） | 实际 stdout | 前 / 后退出码 |
| --- | --- | --- |
| win-x64 `--status <以上x64 DLL>` | `{"registered":false,"state":"partial","requiresAdmin":true}` | 1 / 1 |
| win-x64 `--probe` | `probe enumerated=0 enabled=0 keyboard=0; activation-and-editing-not-probed` | 1 / 1 |
| win-x86 `--status <以上x86 DLL>` | `{"registered":false,"state":"partial","requiresAdmin":true}` | 1 / 1 |
| win-x86 `--probe` | `probe enumerated=0 enabled=0 keyboard=0; activation-and-editing-not-probed` | 1 / 1 |

`--probe` 按 native CLI 契约不接受 DLL 参数；其进程是上表对应架构/安装缓存的 helper，仅枚举固定 CLSID/Profile/语言/类别。没有激活输入法、F8、编辑或语音测试。

| 架构 | 现存 DLL SHA256 | 现存 helper SHA256 |
| --- | --- | --- |
| win-x64 | `08a499aa665fe1af8c847cfd600dd50b5f6b967efb6b73a35182c2b61aea4b75` | `2b76887ae451b34f8d2346caf2e392682343844a3d961e7df16aae101d72bdb5` |
| win-x86 | `819bcd1087af352b7af0f0ba0b94cb4856bdc126a0f1ae04b32890bff1219ab6` | `c59941294654b44ec1b5b7df4244fb5239da9ef15b0b395b72b06699f873aa5b` |

当前重新构建的候选包身份为 `03dad2ceb8ace39ca57265c6755597be883b9e0a452a67f4a630cc8a8420135e`；它与现有 manifest/COM 的包不同，未冒充本次已安装。候选 helper 的 x64/x86 SHA256 分别为 `31fe431dd3f803a2bd7ab3216bb4bae612ca6319d03f44d260aa41d0d7f3f632`、`256a7e5eaad14fd7260b0f64fc03e8d09236579a48ab2ee5d3d7aa28e7ae4c63`。

## 默认输入法与系统状态前后

| 项目 | 调用前 | 调用后 |
| --- | --- | --- |
| `HKCU\Control Panel\International\User Profile\Default` | 值不存在 | 值不存在 |
| `Get-WinDefaultInputMethodOverride` | `0804:{E7EA138E-69F8-11D7-A6EA-00065B844310}{E7EA138F-69F8-11D7-A6EA-00065B844311}` | 同左，精确一致 |
| 语言顺序 | `zh-Hans-CN`、`zh-Hant-HK` | 同左 |
| zh-Hans-CN InputMethodTips | 仅上述 `0804:…` | 同左 |
| zh-Hant-HK InputMethodTips | 空 | 空 |
| Keyboard Layout Preload | `1=00000804`、`2=00000409` | 同左 |
| Keyboard Layout Substitutes | 键存在，无值 | 同左 |

固定 CLSID `{5638E6E5-D18C-4FE8-B778-E59A5170B481}` 的实际 footprint 前后相同：

- HKCU 两视图 COM/TIP 均不存在，用户组件 HostExe 记录也不存在。
- HKLM Registry64 COM 存在，owner=`TrueFluentPro.VoiceIme.v1`、ThreadingModel=`Apartment`，指向上述 b4016e 包 x64 DLL。
- HKLM Registry32 COM 不存在。
- HKLM 两视图 TIP 根存在，固定 Profile `{A91272E1-9973-43F9-8EB4-7D5494EC92B1}` 的语言 profile 子项不存在；native 精确枚举亦为 0。
- 清单 `C:\Users\a9y\AppData\Local\TrueFluentPro\VoiceIme\install-manifest.json` 存在、Version=1，保留两个 b4016e 包架构的 DLL/helper 条目，与基线一致。
- DLL/helper 都保留；没有调用 `UnregisterAsync` 或 helper `--unregister`。服务因预检拒绝而未触发本轮新增登记的 rollback。

## 主 agent 后续处理

- 已证实的直接阻断是**基线既有 partial 安装导致生产服务的安全预检拒绝**；造成这批旧残留的历史 native 失败阶段/HRESULT，本轮没有证据，不做反推。
- 原生 `RegistrationPolicy.h` 的 partial 状态及 rollback 残留检测可作为排查入口，但不能仅据残留形状声称 RegisterProfile 曾失败或权限是唯一原因。
- 由主 agent 单独安排旧安装的受控恢复/反注册；本轮明确禁止真实卸载，因此到此停止，不擅自删除 TIP 或已加载 DLL。
- 在既有登记处置完成、基线明确后，重新使用同一生产服务入口安装，再要求两个实际安装路径 status=registered、probe 三项全 true、C# 刷新已注册及默认项不变。

## 已查询的官方依据

- [IME installation](https://learn.microsoft.com/en-us/windows/apps/develop/input/input-method-editor-requirements#ime-installation)：注册使用 RegisterProfile，不直接写 TSF 登记；用户立即可用另经 InstallLayoutOrTip 加入启用列表。不会为绕过 partial 改写 CTF。
- [EnumProfiles](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-enumprofiles)：按语言枚举 profile；真实精确枚举与磁盘文件/清单存在不是同一事实。
- [InstallLayoutOrTip](https://learn.microsoft.com/en-us/windows/win32/tsf/installlayoutortip)：默认项、.Default 和清空其他输入法分别有显式标志；本轮未调用这些设置。BOOL false 仅表示未指定错误，不能随意推断 HRESULT。
- [RegisterProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-registerprofile)：E_FAIL 为未指定错误；LOCALPROCESS/LOCALTHREAD 仅局部可用，不可用于冒充跨应用成功。
- [Get-WinUserLanguageList](https://learn.microsoft.com/en-us/powershell/module/international/get-winuserlanguagelist?view=windowsserver2025-ps)：返回 WinUserLanguage 的 List，InputMethodTips 属于各个语言；第二轮快照保留逐项对应关系。

本任务仅新增本目录测试脚本/报告，未修改生产/native/Docs 源码。脚本末次修改只为消除命名/自动变量告警，不再进行系统登记。