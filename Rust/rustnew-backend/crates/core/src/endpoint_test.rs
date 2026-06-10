//! 终结点连通性测试 —— 复刻自 C# `EndpointBatchTestService` 的核心思路。
//!
//! 目标：在**没有实际消费端**的情况下，按厂商资料包声明的 URL 候选，
//! 对 AI 终结点发起一次极短的文本请求，验证「地址 / 鉴权 / 路由」是否打通。
//!
//! 范围裁剪（当前期）：仅实现**文字连通性测试**。
//! - 图片 / 语音转写 / 视频 / Realtime：Rust 侧尚无对应消费服务，先产出「暂跳过」项。
//! - AAD 鉴权：Rust 侧尚无 Entra 令牌体系，先产出「暂跳过」项。
//!
//! 主→回退：第 1 条候选为主测试，主测试通过则其余候选记为「已跳过」；
//! 主测试失败则依次尝试回退候选（对齐 C# `RunPrimaryThenFallback`）。

use std::time::{Duration, Instant};

use serde::Serialize;

use crate::endpoint::{AiEndpoint, ApiKeyHeaderMode, AuthMode, EndpointKind};
use crate::endpoint_profile::EndpointProfile;

const TEXT_TIMEOUT_SECS: u64 = 20;
const SYSTEM_PROMPT: &str = "你是连通性测试助手，请用简短中文直接回复。";
const USER_PROMPT: &str = "计算 2+3 的结果";

/// 单条测试结果（序列化为 camelCase 供前端消费）。
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EndpointTestItem {
    pub order: u32,
    pub model_id: String,
    /// 能力中文名，如「文字」。
    pub capability: String,
    /// `Success` / `Failed` / `Skipped`。
    pub status: String,
    pub summary: String,
    pub details: String,
    pub request_url: String,
    pub duration_ms: u64,
}

impl EndpointTestItem {
    fn new(
        order: u32,
        model_id: impl Into<String>,
        capability: impl Into<String>,
        status: &str,
        summary: impl Into<String>,
        details: impl Into<String>,
        request_url: impl Into<String>,
        duration_ms: u64,
    ) -> Self {
        Self {
            order,
            model_id: model_id.into(),
            capability: capability.into(),
            status: status.to_string(),
            summary: summary.into(),
            details: details.into(),
            request_url: request_url.into(),
            duration_ms,
        }
    }
}

/// 对单个终结点执行连通性测试（仅文字）。
///
/// `profile` 为该终结点绑定的资料包（可空：缺资料包时无法构建 URL，直接判失败）。
pub async fn test_endpoint_text(
    endpoint: &AiEndpoint,
    profile: Option<&EndpointProfile>,
) -> Vec<EndpointTestItem> {
    let mut items: Vec<EndpointTestItem> = Vec::new();
    let mut order: u32 = 0;

    // ---- 前置校验（对齐 C# 的整体跳过 / 失败项）----
    if endpoint.base_url.trim().is_empty() {
        items.push(EndpointTestItem::new(
            order,
            "",
            "整体",
            "Failed",
            "未填写 API 地址，无法测试。",
            "请先在终结点表单里填写「基础地址」后再测试。",
            "",
            0,
        ));
        return items;
    }

    if endpoint.auth_mode == AuthMode::Aad {
        items.push(EndpointTestItem::new(
            order,
            "",
            "整体",
            "Skipped",
            "AAD 鉴权暂未支持一键测试。",
            "Rust 版当前仅支持 API Key 鉴权的连通性测试；Microsoft Entra ID (AAD) 登录态体系尚未迁移。",
            "",
            0,
        ));
        return items;
    }

    if endpoint.api_key.trim().is_empty() {
        items.push(EndpointTestItem::new(
            order,
            "",
            "整体",
            "Failed",
            "未填写 API 密钥，无法测试。",
            "请先在终结点表单里填写 API Key 后再测试。",
            "",
            0,
        ));
        return items;
    }

    let Some(profile) = profile else {
        items.push(EndpointTestItem::new(
            order,
            "",
            "整体",
            "Failed",
            "未找到绑定的厂商资料包，无法构建测试 URL。",
            "该终结点的 profileId 在内置资料包目录中不存在；请重新选择厂商资料包后再测试。",
            "",
            0,
        ));
        return items;
    };

    let models = &endpoint.models;
    if models.is_empty() {
        items.push(EndpointTestItem::new(
            order,
            "",
            "整体",
            "Skipped",
            "当前终结点未配置任何模型。",
            "请先为这个终结点添加至少一个文字模型，再发起测试。",
            "",
            0,
        ));
        return items;
    }

    let is_azure = endpoint.kind.is_azure_openai();
    let client = match build_client() {
        Ok(c) => c,
        Err(e) => {
            items.push(EndpointTestItem::new(
                order,
                "",
                "整体",
                "Failed",
                "HTTP 客户端初始化失败。",
                format!("无法创建测试用 HTTP 客户端：{e}"),
                "",
                0,
            ));
            return items;
        }
    };

    let mut tested_any = false;
    for model in models {
        // 只测试声明了「文字」能力的模型。
        if !model.capabilities.contains(crate::capability::ModelCapability::TEXT) {
            items.push(EndpointTestItem::new(
                order,
                model.model_id.clone(),
                "其它能力",
                "Skipped",
                "当前仅支持文字连通性测试，已跳过。",
                "该模型未标记「文字」能力；图片 / 语音 / 视频的快速测试尚未在 Rust 版接入。",
                "",
                0,
            ));
            order += 1;
            continue;
        }

        if model.model_id.trim().is_empty() {
            items.push(EndpointTestItem::new(
                order,
                "",
                "文字",
                "Skipped",
                "模型 ID 未填写，已跳过。",
                "空白模型行不会发起测试。",
                "",
                0,
            ));
            order += 1;
            continue;
        }

        tested_any = true;
        let deployment = if !model.deployment_name.trim().is_empty() {
            model.deployment_name.trim()
        } else {
            model.model_id.trim()
        };

        let urls = profile.build_text_url_candidates(
            &endpoint.base_url,
            is_azure,
            deployment,
            &endpoint.api_version,
        );

        if urls.is_empty() {
            items.push(EndpointTestItem::new(
                order,
                model.model_id.clone(),
                "文字",
                "Failed",
                "资料包未声明可用的文本 URL 候选。",
                "当前严格按资料包执行；该资料包没有给出文本接口的候选 URL，请补齐资料包后再测。",
                "",
                0,
            ));
            order += 1;
            continue;
        }

        // 主 → 回退：逐条尝试，命中成功即停；其余记为已跳过。
        let mut succeeded = false;
        for (idx, url) in urls.iter().enumerate() {
            if succeeded {
                items.push(EndpointTestItem::new(
                    order,
                    model.model_id.clone(),
                    if idx == 0 { "文字".to_string() } else { format!("文字（回退 {idx}）") },
                    "Skipped",
                    "上一候选已通过，本回退地址已跳过。",
                    format!("候选 URL：{url}"),
                    url.clone(),
                    0,
                ));
                order += 1;
                continue;
            }

            let route_label = if idx == 0 {
                "主测试（资料包第 1 条候选）".to_string()
            } else {
                format!("回退测试 {idx}（资料包第 {} 条候选）", idx + 1)
            };
            let capability_name = if idx == 0 {
                "文字".to_string()
            } else {
                format!("文字（回退 {idx}）")
            };

            let outcome = run_text_probe(&client, endpoint, deployment, url).await;
            order = push_outcome(
                &mut items,
                order,
                &model.model_id,
                &capability_name,
                &route_label,
                url,
                outcome,
                &mut succeeded,
            );
        }
    }

    if !tested_any && items.is_empty() {
        items.push(EndpointTestItem::new(
            0,
            "",
            "整体",
            "Skipped",
            "没有可测试的文字模型。",
            "请先为终结点添加至少一个标记了「文字」能力且填写了模型 ID 的模型。",
            "",
            0,
        ));
    }

    items
}

struct ProbeOutcome {
    success: bool,
    summary: String,
    details: String,
    duration_ms: u64,
}

#[allow(clippy::too_many_arguments)]
fn push_outcome(
    items: &mut Vec<EndpointTestItem>,
    order: u32,
    model_id: &str,
    capability_name: &str,
    route_label: &str,
    url: &str,
    outcome: ProbeOutcome,
    succeeded: &mut bool,
) -> u32 {
    if outcome.success {
        *succeeded = true;
    }
    items.push(EndpointTestItem::new(
        order,
        model_id,
        capability_name,
        if outcome.success { "Success" } else { "Failed" },
        outcome.summary,
        format!("{}\n分支：{route_label}", outcome.details),
        url,
        outcome.duration_ms,
    ));
    order + 1
}

fn build_client() -> reqwest::Result<reqwest::Client> {
    reqwest::Client::builder()
        .timeout(Duration::from_secs(TEXT_TIMEOUT_SECS))
        .build()
}

/// 发一次最小文本请求并判定连通性。
async fn run_text_probe(
    client: &reqwest::Client,
    endpoint: &AiEndpoint,
    deployment: &str,
    url: &str,
) -> ProbeOutcome {
    let started = Instant::now();
    let is_responses = url_is_responses(url);
    let body = if is_responses {
        build_responses_body(deployment)
    } else {
        build_chat_body(deployment)
    };

    let mut req = client.post(url).json(&body);
    req = apply_auth(req, endpoint);

    let result = req.send().await;
    let elapsed = started.elapsed().as_millis() as u64;

    match result {
        Ok(resp) => {
            let status = resp.status();
            let text = resp.text().await.unwrap_or_default();
            if status.is_success() {
                let snippet = extract_text_snippet(&text, is_responses);
                let summary = if snippet.is_empty() {
                    format!("连通成功（HTTP {}），但未解析出文本片段。", status.as_u16())
                } else {
                    format!("文字测试通过（HTTP {}）。", status.as_u16())
                };
                let details = if snippet.is_empty() {
                    format!(
                        "协议：{}\n返回前 300 字：{}",
                        if is_responses { "Responses" } else { "chat/completions" },
                        truncate(&text, 300)
                    )
                } else {
                    format!(
                        "协议：{}\n返回片段：{}",
                        if is_responses { "Responses" } else { "chat/completions" },
                        snippet
                    )
                };
                ProbeOutcome { success: true, summary, details, duration_ms: elapsed }
            } else {
                ProbeOutcome {
                    success: false,
                    summary: format!("文字测试失败（HTTP {}）。", status.as_u16()),
                    details: format!(
                        "协议：{}\n状态码：{}\n返回前 500 字：{}",
                        if is_responses { "Responses" } else { "chat/completions" },
                        status.as_u16(),
                        truncate(&text, 500)
                    ),
                    duration_ms: elapsed,
                }
            }
        }
        Err(e) => {
            let reason = if e.is_timeout() {
                format!("请求超时（>{TEXT_TIMEOUT_SECS}s）。请检查地址、网络连通性或防火墙。")
            } else if e.is_connect() {
                "无法建立连接。请检查基础地址是否正确、可达。".to_string()
            } else {
                format!("请求发送失败：{e}")
            };
            ProbeOutcome {
                success: false,
                summary: "文字测试失败。".to_string(),
                details: reason,
                duration_ms: elapsed,
            }
        }
    }
}

fn url_is_responses(url: &str) -> bool {
    // 取问号前的路径部分判断，避免 query 误判。
    let path = url.split('?').next().unwrap_or(url);
    path.to_ascii_lowercase().ends_with("/responses") || path.to_ascii_lowercase().contains("/responses")
}

fn build_chat_body(model: &str) -> serde_json::Value {
    serde_json::json!({
        "model": model,
        "messages": [
            { "role": "system", "content": SYSTEM_PROMPT },
            { "role": "user", "content": USER_PROMPT }
        ],
        "max_tokens": 64,
        "temperature": 0,
        "stream": false
    })
}

fn build_responses_body(model: &str) -> serde_json::Value {
    serde_json::json!({
        "model": model,
        "instructions": SYSTEM_PROMPT,
        "input": USER_PROMPT,
        "max_output_tokens": 64
    })
}

/// 按终结点鉴权设置附加请求头。
fn apply_auth(req: reqwest::RequestBuilder, endpoint: &AiEndpoint) -> reqwest::RequestBuilder {
    let key = endpoint.api_key.trim();
    let mode = match endpoint.api_key_header_mode {
        ApiKeyHeaderMode::Auto => {
            // Auto：Azure / APIM 走 api-key header，其它走 Bearer。
            match endpoint.kind {
                EndpointKind::AzureOpenAi | EndpointKind::ApiManagementGateway => {
                    ApiKeyHeaderMode::ApiKeyHeader
                }
                _ => ApiKeyHeaderMode::Bearer,
            }
        }
        other => other,
    };
    match mode {
        ApiKeyHeaderMode::ApiKeyHeader => req.header("api-key", key),
        _ => req.header("Authorization", format!("Bearer {key}")),
    }
}

/// 从返回 JSON 中尽量解析出一段文本（兼容 chat/completions 与 Responses）。
fn extract_text_snippet(body: &str, is_responses: bool) -> String {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(body) else {
        return String::new();
    };

    if is_responses {
        // 1) 便捷字段 output_text
        if let Some(s) = v.get("output_text").and_then(|x| x.as_str()) {
            if !s.trim().is_empty() {
                return truncate(s.trim(), 160);
            }
        }
        // 2) output[].content[].text
        if let Some(out) = v.get("output").and_then(|x| x.as_array()) {
            for msg in out {
                if let Some(content) = msg.get("content").and_then(|x| x.as_array()) {
                    for c in content {
                        if let Some(t) = c.get("text").and_then(|x| x.as_str()) {
                            if !t.trim().is_empty() {
                                return truncate(t.trim(), 160);
                            }
                        }
                    }
                }
            }
        }
    }

    // chat/completions：choices[0].message.content
    if let Some(s) = v
        .pointer("/choices/0/message/content")
        .and_then(|x| x.as_str())
    {
        if !s.trim().is_empty() {
            return truncate(s.trim(), 160);
        }
    }

    String::new()
}

fn truncate(s: &str, max_chars: usize) -> String {
    let trimmed = s.trim();
    if trimmed.chars().count() <= max_chars {
        return trimmed.to_string();
    }
    let cut: String = trimmed.chars().take(max_chars).collect();
    format!("{cut}…")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn responses_url_detected() {
        assert!(url_is_responses("https://r.openai.azure.com/openai/v1/responses"));
        assert!(url_is_responses("https://gw/responses?api-version=2024-02-01"));
        assert!(!url_is_responses("https://api/v1/chat/completions"));
    }

    #[test]
    fn extract_chat_content() {
        let body = r#"{"choices":[{"message":{"content":"答案是 5"}}]}"#;
        assert_eq!(extract_text_snippet(body, false), "答案是 5");
    }

    #[test]
    fn extract_responses_output_text() {
        let body = r#"{"output_text":"等于 5"}"#;
        assert_eq!(extract_text_snippet(body, true), "等于 5");
    }

    #[test]
    fn extract_responses_nested_output() {
        let body = r#"{"output":[{"content":[{"type":"output_text","text":"结果 5"}]}]}"#;
        assert_eq!(extract_text_snippet(body, true), "结果 5");
    }
}
