use std::path::Path;
use std::sync::Arc;

use tfp_core::{
    EventSink, ImageGenRequest, StudioAsset,
    VideoGenRequest,
};
use tfp_providers::{ImageGenSlot, OpenAiVideoProvider, VideoGenSlot};
use tfp_storage::Database;

/// Background body for center_start_image_round.
pub async fn run_center_image_round(
    db: &Database,
    sink: &dyn EventSink,
    provider: Arc<dyn ImageGenSlot>,
    task_id: &str,
    workspace_id: &str,
    round_id: &str,
    prompt: &str,
    request: ImageGenRequest,
    output_format: &str,
    data_dir: &Path,
) {
    let start = std::time::Instant::now();

    let _ = db.studio_update_task_status(task_id, "running", None).await;
    sink.emit_json("center-task-update", serde_json::json!({
        "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
        "status": "running", "progress": 0.1,
    }));

    let result = provider.generate(&request).await;

    match result {
        Ok(results) => {
            let elapsed = start.elapsed().as_secs_f64();
            let _ = db.center_update_round_status(round_id, "completed").await;

            let img_dir = data_dir.join("center_images");
            let _ = std::fs::create_dir_all(&img_dir);

            let width = request.width;
            let height = request.height;
            let mut asset_ids = Vec::new();

            for (idx, r) in results.iter().enumerate() {
                if let Some(ref b64) = r.base64 {
                    use base64::Engine;
                    if let Ok(bytes) = base64::engine::general_purpose::STANDARD.decode(b64) {
                        let ext = match output_format {
                            "jpeg" | "jpg" => "jpg",
                            "webp" => "webp",
                            _ => "png",
                        };
                        let file_name = format!("center_{}_{}.{}", &round_id[..8.min(round_id.len())], idx, ext);
                        let file_path = img_dir.join(&file_name);
                        if std::fs::write(&file_path, &bytes).is_ok() {
                            let asset_id = uuid::Uuid::new_v4().to_string();
                            let now2 = chrono::Utc::now().to_rfc3339();
                            let asset = StudioAsset {
                                asset_id: asset_id.clone(),
                                session_id: workspace_id.to_string(),
                                group_id: round_id.to_string(),
                                kind: "image".to_string(),
                                workflow: "generation".to_string(),
                                file_name: file_name.clone(),
                                file_path: file_path.to_string_lossy().to_string(),
                                preview_path: file_path.to_string_lossy().to_string(),
                                prompt_text: prompt.to_string(),
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
                            let _ = db.center_add_round_asset(round_id, &asset_id, idx as i64).await;
                            asset_ids.push(asset_id);
                        }
                    }
                }
            }

            let _ = db.studio_update_task_result(task_id, "", Some(elapsed), None).await;
            sink.emit_json("center-task-update", serde_json::json!({
                "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
                "status": "completed", "progress": 1.0,
                "asset_ids": asset_ids, "elapsed_seconds": elapsed,
            }));
        }
        Err(e) => {
            let _ = db.center_update_round_status(round_id, "failed").await;
            let _ = db.studio_update_task_status(task_id, "failed", Some(&e.to_string())).await;
            sink.emit_json("center-task-update", serde_json::json!({
                "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
                "status": "failed", "error": e.to_string(),
            }));
        }
    }
}

/// Background body for center_start_video_round (3 phases: create → poll → download).
pub async fn run_center_video_round(
    db: &Database,
    sink: &dyn EventSink,
    provider: &OpenAiVideoProvider,
    task_id: &str,
    workspace_id: &str,
    round_id: &str,
    prompt: &str,
    request: VideoGenRequest,
    duration_seconds: u32,
    data_dir: &Path,
) {
    let start = std::time::Instant::now();

    let _ = db.studio_update_task_status(task_id, "running", None).await;
    let _ = db.center_update_round_status(round_id, "running").await;
    sink.emit_json("center-task-update", serde_json::json!({
        "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
        "status": "running", "progress": 0.1,
    }));

    // Phase 1: Create video task
    let gen_result = match provider.generate(&request).await {
        Ok(r) => r,
        Err(e) => {
            let err_msg = format!("create_video: {e}");
            let _ = db.studio_update_task_status(task_id, "failed", Some(&err_msg)).await;
            let _ = db.center_update_round_status(round_id, "failed").await;
            sink.emit_json("center-task-update", serde_json::json!({
                "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
                "status": "failed", "error": err_msg,
            }));
            return;
        }
    };

    let video_id = gen_result.video_id;
    let endpoint_id = &request.endpoint_id;

    // Phase 2: Poll (max 10 minutes)
    let poll_interval = std::time::Duration::from_secs(5);
    let max_polls = 120;
    let mut download_url = None;

    for poll_i in 0..max_polls {
        tokio::time::sleep(poll_interval).await;
        match provider.poll_status(&video_id, endpoint_id).await {
            Ok(result) => {
                let progress = 0.1 + (poll_i as f64 / max_polls as f64) * 0.7;
                let _ = db.studio_update_task_progress(task_id, progress).await;
                sink.emit_json("center-task-update", serde_json::json!({
                    "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
                    "status": "running", "progress": progress,
                }));

                let s = result.status.to_lowercase();
                if s == "succeeded" || s == "completed" || s == "success" {
                    download_url = result.download_url;
                    break;
                } else if s == "failed" {
                    let err_msg = "Video generation failed (remote returned failed)";
                    let _ = db.studio_update_task_status(task_id, "failed", Some(err_msg)).await;
                    let _ = db.center_update_round_status(round_id, "failed").await;
                    sink.emit_json("center-task-update", serde_json::json!({
                        "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
                        "status": "failed", "error": err_msg,
                    }));
                    return;
                }
            }
            Err(_) => continue,
        }
    }

    let dl_url = match download_url {
        Some(u) if !u.is_empty() => u,
        _ => {
            let err_msg = "Video generation timed out or no download URL";
            let _ = db.studio_update_task_status(task_id, "failed", Some(err_msg)).await;
            let _ = db.center_update_round_status(round_id, "failed").await;
            sink.emit_json("center-task-update", serde_json::json!({
                "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
                "status": "failed", "error": err_msg,
            }));
            return;
        }
    };

    // Phase 3: Download
    let vid_dir = data_dir.join("center_videos");
    let _ = std::fs::create_dir_all(&vid_dir);
    let fname = format!("vid_{}_{}.mp4", chrono::Utc::now().format("%Y%m%d_%H%M%S"), &task_id[..8.min(task_id.len())]);
    let output_path = vid_dir.join(&fname);

    match crate::video_util::download_video(&dl_url, &output_path).await {
        Ok(path_str) => {
            let gen_secs = start.elapsed().as_secs_f64();
            let _ = db.studio_update_task_result(task_id, &path_str, Some(gen_secs), None).await;
            let _ = db.center_update_round_status(round_id, "completed").await;

            let asset_id = uuid::Uuid::new_v4().to_string();
            let file_name = output_path.file_name().unwrap_or_default().to_string_lossy().to_string();
            let now2 = chrono::Utc::now().to_rfc3339();
            let file_size = std::fs::metadata(&output_path).map(|m| m.len() as i64).ok();
            let asset = StudioAsset {
                asset_id: asset_id.clone(),
                session_id: workspace_id.to_string(),
                group_id: round_id.to_string(),
                kind: "video".to_string(),
                workflow: "generation".to_string(),
                file_name,
                file_path: path_str.clone(),
                preview_path: String::new(),
                prompt_text: prompt.to_string(),
                file_size,
                mime_type: Some("video/mp4".to_string()),
                width: None,
                height: None,
                duration_ms: Some(duration_seconds as i64 * 1000),
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
            let _ = db.center_add_round_asset(round_id, &asset_id, 0).await;

            sink.emit_json("center-task-update", serde_json::json!({
                "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
                "status": "completed", "progress": 1.0,
                "asset_ids": [&asset_id], "elapsed_seconds": gen_secs,
            }));
        }
        Err(e) => {
            let err_msg = format!("download: {e}");
            let _ = db.studio_update_task_status(task_id, "failed", Some(&err_msg)).await;
            let _ = db.center_update_round_status(round_id, "failed").await;
            sink.emit_json("center-task-update", serde_json::json!({
                "task_id": task_id, "session_id": workspace_id, "round_id": round_id,
                "status": "failed", "error": err_msg,
            }));
        }
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn test_center_service_module_compiles() {
        assert!(true);
    }
}
