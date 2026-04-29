use serde::{Deserialize, Serialize};

fn default_true() -> bool { true }
fn default_image_quality() -> String { "auto".into() }
fn default_image_format() -> String { "png".into() }
fn default_image_size() -> String { "1024x1024".into() }
fn default_one() -> u32 { 1 }
fn default_image_background() -> String { "auto".into() }
fn default_video_aspect() -> String { "16:9".into() }
fn default_video_resolution() -> String { "720p".into() }
fn default_video_seconds() -> u32 { 5 }
fn default_poll_interval() -> u32 { 3000 }
fn default_audio_container() -> String { "truefluentpro-audio".into() }
fn default_result_container() -> String { "truefluentpro-results".into() }
fn default_mp3_bitrate() -> u32 { 256 }
fn default_max_history() -> u32 { 500 }
fn default_realtime_max() -> u32 { 150 }
fn default_chunk_duration() -> u32 { 200 }
fn default_initial_silence() -> u32 { 25 }
fn default_end_silence() -> u32 { 1 }
fn default_no_response() -> u32 { 3 }
fn default_audio_threshold() -> u32 { 600 }
fn default_audio_gain() -> f64 { 2.0 }
fn default_search_provider() -> String { "duckduckgo".into() }
fn default_search_trigger() -> String { "auto".into() }
fn default_search_max() -> u32 { 5 }
fn default_image_model_name() -> String { "gpt-image-1".into() }
fn default_video_model_name() -> String { "sora-2".into() }
fn default_input_fidelity() -> String { "auto".into() }
fn default_video_width() -> u32 { 1280 }
fn default_video_height() -> u32 { 720 }
fn default_max_conversation_turns() -> u32 { 20 }
fn default_max_loaded_sessions() -> u32 { 8 }

use super::config::{ImageEditMode, ModelReference};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MediaSettings {
    #[serde(default)]
    pub image_model: ModelReference,
    #[serde(default)]
    pub video_model: ModelReference,
    #[serde(default = "default_image_quality")]
    pub image_quality: String,
    #[serde(default = "default_image_format")]
    pub image_format: String,
    #[serde(default = "default_image_size")]
    pub image_size: String,
    #[serde(default = "default_one")]
    pub image_count: u32,
    #[serde(default = "default_image_background")]
    pub image_background: String,
    #[serde(default = "default_video_aspect")]
    pub video_aspect_ratio: String,
    #[serde(default = "default_video_resolution")]
    pub video_resolution: String,
    #[serde(default = "default_video_seconds")]
    pub video_seconds: u32,
    #[serde(default = "default_one")]
    pub video_variants: u32,
    #[serde(default = "default_poll_interval")]
    pub video_poll_interval_ms: u32,
    #[serde(default = "default_image_model_name")]
    pub image_model_name: String,
    #[serde(default = "default_video_model_name")]
    pub video_model_name: String,
    #[serde(default)]
    pub image_edit_mode: ImageEditMode,
    #[serde(default = "default_input_fidelity")]
    pub input_fidelity: String,
    #[serde(default = "default_true")]
    pub enable_chat_image_generation: bool,
    #[serde(default = "default_video_width")]
    pub video_width: u32,
    #[serde(default = "default_video_height")]
    pub video_height: u32,
    #[serde(default)]
    pub default_enable_studio_reasoning: bool,
    #[serde(default)]
    pub default_enable_studio_web_search: bool,
    #[serde(default = "default_max_conversation_turns")]
    pub default_max_conversation_turns: u32,
    #[serde(default = "default_max_loaded_sessions")]
    pub max_loaded_sessions_in_memory: u32,
    #[serde(default)]
    pub output_directory: String,
}

impl Default for MediaSettings {
    fn default() -> Self {
        Self {
            image_model: ModelReference::default(),
            video_model: ModelReference::default(),
            image_quality: default_image_quality(),
            image_format: default_image_format(),
            image_size: default_image_size(),
            image_count: 1,
            image_background: default_image_background(),
            video_aspect_ratio: default_video_aspect(),
            video_resolution: default_video_resolution(),
            video_seconds: 5,
            video_variants: 1,
            video_poll_interval_ms: 3000,
            image_model_name: default_image_model_name(),
            video_model_name: default_video_model_name(),
            image_edit_mode: ImageEditMode::default(),
            input_fidelity: default_input_fidelity(),
            enable_chat_image_generation: true,
            video_width: 1280,
            video_height: 720,
            default_enable_studio_reasoning: false,
            default_enable_studio_web_search: false,
            default_max_conversation_turns: 20,
            max_loaded_sessions_in_memory: 8,
            output_directory: String::new(),
        }
    }
}

fn default_batch_max_chars() -> u32 { 24 }
fn default_batch_max_duration() -> f64 { 6.0 }
fn default_batch_pause_split_ms() -> u32 { 500 }

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StorageSettings {
    #[serde(default)]
    pub batch_storage_connection_string: String,
    #[serde(default)]
    pub batch_storage_is_valid: bool,
    #[serde(default = "default_audio_container")]
    pub batch_audio_container_name: String,
    #[serde(default = "default_result_container")]
    pub batch_result_container_name: String,
    #[serde(default = "default_true")]
    pub enable_recording: bool,
    #[serde(default = "default_mp3_bitrate")]
    pub recording_mp3_bitrate_kbps: u32,
    #[serde(default = "default_true")]
    pub export_vtt_subtitles: bool,
    #[serde(default)]
    pub export_srt_subtitles: bool,
    /// 0=Off, 1=FailuresOnly, 2=All
    #[serde(default)]
    pub batch_log_level: u32,
    #[serde(default)]
    pub batch_force_regeneration: bool,
    #[serde(default)]
    pub context_menu_force_regeneration: bool,
    #[serde(default)]
    pub enable_batch_sentence_split: bool,
    #[serde(default)]
    pub batch_split_on_comma: bool,
    #[serde(default = "default_batch_max_chars")]
    pub batch_max_chars: u32,
    #[serde(default = "default_batch_max_duration")]
    pub batch_max_duration: f64,
    #[serde(default = "default_batch_pause_split_ms")]
    pub batch_pause_split_ms: u32,
}

impl Default for StorageSettings {
    fn default() -> Self {
        Self {
            batch_storage_connection_string: String::new(),
            batch_storage_is_valid: false,
            batch_audio_container_name: default_audio_container(),
            batch_result_container_name: default_result_container(),
            enable_recording: true,
            recording_mp3_bitrate_kbps: 256,
            export_vtt_subtitles: true,
            export_srt_subtitles: false,
            batch_log_level: 0,
            batch_force_regeneration: false,
            context_menu_force_regeneration: false,
            enable_batch_sentence_split: false,
            batch_split_on_comma: false,
            batch_max_chars: 24,
            batch_max_duration: 6.0,
            batch_pause_split_ms: 500,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RecognitionSettings {
    #[serde(default = "default_true")]
    pub filter_modal_particles: bool,
    #[serde(default = "default_max_history")]
    pub max_history_items: u32,
    #[serde(default = "default_realtime_max")]
    pub realtime_max_length: u32,
    #[serde(default = "default_chunk_duration")]
    pub chunk_duration_ms: u32,
    #[serde(default = "default_true")]
    pub enable_auto_timeout: bool,
    #[serde(default = "default_initial_silence")]
    pub initial_silence_timeout_seconds: u32,
    #[serde(default = "default_end_silence")]
    pub end_silence_timeout_seconds: u32,
    #[serde(default)]
    pub enable_no_response_restart: bool,
    #[serde(default = "default_no_response")]
    pub no_response_restart_seconds: u32,
    #[serde(default = "default_audio_threshold")]
    pub audio_activity_threshold: u32,
    #[serde(default = "default_audio_gain")]
    pub audio_level_gain: f64,
    #[serde(default = "default_true")]
    pub show_reconnect_marker: bool,
}

impl Default for RecognitionSettings {
    fn default() -> Self {
        Self {
            filter_modal_particles: true,
            max_history_items: 500,
            realtime_max_length: 150,
            chunk_duration_ms: 200,
            enable_auto_timeout: true,
            initial_silence_timeout_seconds: 25,
            end_silence_timeout_seconds: 1,
            enable_no_response_restart: false,
            no_response_restart_seconds: 3,
            audio_activity_threshold: 600,
            audio_level_gain: 2.0,
            show_reconnect_marker: true,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WebSearchSettings {
    #[serde(default = "default_search_provider")]
    pub provider_id: String,
    #[serde(default = "default_search_trigger")]
    pub trigger_mode: String,
    #[serde(default = "default_search_max")]
    pub max_results: u32,
    #[serde(default = "default_true")]
    pub enable_intent_analysis: bool,
    #[serde(default)]
    pub enable_result_compression: bool,
    #[serde(default)]
    pub mcp_endpoint: String,
    #[serde(default)]
    pub mcp_tool_name: String,
    #[serde(default)]
    pub mcp_api_key: String,
    #[serde(default)]
    pub debug_mode: bool,
}

impl Default for WebSearchSettings {
    fn default() -> Self {
        Self {
            provider_id: default_search_provider(),
            trigger_mode: default_search_trigger(),
            max_results: 5,
            enable_intent_analysis: true,
            enable_result_compression: false,
            mcp_endpoint: String::new(),
            mcp_tool_name: "web_search".into(),
            mcp_api_key: String::new(),
            debug_mode: false,
        }
    }
}
fn default_max_chars() -> u32 { 42 }
fn default_max_duration_seconds() -> f64 { 8.0 }
fn default_pause_split_ms() -> u32 { 600 }

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BatchSubtitleSplitOptions {
    #[serde(default = "default_true")]
    pub enable_sentence_split: bool,
    #[serde(default)]
    pub split_on_comma: bool,
    #[serde(default = "default_max_chars")]
    pub max_chars: u32,
    #[serde(default = "default_max_duration_seconds")]
    pub max_duration_seconds: f64,
    #[serde(default = "default_pause_split_ms")]
    pub pause_split_ms: u32,
}

impl Default for BatchSubtitleSplitOptions {
    fn default() -> Self {
        Self {
            enable_sentence_split: true,
            split_on_comma: false,
            max_chars: 42,
            max_duration_seconds: 8.0,
            pause_split_ms: 600,
        }
    }
}

/// Batch processing settings (review sheets, concurrency, etc.)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BatchSettings {
    #[serde(default)]
    pub review_sheets: Vec<super::common::ReviewSheetPreset>,
    #[serde(default = "default_true")]
    pub include_subtitle: bool,
    #[serde(default)]
    pub subtitle_split: BatchSubtitleSplitOptions,
}

impl Default for BatchSettings {
    fn default() -> Self {
        Self {
            review_sheets: Vec::new(),
            include_subtitle: true,
            subtitle_split: BatchSubtitleSplitOptions::default(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_storage_settings_default_values() {
        let s = StorageSettings::default();
        assert_eq!(s.batch_log_level, 0);
        assert!(!s.batch_force_regeneration);
        assert!(!s.context_menu_force_regeneration);
        assert!(!s.enable_batch_sentence_split);
        assert!(!s.batch_split_on_comma);
        assert_eq!(s.batch_max_chars, 24);
        assert!((s.batch_max_duration - 6.0).abs() < f64::EPSILON);
        assert_eq!(s.batch_pause_split_ms, 500);
    }

    #[test]
    fn test_storage_settings_serde_roundtrip() {
        let s = StorageSettings {
            batch_log_level: 2,
            batch_force_regeneration: true,
            enable_batch_sentence_split: true,
            batch_max_chars: 30,
            batch_max_duration: 8.5,
            batch_pause_split_ms: 700,
            ..Default::default()
        };
        let json = serde_json::to_string(&s).unwrap();
        let deserialized: StorageSettings = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.batch_log_level, 2);
        assert!(deserialized.batch_force_regeneration);
        assert!(deserialized.enable_batch_sentence_split);
        assert_eq!(deserialized.batch_max_chars, 30);
        assert!((deserialized.batch_max_duration - 8.5).abs() < f64::EPSILON);
        assert_eq!(deserialized.batch_pause_split_ms, 700);
    }

    #[test]
    fn test_storage_settings_missing_new_fields_deserializes_defaults() {
        let json = r#"{"batch_storage_connection_string":"","enable_recording":true}"#;
        let s: StorageSettings = serde_json::from_str(json).unwrap();
        assert_eq!(s.batch_log_level, 0);
        assert!(!s.batch_force_regeneration);
        assert_eq!(s.batch_max_chars, 24);
        assert!((s.batch_max_duration - 6.0).abs() < f64::EPSILON);
        assert_eq!(s.batch_pause_split_ms, 500);
    }

    #[test]
    fn test_media_settings_default() {
        let m = MediaSettings::default();
        assert_eq!(m.image_quality, "auto");
        assert_eq!(m.video_seconds, 5);
        assert_eq!(m.max_loaded_sessions_in_memory, 8);
    }

    #[test]
    fn test_recognition_settings_default() {
        let r = RecognitionSettings::default();
        assert!(r.filter_modal_particles);
        assert_eq!(r.max_history_items, 500);
        assert_eq!(r.initial_silence_timeout_seconds, 25);
    }

    #[test]
    fn test_web_search_settings_default() {
        let ws = WebSearchSettings::default();
        assert_eq!(ws.provider_id, "duckduckgo");
        assert_eq!(ws.trigger_mode, "auto");
        assert_eq!(ws.max_results, 5);
    }
}
