# 批次 16 施工单
> 日期：2026-04-29
> 路线图阶段：Phase 4 — 媒体创作坊 + 中心
> 本阶段进度：4/4（P4 最后一轮）

## 目标
为 Center 补齐 Shell 操作（打开文件/资源管理器/工作区目录）、画布单资产预览模式（含 pending/failed 状态覆盖）、结果组显示、分页加载、确认删除对话框、导出增强，使 Center 前端达到全流程可操作的 P4 退出标准。

## Spec 来源
| 文档 | 相关段落 |
|------|---------|
| .exchange/docs/ViewModels/MediaCenterV2ViewModel.md | § 命令/操作（OpenSelectedAssetCommand, OpenSelectedAssetInExplorerCommand, OpenWorkspaceFolderCommand, RefreshWorkspaceCommand）, § 流程4（RebuildResultCollections + ResultGroup 构建）, § 关键算法（工作区分页加载 + 路径转换）, § 内嵌类型（MediaCreatorResultGroup, MediaCreatorResultAsset 属性表） |
| .exchange/docs/Views/MediaCenterV2View.md | § 中央画布预览区（三种状态覆盖：空/pending/failed）, § 底部计数器 + 左右导航箭头, § 右键菜单（打开文件 + 在文件夹中显示）, § 无限加载工作区（ScrollChanged 检测底部 200px）, § Code-behind 逻辑（ConfirmRemoveWorkspaceAsync）|

## Rust3 现状
- **已有**: center_export_assets 命令（平铺复制，无子目录结构）
- **已有**: 前端 export 按钮调用 dialogOpen + centerExportAssets
- **已有**: 前端右键菜单有 preview/download/delete/copyPath/setAsRef/editThisImage
- **已有**: 前端工作区列表一次加载 50 个（无分页）
- **已有**: 前端结果网格展示当前 round assets（无分组聚合）
- **已有**: 前端删除用 browser confirm()
- **缺失**: 无打开文件/资源管理器的 Shell 命令
- **缺失**: 无画布单资产大图预览模式（仅有 lightbox modal）
- **缺失**: 无 pending/failed 状态覆盖层
- **缺失**: 无资产前后导航（←/→）
- **缺失**: 无结果组（ResultGroup）分组展示
- **缺失**: 无工作区分页（load more / infinite scroll）
- **缺失**: 无正式确认删除对话框（用 browser confirm）
- **缺失**: 导出不支持按轮次子目录或附带元数据

## 前置条件
- batch-15 通过（Center 工作区模型扩展、derive_workspace、all_assets、参考图验证）

## 运行时假设
- 目标 API：本地 Tauri Shell 插件 + 本地文件系统
- 认证方式：不涉及
- 参数约束：工作区分页大小 20 条
- **自测方法**：cargo test -p tfp-storage + cargo test --workspace + tsc --noEmit + 手动操作验证

## 任务清单

### T-001: Shell 操作命令（打开文件 / 在资源管理器中打开 / 打开目录）
- Spec 参考: MediaCenterV2ViewModel.md § 命令/操作（OpenSelectedAsset, OpenInExplorer, OpenWorkspaceFolder）
- 现有代码: 新建
- 产出: 修改 src-tauri/src/commands/center.rs + src-tauri/src/lib.rs + src/lib/api.ts
- 契约:
  - Tauri: pub async fn center_open_file(path: String) -> Result<(), String>
  - Tauri: pub async fn center_reveal_in_explorer(path: String) -> Result<(), String>
  - api.ts: centerOpenFile(path: string) => invoke<void>
  - api.ts: centerRevealInExplorer(path: string) => invoke<void>
- 业务逻辑:
  1. center_open_file: 使用 opener::open(path) 或 std::process::Command::new("cmd").args(["/c", "start", "", &path]) 打开文件
  2. center_reveal_in_explorer: Windows 使用 xplorer.exe /select,{path}, macOS 使用 open -R {path}, Linux 使用 xdg-open {parent_dir}
  3. 验证路径存在后再调用（不存在返回错误）
- 注意: 需要在 Tauri capabilities 中添加 shell:allow-open 或使用 tauri-plugin-opener
- 测试: test_center_open_file_nonexistent (单元测试验证路径检查)
- **自测**: cargo test + 运行后手动点击"打开文件"按钮

### T-002: 画布单资产预览模式 + pending/failed 状态覆盖
- Spec 参考: MediaCenterV2View.md § 中央画布预览区（三种状态叠加）
- 现有代码: MediaCenterView.tsx:79 previewAsset 已有 lightbox 但不是内嵌画布
- 产出: 修改 src/views/MediaCenterView.tsx
- 契约: 新增 canvasAsset 状态（当前组选中的资产）+ 三种覆盖层渲染
- 前端逻辑:
  1. 在结果网格上方新增 **画布预览区**（高 240px 的响应式区域）
  2. 显示当前选中资产的大图（click 一个 asset → 变为 canvasAsset）
  3. 当 running_tasks 非空时显示 **Pending 覆盖层**：脉冲动画圆圈 + "Generating..." 文本 + 经过时间
  4. 当无资产且无任务时显示 **空状态**：图标 + 描述文字
  5. 如果选中资产的 round.status === "failed"，显示 **Failed 覆盖层**：红色图标 + 错误消息
  6. 左右箭头按钮导航 canvasAsset（← / → 在当前 round assets 内切换）
  7. 底部显示资产计数器（如 "2 / 5"）
- 测试: tsc --noEmit 通过
- **自测**: 运行后观察生成中/完成/失败三种状态

### T-003: 结果组（ResultGroup）分组展示
- Spec 参考: MediaCenterV2ViewModel.md § 内嵌类型（MediaCreatorResultGroup）+ § 流程4（RebuildResultCollections）
- 现有代码: MediaCenterView.tsx:512-539 仅平铺 currentAssets
- 产出: 修改 src/views/MediaCenterView.tsx
- 契约: 新增 groupedView 模式 — 按 round 分组展示资产
- 前端逻辑:
  1. 新增视图切换按钮（Grid / Grouped）
  2. Grouped 模式：遍历 activeBundle.round_prompts，每组显示 header（prompt_preview + status badge + asset_count + created_at）
  3. 每组下方水平滚动展示该 round 的资产缩略图
  4. 组 header 点击展开/折叠该组
  5. 工作流 badge：根据 workspace.canvas_mode 显示 "Draw"/"Edit" 标签
  6. 组内资产右键菜单复用现有逻辑
- 注意: round_prompts 已在 bundle 中（batch-15 完成），这里只做前端渲染
- 测试: tsc --noEmit 通过

### T-004: 工作区分页加载 + 无限滚动
- Spec 参考: MediaCenterV2ViewModel.md § 关键算法（工作区分页加载，每页 10 条）+ MediaCenterV2View.md § 无限加载工作区（ScrollChanged 200px）
- 现有代码: MediaCenterView.tsx:100 pi.centerListWorkspaces(50, 0) 一次性加载
- 产出: 修改 src/views/MediaCenterView.tsx
- 契约: 分页加载 + IntersectionObserver 触底加载
- 前端逻辑:
  1. 改为每次加载 20 条（PAGE_SIZE = 20）
  2. 记录 offset 状态（useState）
  3. 在工作区列表底部放置 sentinel div + IntersectionObserver
  4. 触底时 offset += PAGE_SIZE，请求下一页并追加
  5. 无更多数据时（返回 < PAGE_SIZE 条）停止监听
  6. hasMore 状态用于隐藏加载指示器
- 后端不需修改（center_list_workspaces 已支持 limit/offset）
- 测试: tsc --noEmit 通过

### T-005: 确认删除对话框
- Spec 参考: MediaCenterV2View.md § Code-behind 逻辑（ConfirmRemoveWorkspaceAsync — 代码构建确认对话框）
- 现有代码: MediaCenterView.tsx:234 使用 confirm() + MediaCenterView.tsx:384 直接调用
- 产出: 修改 src/views/MediaCenterView.tsx
- 契约: 使用正式的 Dialog 组件替代 browser confirm
- 前端逻辑:
  1. 新增 deleteTarget 状态（{type: 'workspace'|'assets', id: string, name?: string, count?: number} | null）
  2. 当 deleteTarget 非空时渲染 Dialog overlay
  3. Dialog 内容：标题 + 描述（"确定移除工作区 '{name}'？文件不会被删除"）+ Cancel/Confirm 按钮
  4. Confirm 后调用 centerSoftDeleteWorkspace / centerDeleteAssets
  5. 统一所有删除入口（侧边栏 X 按钮、批量删除、右键删除）使用此 Dialog
- 测试: tsc --noEmit 通过

### T-006: 导出增强 — 按轮次子目录 + 元数据 JSON
- Spec 参考: MediaCenterV2ViewModel.md § 流程6（路径转换策略 — 资产路径为 workspace-relative）
- 现有代码: commands/center.rs:324-358 center_export_assets（平铺复制）
- 产出: 新增 center_export_workspace 命令 + 修改 center_repo + api.ts
- 契约:
  - Tauri: pub async fn center_export_workspace(state, workspace_id: String, dest_dir: String, include_metadata: bool) -> Result<ExportResult, String>
  - api.ts: centerExportWorkspace(workspaceId: string, destDir: string, includeMetadata: boolean) => invoke<ExportResult>
- 业务逻辑:
  1. 获取 workspace 的所有 rounds
  2. 对每个 round 创建子目录 ound_{index}_{prompt_preview_8chars}/
  3. 复制该 round 的所有资产文件到子目录
  4. 如果 include_metadata = true，为每个 round 写一个 meta.json（prompt, model, params, status, created_at, assets 列表）
  5. 在根目录写 workspace.json（工作区名称、创建时间、画布模式、轮次数、资产总数、溯源信息）
  6. 返回 ExportResult { copied, failed }
- 测试: test_export_workspace_structure, test_export_workspace_with_metadata
- **自测**: cargo test + 手动导出并检查目录结构

### T-007: 右键菜单增强 — 打开文件 + 在文件夹中显示
- Spec 参考: MediaCenterV2View.md § 资产缩略图右键菜单（打开文件 + 在文件夹中显示）
- 现有代码: MediaCenterView.tsx:567-586 contextMenu
- 产出: 修改 src/views/MediaCenterView.tsx
- 前端逻辑:
  1. 在右键菜单中添加 "打开文件"（调用 centerOpenFile）
  2. 添加 "在文件夹中显示"（调用 centerRevealInExplorer）
  3. 这两项放在 preview 之后、download 之前
  4. 使用 Spec 的图标（FileOpen → ExternalLink, FolderOpen → FolderOpen）
- 测试: tsc --noEmit 通过

### T-008: 前端集成微调 + 视图模式切换
- Spec 参考: MediaCenterV2View.md § 标题栏 + 整体布局
- 现有代码: MediaCenterView.tsx 整体
- 产出: 修改 src/views/MediaCenterView.tsx + 可选修改 api.ts
- 前端逻辑:
  1. 在工具栏添加"刷新"按钮（RefreshWorkspaceCommand → 重新加载 bundle + workspace list）
  2. 添加视图模式切换按钮组（Grid/List/Grouped 三种 — Grid 为现有、List 为紧凑列表、Grouped 为 T-003）
  3. 工作区侧边栏底部添加 "导出整个工作区" 按钮（调用 T-006 的 centerExportWorkspace）
  4. 键盘：添加 ←/→ 箭头键用于 canvasAsset 导航（与 T-002 配合）
  5. 将 workspace 加载失败时的 console.error 升级为 toast 通知（或在状态栏显示）
- 测试: tsc --noEmit 通过

## 技术决策记录
| 编号 | 决策 | Spec 依据 | 与 C# 差异 |
|------|------|-----------|-----------|
| D-01 | 使用 std::process::Command 而非 tauri-plugin-shell 打开文件 | 命令/操作 § OpenSelectedAsset | C# 用 Process.Start，Tauri 等价 |
| D-02 | 画布预览内嵌在主区域（非单独窗口） | View § 中央画布预览区 | 与 C# 布局一致 |
| D-03 | 分页 PAGE_SIZE=20（C# 用 10） | 关键算法 § 分页 | Web 端可用更大页面 |
| D-04 | 导出用文件系统操作不用 Blob 存储 | 本地桌面应用 | C# 同样为本地复制 |
| D-05 | confirm→Dialog 组件内联实现 | Code-behind § ConfirmRemove | C# 构建 Window，React 用 overlay |
| D-06 | 3 种视图模式（Grid/List/Grouped） | View § 整体布局 + 结果轨道 | C# 只有单一画布，我们更灵活 |

## 后续影响
- 本批次完成后，P4 Phase 门卫检查将执行
- P5 batch-17（AAD 登录 + 设置系统）将开始
- Center/Studio 前端功能不再是 P5 的工作项

## 禁止事项
- 禁止修改 studio_sessions 表 schema
- 禁止引入新的 SQLite 表
- 禁止在 Shell 命令中使用不安全的路径拼接（必须验证路径存在）
- 禁止修改已有的 center_export_assets 命令签名（新增而非修改）
- 禁止在前端组件中硬编码 i18n 字符串（新增文本一律走 t()）

## 退出标准
- cargo check --workspace: 0 errors 0 warnings（新代码）
- cargo test --workspace: 全绿
- tsc --noEmit: 0 新增 error
- 前端：画布预览可切换（空/pending/完成），←/→ 导航工作，Shell 打开文件工作
- 前端：分组视图可展示 round_prompts，分页加载工作
- 前端：确认删除 Dialog 可弹出，导出工作区可生成子目录结构
- 新增测试 >= 6 个
- P4 门卫检查全项通过
