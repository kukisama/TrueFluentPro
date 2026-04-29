//! Batch processing coordinator — package lifecycle, queue management, chain triggers.

use tfp_core::{
    AudioTaskRow, BatchBucketNav, BatchPackage, BatchPackageState, BatchQueueItem,
    BatchQueueItemStatus, BatchQueueItemType, BatchSubtaskView, ReviewSheetPreset,
};
use tfp_storage::Database;

use crate::TaskEngine;

/// Batch processing coordinator handles package creation, queue management,
/// state transitions, chain triggers, and bucket projections.
pub struct BatchCoordinator;

impl BatchCoordinator {
    /// Create a new batch package for an audio file, generating queue items
    /// for each enabled review sheet and optionally a subtitle item.
    pub async fn create_package(
        db: &Database,
        session_id: &str,
        audio_file_id: &str,
        display_name: &str,
        review_sheets: &[ReviewSheetPreset],
        include_subtitle: bool,
    ) -> Result<BatchPackage, String> {
        let now = chrono::Utc::now().to_rfc3339();
        let pkg_id = uuid::Uuid::new_v4().to_string();

        let mut total = 0i32;

        // Create subtitle queue item if requested
        if include_subtitle {
            let item = BatchQueueItem {
                id: uuid::Uuid::new_v4().to_string(),
                package_id: pkg_id.clone(),
                queue_type: BatchQueueItemType::SpeechSubtitle,
                file_name: display_name.to_string(),
                full_path: String::new(),
                sheet_name: "字幕转录".to_string(),
                sheet_tag: "subtitle".to_string(),
                prompt: String::new(),
                status: BatchQueueItemStatus::Pending,
                progress: 0.0,
                status_message: String::new(),
                error: None,
                created_at: now.clone(),
                updated_at: now.clone(),
            };
            db.batch_create_queue_item(&item)
                .await
                .map_err(|e| format!("Failed to create subtitle queue item: {e}"))?;
            total += 1;
        }

        // Create review sheet queue items for each enabled sheet
        for sheet in review_sheets {
            if !sheet.is_enabled || !sheet.include_in_batch {
                continue;
            }
            let item = BatchQueueItem {
                id: uuid::Uuid::new_v4().to_string(),
                package_id: pkg_id.clone(),
                queue_type: BatchQueueItemType::ReviewSheet,
                file_name: display_name.to_string(),
                full_path: String::new(),
                sheet_name: sheet.name.clone(),
                sheet_tag: sheet.file_tag.clone(),
                prompt: sheet.prompt.clone(),
                status: BatchQueueItemStatus::Pending,
                progress: 0.0,
                status_message: String::new(),
                error: None,
                created_at: now.clone(),
                updated_at: now.clone(),
            };
            db.batch_create_queue_item(&item)
                .await
                .map_err(|e| format!("Failed to create review queue item: {e}"))?;
            total += 1;
        }

        let pkg = BatchPackage {
            id: pkg_id,
            session_id: session_id.to_string(),
            audio_file_id: audio_file_id.to_string(),
            display_name: display_name.to_string(),
            state: BatchPackageState::Pending,
            is_paused: false,
            is_removed: false,
            total_count: total,
            completed_count: 0,
            failed_count: 0,
            progress: 0.0,
            created_at: now.clone(),
            updated_at: now,
        };
        db.batch_create_package(&pkg)
            .await
            .map_err(|e| format!("Failed to create package: {e}"))?;

        Ok(pkg)
    }

    /// Start batch processing: for each package, enqueue subtitle tasks into the task engine.
    /// ReviewSheet items will be triggered after subtitle completes (chain trigger).
    pub async fn start_batch(
        db: &Database,
        engine: &TaskEngine,
        package_ids: &[String],
        _review_sheets: &[ReviewSheetPreset],
        include_subtitle: bool,
    ) -> Result<u32, String> {
        let mut queued_count = 0u32;
        let now = chrono::Utc::now().to_rfc3339();

        for pkg_id in package_ids {
            let items = db
                .batch_get_pending_items(pkg_id)
                .await
                .map_err(|e| format!("Failed to get pending items: {e}"))?;

            for item in &items {
                // For subtitle items, enqueue transcription task immediately
                // For review items, only enqueue if no subtitle is included
                // (otherwise chain trigger handles them after subtitle completes)
                let should_enqueue = match item.queue_type {
                    BatchQueueItemType::SpeechSubtitle => true,
                    BatchQueueItemType::ReviewSheet => !include_subtitle,
                };

                if should_enqueue {
                    let task_type = match item.queue_type {
                        BatchQueueItemType::SpeechSubtitle => "Transcription",
                        BatchQueueItemType::ReviewSheet => "AiCompletion",
                    };

                    let prompt = format!(
                        "batch_package_id={};batch_queue_item_id={}{}",
                        pkg_id,
                        item.id,
                        if !item.prompt.is_empty() {
                            format!(";prompt={}", item.prompt)
                        } else {
                            String::new()
                        }
                    );

                    let task = AudioTaskRow {
                        id: uuid::Uuid::new_v4().to_string(),
                        audio_item_id: item.file_name.clone(),
                        stage: format!("batch_{}", item.queue_type.as_str()),
                        task_type: task_type.to_string(),
                        status: "Queued".to_string(),
                        priority: 5,
                        retry_count: 0,
                        max_retries: 2,
                        progress: 0.0,
                        prompt_text: Some(prompt),
                        result_text: None,
                        error: None,
                        submitted_at: now.clone(),
                        started_at: None,
                        completed_at: None,
                    };
                    db.submit_task(&task)
                        .await
                        .map_err(|e| format!("Failed to submit task: {e}"))?;

                    // Mark queue item as running
                    db.batch_update_queue_status(&item.id, "running", None)
                        .await
                        .map_err(|e| format!("Failed to update queue status: {e}"))?;

                    queued_count += 1;
                }
            }

            // Update package state to Running
            db.batch_update_package_state(pkg_id, "running", false, false)
                .await
                .map_err(|e| format!("Failed to update package state: {e}"))?;
        }

        // Kick the engine
        engine.kick().await;

        Ok(queued_count)
    }

    // ── T-004: Chain trigger ──

    /// Called when a subtitle task completes: enqueue all pending ReviewSheet items.
    pub async fn on_subtitle_completed(
        db: &Database,
        engine: &TaskEngine,
        package_id: &str,
    ) -> Result<u32, String> {
        let items = db
            .batch_get_items_by_type_and_status(package_id, "review_sheet", "pending")
            .await
            .map_err(|e| format!("Failed to get review items: {e}"))?;

        let now = chrono::Utc::now().to_rfc3339();
        let mut enqueued = 0u32;

        for item in &items {
            let prompt = format!(
                "batch_package_id={};batch_queue_item_id={}{}",
                package_id,
                item.id,
                if !item.prompt.is_empty() {
                    format!(";prompt={}", item.prompt)
                } else {
                    String::new()
                }
            );

            let task = AudioTaskRow {
                id: uuid::Uuid::new_v4().to_string(),
                audio_item_id: item.file_name.clone(),
                stage: "batch_review_sheet".to_string(),
                task_type: "AiCompletion".to_string(),
                status: "Queued".to_string(),
                priority: 5,
                retry_count: 0,
                max_retries: 2,
                progress: 0.0,
                prompt_text: Some(prompt),
                result_text: None,
                error: None,
                submitted_at: now.clone(),
                started_at: None,
                completed_at: None,
            };
            db.submit_task(&task)
                .await
                .map_err(|e| format!("Failed to submit review task: {e}"))?;

            db.batch_update_queue_status(&item.id, "running", None)
                .await
                .map_err(|e| format!("Failed to update queue status: {e}"))?;

            enqueued += 1;
        }

        if enqueued > 0 {
            engine.kick().await;
        }

        Ok(enqueued)
    }

    // ── T-005: Package state machine ──

    /// Pause a package: mark is_paused and pause running items.
    pub async fn pause_package(db: &Database, package_id: &str) -> Result<(), String> {
        db.batch_pause_running_items(package_id)
            .await
            .map_err(|e| format!("Failed to pause items: {e}"))?;
        db.batch_update_package_state(package_id, "pending", true, false)
            .await
            .map_err(|e| format!("Failed to update package: {e}"))?;
        Ok(())
    }

    /// Resume a paused package.
    pub async fn resume_package(
        db: &Database,
        engine: &TaskEngine,
        package_id: &str,
    ) -> Result<(), String> {
        db.batch_resume_paused_items(package_id)
            .await
            .map_err(|e| format!("Failed to resume items: {e}"))?;
        db.batch_update_package_state(package_id, "running", false, false)
            .await
            .map_err(|e| format!("Failed to update package: {e}"))?;
        engine.kick().await;
        Ok(())
    }

    /// Soft-remove a package.
    pub async fn remove_package(db: &Database, package_id: &str) -> Result<(), String> {
        db.batch_pause_running_items(package_id)
            .await
            .map_err(|e| format!("Failed to pause items: {e}"))?;
        db.batch_update_package_state(package_id, "removed", false, true)
            .await
            .map_err(|e| format!("Failed to update package: {e}"))?;
        Ok(())
    }

    /// Restore a removed package. Recomputes state after restore.
    pub async fn restore_package(db: &Database, package_id: &str) -> Result<(), String> {
        db.batch_update_package_state(package_id, "pending", false, false)
            .await
            .map_err(|e| format!("Failed to update package: {e}"))?;
        let _ = Self::recompute_package_state(db, package_id).await?;
        Ok(())
    }

    /// Recompute package state from item counts.
    pub async fn recompute_package_state(
        db: &Database,
        package_id: &str,
    ) -> Result<BatchPackageState, String> {
        let (completed, failed, pending) = db
            .batch_count_by_status(package_id)
            .await
            .map_err(|e| format!("Failed to count items: {e}"))?;

        let pkg = db
            .batch_get_package(package_id)
            .await
            .map_err(|e| format!("Failed to get package: {e}"))?
            .ok_or_else(|| "Package not found".to_string())?;

        if pkg.is_removed {
            return Ok(BatchPackageState::Removed);
        }

        let total = pkg.total_count;
        let running_or_responding = total - completed - failed - pending;

        let new_state = if completed == total && total > 0 {
            BatchPackageState::Completed
        } else if failed > 0 && pending == 0 && running_or_responding <= 0 {
            // All done but some failed
            if completed > 0 {
                BatchPackageState::Partial
            } else {
                BatchPackageState::Failed
            }
        } else if completed > 0 || failed > 0 || running_or_responding > 0 {
            BatchPackageState::Running
        } else {
            BatchPackageState::Pending
        };

        // Persist
        db.batch_update_package_state(package_id, new_state.as_str(), pkg.is_paused, pkg.is_removed)
            .await
            .map_err(|e| format!("Failed to update state: {e}"))?;
        db.batch_update_package_counts(package_id, total, completed, failed)
            .await
            .map_err(|e| format!("Failed to update counts: {e}"))?;

        Ok(new_state)
    }

    // ── T-006: Bucket projection ──

    /// Get bucket navigation showing count per state.
    pub async fn get_bucket_nav(db: &Database) -> Result<Vec<BatchBucketNav>, String> {
        let counts = db
            .batch_count_packages_by_state()
            .await
            .map_err(|e| format!("Failed to count packages: {e}"))?;

        let buckets = ["pending", "running", "completed", "failed", "removed"];
        let titles = ["待处理", "进行中", "已完成", "失败", "已移除"];

        let nav: Vec<BatchBucketNav> = buckets
            .iter()
            .zip(titles.iter())
            .map(|(key, title)| {
                let count = counts
                    .iter()
                    .find(|(s, _)| s == *key)
                    .map(|(_, c)| *c)
                    .unwrap_or(0);
                BatchBucketNav {
                    key: key.to_string(),
                    title: title.to_string(),
                    count,
                }
            })
            .collect();

        Ok(nav)
    }

    /// Get packages for a specific bucket (state).
    pub async fn get_packages_for_bucket(
        db: &Database,
        bucket_key: &str,
    ) -> Result<Vec<BatchPackage>, String> {
        db.batch_list_packages_by_state(bucket_key)
            .await
            .map_err(|e| format!("Failed to list packages: {e}"))
    }

    /// Get subtask views for a package.
    pub async fn get_subtasks_for_package(
        db: &Database,
        package_id: &str,
    ) -> Result<Vec<BatchSubtaskView>, String> {
        let items = db
            .batch_get_items_by_package(package_id)
            .await
            .map_err(|e| format!("Failed to get items: {e}"))?;

        let views: Vec<BatchSubtaskView> = items
            .iter()
            .map(|item| {
                let state = match item.status {
                    BatchQueueItemStatus::Completed => BatchPackageState::Completed,
                    BatchQueueItemStatus::Failed => BatchPackageState::Failed,
                    BatchQueueItemStatus::Running | BatchQueueItemStatus::Responding => {
                        BatchPackageState::Running
                    }
                    BatchQueueItemStatus::Paused => BatchPackageState::Pending,
                    BatchQueueItemStatus::Pending => BatchPackageState::Pending,
                };
                let status_text = match item.status {
                    BatchQueueItemStatus::Pending => "待处理".to_string(),
                    BatchQueueItemStatus::Running => "处理中".to_string(),
                    BatchQueueItemStatus::Responding => "生成中".to_string(),
                    BatchQueueItemStatus::Completed => "已完成".to_string(),
                    BatchQueueItemStatus::Failed => {
                        item.error.clone().unwrap_or_else(|| "失败".to_string())
                    }
                    BatchQueueItemStatus::Paused => "已暂停".to_string(),
                };
                BatchSubtaskView {
                    title: item.sheet_name.clone(),
                    tag: item.sheet_tag.clone(),
                    state,
                    status_text,
                    progress: item.progress,
                    is_speech_subtask: item.queue_type == BatchQueueItemType::SpeechSubtitle,
                }
            })
            .collect();

        Ok(views)
    }

    // ── Regenerate helpers ──

    /// Regenerate all failed items in a package.
    pub async fn regenerate_package(
        db: &Database,
        engine: &TaskEngine,
        package_id: &str,
    ) -> Result<(), String> {
        let items = db
            .batch_get_items_by_package(package_id)
            .await
            .map_err(|e| format!("Failed to get items: {e}"))?;

        let now = chrono::Utc::now().to_rfc3339();
        let mut count = 0u32;

        for item in &items {
            if item.status != BatchQueueItemStatus::Failed {
                continue;
            }
            // Reset to pending
            db.batch_update_queue_status(&item.id, "pending", None)
                .await
                .map_err(|e| format!("Failed to reset queue item: {e}"))?;

            // Create a new task
            let task_type = match item.queue_type {
                BatchQueueItemType::SpeechSubtitle => "Transcription",
                BatchQueueItemType::ReviewSheet => "AiCompletion",
            };
            let prompt = format!(
                "batch_package_id={};batch_queue_item_id={}{}",
                package_id,
                item.id,
                if !item.prompt.is_empty() {
                    format!(";prompt={}", item.prompt)
                } else {
                    String::new()
                }
            );
            let task = AudioTaskRow {
                id: uuid::Uuid::new_v4().to_string(),
                audio_item_id: item.file_name.clone(),
                stage: format!("batch_{}", item.queue_type.as_str()),
                task_type: task_type.to_string(),
                status: "Queued".to_string(),
                priority: 5,
                retry_count: 0,
                max_retries: 2,
                progress: 0.0,
                prompt_text: Some(prompt),
                result_text: None,
                error: None,
                submitted_at: now.clone(),
                started_at: None,
                completed_at: None,
            };
            db.submit_task(&task)
                .await
                .map_err(|e| format!("Failed to submit task: {e}"))?;

            db.batch_update_queue_status(&item.id, "running", None)
                .await
                .map_err(|e| format!("Failed to update queue status: {e}"))?;
            count += 1;
        }

        if count > 0 {
            db.batch_update_package_state(package_id, "running", false, false)
                .await
                .map_err(|e| format!("Failed to update package state: {e}"))?;
            engine.kick().await;
        }
        Ok(())
    }

    /// Regenerate a single failed subtask.
    pub async fn regenerate_subtask(
        db: &Database,
        engine: &TaskEngine,
        queue_item_id: &str,
    ) -> Result<(), String> {
        let item = db
            .batch_get_queue_item(queue_item_id)
            .await
            .map_err(|e| format!("Failed to get queue item: {e}"))?
            .ok_or_else(|| "Queue item not found".to_string())?;

        if item.status != BatchQueueItemStatus::Failed {
            return Err("Only failed items can be regenerated".to_string());
        }

        let now = chrono::Utc::now().to_rfc3339();
        db.batch_update_queue_status(queue_item_id, "pending", None)
            .await
            .map_err(|e| format!("Failed to reset item: {e}"))?;

        let task_type = match item.queue_type {
            BatchQueueItemType::SpeechSubtitle => "Transcription",
            BatchQueueItemType::ReviewSheet => "AiCompletion",
        };
        let prompt = format!(
            "batch_package_id={};batch_queue_item_id={}{}",
            item.package_id,
            item.id,
            if !item.prompt.is_empty() {
                format!(";prompt={}", item.prompt)
            } else {
                String::new()
            }
        );
        let task = AudioTaskRow {
            id: uuid::Uuid::new_v4().to_string(),
            audio_item_id: item.file_name.clone(),
            stage: format!("batch_{}", item.queue_type.as_str()),
            task_type: task_type.to_string(),
            status: "Queued".to_string(),
            priority: 5,
            retry_count: 0,
            max_retries: 2,
            progress: 0.0,
            prompt_text: Some(prompt),
            result_text: None,
            error: None,
            submitted_at: now,
            started_at: None,
            completed_at: None,
        };
        db.submit_task(&task)
            .await
            .map_err(|e| format!("Failed to submit task: {e}"))?;

        db.batch_update_queue_status(queue_item_id, "running", None)
            .await
            .map_err(|e| format!("Failed to update status: {e}"))?;

        engine.kick().await;
        Ok(())
    }
}

// ── Helper: parse batch_package_id from prompt_text ──

/// Parse batch_package_id from a task's prompt_text.
pub fn parse_batch_package_id(prompt_text: Option<&str>) -> Option<String> {
    prompt_text?
        .split(';')
        .find_map(|part| part.strip_prefix("batch_package_id="))
        .map(|s| s.to_string())
}

/// Parse batch_queue_item_id from a task's prompt_text.
pub fn parse_batch_queue_item_id(prompt_text: Option<&str>) -> Option<String> {
    prompt_text?
        .split(';')
        .find_map(|part| part.strip_prefix("batch_queue_item_id="))
        .map(|s| s.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_batch_package_id_from_prompt() {
        let prompt = "batch_package_id=pkg-123;batch_queue_item_id=qi-456;prompt=hello";
        assert_eq!(
            parse_batch_package_id(Some(prompt)),
            Some("pkg-123".to_string())
        );
    }

    #[test]
    fn parse_batch_package_id_missing() {
        assert_eq!(parse_batch_package_id(Some("no_batch_here")), None);
        assert_eq!(parse_batch_package_id(None), None);
    }

    #[test]
    fn parse_batch_queue_item_id_from_prompt() {
        let prompt = "batch_package_id=pkg-123;batch_queue_item_id=qi-456";
        assert_eq!(
            parse_batch_queue_item_id(Some(prompt)),
            Some("qi-456".to_string())
        );
    }

    #[tokio::test]
    async fn create_package_generates_items() {
        let db = Database::open_in_memory().unwrap();
        let sheets = vec![
            ReviewSheetPreset {
                name: "重点摘要".into(),
                file_tag: "summary".into(),
                prompt: "请总结以下内容".into(),
                include_in_batch: true,
                is_enabled: true,
            },
            ReviewSheetPreset {
                name: "Disabled".into(),
                file_tag: "x".into(),
                prompt: "skip".into(),
                include_in_batch: true,
                is_enabled: false,
            },
        ];

        let pkg = BatchCoordinator::create_package(
            &db, "sess-1", "af-1", "Test Audio", &sheets, true,
        )
        .await
        .unwrap();

        assert_eq!(pkg.state, BatchPackageState::Pending);
        assert_eq!(pkg.total_count, 2); // 1 subtitle + 1 enabled review sheet

        let items = db.batch_get_items_by_package(&pkg.id).await.unwrap();
        assert_eq!(items.len(), 2);
        assert!(items.iter().any(|i| i.queue_type == BatchQueueItemType::SpeechSubtitle));
        assert!(items.iter().any(|i| i.queue_type == BatchQueueItemType::ReviewSheet));
    }

    #[tokio::test]
    async fn create_package_no_subtitle() {
        let db = Database::open_in_memory().unwrap();
        let sheets = vec![ReviewSheetPreset {
            name: "Sheet1".into(),
            file_tag: "s1".into(),
            prompt: "test".into(),
            include_in_batch: true,
            is_enabled: true,
        }];

        let pkg = BatchCoordinator::create_package(
            &db, "sess-1", "af-1", "Test", &sheets, false,
        )
        .await
        .unwrap();

        assert_eq!(pkg.total_count, 1);
        let items = db.batch_get_items_by_package(&pkg.id).await.unwrap();
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].queue_type, BatchQueueItemType::ReviewSheet);
    }

    #[tokio::test]
    async fn pause_sets_items_paused() {
        let db = Database::open_in_memory().unwrap();
        let sheets = vec![ReviewSheetPreset {
            name: "S".into(),
            file_tag: "s".into(),
            prompt: "p".into(),
            include_in_batch: true,
            is_enabled: true,
        }];

        let pkg = BatchCoordinator::create_package(
            &db, "s", "a", "Test", &sheets, true,
        )
        .await
        .unwrap();

        BatchCoordinator::pause_package(&db, &pkg.id).await.unwrap();

        let loaded = db.batch_get_package(&pkg.id).await.unwrap().unwrap();
        assert!(loaded.is_paused);

        let pending = db.batch_get_pending_items(&pkg.id).await.unwrap();
        assert_eq!(pending.len(), 0); // all paused
    }

    #[tokio::test]
    async fn recompute_state_all_completed() {
        let db = Database::open_in_memory().unwrap();
        let pkg = BatchPackage {
            id: "pkg-rc".into(),
            session_id: "s".into(),
            audio_file_id: "a".into(),
            display_name: "T".into(),
            state: BatchPackageState::Running,
            is_paused: false,
            is_removed: false,
            total_count: 2,
            completed_count: 0,
            failed_count: 0,
            progress: 0.0,
            created_at: "2026-01-01".into(),
            updated_at: "2026-01-01".into(),
        };
        db.batch_create_package(&pkg).await.unwrap();

        for i in 0..2 {
            let item = BatchQueueItem {
                id: format!("qi-rc-{i}"),
                package_id: "pkg-rc".into(),
                queue_type: BatchQueueItemType::ReviewSheet,
                file_name: String::new(),
                full_path: String::new(),
                sheet_name: format!("S{i}"),
                sheet_tag: String::new(),
                prompt: String::new(),
                status: BatchQueueItemStatus::Completed,
                progress: 1.0,
                status_message: String::new(),
                error: None,
                created_at: "2026-01-01".into(),
                updated_at: "2026-01-01".into(),
            };
            db.batch_create_queue_item(&item).await.unwrap();
        }

        let state = BatchCoordinator::recompute_package_state(&db, "pkg-rc")
            .await
            .unwrap();
        assert_eq!(state, BatchPackageState::Completed);
    }

    #[tokio::test]
    async fn recompute_state_partial() {
        let db = Database::open_in_memory().unwrap();
        let pkg = BatchPackage {
            id: "pkg-pa".into(),
            session_id: "s".into(),
            audio_file_id: "a".into(),
            display_name: "T".into(),
            state: BatchPackageState::Running,
            is_paused: false,
            is_removed: false,
            total_count: 3,
            completed_count: 0,
            failed_count: 0,
            progress: 0.0,
            created_at: "2026-01-01".into(),
            updated_at: "2026-01-01".into(),
        };
        db.batch_create_package(&pkg).await.unwrap();

        // 2 completed, 1 failed
        for (i, status) in ["completed", "completed", "failed"].iter().enumerate() {
            let item = BatchQueueItem {
                id: format!("qi-pa-{i}"),
                package_id: "pkg-pa".into(),
                queue_type: BatchQueueItemType::ReviewSheet,
                file_name: String::new(),
                full_path: String::new(),
                sheet_name: format!("S{i}"),
                sheet_tag: String::new(),
                prompt: String::new(),
                status: BatchQueueItemStatus::from_str(status),
                progress: 1.0,
                status_message: String::new(),
                error: None,
                created_at: "2026-01-01".into(),
                updated_at: "2026-01-01".into(),
            };
            db.batch_create_queue_item(&item).await.unwrap();
        }

        let state = BatchCoordinator::recompute_package_state(&db, "pkg-pa")
            .await
            .unwrap();
        assert_eq!(state, BatchPackageState::Partial);
    }

    #[tokio::test]
    async fn bucket_nav_counts() {
        let db = Database::open_in_memory().unwrap();

        // Create packages in different states
        for (i, s) in ["pending", "running", "completed", "completed", "failed"].iter().enumerate() {
            let pkg = BatchPackage {
                id: format!("pkg-bn-{i}"),
                session_id: "s".into(),
                audio_file_id: "a".into(),
                display_name: format!("P{i}"),
                state: BatchPackageState::from_str(s),
                is_paused: false,
                is_removed: false,
                total_count: 1,
                completed_count: 0,
                failed_count: 0,
                progress: 0.0,
                created_at: "2026-01-01".into(),
                updated_at: "2026-01-01".into(),
            };
            db.batch_create_package(&pkg).await.unwrap();
        }

        let nav = BatchCoordinator::get_bucket_nav(&db).await.unwrap();
        assert_eq!(nav.len(), 5);

        let pending = nav.iter().find(|b| b.key == "pending").unwrap();
        assert_eq!(pending.count, 1);

        let running = nav.iter().find(|b| b.key == "running").unwrap();
        assert_eq!(running.count, 1);

        let completed = nav.iter().find(|b| b.key == "completed").unwrap();
        assert_eq!(completed.count, 2);

        let failed = nav.iter().find(|b| b.key == "failed").unwrap();
        assert_eq!(failed.count, 1);

        let removed = nav.iter().find(|b| b.key == "removed").unwrap();
        assert_eq!(removed.count, 0);
    }
}
