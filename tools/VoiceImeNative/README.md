# TrueFluent Windows 语音 TSF 原生组件

> **当前登记实现（2026-11-02 23:44）**：已针对提供的 `20261102-231458-system-registration-7efe802917ee4655ae58f17a9b171657/native-stages.log` 修复不可枚举 profile 的用户解绑和精确禁用偏好收尾，并接入安装根 `maintenance.lock`。Debug/Release × x64/x86 的构建、COM、内存策略、离线生命周期均通过。**本轮没有执行真实注册/卸载/语言 setter；live 由主 agent 统一验收**。最终产物与报告在 `artifacts/Final-20261102-NativeOnly`，简明交接见 `HANDOFF-20261102-NativeOnly.md`；早先验证记录及默认目录中断报告不代表本次最终结论。

## 范围与文件

仅包含语音 TSF 输入法；没有拼音引擎、配置页、全局热键、剪贴板或 SendInput。主程序的配置页及 `--voice-ime-host` 后台由 C# 工程提供。设计上不依赖主程序 UI，每次听写启动独立后台进程；系统注册、激活与跨应用使用仍受上面的交付阻断限制。下面的编辑/线程/协议段落描述实现契约，不是跨应用实测通过声明。

| 文件 | 用途 |
| --- | --- |
| `Common.h` | 固定 GUID、协议、按键门控、跨线程消息和通用声明 |
| `VoiceIme.cpp` | COM class factory、TSF sinks、异步编辑/composition、UI 线程隐藏 HWND 与非抢焦点状态提示 |
| `PipeClient.cpp` | 每次听写的后台启动、可取消连接/读取、STOP 和自有子进程回收 |
| `NativeInstallPath.h` | 原生 ProgramFilesDir 的 registry64 解析、固定维护路径与只读门禁；无 HKCU 配置/凭据 |
| `Registration.cpp` | 正式 TSF API、机器 COM/用户 HostExe 事务、权限及所有权检查、受限旧 HKCU 清理 |
| `RegistrationPolicy.h`、`RegistrationTests.cpp` | 双架构、状态、零写入权限门控、新增项补偿的共享策略及纯内存后端测试 |
| `RegisterMain.cpp` | 注册/卸载/状态 CLI、用户 profile 操作及错误补偿 |
| `Tests.cpp` | 默认无注册 COM 离线验证；旧 `--registration-api` 写入入口已移除并返回 2 |
| `LifecycleTests.cpp` | 仅测试 EXE 注入本目录 marker 路径，验证 F8/worker/实际 WM_TIMER/旧帧隔离；不启动真实 host |
| `EditTests.cpp` | 真实 TSF thread/document/context + 内存 TextStore；测试专用接口转发定位 Activate 的首个失败，保留失败门禁 |
| `Exports.def`、`Exports.x86.def` | 两种架构的无名称修饰 COM 导出 |
| `Build.ps1` | 自动发现 VS/SDK 的双架构增量构建 |
| `Verify.ps1` | 四组构建、默认 COM/内存策略测试；保存各退出码及导入表，不含编辑宿主通过声明 |
| `Test-Registration.ps1` | 只读 JSON/probe；可选空登记下非管理员返回 10 验证；绝不执行卸载或读取默认输入设置 |
| `Run-LiveValidation.ps1` | 仅 Baseline；本目录构建、COM/编辑宿主验证，保留编辑失败，不再提供写入 exercise |
| `Audit-RegistrationResidue.ps1` | 历史 VirtualStore 空目录审计；本次未执行审计或清理 |
| `VALIDATION-20261102.md` | 旧 HKCU 原型的历史核验记录 |
| `VALIDATION-FORMAL-20261102.md` | 当前正式登记方案、命令与退出码及未执行边界 |
| `.gitignore` | 忽略本目录的生成产物 |
| `README.md` | 集成契约、验证记录及未验证边界 |

## 固定身份

- CLSID：`{5638E6E5-D18C-4FE8-B778-E59A5170B481}`。
- Profile GUID：`{A91272E1-9973-43F9-8EB4-7D5494EC92B1}`。
- 语言：`0x0804`，简体中文。
- 类别：`GUID_TFCAT_TIP_KEYBOARD`；不注册 SecureMode、AppContainer、Immersive 等未经验证的安全/兼容能力。
- 显示名：`TrueFluent 语音输入`。

## 构建与产物

在仓库根执行 `pwsh -NoProfile -File .\tools\VoiceImeNative\Build.ps1 -OutputDirectory 'C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\Release' -Configuration Release`。

Debug 对应命令为 `pwsh -NoProfile -File .\tools\VoiceImeNative\Build.ps1 -OutputDirectory 'C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\Debug' -Configuration Debug`。

输出目录必须是绝对路径。每个输出根包含：

- `win-x64/TrueFluentVoiceIme.dll`、`win-x64/VoiceImeRegister.exe`。
- `win-x86/TrueFluentVoiceIme.dll`、`win-x86/VoiceImeRegister.exe`。
- 额外提供对应架构 `VoiceImeTests.exe`、`VoiceImeEditTests.exe`；对象文件与构建戳位于 `obj-Debug` / `obj-Release`。编辑宿主中的测试注入入口不编译到生产 DLL。
- `VoiceImeRegistrationTests.exe` 仅运行安装策略的内存替身，不调用任何 TSF/注册表写入 API。
- `VoiceImeLifecycleTests.exe <不存在的fixture路径> <已存在的fixture路径>` 只使用注入路径；`Verify.ps1` 在本目录 `artifacts` 创建空测试文件，绝不创建实际 Program Files 下的维护文件。

脚本从 vswhere 找到已安装的 C++ x64/x86 工具，通过 vcvarsall 选择已安装 SDK。没有安装、提权、注册、停用户程序、清缓存或主工程修改。Release `/MT`、Debug `/MTd` 静态链接 CRT；基于本目录源码/头文件/导出定义/脚本时间戳及配置构建戳增量跳过。VS/SDK 本身升级后需选择新的输出目录重建。

helper 和测试 EXE 显式 `/MANIFEST:EMBED /MANIFESTUAC:"level='asInvoker' uiAccess='false'"`，不会自动提权。实测旧 x86 测试 EXE 没有嵌入 manifest，TokenVirtualizationEnabled=1；修后两架构均为 0。不能依靠 WOW64 虚拟注册表的隔离成功假装系统可见。

## 主程序调用的 helper

两种架构分别调用对应 `VoiceImeRegister.exe`，DLL 必须是相同架构，HostExe 可为 x64 主程序。helper 保持 asInvoker，不自动请求 UAC。本轮没有复制文件到 Program Files，也没有执行管理员安装。

- `--register <dllAbsolute> <hostExeAbsolute>`：先查 TokenElevation；非管理员立即返回 10，不加载 DLL、不创建登记项、不调用系统写 API。管理员执行时先检查所有权、路径冲突与旧 HKCU override，再验证 DLL 工厂、正式登记、当前用户 HostExe 和启用状态；真实枚举后置检查通过才返回 0。
- DLL/helper 安装目标为原生 Program Files 下 `TrueFluentPro\VoiceIme\<64位SHA256>\win-x64|win-x86`；helper 校验 DLL 路径形状/架构并拒绝安装路径中的 reparse point。安装方负责保持默认受保护 ACL：所有用户可读、普通用户不可写。
- `--unregister <dllAbsolute>`：DLL 可已不存在。所有 hive/双视图无本组件项且 profile/category 无枚举时只读返回 0、`already-unregistered`，不要求管理员；机器级/部分安装卸载先要求管理员。只删 owned 且路径匹配的键，绝不删除 DLL/helper 文件。
- 旧 HKCU 登记返回 `legacy-user`，注册报 `LegacyUserCleanupRequired`，不覆盖 override。显式卸载只清理自身 owner+路径对应的旧 HKCU COM/TIP，不调用 TSF 注销或 InstallLayoutOrTip，不影响机器安装。双旧架构时保留 TIP 图标路径对应的 COM 到最后，错误顺序报 `LegacyCleanupOrder`，无写入；外来键拒绝清理。
- `--status <dllAbsolute>`：查询成功仅输出一行 JSON，如 `{"registered":false,"state":"unregistered","requiresAdmin":true,"maintenance":false}`。原有三字段及 state 集合 `registered|unregistered|partial|different-path|legacy-user` 保持兼容；新增 `maintenance`。原本完整登记但维护 marker 存在（或维护路径无法安全查询）时返回 `registered=false,state=partial,maintenance=true`，不会报告可用。registered 退出 0，其余已知状态退出 1。未知查询失败退出 6，输出安全阶段及 HRESULT，不伪装成未安装。
- `requiresAdmin` 表示机器安装/修复/卸载需要管理员，不是当前 token 是否已提升。纯 legacy-user 为 false；其余状态为 true。无登记卸载仍按独立幂等规则返回 0。
- `--probe`：只读 EnumProfiles(0) 和 EnumCategoriesInItem，精确匹配 CLSID/profile/0x0804、键盘类别及 enabled；退出 0 表示匹配且启用，1 表示已知不存在/未启用/类别不符，6 表示查询错误。不使用 IsEnabledLanguageProfile 推断存在性，不激活 profile；该 probe 仅报告登记事实，不替代 status 的维护可用性门禁。

helper 不启动主程序、不输出路径/凭据/识别正文。错误形式为 `failed stage=RegisterProfile HRESULT=0x80004005`；补偿失败追加固定 `rollbackStage`、`rollbackHRESULT`。退出码：0 成功，1 状态/probe 正常阴性，2 CLI 语法错误，4 安装/卸载/校验/后置检查失败，5 补偿未完成，6 只读查询/异常失败，10 **仅表示需要管理员且本次尚无写入**。DLL 导出返回 HRESULT，不使用 CLI 退出码。

### 写入和回滚边界

- COM：仅固定 CLSID 的 `HKLM\Software\Classes\CLSID\{5638E6E5-D18C-4FE8-B778-E59A5170B481}`，各 helper 只写本架构独立 view，标记 VoiceImeOwner、DLL 路径及 Apartment。不写 HKCR，不新增 HKCU COM override。
- TSF：只用 RegisterProfile、RegisterCategory(GUID_TFCAT_TIP_KEYBOARD)；沿用 SampleIME 的 bEnabledByDefault=TRUE、flags=0。这不是设置用户默认输入法；用户是否可用仍须 InstallLayoutOrTip 与真实枚举后置。不会手工创建/补写 CTF 表项，不声明 SecureMode/AppContainer/Immersive 等能力，不使用 LOCALPROCESS/LOCALTHREAD。
- HostExe：只写调用用户 `HKCU\Software\TrueFluentPro\VoiceIme` 的 HostExe、VoiceImeOwner，双视图访问；另加 VoiceImeInstall 绑定 hash 安装目录供精确卸载。已有 owner/path 不符在写入前拒绝；已有同值键不重写。不将用户 HostExe 放到 HKLM 给其他用户使用。
- 启用：确认机器 profile（包含成功 RegisterProfile 后读到的精确 profileKeys）和 category 后，分别调用 EnableLanguageProfile(TRUE)、InstallLayoutOrTip(...,0)；再强制真实用户枚举后置。两项结果独立，不因前一项失败跳过后一项。卸载为 FALSE / ILOT_UNINSTALL；不设置默认项，不使用 DEFPROFILE、DEFUSER4、CLEANINSTALL。安装必须在预期用户身份下执行，不能用另一个管理员账户代替其 HKCU。
- 双架构操作由命名 mutex 串行化，COM 独立，profile/category 按共享事实查询。第二架构不会覆盖已有共享 profile；卸载一侧只删本侧 COM，保留共享 TSF/HostExe；最后一侧才正式注销 profile/category、停用当前用户 profile 和删除精确绑定的 HostExe。
- COM/HostExe 每次变更有 KTM 事务；正式 TSF/用户启用 API 不在其中。失败仅补偿本次新增的 COM、profile、category、HostExe 及本次启用；不动既有登记/HostExe。补偿后重新枚举并检查新增残留，失败明确返回 5；API 补偿不完整时保留 COM owner 供显式卸载重试，不能笼统报告原子回滚成功。
- 卸载不把 API 的非零/S_FALSE 统一改成成功。两项用户解绑原始结果留在独立 trace；仅当新快照证明用户入口已禁用/不存在，才继续机器清理，最终还必须证明系统 profile/category/类和用户入口、COM/owned HostExe 真正不存在。无法证明解绑、类 API 失败、偏好收尾失败时保留 COM owner 并报阶段，允许重试。不手工写 CTF 重新登记，也不删除未证明所有权的机器孤项。
- 保留 RegisterCurrentUser/UnregisterCurrentUser ABI 名，Common 注释已明确机器级语义；DLL 的既有 DllRegisterServer/DllUnregisterServer 导出不变。DllRegisterServer 不传 HostExe，因此只登记机器服务、真实枚举，不启用用户；完整安装使用 helper。

双架构仍是两个独立 helper 调用，不是整组原子事务。主程序若补偿整组失败，只撤销本轮新成功安装的架构，不删除预先存在的安装。

## C# 后台协议

DLL 通过 `CreateProcessW` 直接启动 `'<HostExe>' --voice-ime-host --pipe '<pipeName>' --parent <目标应用PID>`，不经 shell、`bInheritHandles=FALSE`、`CREATE_NO_WINDOW`。后台应在原进程创建管道，不转交给另一个进程；Native 会核对服务端 PID 为本次创建的进程。

`pipeName`：`TrueFluentPro.VoiceIme.<PID>.<TID>.<32位hexguid>`，不含管道路径前缀。C# 使用该名字创建 `NamedPipeServerStream(CurrentUserOnly, Async, byte mode, duplex)`，Native 连接 `\\.\pipe\<pipeName>`。连接重试总预算 10 秒，可被 F8/失焦/停用立即取消；等待就绪另有 10 秒读取上限。

| 方向 | 帧 |
| --- | --- |
| Native → host | 仅停止命令：ASCII 五字节 `stop\n`，然后关闭连接 |
| host → Native | 4 字节 little-endian 的 N，随后 N 字节；N 为 1..65536，包含 kind 字节 |
| kind | `P` partial / `F` final / `R` ready / `E` friendly error / `S` stopped |
| payload | kind 后面的严格 UTF-8 原文；Native 仅把 P/F 正文送入 TSF，不显示/记录 E 的原始内容 |

只有收到 R 才显示“正在录音”；R 前收到 P/F 视为协议失败。C# 必须把 R 定义为实际采集启动完成。F8 停止立即隔离本轮所有结果，不等待尾句；每次结果携带 native run generation。后台应响应 STOP 和 EOF 释放采集并退出；尚未连接就取消或后台退出卡住时，worker 最多额外等待 2 秒后结束自己创建的那个进程，不按进程名/PID 搜索并杀其他程序。C# 还应监控 parent 生命周期，并在目标应用退出时自行释放资源。

## 编辑与线程契约

- F8 只有在键盘 profile GUID/CLSID/语言准确匹配、目标 context 当前有焦点且未禁用时才接管；无全局 RegisterHotKey。TestKey 无副作用，长按不重复开关，抬键仅接管本服务已经接管的 F8。
- InputScope 检查在异步编辑会话内进行；拒绝可观察的密码、私密、PIN、只读、禁用键盘和空 context；经典 EDIT 额外检查 ES_PASSWORD。不能检测未提供 InputScope/状态的任意自定义密码控件，不据此宣称安全能力。
- 所有网络/命名管道 I/O 和后台清理均在 worker；TSF COM 只在激活服务的 UI 线程。worker 通过 hidden HWND/PostMessage 投递，generation/context/focus 三重校验；销毁窗口前先关闭投递目标并排空已投递消息。
- SetText 仅出现在 `TF_ES_ASYNC | TF_ES_READWRITE` 编辑会话。当前句由 composition/range 拥有，P 修订、F 结束 composition。新句重读当前 selection。
- 用户只移动光标不停止听写，不把 caret 拉到文末；仍跟随 owned range 末尾才更新 caret，否则保留 live selection range。用户改动当前 owned 文本或 TSF 外部终止 composition 时，抑制这一句直至 F；不覆盖手改，不重复插入本句。
- 切换 context/失焦/停用使 generation 失效并停止本轮；旧异步会话不能写入新输入框。COM、编辑会话、worker 均有卸载保护；worker 不持有或跨线程调用 TSF COM。

## 安装/卸载维护文件契约

- C# 安装器创建/移除**原生** `ProgramFilesDir\TrueFluentPro\VoiceIme\maintenance.lock`，不是 `Program Files (x86)`，也不在版本 hash 目录。x86/x64 均从 `HKLM\Software\Microsoft\Windows\CurrentVersion` 的 **64 位 view** 读取 `ProgramFilesDir`；不依赖环境变量、不读取 HKCU 配置/凭据。
- 文件为管理员保护的普通空文件，无内容协议；安装根 ACL 由安装器负责，Native 不创建文件、不调整 ACL、不提权。文件存在或状态查询异常均阻止新听写；正常不存在才放行。单纯卸载完后保留的空安装目录不阻止使用。
- 新增公共声明 `bool InstallationMaintenanceActive()`；`MaintenancePath` / `MaintenanceActiveAtPath` 可供纯路径离线测试，生产没有环境变量或运行时路径重定向开关。所有使用此实现的版本/hash DLL 共用同一 marker；**旧二进制如果尚未包含门禁，不能因创建文件就假称已受控**。
- `Service::CheapAllowed` 拒绝新 run；`Receive` 和原有 500ms `WM_TIMER` 对 active run 调用已有 `Stop(false)`：generation 增加、cancel 置位、投递目标关闭、composition 收尾，不激活/停用别人的输入法。消息循环繁忙时 WM_TIMER 不是硬实时 500ms 保证。
- worker 在读取 HostExe 前检查一次，`LaunchHost` 在 `CreateProcessW` 前再次检查，防止排队/解析 host 后才出现维护状态的窗口。检查与进程创建不能跨进程原子化；创建后的极短竞态由 timer 与另 agent 实现的 C# host 文件监视共同收口，不能声称消除了所有 OS 调度竞态。
- 取消仍走现有管道 `stop\n`（最多 500ms 写入）+关闭连接/EOF；仅自己创建的子进程在额外 2s 不退出时才被 worker 回收。无新服务、broker、daemon 或采音代码。
- 安装器应在变更开始前保持 marker，失败保留维护状态和阶段记录以便恢复；完成登记/清理与对应后置检查后才移除 marker，并重新查询 status。Native helper 不自行移除安装器的 marker，维护期间 status 不能被当成已可用。

## 2026-11-02 23:31:33

### 实现目标
- 修复最新管理员卸载中“类注销成功但官方 settingsOnly HKCU TIP 残留”导致的后置失败，保持标准 API 生命周期及严格自有边界。
- 对接固定安装根 maintenance.lock，阻止新听写并停止本服务当前 run；只做原生编译与离线定向验证。

### 变更内容
- `Snapshot` 区分 `profile`、`registeredProfile`、`profileKeys`、`settingsOnly`、`settingsDisabled`、`active`；trace 全量记录这些布尔量及 ownedCOM，不再把官方 Enable-only 设置误作 owner-marked legacy。
- `ChangeUserBinding` / `UserBindingResult` 独立保存 EnableLanguageProfile 与 InstallLayoutOrTip 的结果；即便 profile 不可枚举，settingsOnly 仍触发两项解绑。HRESULT/BOOL 原始失败保留，不把任意非零解释成成功；`UserDetached()` 必须看到 disabled/absent，最后 `Snapshot::Empty()` 才是末架构成功。
- 末架构先 `SwitchAway()`：只有当前确为固定 CLSID/profile/0x0804 才切走；查询同语言现有用户默认 text-service profile，必须已启用、键盘类别且 CLSID 非本组件。只调用 `ActivateProfile(...,TF_IPPMF_FORSESSION)`，不用 ENABLEPROFILE、默认 setter 或语言 setter，S_FALSE 不算成功。没有安全候选就 `ERROR_BUSY` 保留 COM，等待用户/主 agent 先切到既有输入法后重试；不会任意启用/挑选第三方输入法。
- 之后用户解绑、正式 profile/category 清理；空类残留仍由官方 class API 清理，失败保留 COM。非末架构只移除精确 owned 当前 COM，保留 profile/category/HostExe 和用户启用；旧 legacy 顺序规则保留。
- **精确偏好收尾**：仅在最后架构的机器 COM Owner+完整 DLL 路径+Apartment 锚点一致、所有 system TIP/profile/category 已不存在、无其他 profile/category、无 HKCU legacy COM、当前 profile 不再 active/enabled 时，才允许 `RemoveDisabledUserSettings`。事务内重读双视图 COM/机器键和用户树；用户树每层只能是固定 `LanguageProfile\0x00000804\{A91272E1-9973-43F9-8EB4-7D5494EC92B1}`，末端只接受 `Enable` 的 DWORD 0 或空树。Enable=1、外来语言/profile/category/额外值/子键一律拒绝。不动 CTF/TIP 父树、机器 TSF 或别人的输入法，绝不创建 TSF 表项。
- 偏好收尾后再次读取，再事务移除 owned COM/绑定 HostExe，再做最终快照。API 声称成功但仍有 COM、profile/category、用户入口或 host 的情况仍报错；类/偏好清理失败不删除 COM 锚点。
- JSON helper status 保留 `registered/state/requiresAdmin`，只新增 `maintenance`，state 集合及退出码不变；纯 `Status` / `FormatStatus` 用内存快照核验维护前后准确 JSON，不运行真实 status。注册/卸载 stdout 固定标签保持原协议，DLL ABI 导出不变。内部 `--tsf-facts` 增加 `active`，必须使用本次配套 helper/DLL，旧版 8 字段输出被严格拒绝，不能混用。

### 机制依据
- [Microsoft EnableLanguageProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofiles-enablelanguageprofile)：只启用/禁用当前用户 profile，未承诺删除偏好树。
- [Microsoft InstallLayoutOrTip](https://learn.microsoft.com/en-us/windows/win32/tsf/installlayoutortip)：ILOT_UNINSTALL=0x1 与 DISABLED 同义，FALSE 为未指明失败；没有“必删所有 HKCU preferences”的保证，不使用不受保证的 GetLastError 冒充错误原因。
- [SampleIME Register.cpp](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/Register.cpp)、[Server.cpp](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/Server.cpp)：RegisterProfiles→RegisterCategories；DllUnregisterServer 为 profiles→categories→server。这里只采用接口/顺序依据，不复制样例忽略卸载错误的做法。
- [GetDefaultLanguageProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofiles-getdefaultlanguageprofile)、[ActivateProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-activateprofile)、[GetActiveProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-getactiveprofile)：默认查询不改默认，FORSESSION 仅激活既有项，S_FALSE 未启用不能当成功；未找到官方“previous profile”查询契约，本轮不猜测注册表或随意换语言。
- [UnregisterProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-unregisterprofile)、[Unregister](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofiles-unregister)：注销 text service/profile；ALLPROFILES 忽略传入语言/profile，只在 EmptyOwnedClass 已证明独占空类的既有逻辑使用。
- [RegDeleteKeyTransacted](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regdeletekeytransactedw)：transacted key handle 可用于整树删除，清理失败不提交。此处“失效精确用户偏好收尾”是有所有权门禁的本组件策略，**不是微软 TSF 通用卸载保证**；真实 Windows 行为仍交主 agent 验收。
- [IME installation](https://learn.microsoft.com/en-us/windows/apps/develop/input/input-method-editor-requirements#ime-installation)：不要直接写 TSF 登记表项，安装登记和加入用户启用列表是分开的；本次不会为清理残留重新制造登记项。

### 验证结果
- `artifacts/validation.json`：GeneratedUtc `2026-11-02T15:31:33.1065208Z`，整体 ExitCode=0；Debug/Release 双架构构建均 0，四组 COM、四组内存策略、四组生命周期均 0，所有测试日志 `failures=0`。
- 定向覆盖不可枚举+settingsOnly 解绑、语言 API 失败但 ILOT 仍执行、API 成功但状态未解绑仍失败、末架构 Empty/重复幂等、非末架构共享保留、外来项/Enable=1/其他类别与架构拒绝精确清理、类和偏好失败保留 COM、活动切走顺序及无安全候选失败。
- 生命周期使用本目录 fixture：存在/不存在门禁对照、F8 不创建 run、不抢其他输入法按键、LaunchHost 在 CreateProcess 前取消、worker 在任何 HKCU 读取前停止、实际 WM_TIMER 停止 active/generation 隔离、排空旧帧与对象释放均通过。没有启动真实 C# host，不等同 STOP/EOF 的 C# 实机验收。
- 增量 Release 0；相对输出路径拒绝 1 为预期；导入表没有非预期动态 VC 运行库。首次新测试因 initguid 包含顺序出现 GUID_PROP_INPUTSCOPE 链接错误，仅修测试装配后重建通过。
- 只编译和离线原生验证；未执行真实注册/反注册/语言 setter，未读真实 config/凭据，未提权、删除真实系统项、采音或调用云。未运行主工程/C#测试/编辑宿主，也未编辑 Services/UI/主变更日志。

### 后续计划
- 主 agent 统一 live 验收：当前残留收尾及新 trace、标准安装到注销完整生命周期、默认输入法不被重设、用户活动切走、维护时实际 C# host STOP/EOF/文件监视退出、跨应用选中 F8/partial/final。旧版无门禁 DLL、无安全默认候选、其他用户账户或忙碌/挂起应用消息循环须独立处理，不能由离线通过外推。

## 2026-11-02 23:44:06

### 实现目标
- 对最终维护状态 JSON 及全部原生源码收口验证，排除共享终端中断和日志覆盖造成的误报。

### 变更内容
- `Verify.ps1` 默认失败，完整执行后才置成功，避免 Ctrl+C 直接进入 finally 却写出空矩阵 ExitCode=0。
- 增加可选 `ArtifactDirectory`，严格限制在 `tools/VoiceImeNative/artifacts` 下，允许并行任务采用不同输出根。默认目录原有中断报告保留为历史，不能当通过证据。
- 共用终端的回显/中断不足以判断进程结果；最终用现有 Python 3.12 仅启动独立 PowerShell 验证进程并等待完成，不创建 Python 源文件、不安装依赖。依据 [Microsoft about_Pwsh](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_pwsh)：`-File` 的 Ctrl+C 退出码可为 0，必须检查完整结果，而非单看退出码。

### 验证结果
- **最终有效报告**：`artifacts/Final-20261102-NativeOnly/validation.json`，GeneratedUtc `2026-11-02T15:44:06.9808358Z`；实际 PowerShell 进程退出 0，stderr 空，报告 ExitCode=0，Builds 两配置、Tests 四组 COM/四组内存策略/四组生命周期齐全且全部退出 0。
- 四组均有明确 BUILT 标签；每份离线日志 failures=0。新增维护前后准确 JSON 断言通过；报告所列全部源码/脚本最后修改时间与实际文件一致，最终 DLL/helper 哈希见简明交接。
- 增量 Release 0；相对输出目录拒绝 1 为预期；无非预期动态 VC 运行库依赖；编辑器诊断无错误。本节之后仅补文档，不改变已验证源码或产物。
- 本轮仍无真实注册/卸载/语言 setter、真实配置/凭据读取、提权、系统项删除、采音/云调用；未执行主工程或 C# 测试。真实生命周期和 STOP/EOF host 退出由主 agent 验收。

## 历史 HKCU 原型验证与边界（20:37；非当前实现）

原交付使用本机 VS 2022 Community / MSVC 14.44.35207 构建了 x64/x86 的 Debug 和 Release，旧记录在 `artifacts/validation.json`。本轮修正仅重建两架构 Debug 到 `artifacts/Live`；不把旧 Release 记录当作当前源码的 Release 验证。旧 `artifacts/dependencies.log` 是原交付的导入表记录。

离线测试通过相同架构的 `VoiceImeTests.exe <dllAbsolute>` 运行，直接 LoadLibrary/DllGetClassObject 创建 COM，对接口、非选中 F8 透传、独立按键门控、严格 UTF-8、导出名和引用计数做最小验证。测试不会调用注册导出、不会读取真实 HostExe，也不启动实际语音后台。

从仓库根执行 `pwsh -NoProfile -File .\tools\VoiceImeNative\Verify.ps1` 可复现构建矩阵和离线验证；所有日志/报告均写在本目录的 `artifacts` 下，不执行注册/卸载/状态查询。脚本专门把相对输出目录的拒绝结果归为预期失败，不与构建失败混淆。

本轮已受控执行两架构 HKCU 注册、InstallLayoutOrTip、真实 COM 枚举和反注册。**注册可用性失败**：原 helper 两架构返回 0，枚举却均 matched=0；修正版两架构返回 4 并清理，随后反注册均为 0。禁用虚拟化后，官方 RegisterProfile 两架构均为 `0x80004005`，该 HRESULT 不能擅自改写成 E_ACCESSDENIED，也不能据此断言所有 Windows 版本都不支持 per-user。

`VoiceImeEditTests.exe` 已实际创建 TSF thread/document/context。20:37 定向诊断确认：两种线程 sink 订阅成功，首个失败是 `ITfKeystrokeMgr::AdviseKeyEventSink(service-client=24, sink, TRUE)`，返回 `0x80070057`；不经测试转发的生产 Activate 对照相同。生产尚未调用 `GetFocus`，独立预检 `GetFocus=S_FALSE`、已建文档 `GetTop=S_OK`。本组件键盘类别查询返回空 GUID；直接使用非前台 sink 或 owner-client=32 也失败。这定位了 API 边界，尚不能证明 Windows 内部哪个身份条件导致拒绝，更不能据此认定生产 Activate 在正式 profile 下有错。未改生产、未绕过激活断言，partial/final 未执行。**不能声称标准编辑会话、移动光标映射、选中 profile 的 F8 或跨应用输入已通过**。

最新产物与日志为 `artifacts/ActivateDiagnosis`：Debug 双架构构建退出 0，COM 测试各退出 0，编辑测试各退出 1；确切命令/参数/日志见 `final-exits.json`。此轮没有注册、反注册、用户启用或根构建；生产 DLL 不含测试转发器。此前 `artifacts/Live` 的注册/恢复记录属于早先试验，不是本轮重新执行。

恢复核对覆盖 HKCU/HKLM 两种视图的本组件、默认覆盖项、语言列表、Preload/Substitutes、CTF SortOrder/Assemblies；后续扩展覆盖固定 CLSID 的 VirtualStore。原始快照未覆盖 VirtualStore，官方 API 对照后发现本试验时间窗内的 8 个全空节点，单独记录并逐叶清理，未删父目录或任何有值节点。最终两轮包含 VirtualStore 的完整前后快照相同，DLL/helper 保留；详见交接报告。

## 机制依据

实现为独立编写，参考微软样例的接口使用机制，未拷贝其拼音引擎或大段实现：

- [微软 SampleIME KeyEventSink](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/KeyEventSink.cpp)：foreground key sink，禁用 context 不吞键。
- [微软 SampleIME TextEditSink](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/TextEditSink.cpp)：只在顶层 context 订阅编辑 sink。本组件有意不沿用其“光标离开 composition 就停止”的策略。
- [RequestEditSession](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcontext-requesteditsession)：异步写锁及会话调度。
- [GetActiveProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-getactiveprofile)：只查询键盘类别的当前 profile。
- [GetTextAndPropertyUpdates](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfeditrecord-gettextandpropertyupdates)：识别手动文本编辑的范围。
- [WOW64 注册表共享/重定向规则](https://learn.microsoft.com/en-us/windows/win32/winprog64/shared-registry-keys)：COM CLSID 分架构，HKCU Software/CTF 共享。
- [注册表事务](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regcreatekeytransactedw)：写入失败不提交；[RegDeleteKeyTransacted](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regdeletekeytransactedw) 说明事务句柄上的树删除。
- [InstallLayoutOrTip](https://learn.microsoft.com/en-us/windows/win32/tsf/installlayoutortip)：指定用户输入 profile 的格式、零标志启用和不使用默认项/清空其他输入法标志。
- [微软 SampleIME Register.cpp](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/Register.cpp)、[IME requirements / IME installation](https://learn.microsoft.com/en-us/windows/apps/develop/input/input-method-editor-requirements)：用 RegisterProfile 和 RegisterCategory 注册服务，再用 InstallLayoutOrTip 加入用户启用列表；官方明确不要直接写 TSF 注册项。
- [RegisterProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-registerprofile)：LOCALPROCESS/LOCALTHREAD 仅局部可用，不是跨进程 per-user 注册的替代证明。
- [EnumProfiles](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-enumprofiles)、[EnumLanguageProfiles](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofiles-enumlanguageprofiles)：真实系统枚举接口；本轮均正常返回枚举器但本组件匹配数为 0。
- [注册表虚拟化](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-virtualization)、[MSVC /MANIFEST](https://learn.microsoft.com/en-us/cpp/build/reference/manifest-create-side-by-side-assembly-manifest)、[MSVC /MANIFESTUAC](https://learn.microsoft.com/en-us/cpp/build/reference/manifestuac-embeds-uac-information-in-manifest)：32 位兼容虚拟化会隔离写入；显式嵌入 asInvoker manifest 排除该干扰，不请求 UAC。
- [CreateContext](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfdocumentmgr-createcontext)、[ITfClientId](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itfclientid)、[ITextStoreACP::RequestLock](https://learn.microsoft.com/en-us/windows/win32/api/textstor/nf-textstor-itextstoreacp-requestlock)：可控宿主使用真实 TSF context、独立 client ID 和 OnLockGranted；不自行伪造 ITfRange。
- [MSVC /Fo](https://learn.microsoft.com/en-us/cpp/build/reference/fo-object-file-name)：带引号的目录使用双反斜杠结尾；[SDK inputscope.h/initguid.h]：单编译单元定义 InputScope GUID。
- CTF 持久化 schema 同时核对了 Wine 的 `dlls/msctf/inputprocessor.c` 与 `categorymgr.c`；仅作为公开兼容实现的布局参考，不是 Windows 注册成功的实测证据。

### 正式安装登记、用户启用与权限证据（20:37 只读复核）

- [IME requirements 的 IME installation](https://learn.microsoft.com/en-us/windows/apps/develop/input/input-method-editor-requirements#ime-installation) 明确：安装时用 `RegisterProfile`，不要直接写 TSF 登记；需要立即可用时，另用 `InstallLayoutOrTip` 加入用户启用列表。安装登记、用户启用、选为当前 profile 是不同状态。
- [SampleIME Register.cpp](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/Register.cpp) 分别实现 `RegisterServer`、`RegisterProfiles`、`RegisterCategories`：COM CLSID/DLL/Apartment，服务 CLSID/语言/profile GUID/描述/图标，以及类别信息缺一不可；不应照抄样例的全部能力类别来冒充本组件已支持。
- 机器级 COM 位置由 [InprocServer32](https://learn.microsoft.com/en-us/windows/win32/com/inprocserver32) 列为 `HKLM\SOFTWARE\Classes\CLSID\{CLSID}\InprocServer32`，包含 DLL 路径和 `ThreadingModel`。[HKCR 规则](https://learn.microsoft.com/en-us/windows/win32/sysinfo/hkey-classes-root-key) 说明它是 HKLM/HKCU 合并视图；样例向 HKCR 创建新 class key，不能因此误称它仅写 HKCU。服务/profile/类别应由官方 API 管理，本段不将手写 `CTF\TIP` schema 宣称为官方安装契约。
- [微软 UAC 开发要求](https://learn.microsoft.com/en-us/previous-versions/aa905330(v=msdn.10)) 的 Installer Detection 段明确：受保护机器目录/登记通常只有管理员可写。[注册表访问权限](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-key-security-and-access-rights) 则明确最终按 key 的安全描述符检查权限。本机只读 ACL 投影中，`HKLM\SOFTWARE\Classes\CLSID`、`HKLM\SOFTWARE\Microsoft\CTF\TIP` 给 Users 的权限是 ReadKey，给 Administrators/System 是 FullControl。因此通常机器级安装需管理员授权；这不等于日常用户启用也必须提权。
- **上述安装要求页和 RegisterProfile API 页没有把 `E_FAIL` 定义为权限不足，也没有给出“所有 Windows 上任何注册方式一律必须管理员”的保证。** 先前两架构非虚拟化 `RegisterProfile=E_FAIL`、匹配数 0 仍是实际阻断；未追踪其内部注册表调用，不能宣称已证明失败仅因权限或提权一定成功。本轮不重试注册、不测试写权限、不提权。
- [GetClientId](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfclientid-getclientid) 区分 app、正式激活 text service 和其他 TSF 对象的 client 来源；返回一个 ID 不建立 profile/category。[AdviseKeyEventSink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-advisekeyeventsink) 列出 E_INVALIDARG，但没有列出 Windows 所有内部身份校验条件；样例以 TSF 传入的 service ID 订阅 TRUE，生产不改用 FALSE 或 app ID。
- [AdviseSink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfsource-advisesink) 支持当前两种 thread sink；[GetFocus](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-getfocus) 的 S_FALSE 表示无焦点文档；[GetTop](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfdocumentmgr-gettop) 获取文档栈顶；[FindClosestCategory](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcategorymgr-findclosestcategory) 允许返回 S_OK 加空 GUID，不能只看成功 HRESULT 就称键盘类别存在。

## 变更记录：2026-11-02 20:15

- 新增实际枚举 `--probe`，保留 `--status` 契约；注册增加枚举 postcheck 和失败补偿，不再将 Input.dll 的 TRUE 当成成功。
- EXE 嵌入 asInvoker manifest，排除 x86 注册表虚拟化；无提权、安装或全局注册根替换。
- 增加受控注册/恢复快照、官方 API 对照和可选内存 TextStore 宿主；激活失败如实保留。
- 改动仅在本目录；主程序、设置页、`Docs` 未编辑。原生两架构 Debug 编译通过，主 agent 后续统一执行根构建；注册/激活阻断不能由根构建成功消除。

## 2026-11-02 20:37:43

### 实现目标
- 只读调查正式注册机制，定位内存 TextStore 宿主 Activate 的具体失败 API。

### 变更内容
- 仅 `EditTests.cpp` 添加测试专用真实接口转发、setup/焦点/类别阶段诊断及无转发/参数对照，HRESULT 原样返回。
- 保留生产激活断言和 partial/final 门禁；`VoiceIme.cpp`、`Tests.cpp`、注册代码均未修改。
- 更新本 README 与 `VALIDATION-20261102.md`；不编辑外部变更日志或其它目录。

### 验证结果
- x64/x86 Debug 构建 0，COM 各 0，nativeEditTest 各 1：`AdviseKeyEventSink=0x80070057`，partial/final NOT-RUN、writes=0、workerStarts=0，原生 COM 对象计数归零。
- 未注册、反注册、提权、写 HKLM、安装或改变既有输入法，未读真实配置、启动 worker 或云调用。