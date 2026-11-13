//! 微软 Speech STS 令牌签发（`/sts/v1.0/issueToken`）与终结点连通性校验。
//!
//! 兼容国际版 Global (`https://<region>.api.cognitive.microsoft.com/sts/v1.0/issueToken`)
//! 与中国区世纪互联 21Vianet (`https://<region>.api.cognitive.azure.cn/sts/v1.0/issueToken`)。

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tokio::sync::Mutex;
use voice_ime_config::{parse_speech_endpoint, ResolvedSpeechEndpoint};

/// STS JWT 默认有效期 10 分钟；缓存 8 分钟提前刷新，避免临界过期。
const TOKEN_CACHE_TTL: Duration = Duration::from_secs(8 * 60);
const STS_HTTP_TIMEOUT: Duration = Duration::from_secs(10);

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SubscriptionValidationOutcome {
    pub valid: bool,
    pub region: String,
    pub cloud_label: String,
    pub sts_url: String,
    pub latency_ms: u64,
    pub message: String,
}

#[derive(Clone)]
struct CachedToken {
    token: String,
    issued_at: Instant,
}

#[derive(Clone, Default)]
pub struct StsTokenCache {
    entries: Arc<Mutex<HashMap<String, CachedToken>>>,
    client: reqwest::Client,
}

impl StsTokenCache {
    pub fn new() -> Self {
        let client = reqwest::Client::builder()
            .timeout(STS_HTTP_TIMEOUT)
            .build()
            .unwrap_or_default();
        Self {
            entries: Arc::new(Mutex::new(HashMap::new())),
            client,
        }
    }

    /// 获取或签发 STS ******
    pub async fn get_or_issue_token(
        &self,
        endpoint: &ResolvedSpeechEndpoint,
        subscription_key: &str,
    ) -> Result<String, String> {
        let cache_key = Self::make_cache_key(&endpoint.sts_issue_token_url, subscription_key);
        {
            let guard = self.entries.lock().await;
            if let Some(entry) = guard.get(&cache_key) {
                if entry.issued_at.elapsed() < TOKEN_CACHE_TTL {
                    return Ok(entry.token.clone());
                }
            }
        }

        let token = issue_sts_token(&self.client, &endpoint.sts_issue_token_url, subscription_key)
            .await
            .map_err(|(_, msg)| msg)?;

        let mut guard = self.entries.lock().await;
        guard.insert(
            cache_key,
            CachedToken {
                token: token.clone(),
                issued_at: Instant::now(),
            },
        );
        Ok(token)
    }

    /// 在设置界面执行一键连通性验证（对齐 C# `AzureSubscriptionValidator`）。
    pub async fn validate_endpoint_and_key(
        &self,
        raw_endpoint: &str,
        subscription_key: &str,
    ) -> SubscriptionValidationOutcome {
        let trimmed_key = subscription_key.trim();
        if trimmed_key.len() < 16 {
            return SubscriptionValidationOutcome {
                valid: false,
                region: String::new(),
                cloud_label: String::new(),
                sts_url: String::new(),
                latency_ms: 0,
                message: "订阅密钥为空或长度不正确，请检查密钥。".into(),
            };
        }

        let resolved = match parse_speech_endpoint(raw_endpoint) {
            Ok(r) => r,
            Err(e) => {
                return SubscriptionValidationOutcome {
                    valid: false,
                    region: String::new(),
                    cloud_label: String::new(),
                    sts_url: String::new(),
                    latency_ms: 0,
                    message: e.to_string(),
                };
            }
        };

        let started = Instant::now();
        match issue_sts_token(&self.client, &resolved.sts_issue_token_url, trimmed_key).await {
            Ok(token) => {
                let elapsed = started.elapsed().as_millis() as u64;
                let cache_key = Self::make_cache_key(&resolved.sts_issue_token_url, trimmed_key);
                self.entries.lock().await.insert(
                    cache_key,
                    CachedToken {
                        token,
                        issued_at: Instant::now(),
                    },
                );
                SubscriptionValidationOutcome {
                    valid: true,
                    region: resolved.region.clone(),
                    cloud_label: resolved.cloud.display_label().to_string(),
                    sts_url: resolved.sts_issue_token_url,
                    latency_ms: elapsed,
                    message: format!(
                        "验证成功 · {} ({}) · 耗时 {} ms",
                        resolved.region,
                        resolved.cloud.display_label(),
                        elapsed
                    ),
                }
            }
            Err((elapsed, msg)) => SubscriptionValidationOutcome {
                valid: false,
                region: resolved.region,
                cloud_label: resolved.cloud.display_label().to_string(),
                sts_url: resolved.sts_issue_token_url,
                latency_ms: elapsed,
                message: msg,
            },
        }
    }

    fn make_cache_key(sts_url: &str, subscription_key: &str) -> String {
        let mut hasher = Sha256::new();
        hasher.update(sts_url.as_bytes());
        hasher.update(b"|");
        hasher.update(subscription_key.as_bytes());
        hex::encode(hasher.finalize())
    }
}

async fn issue_sts_token(
    client: &reqwest::Client,
    sts_url: &str,
    subscription_key: &str,
) -> Result<String, (u64, String)> {
    let started = Instant::now();
    let response = client
        .post(sts_url)
        .header("Ocp-Apim-Subscription-Key", subscription_key)
        .header("Content-Length", "0")
        .body("")
        .send()
        .await
        .map_err(|e| {
            let ms = started.elapsed().as_millis() as u64;
            let detail = if e.is_timeout() {
                "连接 STS 鉴权端点超时，请检查网络或区域地址。".to_string()
            } else {
                "无法连接到微软 Speech 鉴权端点，请检查网络与终结点地址。".to_string()
            };
            (ms, detail)
        })?;

    let elapsed = started.elapsed().as_millis() as u64;
    let status = response.status();
    if status.is_success() {
        let body = response.text().await.unwrap_or_default();
        if body.trim().is_empty() {
            return Err((elapsed, "鉴权服务返回了空令牌。".into()));
        }
        return Ok(body.trim().to_string());
    }

    let code = status.as_u16();
    if code == 401 || code == 403 {
        Err((
            elapsed,
            format!("鉴权失败 (HTTP {code})：请检查密钥与区域（21V 中国区 / Global 国际版）是否匹配。"),
        ))
    } else {
        Err((elapsed, format!("终结点验证失败 (HTTP {code})。")))
    }
}
