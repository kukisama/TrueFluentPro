# VoiceIme 最小定向回归

从仓库根执行 `dotnet build C:\Scripts\TranslationToolUI\tools\VoiceImeRegression\VoiceImeRegression.csproj -v quiet --nologo`，然后直接执行 `C:\Scripts\TranslationToolUI\tools\VoiceImeRegression\bin\Debug\net10.0\VoiceImeRegression.exe`。不加入 solution；引用构建会正常生成主项目 Debug apphost，但不注册输入法。

## 范围

- 配置：真实模型/语音/Realtime resolver + 内存 endpoint/model 注册表；深拷贝、独立选择、ModelRef 本次覆盖、显式资源丢失拒绝、跟随、语言/设备/分句。
- 消息：只构造生产 `ChatVoiceInputFactory(realResolvers, NoToken)`，反射检查 `BuildSessionUpdate`，绝不对生产会话调用 Start。核对 en-US → en、auto 无语言字段、0.8s → 800ms，默认聊天保留 auto、GA semantic 与 Azure preview 平铺 server_vad 协议。
- 管道：每次 GUID 命名的真实 duplex server/client，反射调用生产 `RunSessionAsync`；同步 Start 内 P/F 必须在 R 后，逐字节检查 4byteLE 长度、kind 和中文 emoji UTF-8。stop、连接异常、连接中 Failed 都检查任务和假麦克风释放。已捕获委托在 Stop/Dispose/结束后继续调用，不能继续发送正文或错误。
- 进程：只启动 `bin/Debug/net10.0/TrueFluentPro.exe --voice-ime-host`（故意无效，缺参数），10s 上限、预期退出 2。启动时挂起，先装精确 PID 的 WinEvent 监听再恢复；真实屏外 1px STATIC 窗口作 CREATE/SHOW 观察器对照。不运行合法参数，不读用户配置，不创建 Avalonia/主程序窗口。

所有读取配置均为测试内存对象；不构造真实 TokenProvider，不读用户凭据、不使用麦克风/云，不执行安装/系统注册。假麦克风仅为替身中的资源占用计数。任何失败均返回 1；入口子进程预期返回 2，不是测试程序失败码。日志、JSON 帧、原始十六进制和消息样本写入本目录 `artifacts/<时间-GUID>/`。

任务、连接、管道读写和收尾均有硬超时；消息循环等待原生句柄/事件，不用 Sleep 轮询。若 Host 未自行收尾，测试记录 FAIL 后才外部取消并清理，不把这次强制退出作为 PASS，也不降低断言。

## 依据

- [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)：进程过滤与 OUTOFCONTEXT 消息循环要求。
- [Process Creation Flags](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags)：CREATE_SUSPENDED 在 ResumeThread 前不运行主线程；不能用只针对 console 的 CREATE_NO_WINDOW 来证明没有 GUI。
- [MsgWaitForMultipleObjectsEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-msgwaitformultipleobjectsex)：在原生进程句柄和消息队列之间等待，包含硬期限。
- [Single-Threaded Apartments](https://learn.microsoft.com/en-us/windows/win32/com/single-threaded-apartments)：COM 为 STA 创建隐藏的 `OleMainThreadWndClass`。实测无效参数进程会创建一个此类辅助窗口；仅排除此类、匹配标题且不可见的 CREATE，任何 SHOW、未知类、splash/Avalonia 类创建均失败。

## 已复现生产缺陷（2026-11-02）

`callback-error-connecting`：识别源 Start 尚未完成时触发 Failed，Host 虽已发送脱敏 E 帧，却仍等待 `voice.StartAsync(token)`，没有取消源 token，也没有 Stop/Dispose。4s 硬期限时假麦克风仍占用。测试先记录失败状态，随后外部取消兜底，确认全部任务与假资源均收尾；该用例保留 FAIL，不修改生产代码。

定位：`Services/VoiceImeHost.cs` 的 `OnFailure` 只 Publish E；写循环只完成 `finished`，而主流程尚未越过 Start 到达 `finished` 的等待。主 agent 需修复错误通知与启动/收尾的协调，再用相同断言复验。