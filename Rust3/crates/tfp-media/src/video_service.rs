use std::path::Path;

use tfp_core::EventSink;
use tfp_providers::{OpenAiVideoProvider, VideoGenSlot};
use tfp_core::VideoGenRequest;

/// Background body for the standalone `generate_video` command.
/// Emits "video-progress" events with structured JSON through the sink.
pub async fn run_video_generation(
    sink: &dyn EventSink,
    provider: &OpenAiVideoProvider,
    endpoint_id: &str,
    request: VideoGenRequest,
    task_id: &str,
    data_dir: &Path,
) {
    let start = std::time::Instant::now();

    // Phase 1: Create video
    sink.emit_json("video-progress", serde_json::json!({
        "task_id": task_id, "status": "creating",
        "message": "Submitting video generation request...",
        "video_id": null, "file_path": null,
        "elapsed_seconds": start.elapsed().as_secs_f64(), "error": null,
    }));

    let gen_result = match provider.generate(&request).await {
        Ok(r) => r,
        Err(e) => {
            sink.emit_json("video-progress", serde_json::json!({
                "task_id": task_id, "status": "error",
                "message": format!("Video creation failed: {e}"),
                "video_id": null, "file_path": null,
                "elapsed_seconds": start.elapsed().as_secs_f64(),
                "error": e.to_string(),
            }));
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
            sink.emit_json("video-progress", serde_json::json!({
                "task_id": task_id, "status": "timeout",
                "message": "Video generation timed out after 10 minutes",
                "video_id": &video_id, "file_path": null,
                "elapsed_seconds": start.elapsed().as_secs_f64(),
                "error": "Timeout",
            }));
            return;
        }

        tokio::time::sleep(poll_interval).await;

        sink.emit_json("video-progress", serde_json::json!({
            "task_id": task_id, "status": "polling",
            "message": format!("Checking status... ({:.0}s elapsed)", start.elapsed().as_secs_f64()),
            "video_id": &video_id, "file_path": null,
            "elapsed_seconds": start.elapsed().as_secs_f64(), "error": null,
        }));

        match provider.poll_status(&video_id, endpoint_id).await {
            Ok(poll) => {
                consecutive_failures = 0;
                match poll.status.as_str() {
                    "completed" | "succeeded" => {
                        if let Some(dl_url) = &poll.download_url {
                            match download_video_file(dl_url, data_dir, task_id).await {
                                Ok(file_path) => {
                                    sink.emit_json("video-progress", serde_json::json!({
                                        "task_id": task_id, "status": "completed",
                                        "message": "Video saved successfully",
                                        "video_id": &video_id,
                                        "file_path": file_path,
                                        "elapsed_seconds": start.elapsed().as_secs_f64(),
                                        "error": null,
                                    }));
                                }
                                Err(e) => {
                                    sink.emit_json("video-progress", serde_json::json!({
                                        "task_id": task_id, "status": "error",
                                        "message": format!("Download failed: {e}"),
                                        "video_id": &video_id, "file_path": null,
                                        "elapsed_seconds": start.elapsed().as_secs_f64(),
                                        "error": e.to_string(),
                                    }));
                                }
                            }
                        } else {
                            sink.emit_json("video-progress", serde_json::json!({
                                "task_id": task_id, "status": "completed",
                                "message": "Video completed (no download URL)",
                                "video_id": &video_id, "file_path": null,
                                "elapsed_seconds": start.elapsed().as_secs_f64(),
                                "error": null,
                            }));
                        }
                        return;
                    }
                    "failed" | "error" | "cancelled" => {
                        sink.emit_json("video-progress", serde_json::json!({
                            "task_id": task_id, "status": "error",
                            "message": format!("Video generation {}", poll.status),
                            "video_id": &video_id, "file_path": null,
                            "elapsed_seconds": start.elapsed().as_secs_f64(),
                            "error": &poll.status,
                        }));
                        return;
                    }
                    _ => { /* still in progress */ }
                }
            }
            Err(e) => {
                consecutive_failures += 1;
                if consecutive_failures >= 3 {
                    sink.emit_json("video-progress", serde_json::json!({
                        "task_id": task_id, "status": "error",
                        "message": format!("3 consecutive poll failures: {e}"),
                        "video_id": &video_id, "file_path": null,
                        "elapsed_seconds": start.elapsed().as_secs_f64(),
                        "error": e.to_string(),
                    }));
                    return;
                }
            }
        }
    }
}

async fn download_video_file(
    url: &str,
    data_dir: &Path,
    _task_id: &str,
) -> Result<String, String> {
    let videos_dir = data_dir.join("videos");
    tokio::fs::create_dir_all(&videos_dir)
        .await
        .map_err(|e| format!("Cannot create videos dir: {e}"))?;

    let uuid8 = &uuid::Uuid::new_v4().to_string()[..8];
    let now = chrono::Utc::now();
    let timestamp = format!(
        "{:04}{:02}{:02}_{:02}{:02}{:02}",
        now.year(), now.month(), now.day(),
        now.hour(), now.minute(), now.second()
    );
    let filename = format!("vid_{timestamp}_{uuid8}.mp4");
    let final_path = videos_dir.join(&filename);
    let tmp_path = videos_dir.join(format!("{filename}.tmp"));

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(300))
        .build()
        .map_err(|e| e.to_string())?;

    let resp = client.get(url).send().await
        .map_err(|e| format!("Download request failed: {e}"))?;

    if !resp.status().is_success() {
        return Err(format!("Download HTTP {}", resp.status()));
    }

    let bytes = resp.bytes().await
        .map_err(|e| format!("Read download bytes: {e}"))?;

    tokio::fs::write(&tmp_path, &bytes).await
        .map_err(|e| format!("Write tmp video: {e}"))?;
    tokio::fs::rename(&tmp_path, &final_path).await
        .map_err(|e| format!("Rename video: {e}"))?;

    Ok(final_path.to_string_lossy().to_string())
}

use chrono::{Datelike, Timelike};

#[cfg(test)]
mod tests {
    #[test]
    fn test_video_service_module_compiles() {
        assert!(true);
    }
}
