# 3.14 变更日志

> 日期：2026-03-04
> 基于 计划3.14.md 的 UI 架构现代化改造实施记录

---

## Phase 0：FluentAvalonia 引入与 UI 骨架替换

- ✅ 安装 `FluentAvaloniaUI` v2.5.0 NuGet 包
- ✅ 在 `App.axaml` 中添加 FluentAvalonia 命名空间声明
- ✅ 验证 FluentAvalonia 与 `Avalonia.Themes.Fluent` 共存无冲突
- ✅ `dotnet build` 编译通过，零错误

## Phase 1：DI 容器引入

- ✅ 引入 `Microsoft.Extensions.DependencyInjection` v10.0.3
- ✅ 引入 `CommunityToolkit.Mvvm` v8.4.0
- ✅ `App.axaml.cs` 中构建 `ServiceCollection`，注册核心服务：
  - `ConfigurationService` (Singleton)
  - `AzureSubscriptionValidator` (Singleton)
  - `MainWindowViewModel` (Singleton)
- ✅ `MainWindowViewModel` 通过构造函数注入 `ConfigurationService` 和 `AzureSubscriptionValidator`
- ✅ `ViewModelBase` 改为继承 `CommunityToolkit.Mvvm.ComponentModel.ObservableObject`
- ✅ `MainWindow.DataContext` 由 `App.axaml.cs` 通过 DI 赋值

## Phase 2：ViewModel 拆分

- ✅ 从 `MainWindowViewModel` 拆分出独立子 ViewModel：
  - `AudioDevicesViewModel` — 设备枚举/选择/增益
  - `ConfigViewModel` — 配置加载/保存 + 订阅验证
  - `FileLibraryViewModel` — 文件库管理
  - `BatchProcessingViewModel` — 批处理队列 + 复盘生成
  - `PlaybackViewModel` — NAudio 播放控制
  - `AiInsightViewModel` — AI 洞察 + 自动循环
- ✅ 主 ViewModel 保留 partial 分区：
  - `MainWindowViewModel.cs` — 核心构造/字段/子VM组装
  - `MainWindowViewModel.Core.cs` — 生命周期/Dispose
  - `MainWindowViewModel.Commands.cs` — ICommand 属性声明
  - `MainWindowViewModel.TranslationAndUi.cs` — 翻译启停 + UI 模式
- ✅ AXAML 绑定路径更新为子 VM 路径（如 `{Binding AudioDevices.XXX}`）
- ✅ 跨 VM 回调通过构造函数委托传递

## Phase 3：视图拆分与导航接管

- ✅ 创建 `Views/LiveTranslationView.axaml` + `.cs` UserControl
  - 提取订阅/输入选择区域
  - 提取实时翻译编辑器区域（原文/译文/双语）
  - 提取历史记录 + AI 洞察 TabControl
- ✅ 创建 `Views/ReviewModeView.axaml` + `.cs` UserControl
  - 提取文件库 + 批处理 TabControl
  - 提取音频播放器 + 字幕列表
  - 提取复盘洞察总结面板
  - 迁移事件处理器（SubtitleCueListBox_DoubleTapped, AudioFileList_PointerPressed, AudioFileEnqueue_Click）
- ✅ 创建 `Views/SettingsView.axaml` + `.cs` UserControl
  - 订阅管理（含配置中心入口）
  - 音频设备设置
  - 外部链接（Azure Speech / Foundry / GitHub）
  - 帮助与关于
- ✅ 重构 `MainWindow.axaml` 为 NavigationView Shell
  - 使用 FluentAvalonia `NavigationView` 替换 RadioButton 模式切换
  - 左侧导航栏：实时翻译 / 复盘批处理 / Media Studio
  - 内置 Settings 齿轮导航至设置页
  - 顶部精简工具栏保留品牌 + 核心操作
  - 底部状态栏不变
- ✅ `MainWindow.axaml.cs` 添加 `NavView_SelectionChanged` 处理器
  - 页面切换通过 Panel + IsVisible 实现（保持状态）
- ✅ `MainWindowViewModel.TranslationAndUi.cs` 添加 `SelectedNavTag` 属性
  - 与 `UiModeIndex` 双向同步（兼容遗留绑定）
- ✅ `dotnet build` 编译通过，零错误，7 个预存警告不变

## Phase 4：Bug 修复与集成优化

### Bug 1：Media Studio 集成到主窗体

- ✅ 创建 `Views/MediaStudioView.axaml` UserControl
  - 从 `MediaStudioWindow.axaml` 提取全部 UI 内容（512 行 XAML）
  - `Window.Styles` → `UserControl.Styles`，`Window.Resources` → `UserControl.Resources`
- ✅ 创建 `Views/MediaStudioView.axaml.cs` 代码后置
  - 从 `MediaStudioWindow.axaml.cs` 适配（1286 行代码后置）
  - 参数化构造函数 → `Initialize(AiConfig, MediaGenConfig)` 延迟初始化
  - `OnClosing()` → `Cleanup()` 公开清理方法
  - `StorageProvider` → `TopLevel.GetTopLevel(this)?.StorageProvider`
  - `ShowDialog(this)` → `ShowDialog(TopLevel.GetTopLevel(this) as Window)`
- ✅ `MainWindow.axaml` Panel 添加 `MediaStudioView`
- ✅ `MainWindow.axaml.cs` ShowPage 支持 "media" tag，延迟初始化 ViewModel
- ✅ `MainWindowViewModel.TranslationAndUi.cs` ShowMediaStudio 置为空操作
- ✅ 保留 `MediaStudioWindow` 原始文件不删除

### Bug 2：订阅/麦克风/系统回环设备持久化

- ✅ `ConfigViewModel` 添加 `_suppressIndexPersistence` 标志
  - `LoadConfigAsync` 加载配置时抑制 `UpdateSubscriptionNames().Clear()` 引起的 -1 回写
  - `HandleExternalConfigUpdate` 同理保护
- ✅ `ActiveSubscriptionIndex` setter 增加抑制分支，加载期间不触发保存/验证
- ✅ `ForceUpdateComboBoxSelection()` 重写
  - 移除 `FindControl<ComboBox>("SubscriptionComboBox")` 旧方式（Phase 3 后控件在 UserControl 内部无法找到）
  - 改为通过 OnPropertyChanged 属性值弹跳强制刷新绑定
- ✅ `AudioDevicesViewModel.ForceUpdateDeviceComboBoxSelection()` 重写
  - 同样移除 `FindControl` 方式
  - 改为通过 `SelectedAudioDevice` / `SelectedOutputDevice` 属性弹跳刷新

### Bug 3：开始翻译按钮灰色

- ✅ `MainWindowViewModel.OnConfigVMConfigLoaded()` 添加
  ```csharp
  ((RelayCommand)StartTranslationCommand).RaiseCanExecuteChanged();
  ((RelayCommand)ToggleTranslationCommand).RaiseCanExecuteChanged();
  ```
  - 根因：`_translationCommandsRefresh()` 在 `ConfigLoaded` 事件之前触发，此时 MainVM._config 尚未更新
- ✅ `OnConfigVMConfigUpdatedFromExternal` 同步添加

### Bug 4：实时翻译区域字号行高度不足

- ✅ `Controls/AdvancedRichTextBox.cs` 工具栏 StackPanel 增加 `MinHeight = 32`
- ✅ 字号 ComboBox 从 `Width=60, Height=25` 调整为 `Width=70, Height=30`

### Bug 5：帮助按钮无效

- ✅ 移除 `MainWindow.axaml` 中 ContextMenu 嵌套的"帮助"按钮
- ✅ 替换为直接的"说明"和"关于"两个独立按钮，分别绑定 `ShowHelpCommand` 和 `ShowAboutCommand`
- ✅ 移除 `HelpButton` 相关样式（`Button#HelpButton` 选择器）
- ✅ 移除 `MainWindow.axaml.cs` 中 `HelpButton_Click` 事件处理器

### Bug 6：配置中心设计

- ✅ 创建 `配置中心3.4设计.md`
  - 现状分析（9 个 TabItem、1,317 行代码后置）
  - 目标设计（SettingsView 分组卡片 + 可展开面板）
  - ViewModel 改造方案（新增 SettingsViewModel）
  - 5 步实施计划
  - 不在本次实施范围

### Bug 7：播放器按钮过小

- ✅ `Views/ReviewModeView.axaml` 播放/暂停按钮从 `Width="34"` 调整为 `Width="40" Height="32" FontSize="16"`

---

## 构建验证

| 阶段 | 结果 |
|------|------|
| `dotnet build` | ✅ 0 Error, 7 Warning (均为预存 nullable 警告) |
| AXAML 解析 | ✅ 所有新增 .axaml 文件通过 Avalonia XAML 编译 |
| 导航流程 | ✅ NavigationView → LiveTranslationView / ReviewModeView / MediaStudioView / SettingsView 切换 |
| Media Studio | ✅ 从独立窗口改为主窗体内嵌页面，延迟初始化 |
| 设备持久化 | ✅ 修复配置加载期间 ComboBox 回写 -1 的竞态问题 |
| 翻译按钮 | ✅ 配置加载完成后正确刷新 CanExecute 状态 |
