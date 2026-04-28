use serde::{Deserialize, Serialize};

/// 音频文件记录（对应 audio_files 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioFile {
    pub id: String,
    pub display_name: String,
    pub source_path: String,
    pub mp3_path: Option<String>,
    pub sample_rate: i64,
    pub channels: i64,
    pub duration_ms: i64,
    pub file_size_bytes: i64,
    pub sha256: String,
    pub imported_at: String,
    pub last_opened_at: Option<String>,
    pub is_legacy_import: bool,
    pub legacy_source_path: Option<String>,
    pub import_batch_id: Option<String>,
    /// 关联的 studio_session id（通过 source_asset_id JOIN 获得，非 DB 原生列）
    #[serde(default)]
    pub session_id: Option<String>,
}

/// 转录记录（对应 audio_transcripts 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioTranscript {
    pub id: String,
    pub session_id: String,
    pub audio_file_id: String,
    pub language: String,
    pub raw_json: Option<String>,
    pub parser_kind: String,
    pub created_at: String,
}

/// 转录段落（对应 audio_segments 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioSegment {
    pub id: String,
    pub transcript_id: String,
    pub sequence: i64,
    pub speaker: String,
    pub speaker_index: i64,
    pub start_ms: i64,
    pub end_ms: i64,
    pub text: String,
    pub confidence: Option<f64>,
}

/// 阶段产出（对应 audio_stage_outputs 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioStageOutput {
    pub id: String,
    pub session_id: String,
    pub stage_key: String,
    pub content_markdown: String,
    pub status: String,
    pub error_message: Option<String>,
    pub model_ref: Option<String>,
    pub generated_at: Option<String>,
    pub custom_stage_key: Option<String>,
    pub custom_is_mindmap: Option<bool>,
}

/// 深度研究 topic（对应 audio_research_topics 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioResearchTopic {
    pub id: String,
    pub session_id: String,
    pub title: String,
    pub description: String,
    pub status: String,
    pub report_markdown: Option<String>,
    pub created_at: String,
}

/// 自动标签（对应 audio_auto_tags 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioAutoTag {
    pub id: String,
    pub session_id: String,
    pub tag: String,
    pub source: String,
    pub created_at: String,
}

/// 阶段预设（对应 audio_stage_presets 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioStagePreset {
    pub id: String,
    pub stage: String,
    pub display_name: String,
    pub system_prompt: String,
    pub show_in_tab: bool,
    pub include_in_batch: bool,
    pub is_enabled: bool,
    pub display_mode: String,
    pub sort_order: i64,
}

/// 懒加载 bundle（audiolab_get_bundle 返回）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioLabBundle {
    pub file: AudioFile,
    pub transcript: Option<AudioTranscript>,
    pub segments: Vec<AudioSegment>,
    pub auto_tags: Vec<AudioAutoTag>,
    pub stage_outputs: Vec<AudioStageOutput>,
    pub research_topics: Vec<AudioResearchTopic>,
    pub custom_presets: Vec<AudioStagePreset>,
}

/// 播放打开返回信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioPlaybackInfo {
    pub file_id: String,
    pub playback_path: String,
    pub duration_ms: i64,
    pub display_name: String,
}
