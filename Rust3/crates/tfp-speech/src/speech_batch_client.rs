//! Speech Batch API Client — Azure Speech Batch Transcription (v3.2).
//!
//! Creates transcription jobs, polls for completion, downloads results,
//! and parses into SubtitleCue lists.

use serde_json::json;
use std::time::Duration;
use tfp_core::SubtitleCue;

use crate::batch_transcription::parse_batch_transcription;

/// Errors from the Speech Batch API client.
#[derive(Debug, thiserror::Error)]
pub enum SpeechBatchError {
    #[error("HTTP error: {0}")]
    Http(String),
    #[error("Transcription failed: {0}")]
    Failed(String),
    #[error("Timeout after {0} polls")]
    Timeout(u32),
    #[error("Parse error: {0}")]
    Parse(String),
}

impl From<SpeechBatchError> for String {
    fn from(e: SpeechBatchError) -> Self {
        e.to_string()
    }
}

/// Build the JSON body for creating a batch transcription job.
pub fn build_transcription_request_body(
    audio_sas_url: &str,
    locale: &str,
    display_name: &str,
) -> serde_json::Value {
    json!({
        "displayName": display_name,
        "locale": locale,
        "contentUrls": [audio_sas_url],
        "properties": {
            "diarizationEnabled": true,
            "wordLevelTimestampsEnabled": true
        }
    })
}

/// Extract the transcription content URL from the /files response JSON.
pub fn extract_content_url_from_files_json(files_json: &serde_json::Value) -> Option<String> {
    let values = files_json["values"].as_array()?;
    for val in values {
        if val["kind"].as_str() == Some("Transcription") {
            if let Some(url) = val["links"]["contentUrl"].as_str() {
                return Some(url.to_string());
            }
        }
    }
    None
}

/// Azure Speech Batch Transcription API client.
pub struct SpeechBatchClient;

impl SpeechBatchClient {
    /// Run a full batch transcription: create job → poll → download → parse.
    pub async fn batch_transcribe(
        region: &str,
        subscription_key: &str,
        audio_sas_url: &str,
        locale: &str,
        display_name: &str,
    ) -> Result<Vec<SubtitleCue>, SpeechBatchError> {
        let client = reqwest::Client::new();

        // Step 1: Create transcription job
        let create_url = format!(
            "https://{}.api.cognitive.microsoft.com/speechtotext/v3.2/transcriptions",
            region
        );
        let body = build_transcription_request_body(audio_sas_url, locale, display_name);

        tracing::info!("Creating batch transcription: {}", display_name);

        let resp = client
            .post(&create_url)
            .header("Ocp-Apim-Subscription-Key", subscription_key)
            .header("Content-Type", "application/json")
            .json(&body)
            .send()
            .await
            .map_err(|e| SpeechBatchError::Http(e.to_string()))?;

        let status = resp.status().as_u16();
        if status != 201 && status != 200 {
            let resp_body = resp.text().await.unwrap_or_default();
            return Err(SpeechBatchError::Http(format!(
                "Create transcription returned {}: {}",
                status, resp_body
            )));
        }

        let create_resp: serde_json::Value = resp
            .json()
            .await
            .map_err(|e| SpeechBatchError::Parse(e.to_string()))?;

        // Get the status URL from Location header or response.self
        let status_url = create_resp["self"]
            .as_str()
            .map(|s| s.to_string())
            .ok_or_else(|| SpeechBatchError::Parse("Missing 'self' URL in create response".into()))?;

        tracing::info!("Batch transcription created, polling: {}", status_url);

        // Step 2: Poll for completion
        let poll_result = Self::poll_transcription_status(
            &status_url,
            subscription_key,
            5,   // poll every 5 seconds
            120, // max 120 polls = 10 minutes
        )
        .await?;

        // Step 3: Get files URL
        let files_url = poll_result["links"]["files"]
            .as_str()
            .map(|s| s.to_string())
            .ok_or_else(|| SpeechBatchError::Parse("Missing 'files' link in status response".into()))?;

        // Step 4: Download files listing
        let files_resp = client
            .get(&files_url)
            .header("Ocp-Apim-Subscription-Key", subscription_key)
            .send()
            .await
            .map_err(|e| SpeechBatchError::Http(e.to_string()))?;

        let files_json: serde_json::Value = files_resp
            .json()
            .await
            .map_err(|e| SpeechBatchError::Parse(e.to_string()))?;

        let content_url = extract_content_url_from_files_json(&files_json)
            .ok_or_else(|| SpeechBatchError::Parse("No Transcription file found in results".into()))?;

        // Step 5: Download transcription content
        let content_resp = client
            .get(&content_url)
            .send()
            .await
            .map_err(|e| SpeechBatchError::Http(e.to_string()))?;

        let content_json = content_resp
            .text()
            .await
            .map_err(|e| SpeechBatchError::Parse(e.to_string()))?;

        // Step 6: Parse into SubtitleCues
        let options = tfp_core::BatchSubtitleSplitOptions::default();
        let cues = parse_batch_transcription(&content_json, &options);

        tracing::info!("Batch transcription complete: {} cues", cues.len());
        Ok(cues)
    }

    /// Poll the transcription status URL until it succeeds, fails, or times out.
    pub async fn poll_transcription_status(
        status_url: &str,
        subscription_key: &str,
        poll_interval_secs: u64,
        max_polls: u32,
    ) -> Result<serde_json::Value, SpeechBatchError> {
        let client = reqwest::Client::new();

        for i in 0..max_polls {
            tokio::time::sleep(Duration::from_secs(poll_interval_secs)).await;

            let resp = client
                .get(status_url)
                .header("Ocp-Apim-Subscription-Key", subscription_key)
                .send()
                .await
                .map_err(|e| SpeechBatchError::Http(e.to_string()))?;

            let json: serde_json::Value = resp
                .json()
                .await
                .map_err(|e| SpeechBatchError::Parse(e.to_string()))?;

            let status = json["status"].as_str().unwrap_or("Unknown");
            tracing::debug!("Poll {}/{}: status = {}", i + 1, max_polls, status);

            match status {
                "Succeeded" => return Ok(json),
                "Failed" => {
                    let error_msg = json["properties"]["error"]["message"]
                        .as_str()
                        .unwrap_or("Unknown error");
                    return Err(SpeechBatchError::Failed(error_msg.to_string()));
                }
                "NotStarted" | "Running" => continue,
                other => {
                    tracing::warn!("Unexpected transcription status: {}", other);
                    continue;
                }
            }
        }

        Err(SpeechBatchError::Timeout(max_polls))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn build_transcription_request_body_structure() {
        let body = build_transcription_request_body(
            "https://example.blob.core.windows.net/audio/test.wav?sv=2020-10-02&sig=abc",
            "en-US",
            "Test Job",
        );
        assert_eq!(body["displayName"], "Test Job");
        assert_eq!(body["locale"], "en-US");
        assert!(body["contentUrls"].as_array().unwrap().len() == 1);
        assert_eq!(body["properties"]["diarizationEnabled"], true);
        assert_eq!(body["properties"]["wordLevelTimestampsEnabled"], true);
    }

    #[test]
    fn extract_content_url_from_files_json_found() {
        let json = serde_json::json!({
            "values": [
                {
                    "kind": "TranscriptionReport",
                    "links": { "contentUrl": "https://example.com/report.json" }
                },
                {
                    "kind": "Transcription",
                    "links": { "contentUrl": "https://example.com/transcription.json" }
                }
            ]
        });
        let url = extract_content_url_from_files_json(&json);
        assert_eq!(url, Some("https://example.com/transcription.json".to_string()));
    }

    #[test]
    fn extract_content_url_from_files_json_not_found() {
        let json = serde_json::json!({
            "values": [
                {
                    "kind": "TranscriptionReport",
                    "links": { "contentUrl": "https://example.com/report.json" }
                }
            ]
        });
        let url = extract_content_url_from_files_json(&json);
        assert!(url.is_none());
    }

    #[test]
    fn extract_content_url_from_empty_files() {
        let json = serde_json::json!({ "values": [] });
        assert!(extract_content_url_from_files_json(&json).is_none());

        let json2 = serde_json::json!({});
        assert!(extract_content_url_from_files_json(&json2).is_none());
    }

    #[test]
    fn speech_batch_error_display() {
        let e = SpeechBatchError::Http("connection refused".into());
        assert_eq!(e.to_string(), "HTTP error: connection refused");

        let e2 = SpeechBatchError::Timeout(10);
        assert_eq!(e2.to_string(), "Timeout after 10 polls");

        let e3 = SpeechBatchError::Failed("audio too short".into());
        assert_eq!(e3.to_string(), "Transcription failed: audio too short");
    }
}
