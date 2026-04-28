use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// Studio session (maps to studio_sessions table)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioSession {
    pub id: String,
    pub session_type: String,
    pub name: String,
    pub directory_path: String,
    pub canvas_mode: String,
    pub media_kind: String,
    pub is_deleted: bool,
    pub created_at: String,
    pub updated_at: String,
    pub last_accessed_at: Option<String>,
    pub source_session_id: Option<String>,
    pub source_session_name: Option<String>,
    pub source_session_directory_name: Option<String>,
    pub source_asset_id: Option<String>,
    pub source_asset_kind: Option<String>,
    pub source_asset_file_name: Option<String>,
    pub source_asset_path: Option<String>,
    pub source_preview_path: Option<String>,
    pub source_reference_role: Option<String>,
    pub message_count: i64,
    pub task_count: i64,
    pub asset_count: i64,
    pub latest_message_preview: Option<String>,
    pub legacy_source_path: Option<String>,
    pub import_batch_id: Option<String>,
    pub imported_at: Option<String>,
    pub is_legacy_import: bool,
}

/// Studio message (maps to studio_messages table)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioMessage {
    pub id: String,
    pub session_id: String,
    pub sequence_no: i64,
    pub role: String,
    pub content_type: String,
    pub text: String,
    pub reasoning_text: String,
    pub prompt_tokens: Option<i64>,
    pub completion_tokens: Option<i64>,
    pub generate_seconds: Option<f64>,
    pub download_seconds: Option<f64>,
    pub search_summary: Option<String>,
    pub timestamp: String,
    pub is_deleted: bool,
}

/// Studio media reference (maps to studio_media_refs table)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioMediaRef {
    pub id: i64,
    pub message_id: String,
    pub media_path: String,
    pub media_kind: String,
    pub sort_order: i64,
    pub preview_path: Option<String>,
}

/// Studio citation (maps to studio_citations table)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioCitation {
    pub id: i64,
    pub message_id: String,
    pub citation_number: i64,
    pub title: String,
    pub url: String,
    pub snippet: String,
    pub hostname: String,
}

/// Studio attachment (maps to studio_attachments table)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioAttachment {
    pub id: i64,
    pub message_id: String,
    pub attachment_type: String,
    pub file_name: String,
    pub file_path: String,
    pub file_size: i64,
    pub sort_order: i64,
}

/// Studio task (maps to studio_tasks table)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioTask {
    pub id: String,
    pub session_id: String,
    pub task_type: String,
    pub status: String,
    pub prompt: String,
    pub progress: f64,
    pub result_file_path: Option<String>,
    pub error_message: Option<String>,
    pub has_reference_input: bool,
    pub remote_video_id: Option<String>,
    pub remote_video_api_mode: Option<String>,
    pub remote_generation_id: Option<String>,
    pub remote_download_url: Option<String>,
    pub generate_seconds: Option<f64>,
    pub download_seconds: Option<f64>,
    pub created_at: String,
    pub updated_at: String,
}

/// Studio asset (maps to studio_assets table)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioAsset {
    pub asset_id: String,
    pub session_id: String,
    pub group_id: String,
    pub kind: String,
    pub workflow: String,
    pub file_name: String,
    pub file_path: String,
    pub preview_path: String,
    pub prompt_text: String,
    pub file_size: Option<i64>,
    pub mime_type: Option<String>,
    pub width: Option<i64>,
    pub height: Option<i64>,
    pub duration_ms: Option<i64>,
    pub created_at: String,
    pub modified_at: String,
    pub storage_scope: String,
    pub derived_from_session_id: Option<String>,
    pub derived_from_session_name: Option<String>,
    pub derived_from_asset_id: Option<String>,
    pub derived_from_asset_file_name: Option<String>,
    pub derived_from_asset_kind: Option<String>,
    pub derived_from_reference_role: Option<String>,
}

/// Studio reference image (maps to studio_reference_images table)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioReferenceImage {
    pub id: String,
    pub session_id: String,
    pub file_path: String,
    pub sort_order: i64,
    pub width: Option<i64>,
    pub height: Option<i64>,
    pub created_at: String,
}

/// Bundle of session messages with associated data
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StudioSessionBundle {
    pub messages: Vec<StudioMessage>,
    pub media_refs: HashMap<String, Vec<StudioMediaRef>>,
    pub citations: HashMap<String, Vec<StudioCitation>>,
    pub attachments: HashMap<String, Vec<StudioAttachment>>,
}
