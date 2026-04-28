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

use super::config::ModelReference;

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
        }
    }
}

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
