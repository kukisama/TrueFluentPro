use tauri::{Emitter, Manager, State};
use tfp_core::VideoGenRequest;
use tfp_providers::{OpenAiVideoProvider, VideoGenSlot};

use crate::state::AppState;

#[derive(Clone, serde::Serialize)]
struct VideoProgress {
    task_id: String,
    status: String,
    message: String,
    video_id: Option<String>,
    file_path: Option<String>,
    elapsed_seconds: Option<f64>,
    error: Option<String>,
}

#[tauri::command]
pub async fn generate_video(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: VideoGenRequest,
) -> Result<String, String> {
    // Resolve endpoint
    let config = state.config.read().await;
    let endpoint = config
        .endpoints
        .iter()
        .find(|e| e.id == request.endpoint_id)
        .ok_or_else(|| format!("Endpoint not found: {}", request.endpoint_id))?
        .clone();
    drop(config);

    let task_id = uuid::Uuid::new_v4().to_string();
    let tid = task_id.clone();

    let data_dir = app
        .path()
        .app_data_dir()
        .map_err(|e| e.to_string())?;

    tauri::async_runtime::spawn(async move {
        let start = std::time::Instant::now();
        let provider = OpenAiVideoProvider::new(endpoint.clone());

        // Phase 1: Create video
        let _ = app.emit(
            "video-progress",
            VideoProgress {
                task_id: tid.clone(),
                status: "creating".into(),
                message: "Submitting video generation request...".into(),
                video_id: None,
                file_path: None,
                elapsed_seconds: Some(start.elapsed().as_secs_f64()),
                error: None,
            },
        );

        let gen_result = match provider.generate(&request).await {
            Ok(r) => r,
            Err(e) => {
                let _ = app.emit(
                    "video-progress",
                    VideoProgress {
                        task_id: tid.clone(),
                        status: "error".into(),
                        message: format!("Video creation failed: {e}"),
                        video_id: None,
                        file_path: None,
                        elapsed_seconds: Some(start.elapsed().as_secs_f64()),
                        error: Some(e.to_string()),
                    },
                );
                return;
            }
        };

        let video_id = gen_result.video_id.clone();

        // Phase 2: Poll for completion (5s interval, max 10 min, 3 consecutive failures → abort)
        let max_duration = std::time::Duration::from_secs(600);
        let poll_interval = std::time::Duration::from_secs(5);
        let mut consecutive_failures = 0u32;

        loop {
            if start.elapsed() > max_duration {
                let _ = app.emit(
                    "video-progress",
                    VideoProgress {
                        task_id: tid.clone(),
                        status: "timeout".into(),
                        message: "Video generation timed out after 10 minutes".into(),
                        video_id: Some(video_id.clone()),
                        file_path: None,
                        elapsed_seconds: Some(start.elapsed().as_secs_f64()),
                        error: Some("Timeout".into()),
                    },
                );
                return;
            }

            tokio::time::sleep(poll_interval).await;

            let _ = app.emit(
                "video-progress",
                VideoProgress {
                    task_id: tid.clone(),
                    status: "polling".into(),
                    message: format!(
                        "Checking status... ({:.0}s elapsed)",
                        start.elapsed().as_secs_f64()
                    ),
                    video_id: Some(video_id.clone()),
                    file_path: None,
                    elapsed_seconds: Some(start.elapsed().as_secs_f64()),
                    error: None,
                },
            );

            match provider.poll_status(&video_id, &endpoint.id).await {
                Ok(poll) => {
                    consecutive_failures = 0;
                    match poll.status.as_str() {
                        "completed" | "succeeded" => {
                            // Phase 3: Download video
                            if let Some(dl_url) = &poll.download_url {
                                match download_video(
                                    &endpoint,
                                    dl_url,
                                    &data_dir,
                                    &tid,
                                )
                                .await
                                {
                                    Ok(file_path) => {
                                        let _ = app.emit(
                                            "video-progress",
                                            VideoProgress {
                                                task_id: tid.clone(),
                                                status: "completed".into(),
                                                message: "Video saved successfully".into(),
                                                video_id: Some(video_id.clone()),
                                                file_path: Some(file_path),
                                                elapsed_seconds: Some(
                                                    start.elapsed().as_secs_f64(),
                                                ),
                                                error: None,
                                            },
                                        );
                                    }
                                    Err(e) => {
                                        let _ = app.emit(
                                            "video-progress",
                                            VideoProgress {
                                                task_id: tid.clone(),
                                                status: "error".into(),
                                                message: format!("Download failed: {e}"),
                                                video_id: Some(video_id.clone()),
                                                file_path: None,
                                                elapsed_seconds: Some(
                                                    start.elapsed().as_secs_f64(),
                                                ),
                                                error: Some(e.to_string()),
                                            },
                                        );
                                    }
                                }
                            } else {
                                let _ = app.emit(
                                    "video-progress",
                                    VideoProgress {
                                        task_id: tid.clone(),
                                        status: "completed".into(),
                                        message: "Video completed (no download URL)".into(),
                                        video_id: Some(video_id.clone()),
                                        file_path: None,
                                        elapsed_seconds: Some(start.elapsed().as_secs_f64()),
                                        error: None,
                                    },
                                );
                            }
                            return;
                        }
                        "failed" | "error" | "cancelled" => {
                            let _ = app.emit(
                                "video-progress",
                                VideoProgress {
                                    task_id: tid.clone(),
                                    status: "error".into(),
                                    message: format!("Video generation {}", poll.status),
                                    video_id: Some(video_id.clone()),
                                    file_path: None,
                                    elapsed_seconds: Some(start.elapsed().as_secs_f64()),
                                    error: Some(poll.status.clone()),
                                },
                            );
                            return;
                        }
                        _ => {
                            // Still in progress
                        }
                    }
                }
                Err(e) => {
                    consecutive_failures += 1;
                    if consecutive_failures >= 3 {
                        let _ = app.emit(
                            "video-progress",
                            VideoProgress {
                                task_id: tid.clone(),
                                status: "error".into(),
                                message: format!("3 consecutive poll failures: {e}"),
                                video_id: Some(video_id.clone()),
                                file_path: None,
                                elapsed_seconds: Some(start.elapsed().as_secs_f64()),
                                error: Some(e.to_string()),
                            },
                        );
                        return;
                    }
                }
            }
        }
    });

    Ok(task_id)
}

async fn download_video(
    _endpoint: &tfp_core::AiEndpoint,
    url: &str,
    data_dir: &std::path::Path,
    _task_id: &str,
) -> Result<String, String> {
    let videos_dir = data_dir.join("videos");
    tokio::fs::create_dir_all(&videos_dir)
        .await
        .map_err(|e| format!("Cannot create videos dir: {e}"))?;

    let timestamp = super::media::format_timestamp_for_filename();
    let uuid8 = &uuid::Uuid::new_v4().to_string()[..8];
    let filename = format!("vid_{timestamp}_{uuid8}.mp4");
    let final_path = videos_dir.join(&filename);
    let tmp_path = videos_dir.join(format!("{filename}.tmp"));

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(300))
        .build()
        .map_err(|e| e.to_string())?;

    let resp = client
        .get(url)
        .send()
        .await
        .map_err(|e| format!("Download request failed: {e}"))?;

    if !resp.status().is_success() {
        return Err(format!("Download HTTP {}", resp.status()));
    }

    let bytes = resp
        .bytes()
        .await
        .map_err(|e| format!("Read download bytes: {e}"))?;

    tokio::fs::write(&tmp_path, &bytes)
        .await
        .map_err(|e| format!("Write tmp video: {e}"))?;
    tokio::fs::rename(&tmp_path, &final_path)
        .await
        .map_err(|e| format!("Rename video: {e}"))?;

    Ok(final_path.to_string_lossy().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_video_progress_json_fields() {
        let p = VideoProgress {
            task_id: "t1".into(),
            status: "polling".into(),
            message: "Checking...".into(),
            video_id: Some("vid-1".into()),
            file_path: None,
            elapsed_seconds: Some(12.5),
            error: None,
        };
        let v = serde_json::to_value(&p).unwrap();
        let obj = v.as_object().unwrap();
        assert_eq!(obj.len(), 7);
        assert_eq!(obj["task_id"], "t1");
        assert_eq!(obj["status"], "polling");
        assert_eq!(obj["video_id"], "vid-1");
        assert!(obj["file_path"].is_null());
        assert_eq!(obj["elapsed_seconds"], 12.5);
    }
}
