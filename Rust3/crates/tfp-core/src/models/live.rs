use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TranslationSession {
    pub id: String,
    pub started_at: String,
    pub stopped_at: Option<String>,
    pub source_lang: String,
    /// JSON array string, e.g. '["en","ja"]'
    pub target_langs: String,
    pub provider: String,
    /// "active" or "stopped"
    pub status: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TranslationSegment {
    pub id: String,
    pub session_id: String,
    pub sequence: i64,
    pub original_text: String,
    pub translated_text: String,
    pub target_lang: String,
    pub started_at: Option<String>,
    pub ended_at: Option<String>,
    pub is_bookmarked: bool,
    pub bookmark_note: Option<String>,
    pub audio_path: Option<String>,
    pub raw_event_json: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SupportedLanguage {
    pub code: String,
    pub label: String,
    /// "source", "target", or "both"
    pub kind: String,
}
