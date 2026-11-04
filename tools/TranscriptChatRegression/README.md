# 字幕问答定向回归

从仓库根目录运行 `dotnet run --project tools/TranscriptChatRegression/TranscriptChatRegression.csproj`。

- 需要可显示窗口的桌面环境，会短暂打开并关闭实际 Avalonia 窗口。
- 复用生产代码验证完整录音稿快照、SQLite 保存与历史恢复、连续追问上下文，以及独立窗口关闭/返回/重开。
- 测试资料及独立数据库保存在 `artifacts/transcript-chat-regression/<随机目录>`，不读取真实配置或模型凭据，不调用云端模型。
- 成功时输出 `PASS` 并返回 0；失败时输出异常并返回非零退出码。
- 聊天听写使用模拟识别源验证实际绘制的录音底色、整体呼吸和音量音柱、临时/定稿文字去重、句中插入及前后原文保留、连续句、录音中鼠标路由点击及键盘调整锚点、当前句固定范围修订、下一句选区随修订长度映射、选区替换及手改保护、立即停止、迟到结果隔离和错误退出；另检查生产纯转录协议及配置快照。停止测试故意保持设备释放未完成，确认输入与发送不被阻塞。
- Windows 下验证 F8 注册及原生 `WM_HOTKEY` 开关。测试窗口能取得前台时使用 `SendInput`；无法取得前台时输出明确的 `SKIP physical global F8`，只执行原生窗口消息对照，不将其宣称为真实全局按键验收。
- 不使用真实麦克风和云端识别，不能代替实际语音资源及网络环境的端到端验收。
- 光标专项同时检查 TextBox 逻辑位置、TextPresenter 光标、实际绘制字符命中和可见坐标，覆盖片段末尾、移动后保留新位置、中文增量、等待下一句、缩短修订、中英混排、多行长文和键盘交错输入；不能只以 CaretIndex 等于文本长度作为通过依据。普通点击只调整锚点，不停止麦克风；未定稿句继续在原范围修订，下一句使用新锚点，不强制插入换行。

## 浮动字幕真实桌面验收

- 先从仓库根执行 `dotnet build`，再运行 `tools/TranscriptChatRegression/bin/Debug/net10.0/TranscriptChatRegression.exe --floating-subtitle`，仅运行字幕专项，不启动默认聊天／听写回归。
- 需要可交互的 Windows 桌面，会暂时操作系统鼠标和键盘；运行期间请勿同时操作。使用生产字幕窗口、LiveTranslationView 恢复菜单及命令，跳过真实主业务初始化，不加载用户配置、不访问麦克风或云端。
- 验证实际字形两行、无横向溢出／控制栏遮挡、第三行出现时旧下行原样上推、同句 partial 幂等修订、终稿缩短、静默保留、宽度换行与高度自动字号、字号锁定和重置、背景循环、系统键盘快捷键、原生拖动及缩放把手、下层按钮实际接收穿透点击、生产菜单恢复，以及关闭清理和重新打开。
- 实时同步专项确认：新句衔接旧下行，不整页清屏；同句从固定检查点重新排版，不重复追加，不再按计时器翻页。快速连续新句直接显示最新末两行；旧终稿、旧空帧、终稿后迟到 partial 不污染显示及 VM 最新快照，只有显式清空才重置上下文。另以不限行 TextLayout 确认输出本身不超过两行，不能仅凭 MaxLines 裁剪后的结果通过。顶部控制栏不超过 28，正文与控制栏间距不超过 5。
- 字号使用 `Ctrl+加减号`、`Ctrl+0`；窗口移动使用 `Alt+方向键`，缩放使用 `Ctrl+Alt+方向键`。不再使用本机实测会丢失 Shift 修饰键的 `Alt+Shift` 组合。
- 原生鼠标检查包含 HWND、实际光标坐标和命中控件；下层窗口激活排序后确认真正位于字幕下方，不能只看 WS_EX_TRANSPARENT 样式位。拖动分阶段送入系统输入，避免移动消息合并及原生移动循环尚未进入造成假失败。
- 结果和生产控件渲染图保存在 `artifacts/floating-subtitle-validation/<时间戳>`；成功输出 `FLOATING_SUBTITLE_EXIT=0` 并返回 0。这是受控字幕的真实桌面 UI 验收，不代表真实音频／云端翻译已通过。
- 若必须保留现用程序和输入法后台，可将生产项目与专项项目分别以 `dotnet msbuild <项目> -target:CoreBuild -property:OutputPath=<同一绝对隔离目录>/ -property:BuildProjectReferences=false` 编译，再直接运行隔离目录的 `TranscriptChatRegression.dll --floating-subtitle`。普通 Build 的后置步骤会执行独立的 Host 热同步，不属于无干扰验证。

## 主窗口自绘标题栏验收

- 运行已构建的 `TranscriptChatRegression.dll --window-chrome`，只执行主窗口外壳专项。复用生产 `MainWindow` 与主题，不构造主 VM、不加载业务配置或真实服务；使用真实 HWND 和系统鼠标，结束后关闭测试窗口并恢复主题。
- 检查系统 caption 已移除、正常状态仍保留原生缩放边框；顶部为 40 DIP 拖动／按钮区加 1 DIP 分隔线，按钮为 46×40 DIP，业务导航及页面从标题栏下方开始，不重叠。
- 真实点击最小化、最大化／还原和关闭，并同时核验 Avalonia 状态、IsIconic／IsZoomed、图标／辅助功能名称；检查最大化客户区不覆盖任务栏。真实标题拖动和双击，以及原生 HTBOTTOMRIGHT 边缘拖动、WM_SIZING 和实际尺寸增长均须通过。
- 若 Windows 拒绝 Activate，测试只临时置顶自己的窗口，核验 WindowFromPoint 后点击自身标题区取得焦点，再恢复置顶状态；不绕过系统前台策略、不向其他窗口注入输入。
- 记录现有屏幕 Scaling。只有实际存在且工作区能容纳当前主窗口最小尺寸的 200%／400% 屏幕才执行相应原生交互；否则明确 SKIP，不修改系统 DPI、不伪造 WM_DPICHANGED。
- 标题栏另以 2×／4×、192／384 DPI 渲染，检查 DIP／像素比例、文字与图标边界并保存 PNG；这是倍率绘制检查，不代表原生高 DPI 跨屏通过。报告位于 `artifacts/window-chrome-validation/<时间戳>/result.txt`，成功输出 `WINDOW_CHROME_EXIT=0`。

## 宋人书斋／经典外观专项

- 构建后运行 `TranscriptChatRegression.dll --appearance`；该分支只执行外观专项，不运行默认聊天、字幕或原生标题栏测试。需要可显示窗口的桌面，专项设 40 秒取消预算，保留入口原有 45 秒总 deadline。
- 创建真实生产 `MainWindow`，显式传入 `ShellAppearancePreferencesService`，路径限定为仓库 `artifacts/song-study-validation/<时间戳-随机后缀>/appearance.json`。由 `OnOpened` 创建原有 `LiveTranslationView`；不构造主 VM、不启动业务服务，不读取真实配置／注册表／麦克风／云端，不写真实 AppData、不改系统主题。
- 使用公开 `INotifyPropertyChanged` 轻量夹具保留三个编辑器的真实文本绑定。两次书斋／经典往返检查 PageHost、页面、缓存 Spy、三个 `ConfigurableTextEditor`、底层 AvaloniaEdit 编辑器／文档、历史集合和原历史项的引用；监测切换期间 Detach 为零，检查逻辑编辑焦点、生产字号选择器设置及受控正文更新不丢。关闭窗口的正常 Detach 不计入切换断言。
- 应用保持 Dark 时，书斋窗口／页面须为 Light；返回经典恢复继承 Dark。通过窗口及页面作用域 `TryFindResource(key, ActualThemeVariant, ...)` 检查 PrimaryBrush、正文、纸面、表面与边框色；预载样式集合始终不变，局部书斋色板须真正撤销，编辑器经典颜色／最小高度恢复，应用资源及样式不变。直接 `ApplyShellAppearance` 不得改写偏好文件字节或修改时间。
- 在实际 1000×550、1440×860 客户区读取 SideRegion、TranscriptRegion、TranslationToggleButton 的 DIP／屏幕像素边界及祖先裁剪；激活生产 Expander 标题验证折叠／展开，有溢出时检查 SettingsScroll 独立滚动。开始／停止按钮必须在折叠内容之外且仍可见可用，绝不点击或执行启动命令。
- `RaiseClick` 通过 Avalonia 11.3.11 的生产 `Button.OnClick` 激活入口打开主题 Flyout，再通过实际 `MenuItem.ClickEvent` 选择书斋，走生产 SaveAndApply 并用新服务实例重载隔离文件。随后选择“经典 · 浅色”：无主 VM 时 UpdateThemeMode 不执行，原 Application Dark 仍须保留。该流程是控件激活／路由验收，不冒充物理鼠标键盘验收。API 依据为该版本官方 `Button.cs`、`MenuItem.cs`、`ToggleButton.cs` 和 `ResourceNodeExtensions.cs` 源码。
- 服务仅覆盖缺失文件、未知外观标识回退 Classic 及正常保存／重载；同目录隔离标记须原样保留、保存后无 `.tmp` 残留，不删除测试目录或扫描用户目录。成功输出 PASS、`ARTIFACTS` 与 `SHELL_APPEARANCE_EXIT=0`；`finally` 隐藏菜单、关闭窗口、恢复回归宿主原主题并写 `result.txt`。
- 保存且仅保存一张 `study.png` 和一张 `classic.png`，由 `RenderTargetBitmap` 渲染已布局的生产窗口供主 agent 查看真实 UI，不做素材总览。这是**受控状态的真实 UI 验收**，不代表实际音频／翻译服务、真实业务 VM 状态、物理输入或系统高 DPI 已通过；专项源码本身不等于已构建／已运行结果。
- 布局更新后等待实际 `CompositionBatch.Rendered` 再核验命中，避免合成线程仍使用上一布局的变换读回；`Processed` 或固定 RAF 数不代表本批已经渲染，不通过重试到命中成功来放宽断言。

## 原稿独立书斋专项（InkStudy）

- 入口：已构建的 `TranscriptChatRegression.dll --ink-study` → `InkStudyRegression.VerifyAsync`；40 秒专项预算、45 秒总 deadline。本次仅编码，未构建/执行。真实主窗先 Show 再切 InkStudy，显式外观文件限定本次 `artifacts/ink-study-validation/<时间戳-随机后缀>/appearance.json`；公开源 fake 提供配置/设备/长洞察 Markdown/计数器命令，不构造主 VM、不读真实配置或服务、不访问 AppData/音频/云端。
- 覆盖 partial 同卡、final→History 去重、未知来源中性、释放后不更新、本地模式/字号隔离；以 `CompositionBatch.Rendered` 同步三档窗口布局、三分区安全区/洞察视口/语言组/来源卡及主按钮。最小 1000×550 保留 704 高画布并外层滚动；只按 live/主题，核验菜单隔离保存、旧页缓存/资源恢复与 Application Dark 不变。
- 成功输出 `PASS`、`ARTIFACTS`、`INK_STUDY_EXIT=0`；`finally` 关闭并恢复主题、写 `result.txt`。运行时保存 `ink-study-default.png`、`ink-study-minimum.png` 供主 agent 与原稿比较；不审查生成资产、不做 OCR/总览，不冒充真实音频/服务或物理输入验收。严格只写本次 artifacts 时须使用生产 Release 产物：生产 Debug Markdown 渲染器可能另写输出目录的 `scroll_diag.log`，本专项不修改该生产日志实现。