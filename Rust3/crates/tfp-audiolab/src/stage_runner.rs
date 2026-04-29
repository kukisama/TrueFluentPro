//! Stage runner — executes AudioLab stage generation using AI completion.
//!
//! Takes a stage key + transcript, builds the prompt, calls the AI provider,
//! and streams results back via events.

use std::sync::Arc;
use tfp_core::{AudioSegment, AudioStageOutput};
use tfp_providers::traits::AiCompletionSlot;
use tfp_storage::Database;

use crate::prompts::{build_stage_prompt, stage_system_prompt};

/// Run a stage generation task.
///
/// 1. Load transcript segments from DB
/// 2. Build prompt from stage_key + transcript text
/// 3. Call AI completion (streaming)
/// 4. Accumulate response → update stage output in DB
///
/// Returns the generated markdown content.
pub async fn run_stage(
    db: &Database,
    session_id: &str,
    stage_key: &str,
    custom_prompt: Option<&str>,
    ai_provider: Arc<dyn AiCompletionSlot>,
) -> Result<String, String> {
    // 1. Load transcript
    let transcript = db
        .audiolab_get_transcript(session_id)
        .await
        .map_err(|e| e.to_string())?
        .ok_or_else(|| "No transcript available for this session".to_string())?;

    let segments = db
        .audiolab_get_segments(&transcript.id)
        .await
        .map_err(|e| e.to_string())?;

    if segments.is_empty() {
        return Err("Transcript has no segments".to_string());
    }

    let transcript_text = segments_to_text(&segments);

    // 2. Build prompt
    let system_prompt = stage_system_prompt(stage_key).to_string();
    let user_prompt = build_stage_prompt(stage_key, &transcript_text, custom_prompt);

    // 3. Call AI completion (non-streaming for simplicity)
    let request = tfp_core::CompletionRequest {
        messages: vec![
            tfp_core::ChatMessage {
                role: "system".to_string(),
                content: serde_json::Value::String(system_prompt),
            },
            tfp_core::ChatMessage {
                role: "user".to_string(),
                content: serde_json::Value::String(user_prompt),
            },
        ],
        model: String::new(),
        temperature: Some(0.7),
        max_tokens: Some(4096),
        endpoint_id: String::new(),
        reasoning_effort: None,
        enable_image_generation: false,
        image_model_deployment: None,
        image_size: None,
        image_quality: None,
    };

    let response = ai_provider
        .complete(&request)
        .await
        .map_err(|e| format!("AI completion failed: {e}"))?;

    let content = response.content;

    // 4. Update stage output in DB
    let outputs = db
        .audiolab_get_stage_outputs(session_id)
        .await
        .map_err(|e| e.to_string())?;

    if let Some(existing) = outputs.iter().find(|o| o.stage_key == stage_key) {
        let mut updated = existing.clone();
        updated.content_markdown = content.clone();
        updated.status = "Ready".to_string();
        updated.error_message = None;
        db.audiolab_upsert_stage_output(&updated)
            .await
            .map_err(|e| e.to_string())?;
    } else {
        let output = AudioStageOutput {
            id: uuid::Uuid::new_v4().to_string(),
            session_id: session_id.to_string(),
            stage_key: stage_key.to_string(),
            content_markdown: content.clone(),
            status: "Ready".to_string(),
            error_message: None,
            model_ref: None,
            generated_at: Some(chrono::Utc::now().to_rfc3339()),
            custom_stage_key: None,
            custom_is_mindmap: None,
        };
        db.audiolab_upsert_stage_output(&output)
            .await
            .map_err(|e| e.to_string())?;
    }

    Ok(content)
}

/// Mark a stage as failed.
pub async fn mark_stage_error(
    db: &Database,
    session_id: &str,
    stage_key: &str,
    error: &str,
) -> Result<(), String> {
    let outputs = db
        .audiolab_get_stage_outputs(session_id)
        .await
        .map_err(|e| e.to_string())?;

    if let Some(existing) = outputs.iter().find(|o| o.stage_key == stage_key) {
        let mut updated = existing.clone();
        updated.status = "Error".to_string();
        updated.error_message = Some(error.to_string());
        db.audiolab_upsert_stage_output(&updated)
            .await
            .map_err(|e| e.to_string())?;
    }
    Ok(())
}

/// Convert segments to a readable text block for prompts.
fn segments_to_text(segments: &[AudioSegment]) -> String {
    let mut out = String::new();
    for seg in segments {
        let time = format_time_ms(seg.start_ms);
        let speaker = &seg.speaker;
        out.push_str(&format!("[{time}] {speaker}: {}\n", seg.text));
    }
    out
}

fn format_time_ms(ms: i64) -> String {
    let total_secs = ms / 1000;
    let m = total_secs / 60;
    let s = total_secs % 60;
    format!("{m:02}:{s:02}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_segments_to_text() {
        let segments = vec![
            AudioSegment {
                id: "s1".into(),
                transcript_id: "t1".into(),
                sequence: 0i64,
                start_ms: 0,
                end_ms: 3000,
                text: "Hello world".into(),
                speaker: "Speaker 1".into(),
                speaker_index: 0i64,
                confidence: Some(0.95),
            },
            AudioSegment {
                id: "s2".into(),
                transcript_id: "t1".into(),
                sequence: 1i64,
                start_ms: 3000,
                end_ms: 6000,
                text: "How are you".into(),
                speaker: "Speaker 2".into(),
                speaker_index: 1i64,
                confidence: Some(0.9),
            },
        ];
        let text = segments_to_text(&segments);
        assert!(text.contains("[00:00] Speaker 1: Hello world"));
        assert!(text.contains("[00:03] Speaker 2: How are you"));
    }

    #[test]
    fn test_format_time_ms() {
        assert_eq!(format_time_ms(0), "00:00");
        assert_eq!(format_time_ms(61000), "01:01");
        assert_eq!(format_time_ms(3723000), "62:03");
    }
}
