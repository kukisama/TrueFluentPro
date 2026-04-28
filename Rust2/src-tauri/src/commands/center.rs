use tauri::{Emitter, State};
use tauri::Manager;
use crate::models::*;
use crate::state::AppState;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  媒体中心命令（对齐 C# ICreativeSessionRepository + ISessionContentRepository）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

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
    // 校验 kind
    if kind != "canvas_image" && kind != "canvas_video" {
        return Err("kind must be 'canvas_image' or 'canvas_video'".to_string());
    }
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

/// 启动图片生成 round: 创建 round → 创建 task → 返回 (task_id, round_id)
#[tauri::command]
pub async fn center_start_image_round(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    workspace_id: String,
    prompt: String,
    params: serde_json::Value,
    reference_paths: Vec<String>,
) -> Result<serde_json::Value, String> {
    let params_json = serde_json::to_string(&params).unwrap_or_else(|_| "{}".to_string());
    let model_ref = params.get("model").and_then(|v| v.as_str()).unwrap_or("").to_string();

    // 创建 round
    let round = state.db.center_create_round(&workspace_id, &prompt, &params_json, &model_ref)
        .await
        .map_err(|e| e.to_string())?;

    // 创建 studio_task
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

    // 获取 provider Arc（在 spawn 之前）
    let endpoint_id = params.get("endpoint_id").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let provider = {
        let reg = state.providers.read().await;
        reg.get_image_gen(&endpoint_id)
    };
    let provider = match provider {
        Some(p) => p,
        None => return Err(format!("未找到图片生成 Provider: {}", endpoint_id)),
    };

    let round_id = round.id.clone();
    let db = state.db.clone();
    let data_dir = app.path().app_data_dir().map_err(|e| e.to_string())?;
    let app_handle = app.clone();
    let task_id_ret = task_id.clone();
    let round_id_ret = round.id.clone();

    tokio::spawn(async move {
        let start = std::time::Instant::now();

        // 从 params 提取图片参数
        let width = params.get("width").and_then(|v| v.as_u64()).unwrap_or(1024) as u32;
        let height = params.get("height").and_then(|v| v.as_u64()).unwrap_or(1024) as u32;
        let quality = params.get("quality").and_then(|v| v.as_str()).unwrap_or("auto").to_string();
        let n = params.get("n").and_then(|v| v.as_u64()).unwrap_or(1) as u32;
        let output_format = params.get("output_format").and_then(|v| v.as_str()).unwrap_or("png").to_string();
        let background = params.get("background").and_then(|v| v.as_str()).map(|s| s.to_string());
        let model = params.get("model").and_then(|v| v.as_str()).unwrap_or("gpt-image-2").to_string();

        // 更新任务为 running
        let _ = db.studio_update_task_status(&task_id, "running", None).await;
        let _ = app_handle.emit("center-task-update", serde_json::json!({
            "task_id": &task_id,
            "session_id": &workspace_id,
            "round_id": &round_id,
            "status": "running",
            "progress": 0.1,
        }));

        let request = ImageGenRequest {
            prompt: prompt.clone(),
            width,
            height,
            model: model.clone(),
            quality: Some(quality),
            n: Some(n),
            output_format: Some(output_format.clone()),
            background,
            endpoint_id: endpoint_id.clone(),
            text_model: None,
            image_model: None,
            previous_response_id: None,
        };

        let result = provider.generate(&request).await;

        match result {
            Ok(results) => {
                let elapsed = start.elapsed().as_secs_f64();
                let _ = db.center_update_round_status(&round_id, "completed").await;

                // 保存每张图片到文件 + 数据库
                let img_dir = data_dir.join("images");
                let _ = std::fs::create_dir_all(&img_dir);

                let mut asset_ids = Vec::new();
                for (idx, r) in results.iter().enumerate() {
                    if let Some(ref b64) = r.base64 {
                        use base64::Engine;
                        if let Ok(bytes) = base64::engine::general_purpose::STANDARD.decode(b64) {
                            let ext = match output_format.as_str() {
                                "jpeg" | "jpg" => "jpg",
                                "webp" => "webp",
                                _ => "png",
                            };
                            let file_name = format!("center_{}_{}.{}", &round_id[..8], idx, ext);
                            let file_path = img_dir.join(&file_name);
                            if std::fs::write(&file_path, &bytes).is_ok() {
                                let asset_id = uuid::Uuid::new_v4().to_string();
                                let now2 = chrono::Utc::now().to_rfc3339();
                                let asset = StudioAsset {
                                    asset_id: asset_id.clone(),
                                    session_id: workspace_id.clone(),
                                    group_id: round_id.clone(),
                                    kind: "image".to_string(),
                                    workflow: "generation".to_string(),
                                    file_name: file_name.clone(),
                                    file_path: file_path.to_string_lossy().to_string(),
                                    preview_path: file_path.to_string_lossy().to_string(),
                                    prompt_text: prompt.clone(),
                                    file_size: Some(bytes.len() as i64),
                                    mime_type: Some(format!("image/{}", ext)),
                                    width: Some(width as i64),
                                    height: Some(height as i64),
                                    duration_ms: None,
                                    created_at: now2.clone(),
                                    modified_at: now2,
                                    storage_scope: "workspace-relative".to_string(),
                                    derived_from_session_id: None,
                                    derived_from_session_name: None,
                                    derived_from_asset_id: None,
                                    derived_from_asset_file_name: None,
                                    derived_from_asset_kind: None,
                                    derived_from_reference_role: None,
                                };
                                let _ = db.studio_insert_asset(&asset).await;
                                let _ = db.center_add_round_asset(&round_id, &asset_id, idx as i64).await;
                                asset_ids.push(asset_id);
                            }
                        }
                    }
                }

                let _ = db.studio_update_task_result(&task_id, "", Some(elapsed), None).await;
                let _ = app_handle.emit("center-task-update", serde_json::json!({
                    "task_id": &task_id,
                    "session_id": &workspace_id,
                    "round_id": &round_id,
                    "status": "completed",
                    "progress": 1.0,
                    "asset_ids": &asset_ids,
                    "elapsed_seconds": elapsed,
                }));
            }
            Err(e) => {
                let _ = db.center_update_round_status(&round_id, "failed").await;
                let _ = db.studio_update_task_status(&task_id, "failed", Some(&e.to_string())).await;
                let _ = app_handle.emit("center-task-update", serde_json::json!({
                    "task_id": &task_id,
                    "session_id": &workspace_id,
                    "round_id": &round_id,
                    "status": "failed",
                    "error": e.to_string(),
                }));
            }
        }
    });

    Ok(serde_json::json!({
        "task_id": task_id_ret,
        "round_id": round_id_ret,
    }))
}

/// 启动视频生成 round
#[tauri::command]
pub async fn center_start_video_round(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    workspace_id: String,
    prompt: String,
    params: serde_json::Value,
    reference_path: Option<String>,
) -> Result<serde_json::Value, String> {
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

    let round_id = round.id.clone();
    let db = state.db.clone();
    let app_handle = app.clone();
    let task_id_ret = task_id.clone();
    let round_id_ret = round.id.clone();

    tokio::spawn(async move {
        let _ = db.studio_update_task_status(&task_id, "running", None).await;
        let _ = db.center_update_round_status(&round_id, "running").await;
        let _ = app_handle.emit("center-task-update", serde_json::json!({
            "task_id": &task_id,
            "session_id": &workspace_id,
            "round_id": &round_id,
            "status": "running",
            "progress": 0.1,
        }));

        let _ = db.studio_update_task_status(&task_id, "failed", Some("视频生成请通过 generate_video 命令触发")).await;
        let _ = db.center_update_round_status(&round_id, "failed").await;
        let _ = app_handle.emit("center-task-update", serde_json::json!({
            "task_id": &task_id,
            "session_id": &workspace_id,
            "round_id": &round_id,
            "status": "failed",
            "error": "视频生成通道搭建中",
        }));
    });

    Ok(serde_json::json!({
        "task_id": task_id_ret,
        "round_id": round_id_ret,
    }))
}

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
        std::fs::create_dir_all(dest).map_err(|e| format!("创建目录失败: {e}"))?;
    }

    // 获取每个 asset 的文件路径并复制
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

/// 获取视频能力组合表（对齐 C# VideoCapabilityResolver）
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
