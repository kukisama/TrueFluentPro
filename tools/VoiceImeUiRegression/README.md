# VoiceIme 最小真实桌面验收

只运行本目录 `Run.ps1`。需要已成功构建的 `bin/Debug/net10.0/TrueFluentPro.dll`；不加入 solution，无 ProjectReference，无生产源码链接，不触发主 build/其他专项、Host、系统注册或 UAC。

- 仅构建本项目。直接引用生产 Debug 程序集及现有依赖，复制原 DLL 到独立输出；运行时记录原件/加载副本 SHA256、MVID，并核对 solution 前后摘要。
- 独立 Application 加载生产已编译 App.axaml（资源转移前解除原所有者），按轻量入口顺序加入 FluentTheme。不调用任何生产 Application 的框架完成回调，不初始化主 DI。
- 反射调用两窗 internal 隔离构造：语言 JSON 和用量 SQLite 均在本项目的单次 artifacts 目录内。
- Win32 桌面真正 Show 两窗，等待 CompositionBatch.Rendered。以公开 routed KeyDown(Enter) 触发 Button.OnClick / ToggleButton.Toggle，以方向键驱动 ComboBox.SelectedItem，断言实际控件、生产 TwoWay 绑定和保存重开；不反射调用窗口私有业务方法。
- 用量今日/7/30/返回7天、4个指标、趋势列数、选中视觉语义、明细条数；外部 fixture 新增定稿后先保留旧快照，再由真实刷新 Click 更新。查询前后 DB 字节不变。
- 语言开关启停目标、选择、保存仅 patch 三字段、保留新修改的其它字段及未知字段、备份、全新窗口和 VM 重开读取、重新加载。
- 报表缩至生产最小客户区 760×600，顶部/底部滚动；语言窗禁止缩放，按真实自动高度/最小约束验边界。记录文字实际 TextLayout 和可见裁剪面积，异常单列警告，不将截图文件存在当成视觉通过。

证据：`artifacts/<时间戳-id>/build.log`、`desktop.log`、`assertions.log`、`result.json`、`integrity.json`、`exit.txt`、PNG 与隔离 config/DB。

边界：无物理 SendInput；截图是实际已显示窗口的 RenderTargetBitmap，不是屏幕/DWM 像素。只覆盖本机实际 DPI 与最小窗口尺寸，不验证音频/云、跨应用输入或全部错误态。仅关闭本次构造的窗口，不停止任何用户进程。

API 依据（Avalonia 11.3.11 官方源码）：
- https://github.com/AvaloniaUI/Avalonia/blob/11.3.11/src/Avalonia.Controls/Button.cs — Enter 调用 OnClick 后冒泡 Click。
- https://github.com/AvaloniaUI/Avalonia/blob/11.3.11/src/Avalonia.Controls/Primitives/ToggleButton.cs — OnClick 调 Toggle，使用 SetCurrentValue 保留绑定。
- https://github.com/AvaloniaUI/Avalonia/blob/11.3.11/src/Avalonia.Controls/ComboBox.cs — 关闭下拉时方向键移动选择。
- https://github.com/AvaloniaUI/Avalonia/blob/11.3.11/src/Avalonia.Base/Rendering/Composition/Transport/Batch.cs — Rendered 表示渲染线程已渲染，不仅是 Processed。

宿主装载修正依据：Microsoft Learn 的 [Default probing](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/default-probing) 说明存在 deps.json 时按该清单初始化探测；[MSBuild Reference.Private](https://learn.microsoft.com/en-us/visualstudio/msbuild/common-msbuild-project-items) 对应 Copy Local。首次失败清单只有宿主自身，故本项目必须将二进制 Reference 的 Private 设为 true，不能只当 Content 复制。

SQLite bundle 还须显式引用生产 `SQLitePCLRaw*.dll`：[SqliteConnection 9.0.22 源码](https://raw.githubusercontent.com/dotnet/efcore/v9.0.22/src/Microsoft.Data.Sqlite.Core/SqliteConnection.cs) 在静态构造按名称反射加载 Batteries_V2，非普通静态依赖；[官方说明](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions) 指出 bundle 由 Microsoft.Data.Sqlite 自动初始化。本宿主保留此生产机制，不另设 provider。