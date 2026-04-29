use serde::{Deserialize, Serialize};
use std::path::Path;

/// Model capability catalog — dynamically loaded from Assets/image-models.json

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModelCapabilityEntry {
    pub model_id: String,
    pub display_name: String,
    pub provider: String,
    pub capabilities: Vec<String>,
    pub supported_sizes: Vec<String>,
    pub supported_qualities: Vec<String>,
    pub supported_styles: Vec<String>,
    pub max_prompt_length: u32,
    pub supports_negative_prompt: bool,
    pub supports_transparent_background: bool,
    pub supports_input_fidelity: bool,
    pub resolution_mode: String,
}

/// Raw JSON format parsed from Assets/image-models.json
#[derive(Debug, Deserialize)]
struct ImageModelsJson {
    defaults: ImageModelDefaults,
    models: Vec<ImageModelEntry>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct ImageModelDefaults {
    #[serde(default)]
    quality_options: Vec<String>,
    #[serde(default)]
    output_formats: Vec<String>,
    #[serde(default)]
    supports_generation: bool,
    #[serde(default)]
    supports_editing: bool,
    #[serde(default)]
    supports_transparent_background: bool,
    #[serde(default)]
    supports_input_fidelity: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct ImageModelEntry {
    model_id: String,
    #[serde(default)]
    snapshot_version: Option<String>,
    #[serde(default)]
    resolution_mode: Option<String>,
    #[serde(default)]
    fixed_sizes: Option<Vec<String>>,
    #[serde(default)]
    free_form_constraints: Option<FreeFormConstraints>,
    #[serde(default)]
    supports_transparent_background: Option<bool>,
    #[serde(default)]
    supports_input_fidelity: Option<bool>,
    #[serde(default)]
    supports_text_output: Option<bool>,
    #[serde(default)]
    supports_multi_reference: Option<bool>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
#[allow(dead_code)]
struct FreeFormConstraints {
    #[serde(default)]
    max_edge: u32,
    #[serde(default)]
    max_aspect_ratio: f64,
}

/// Load model catalog from Assets/image-models.json
pub fn load_image_models_from_file(path: &Path) -> Vec<ModelCapabilityEntry> {
    match std::fs::read_to_string(path) {
        Ok(content) => parse_image_models_json(&content),
        Err(e) => {
            tracing::warn!("Cannot load image-models.json ({path:?}): {e}, using builtin defaults");
            builtin_image_models()
        }
    }
}

/// Parse image-models.json content
fn parse_image_models_json(content: &str) -> Vec<ModelCapabilityEntry> {
    let parsed: ImageModelsJson = match serde_json::from_str(content) {
        Ok(v) => v,
        Err(e) => {
            tracing::warn!("Failed to parse image-models.json: {e}, using builtin defaults");
            return builtin_image_models();
        }
    };

    parsed.models.iter().map(|m| {
        let mut capabilities = Vec::new();
        if parsed.defaults.supports_generation { capabilities.push("generate".into()); }
        if parsed.defaults.supports_editing { capabilities.push("edit".into()); }

        let supported_sizes = m.fixed_sizes.clone().unwrap_or_else(|| {
            if let Some(ref fc) = m.free_form_constraints {
                vec![format!("max_edge:{}", fc.max_edge), "auto".into()]
            } else {
                vec!["1024x1024".into(), "1536x1024".into(), "1024x1536".into()]
            }
        });

        ModelCapabilityEntry {
            model_id: m.model_id.clone(),
            display_name: m.snapshot_version.clone().unwrap_or_else(|| m.model_id.clone()),
            provider: "openai".into(),
            capabilities,
            supported_sizes,
            supported_qualities: parsed.defaults.quality_options.clone(),
            supported_styles: vec![],
            max_prompt_length: 32000,
            supports_negative_prompt: false,
            supports_transparent_background: m.supports_transparent_background.unwrap_or(parsed.defaults.supports_transparent_background),
            supports_input_fidelity: m.supports_input_fidelity.unwrap_or(parsed.defaults.supports_input_fidelity),
            resolution_mode: m.resolution_mode.clone().unwrap_or_else(|| "Fixed".into()),
        }
    }).collect()
}

/// Builtin fallback catalog — used when JSON file is unavailable
pub fn builtin_image_models() -> Vec<ModelCapabilityEntry> {
    vec![
        ModelCapabilityEntry {
            model_id: "gpt-image-2".into(),
            display_name: "GPT Image 2 (Azure)".into(),
            provider: "azure_openai".into(),
            capabilities: vec!["generate".into(), "edit".into()],
            supported_sizes: vec!["1024x1024".into(), "1536x1024".into(), "1024x1536".into(), "auto".into()],
            supported_qualities: vec!["auto".into(), "low".into(), "medium".into(), "high".into()],
            supported_styles: vec![],
            max_prompt_length: 32000,
            supports_negative_prompt: false,
            supports_transparent_background: false,
            supports_input_fidelity: false,
            resolution_mode: "FreeForm".into(),
        },
        ModelCapabilityEntry {
            model_id: "gpt-image-1".into(),
            display_name: "GPT Image 1".into(),
            provider: "openai".into(),
            capabilities: vec!["generate".into(), "edit".into()],
            supported_sizes: vec!["1024x1024".into(), "1536x1024".into(), "1024x1536".into()],
            supported_qualities: vec!["low".into(), "medium".into(), "high".into()],
            supported_styles: vec![],
            max_prompt_length: 32000,
            supports_negative_prompt: false,
            supports_transparent_background: true,
            supports_input_fidelity: true,
            resolution_mode: "Fixed".into(),
        },
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_builtin_image_models() {
        let models = builtin_image_models();
        assert_eq!(models.len(), 2);
        assert_eq!(models[0].model_id, "gpt-image-2");
        assert!(models[0].capabilities.contains(&"generate".to_string()));
        assert!(models[0].capabilities.contains(&"edit".to_string()));
        assert_eq!(models[1].model_id, "gpt-image-1");
        assert_eq!(models[1].resolution_mode, "Fixed");
    }

    #[test]
    fn test_parse_valid_json() {
        let json = r#"{
            "defaults": {
                "qualityOptions": ["auto"],
                "outputFormats": [],
                "supportsGeneration": true,
                "supportsEditing": false,
                "supportsTransparentBackground": false,
                "supportsInputFidelity": false
            },
            "models": [{"modelId": "test-model"}]
        }"#;

        let result = parse_image_models_json(json);
        assert_eq!(result.len(), 1);
        assert_eq!(result[0].model_id, "test-model");
        assert!(result[0].capabilities.contains(&"generate".to_string()));
        assert!(!result[0].capabilities.contains(&"edit".to_string()));
    }

    #[test]
    fn test_parse_invalid_json_returns_builtin() {
        let result = parse_image_models_json("not json!");
        assert_eq!(result.len(), 2); // falls back to builtin
    }
}
