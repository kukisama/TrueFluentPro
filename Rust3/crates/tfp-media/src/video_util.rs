use std::path::Path;

/// Determine the video API mode for a given model name.
///
/// Sora-family models use the `"videos"` endpoint; all others use `"sora_jobs"`.
pub fn determine_video_api_mode(model: &str) -> &'static str {
    let lower = model.to_lowercase();
    if lower.starts_with("sora") {
        "videos"
    } else {
        "sora_jobs"
    }
}

/// Download a video from `url` and write it to `output_path`.
///
/// Returns the output path as a string on success.
pub async fn download_video(
    url: &str,
    output_path: &Path,
) -> Result<String, String> {
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

    std::fs::write(output_path, &bytes)
        .map_err(|e| format!("Write video file: {e}"))?;

    Ok(output_path.to_string_lossy().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_api_mode_sora() {
        assert_eq!(determine_video_api_mode("sora"), "videos");
    }

    #[test]
    fn test_api_mode_sora2_variants() {
        assert_eq!(determine_video_api_mode("sora-2"), "videos");
        assert_eq!(determine_video_api_mode("sora-2-turbo"), "videos");
    }

    #[test]
    fn test_api_mode_other_models() {
        assert_eq!(determine_video_api_mode("other-model"), "sora_jobs");
        assert_eq!(determine_video_api_mode(""), "sora_jobs");
        // Note: original code is case-sensitive — "Sora" lowercased matches
        assert_eq!(determine_video_api_mode("Sora"), "videos");
    }
}
