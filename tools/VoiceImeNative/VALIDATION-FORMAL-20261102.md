# VoiceImeNative 正式登记方案交接（2026-11-02）

## 当前结论

原生实现已从 HKCU 手写 CTF 改为机器级 COM + 微软正式 TSF 登记 API。**管理员成功注册未执行**，不能据此承诺 Win+Space、F8、真实语音或跨应用编辑已可用。

仅修改/生成 `tools/VoiceImeNative/` 内的文件。未运行根构建、未修改 C# 或外部变更日志，未写 HKLM、未实际登记/反登记、未调用 InstallLayoutOrTip、未请求 UAC、未安装 Program Files 文件、未读取真实应用配置/凭据、未改用户输入法、未删缓存。允许的 `--register` 仅在无登记且非管理员情况下验证权限拒绝，返回 10，止于 TokenElevation 门控。

`VALIDATION-20261102.md` 仅保留旧 HKCU 原型及旧试验写入记录，不是本次执行或新方案成功证据。

## 修改文件

- `Registration.cpp`：正式 Windows 后端、机器 COM、用户 HostExe、权限、路径、legacy 清理、真实枚举、错误记录。
- `RegistrationPolicy.h`（新增）：状态/双架构策略和新增项补偿。
- `Common.h`：状态/错误声明；保留原函数 ABI，注释机器级语义。
- `RegisterMain.cpp`：单行 JSON、stage/HRESULT、10 零写入权限拒绝、幂等输出。
- `RegistrationTests.cpp`（新增）：纯内存后端定向测试，不链接 Windows 写入后端。
- `Tests.cpp`：保留默认 COM 测试，移除旧真实 RegisterProfile 对照入口。
- `Build.ps1`、`Verify.ps1`：构建四组产物及内存策略测试；不执行登记。
- `Test-Registration.ps1`：只读 status/probe，可选非管理员拒绝；删除旧 exercise/恢复/默认输入设置读取逻辑。
- `Run-LiveValidation.ps1`：仅 Baseline；删除 Api/Helper 写入模式，编辑失败仍阻断。
- `README.md`、本报告：更新正式契约与实测边界。

`VoiceIme.cpp`、`PipeClient.cpp`、`EditTests.cpp`、双架构 `.def` 均未改；原导出继续调用相同注册函数名。产物和证据均位于本目录 `artifacts`。

## 实际命令与退出码

执行目录：`C:\Scripts\TranslationToolUI`。以下只写已执行命令；管理员安装命令未执行。

### 构建及默认离线测试

命令：`pwsh -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeNative\Verify.ps1`，退出 **0**。

该脚本通过原有 Build.ps1 实际执行：

- `Build.ps1 -OutputDirectory C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\Debug -Configuration Debug`：退出 0，包含 x64/x86。
- `Build.ps1 -OutputDirectory C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\Release -Configuration Release`：退出 0，包含 x64/x86。

每组分别执行 `VoiceImeTests.exe <同目录绝对DLL>` 与无参数 `VoiceImeRegistrationTests.exe`：

| 配置 | 架构 | Build | 默认 COM | 内存安装策略 |
| --- | --- | ---: | ---: | ---: |
| Debug | x64 | 0 | 0 | 0 |
| Debug | x86 | 0 | 0 | 0 |
| Release | x64 | 0 | 0 | 0 |
| Release | x86 | 0 | 0 | 0 |

产物前缀为 `C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\<配置>\win-<架构>\`。默认 COM 不调用注册导出、不启动后台；内存策略不使用真实 Windows 登记后端，覆盖零写入门控、枚举缺失不成功、双架构保留、路径冲突、owned legacy 清理/顺序、未知查询失败、新增项补偿及补偿失败。

附带检查：Release 增量构建退出 0；相对输出目录拒绝退出 1（预期）；导入表检查退出 0，无动态 VC 运行库依赖。完整矩阵：`artifacts/validation.json`（2026-11-02T13:08:31Z）；各配置 `build-final.log`、`test-final-win-*.log`、`registration-policy-win-*.log`。

### 真实只读 helper 与非管理员拒绝

命令：`pwsh -NoProfile -File C:\Scripts\TranslationToolUI\tools\VoiceImeNative\Test-Registration.ps1 -BinaryDirectory C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\Release -VerifyNonAdminRefusal`，脚本退出 **0**。

执行进程 `Elevated=false`。两架构分别调用各自 Release 产物：

| 参数 | x64 退出码 | x86 退出码 | 结果 |
| --- | ---: | ---: | --- |
| `--status <同目录TrueFluentVoiceIme.dll>` | 1 | 1 | 单行 JSON：registered=false，state=unregistered，requiresAdmin=true |
| `--probe` | 1 | 1 | enumerated=0，enabled=0，keyboard=0 |
| `--register <同目录TrueFluentVoiceIme.dll> <同目录VoiceImeTests.exe>` | 10 | 10 | `failed stage=RequiresAdmin HRESULT=0x800702E4` |
| 拒绝后再次 `--status <同目录DLL>` | 1 | 1 | 与拒绝前 JSON 完全一致 |

Host 参数仅使用测试 EXE 作为本地 fixture；权限检查之前不加载 DLL/Host、不进入任何写 API，未运行 Host。没有调用任何 `--unregister`，幂等注销仅经内存策略验证。完整绝对路径/参数/输出/退出码：`artifacts/readonly-registration.json`（2026-11-02T13:09:14Z）。

### 原生编辑宿主：仍然失败，不计入通过

分别执行 `C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\Release\win-x64\VoiceImeEditTests.exe` 和 x86 对应命令，各退出 **1**。

- 日志：`artifacts/Release/edit-current-win-x64.log`、`edit-current-win-x86.log`。
- 两架构都成功创建真实 TSF thread/document/context。
- 生产 Activate 首个失败仍为 `ITfKeystrokeMgr::AdviseKeyEventSink(24,sink,TRUE)`：`0x80070057`。
- partial/final 为 NOT-RUN、文本 writes=0；后台未启动，原生 COM 对象计数归零。
- 未降低激活门禁或改生产代码伪造成功。失败后复核微软 AdviseKeyEventSink/GetClientId 文档；这些文档没有证明正式安装后一定可用。需在用户完成管理员安装后另验真实激活/编辑链路。

`git diff --check -- tools/VoiceImeNative`：退出 0。该目录当前整体未被 Git 跟踪，因此不能把此结果当作全部新增源码的差异检查；另用绝对路径逐一读取本报告列出的 12 个修改文件，尾随空白匹配数为 0。四个 PowerShell 脚本的 ParseFile 语法错误数均为 0。

最终独立绝对路径复核：`artifacts/validation.json` 存在，Verify/Build/全部 COM/内存策略 ExitCode 均为 0；只读报告 Elevated=false，状态/probe 各 1、拒绝各 10。一次子摘要误报报告不存在，未据此重跑测试或修改源码，以实际落盘文件与独立终端读取为准。

## 主程序集成要点

- CLI 参数未变，状态输出已替换为 JSON，错误信息为固定 ASCII stage + HRESULT，不解析/显示路径和凭据。
- `requiresAdmin` 指机器变更权限需求，不是当前进程状态；10 只由写入前 TokenElevation 门控产生。其它 E_ACCESSDENIED 不伪装成“无写入的 10”。状态/错误细则见 README。
- DLL/helper 由安装方放到受保护的原生 Program Files 下 `TrueFluentPro\VoiceIme\<SHA256>\win-x64|win-x86`；安装方保留 all-users-read / non-admin-not-write ACL。原生层不会创建目录/改 ACL/请求提升。
- HostExe 和启用仅属于执行 helper 的用户；若另一个管理员身份运行，将针对其 HKCU，不能当作原用户启用成功。
- RegisterProfile 默认禁用，再经用户启用；不修改默认输入法或其它 profile。不在第二架构重复注册已有共享 profile/category。
- 注销非最后机器架构不调用 UnregisterProfile/UnregisterCategory；最后一侧才注销共享部分。DLL 文件保留，不因图标路径仍引用另一架构而删除它。
- 新安装失败精确补偿本轮新增项。任一补偿失败/查询未知/新增 TSF 残留输出 5 与 rollback stage，不声称原子成功；保留 COM 所有权供重试。已存在不同路径或无 owner 的项在写入前拒绝。
- 旧 HKCU override 明确提示 legacy 清理；只在精确 owner+路径匹配时显式清理。旧共享 TIP 图标路径对应的 COM 必须最后清，错误顺序明确拒绝；不偷偷删别的机器安装。
- 当前仅完成代码、编译、离线策略、只读及拒绝测试。用户管理员安装、枚举、Win+Space、物理 F8、真实文本编辑由主 agent 最后统一交接，不把这份报告当作已安装声明。

## 官方机制依据

- [IME installation](https://learn.microsoft.com/en-us/windows/apps/develop/input/input-method-editor-requirements#ime-installation)：安装用 RegisterProfile，不直接写 TSF；立即可用另用 InstallLayoutOrTip。
- [微软 SampleIME Register.cpp](https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/IME/cpp/SampleIME/Register.cpp)：分离 COM server、profile、category；不照抄样例额外能力类别。
- [RegisterProfile](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-registerprofile)：bEnabledByDefault 和 flags；E_FAIL 只是 unspecified error。
- [InstallLayoutOrTip](https://learn.microsoft.com/en-us/windows/win32/tsf/installlayoutortip)：BOOL 返回、零 flags 启用、ILOT_UNINSTALL；不使用默认项或清空配置标志。
- [WOW64 共享项](https://learn.microsoft.com/en-us/windows/win32/winprog64/shared-registry-keys)：HKLM CTF TIP 共享、CLSID 分架构，HKCU Software 共享。
- [EnumProfiles](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-enumprofiles)、[EnumCategoriesInItem](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfcategorymgr-enumcategoriesinitem)：实际枚举，而非 IsEnabledLanguageProfile 假定存在。
- [KNOWNFOLDERID](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid)：32 位进程不能使用 FOLDERID_ProgramFilesX64；本实现从显式原生注册表视图读取 ProgramFilesDir，避免 x86 指向 Program Files (x86)。
- [AdviseKeyEventSink](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-advisekeyeventsink)、[GetClientId](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfclientid-getclientid)：本次编辑失败的机制核对；GetClientId 成功不等于已正式激活 text service。

## 2026-11-02 13:09:14 UTC

### 实现目标
- 替换 HKCU 手写 CTF 原型为正式系统登记方案，保持非提权执行零写入。

### 变更内容
- 正式 TSF API、机器 COM/当前用户 HostExe 分离、状态 JSON、精确所有权、共享双架构及失败补偿；更新仅本目录文档与测试。

### 验证结果
- 四组构建、COM、内存策略通过；只读状态/probe 各 1，权限拒绝各 10；编辑宿主各 1，未掩盖。
- 管理员安装/真实成功登记未执行；没有实机卸载或 InstallLayoutOrTip 调用。