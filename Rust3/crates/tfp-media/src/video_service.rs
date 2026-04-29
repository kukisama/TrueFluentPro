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
    let mut generation_id = gen_result.generation_id.clone();

    // Phase 2: Poll for completion (5s interval, max 10 min, 3 consecutive failures -> abort)
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
                // Track generation_id from poll response if not yet known
                if generation_id.is_none() {
                    generation_id = poll.generation_id.clone();
                }

                match poll.status.as_str() {
                    "completed" | "succeeded" => {
                        if let Some(dl_url) = &poll.download_url {
                            // Build fallback URLs from download_url pattern
                            let fallbacks = build_download_fallbacks(
                                dl_url,
                                &video_id,
                                generation_id.as_deref(),
                            );

                            match download_video_file(dl_url, &fallbacks, data_dir, task_id).await {
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

/// Build simple fallback download URLs by inferring /content/video variants.
fn build_download_fallbacks(
    primary_url: &str,
    video_id: &str,
    generation_id: Option<&str>,
) -> Vec<String> {
    let mut fallbacks = Vec::new();

    // If primary URL doesn't end with /content/video, try adding it
    if !primary_url.contains("/content/video") && !primary_url.contains("/content") {
        // Looks like a direct URL (e.g. CDN), try a /content/video variant
        // by finding the job path in the URL
        if let Some(jobs_pos) = primary_url.find("/video/generations/jobs/") {
            let base = &primary_url[..jobs_pos];
            fallbacks.push(format!(
                "{base}/video/generations/jobs/{video_id}/content/video"
            ));
        }
    }

    // If we have a generation_id, add a generation-based download URL
    if let Some(gen_id) = generation_id {
        if let Some(gen_pos) = primary_url.find("/video/generations/") {
            let base = &primary_url[..gen_pos];
            fallbacks.push(format!(
                "{base}/video/generations/{gen_id}/content/video"
            ));
        }
    }

    fallbacks
}

/// Download video with fallback URL support.
/// Tries primary_url first, then each fallback_url in order.
async fn download_video_file(
    primary_url: &str,
    fallback_urls: &[String],
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

    // Build URL list: primary first, then fallbacks
    let mut all_urls = vec![primary_url.to_string()];
    all_urls.extend_from_slice(fallback_urls);

    let mut last_error = String::new();

    for url in &all_urls {
        match client.get(url).send().await {
            Ok(resp) => {
                if !resp.status().is_success() {
                    last_error = format!("HTTP {} from {url}", resp.status());
                    continue;
                }
                match resp.bytes().await {
                    Ok(bytes) => {
                        tokio::fs::write(&tmp_path, &bytes).await
                            .map_err(|e| format!("Write tmp video: {e}"))?;
                        tokio::fs::rename(&tmp_path, &final_path).await
                            .map_err(|e| format!("Rename video: {e}"))?;
                        return Ok(final_path.to_string_lossy().to_string());
                    }
                    Err(e) => {
                        last_error = format!("Read bytes from {url}: {e}");
                        continue;
                    }
                }
            }
            Err(e) => {
                last_error = format!("Request {url}: {e}");
                continue;
            }
        }
    }

    Err(format!(
        "All {} download URLs failed: {last_error}",
        all_urls.len()
    ))
}

use chrono::{Datelike, Timelike};

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_video_service_module_compiles() {
        assert!(true);
    }

    #[test]
    fn test_build_download_fallbacks_no_gen_id() {
        let fallbacks = build_download_fallbacks(
            "https://cdn.example.com/video.mp4",
            "job-1",
            None,
        );
        // CDN URL doesn't contain job paths, so no fallbacks
        assert!(fallbacks.is_empty());
    }

    #[test]
    fn test_build_download_fallbacks_with_gen_id_and_job_url() {
        let fallbacks = build_download_fallbacks(
            "https://api.example.com/v1/video/generations/jobs/job-1",
            "job-1",
            Some("gen-42"),
        );
        // Should have /content/video and generation_id variants
        assert!(fallbacks.iter().any(|u| u.contains("/content/video")));
        assert!(fallbacks.iter().any(|u| u.contains("gen-42")));
    }

    #[test]
    fn test_build_download_fallbacks_already_has_content() {
        let fallbacks = build_download_fallbacks(
            "https://api.example.com/v1/video/generations/jobs/job-1/content/video",
            "job-1",
            Some("gen-42"),
        );
        // Already has /content/video, so only gen_id variant
        assert!(fallbacks.iter().any(|u| u.contains("gen-42")));
    }
}