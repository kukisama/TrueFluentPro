# 输入法常驻 UI 宿主定向验证

## 接线

- `Program.Main` 在主程序初始化前识别固定 `--voice-ime-ui-host <parentPid> <parentStartUtcTicks>` 模式；只允许此三个参数。保留 `--voice-ime-settings`、`--voice-ime-usage`、`--voice-ime-language` 冷启动入口。
- `VoiceImeSharedHost` 在 toolbar/server 就绪后排队 Warm，浮条 kind3/5/6 分别排队 Settings/Usage/Language。actor 不执行进程启动或等待 IPC。语音 owner 不创建 Avalonia Application，UI 客户代理单独位于无 Avalonia 引用的源码文件。
- UI 新路径为 `source.voice-ime-ui/<完整内容SHA-256>/<exe>`（`source` 为原 owner 所在目录，保留 exe 文件名）；内容身份来自交付清单，不只是 exe 摘要。`SourceExecutable` 严格校验路径与 64 位十六进制内容目录，回映原 owner exe，再核对进程身份及包内容。旧直接 suffix 路径仅保留兼容维护回映，不用于新包部署。变包另建内容目录，不覆盖或清理仍在使用的旧 UI。
- `VoiceImeUiClient` 单个消费者、16 项有界队列，ready 上限 15 秒、请求上限 5 秒；存活子进程未实际退出时不再重复启动。收尾仅取消消费者、断开 lease、释放进程句柄，不强杀 UI、不等 UI 退出，也不调用 `RecordHost`。
- UI 只有一个 pipe 连接，`CurrentUserOnly` 双向检查当前用户和提权级别；SID/session/父 PID/启动时间隔离命名，双方再检查实际 pipe 对端 PID，核对 exe/session/进程身份。Mutex 与 FirstPipeInstance 保持单飞。五字节固定帧，长度必须为 1，负载只能为 Warm/Settings/Usage/Language；首字节后的剩余数据限时 5 秒，无任意路径、命令或编辑目标。
- UI `OnExplicitShutdown`，不设置 MainWindow；只在明确窗口命令后 Show/Activate。每种窗口最多一个实例；重复点击还原/激活，Closed 从字典删除，下一次重新构造，不 hide。owner 退出、lease 断开或维护门禁出现后停止接新命令；已开的设置/语言/统计保留到用户关闭，末窗关闭后退出；无窗时直接退出。
- UI 不写录音 Host receipt；设置维护使用严格回映的原 owner 路径，录音进程发现与 UI 分离。录音 receipt 的 `WaitForHostAsync` 仍严格等待真实录音进程退出，不能因麦克风已释放或 UI 仍存活而放宽。
- `VoiceImeUiTheme` 仅 FluentTheme、设置页副标题 ControlTheme、卡片/说明文本类及四个明暗资源，不加载 App.axaml、FluentAvalonia 主主题或主 DI。
- 设置窗先等待真实 CompositionBatch.Rendered，再后台读取/解析配置；调用原有设备与状态刷新命令并行启动。关闭后的迟到加载不再更新窗口，已提交的自动保存保持原有防抖语义。
- 提权确认优先使用当前可见设置窗口，关闭时清除弱引用；主程序调用仍可回退到可见活动窗/MainWindow。没有改变提权 worker 的事务实现。

## 运行

从仓库根执行 `dotnet build .\tools\VoiceImeUiHostRegression\VoiceImeUiHostRegression.csproj --nologo`，然后执行 `& .\tools\VoiceImeUiHostRegression\bin\Debug\net10.0\VoiceImeUiHostRegression.exe`。

此工具不引用主项目、不加入 solution、不触发 native/Updater/Sync-Host。直接链接本轮 UI 宿主、设置、提权框及现有语言/报表源码，复用 `bin/Debug/net10.0` 已有托管依赖和设置 Section 的编译 XAML。若主 agent 更改了相关依赖，可用 MSBuild 的 `ProductionDir` 属性指定另一份现成产物，不运行主构建。

测试只启动本工具自身 exe 或其隔离复制件；生产 CLI 不接受测试路径或开关。测试工厂给设置注入内存配置与刷新替身，语言指向本工具私有 JSON，报表指向私有缺失 DB。无真实配置/数据库、系统登记、UAC、麦克风、云、主窗口/主 DI，也不结束现用程序。维护专项使用 `--integration-test`，替换授权/维护/状态动作，保留真实设置按钮、生命周期与严格录音等待。

验收包括：真实 Show + Rendered 冷启动/预热计时、无窗/无激活/零业务窗口构造预热、同包单飞、三窗口同 PID、最小化后重复打开、关闭重建、当前设置 owner、维护/lease/父退出后的保留与关闭、超大帧及错误父身份拒绝、连接期间末窗关闭后框架仍活着。窗口数核对实际 Avalonia `Window` 类型及 HWND/PID，不把 dispatcher、IME 等所有辅助 HWND 算成业务窗口。

## 本机结果（2026-11-06）

最终独立构建、默认专项、维护专项均退出 0，两个专项均 PASS：[退出汇总](../../artifacts/voice-ime-ui-final/root-cause-final-20261106-224402-994/exit-summary.json)、[默认结果](artifacts/20261106-224407-504/result.json)、[维护结果](artifacts/20261106-224458-803-maintenance/result.json)。

| 最新真窗口 | 单独 cold 到 Rendered | shared ready 后到 Rendered |
| --- | ---: | ---: |
| 设置 | 2840.46 ms | 2414.25 ms |
| 统计（Usage） | 2712.63 ms | 613.05 ms |
| 语言 | 2950.62 ms | 717.62 ms |

两组每项各一次；shared 按“设置→统计→语言”依次打开，后两项复用首窗字体/模板工作。负载波动显著，不承诺固定速度，也不能把后续窗口耗时外推为首次点击速度。使用真生产窗口源码及最小主题/隔离数据，不是空壳或旧完整 App.axaml 基线；设置加载替身不代表真实设备/状态耗时。

计时起点为 caller 在 Request 或 Process.Start 前记录的 Stopwatch 时间戳，终点为实际窗口 Show 后 CompositionBatch.Rendered 完成；不把 ready、ACK 或 Process.Start 返回当作渲染完成。无固定睡眠或十轮循环。

历史样本保留：[旧结果](artifacts/20261106-210001-543/result.json)，[独立构建](artifacts/build-4.log)退出 0、0 警告/0 错误，[桌面运行](artifacts/run-2.log)退出 0。

| 历史真窗口 | cold | shared |
| --- | ---: | ---: |
| 设置 | 1295.67 ms | 786.07 ms |
| 统计（Usage） | 1003.15 ms | 101.79 ms |
| 语言 | 1169.78 ms | 81.52 ms |

旧样本也各一次；预热 ready 547.47 ms、working set 106999808 字节（约 102 MiB），无窗预热前后前台句柄相同、业务窗口构造为零。旧 lease/维护退出 133.89/94.43 ms 属于旧生命周期，与当前“已开窗口保留”验证边界不同，不能作为当前退出或速度承诺。

- [内容目录共存结果](artifacts/20261106-220648-105-content/result.json) PASS：旧设置及原结果保持可见；同内容新 owner 复用内容目录，配置/DLL 变化使用新目录，旧 UI 均仍存活，新 owner Ready/ACK 通过；过期源包拒绝且客户端未启动 child，用户关闭后自有进程清零。此为源码链接隔离复制件，不是实际 Sync 或系统安装验收。
- 最终维护专项覆盖安装/卸载按钮、owner Dispose/父退出、原设置保留到用户关闭、无录音 receipt、替代录音 owner 单独发现；麦克风释放但录音 receipt 对应进程仍活着时继续等待，真实退出后才通过。
- 错误同用户 client 必须零字节拒绝；Connect 阶段 `CurrentUserOnly` ACL 查询被服务端断连打断也可成立，仅接受已核验的断连 HRESULT，或 `stage=connect`、`IsConnected` 且栈含 `ValidateRemotePipeUser` 的 `InvalidOperationException`。超时、权限错误及其它异常不算通过；收到任何字节即失败。随后合法 owner 必须在原监听 PID/start/exe 上收到 Ready 和 Warm ACK，不能只验拒绝端。

失败记录不覆盖：[首次运行](artifacts/run.log)误把加载 Avalonia.Controls 当作 Application/主 DI 初始化；首次编译缺报表配套类型/MVVM 引用，仅修专项依赖。另保留[共享信号失败](../../artifacts/voice-ime-ui-final/maintenance-race-20261106-213415-432-090b640e/maintenance.stderr.log)与[错误客户端 ACL 断连栈](../../artifacts/voice-ime-ui-final/ui-host-integration.log.stderr.log)；前者以写完再重命名、读取允许 `FileShare.Delete` 处理，后者按上述零字节/合法重连判据验收。执行包装器未返回结果的空日志不作证据。

## 边界与交接

- 已有[主构建](../../artifacts/voice-ime-ui-final/production-build.log.result.json)退出 0；`VoiceImeRegression` 的 [--shared](../../artifacts/voice-ime-ui-final/voice-shared.log.result.json)、[--usage](../../artifacts/voice-ime-ui-final/voice-usage.log.result.json)均退出 0。这些是各自已执行记录，不等于全部真实业务验收。
- [实际 Sync-Host](../../artifacts/voice-ime-ui-final/sync-result.json)退出 0，主托管 DLL、x64/x86 输入法 DLL 三组均匹配 Debug，安装 HostExe 未变。新 owner 与内容目录 UI 子进程存在、路径/父 PID 符合，只能支持准备完成后启动的推导；本次 Sync 未连接 IPC，Ready、Warm ACK、窗口渲染均未直接观测。不能拿 UI 进程存在证明 ready。
- [早期三 DLL 核对](../../artifacts/voice-ime-ui-final/final-deployment-verified.json)当时均不匹配，后由上述实际 Sync 匹配；[边界核对](../../artifacts/voice-ime-ui-final/final-boundary-verified.json)标明更早报告的比较根目录错误/乱码比较，不能沿用无效报告作为成功证据。
- 未实测不同 SID/session/提权 token 的对抗连接、Windows 物理点击前台授权、多屏 DPI、DWM 实际 Present、长时内存稳定性或真实 UAC/维护续办。代码的固定身份/命令边界与本机同令牌测试不能替代这些验证。
- 启动失败不循环重试；仍活着但不响应的 child 不再另起副本，不以强杀 UI 恢复。Windows 前台限制下 Activate 只能尽力，不能把调用成功冒充实际焦点切换。
- 历史上发现本专项被写入 solution 后，仅移除了自身 GUID 条目，写入进程未取证；摘要恢复为 `4616E87DD037B26B2E68828250B9643E468D0FDE56794226A0D1DD0F4329B1AE`。最终测试运行前后字节一致仅证明该次运行未变，不能声称整任务 solution 从未改变，也不归因 IDE 或其它 agent。本次仅更新 README，不测试、不构建、不改 Docs/源码/solution。

## 官方依据

- [Avalonia 11.3.11 ClassicDesktopStyleApplicationLifetime](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/11.3.11/src/Avalonia.Controls/ApplicationLifetimes/ClassicDesktopStyleApplicationLifetime.cs)：无 MainWindow 仍可运行 dispatcher，OnExplicitShutdown 不因末窗关闭退出。
- [Avalonia 11.3.11 Compositor](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/11.3.11/src/Avalonia.Base/Rendering/Composition/Compositor.cs)：RequestCompositionBatchCommitAsync 返回 batch；Processed 不能替代 Rendered。
- [PipeOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0)：CurrentUserOnly 在 Windows 同时比较用户和提权级别，本 UI 不跨提权通信。
- [.NET 10 NamedPipeClientStream.Windows](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.Windows.cs)：TryConnect 先设置 Connected，再由 ValidateRemotePipeUser 查询 ACL，解释 Connect 阶段的断连竞态。
- [Windows 重命名通知](https://devblogs.microsoft.com/oldnewthing/20211022-00/?p=105822)：通知可早于 DELETE 句柄关闭，读取需允许删除/重命名共享，不应靠放宽写共享或盲目延时。
- [CLR 托管程序集加载规则](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/loading-managed)：静态程序集加载时机未指定，并受内联影响；加载程序集不等于创建 Avalonia Application。