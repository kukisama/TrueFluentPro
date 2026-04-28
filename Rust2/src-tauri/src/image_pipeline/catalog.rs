use serde::{Deserialize, Serialize};
use std::path::Path;

/// P3-5: 模型能力目录 — 从 Assets/image-models.json 动态加载
/// 替代之前的硬编码 2 模型

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

/// 从 JSON 文件解析的原始格式（对齐 Assets/image-models.json 结构）
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

/// 从 Assets/image-models.json 加载模型目录
pub fn load_image_models_from_file(path: &Path) -> Vec<ModelCapabilityEntry> {
    match std::fs::read_to_string(path) {
        Ok(content) => parse_image_models_json(&content),
        Err(e) => {
            tracing::warn!("无法加载 image-models.json ({path:?}): {e}, 使用内置默认值");
            builtin_image_models()
        }
    }
}

/// 解析 image-models.json 内容
fn parse_image_models_json(content: &str) -> Vec<ModelCapabilityEntry> {
    let parsed: ImageModelsJson = match serde_json::from_str(content) {
        Ok(v) => v,
        Err(e) => {
            tracing::warn!("image-models.json 解析失败: {e}, 使用内置默认值");
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

/// 内置 fallback 目录 — 当 JSON 文件不可用时使用
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
