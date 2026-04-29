use rusqlite::params;
use chrono::Utc;

use crate::db::{Database, map_db_err};
use tfp_core::{
    BatchPackage, BatchPackageState, BatchQueueItem, BatchQueueItemType,
    BatchQueueItemStatus,
};

// ── Row mappers ──

fn map_package(row: &rusqlite::Row) -> rusqlite::Result<BatchPackage> {
    Ok(BatchPackage {
        id: row.get(0)?,
        session_id: row.get(1)?,
        audio_file_id: row.get(2)?,
        display_name: row.get(3)?,
        state: BatchPackageState::from_str(row.get::<_, String>(4)?.as_str()),
        is_paused: row.get::<_, i64>(5)? != 0,
        is_removed: row.get::<_, i64>(6)? != 0,
        total_count: row.get(7)?,
        completed_count: row.get(8)?,
        failed_count: row.get(9)?,
        progress: row.get(10)?,
        created_at: row.get(11)?,
        updated_at: row.get(12)?,
    })
}

fn map_queue_item(row: &rusqlite::Row) -> rusqlite::Result<BatchQueueItem> {
    Ok(BatchQueueItem {
        id: row.get(0)?,
        package_id: row.get(1)?,
        queue_type: BatchQueueItemType::from_str(row.get::<_, String>(2)?.as_str()),
        file_name: row.get(3)?,
        full_path: row.get(4)?,
        sheet_name: row.get(5)?,
        sheet_tag: row.get(6)?,
        prompt: row.get(7)?,
        status: BatchQueueItemStatus::from_str(row.get::<_, String>(8)?.as_str()),
        progress: row.get(9)?,
        status_message: row.get(10)?,
        error: row.get(11)?,
        created_at: row.get(12)?,
        updated_at: row.get(13)?,
    })
}

const PKG_COLS: &str = "id, session_id, audio_file_id, display_name, state, is_paused, is_removed, total_count, completed_count, failed_count, progress, created_at, updated_at";
const ITEM_COLS: &str = "id, package_id, queue_type, file_name, full_path, sheet_name, sheet_tag, prompt, status, progress, status_message, error, created_at, updated_at";

// ── Database impl ──

impl Database {
    /// Insert a new batch package.
    pub async fn batch_create_package(&self, pkg: &BatchPackage) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO batch_packages (id, session_id, audio_file_id, display_name, state, is_paused, is_removed, total_count, completed_count, failed_count, progress, created_at, updated_at)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13)",
            params![
                pkg.id, pkg.session_id, pkg.audio_file_id, pkg.display_name,
                pkg.state.as_str(), pkg.is_paused as i64, pkg.is_removed as i64,
                pkg.total_count, pkg.completed_count, pkg.failed_count, pkg.progress,
                pkg.created_at, pkg.updated_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    /// Update package state, is_paused, and is_removed.
    pub async fn batch_update_package_state(
        &self,
        id: &str,
        state: &str,
        is_paused: bool,
        is_removed: bool,
    ) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE batch_packages SET state=?1, is_paused=?2, is_removed=?3, updated_at=?4 WHERE id=?5",
            params![state, is_paused as i64, is_removed as i64, now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    /// Update package count/progress fields.
    pub async fn batch_update_package_counts(
        &self,
        id: &str,
        total: i32,
        completed: i32,
        failed: i32,
    ) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        let progress = if total > 0 {
            (completed + failed) as f64 / total as f64
        } else {
            0.0
        };
        conn.execute(
            "UPDATE batch_packages SET total_count=?1, completed_count=?2, failed_count=?3, progress=?4, updated_at=?5 WHERE id=?6",
            params![total, completed, failed, progress, now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    /// List packages matching a given state.
    pub async fn batch_list_packages_by_state(&self, state: &str) -> tfp_core::Result<Vec<BatchPackage>> {
        let conn = self.conn().lock().await;
        let sql = format!("SELECT {PKG_COLS} FROM batch_packages WHERE state=?1 ORDER BY created_at DESC");
        let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
        let rows = stmt.query_map(params![state], map_package).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    /// Get a single package by id.
    pub async fn batch_get_package(&self, id: &str) -> tfp_core::Result<Option<BatchPackage>> {
        let conn = self.conn().lock().await;
        let sql = format!("SELECT {PKG_COLS} FROM batch_packages WHERE id=?1");
        let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
        let mut rows = stmt.query_map(params![id], map_package).map_err(map_db_err)?;
        match rows.next() {
            Some(r) => Ok(Some(r.map_err(map_db_err)?)),
            None => Ok(None),
        }
    }

    /// Delete a package and its queue items.
    pub async fn batch_delete_package(&self, id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM batch_queue_items WHERE package_id=?1", params![id])
            .map_err(map_db_err)?;
        conn.execute("DELETE FROM batch_packages WHERE id=?1", params![id])
            .map_err(map_db_err)?;
        Ok(())
    }

    /// Insert a new batch queue item.
    pub async fn batch_create_queue_item(&self, item: &BatchQueueItem) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT INTO batch_queue_items (id, package_id, queue_type, file_name, full_path, sheet_name, sheet_tag, prompt, status, progress, status_message, error, created_at, updated_at)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14)",
            params![
                item.id, item.package_id, item.queue_type.as_str(),
                item.file_name, item.full_path, item.sheet_name, item.sheet_tag,
                item.prompt, item.status.as_str(), item.progress, item.status_message,
                item.error, item.created_at, item.updated_at,
            ],
        ).map_err(map_db_err)?;
        Ok(())
    }

    /// Update a queue item's status and error.
    pub async fn batch_update_queue_status(
        &self,
        id: &str,
        status: &str,
        error: Option<&str>,
    ) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE batch_queue_items SET status=?1, error=?2, updated_at=?3 WHERE id=?4",
            params![status, error, now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    /// Get all pending items for a package.
    pub async fn batch_get_pending_items(&self, package_id: &str) -> tfp_core::Result<Vec<BatchQueueItem>> {
        let conn = self.conn().lock().await;
        let sql = format!("SELECT {ITEM_COLS} FROM batch_queue_items WHERE package_id=?1 AND status='pending' ORDER BY created_at ASC");
        let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
        let rows = stmt.query_map(params![package_id], map_queue_item).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    /// Get all items for a package.
    pub async fn batch_get_items_by_package(&self, package_id: &str) -> tfp_core::Result<Vec<BatchQueueItem>> {
        let conn = self.conn().lock().await;
        let sql = format!("SELECT {ITEM_COLS} FROM batch_queue_items WHERE package_id=?1 ORDER BY created_at ASC");
        let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
        let rows = stmt.query_map(params![package_id], map_queue_item).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    /// Count items by status for a package: (completed, failed, pending).
    pub async fn batch_count_by_status(&self, package_id: &str) -> tfp_core::Result<(i32, i32, i32)> {
        let conn = self.conn().lock().await;
        let completed: i32 = conn
            .query_row(
                "SELECT COUNT(*) FROM batch_queue_items WHERE package_id=?1 AND status='completed'",
                params![package_id],
                |r| r.get(0),
            )
            .map_err(map_db_err)?;
        let failed: i32 = conn
            .query_row(
                "SELECT COUNT(*) FROM batch_queue_items WHERE package_id=?1 AND status='failed'",
                params![package_id],
                |r| r.get(0),
            )
            .map_err(map_db_err)?;
        let pending: i32 = conn
            .query_row(
                "SELECT COUNT(*) FROM batch_queue_items WHERE package_id=?1 AND status IN ('pending','paused')",
                params![package_id],
                |r| r.get(0),
            )
            .map_err(map_db_err)?;
        Ok((completed, failed, pending))
    }

    /// Get items by package + type + status (for chain trigger).
    pub async fn batch_get_items_by_type_and_status(
        &self,
        package_id: &str,
        queue_type: &str,
        status: &str,
    ) -> tfp_core::Result<Vec<BatchQueueItem>> {
        let conn = self.conn().lock().await;
        let sql = format!("SELECT {ITEM_COLS} FROM batch_queue_items WHERE package_id=?1 AND queue_type=?2 AND status=?3 ORDER BY created_at ASC");
        let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
        let rows = stmt.query_map(params![package_id, queue_type, status], map_queue_item).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    /// Count all packages grouped by state (for bucket nav).
    pub async fn batch_count_packages_by_state(&self) -> tfp_core::Result<Vec<(String, i32)>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT state, COUNT(*) FROM batch_packages GROUP BY state"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map([], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, i32>(1)?))
        }).map_err(map_db_err)?;
        let mut result = Vec::new();
        for r in rows {
            result.push(r.map_err(map_db_err)?);
        }
        Ok(result)
    }

    /// Set status for all running items of a package to 'paused'.
    pub async fn batch_pause_running_items(&self, package_id: &str) -> tfp_core::Result<u32> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        let count = conn.execute(
            "UPDATE batch_queue_items SET status='paused', updated_at=?1 WHERE package_id=?2 AND status IN ('pending','running')",
            params![now, package_id],
        ).map_err(map_db_err)?;
        Ok(count as u32)
    }

    /// Resume paused items back to pending.
    pub async fn batch_resume_paused_items(&self, package_id: &str) -> tfp_core::Result<u32> {
        let conn = self.conn().lock().await;
        let now = Utc::now().to_rfc3339();
        let count = conn.execute(
            "UPDATE batch_queue_items SET status='pending', updated_at=?1 WHERE package_id=?2 AND status='paused'",
            params![now, package_id],
        ).map_err(map_db_err)?;
        Ok(count as u32)
    }

    /// Get a single queue item by id.
    pub async fn batch_get_queue_item(&self, id: &str) -> tfp_core::Result<Option<BatchQueueItem>> {
        let conn = self.conn().lock().await;
        let sql = format!("SELECT {ITEM_COLS} FROM batch_queue_items WHERE id=?1");
        let mut stmt = conn.prepare(&sql).map_err(map_db_err)?;
        let mut rows = stmt.query_map(params![id], map_queue_item).map_err(map_db_err)?;
        match rows.next() {
            Some(r) => Ok(Some(r.map_err(map_db_err)?)),
            None => Ok(None),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn batch_package_roundtrip() {
        let db = Database::open_in_memory().unwrap();
        let pkg = BatchPackage {
            id: "pkg-1".into(),
            session_id: "sess-1".into(),
            audio_file_id: "af-1".into(),
            display_name: "Test Package".into(),
            state: BatchPackageState::Pending,
            is_paused: false,
            is_removed: false,
            total_count: 3,
            completed_count: 0,
            failed_count: 0,
            progress: 0.0,
            created_at: "2026-01-01T00:00:00Z".into(),
            updated_at: "2026-01-01T00:00:00Z".into(),
        };
        db.batch_create_package(&pkg).await.unwrap();

        let loaded = db.batch_get_package("pkg-1").await.unwrap().unwrap();
        assert_eq!(loaded.id, "pkg-1");
        assert_eq!(loaded.display_name, "Test Package");
        assert_eq!(loaded.state, BatchPackageState::Pending);
        assert_eq!(loaded.total_count, 3);

        // Update state
        db.batch_update_package_state("pkg-1", "running", false, false).await.unwrap();
        let loaded2 = db.batch_get_package("pkg-1").await.unwrap().unwrap();
        assert_eq!(loaded2.state, BatchPackageState::Running);

        // Update counts
        db.batch_update_package_counts("pkg-1", 3, 2, 1).await.unwrap();
        let loaded3 = db.batch_get_package("pkg-1").await.unwrap().unwrap();
        assert_eq!(loaded3.completed_count, 2);
        assert_eq!(loaded3.failed_count, 1);
        assert!((loaded3.progress - 1.0).abs() < 0.01);
    }

    #[tokio::test]
    async fn batch_queue_item_crud() {
        let db = Database::open_in_memory().unwrap();

        // Create package first
        let pkg = BatchPackage {
            id: "pkg-1".into(),
            session_id: "sess-1".into(),
            audio_file_id: "af-1".into(),
            display_name: "Pkg".into(),
            state: BatchPackageState::Pending,
            is_paused: false,
            is_removed: false,
            total_count: 0,
            completed_count: 0,
            failed_count: 0,
            progress: 0.0,
            created_at: "2026-01-01T00:00:00Z".into(),
            updated_at: "2026-01-01T00:00:00Z".into(),
        };
        db.batch_create_package(&pkg).await.unwrap();

        let item = BatchQueueItem {
            id: "qi-1".into(),
            package_id: "pkg-1".into(),
            queue_type: BatchQueueItemType::SpeechSubtitle,
            file_name: "audio.wav".into(),
            full_path: "/path/audio.wav".into(),
            sheet_name: String::new(),
            sheet_tag: String::new(),
            prompt: String::new(),
            status: BatchQueueItemStatus::Pending,
            progress: 0.0,
            status_message: String::new(),
            error: None,
            created_at: "2026-01-01T00:00:00Z".into(),
            updated_at: "2026-01-01T00:00:00Z".into(),
        };
        db.batch_create_queue_item(&item).await.unwrap();

        let items = db.batch_get_pending_items("pkg-1").await.unwrap();
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].queue_type, BatchQueueItemType::SpeechSubtitle);

        // Update status
        db.batch_update_queue_status("qi-1", "completed", None).await.unwrap();
        let pending = db.batch_get_pending_items("pkg-1").await.unwrap();
        assert_eq!(pending.len(), 0);

        let (completed, failed, p) = db.batch_count_by_status("pkg-1").await.unwrap();
        assert_eq!(completed, 1);
        assert_eq!(failed, 0);
        assert_eq!(p, 0);
    }

    #[tokio::test]
    async fn batch_pause_resume_items() {
        let db = Database::open_in_memory().unwrap();
        let pkg = BatchPackage {
            id: "pkg-p".into(),
            session_id: "s".into(),
            audio_file_id: "a".into(),
            display_name: "P".into(),
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
                id: format!("qi-{i}"),
                package_id: "pkg-p".into(),
                queue_type: BatchQueueItemType::ReviewSheet,
                file_name: String::new(),
                full_path: String::new(),
                sheet_name: format!("Sheet{i}"),
                sheet_tag: String::new(),
                prompt: "test prompt".into(),
                status: BatchQueueItemStatus::Pending,
                progress: 0.0,
                status_message: String::new(),
                error: None,
                created_at: "2026-01-01".into(),
                updated_at: "2026-01-01".into(),
            };
            db.batch_create_queue_item(&item).await.unwrap();
        }

        // Pause
        let paused = db.batch_pause_running_items("pkg-p").await.unwrap();
        assert_eq!(paused, 2);

        let pending = db.batch_get_pending_items("pkg-p").await.unwrap();
        assert_eq!(pending.len(), 0);

        // Resume
        let resumed = db.batch_resume_paused_items("pkg-p").await.unwrap();
        assert_eq!(resumed, 2);

        let pending2 = db.batch_get_pending_items("pkg-p").await.unwrap();
        assert_eq!(pending2.len(), 2);
    }

    #[tokio::test]
    async fn batch_delete_package_cascades() {
        let db = Database::open_in_memory().unwrap();
        let pkg = BatchPackage {
            id: "pkg-del".into(),
            session_id: "s".into(),
            audio_file_id: "a".into(),
            display_name: "Del".into(),
            state: BatchPackageState::Completed,
            is_paused: false,
            is_removed: false,
            total_count: 1,
            completed_count: 1,
            failed_count: 0,
            progress: 1.0,
            created_at: "2026-01-01".into(),
            updated_at: "2026-01-01".into(),
        };
        db.batch_create_package(&pkg).await.unwrap();
        let item = BatchQueueItem {
            id: "qi-del".into(),
            package_id: "pkg-del".into(),
            queue_type: BatchQueueItemType::ReviewSheet,
            file_name: String::new(),
            full_path: String::new(),
            sheet_name: "S".into(),
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

        db.batch_delete_package("pkg-del").await.unwrap();
        assert!(db.batch_get_package("pkg-del").await.unwrap().is_none());
        let items = db.batch_get_items_by_package("pkg-del").await.unwrap();
        assert!(items.is_empty());
    }
}
