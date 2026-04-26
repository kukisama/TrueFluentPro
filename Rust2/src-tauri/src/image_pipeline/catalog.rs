use serde::{Deserialize, Serialize};

/// 模型能力目录 — 对齐 C# Assets/image-models.json
/// 提供统一查询接口，前端可据此渲染 UI 选项

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
}

/// 内置目录 — 可从 Assets/image-models.json 加载
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
        },
        ModelCapabilityEntry {
            model_id: "dall-e-3".into(),
            display_name: "DALL·E 3".into(),
            provider: "openai".into(),
            capabilities: vec!["generate".into()],
            supported_sizes: vec!["1024x1024".into(), "1792x1024".into(), "1024x1792".into()],
            supported_qualities: vec!["standard".into(), "hd".into()],
            supported_styles: vec!["vivid".into(), "natural".into()],
            max_prompt_length: 4000,
            supports_negative_prompt: false,
        },
    ]
}
