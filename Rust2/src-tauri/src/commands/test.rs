use tauri::{Emitter, State};

use crate::models::*;
use crate::state::AppState;

/// 测试端点连通性 — 逐模型逐能力测试，通过事件实时推送进度
#[tauri::command]
pub async fn test_endpoint(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    endpoint_id: String,
) -> Result<crate::models::EndpointTestReport, String> {
    let config = state.config.read().await;
    let ep = config
        .endpoints
        .iter()
        .find(|e| e.id == endpoint_id)
        .ok_or("端点不存在")?
        .clone();
    drop(config);

    let profiles = build_vendor_profiles();
    let profile = profiles.iter().find(|p| p.endpoint_type == ep.endpoint_type);
    let started_at = chrono::Utc::now().to_rfc3339();
    let start = std::time::Instant::now();

    // ── Speech 端点走独立逻辑 ──
    if ep.is_speech() {
        let mut items = test_speech_endpoint(&ep).await;
        // O-49: 追加 TTS 语音合成测试
        items.push(test_speech_tts_voices(&ep).await);
        let report = build_report(&ep, items, start.elapsed().as_millis() as u64);
        return Ok(report);
    }

    // ── AI 端点前置校验 ──
    if ep.url.trim().is_empty() {
        return Err("端点 URL 为空，请先填写".into());
    }
    if ep.api_key.trim().is_empty() {
        return Err("API Key 为空，请先填写".into());
    }
    if ep.models.is_empty() {
        return Err("模型列表为空，请至少添加一个模型".into());
    }

    // ── 计算总测试项并初始化进度 ──
    let mut plan: Vec<(String, crate::models::ModelCapability)> = Vec::new();
    for model in &ep.models {
        for cap in &model.capabilities {
            plan.push((model.model_id.clone(), cap.clone()));
        }
    }
    let total = plan.len();

    // 初始化 items 为 Running（全部并发，无 Pending 排队）
    let items: Vec<crate::models::EndpointTestItem> = plan
        .iter()
        .map(|(mid, cap)| crate::models::EndpointTestItem {
            model_id: mid.clone(),
            capability: cap_label(cap),
            status: crate::models::TestStatus::Running,
            summary: format!("正在测试 {}...", cap_label(cap)),
            detail: None,
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        })
        .collect();

    // 推送初始进度——全部 Running
    emit_progress(&app, &ep, &items, &started_at, false);

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(25))
        .build()
        .map_err(|e| e.to_string())?;

    // ── 并发测试：所有 (model, capability) 一口气发出 ──
    let items_shared = std::sync::Arc::new(tokio::sync::Mutex::new(items));
    let mut handles = Vec::new();

    for (idx, (model_id, cap)) in plan.iter().enumerate() {
        let client = client.clone();
        let ep = ep.clone();
        let profile = profile.cloned();
        let model = ep.models.iter().find(|m| m.model_id == *model_id).unwrap().clone();
        let cap = cap.clone();
        let items_shared = items_shared.clone();
        let app = app.clone();
        let started_at = started_at.clone();

        let handle = tokio::spawn(async move {
            let t0 = std::time::Instant::now();
            let mut result = test_single_capability_v2(&client, &ep, &model, &cap, profile.as_ref()).await;
            result.duration_ms = t0.elapsed().as_millis() as u64;

            // 更新共享 items 并推送进度
            let mut items = items_shared.lock().await;
            items[idx] = result;
            emit_progress(&app, &ep, &items, &started_at, false);
        });
        handles.push(handle);
    }

    // 等待全部完成
    for h in handles {
        let _ = h.await;
    }

    let items = match std::sync::Arc::try_unwrap(items_shared) {
        Ok(mutex) => mutex.into_inner(),
        Err(arc) => arc.lock().await.clone(),
    };

    // 推送完成
    emit_progress(&app, &ep, &items, &started_at, true);

    Ok(build_report(&ep, items, start.elapsed().as_millis() as u64))
}

fn emit_progress(
    app: &tauri::AppHandle,
    ep: &crate::models::AiEndpoint,
    items: &[crate::models::EndpointTestItem],
    started_at: &str,
    is_completed: bool,
) {
    let progress = crate::models::EndpointTestProgress {
        endpoint_id: ep.id.clone(),
        endpoint_name: ep.name.clone(),
        total_count: items.len(),
        pending_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Pending).count(),
        running_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Running).count(),
        success_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Success).count(),
        failed_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Failed).count(),
        skipped_count: items.iter().filter(|i| i.status == crate::models::TestStatus::Skipped).count(),
        items: items.to_vec(),
        is_completed,
        started_at: started_at.to_string(),
    };
    let _ = app.emit("endpoint-test-progress", &progress);
}

fn build_report(
    ep: &crate::models::AiEndpoint,
    items: Vec<crate::models::EndpointTestItem>,
    duration_ms: u64,
) -> crate::models::EndpointTestReport {
    let success_count = items.iter().filter(|i| i.status == crate::models::TestStatus::Success).count();
    let failed_count = items.iter().filter(|i| i.status == crate::models::TestStatus::Failed).count();
    let skipped_count = items.iter().filter(|i| i.status == crate::models::TestStatus::Skipped).count();
    crate::models::EndpointTestReport {
        endpoint_id: ep.id.clone(),
        endpoint_name: ep.name.clone(),
        endpoint_type_name: format!("{:?}", ep.endpoint_type),
        items: items.clone(),
        duration_ms,
        total_count: items.len(),
        success_count,
        failed_count,
        skipped_count,
    }
}

fn cap_label(cap: &crate::models::ModelCapability) -> String {
    use crate::models::ModelCapability;
    match cap {
        ModelCapability::Text => "文字".into(),
        ModelCapability::Image => "图片".into(),
        ModelCapability::Video => "视频".into(),
        ModelCapability::SpeechToText => "语音识别".into(),
        ModelCapability::TextToSpeech => "语音合成".into(),
    }
}

/// 解析最终认证头模式（对齐 C# GetEffectiveApiKeyHeaderMode 四级级联）
fn resolve_auth_mode(ep: &crate::models::AiEndpoint, profile: Option<&crate::models::VendorProfile>) -> String {
    let mode = ep.auth_header_mode.as_str();
    if mode != "auto" && !mode.is_empty() {
        return mode.to_string();
    }
    // auto → 从 profile 取默认
    if let Some(p) = profile {
        return p.default_auth_header.clone();
    }
    // 无 profile 时的平台默认
    if ep.is_azure() && ep.endpoint_type != crate::models::EndpointType::ApiManagementGateway {
        "api_key".into()
    } else {
        "bearer".into()
    }
}

fn resolve_api_version(ep: &crate::models::AiEndpoint, profile: Option<&crate::models::VendorProfile>) -> String {
    if let Some(v) = &ep.api_version {
        if !v.is_empty() {
            return v.clone();
        }
    }
    if let Some(p) = profile {
        if !p.default_api_version.is_empty() {
            return p.default_api_version.clone();
        }
    }
    "2025-03-01-preview".into()
}

fn build_authed_request(
    req: reqwest::RequestBuilder,
    ep: &crate::models::AiEndpoint,
    profile: Option<&crate::models::VendorProfile>,
) -> reqwest::RequestBuilder {
    let mode = resolve_auth_mode(ep, profile);
    match mode.as_str() {
        "bearer" => req.header("Authorization", format!("Bearer {}", ep.api_key)),
        "api_key" | "api_key_header" => req.header("api-key", &ep.api_key),
        _ => {
            // fallback: 所有 Azure 系（含 APIM）统一 api-key，非 Azure 用 Bearer
            if ep.is_azure() {
                req.header("api-key", &ep.api_key)
            } else {
                req.header("Authorization", format!("Bearer {}", ep.api_key))
            }
        }
    }
}

fn auth_mode_display(mode: &str) -> &str {
    match mode {
        "bearer" => "Bearer Token",
        "api_key" | "api_key_header" => "api-key Header",
        _ => "auto",
    }
}

/// 构建 URL 候选列表（对齐 C# EndpointProfileUrlBuilder.BuildConfiguredTextUrlCandidates）
fn build_url_candidates(
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    cap: &crate::models::ModelCapability,
    profile: Option<&crate::models::VendorProfile>,
) -> Vec<String> {
    use crate::models::{EndpointType, ModelCapability};
    let base = ep.url.trim_end_matches('/');
    let deploy = model.effective_deployment();
    let api_ver = resolve_api_version(ep, profile);

    // 如果 profile 有候选列表，使用它
    if let Some(p) = profile {
        let empty = Vec::new();
        let templates = match cap {
            ModelCapability::Text => &p.text_url_candidates,
            ModelCapability::Image => &p.image_url_candidates,
            ModelCapability::Video => &p.video_url_candidates,
            ModelCapability::SpeechToText => &p.audio_url_candidates,
            ModelCapability::TextToSpeech => &p.speech_url_candidates,
        };
        let templates = if templates.is_empty() { &empty } else { templates };
        if !templates.is_empty() {
            return templates
                .iter()
                .map(|t| {
                    t.replace("{baseUrl}", base)
                        .replace("{deployment}", deploy)
                        .replace("{apiVersion}", &api_ver)
                        .replace("{model}", &model.model_id)
                })
                .collect();
        }
    }

    // 无 profile 或无候选时的默认构建
    match (cap, &ep.endpoint_type) {
        (ModelCapability::Text, EndpointType::AzureOpenAi) => {
            vec![format!("{base}/openai/deployments/{deploy}/chat/completions?api-version={api_ver}")]
        }
        (ModelCapability::Text, _) => {
            vec![format!("{base}/v1/chat/completions")]
        }
        (ModelCapability::Image, EndpointType::AzureOpenAi) => {
            vec![format!("{base}/openai/deployments/{deploy}/images/generations?api-version={api_ver}")]
        }
        (ModelCapability::Image, _) => {
            vec![format!("{base}/v1/images/generations")]
        }
        (ModelCapability::Video, _) => {
            vec![format!("{base}/v1/video/generations")]
        }
        (ModelCapability::SpeechToText, EndpointType::AzureOpenAi) => {
            vec![format!("{base}/openai/deployments/{deploy}/audio/transcriptions?api-version={api_ver}")]
        }
        (ModelCapability::SpeechToText, _) => {
            vec![format!("{base}/v1/audio/transcriptions")]
        }
        (ModelCapability::TextToSpeech, _) => {
            vec![]
        }
    }
}

/// 构建请求摘要（对齐 C# RequestSummary）
fn build_request_summary(
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    cap: &crate::models::ModelCapability,
    profile: Option<&crate::models::VendorProfile>,
    url_idx: usize,
    total_urls: usize,
) -> String {
    let auth_mode = resolve_auth_mode(ep, profile);
    let auth = auth_mode_display(&auth_mode);
    let api_ver = resolve_api_version(ep, profile);
    let protocol = if let Some(p) = profile {
        if !p.text_protocol.is_empty() { p.text_protocol.as_str() } else { "chat_completions" }
    } else {
        "chat_completions"
    };
    let source = if profile.is_some() { "资料包" } else { "默认" };
    let branch = if total_urls > 1 {
        format!("候选 {}/{}", url_idx + 1, total_urls)
    } else {
        "唯一候选".into()
    };

    let mut lines = Vec::new();
    lines.push(format!("认证: {auth}"));
    lines.push(format!("基础地址: {}", ep.url));
    lines.push(format!("模型: {}", model.model_id));
    if !api_ver.is_empty() {
        lines.push(format!("API版本: {api_ver}"));
    }
    if matches!(cap, crate::models::ModelCapability::Text) {
        lines.push(format!("文本协议: {protocol} ({})", source));
    }
    lines.push(format!("测试来源: {source}"));
    lines.push(format!("测试分支: {} ({source}第 {} 条候选)", if url_idx == 0 { "主测试" } else { "回退测试" }, url_idx + 1));
    lines.join("\n")
}

/// V2 单能力测试——支持候选 URL 回退、流式、推理检测
async fn test_single_capability_v2(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    cap: &crate::models::ModelCapability,
    profile: Option<&crate::models::VendorProfile>,
) -> crate::models::EndpointTestItem {
    use crate::models::ModelCapability;

    let label = cap_label(cap);

    // STT / TTS 暂不支持
    if matches!(cap, ModelCapability::SpeechToText) {
        return crate::models::EndpointTestItem {
            model_id: model.model_id.clone(),
            capability: label.clone(),
            status: crate::models::TestStatus::Skipped,
            summary: "⏭ 语音识别测试暂未实现（需上传音频）".into(),
            detail: Some("语音识别测试需要上传音频文件，暂不支持一键测试".into()),
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        };
    }
    if matches!(cap, ModelCapability::TextToSpeech) {
        return crate::models::EndpointTestItem {
            model_id: model.model_id.clone(),
            capability: label.clone(),
            status: crate::models::TestStatus::Skipped,
            summary: "⏭ 语音合成测试暂未实现".into(),
            detail: None,
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        };
    }

    let candidates = build_url_candidates(ep, model, cap, profile);
    if candidates.is_empty() {
        let summary = format!("❌ {}无可用 URL 候选", &label);
        return crate::models::EndpointTestItem {
            model_id: model.model_id.clone(),
            capability: label,
            status: crate::models::TestStatus::Failed,
            summary,
            detail: Some("资料包中未声明该能力的 URL 候选列表".into()),
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        };
    }

    let total_urls = candidates.len();
    let mut urls_tried = Vec::new();

    // 逐候选测试，主 URL 成功即返回
    for (url_idx, url) in candidates.iter().enumerate() {
        urls_tried.push(url.clone());
        let request_summary = build_request_summary(ep, model, cap, profile, url_idx, total_urls);
        let branch = if url_idx == 0 {
            format!("主测试 (资料包第 1 条候选)")
        } else {
            format!("回退测试 (资料包第 {} 条候选)", url_idx + 1)
        };

        let result = match cap {
            ModelCapability::Text => {
                test_text_capability(client, ep, model, url, profile).await
            }
            ModelCapability::Image => {
                test_image_capability(client, ep, model, url, profile).await
            }
            ModelCapability::Video => {
                test_video_capability(client, ep, model, url, profile).await
            }
            _ => unreachable!(),
        };

        match result {
            Ok((summary, detail)) => {
                return crate::models::EndpointTestItem {
                    model_id: model.model_id.clone(),
                    capability: label.clone(),
                    status: crate::models::TestStatus::Success,
                    summary,
                    detail,
                    request_url: Some(format!("POST {url}")),
                    request_summary: Some(request_summary),
                    duration_ms: 0,
                    test_branch: Some(branch),
                    urls_tried: urls_tried.clone(),
                };
            }
            Err((summary, detail)) => {
                // 如果是主 URL 且有回退候选，继续尝试
                if url_idx < total_urls - 1 {
                    continue;
                }
                // 最后一个候选也失败了
                return crate::models::EndpointTestItem {
                    model_id: model.model_id.clone(),
                    capability: label.clone(),
                    status: crate::models::TestStatus::Failed,
                    summary,
                    detail: Some(detail),
                    request_url: Some(format!("POST {url}")),
                    request_summary: Some(request_summary),
                    duration_ms: 0,
                    test_branch: Some(format!("全部 {total_urls} 条候选均失败")),
                    urls_tried,
                };
            }
        }
    }

    // 不应到这里
    crate::models::EndpointTestItem {
        model_id: model.model_id.clone(),
        capability: label,
        status: crate::models::TestStatus::Failed,
        summary: "❌ 未知错误".into(),
        detail: None,
        request_url: None,
        request_summary: None,
        duration_ms: 0,
        test_branch: None,
        urls_tried,
    }
}

/// 文字能力测试——流式请求 + 推理检测
async fn test_text_capability(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    url: &str,
    profile: Option<&crate::models::VendorProfile>,
) -> Result<(String, Option<String>), (String, String)> {
    let is_responses = url.contains("/responses");
    // 仅 Azure 旧式 deployment 路由（/openai/deployments/{deploy}/...）由 URL 指定模型；
    // 其他所有情况（含 /openai/v1/、/v1/）都需要在 body 内提供 model
    let url_has_deployment = url.contains("/openai/deployments/");

    let body = if is_responses {
        // Responses API 格式 — 对齐 C# AiInsightService Responses body
        // input 必须是结构化数组（role + content[{type:"input_text",text}]），不能是字符串
        let mut b = serde_json::json!({
            "model": model.model_id,
            "input": [
                {
                    "role": "system",
                    "content": [{"type": "input_text", "text": "你是连通性测试助手，请用简短中文直接回复。"}]
                },
                {
                    "role": "user",
                    "content": [{"type": "input_text", "text": "计算 2+3 的结果"}]
                }
            ],
            "stream": true,
        });
        if url_has_deployment {
            b.as_object_mut().unwrap().remove("model");
        }
        b
    } else {
        // ChatCompletions 格式 — 对齐 C# AiInsightService Azure/ChatCompletions body
        // 不使用 max_tokens（gpt-5.x 等新模型会 400），保留 stream_options 以与 C# 一致
        let mut b = serde_json::json!({
            "model": model.model_id,
            "messages": [
                {"role": "system", "content": "你是连通性测试助手，请用简短中文直接回复。"},
                {"role": "user", "content": "计算 2+3 的结果"}
            ],
            "stream": true,
            "stream_options": { "include_usage": true }
        });
        if url_has_deployment {
            b.as_object_mut().unwrap().remove("model");
        }
        b
    };

    let req = build_authed_request(client.post(url), ep, profile).json(&body);

    match req.send().await {
        Ok(resp) => {
            let status = resp.status();
            let status_code = status.as_u16();
            if !status.is_success() {
                let body = resp.text().await.unwrap_or_default();
                let detail = parse_error_body(status_code, &body);
                return Err((
                    format!("❌ 文字测试失败 (HTTP {status_code})"),
                    detail,
                ));
            }

            // 读取流式响应
            let body_text = resp.text().await.unwrap_or_default();
            let (text_chunks, reasoning_chunks, model_name) =
                parse_stream_response(&body_text, is_responses);

            let total_chunks = text_chunks + reasoning_chunks;
            let has_reasoning = reasoning_chunks > 0;

            let mut summary = format!("✅ 文字测试通过");
            if has_reasoning {
                summary.push_str("。⚡ 推理可用");
            } else {
                summary.push_str("。⚠ 推理未返回（模型可能不支持 reasoning）");
            }

            let mut detail_lines = Vec::new();
            detail_lines.push(format!("返回片段: {total_chunks}"));
            if has_reasoning {
                detail_lines.push(format!("推理片段: {reasoning_chunks}"));
            }
            if let Some(m) = &model_name {
                detail_lines.push(format!("响应模型: {m}"));
            }

            Ok((summary, Some(detail_lines.join("\n"))))
        }
        Err(e) => {
            let detail = if e.is_timeout() {
                "请求超时（25秒），请检查网络或终结点地址是否可达".into()
            } else if e.is_connect() {
                format!("无法连接到服务器: {e}")
            } else {
                format!("网络错误: {e}")
            };
            Err((
                "❌ 文字连接失败".into(),
                detail,
            ))
        }
    }
}

/// 解析 SSE 流式响应，统计文字/推理片段数
fn parse_stream_response(body: &str, is_responses: bool) -> (usize, usize, Option<String>) {
    let mut text_chunks = 0usize;
    let mut reasoning_chunks = 0usize;
    let mut model_name: Option<String> = None;

    for line in body.lines() {
        let line = line.trim();
        if !line.starts_with("data: ") {
            continue;
        }
        let data = &line[6..];
        if data == "[DONE]" {
            break;
        }
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(data) {
            // 提取 model
            if model_name.is_none() {
                if let Some(m) = v.get("model").and_then(|m| m.as_str()) {
                    model_name = Some(m.to_string());
                }
            }

            if is_responses {
                // Responses API: output_text.delta / reasoning.delta 等
                if let Some(t) = v.get("type").and_then(|t| t.as_str()) {
                    match t {
                        "response.output_text.delta" => text_chunks += 1,
                        "response.reasoning.delta" | "response.reasoning_summary_text.delta" => reasoning_chunks += 1,
                        _ => {}
                    }
                }
            } else {
                // ChatCompletions SSE: choices[0].delta
                if let Some(choices) = v.get("choices").and_then(|c| c.as_array()) {
                    if let Some(delta) = choices.first().and_then(|c| c.get("delta")) {
                        if delta.get("content").and_then(|c| c.as_str()).map_or(false, |s| !s.is_empty()) {
                            text_chunks += 1;
                        }
                        if delta.get("reasoning_content").and_then(|c| c.as_str()).map_or(false, |s| !s.is_empty()) {
                            reasoning_chunks += 1;
                        }
                    }
                }
            }
        }
    }

    (text_chunks, reasoning_chunks, model_name)
}

/// 图片能力测试
async fn test_image_capability(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    url: &str,
    profile: Option<&crate::models::VendorProfile>,
) -> Result<(String, Option<String>), (String, String)> {
    // 对齐 C# AiImageGenService.SendImageGenerateRequestAsync body 格式
    let body = serde_json::json!({
        "prompt": "一只卡通兔子",
        "model": model.model_id,
        "size": "1024x1024",
        "quality": "low",
        "output_format": "png",
    });
    let req = build_authed_request(client.post(url), ep, profile).json(&body);

    match req.send().await {
        Ok(resp) => {
            let status = resp.status();
            let status_code = status.as_u16();
            if !status.is_success() {
                let body = resp.text().await.unwrap_or_default();
                return Err((
                    format!("❌ 图片测试失败 (HTTP {status_code})"),
                    parse_error_body(status_code, &body),
                ));
            }
            let body = resp.text().await.unwrap_or_default();
            let image_count = serde_json::from_str::<serde_json::Value>(&body)
                .ok()
                .and_then(|v| v.get("data").and_then(|d| d.as_array()).map(|a| a.len()))
                .unwrap_or(0);
            Ok((
                format!("✅ 图片生成成功 (返回 {image_count} 张)"),
                Some(format!("图片数量: {image_count}")),
            ))
        }
        Err(e) => Err((
            "❌ 图片连接失败".into(),
            format!("网络错误: {e}"),
        )),
    }
}

/// 视频能力测试——仅验证创建接口可达
async fn test_video_capability(
    client: &reqwest::Client,
    ep: &crate::models::AiEndpoint,
    model: &crate::models::AiModelEntry,
    url: &str,
    profile: Option<&crate::models::VendorProfile>,
) -> Result<(String, Option<String>), (String, String)> {
    let body = serde_json::json!({
        "prompt": "一只卡通兔子在草地上跳跃",
        "model": model.model_id,
    });
    let req = build_authed_request(client.post(url), ep, profile).json(&body);

    match req.send().await {
        Ok(resp) => {
            let status = resp.status();
            let status_code = status.as_u16();
            // 视频创建通常返回 200 或 202
            if status.is_success() || status_code == 202 {
                let body = resp.text().await.unwrap_or_default();
                let video_id = serde_json::from_str::<serde_json::Value>(&body)
                    .ok()
                    .and_then(|v| {
                        v.get("id").or_else(|| v.get("video_id"))
                            .and_then(|id| id.as_str().map(String::from))
                    });
                let summary = if let Some(vid) = &video_id {
                    format!("✅ 视频创建已提交 (ID: {vid})")
                } else {
                    "✅ 视频接口连通成功".into()
                };
                Ok((summary, video_id.map(|v| format!("video_id: {v}"))))
            } else {
                let body = resp.text().await.unwrap_or_default();
                Err((
                    format!("❌ 视频测试失败 (HTTP {status_code})"),
                    parse_error_body(status_code, &body),
                ))
            }
        }
        Err(e) => Err((
            "❌ 视频连接失败".into(),
            format!("网络错误: {e}"),
        )),
    }
}

/// Speech 端点独立测试
async fn test_speech_endpoint(ep: &crate::models::AiEndpoint) -> Vec<crate::models::EndpointTestItem> {
    let mut items = Vec::new();
    let key = if !ep.speech_subscription_key.is_empty() {
        &ep.speech_subscription_key
    } else {
        &ep.api_key
    };
    let region = &ep.speech_region;
    let endpoint = &ep.speech_endpoint;

    if key.is_empty() {
        items.push(crate::models::EndpointTestItem {
            model_id: "speech-sdk".into(),
            capability: "语音翻译".into(),
            status: crate::models::TestStatus::Failed,
            summary: "❌ 订阅密钥为空".into(),
            detail: Some("请在终结点配置中填写 Speech 订阅密钥".into()),
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        });
        return items;
    }

    let t0 = std::time::Instant::now();
    let test_url = if !endpoint.is_empty() {
        let base = endpoint.trim_end_matches('/');
        if base.contains("/sts/") { base.to_string() } else { format!("{base}/sts/v1.0/issuetoken") }
    } else if !region.is_empty() {
        format!("https://{region}.api.cognitive.microsoft.com/sts/v1.0/issuetoken")
    } else {
        items.push(crate::models::EndpointTestItem {
            model_id: "speech-sdk".into(),
            capability: "语音翻译".into(),
            status: crate::models::TestStatus::Failed,
            summary: "❌ 区域和终结点均为空".into(),
            detail: Some("请至少填写区域或终结点".into()),
            request_url: None,
            request_summary: None,
            duration_ms: 0,
            test_branch: None,
            urls_tried: vec![],
        });
        return items;
    };

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(15))
        .build()
        .unwrap();

    let resp = client
        .post(&test_url)
        .header("Ocp-Apim-Subscription-Key", key)
        .header("Content-Length", "0")
        .send()
        .await;

    let dur = t0.elapsed().as_millis() as u64;
    let region_display = if !region.is_empty() { region.as_str() } else { "自定义终结点" };

    let request_summary = format!(
        "认证: Ocp-Apim-Subscription-Key\n\
         终结点: {test_url}\n\
         区域: {region_display}"
    );

    match resp {
        Ok(r) if r.status().is_success() => {
            items.push(crate::models::EndpointTestItem {
                model_id: "speech-sdk".into(),
                capability: "语音翻译".into(),
                status: crate::models::TestStatus::Success,
                summary: format!("✅ Speech 连通成功 (区域: {region_display})"),
                detail: None,
                request_url: Some(format!("POST {test_url}")),
                request_summary: Some(request_summary),
                duration_ms: dur,
                test_branch: Some("Token Issue 接口".into()),
                urls_tried: vec![test_url],
            });
        }
        Ok(r) => {
            let status = r.status().as_u16();
            let body = r.text().await.unwrap_or_default();
            items.push(crate::models::EndpointTestItem {
                model_id: "speech-sdk".into(),
                capability: "语音翻译".into(),
                status: crate::models::TestStatus::Failed,
                summary: format!("❌ Speech 认证失败 (HTTP {status})"),
                detail: Some(if status == 401 {
                    "订阅密钥无效或已过期，请检查密钥和区域是否匹配".into()
                } else {
                    body
                }),
                request_url: Some(format!("POST {test_url}")),
                request_summary: Some(request_summary),
                duration_ms: dur,
                test_branch: Some("Token Issue 接口".into()),
                urls_tried: vec![test_url],
            });
        }
        Err(e) => {
            items.push(crate::models::EndpointTestItem {
                model_id: "speech-sdk".into(),
                capability: "语音翻译".into(),
                status: crate::models::TestStatus::Failed,
                summary: "❌ Speech 连接失败".into(),
                detail: Some(format!("无法连接: {e}")),
                request_url: Some(format!("POST {test_url}")),
                request_summary: Some(request_summary),
                duration_ms: dur,
                test_branch: Some("Token Issue 接口".into()),
                urls_tried: vec![test_url],
            });
        }
    }

    items
}

/// O-49: 测试 TTS voices 列表接口 — 验证 STT/TTS 能力
async fn test_speech_tts_voices(ep: &crate::models::AiEndpoint) -> crate::models::EndpointTestItem {
    let key = if !ep.speech_subscription_key.is_empty() { &ep.speech_subscription_key } else { &ep.api_key };
    let region = &ep.speech_region;
    let voices_url = if !ep.speech_endpoint.is_empty() {
        let base = ep.speech_endpoint.trim_end_matches('/');
        format!("{base}/cognitiveservices/voices/list")
    } else if !region.is_empty() {
        format!("https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list")
    } else {
        return crate::models::EndpointTestItem {
            model_id: "speech-tts".into(),
            capability: "TTS 语音合成".into(),
            status: crate::models::TestStatus::Skipped,
            summary: "⏭ 区域为空，跳过 TTS 测试".into(),
            detail: None, request_url: None, request_summary: None,
            duration_ms: 0, test_branch: None, urls_tried: vec![],
        };
    };

    let t0 = std::time::Instant::now();
    let client = reqwest::Client::builder().timeout(std::time::Duration::from_secs(10)).build().unwrap();
    let resp = client.get(&voices_url).header("Ocp-Apim-Subscription-Key", key).send().await;
    let dur = t0.elapsed().as_millis() as u64;

    match resp {
        Ok(r) if r.status().is_success() => {
            let body = r.text().await.unwrap_or_default();
            let count = serde_json::from_str::<Vec<serde_json::Value>>(&body).map(|v| v.len()).unwrap_or(0);
            crate::models::EndpointTestItem {
                model_id: "speech-tts".into(),
                capability: "TTS 语音合成".into(),
                status: crate::models::TestStatus::Success,
                summary: format!("✅ TTS 可用，{count} 个语音"),
                detail: None,
                request_url: Some(format!("GET {voices_url}")),
                request_summary: None, duration_ms: dur,
                test_branch: Some("Voices List API".into()),
                urls_tried: vec![voices_url],
            }
        }
        Ok(r) => {
            let status = r.status().as_u16();
            crate::models::EndpointTestItem {
                model_id: "speech-tts".into(),
                capability: "TTS 语音合成".into(),
                status: crate::models::TestStatus::Failed,
                summary: format!("❌ TTS 测试失败 (HTTP {status})"),
                detail: None,
                request_url: Some(format!("GET {voices_url}")),
                request_summary: None, duration_ms: dur,
                test_branch: Some("Voices List API".into()),
                urls_tried: vec![voices_url],
            }
        }
        Err(e) => crate::models::EndpointTestItem {
            model_id: "speech-tts".into(),
            capability: "TTS 语音合成".into(),
            status: crate::models::TestStatus::Failed,
            summary: "❌ TTS 连接失败".into(),
            detail: Some(format!("{e}")),
            request_url: Some(format!("GET {voices_url}")),
            request_summary: None, duration_ms: dur,
            test_branch: Some("Voices List API".into()),
            urls_tried: vec![voices_url],
        },
    }
}

/// 解析错误响应体，提取人类可读的错误信息
fn parse_error_body(status_code: u16, body: &str) -> String {
    // 尝试解析 JSON 错误
    if let Ok(v) = serde_json::from_str::<serde_json::Value>(body) {
        // OpenAI 格式: {"error": {"message": "...", "type": "...", "code": "..."}}
        if let Some(err) = v.get("error") {
            let msg = err
                .get("message")
                .and_then(|m| m.as_str())
                .unwrap_or("未知错误");
            let code = err
                .get("code")
                .and_then(|c| c.as_str())
                .unwrap_or("");
            let err_type = err
                .get("type")
                .and_then(|t| t.as_str())
                .unwrap_or("");
            let mut detail = format!("HTTP {status_code}");
            if !code.is_empty() {
                detail.push_str(&format!(" | 错误码: {code}"));
            }
            if !err_type.is_empty() {
                detail.push_str(&format!(" | 类型: {err_type}"));
            }
            detail.push_str(&format!("\n{msg}"));
            return detail;
        }
        // 有些 API 返回 {"message": "..."}
        if let Some(msg) = v.get("message").and_then(|m| m.as_str()) {
            return format!("HTTP {status_code} | {msg}");
        }
        // 有 statusCode 字段的
        if let Some(_status) = v.get("statusCode") {
            let message = v
                .get("message")
                .and_then(|m| m.as_str())
                .unwrap_or("未知错误");
            return format!("HTTP {status_code} | {message}");
        }
    }
    // 非 JSON 或解析失败
    if body.len() > 500 {
        format!("HTTP {status_code} | {}", &body[..500])
    } else if body.is_empty() {
        format!("HTTP {status_code} (无响应体)")
    } else {
        format!("HTTP {status_code} | {body}")
    }
}

/// 获取厂商资料包列表（内置）
/// 获取厂商资料包列表（从嵌入的 JSON 解析，与 C# 共用同一份 JSON）
#[tauri::command]
pub async fn get_vendor_profiles() -> Result<Vec<crate::models::VendorProfile>, String> {
    Ok(build_vendor_profiles())
}

fn build_vendor_profiles() -> Vec<crate::models::VendorProfile> {
    crate::profile_loader::load_profiles()
}

/// 从终结点自动发现可用模型列表
#[tauri::command]
pub async fn discover_models(
    state: State<'_, AppState>,
    endpoint_id: String,
) -> Result<Vec<crate::models::DiscoveredModel>, String> {
    let config = state.config.read().await;
    let ep = config
        .endpoints
        .iter()
        .find(|e| e.id == endpoint_id)
        .ok_or("端点不存在")?
        .clone();
    drop(config);

    if ep.is_azure() && ep.endpoint_type == crate::models::EndpointType::AzureOpenAi {
        return Err("Azure OpenAI 终结点不支持自动发现模型，请手动添加部署名称".into());
    }
    if ep.url.trim().is_empty() {
        return Err("端点 URL 为空".into());
    }
    if ep.api_key.trim().is_empty() {
        return Err("API Key 为空".into());
    }

    let client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(20))
        .build()
        .map_err(|e| e.to_string())?;

    let base = ep.url.trim_end_matches('/');
    let candidates = match ep.endpoint_type {
        crate::models::EndpointType::ApiManagementGateway => {
            vec![format!("{base}/models"), format!("{base}/v1/models")]
        }
        _ => {
            vec![format!("{base}/v1/models"), format!("{base}/models")]
        }
    };

    let profiles = build_vendor_profiles();
    let profile = profiles.iter().find(|p| p.endpoint_type == ep.endpoint_type);

    for url in &candidates {
        let req = build_authed_request(client.get(url), &ep, profile);
        match req.send().await {
            Ok(resp) if resp.status().is_success() => {
                let body = resp.text().await.unwrap_or_default();
                if let Ok(models) = parse_model_list(&body) {
                    if !models.is_empty() {
                        return Ok(models);
                    }
                }
            }
            _ => continue,
        }
    }

    Err(format!(
        "无法从以下地址发现模型: {}",
        candidates.join(", ")
    ))
}

fn parse_model_list(body: &str) -> Result<Vec<crate::models::DiscoveredModel>, ()> {
    let v: serde_json::Value = serde_json::from_str(body).map_err(|_| ())?;

    // OpenAI 格式: {"data": [{"id": "..."}]}
    let arr = v
        .get("data")
        .and_then(|d| d.as_array())
        // {"models": [...]}
        .or_else(|| v.get("models").and_then(|m| m.as_array()))
        // {"value": [...]}
        .or_else(|| v.get("value").and_then(|m| m.as_array()))
        // 顶层数组
        .or_else(|| v.as_array());

    let arr = arr.ok_or(())?;

    let models: Vec<crate::models::DiscoveredModel> = arr
        .iter()
        .filter_map(|item| {
            // 支持字符串或对象
            if let Some(s) = item.as_str() {
                return Some(crate::models::DiscoveredModel {
                    id: s.to_string(),
                    display_name: None,
                    owned_by: None,
                });
            }
            let id = item
                .get("id")
                .or_else(|| item.get("model"))
                .or_else(|| item.get("name"))
                .and_then(|v| v.as_str())?;
            Some(crate::models::DiscoveredModel {
                id: id.to_string(),
                display_name: item
                    .get("display_name")
                    .and_then(|v| v.as_str())
                    .map(String::from),
                owned_by: item
                    .get("owned_by")
                    .and_then(|v| v.as_str())
                    .map(String::from),
            })
        })
        .collect();

    Ok(models)
}
