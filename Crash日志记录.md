# Crash 日志记录

## 文档目的

本文说明项目内 `CrashLogger` 的设计与使用方式，帮助你在出现崩溃、未处理异常、偶发问题时，快速定位根因。

适用范围：

- 程序启动崩溃
- UI 线程异常
- 任务线程未观察异常（`TaskScheduler.UnobservedTaskException`）
- 复杂交互链路（通过 Breadcrumb 回放）

---

## 一、当前实现概览

核心文件：`Services/CrashLogger.cs`

### 1) 负责捕获的异常来源

- `AppDomain.CurrentDomain.UnhandledException`
- `TaskScheduler.UnobservedTaskException`
- `Dispatcher.UIThread.UnhandledException`（通过 `HookAvaloniaUiThread`）
- `Program.Main` 顶层 `try/catch`

### 2) 日志输出位置

`CrashLogger` 会优先尝试创建以下目录：

1. `AppContext.BaseDirectory/logs`
2. `%LocalAppData%/TrueFluentPro/logs`

常见产物：

- `crash-YYYYMMDD-HHmmss.fff.log`（单次崩溃快照）
- `last-crash.log`（最后一次崩溃副本）
- `trace.log`（Trace 监听器输出）

### 3) 记录内容结构

每份 crash 日志包括：

- 基础元数据：时间、来源、是否终止、进程路径、OS、Framework
- `RuntimeDiagnostics`：线程池、GC、进程内存、句柄、线程数、运行时长
- `AppContextSnapshot`：业务上下文快照（由 `SetContextProvider` 注入）
- `Breadcrumbs`：最近 200 条关键路径面包屑（由 `AddBreadcrumb` 追加）
- 异常详情：完整 `Exception.ToString()`

---

## 二、项目里是怎么接入的

### 1) 启动期初始化（Program）

文件：`Program.cs`

- 在 `Main` 入口调用 `CrashLogger.Init()`
- 顶层 `catch` 中调用 `CrashLogger.Write("Program.Main", ex, true)`

### 2) UI 线程异常挂接（App）

文件：`App.axaml.cs`

- 在 `OnFrameworkInitializationCompleted` 中调用 `CrashLogger.HookAvaloniaUiThread()`

### 3) 注入业务上下文快照（MainWindowViewModel）

文件：`ViewModels/MainWindowViewModel.cs`

- 调用 `CrashLogger.SetContextProvider(BuildCrashContextSnapshot)`
- `BuildCrashContextSnapshot()` 输出：
  - 当前状态消息、翻译状态
  - 音频/字幕集合规模
  - 当前选中音频/字幕
  - 播放状态、批处理统计

### 4) 关键链路打点（Breadcrumb）

在业务关键路径调用：

- `CrashLogger.AddBreadcrumb("...")`

例如：

- 音频选择开始/完成
- 字幕加载开始/结束
- 批处理复盘调度链路
- 右键菜单显示/关闭等 UI 交互事件

---

## 三、能做什么

### 1) 快速判断崩溃归因层

- 启动层：`Source = Program.Main`
- UI 层：`Source = Avalonia.Dispatcher.UIThread.UnhandledException`
- 后台任务层：`Source = TaskScheduler.UnobservedTaskException`

### 2) 复原崩溃前状态

通过 `AppContextSnapshot + Breadcrumbs` 可还原“崩溃前最后几步”而不是只看堆栈。

### 3) 分析是否资源压力导致

通过 `RuntimeDiagnostics` 可以快速发现：

- 内存占用是否异常飙升
- 线程/句柄是否异常增多
- 线程池是否被耗尽

---

## 四、如何使用（推荐流程）

### 场景 A：用户反馈“闪退/崩溃”

1. 到日志目录找到最新 `crash-*.log`（或 `last-crash.log`）
2. 看 `Source` 判断异常来源层
3. 看异常堆栈顶部 5-10 行定位触发点
4. 对照 `Breadcrumbs` 回放操作序列
5. 对照 `AppContextSnapshot` 判断是否是特定数据规模触发
6. 如有必要补充新的 Breadcrumb 继续缩小范围

### 场景 B：偶发问题难复现

1. 在关键路径先补 `AddBreadcrumb`
2. 让问题再出现一次
3. 用 Breadcrumb 时序比对正常路径与异常路径差异

---

## 五、开发接入指南

### 1) 新模块如何接入上下文

建议在主业务 ViewModel 中统一扩充快照字段，而不是到处拼接日志。

建议写法：

- 在 `BuildCrashContextSnapshot()` 增加“可量化、可诊断”的关键状态
- 避免写入敏感信息（密钥、令牌、完整连接串）

### 2) Breadcrumb 打点建议

建议写“事件 + 关键参数”，例如：

- `LoadSubtitleCues start: subtitle=...`
- `BatchReviewQueuedAfterSubtitleLoad: seq=...`
- `AudioFileInlineMenu shown: ...`

不建议：

- 高频每帧打点（会淹没有效信息）
- 记录敏感数据

### 3) 何时调用 `WriteMessage`

`WriteMessage(source, message)` 适用于“需要持久化诊断但没有异常对象”的场景。

---

## 六、最佳实践

1. **先看 Source，再看 Stack，再看 Breadcrumb**
2. **新增诊断优先 Breadcrumb，不要先大改业务逻辑**
3. **保持快照可读且稳定**（字段少而关键）
4. **避免记录敏感数据**
5. **每次关键链路改造后，补充至少 1-2 个 Breadcrumb 点**

---

## 七、常见问题

### Q1：为什么构建通过但运行时还是崩溃？

因为这类问题通常是运行时状态组合触发（数据量、交互时序、资源占用），编译器无法覆盖。

### Q2：为什么有时只有 `last-crash.log`？

`last-crash.log` 是最新日志副本；单次崩溃原始文件仍是 `crash-*.log`。

### Q3：`Dispatcher` 异常为什么没被吞掉？

`HookAvaloniaUiThread` 中明确 `e.Handled = false`，保持默认崩溃行为，确保错误不被静默掩盖。

---

## 八、相关代码位置

- `Services/CrashLogger.cs`
- `Program.cs`
- `App.axaml.cs`
- `ViewModels/MainWindowViewModel.cs`
- `ViewModels/FileLibraryViewModel.cs`
- `Views/ReviewModeView.axaml.cs`

---

## 结语

`CrashLogger` 的价值不只是“记录崩溃”，而是把崩溃从“黑盒”变成“可回放的现场”。

如果你后续继续做复杂交互（例如菜单、批处理、播放器联动），优先补 Breadcrumb，而不是先猜。这样每次定位都会更快。