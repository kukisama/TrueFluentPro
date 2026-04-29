use serde::{Deserialize, Serialize};

use super::config::{EndpointType, ModelReference};
use super::enums::ProcessingDisplayState;

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EndpointTemplateDefinition {
    pub profile_id: String,
    pub endpoint_type: EndpointType,
    pub display_name: String,
    #[serde(default)]
    pub subtitle: String,
    #[serde(default)]
    pub glyph: String,
    #[serde(default)]
    pub summary: String,
    #[serde(default)]
    pub default_name_prefix: String,
    #[serde(default)]
    pub default_api_version: String,
    #[serde(default)]
    pub icon_asset_path: String,
    #[serde(default)]
    pub supports_aad: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModelOption {
    pub reference: ModelReference,
    pub endpoint_name: String,
    pub model_display_name: String,
    pub endpoint_type: EndpointType,
}

impl ModelOption {
    pub fn display_string(&self) -> String {
        format!("{} / {}", self.endpoint_name, self.model_display_name)
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SubtitleCue {
    pub start_ms: i64,
    pub end_ms: i64,
    pub text: String,
}

impl SubtitleCue {
    pub fn display_text(&self, max_len: usize) -> String {
        if self.text.chars().count() <= max_len {
            self.text.clone()
        } else {
            let truncated: String = self.text.chars().take(max_len).collect();
            format!("{truncated}\u{2026}")
        }
    }

    pub fn range_text(&self) -> String {
        let fmt = |ms: i64| -> String {
            let total_secs = ms / 1000;
            let h = total_secs / 3600;
            let m = (total_secs % 3600) / 60;
            let s = total_secs % 60;
            format!("{h:02}:{m:02}:{s:02}")
        };
        format!("{} \u{2192} {}", fmt(self.start_ms), fmt(self.end_ms))
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReviewSheetPreset {
    pub name: String,
    pub file_tag: String,
    pub prompt: String,
    #[serde(default = "default_true")]
    pub include_in_batch: bool,
    #[serde(default = "default_true")]
    pub is_enabled: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct InsightPresetButton {
    pub name: String,
    pub prompt: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UpdateInfo {
    pub latest_version: String,
    #[serde(default)]
    pub download_url: String,
    #[serde(default)]
    pub release_page_url: String,
    #[serde(default)]
    pub release_notes: String,
    #[serde(default)]
    pub asset_size: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AzureTenantInfo {
    pub tenant_id: String,
    #[serde(default)]
    pub display_name: String,
}

impl AzureTenantInfo {
    pub fn display_string(&self) -> String {
        if self.display_name.is_empty() {
            self.tenant_id.clone()
        } else {
            format!("{} ({})", self.display_name, self.tenant_id)
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReviewTimeLink {
    pub time_ms: i64,
    pub time_text: String,
    pub label: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AudioFileProcessingSnapshot {
    pub audio_path: String,
    pub state: ProcessingDisplayState,
    #[serde(default)]
    pub badge_text: String,
    #[serde(default)]
    pub detail_text: String,
}
