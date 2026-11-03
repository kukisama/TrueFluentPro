# libIME2 原生语音适配

- 上游：https://github.com/EasyIME/libIME2
- 固定提交：`287c24b3caa5f5412e3c91f94056f8a32ad9868d`
- 原始许可：`LICENSE.txt`（GNU LGPL 2.1）；原源码中的版权与 Library GPL v2-or-later 声明原样保留。
- 修改日期：2026-11-03；修改者：TrueFluentPro contributors。

## 本地修改

- `ComPtr.h`、`SinkAdvice.h`：移动赋值释放旧引用/订阅，保存订阅的真实 HRESULT。
- `TextService.h/.cpp`：框架统一管理线程、键盘与 compartment sink，激活失败清理并返回错误；不在 thread manager 上订阅 context 编辑 sink。提供 `keyEventsNeedEditSession()` 扩展，语音 F8 回调不申请同步编辑锁；实际文本仍用原有异步写锁。没有语言栏按钮时不创建语言栏，无编辑按键模式不自动打开共享键盘 compartment。
- `ImeModule.cpp`：无显示属性时不初始化候选窗口类；`VOICE_IME_RUNTIME_ONLY` 将整个上游注册/卸载/全用户 hive 实现排除，注册方法返回 `E_NOTIMPL`。DLL 导出继续使用项目原有 Registration 公共接口。

`VoiceIme.cpp` 实际继承 `Ime::TextService`，保留原有唯一 composition、密码/焦点/profile/maintenance 门禁和管道后台。仅补框架缺少的线程焦点与 context 编辑/状态通知；不使用上游候选引擎、配置 UI 或另一套组合区。

## 构建及许可交付

`tools/VoiceImeNative/Build.ps1` 直接编译上游 CMake 源码列表中所需的 cpp，静态链接入原生 DLL；Debug `/MTd`，Release `/MT`。无需安装依赖。源文件与许可随本目录保留，不以只复制 include 冒充框架集成。

本仓库不提交未使用的 `lib/googletest-release-1.10.0/`（由原生模块 `.gitignore` 精确排除），保留核心源码、许可、上游测试与构建说明。项目构建和重链接使用上述 `Build.ps1`；若要运行上游 README 的 CMake 测试流程，需从上述固定提交补齐 GoogleTest 目录。

发布静态链接产物时须同时提供本库完整对应源码（含本地修改）、许可与足以重新链接该原生模块的应用源码/对象及构建材料，并保留修改/调试权利。单独打包 DLL 不代表已完成 LGPL 分发义务；正式发行包由主工程维护者统一处理。

## 变更日志

### 2026-11-03
- 实现目标：以固定 GitHub 开源框架承接真实 TSF 生命周期，保留纯语音编辑契约。
- 变更内容：上述受限 runtime 修补及最薄 Service 适配；Registration、C#、协议、固定 GUID、既有测试未修改。
- 验证记录：见本模块 `artifacts` 下本轮构建及离线测试日志；实际退出码由交接报告说明。

### 本轮验证结果

- 从仓库根执行 `pwsh -NoProfile -File tools/VoiceImeNative/Build.ps1 -OutputDirectory C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\libIME2-20261103\Release -Configuration Release`；首次 x64/x86 均成功，无编译错误。后续只消除本次引入的宏重复/局部遮蔽警告，没有修改注册代码或测试。
- 最终执行 `pwsh -NoProfile -File tools/VoiceImeNative/Verify.ps1 -ArtifactDirectory C:\Scripts\TranslationToolUI\tools\VoiceImeNative\artifacts\libIME2-20261103`；报告 `validation.json` 总 ExitCode=0，Debug/Release 构建各 0，四组 COM、四组内存策略、四组 maintenance 生命周期退出码全部 0，增量 Release 0；相对输出目录拒绝 1 为预期。
- 日志：`verify.stdout.log`、`verify.stderr.log`（空）、各配置 `build-final.log`、`test-final-win-*.log`、`registration-policy-win-*.log`、`lifecycle-win-*.log`；`dependencies.log` 没有动态 VC 运行库依赖。
- COM 覆盖真实 DLL 加载、框架派生服务创建、必要接口、非选中 F8 透传、活动对象阻止卸载及最终引用归零。maintenance 覆盖生产 WM_TIMER 停止、generation/旧帧隔离和未读配置前 worker 退出。
- 没有执行系统注册/卸载、提权、根工程构建、真实配置/凭据读取、后台启动、音频或云端调用。现有 `VoiceImeEditTests.exe` 仅编译，未运行；真实已登记 profile 激活、跨应用 partial/final、光标映射、密码控件与实际后台 STOP/EOF 仍由主 agent 单独验收。
- 原始克隆 Git 元数据仅移入本模块被忽略的 `artifacts/libIME2-20261103/upstream-git-287c24b3` 保留，没有删除；本目录本身为普通 vendored 源码，不需要嵌套仓库或 submodule 拉取。