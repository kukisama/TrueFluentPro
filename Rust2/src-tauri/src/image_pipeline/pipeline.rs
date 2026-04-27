use serde::{Deserialize, Serialize};

/// 五步图片管道
/// Step 1: Prompt Optimization — 用 AI 优化/翻译用户 prompt
/// Step 2: Generation — 调用 ImageGenSlot 生成初版图片
/// Step 3: Upscale/Enhance — (预留) 调用超分辨率
/// Step 4: Edit — (预留) 调用 image edit 接口
/// Step 5: Export — 保存到本地或上传

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PipelineRequest {
    pub prompt: String,
    pub model: String,
    pub width: u32,
    pub height: u32,
    pub quality: Option<String>,
    pub output_format: Option<String>,
    pub background: Option<String>,
    pub endpoint_id: String,
    pub optimize_prompt: bool,
    pub upscale: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PipelineResult {
    pub original_prompt: String,
    pub optimized_prompt: Option<String>,
    pub image_base64: Option<String>,
    pub image_url: Option<String>,
    pub revised_prompt: Option<String>,
    pub steps_completed: Vec<String>,
}

/// 执行图片五步管道
pub async fn run_pipeline(
    app_handle: &tauri::AppHandle,
    request: PipelineRequest,
) -> Result<PipelineResult, String> {
    use tauri::Manager;
    let state = app_handle.state::<crate::state::AppState>();
    let mut result = PipelineResult {
        original_prompt: request.prompt.clone(),
        optimized_prompt: None,
        image_base64: None,
        image_url: None,
        revised_prompt: None,
        steps_completed: vec![],
    };

    // Step 1: Prompt Optimization
    let final_prompt = if request.optimize_prompt {
        let providers = state.providers.read().await;
        let config = state.config.read().await;
        let ai_ep = config.endpoints.iter()
            .find(|ep| ep.enabled && matches!(
                ep.endpoint_type,
                crate::models::EndpointType::AzureOpenAi
                | crate::models::EndpointType::OpenAiCompatible
                | crate::models::EndpointType::ApiManagementGateway
            ))
            .cloned();

        let quick_model_id = config.ai.quick_model.model_id.clone();
        drop(config);

        if let Some(ep) = ai_ep {
            if let Some(ai) = providers.get_ai_completion(&ep.id) {
                drop(providers);
                let model = if quick_model_id.is_empty() { "gpt-4.1-mini".to_string() } else { quick_model_id };

                let req = crate::models::CompletionRequest {
                    messages: vec![
                        crate::models::ChatMessage {
                            role: "system".into(),
                            content: "You are an image prompt optimizer. Improve the user's image generation prompt to be more descriptive and effective. Return ONLY the optimized prompt, nothing else.".into(),
                        },
                        crate::models::ChatMessage {
                            role: "user".into(),
                            content: request.prompt.clone(),
                        },
                    ],
                    model,
                    temperature: Some(0.7),
                    max_tokens: Some(500),
                    endpoint_id: ep.id.clone(),
                };
                match ai.complete(&req).await {
                    Ok(resp) => {
                        result.optimized_prompt = Some(resp.content.clone());
                        result.steps_completed.push("prompt_optimization".into());
                        resp.content
                    }
                    Err(_) => request.prompt.clone(),
                }
            } else {
                request.prompt.clone()
            }
        } else {
            request.prompt.clone()
        }
    } else {
        request.prompt.clone()
    };

    // Step 2: Generation
    let providers = state.providers.read().await;
    let img_provider = providers.get_image_gen(&request.endpoint_id);
    if let Some(img) = img_provider {
        drop(providers);
        let gen_req = crate::models::ImageGenRequest {
            prompt: final_prompt,
            width: request.width,
            height: request.height,
            model: request.model,
            quality: request.quality,
            output_format: request.output_format,
            background: request.background,
            n: None,
            endpoint_id: request.endpoint_id,
        };
        let results = img.generate(&gen_req).await.map_err(|e| e.to_string())?;
        if let Some(r) = results.first() {
            result.image_base64 = r.base64.clone();
            result.image_url = r.url.clone();
            result.revised_prompt = r.revised_prompt.clone();
        }
        result.steps_completed.push("generation".into());
    } else {
        return Err("Image generation provider not found".into());
    }

    // Step 3: Upscale (预留)
    if request.upscale {
        // TODO: integrate upscale API when available
        result.steps_completed.push("upscale_skipped".into());
    }

    // Step 4: Edit (预留 — 按需调用)
    // Step 5: Export 在前端处理

    Ok(result)
}
