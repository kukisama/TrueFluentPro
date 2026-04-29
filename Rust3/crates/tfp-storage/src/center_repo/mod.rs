use rusqlite::params;

use crate::db::{Database, map_db_err};
use crate::studio_repo::map_studio_task;
use tfp_core::{
    CenterWorkspace, CenterWorkspaceBundle, CenterAssetDetail,
    CanvasRound, RoundPromptSummary, StudioReferenceImage, StudioTask,
};

impl Database {
    // ── Workspace CRUD (5) ──

    pub async fn center_list_workspaces(&self, limit: i64, offset: i64) -> tfp_core::Result<Vec<CenterWorkspace>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT s.id, s.session_type, s.name, s.is_deleted, s.created_at, s.updated_at,
                    s.last_accessed_at, s.current_round_id,
                    (SELECT COUNT(*) FROM canvas_rounds WHERE session_id = s.id) as round_count,
                    s.asset_count,
                    (SELECT COUNT(*) FROM studio_tasks WHERE session_id = s.id AND status IN ('pending','running','polling')) as has_running,
                    s.canvas_mode, s.media_kind,
                    s.source_session_id, s.source_session_name,
                    s.source_asset_id, s.source_asset_file_name,
                    s.source_asset_kind, s.source_reference_role
             FROM studio_sessions s
             WHERE s.is_deleted = 0 AND s.session_type IN ('canvas_image','canvas_video')
             ORDER BY s.updated_at DESC LIMIT ?1 OFFSET ?2"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![limit, offset], |row| {
            Ok(CenterWorkspace {
                id: row.get(0)?,
                session_type: row.get(1)?,
                name: row.get(2)?,
                is_deleted: row.get::<_, i64>(3)? != 0,
                created_at: row.get(4)?,
                updated_at: row.get(5)?,
                last_accessed_at: row.get(6)?,
                current_round_id: row.get(7)?,
                round_count: row.get(8)?,
                asset_count: row.get(9)?,
                has_running_task: row.get::<_, i64>(10)? > 0,
                canvas_mode: row.get::<_, String>(11).unwrap_or_default(),
                media_kind: row.get::<_, String>(12).unwrap_or_default(),
                source_session_id: row.get(13)?,
                source_session_name: row.get(14)?,
                source_asset_id: row.get(15)?,
                source_asset_file_name: row.get(16)?,
                source_asset_kind: row.get(17)?,
                source_reference_role: row.get(18)?,
            })
        }).map_err(map_db_err)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)
    }

    pub async fn center_create_workspace(&self, kind: &str, name: &str) -> tfp_core::Result<CenterWorkspace> {
        self.center_create_workspace_full(kind, name, "", "").await
    }

    /// Create workspace with explicit canvas_mode and media_kind
    pub async fn center_create_workspace_full(
        &self,
        kind: &str,
        name: &str,
        canvas_mode: &str,
        media_kind: &str,
    ) -> tfp_core::Result<CenterWorkspace> {
        let conn = self.conn().lock().await;
        let id = uuid::Uuid::new_v4().to_string();
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "INSERT INTO studio_sessions (id, session_type, name, directory_path, canvas_mode, media_kind, is_deleted, created_at, updated_at, last_accessed_at, message_count, task_count, asset_count, is_legacy_import)
             VALUES (?1, ?2, ?3, '', ?4, ?5, 0, ?6, ?6, ?6, 0, 0, 0, 0)",
            params![id, kind, name, canvas_mode, media_kind, now],
        ).map_err(map_db_err)?;
        Ok(CenterWorkspace {
            id,
            session_type: kind.to_string(),
            name: name.to_string(),
            is_deleted: false,
            created_at: now.clone(),
            updated_at: now.clone(),
            last_accessed_at: Some(now),
            current_round_id: None,
            round_count: 0,
            asset_count: 0,
            has_running_task: false,
            canvas_mode: canvas_mode.to_string(),
            media_kind: media_kind.to_string(),
            source_session_id: None,
            source_session_name: None,
            source_asset_id: None,
            source_asset_file_name: None,
            source_asset_kind: None,
            source_reference_role: None,
        })
    }

    pub async fn center_rename_workspace(&self, id: &str, new_name: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET name = ?1, updated_at = ?2 WHERE id = ?3",
            params![new_name, now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn center_soft_delete_workspace(&self, id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET is_deleted = 1, updated_at = ?1 WHERE id = ?2",
            params![now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn center_update_last_accessed(&self, id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET last_accessed_at = ?1 WHERE id = ?2",
            params![now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    // ── Bundle query (1, complex) ──

    pub async fn center_get_workspace_bundle(&self, workspace_id: &str) -> tfp_core::Result<CenterWorkspaceBundle> {
        let conn = self.conn().lock().await;

        // 1. Workspace metadata
        let ws = conn.query_row(
            "SELECT s.id, s.session_type, s.name, s.is_deleted, s.created_at, s.updated_at,
                    s.last_accessed_at, s.current_round_id,
                    (SELECT COUNT(*) FROM canvas_rounds WHERE session_id = s.id) as round_count,
                    s.asset_count,
                    (SELECT COUNT(*) FROM studio_tasks WHERE session_id = s.id AND status IN ('pending','running','polling')) as has_running,
                    s.canvas_mode, s.media_kind,
                    s.source_session_id, s.source_session_name,
                    s.source_asset_id, s.source_asset_file_name,
                    s.source_asset_kind, s.source_reference_role
             FROM studio_sessions s WHERE s.id = ?1",
            params![workspace_id],
            |row| Ok(CenterWorkspace {
                id: row.get(0)?,
                session_type: row.get(1)?,
                name: row.get(2)?,
                is_deleted: row.get::<_, i64>(3)? != 0,
                created_at: row.get(4)?,
                updated_at: row.get(5)?,
                last_accessed_at: row.get(6)?,
                current_round_id: row.get(7)?,
                round_count: row.get(8)?,
                asset_count: row.get(9)?,
                has_running_task: row.get::<_, i64>(10)? > 0,
                canvas_mode: row.get::<_, String>(11).unwrap_or_default(),
                media_kind: row.get::<_, String>(12).unwrap_or_default(),
                source_session_id: row.get(13)?,
                source_session_name: row.get(14)?,
                source_asset_id: row.get(15)?,
                source_asset_file_name: row.get(16)?,
                source_asset_kind: row.get(17)?,
                source_reference_role: row.get(18)?,
            }),
        ).map_err(map_db_err)?;

        // 2. All rounds
        let mut round_stmt = conn.prepare(
            "SELECT id, session_id, round_index, prompt, params_json, model_ref, created_at, status
             FROM canvas_rounds WHERE session_id = ?1 ORDER BY round_index ASC"
        ).map_err(map_db_err)?;
        let rounds: Vec<CanvasRound> = round_stmt.query_map(params![workspace_id], |row| {
            Ok(CanvasRound {
                id: row.get(0)?,
                session_id: row.get(1)?,
                round_index: row.get(2)?,
                prompt: row.get(3)?,
                params_json: row.get(4)?,
                model_ref: row.get(5)?,
                created_at: row.get(6)?,
                status: row.get(7)?,
            })
        }).map_err(map_db_err)?.collect::<Result<Vec<_>, _>>().map_err(map_db_err)?;

        // 3. Current round assets
        let current_round_id = ws.current_round_id.clone()
            .or_else(|| rounds.last().map(|r| r.id.clone()));
        let current_round_assets = if let Some(ref rid) = current_round_id {
            let mut stmt = conn.prepare(
                "SELECT cra.id, cra.round_id, cra.asset_id, cra.sequence, cra.is_selected,
                        sa.file_path, sa.preview_path, sa.kind, sa.width, sa.height, sa.duration_ms, sa.created_at
                 FROM canvas_round_assets cra
                 JOIN studio_assets sa ON sa.asset_id = cra.asset_id
                 WHERE cra.round_id = ?1 ORDER BY cra.sequence"
            ).map_err(map_db_err)?;
            let rows = stmt.query_map(params![rid], |row| {
                Ok(CenterAssetDetail {
                    id: row.get(0)?,
                    round_id: row.get(1)?,
                    asset_id: row.get(2)?,
                    sequence: row.get(3)?,
                    is_selected: row.get::<_, i64>(4)? != 0,
                    file_path: row.get(5)?,
                    preview_path: row.get(6)?,
                    kind: row.get(7)?,
                    width: row.get(8)?,
                    height: row.get(9)?,
                    duration_ms: row.get(10)?,
                    created_at: row.get(11)?,
                })
            }).map_err(map_db_err)?;
            rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)?
        } else {
            vec![]
        };

        // 4. Reference images
        let mut ref_stmt = conn.prepare(
            "SELECT id, session_id, file_path, sort_order, width, height, created_at
             FROM studio_reference_images WHERE session_id = ?1 ORDER BY sort_order"
        ).map_err(map_db_err)?;
        let reference_images: Vec<StudioReferenceImage> = ref_stmt.query_map(params![workspace_id], |row| {
            Ok(StudioReferenceImage {
                id: row.get(0)?,
                session_id: row.get(1)?,
                file_path: row.get(2)?,
                sort_order: row.get(3)?,
                width: row.get(4)?,
                height: row.get(5)?,
                created_at: row.get(6)?,
            })
        }).map_err(map_db_err)?.collect::<Result<Vec<_>, _>>().map_err(map_db_err)?;

        // 5. Running tasks
        let mut task_stmt = conn.prepare(
            "SELECT * FROM studio_tasks WHERE session_id = ?1 AND status IN ('pending','running','polling')"
        ).map_err(map_db_err)?;
        let running_tasks: Vec<StudioTask> = task_stmt.query_map(params![workspace_id], map_studio_task)
            .map_err(map_db_err)?.collect::<Result<Vec<_>, _>>().map_err(map_db_err)?;

        // 6. All asset count (across all rounds)
        let all_asset_count: i64 = conn.query_row(
            "SELECT COUNT(*) FROM canvas_round_assets cra
             JOIN canvas_rounds cr ON cra.round_id = cr.id
             WHERE cr.session_id = ?1",
            params![workspace_id],
            |row| row.get(0),
        ).unwrap_or(0);

        // 7. Round prompt summaries
        let mut summary_stmt = conn.prepare(
            "SELECT cr.id, cr.round_index, cr.prompt, cr.status, cr.created_at,
                    (SELECT COUNT(*) FROM canvas_round_assets WHERE round_id = cr.id) as asset_count
             FROM canvas_rounds cr WHERE cr.session_id = ?1 ORDER BY cr.round_index ASC"
        ).map_err(map_db_err)?;
        let round_prompts: Vec<RoundPromptSummary> = summary_stmt.query_map(params![workspace_id], |row| {
            let prompt: String = row.get(2)?;
            let preview: String = prompt.chars().take(80).collect();
            Ok(RoundPromptSummary {
                round_id: row.get(0)?,
                round_index: row.get(1)?,
                prompt_preview: preview,
                status: row.get(3)?,
                asset_count: row.get(5)?,
                created_at: row.get(4)?,
            })
        }).map_err(map_db_err)?.collect::<Result<Vec<_>, _>>().map_err(map_db_err)?;

        let has_more_rounds = rounds.len() > 50; // arbitrary threshold for "more"

        Ok(CenterWorkspaceBundle {
            workspace: ws,
            rounds,
            current_round_assets,
            reference_images,
            running_tasks,
            all_asset_count,
            has_more_rounds,
            round_prompts,
        })
    }

    // ── Round CRUD (5) ──

    pub async fn center_list_rounds(&self, workspace_id: &str) -> tfp_core::Result<Vec<CanvasRound>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, round_index, prompt, params_json, model_ref, created_at, status
             FROM canvas_rounds WHERE session_id = ?1 ORDER BY round_index ASC"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![workspace_id], |row| {
            Ok(CanvasRound {
                id: row.get(0)?, session_id: row.get(1)?, round_index: row.get(2)?,
                prompt: row.get(3)?, params_json: row.get(4)?, model_ref: row.get(5)?,
                created_at: row.get(6)?, status: row.get(7)?,
            })
        }).map_err(map_db_err)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)
    }

    pub async fn center_get_round(&self, round_id: &str) -> tfp_core::Result<Option<CanvasRound>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT id, session_id, round_index, prompt, params_json, model_ref, created_at, status
             FROM canvas_rounds WHERE id = ?1"
        ).map_err(map_db_err)?;
        let mut rows = stmt.query_map(params![round_id], |row| {
            Ok(CanvasRound {
                id: row.get(0)?, session_id: row.get(1)?, round_index: row.get(2)?,
                prompt: row.get(3)?, params_json: row.get(4)?, model_ref: row.get(5)?,
                created_at: row.get(6)?, status: row.get(7)?,
            })
        }).map_err(map_db_err)?;
        match rows.next() {
            Some(Ok(r)) => Ok(Some(r)),
            Some(Err(e)) => Err(map_db_err(e)),
            None => Ok(None),
        }
    }

    pub async fn center_create_round(&self, session_id: &str, prompt: &str, params_json: &str, model_ref: &str) -> tfp_core::Result<CanvasRound> {
        let conn = self.conn().lock().await;
        let id = uuid::Uuid::new_v4().to_string();
        let now = chrono::Utc::now().to_rfc3339();
        let round_index: i64 = conn.query_row(
            "SELECT COALESCE(MAX(round_index), 0) + 1 FROM canvas_rounds WHERE session_id = ?1",
            params![session_id],
            |row| row.get(0),
        ).map_err(map_db_err)?;
        conn.execute(
            "INSERT INTO canvas_rounds (id, session_id, round_index, prompt, params_json, model_ref, created_at, status)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, 'pending')",
            params![id, session_id, round_index, prompt, params_json, model_ref, now],
        ).map_err(map_db_err)?;
        conn.execute(
            "UPDATE studio_sessions SET current_round_id = ?1, updated_at = ?2 WHERE id = ?3",
            params![id, now, session_id],
        ).map_err(map_db_err)?;
        Ok(CanvasRound {
            id,
            session_id: session_id.to_string(),
            round_index,
            prompt: prompt.to_string(),
            params_json: params_json.to_string(),
            model_ref: model_ref.to_string(),
            created_at: now,
            status: "pending".to_string(),
        })
    }

    pub async fn center_set_active_round(&self, workspace_id: &str, round_id: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET current_round_id = ?1, updated_at = ?2 WHERE id = ?3",
            params![round_id, now, workspace_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    pub async fn center_update_round_status(&self, round_id: &str, status: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "UPDATE canvas_rounds SET status = ?1 WHERE id = ?2",
            params![status, round_id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    // ── Round asset operations (4) ──

    pub async fn center_add_round_asset(&self, round_id: &str, asset_id: &str, sequence: i64) -> tfp_core::Result<String> {
        let conn = self.conn().lock().await;
        let id = uuid::Uuid::new_v4().to_string();
        conn.execute(
            "INSERT INTO canvas_round_assets (id, round_id, asset_id, sequence, is_selected)
             VALUES (?1, ?2, ?3, ?4, 0)",
            params![id, round_id, asset_id, sequence],
        ).map_err(map_db_err)?;
        Ok(id)
    }

    pub async fn center_select_assets(&self, round_id: &str, asset_ids: &[String], selected: bool) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let val = if selected { 1i64 } else { 0 };
        for aid in asset_ids {
            conn.execute(
                "UPDATE canvas_round_assets SET is_selected = ?1 WHERE round_id = ?2 AND asset_id = ?3",
                params![val, round_id, aid],
            ).map_err(map_db_err)?;
        }
        Ok(())
    }

    pub async fn center_delete_assets(&self, asset_ids: &[String]) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        for aid in asset_ids {
            conn.execute("DELETE FROM canvas_round_assets WHERE asset_id = ?1", params![aid]).map_err(map_db_err)?;
            conn.execute("DELETE FROM studio_assets WHERE asset_id = ?1", params![aid]).map_err(map_db_err)?;
        }
        Ok(())
    }

    pub async fn center_get_round_assets(&self, round_id: &str) -> tfp_core::Result<Vec<CenterAssetDetail>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT cra.id, cra.round_id, cra.asset_id, cra.sequence, cra.is_selected,
                    sa.file_path, sa.preview_path, sa.kind, sa.width, sa.height, sa.duration_ms, sa.created_at
             FROM canvas_round_assets cra
             JOIN studio_assets sa ON sa.asset_id = cra.asset_id
             WHERE cra.round_id = ?1 ORDER BY cra.sequence"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![round_id], |row| {
            Ok(CenterAssetDetail {
                id: row.get(0)?, round_id: row.get(1)?, asset_id: row.get(2)?,
                sequence: row.get(3)?, is_selected: row.get::<_, i64>(4)? != 0,
                file_path: row.get(5)?, preview_path: row.get(6)?, kind: row.get(7)?,
                width: row.get(8)?, height: row.get(9)?, duration_ms: row.get(10)?,
                created_at: row.get(11)?,
            })
        }).map_err(map_db_err)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)
    }

    // ── Helper methods (2) ──

    pub async fn center_get_next_round_index(&self, session_id: &str) -> tfp_core::Result<i64> {
        let conn = self.conn().lock().await;
        let idx: i64 = conn.query_row(
            "SELECT COALESCE(MAX(round_index), 0) + 1 FROM canvas_rounds WHERE session_id = ?1",
            params![session_id],
            |row| row.get(0),
        ).map_err(map_db_err)?;
        Ok(idx)
    }

    pub async fn center_get_asset_path(&self, asset_id: &str) -> tfp_core::Result<Option<String>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare("SELECT file_path FROM studio_assets WHERE asset_id = ?1").map_err(map_db_err)?;
        let mut rows = stmt.query(params![asset_id]).map_err(map_db_err)?;
        if let Some(row) = rows.next().map_err(map_db_err)? {
            Ok(Some(row.get(0).map_err(map_db_err)?))
        } else {
            Ok(None)
        }
    }

    // ── T-002: Update workspace mode ──

    /// Update canvas_mode and media_kind for a workspace.
    pub async fn center_update_workspace_mode(&self, id: &str, canvas_mode: &str, media_kind: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        let now = chrono::Utc::now().to_rfc3339();
        conn.execute(
            "UPDATE studio_sessions SET canvas_mode = ?1, media_kind = ?2, updated_at = ?3 WHERE id = ?4",
            params![canvas_mode, media_kind, now, id],
        ).map_err(map_db_err)?;
        Ok(())
    }

    // ── T-003: Derive workspace from asset ──

    /// Create a new workspace derived from an existing asset (EditAsset flow).
    pub async fn center_derive_workspace(
        &self,
        source_workspace_id: &str,
        source_asset_id: &str,
        kind: &str,
        name: &str,
        reference_file_path: &str,
    ) -> tfp_core::Result<CenterWorkspace> {
        let conn = self.conn().lock().await;
        let id = uuid::Uuid::new_v4().to_string();
        let now = chrono::Utc::now().to_rfc3339();

        // Look up source workspace name
        let source_session_name: String = conn.query_row(
            "SELECT COALESCE(name, '') FROM studio_sessions WHERE id = ?1",
            params![source_workspace_id],
            |row| row.get(0),
        ).unwrap_or_default();

        // Look up source asset file name
        let source_asset_file_name: String = conn.query_row(
            "SELECT COALESCE(file_path, '') FROM studio_assets WHERE asset_id = ?1",
            params![source_asset_id],
            |row| {
                let path: String = row.get(0)?;
                Ok(path.rsplit(['/', '\\']).next().unwrap_or("").to_string())
            },
        ).unwrap_or_default();

        // Determine media_kind from kind
        let media_kind = if kind == "video" { "video" } else { "image" };
        let session_type = if kind == "video" { "canvas_video" } else { "canvas_image" };
        let reference_role = if kind == "video" { "video_last_frame" } else { "direct_image" };

        // Create workspace with lineage
        conn.execute(
            "INSERT INTO studio_sessions (id, session_type, name, directory_path, canvas_mode, media_kind,
             is_deleted, created_at, updated_at, last_accessed_at,
             source_session_id, source_session_name, source_asset_id,
             source_asset_file_name, source_asset_kind, source_reference_role,
             message_count, task_count, asset_count, is_legacy_import)
             VALUES (?1, ?2, ?3, '', 'edit', ?4, 0, ?5, ?5, ?5,
                     ?6, ?7, ?8, ?9, ?10, ?11,
                     0, 0, 0, 0)",
            params![
                id, session_type, name, media_kind, now,
                source_workspace_id, source_session_name, source_asset_id,
                source_asset_file_name, kind, reference_role,
            ],
        ).map_err(map_db_err)?;

        // Insert reference image record
        let ref_id = uuid::Uuid::new_v4().to_string();
        conn.execute(
            "INSERT INTO studio_reference_images (id, session_id, file_path, sort_order, width, height, created_at)
             VALUES (?1, ?2, ?3, 0, NULL, NULL, ?4)",
            params![ref_id, id, reference_file_path, now],
        ).map_err(map_db_err)?;

        Ok(CenterWorkspace {
            id,
            session_type: session_type.to_string(),
            name: name.to_string(),
            is_deleted: false,
            created_at: now.clone(),
            updated_at: now.clone(),
            last_accessed_at: Some(now),
            current_round_id: None,
            round_count: 0,
            asset_count: 0,
            has_running_task: false,
            canvas_mode: "edit".to_string(),
            media_kind: media_kind.to_string(),
            source_session_id: Some(source_workspace_id.to_string()),
            source_session_name: Some(source_session_name),
            source_asset_id: Some(source_asset_id.to_string()),
            source_asset_file_name: Some(source_asset_file_name),
            source_asset_kind: Some(kind.to_string()),
            source_reference_role: Some(reference_role.to_string()),
        })
    }

    // ── T-004: Get all assets across all rounds ──

    /// Retrieve all assets across all rounds for a workspace.
    pub async fn center_get_all_assets(&self, workspace_id: &str, limit: i64) -> tfp_core::Result<Vec<CenterAssetDetail>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn.prepare(
            "SELECT cra.id, cra.round_id, cra.asset_id, cra.sequence, cra.is_selected,
                    sa.file_path, sa.preview_path, sa.kind, sa.width, sa.height, sa.duration_ms, sa.created_at
             FROM canvas_round_assets cra
             JOIN studio_assets sa ON sa.asset_id = cra.asset_id
             JOIN canvas_rounds cr ON cra.round_id = cr.id
             WHERE cr.session_id = ?1
             ORDER BY sa.created_at DESC
             LIMIT ?2"
        ).map_err(map_db_err)?;
        let rows = stmt.query_map(params![workspace_id, limit], |row| {
            Ok(CenterAssetDetail {
                id: row.get(0)?,
                round_id: row.get(1)?,
                asset_id: row.get(2)?,
                sequence: row.get(3)?,
                is_selected: row.get::<_, i64>(4)? != 0,
                file_path: row.get(5)?,
                preview_path: row.get(6)?,
                kind: row.get(7)?,
                width: row.get(8)?,
                height: row.get(9)?,
                duration_ms: row.get(10)?,
                created_at: row.get(11)?,
            })
        }).map_err(map_db_err)?;
        rows.collect::<Result<Vec<_>, _>>().map_err(map_db_err)
    }

    // ── T-005 helper: Count reference images for a session ──

    /// Count existing reference images for a workspace/session.
    pub async fn center_count_reference_images(&self, session_id: &str) -> tfp_core::Result<usize> {
        let conn = self.conn().lock().await;
        let count: i64 = conn.query_row(
            "SELECT COUNT(*) FROM studio_reference_images WHERE session_id = ?1",
            params![session_id],
            |row| row.get(0),
        ).map_err(map_db_err)?;
        Ok(count as usize)
    }
}

#[cfg(test)]
mod tests;
