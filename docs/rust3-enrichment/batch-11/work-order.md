# 批次 11 施工单
> 日期：2026-04-29 | Phase 3 — 听析中心 + 批处理 | 进度 11/21 | 依赖：batch 10 ✅

## 目标
实现批处理包（Package）生命周期状态机、队列运行器、字幕→复盘链式触发逻辑，使音频文件可被批量处理为字幕+AI复盘。

## Spec 来源
| 文档 | 相关段落 |
|------|---------|
| .exchange/docs/ViewModels/BatchProcessingViewModel.md | § 状态与数据 (核心集合+状态), § 流程1 (全量启动), § 流程2 (队列运行器), § 流程3 (处理单个队列项), § 流程4 (包管理操作), § 流程6 (包投影刷新) |
| .exchange/docs/Models/BatchProcessing.md | § BatchTaskItem (字段), § BatchQueueItem (字段+类型枚举), § BatchPackageItem (字段+状态), § BatchSubtaskItem (字段), § BatchBucketNavItem (字段) |

## Rust3 现状
- **已有**: task_engine 调度循环（并发控制+超时+重试+billing），audio_task_queue 表，ReviewSheetPreset 模型，batch_tasks 表（简单结构不匹配 Spec）
- **缺失**: 无 BatchPackage/BatchQueueItem 模型，无批处理协调器，无包状态机，无链式触发，无桶投影，无批处理命令

## 运行时假设
- 已有 AI completion provider 可执行复盘生成（batch-4 验证）
- 已有转录能力（STT provider 在 batch-7 验证）
- 自测方法：cargo test -p tfp-engine -p tfp-core -p tfp-storage 全绿 + 新 batch 状态机单元测试

## 任务清单

- [ ] T-001: 批处理模型定义 [Spec: BatchProcessing.md § 全文]
  - 位置: 新建 @Rust3/crates/tfp-core/src/models/batch.rs + 注册到 mod.rs
  - 契约:
    `ust
    pub enum BatchPackageState { Pending, Running, Partial, Completed, Failed, Removed }
    pub enum BatchQueueItemType { SpeechSubtitle, ReviewSheet }
    pub enum BatchQueueItemStatus { Pending, Running, Responding, Completed, Failed, Paused }

    pub struct BatchPackage {
        pub id: String,
        pub session_id: String,         // 关联 studio_session
        pub audio_file_id: String,
        pub display_name: String,
        pub state: BatchPackageState,
        pub is_paused: bool,
        pub is_removed: bool,
        pub total_count: i32,
        pub completed_count: i32,
        pub failed_count: i32,
        pub progress: f64,
        pub created_at: String,
        pub updated_at: String,
    }

    pub struct BatchQueueItem {
        pub id: String,
        pub package_id: String,
        pub queue_type: BatchQueueItemType,
        pub file_name: String,
        pub full_path: String,
        pub sheet_name: String,         // 复盘分类名
        pub sheet_tag: String,          // 文件标签
        pub prompt: String,
        pub status: BatchQueueItemStatus,
        pub progress: f64,
        pub status_message: String,
        pub error: Option<String>,
        pub created_at: String,
        pub updated_at: String,
    }

    pub struct BatchSubtaskView {
        pub title: String,
        pub tag: String,
        pub state: BatchPackageState,
        pub status_text: String,
        pub progress: f64,
        pub is_speech_subtask: bool,
    }

    pub struct BatchBucketNav {
        pub key: String,
        pub title: String,
        pub count: i32,
    }
    `
  - 测试: #[test] batch_package_state_serde_roundtrip, #[test] queue_item_type_serde

- [ ] T-002: 批处理存储层 — 迁移 + CRUD [Spec: BatchProcessing.md § 字段]
  - 位置: 新建 @Rust3/crates/tfp-storage/src/migrations/v8.sql + 新建 @Rust3/crates/tfp-storage/src/batch_repo/mod.rs
  - 契约:
    `ust
    impl Database {
        pub async fn batch_create_package(&self, pkg: &BatchPackage) -> Result<()>
        pub async fn batch_update_package_state(&self, id: &str, state: &str, is_paused: bool, is_removed: bool) -> Result<()>
        pub async fn batch_update_package_counts(&self, id: &str, total: i32, completed: i32, failed: i32) -> Result<()>
        pub async fn batch_list_packages_by_state(&self, state: &str) -> Result<Vec<BatchPackage>>
        pub async fn batch_get_package(&self, id: &str) -> Result<Option<BatchPackage>>
        pub async fn batch_delete_package(&self, id: &str) -> Result<()>
        pub async fn batch_create_queue_item(&self, item: &BatchQueueItem) -> Result<()>
        pub async fn batch_update_queue_status(&self, id: &str, status: &str, error: Option<&str>) -> Result<()>
        pub async fn batch_get_pending_items(&self, package_id: &str) -> Result<Vec<BatchQueueItem>>
        pub async fn batch_get_items_by_package(&self, package_id: &str) -> Result<Vec<BatchQueueItem>>
        pub async fn batch_count_by_status(&self, package_id: &str) -> Result<(i32, i32, i32)> // (completed, failed, pending)
    }
    `
  - 逻辑:
    1. v8.sql: CREATE TABLE batch_packages (...), CREATE TABLE batch_queue_items (...)
    2. mod.rs: 全部方法实现
    3. db.rs 中注册迁移 v8
  - 测试: #[test] batch_package_roundtrip (内存 DB 测试)

- [ ] T-003: 批处理协调器 — 包创建 + 入队逻辑 [Spec: BatchProcessingViewModel.md § 流程1]
  - 位置: 新建 @Rust3/crates/tfp-engine/src/batch_coordinator.rs
  - 契约:
    `ust
    pub struct BatchCoordinator { /* ... */ }

    impl BatchCoordinator {
        pub async fn create_package(
            db: &Database,
            session_id: &str,
            audio_file_id: &str,
            display_name: &str,
            review_sheets: &[ReviewSheetPreset],
            include_subtitle: bool,
        ) -> Result<BatchPackage, String>

        pub async fn start_batch(
            db: &Database,
            engine: &TaskEngine,
            packages: &[String],  // package_ids
            review_sheets: &[ReviewSheetPreset],
            include_subtitle: bool,
        ) -> Result<u32, String>  // count of queued items
    }
    `
  - 逻辑:
    1. create_package: 创建 BatchPackage，为每个启用的 review_sheet 创建 BatchQueueItem（type=ReviewSheet），若 include_subtitle 则创建一个 SpeechSubtitle 类型的 QueueItem
    2. start_batch: 对每个 package 遍历其 Pending QueueItems → 创建 AudioTaskRow 写入 audio_task_queue → kick engine → 更新 package state 为 Running
  - 测试: #[test] create_package_generates_items, #[test] start_batch_queues_tasks

- [ ] T-004: 链式触发 — 字幕完成后入队复盘 [Spec: BatchProcessingViewModel.md § 流程3 步骤2 末尾]
  - 位置: @Rust3/crates/tfp-engine/src/batch_coordinator.rs (新方法)
  - 契约:
    `ust
    impl BatchCoordinator {
        pub async fn on_subtitle_completed(
            db: &Database,
            engine: &TaskEngine,
            package_id: &str,
        ) -> Result<u32, String>  // newly enqueued review items
    }
    `
  - 逻辑:
    1. 找到 package 下所有 type=ReviewSheet 且 status=Pending 的 QueueItems
    2. 对每个 item 创建 AudioTaskRow (task_type="AiCompletion", prompt_text=item.prompt)
    3. 更新 QueueItem status 为 Running
    4. Kick engine
    5. 返回入队数量
  - 在 task_engine.rs 的完成路径中，检查已完成 task 是否为 batch subtitle → 调用 on_subtitle_completed
  - 测试: #[test] chain_trigger_after_subtitle

- [ ] T-005: 包状态机 — 状态转换 + 包管理操作 [Spec: BatchProcessingViewModel.md § 流程4 + § 包状态计算逻辑]
  - 位置: @Rust3/crates/tfp-engine/src/batch_coordinator.rs (新方法)
  - 契约:
    `ust
    impl BatchCoordinator {
        pub async fn pause_package(db: &Database, package_id: &str) -> Result<(), String>
        pub async fn resume_package(db: &Database, engine: &TaskEngine, package_id: &str) -> Result<(), String>
        pub async fn remove_package(db: &Database, package_id: &str) -> Result<(), String>
        pub async fn restore_package(db: &Database, package_id: &str) -> Result<(), String>
        pub async fn recompute_package_state(db: &Database, package_id: &str) -> Result<BatchPackageState, String>
    }
    `
  - 逻辑:
    1. pause: 标记 is_paused=true，将 Running items 设为 Paused
    2. esume: 取消暂停，Paused items 恢复为 Pending，kick engine
    3. emove: 标记 is_removed=true，state=Removed
    4. estore: 取消 is_removed，重新计算状态
    5. ecompute: 统计 completed/failed/active counts → 按优先级判定状态（Spec §包状态计算逻辑）
  - 测试: #[test] pause_sets_items_paused, #[test] recompute_state_all_completed, #[test] recompute_state_partial

- [ ] T-006: 桶投影 — 按状态分组返回 [Spec: BatchProcessingViewModel.md § 流程6]
  - 位置: @Rust3/crates/tfp-engine/src/batch_coordinator.rs (新方法)
  - 契约:
    `ust
    impl BatchCoordinator {
        pub async fn get_bucket_nav(db: &Database) -> Result<Vec<BatchBucketNav>, String>
        pub async fn get_packages_for_bucket(db: &Database, bucket_key: &str) -> Result<Vec<BatchPackage>, String>
        pub async fn get_subtasks_for_package(db: &Database, package_id: &str) -> Result<Vec<BatchSubtaskView>, String>
    }
    `
  - 逻辑:
    1. get_bucket_nav: 统计 5 个桶的包数量 (pending, running, completed, failed, removed)
    2. get_packages_for_bucket: 按 state 查询
    3. get_subtasks_for_package: 将 QueueItems 转为 BatchSubtaskView
  - 测试: #[test] bucket_nav_counts

- [ ] T-007: Tauri 命令层 [Spec: BatchProcessingViewModel.md § 命令/操作清单]
  - 位置: 新建 @Rust3/src-tauri/src/commands/batch.rs + 注册到 mod.rs + lib.rs
  - 契约: 12 个 #[tauri::command] 函数:
    `ust
    pub async fn batch_create_package(state, session_id, audio_file_id, display_name, include_subtitle) -> Result<BatchPackage, String>
    pub async fn batch_start(state, package_ids, include_subtitle) -> Result<u32, String>
    pub async fn batch_stop(state, package_ids) -> Result<(), String>
    pub async fn batch_pause_package(state, package_id) -> Result<(), String>
    pub async fn batch_resume_package(state, package_id) -> Result<(), String>
    pub async fn batch_remove_package(state, package_id) -> Result<(), String>
    pub async fn batch_restore_package(state, package_id) -> Result<(), String>
    pub async fn batch_get_bucket_nav(state) -> Result<Vec<BatchBucketNav>, String>
    pub async fn batch_get_packages(state, bucket_key) -> Result<Vec<BatchPackage>, String>
    pub async fn batch_get_subtasks(state, package_id) -> Result<Vec<BatchSubtaskView>, String>
    pub async fn batch_regenerate_package(state, package_id) -> Result<(), String>
    pub async fn batch_regenerate_subtask(state, queue_item_id) -> Result<(), String>
    `
  - 逻辑: 薄壳委托给 BatchCoordinator
  - 测试: 无（命令层为薄壳，coordinator 已测）

- [ ] T-008: task_engine 集成链式触发 [Spec: BatchProcessingViewModel.md § 流程3 步骤2]
  - 位置: @Rust3/crates/tfp-engine/src/task_engine.rs (完成路径中增加链式检查)
  - 契约: 在 task 完成 Ok 路径中，若 task_type 含 "batch_subtitle" → 调用 BatchCoordinator::on_subtitle_completed()
  - 逻辑:
    1. 从 prompt_text 中解析 atch_package_id=XXX
    2. 若有 → 调用 on_subtitle_completed(db, engine_handle, package_id)
    3. 调用 recompute_package_state(db, package_id)
    4. Emit "batch-package-update" 事件
  - 测试: #[test] parse_batch_package_id_from_prompt

## 退出标准
- cargo check -p tfp-core -p tfp-storage -p tfp-engine -p truefluent-pro-r3 0 errors 0 warnings
- cargo test -p tfp-core 全绿
- cargo test -p tfp-storage 全绿
- cargo test -p tfp-engine 全绿（含新增 batch 状态机测试）
- 新增测试 ≥ 12 个
- 调用链完整：batch_create_package → batch_start → task_engine 执行 → chain trigger → recompute_state
- 备注：本 batch 完成后 batch-12（批处理前端 + Blob/Batch API）的包管理后端已就绪
