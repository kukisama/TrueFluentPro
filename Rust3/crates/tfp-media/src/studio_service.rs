//! Studio image & video task execution — framework-agnostic async service functions.
//!
//! Each function represents the body of a `tokio::spawn` that was previously in a Tauri command.

use std::path::Path;
use std::sync::Arc;

use tfp_core::{
    EventSink, ImageGenRequest,
    StudioMediaRef, StudioMessage, StudioTask,
    VideoGenRequest,
};
use tfp_providers::{ImageGenSlot, OpenAiVideoProvider, VideoGenSlot};
use tfp_storage::Database;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Image task inner body
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

pub async fn run_studio_image_task(
    db: &Database,
    sink: &dyn EventSink,
    provider: Arc<dyn ImageGenSlot>,
    task_id: &str,
    session_id: &str,
    _prompt: &str,
    request: ImageGenRequest,
    format: &str,
    data_dir: &Path,
) {
    let start = std::time::Instant::now();

    sink.emit_json("studio-task-update", serde_json::json!({
        "task_id": task_id, "session_id": session_id, "status": "running", "progress": 0.1,
    }));

    let result = provider.generate(&request).await;
    let gen_secs = start.elapsed().as_secs_f64();

    match result {
        Ok(images) => {
            let img_dir = data_dir.join("studio_images");
            let _ = std::fs::create_dir_all(&img_dir);

            let mut saved_paths = Vec::new();
            for (i, img) in images.iter().enumerate() {
                if let Some(b64) = &img.base64 {
                    use base64::Engine;
                    if let Ok(bytes) = base64::engine::general_purpose::STANDARD.decode(b64) {
                        let ext = match format {
                            "webp" => "webp",
                            "jpeg" | "jpg" => "jpg",
                            _ => "png",
                        };
                        let fname = format!("{}_{}.{}", &task_id[..8.min(task_id.len())], i, ext);
                        let path = img_dir.join(&fname);
                        if std::fs::write(&path, &bytes).is_ok() {
                            saved_paths.push(path.to_string_lossy().to_string());
                        }
                    }
                }
            }

            let result_path = saved_paths.first().cloned().unwrap_or_default();
            let _ = db.studio_update_task_result(task_id, &result_path, Some(gen_secs), None).await;

            // Create assistant message + media refs
            let msg_id = uuid::Uuid::new_v4().to_string();
            let seq = db.studio_get_max_sequence(session_id).await.unwrap_or(0) + 1;
            let revised = images.first().and_then(|i| i.revised_prompt.clone()).unwrap_or_default();
            let msg_text = if revised.is_empty() {
                format!("[Generated {} image(s)]", saved_paths.len())
            } else {
                revised
            };
            let msg = StudioMessage {
                id: msg_id.clone(),
                session_id: session_id.to_string(),
                sequence_no: seq,
                role: "assistant".to_string(),
                content_type: "image".to_string(),
                text: msg_text,
                reasoning_text: String::new(),
                prompt_tokens: None,
                completion_tokens: None,
                generate_seconds: Some(gen_secs),
                download_seconds: None,
                search_summary: None,
                timestamp: chrono::Utc::now().to_rfc3339(),
                is_deleted: false,
            };
            let _ = db.studio_append_message(&msg).await;

            let media_refs: Vec<StudioMediaRef> = saved_paths.iter().enumerate().map(|(i, p)| {
                StudioMediaRef {
                    id: 0,
                    message_id: msg_id.clone(),
                    media_path: p.clone(),
                    media_kind: "image".to_string(),
                    sort_order: i as i64,
                    preview_path: None,
                }
            }).collect();
            let _ = db.studio_insert_media_refs(&msg_id, &media_refs).await;

            sink.emit_json("studio-task-update", serde_json::json!({
                "task_id": task_id, "session_id": session_id, "status": "completed",
                "progress": 1.0, "result_paths": saved_paths,
            }));

            sink.emit_json("studio-message-new", serde_json::json!({
                "session_id": session_id, "message": msg, "media_refs": media_refs,
            }));
        }
        Err(e) => {
            let err_msg = e.to_string();
            let _ = db.studio_update_task_status(task_id, "failed", Some(&err_msg)).await;
            sink.emit_json("studio-task-update", serde_json::json!({
                "task_id": task_id, "session_id": session_id, "status": "failed", "error": err_msg,
            }));
        }
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Video task inner body
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

pub async fn run_studio_video_task(
    db: &Database,
    sink: &dyn EventSink,
    provider: &OpenAiVideoProvider,
    task_id: &str,
    session_id: &str,
    prompt: &str,
    request: VideoGenRequest,
    model: &str,
    endpoint_id: &str,
    task_created_at: &str,
    data_dir: &Path,
) {
    let start = std::time::Instant::now();

    sink.emit_json("studio-task-update", serde_json::json!({
        "task_id": task_id, "session_id": session_id, "status": "running", "progress": 0.1,
    }));

    // Phase 1: Create video
    let gen_result = match provider.generate(&request).await {
        Ok(r) => r,
        Err(e) => {
            let err_msg = format!("create_video: {e}");
            let _ = db.studio_update_task_status(task_id, "failed", Some(&err_msg)).await;
            sink.emit_json("studio-task-update", serde_json::json!({
                "task_id": task_id, "session_id": session_id, "status": "failed", "error": err_msg,
            }));
            return;
        }
    };

    let video_id = gen_result.video_id;

    // Persist remote_video_id for crash recovery
    let api_mode_str = crate::video_util::determine_video_api_mode(model);
    {
        let now2 = chrono::Utc::now().to_rfc3339();
        let _ = db.studio_upsert_task(&StudioTask {
            id: task_id.to_string(),
            session_id: session_id.to_string(),
            task_type: "video".to_string(),
            status: "running".to_string(),
            prompt: prompt.to_string(),
            progress: 0.2,
            result_file_path: None,
            error_message: None,
            has_reference_input: false,
            remote_video_id: Some(video_id.clone()),
            remote_video_api_mode: Some(api_mode_str.to_string()),
            remote_generation_id: None,
            remote_download_url: None,
            generate_seconds: None,
            download_seconds: None,
            created_at: task_created_at.to_string(),
            updated_at: now2,
        }).await;
    }

    // Phase 2: Poll (max 10 minutes)
    let download_url = poll_video_status(
        db, sink, provider, task_id, session_id, &video_id, endpoint_id,
    ).await;

    let dl_url = match download_url {
        Some(u) if !u.is_empty() => u,
        _ => {
            let _ = db.studio_update_task_status(task_id, "failed", Some("Video generation timed out or no download URL")).await;
            sink.emit_json("studio-task-update", serde_json::json!({
                "task_id": task_id, "session_id": session_id, "status": "failed", "error": "Timeout",
            }));
            return;
        }
    };

    // Phase 3: Download
    let dl_start = std::time::Instant::now();
    let vid_dir = data_dir.join("studio_videos");
    let _ = std::fs::create_dir_all(&vid_dir);
    let fname = format!("{}.mp4", &task_id[..8.min(task_id.len())]);
    let output_path = vid_dir.join(&fname);

    match crate::video_util::download_video(&dl_url, &output_path).await {
        Ok(path_str) => {
            let gen_secs = start.elapsed().as_secs_f64();
            let dl_secs = dl_start.elapsed().as_secs_f64();
            let _ = db.studio_update_task_result(task_id, &path_str, Some(gen_secs), Some(dl_secs)).await;

            // Create assistant message
            let msg_id = uuid::Uuid::new_v4().to_string();
            let seq = db.studio_get_max_sequence(session_id).await.unwrap_or(0) + 1;
            let msg = StudioMessage {
                id: msg_id.clone(),
                session_id: session_id.to_string(),
                sequence_no: seq,
                role: "assistant".to_string(),
                content_type: "video".to_string(),
                text: "[Video generated]".to_string(),
                reasoning_text: String::new(),
                prompt_tokens: None,
                completion_tokens: None,
                generate_seconds: Some(gen_secs),
                download_seconds: Some(dl_secs),
                search_summary: None,
                timestamp: chrono::Utc::now().to_rfc3339(),
                is_deleted: false,
            };
            let _ = db.studio_append_message(&msg).await;
            let media_refs = vec![StudioMediaRef {
                id: 0,
                message_id: msg_id.clone(),
                media_path: path_str.clone(),
                media_kind: "video".to_string(),
                sort_order: 0,
                preview_path: None,
            }];
            let _ = db.studio_insert_media_refs(&msg_id, &media_refs).await;

            sink.emit_json("studio-task-update", serde_json::json!({
                "task_id": task_id, "session_id": session_id, "status": "completed",
                "progress": 1.0, "result_path": path_str,
            }));
            sink.emit_json("studio-message-new", serde_json::json!({
                "session_id": session_id, "message": msg, "media_refs": media_refs,
            }));
        }
        Err(e) => {
            let err_msg = format!("download: {e}");
            let _ = db.studio_update_task_status(task_id, "failed", Some(&err_msg)).await;
            sink.emit_json("studio-task-update", serde_json::json!({
                "task_id": task_id, "session_id": session_id, "status": "failed", "error": err_msg,
            }));
        }
    }
}

/// Poll video generation status until done/failed/timeout
async fn poll_video_status(
    db: &Database,
    sink: &dyn EventSink,
    provider: &OpenAiVideoProvider,
    task_id: &str,
    session_id: &str,
    video_id: &str,
    endpoint_id: &str,
) -> Option<String> {
    let poll_interval = std::time::Duration::from_secs(5);
    let max_polls: usize = 120;

    for poll_i in 0..max_polls {
        tokio::time::sleep(poll_interval).await;
        match provider.poll_status(video_id, endpoint_id).await {
            Ok(result) => {
                let progress = 0.2 + (poll_i as f64 / max_polls as f64) * 0.6;
                let _ = db.studio_update_task_progress(task_id, progress).await;
                sink.emit_json("studio-task-update", serde_json::json!({
                    "task_id": task_id, "session_id": session_id, "status": "running", "progress": progress,
                }));

                let s = result.status.to_lowercase();
                if s == "succeeded" || s == "completed" || s == "success" {
                    return result.download_url;
                } else if s == "failed" {
                    let err_msg = "Video generation failed (remote returned failed)";
                    let _ = db.studio_update_task_status(task_id, "failed", Some(err_msg)).await;
                    sink.emit_json("studio-task-update", serde_json::json!({
                        "task_id": task_id, "session_id": session_id, "status": "failed", "error": err_msg,
                    }));
                    return None;
                }
            }
            Err(_) => continue,
        }
    }
    None
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Resume interrupted video tasks
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

pub async fn resume_interrupted_video_tasks(
    db: Arc<Database>,
    sink: Arc<dyn EventSink>,
    data_dir: &Path,
) {
    let tasks = match db.studio_get_interrupted_video_tasks().await {
        Ok(t) => t,
        Err(e) => {
            tracing::warn!("Failed to resume interrupted video tasks: {e}");
            return;
        }
    };

    if tasks.is_empty() { return; }
    tracing::info!("Found {} interrupted video task(s), resuming...", tasks.len());

    for task in tasks {
        let db = db.clone();
        let sink = sink.clone();
        let data_dir = data_dir.to_path_buf();
        let tid = task.id.clone();
        let sid = task.session_id.clone();
        let video_id = match &task.remote_video_id {
            Some(id) => id.clone(),
            None => continue,
        };

        tokio::spawn(async move {
            // Recover endpoint info from persisted config
            let config = {
                let cfg = db.kv_get("app_config").await.ok().flatten();
                cfg.and_then(|j| serde_json::from_str::<tfp_core::AppConfig>(&j).ok()).unwrap_or_default()
            };

            let Some(ep) = config.endpoints.first().cloned() else { return; };
            let provider = OpenAiVideoProvider::new(ep.clone());

            tracing::info!("Resuming video poll: task={}, video_id={}", tid, video_id);

            let poll_interval = std::time::Duration::from_secs(3);
            for _ in 0..200 {
                tokio::time::sleep(poll_interval).await;

                match provider.poll_status(&video_id, &ep.id).await {
                    Ok(result) => {
                        let s = result.status.to_lowercase();
                        if s == "succeeded" || s == "completed" || s == "success" {
                            if let Some(dl_url) = result.download_url {
                                if !dl_url.is_empty() {
                                    let vid_dir = data_dir.join("studio_videos");
                                    let _ = std::fs::create_dir_all(&vid_dir);
                                    let path = vid_dir.join(format!("{}.mp4", &tid[..8.min(tid.len())]));

                                    match crate::video_util::download_video(&dl_url, &path).await {
                                        Ok(path_str) => {
                                            let _ = db.studio_update_task_result(&tid, &path_str, None, None).await;
                                            sink.emit_json("studio-task-update", serde_json::json!({
                                                "task_id": tid, "session_id": sid, "status": "completed", "progress": 1.0,
                                            }));
                                        }
                                        Err(e) => {
                                            let err_msg = format!("Resume download failed: {e}");
                                            let _ = db.studio_update_task_status(&tid, "failed", Some(&err_msg)).await;
                                        }
                                    }
                                }
                            }
                            return;
                        } else if s == "failed" {
                            let _ = db.studio_update_task_status(&tid, "failed", Some("Video generation failed (resume)")).await;
                            return;
                        }
                    }
                    Err(_) => continue,
                }
            }
            let _ = db.studio_update_task_status(&tid, "failed", Some("Resume poll timed out")).await;
        });
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn test_api_mode() {
        assert_eq!(crate::video_util::determine_video_api_mode("sora"), "videos");
        assert_eq!(crate::video_util::determine_video_api_mode("other"), "sora_jobs");
    }
}
