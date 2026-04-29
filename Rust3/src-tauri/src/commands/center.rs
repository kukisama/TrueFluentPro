use std::sync::Arc;
use std::path::Path;
use tauri::{Manager, State};

use crate::state::AppState;
use tfp_core::{
    CenterWorkspace, CenterWorkspaceBundle, CenterAssetDetail,
    CanvasRound, StudioTask, ImageGenRequest,
    VideoGenRequest, VideoCapabilityEntry, ExportResult,
};
use tfp_providers::OpenAiVideoProvider;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Media Center commands
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

pub(crate) fn validate_workspace_kind(kind: &str) -> Result<(), String> {
    if kind != "canvas_image" && kind != "canvas_video" {
        return Err("kind must be 'canvas_image' or 'canvas_video'".to_string());
    }
    Ok(())
}

#[tauri::command]
pub async fn center_list_workspaces(
    state: State<'_, AppState>,
    limit: Option<i64>,
    offset: Option<i64>,
) -> Result<Vec<CenterWorkspace>, String> {
    state.db.center_list_workspaces(limit.unwrap_or(30), offset.unwrap_or(0))
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn center_create_workspace(
    state: State<'_, AppState>,
    kind: String,
    name: String,
) -> Result<CenterWorkspace, String> {
    validate_workspace_kind(&kind)?;
    state.db.center_create_workspace(&kind, &name)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn center_rename_workspace(
    state: State<'_, AppState>,
    id: String,
    name: String,
) -> Result<(), String> {
    state.db.center_rename_workspace(&id, &name)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn center_soft_delete_workspace(
    state: State<'_, AppState>,
    id: String,
) -> Result<(), String> {
    state.db.center_soft_delete_workspace(&id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn center_get_workspace_bundle(
    state: State<'_, AppState>,
    id: String,
) -> Result<CenterWorkspaceBundle, String> {
    let _ = state.db.center_update_last_accessed(&id).await;
    state.db.center_get_workspace_bundle(&id)
        .await
        .map_err(|e| e.to_string())
}

// ── Round management (3) ──

#[tauri::command]
pub async fn center_list_rounds(
    state: State<'_, AppState>,
    workspace_id: String,
) -> Result<Vec<CanvasRound>, String> {
    state.db.center_list_rounds(&workspace_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn center_get_round(
    state: State<'_, AppState>,
    round_id: String,
) -> Result<Option<CanvasRound>, String> {
    state.db.center_get_round(&round_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn center_set_active_round(
    state: State<'_, AppState>,
    workspace_id: String,
    round_id: String,
) -> Result<(), String> {
    state.db.center_set_active_round(&workspace_id, &round_id)
        .await
        .map_err(|e| e.to_string())
}

// ── Generation tasks (2, complex async) ──

/// Start an image generation round: create round → create task → spawn background → return (task_id, round_id)
#[tauri::command]
pub async fn center_start_image_round(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    workspace_id: String,
    prompt: String,
    params: serde_json::Value,
    reference_paths: Vec<String>,
) -> Result<serde_json::Value, String> {
    // T-005: Validate reference count before proceeding
    let existing_refs = state.db.center_count_reference_images(&workspace_id).await.unwrap_or(0);
    tfp_media::center_validation::validate_reference_count("canvas_image", existing_refs, reference_paths.len())?;

    let params_json = serde_json::to_string(&params).unwrap_or_else(|_| "{}".to_string());
    let model_ref = params.get("model").and_then(|v| v.as_str()).unwrap_or("").to_string();

    let round = state.db.center_create_round(&workspace_id, &prompt, &params_json, &model_ref)
        .await
        .map_err(|e| e.to_string())?;

    let task_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now().to_rfc3339();
    let task = StudioTask {
        id: task_id.clone(),
        session_id: workspace_id.clone(),
        task_type: "image_generation".to_string(),
        status: "pending".to_string(),
        prompt: prompt.clone(),
        progress: 0.0,
        result_file_path: None,
        error_message: None,
        has_reference_input: !reference_paths.is_empty(),
        remote_video_id: None,
        remote_video_api_mode: None,
        remote_generation_id: None,
        remote_download_url: None,
        generate_seconds: None,
        download_seconds: None,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.studio_upsert_task(&task).await.map_err(|e| e.to_string())?;

    let endpoint_id = params.get("endpoint_id").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let provider = {
        let reg = state.providers.read().await;
        reg.get_image_gen(&endpoint_id)
    };
    let provider = match provider {
        Some(p) => p,
        None => return Err(format!("Image gen provider not found: {}", endpoint_id)),
    };

    let width = params.get("width").and_then(|v| v.as_u64()).unwrap_or(1024) as u32;
    let height = params.get("height").and_then(|v| v.as_u64()).unwrap_or(1024) as u32;
    let quality = params.get("quality").and_then(|v| v.as_str()).unwrap_or("auto").to_string();
    let n = params.get("n").and_then(|v| v.as_u64()).unwrap_or(1) as u32;
    let output_format = params.get("output_format").and_then(|v| v.as_str()).unwrap_or("png").to_string();
    let background = params.get("background").and_then(|v| v.as_str()).map(|s| s.to_string());
    let model = params.get("model").and_then(|v| v.as_str()).unwrap_or("gpt-image-2").to_string();

    let request = ImageGenRequest {
        prompt: prompt.clone(),
        width, height,
        model,
        quality: Some(quality),
        n: Some(n),
        output_format: Some(output_format.clone()),
        background,
        endpoint_id: endpoint_id.clone(),
        text_model: None, image_model: None, previous_response_id: None,
        reference_image_path: None, image_edit_mode: None,
        uploaded_file_ids: vec![],
    };

    let round_id = round.id.clone();
    let db = state.db.clone();
    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app));
    let task_id_ret = task_id.clone();
    let round_id_ret = round.id.clone();

    tokio::spawn(async move {
        tfp_media::center_service::run_center_image_round(
            &db, sink.as_ref(), provider,
            &task_id, &workspace_id, &round_id, &prompt, request, &output_format, &data_dir,
        ).await;
    });

    Ok(serde_json::json!({
        "task_id": task_id_ret,
        "round_id": round_id_ret,
    }))
}

/// Start a video generation round (3 phases: create → poll → download)
#[tauri::command]
pub async fn center_start_video_round(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    workspace_id: String,
    prompt: String,
    params: serde_json::Value,
    reference_path: Option<String>,
) -> Result<serde_json::Value, String> {
    // T-005: Validate reference count for video (max 1)
    if reference_path.is_some() {
        let existing_refs = state.db.center_count_reference_images(&workspace_id).await.unwrap_or(0);
        tfp_media::center_validation::validate_reference_count("canvas_video", existing_refs, 1)?;
    }

    let params_json = serde_json::to_string(&params).unwrap_or_else(|_| "{}".to_string());
    let model_ref = params.get("model").and_then(|v| v.as_str()).unwrap_or("").to_string();

    let round = state.db.center_create_round(&workspace_id, &prompt, &params_json, &model_ref)
        .await
        .map_err(|e| e.to_string())?;

    let task_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now().to_rfc3339();
    let task = StudioTask {
        id: task_id.clone(),
        session_id: workspace_id.clone(),
        task_type: "video_generation".to_string(),
        status: "pending".to_string(),
        prompt: prompt.clone(),
        progress: 0.0,
        result_file_path: None,
        error_message: None,
        has_reference_input: reference_path.is_some(),
        remote_video_id: None,
        remote_video_api_mode: None,
        remote_generation_id: None,
        remote_download_url: None,
        generate_seconds: None,
        download_seconds: None,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.studio_upsert_task(&task).await.map_err(|e| e.to_string())?;

    let endpoint_id = params.get("endpoint_id").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let ep = {
        let config = state.config.read().await;
        config.endpoints.iter().find(|e| e.id == endpoint_id).cloned()
            .ok_or_else(|| format!("Endpoint not found: {}", endpoint_id))?
    };

    let model = params.get("model").and_then(|v| v.as_str()).unwrap_or("sora").to_string();
    let size = params.get("size").and_then(|v| v.as_str()).unwrap_or("1920x1080").to_string();
    let duration = params.get("duration_seconds").and_then(|v| v.as_u64()).unwrap_or(10) as u32;
    let n = params.get("n").and_then(|v| v.as_u64()).map(|v| v as u32);

    let request = VideoGenRequest {
        prompt: prompt.clone(),
        model,
        endpoint_id,
        size,
        duration_seconds: duration,
        api_mode: None,
        reference_image_path: reference_path,
        n,
    };

    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let round_id = round.id.clone();
    let db = state.db.clone();
    let sink: Arc<dyn tfp_core::EventSink> = Arc::new(crate::tauri_event_sink::TauriEventSink::new(app));
    let task_id_ret = task_id.clone();
    let round_id_ret = round.id.clone();

    tokio::spawn(async move {
        let provider = OpenAiVideoProvider::new(ep);
        tfp_media::center_service::run_center_video_round(
            &db, sink.as_ref(), &provider,
            &task_id, &workspace_id, &round_id, &prompt, request, duration, &data_dir,
        ).await;
    });

    Ok(serde_json::json!({
        "task_id": task_id_ret,
        "round_id": round_id_ret,
    }))
}

// ── Asset management (3) ──

#[tauri::command]
pub async fn center_select_assets(
    state: State<'_, AppState>,
    round_id: String,
    asset_ids: Vec<String>,
    selected: bool,
) -> Result<(), String> {
    state.db.center_select_assets(&round_id, &asset_ids, selected)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn center_delete_assets(
    state: State<'_, AppState>,
    asset_ids: Vec<String>,
) -> Result<(), String> {
    state.db.center_delete_assets(&asset_ids)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn center_export_assets(
    state: State<'_, AppState>,
    asset_ids: Vec<String>,
    dest_dir: String,
) -> Result<ExportResult, String> {
    let mut copied: i64 = 0;
    let mut failed: i64 = 0;

    let dest = std::path::Path::new(&dest_dir);
    if !dest.exists() {
        std::fs::create_dir_all(dest).map_err(|e| format!("Failed to create directory: {e}"))?;
    }

    for aid in &asset_ids {
        let file_path = state.db.center_get_asset_path(aid).await;
        match file_path {
            Ok(Some(src)) => {
                let src_path = std::path::Path::new(&src);
                if src_path.exists() {
                    let file_name = src_path.file_name().unwrap_or_default();
                    let dest_path = dest.join(file_name);
                    match std::fs::copy(src_path, &dest_path) {
                        Ok(_) => copied += 1,
                        Err(_) => failed += 1,
                    }
                } else {
                    failed += 1;
                }
            }
            _ => failed += 1,
        }
    }

    Ok(ExportResult { copied, failed })
}

// ── Queries (2) ──

#[tauri::command]
pub async fn center_list_running_tasks(
    state: State<'_, AppState>,
    workspace_id: String,
) -> Result<Vec<StudioTask>, String> {
    state.db.studio_list_running_tasks(&workspace_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn center_get_round_assets(
    state: State<'_, AppState>,
    round_id: String,
) -> Result<Vec<CenterAssetDetail>, String> {
    state.db.center_get_round_assets(&round_id)
        .await
        .map_err(|e| e.to_string())
}

// ── Mode & derivation commands (T-002, T-003, T-004) ──

/// Update workspace canvas_mode and media_kind.
#[tauri::command]
pub async fn center_update_workspace_mode(
    state: State<'_, AppState>,
    id: String,
    canvas_mode: String,
    media_kind: String,
) -> Result<(), String> {
    // Validate canvas_mode
    if !["", "draw", "edit"].contains(&canvas_mode.as_str()) {
        return Err(format!("Invalid canvas_mode '{}': must be '', 'draw', or 'edit'", canvas_mode));
    }
    // Validate media_kind
    if !["", "image", "video"].contains(&media_kind.as_str()) {
        return Err(format!("Invalid media_kind '{}': must be '', 'image', or 'video'", media_kind));
    }
    state.db.center_update_workspace_mode(&id, &canvas_mode, &media_kind)
        .await
        .map_err(|e| e.to_string())
}

/// Derive a new workspace from an existing asset (EditAsset flow).
#[tauri::command]
pub async fn center_derive_workspace(
    state: State<'_, AppState>,
    source_workspace_id: String,
    source_asset_id: String,
    kind: String,
    name: String,
    reference_file_path: String,
) -> Result<CenterWorkspace, String> {
    // Validate kind
    if kind != "image" && kind != "video" {
        return Err("kind must be 'image' or 'video'".to_string());
    }
    state.db.center_derive_workspace(
        &source_workspace_id, &source_asset_id, &kind, &name, &reference_file_path,
    ).await.map_err(|e| e.to_string())
}

/// Get all assets across all rounds in a workspace.
#[tauri::command]
pub async fn center_get_all_assets(
    state: State<'_, AppState>,
    workspace_id: String,
    limit: Option<i64>,
) -> Result<Vec<CenterAssetDetail>, String> {
    state.db.center_get_all_assets(&workspace_id, limit.unwrap_or(200))
        .await
        .map_err(|e| e.to_string())
}

// ── Shell operations (T-001) ──

/// Open a file with the system's default application.
#[tauri::command]
pub async fn center_open_file(path: String) -> Result<(), String> {
    let p = Path::new(&path);
    if !p.exists() {
        return Err(format!("File not found: {}", path));
    }
    open::that(&path).map_err(|e| format!("Failed to open file: {e}"))
}

/// Reveal a file in the system file explorer (select it).
#[tauri::command]
pub async fn center_reveal_in_explorer(path: String) -> Result<(), String> {
    let p = Path::new(&path);
    if !p.exists() {
        return Err(format!("Path not found: {}", path));
    }

    #[cfg(target_os = "windows")]
    {
        std::process::Command::new("explorer.exe")
            .args(["/select,", &path])
            .spawn()
            .map_err(|e| format!("Failed to reveal in explorer: {e}"))?;
    }
    #[cfg(target_os = "macos")]
    {
        std::process::Command::new("open")
            .args(["-R", &path])
            .spawn()
            .map_err(|e| format!("Failed to reveal in Finder: {e}"))?;
    }
    #[cfg(target_os = "linux")]
    {
        let parent = p.parent().unwrap_or(p);
        open::that(parent.to_string_lossy().as_ref())
            .map_err(|e| format!("Failed to open directory: {e}"))?;
    }

    Ok(())
}

// ── Export workspace (T-006) ──

/// Export an entire workspace with per-round subdirectories and optional metadata.
#[tauri::command]
pub async fn center_export_workspace(
    state: State<'_, AppState>,
    workspace_id: String,
    dest_dir: String,
    include_metadata: bool,
) -> Result<ExportResult, String> {
    let dest = Path::new(&dest_dir);
    if !dest.exists() {
        std::fs::create_dir_all(dest).map_err(|e| format!("Failed to create directory: {e}"))?;
    }

    let bundle = state.db.center_get_workspace_bundle(&workspace_id)
        .await
        .map_err(|e| e.to_string())?;

    let mut copied: i64 = 0;
    let mut failed: i64 = 0;

    // For each round, create subdirectory and copy assets
    for rps in &bundle.round_prompts {
        let round_assets = state.db.center_get_round_assets(&rps.round_id)
            .await
            .map_err(|e| e.to_string())?;

        let preview = rps.prompt_preview.chars().take(8).collect::<String>()
            .replace(['/', '\\', ':', '*', '?', '"', '<', '>', '|'], "_");
        let dir_name = format!("round_{}_{}", rps.round_index, preview);
        let round_dir = dest.join(&dir_name);
        std::fs::create_dir_all(&round_dir)
            .map_err(|e| format!("Failed to create round directory: {e}"))?;

        for asset in &round_assets {
            let src_path = Path::new(&asset.file_path);
            if src_path.exists() {
                let file_name = src_path.file_name().unwrap_or_default();
                match std::fs::copy(src_path, round_dir.join(file_name)) {
                    Ok(_) => copied += 1,
                    Err(_) => failed += 1,
                }
            } else {
                failed += 1;
            }
        }

        if include_metadata {
            // Retrieve full round info
            let round_info = state.db.center_get_round(&rps.round_id).await.ok().flatten();
            let meta = serde_json::json!({
                "prompt": rps.prompt_preview,
                "model": round_info.as_ref().map(|r| r.model_ref.as_str()).unwrap_or(""),
                "params": round_info.as_ref().and_then(|r| serde_json::from_str::<serde_json::Value>(&r.params_json).ok()).unwrap_or(serde_json::Value::Null),
                "status": rps.status,
                "created_at": rps.created_at,
                "assets": round_assets.iter().map(|a| serde_json::json!({
                    "id": a.asset_id,
                    "kind": a.kind,
                    "file_name": Path::new(&a.file_path).file_name().unwrap_or_default().to_string_lossy(),
                    "width": a.width,
                    "height": a.height,
                })).collect::<Vec<_>>(),
            });
            let meta_path = round_dir.join("meta.json");
            let _ = std::fs::write(&meta_path, serde_json::to_string_pretty(&meta).unwrap_or_default());
        }
    }

    // Write workspace.json at root
    if include_metadata {
        let ws_meta = serde_json::json!({
            "workspace_name": bundle.workspace.name,
            "created_at": bundle.workspace.created_at,
            "canvas_mode": bundle.workspace.canvas_mode,
            "media_kind": bundle.workspace.media_kind,
            "round_count": bundle.round_prompts.len(),
            "total_assets": copied + failed,
            "source_session_name": bundle.workspace.source_session_name,
            "source_asset_file_name": bundle.workspace.source_asset_file_name,
        });
        let ws_meta_path = dest.join("workspace.json");
        let _ = std::fs::write(&ws_meta_path, serde_json::to_string_pretty(&ws_meta).unwrap_or_default());
    }

    Ok(ExportResult { copied, failed })
}

// ── Static data (1) ──

#[tauri::command]
pub async fn video_get_capabilities() -> Result<Vec<VideoCapabilityEntry>, String> {
    Ok(vec![
        VideoCapabilityEntry {
            aspect_ratio: "16:9".to_string(),
            resolution: "480p".to_string(),
            duration_seconds: vec![5, 10, 15, 20],
            max_count: 4,
        },
        VideoCapabilityEntry {
            aspect_ratio: "16:9".to_string(),
            resolution: "720p".to_string(),
            duration_seconds: vec![5, 10, 15, 20],
            max_count: 4,
        },
        VideoCapabilityEntry {
            aspect_ratio: "16:9".to_string(),
            resolution: "1080p".to_string(),
            duration_seconds: vec![5, 10],
            max_count: 2,
        },
        VideoCapabilityEntry {
            aspect_ratio: "9:16".to_string(),
            resolution: "480p".to_string(),
            duration_seconds: vec![5, 10, 15, 20],
            max_count: 4,
        },
        VideoCapabilityEntry {
            aspect_ratio: "9:16".to_string(),
            resolution: "720p".to_string(),
            duration_seconds: vec![5, 10, 15, 20],
            max_count: 4,
        },
        VideoCapabilityEntry {
            aspect_ratio: "9:16".to_string(),
            resolution: "1080p".to_string(),
            duration_seconds: vec![5, 10],
            max_count: 2,
        },
        VideoCapabilityEntry {
            aspect_ratio: "1:1".to_string(),
            resolution: "480p".to_string(),
            duration_seconds: vec![5, 10, 15, 20],
            max_count: 4,
        },
        VideoCapabilityEntry {
            aspect_ratio: "1:1".to_string(),
            resolution: "720p".to_string(),
            duration_seconds: vec![5, 10, 15, 20],
            max_count: 4,
        },
        VideoCapabilityEntry {
            aspect_ratio: "1:1".to_string(),
            resolution: "1080p".to_string(),
            duration_seconds: vec![5, 10],
            max_count: 2,
        },
    ])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_validate_workspace_kind_valid() {
        assert_eq!(validate_workspace_kind("canvas_image"), Ok(()));
        assert_eq!(validate_workspace_kind("canvas_video"), Ok(()));
    }

    #[test]
    fn test_validate_workspace_kind_invalid() {
        assert!(validate_workspace_kind("image").is_err());
        assert!(validate_workspace_kind("").is_err());
    }

    #[tokio::test]
    async fn test_video_capabilities_count() {
        let result = video_get_capabilities().await;
        assert!(result.is_ok());
        assert_eq!(result.unwrap().len(), 9);
    }

    #[tokio::test]
    async fn test_video_capabilities_aspect_ratios() {
        let caps = video_get_capabilities().await.unwrap();
        let mut ratios: Vec<&str> = caps.iter().map(|c| c.aspect_ratio.as_str()).collect();
        ratios.sort();
        ratios.dedup();
        assert_eq!(ratios, vec!["16:9", "1:1", "9:16"]);
    }

    #[tokio::test]
    async fn test_center_open_file_nonexistent() {
        let result = center_open_file("/nonexistent/path/to/file.png".to_string()).await;
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("File not found"));
    }

    #[tokio::test]
    async fn test_center_reveal_in_explorer_nonexistent() {
        let result = center_reveal_in_explorer("/nonexistent/path/to/dir".to_string()).await;
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("Path not found"));
    }

    #[tokio::test]
    async fn test_center_open_file_existing() {
        // Create a temp file and verify open doesn't error for path validation
        let tmp = std::env::temp_dir().join("tfp_test_open_file.txt");
        std::fs::write(&tmp, "test").unwrap();
        // We can't truly test open (it opens a window), but path validation passes
        let result = center_open_file(tmp.to_string_lossy().to_string()).await;
        // open::that may succeed or fail depending on env, but path is valid
        // We just check it doesn't return "File not found"
        if let Err(ref e) = result {
            assert!(!e.contains("File not found"));
        }
        let _ = std::fs::remove_file(&tmp);
    }
}
