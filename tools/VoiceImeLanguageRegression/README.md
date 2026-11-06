# 输入法语言与文本翻译定向回归

独立工程，无 ProjectReference。使用已有 bin/Debug/net10.0 托管依赖，仅编译链接的新配置、翻译适配器、语言 VM 与窗口；不构建主项目、不触发 native/Updater/Sync-Host。

运行入口：`dotnet run --project tools/VoiceImeLanguageRegression/VoiceImeLanguageRegression.csproj`。

覆盖：逐句顺序、原文 partial/空帧/final 内部转发、译文仅成功 final、Stop/Dispose 取消请求 token 与排队句子、不合作的迟到结果丢弃、错误脱敏与空译文不回退、缺模型在 ASR 启动前失败、实际 AiInsightService 本机 HTTP/SSE 调用与 503 对照、请求参数冻结、旧配置默认 false/en-US、保存时读取最新 JSON 且只 patch 三字段/保留未知字段与备份。

ASR 用测试替身。HTTP 仅绑定 127.0.0.1 临时端口、使用测试凭据；不使用真实配置、数据库、音频或云端。窗口源码使用真实 Avalonia API 编译，但该入口不启动 GUI；不据此声称浮条按钮、真实麦克风、AAD 或跨应用 TSF 已验收。

## 给共享宿主的准确契约

- 运行快照继续调用 `VoiceImeHost.CreateRuntimeConfiguration(raw)`，SourceLanguage 来自 VoiceIme.RecognitionLanguage，TargetLanguage 来自 VoiceIme.TargetLanguage（缺失默认 en-US）；原配置不改。
- 创建会话调用 internal `VoiceImeHost.CreateVoiceInputSession(config, recognitionFactory, tokens)`。其中 recognitionFactory 仍是现有 ChatVoiceInputFactory；开关关闭原样返回 ASR，开启才包装 VoiceImeTranslationSession。不要修改聊天听写装配。
- `public VoiceImeTranslationSession(IChatVoiceInputSession recognition, AzureSpeechConfig config, IAzureTokenProviderStore? tokens = null)` 实现原 IChatVoiceInputSession 的三个事件及 StartAsync/StopAsync/DisposeAsync。每实例一轮，不可重启。先选 QuickModelRef，其次 Insight/Summary/Review；首个已配置引用无效会明确失败，不悄悄切其它模型。复用 ModelRuntimeResolver、AiInsightService、AzureTokenProviderStore；不新建凭据配置。
- `internal event Action<string,bool>? SourceRecognized` 原样转发 ASR 快照，先于对应译文入队，含 partial、空帧与 final。仅此链路记录原文用量、隐私授权与无字超时；不能发给 TSF。
- `public TextRecognized` 只输出成功译文，当前仅 final=true，每个非空 ASR final 对应一个译文 final，严格 FIFO；不发送原文 partial、空译文、失败半句或原文回退。同文不同句不会去重。任何翻译失败会终止此轮并丢弃后续队列。
- `Failed` 只给可展示的中文提示，不含上游异常正文/URL/密钥。共享宿主应展示此提示，而不是把文本模型缺失或翻译失败误报成麦克风故障。
- `internal PrewarmAsync(CancellationToken)` 与 `internal Task ResourcesReleased` 转发真实 ASR 生命周期。SharedHost 中三处 `is ChatVoiceInputSession`（预热、预热丢弃释放、正式录音释放）也须识别包装器。Stop/Dispose 立即截止回调，取消翻译，不等待尾句；实际设备释放仍必须等待 ResourcesReleased。
- 独立 Host 不启动 Avalonia/主程序 DI。EndpointProfileCatalogService 使用独立 StandardAssetLoader 读取内置厂商资料包，平台默认策略复用同一读取器，不访问 AvaloniaLocator 私有 API；提前解析并冻结请求 URL，缺路由时在录音前明确失败。不改全局资源加载器、不新建主程序服务容器。

## target/generation 防迟到接线

1. 在 actor 内处理 SourceRecognized：验证 Current(round)，只更新原文用量/LastTextTimestamp；不要走已有 Recognized 插字路径，也不要用原文长度更新译文 boundary。
2. 在每句首个非空 source partial（没有 partial 时为 final）捕获当时的目标身份与 generation；跨目标变化时将该句标记不可投递，不能把整句重绑到新输入框。非空 source final 把这一身份（包括“无目标/已失效”的占位项）放入 FIFO；空白 final 不入队。
3. 译文 TextRecognized 只入 actor，从 FIFO 消费对应身份。即使该句失效也须消费占位，保持后续句子对齐；不以处理译文时的当前目标补绑。
4. 发送前同时检查 round/epoch、捕获的 Peer/Generation、当前 target 与前台资格；沿用 WritePeerAsync 出队再检查。译文是整句译文，不能按原文 `_boundary` 切片。Stop/失败/重启清空当前句状态与目标 FIFO。
5. 语言保存后下次录音必须重读配置；旧 `_prewarmed` 会话已冻结旧语言/开关，需按配置版本失效/丢弃，不能直接拿旧预热对象启动。
6. “言”按钮与 Program 启动分支由主 agent 接：无参构造 `public VoiceImeLanguageWindow()`。窗口显式保存，重读最新 JSON，只写 VoiceIme.RecognitionLanguage/EnableTranslation/TargetLanguage。原设置窗口/主配置页若继续持有旧快照，后续保存也须由其所有者合并最新字段，不能在本窗口保存后再覆盖回旧值。

现有配置服务未提供跨进程事务；本窗口保存前重读、提交前比较并拒绝已观察到的冲突，避免长期旧快照覆盖，但不宣称跨进程并发保存完全原子化。

## 验证边界与依据

VoiceImeHost 的改动由源码与编辑器诊断检查，不包含在独立工程中；完整集成编译留给主 agent，遵守多人编辑时不跑主构建的约定。

- [Task.WaitAsync](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.waitasync?view=net-10.0)：返回代表等待的任务；适配器同时向实际请求传入取消 token，并在输出前再检查，不能仅取消等待。
- [C# ref struct](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct)：ref struct 值不能跨 await 保留，测试先完成文件读取再比较字节。
- [MSB3246](https://learn.microsoft.com/en-us/visualstudio/msbuild/errors/msb3246)：无托管元数据的 DLL 不能作为程序集引用，独立工程只引用需要的托管 DLL。
- [Avalonia 11.3.11 AssetLoader 源码](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/11.3.11/src/Avalonia.Base/Platform/AssetLoader.cs) 通过 AvaloniaLocator 取得 IAssetLoader；[StandardAssetLoader 源码](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/11.3.11/src/Avalonia.Base/Platform/StandardAssetLoader.cs) 可仅读取程序集资源，不要求 Application/窗口。第一次真实文本回环未发出请求，核对无 UI 启动路径与该依赖后补齐；未通过延长超时或修改公共翻译服务掩盖问题。

2026-11-06 18:38:32 最终执行退出 0：顺序、Stop/Dispose、失败/空返回、缺模型、实际 HTTP/SSE、503 对照、冻结请求与三字段保存全部通过；相关生产文件编辑器诊断无错误。未跑主构建、native、部署或真实云端/AAD/TSF/GUI 验收。

## 完整集成复验（2026-11-06）

- 主程序集完整构建已通过。首次完整构建暴露 AvaloniaLocator 私有 API 不在主项目引用面，依据上述官方源码改为厂商资料包直接使用 StandardAssetLoader；不靠放开私有 API 或启动 UI 绕过。
- 独立项目必须在主构建后再构建，才能更新复制的 TrueFluentPro.dll。最终本机 SSE/503 对照与全部定向项退出 0，生产 DLL 与测试副本摘要一致；证据 artifacts/voice-ime-language-usage/language-verified.log 的 AUTHORITATIVE VERIFICATION 段。
- 真实管道 SharedHost 译文 FIFO、句首目标、目标往返 revision、在途取消、迟到结果及原文单次计量通过，见 artifacts/voice-ime-language-usage/shared-verified.log。已完成请求的 deadline 已释放，不要求后来 stop 再取消该已完成请求的 token；仍严格验证在途请求和录音会话取消。
- 两个生产窗口的真实 Show/Rendered、控件路由交互与配置重开通过，见 tools/VoiceImeUiRegression/artifacts/20261106-190856-647-112e2754/HANDOFF.md；不等于物理键鼠、真实云端/麦克风或系统 TSF 端到端验收。