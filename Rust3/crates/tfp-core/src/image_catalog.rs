//! Image model capabilities catalog.

use serde::{Deserialize, Serialize};

const IMAGE_MODELS_JSON: &str = include_str!("../../../src-tauri/assets/image-models.json");

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImageModelsConfig {
    pub defaults: serde_json::Value,
    pub models: Vec<ImageModelCapabilities>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImageModelCapabilities {
    pub model_id: String,
    #[serde(default)]
    pub snapshot_version: Option<String>,
    #[serde(default)]
    pub supports_text_output: bool,
    #[serde(default = "default_true")]
    pub supports_generation: bool,
    #[serde(default = "default_true")]
    pub supports_editing: bool,
    #[serde(default = "default_true")]
    pub supports_mask: bool,
    #[serde(default = "default_true")]
    pub supports_multi_reference: bool,
    #[serde(default)]
    pub resolution_mode: String,
    #[serde(default)]
    pub fixed_sizes: Vec<String>,
    #[serde(default)]
    pub free_form_constraints: Option<ResolutionConstraints>,
    #[serde(default = "default_quality_options")]
    pub quality_options: Vec<String>,
    #[serde(default = "default_output_formats")]
    pub output_formats: Vec<String>,
    #[serde(default)]
    pub supports_transparent_background: bool,
    #[serde(default)]
    pub supports_input_fidelity: bool,
    #[serde(default = "default_high")]
    pub default_input_fidelity: String,
    #[serde(default = "default_true")]
    pub supports_streaming: bool,
    #[serde(default = "default_max_partial")]
    pub max_partial_images: u32,
    #[serde(default = "default_true")]
    pub supports_image_api: bool,
    #[serde(default = "default_true")]
    pub supports_responses_api: bool,
    #[serde(default)]
    pub requires_deployment_header: bool,
    #[serde(default)]
    pub billing: Option<ImageBillingModel>,
}

fn default_true() -> bool { true }
fn default_high() -> String { "high".into() }
fn default_max_partial() -> u32 { 3 }
fn default_quality_options() -> Vec<String> { vec!["low".into(), "medium".into(), "high".into()] }
fn default_output_formats() -> Vec<String> { vec!["png".into(), "jpeg".into(), "webp".into()] }

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImageBillingModel {
    #[serde(default)]
    pub supports_token_billing: bool,
    #[serde(default)]
    pub supports_per_image_billing: bool,
    #[serde(default)]
    pub token_calc_mode: String,
    #[serde(default)]
    pub image_output_token_price: f64,
    #[serde(default)]
    pub text_input_token_price: f64,
    #[serde(default)]
    pub image_input_token_price: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolutionConstraints {
    pub max_edge: u32,
    pub edge_multiple: u32,
    pub max_aspect_ratio: f64,
    pub min_pixels: u64,
    pub max_pixels: u64,
}

/// Load image model capabilities from embedded JSON asset.
pub fn load_image_models() -> ImageModelsConfig {
    serde_json::from_str(IMAGE_MODELS_JSON)
        .expect("embedded image-models.json must be valid")
}

/// Find a model's capabilities by model_id.
pub fn get_model<'a>(config: &'a ImageModelsConfig, model_id: &str) -> Option<&'a ImageModelCapabilities> {
    config.models.iter().find(|m| m.model_id == model_id)
}

/// Validate image dimensions against FreeForm constraints.
/// Returns None if valid, Some(error_message) if invalid.
pub fn validate_freeform_size(constraints: &ResolutionConstraints, width: u32, height: u32) -> Option<String> {
    if width == 0 || height == 0 {
        return Some("Width and height must be positive".into());
    }
    if width > constraints.max_edge || height > constraints.max_edge {
        return Some(format!(
            "Edge exceeds maximum {}px (got {}x{})",
            constraints.max_edge, width, height
        ));
    }
    if width % constraints.edge_multiple != 0 || height % constraints.edge_multiple != 0 {
        return Some(format!(
            "Dimensions must be multiples of {} (got {}x{})",
            constraints.edge_multiple, width, height
        ));
    }
    let pixels = width as u64 * height as u64;
    if pixels < constraints.min_pixels {
        return Some(format!(
            "Total pixels {} below minimum {} ({}x{})",
            pixels, constraints.min_pixels, width, height
        ));
    }
    if pixels > constraints.max_pixels {
        return Some(format!(
            "Total pixels {} exceeds maximum {} ({}x{})",
            pixels, constraints.max_pixels, width, height
        ));
    }
    let aspect = if width >= height {
        width as f64 / height as f64
    } else {
        height as f64 / width as f64
    };
    if aspect > constraints.max_aspect_ratio {
        return Some(format!(
            "Aspect ratio {:.2} exceeds maximum {:.1} ({}x{})",
            aspect, constraints.max_aspect_ratio, width, height
        ));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_load_image_models() {
        let config = load_image_models();
        assert_eq!(config.models.len(), 3);
        assert!(get_model(&config, "gpt-image-2").is_some());
        assert!(get_model(&config, "gpt-image-1.5").is_some());
        assert!(get_model(&config, "gpt-image-1").is_some());
    }

    #[test]
    fn test_get_model_capabilities() {
        let config = load_image_models();
        let m = get_model(&config, "gpt-image-2").unwrap();
        assert_eq!(m.resolution_mode, "FreeForm");
        assert!(m.free_form_constraints.is_some());
        assert!(!m.supports_text_output);
    }

    #[test]
    fn test_freeform_valid_size() {
        let c = ResolutionConstraints {
            max_edge: 3840,
            edge_multiple: 16,
            max_aspect_ratio: 3.0,
            min_pixels: 655_360,
            max_pixels: 8_294_400,
        };
        assert!(validate_freeform_size(&c, 1024, 1024).is_none());
        assert!(validate_freeform_size(&c, 1536, 1024).is_none());
        assert!(validate_freeform_size(&c, 2880, 2880).is_none());
    }

    #[test]
    fn test_freeform_edge_too_large() {
        let c = ResolutionConstraints {
            max_edge: 3840,
            edge_multiple: 16,
            max_aspect_ratio: 3.0,
            min_pixels: 655_360,
            max_pixels: 8_294_400,
        };
        let err = validate_freeform_size(&c, 4096, 1024).unwrap();
        assert!(err.contains("Edge exceeds"));
    }

    #[test]
    fn test_freeform_not_multiple() {
        let c = ResolutionConstraints {
            max_edge: 3840,
            edge_multiple: 16,
            max_aspect_ratio: 3.0,
            min_pixels: 655_360,
            max_pixels: 8_294_400,
        };
        let err = validate_freeform_size(&c, 1025, 1024).unwrap();
        assert!(err.contains("multiples of"));
    }

    #[test]
    fn test_freeform_too_few_pixels() {
        let c = ResolutionConstraints {
            max_edge: 3840,
            edge_multiple: 16,
            max_aspect_ratio: 3.0,
            min_pixels: 655_360,
            max_pixels: 8_294_400,
        };
        let err = validate_freeform_size(&c, 256, 256).unwrap();
        assert!(err.contains("below minimum"));
    }

    #[test]
    fn test_freeform_aspect_ratio_exceeded() {
        let c = ResolutionConstraints {
            max_edge: 3840,
            edge_multiple: 16,
            max_aspect_ratio: 3.0,
            min_pixels: 655_360,
            max_pixels: 8_294_400,
        };
        // 3840x256 = aspect 15.0 > 3.0, but also pixels = 983040 which is within range
        let err = validate_freeform_size(&c, 3840, 256).unwrap();
        assert!(err.contains("Aspect ratio"));
    }

    #[test]
    fn test_get_model_not_found() {
        let config = load_image_models();
        assert!(get_model(&config, "nonexistent").is_none());
    }
}
