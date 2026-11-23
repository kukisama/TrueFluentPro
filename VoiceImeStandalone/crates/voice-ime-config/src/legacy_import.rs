//! 从主程序 `TrueFluentPro` 的 `config.json` 单向只读导入传统微软 Speech 终结点。
//!
//! 只读取并提取有效的国际版 Global 与世纪互联 21Vianet 微软 Speech 密钥资源，
//! 绝不修改主程序任何文件，去重合并进独立版配置。

use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::endpoint::{parse_speech_endpoint, SpeechSubscription};
use crate::model::StandaloneConfig;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LegacyImportSummary {
    pub imported_count: usize,
    pub skipped_duplicates: usize,
    pub source_path: String,
}

/// 探测本机 `TrueFluentPro` 默认配置文件位置。
pub fn default_truefluentpro_config_path() -> Option<PathBuf> {
    let local = dirs::data_local_dir()?;
    let candidate = local.join("TrueFluentPro").join("config.json");
    if candidate.exists() {
        Some(candidate)
    } else {
        None
    }
}

/// 从主程序 JSON 字节流单向提取传统微软 Speech 订阅并合并入 `target`。
pub fn import_from_truefluent_config_json(
    raw_json: &str,
    source_label: impl Into<String>,
    target: &mut StandaloneConfig,
) -> Result<LegacyImportSummary, String> {
    let root: Value = serde_json::from_str(raw_json)
        .map_err(|_| "主程序配置文件不是有效的 JSON 格式。".to_string())?;

    let mut existing_fingerprints: HashSet<String> = target
        .subscriptions
        .iter()
        .filter_map(|s| {
            let ep = s.resolve_endpoint().ok()?;
            let key = s.plain_subscription_key().ok()?;
            Some(format!("{}|{}", ep.sts_issue_token_url, key))
        })
        .collect();

    let mut imported_count = 0usize;
    let mut skipped_duplicates = 0usize;

    let mut try_add = |name: &str, endpoint_or_region: &str, key: &str| {
        let trimmed_key = key.trim();
        if trimmed_key.is_empty() {
            return;
        }
        let Ok(resolved) = parse_speech_endpoint(endpoint_or_region) else {
            return;
        };
        let fp = format!("{}|{}", resolved.sts_issue_token_url, trimmed_key);
        if existing_fingerprints.contains(&fp) {
            skipped_duplicates += 1;
            return;
        }
        let final_name = if name.trim().is_empty() {
            format!("导入 · Azure Speech ({})", resolved.region)
        } else {
            name.trim().to_string()
        };
        if let Ok(sub) = SpeechSubscription::create(final_name, &resolved.cognitive_base_url, trimmed_key) {
            if target.active_subscription_id.is_empty() {
                target.active_subscription_id = sub.id.clone();
            }
            existing_fingerprints.insert(fp);
            target.subscriptions.push(sub);
            imported_count += 1;
        }
    };

    // 1. 解析旧版/标准 Subscriptions 数组
    if let Some(subs) = find_case_insensitive(&root, "Subscriptions").and_then(|v| v.as_array()) {
        for item in subs {
            let name = str_field(item, "Name");
            let key = str_field(item, "SubscriptionKey");
            let ep = str_field(item, "Endpoint");
            let region = str_field(item, "ServiceRegion");
            try_add(&name, if !ep.is_empty() { &ep } else { &region }, &key);
        }
    }

    // 2. 解析 SpeechResources 数组（MicrosoftSpeech）
    if let Some(resources) = find_case_insensitive(&root, "SpeechResources").and_then(|v| v.as_array()) {
        for item in resources {
            let connector = str_field(item, "ConnectorType");
            if !connector.is_empty() && !connector.eq_ignore_ascii_case("MicrosoftSpeech") {
                continue;
            }
            let name = str_field(item, "Name");
            let key = str_field(item, "SubscriptionKey");
            let ep = str_field(item, "Endpoint");
            let region = str_field(item, "ServiceRegion");
            try_add(&name, if !ep.is_empty() { &ep } else { &region }, &key);
        }
    }

    // 3. 解析新版 Endpoints 数组中的 AzureSpeech 终结点
    if let Some(endpoints) = find_case_insensitive(&root, "Endpoints").and_then(|v| v.as_array()) {
        for item in endpoints {
            let ep_type = str_field(item, "EndpointType");
            if !ep_type.eq_ignore_ascii_case("AzureSpeech") {
                continue;
            }
            let name = str_field(item, "Name");
            let key = {
                let s = str_field(item, "SpeechSubscriptionKey");
                if !s.is_empty() { s } else { str_field(item, "ApiKey") }
            };
            let ep = {
                let s = str_field(item, "SpeechEndpoint");
                if !s.is_empty() { s } else { str_field(item, "ApiEndpoint") }
            };
            let region = str_field(item, "SpeechRegion");
            try_add(&name, if !ep.is_empty() { &ep } else { &region }, &key);
        }
    }

    Ok(LegacyImportSummary {
        imported_count,
        skipped_duplicates,
        source_path: source_label.into(),
    })
}

pub fn import_from_file(path: &Path, target: &mut StandaloneConfig) -> Result<LegacyImportSummary, String> {
    let raw = fs::read_to_string(path)
        .map_err(|_| "无法读取指定的主程序配置文件。".to_string())?;
    import_from_truefluent_config_json(&raw, path.display().to_string(), target)
}

fn find_case_insensitive<'a>(obj: &'a Value, target_key: &str) -> Option<&'a Value> {
    let map = obj.as_object()?;
    map.iter()
        .find(|(k, _)| k.eq_ignore_ascii_case(target_key))
        .map(|(_, v)| v)
}

fn str_field(obj: &Value, key: &str) -> String {
    find_case_insensitive(obj, key)
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .trim()
        .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn imports_both_21v_and_global_and_deduplicates() {
        let sample = r#"{
            "Subscriptions": [
                {
                    "Name": "东南亚国际版",
                    "SubscriptionKey": "key_global_123456789012345678901234",
                    "Endpoint": "https://southeastasia.api.cognitive.microsoft.com"
                },
                {
                    "Name": "华北三 21V",
                    "SubscriptionKey": "key_china_1234567890123456789012345",
                    "Endpoint": "https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issuetoken"
                }
            ],
            "Endpoints": [
                {
                    "EndpointType": "AzureSpeech",
                    "Name": "重复的东南亚",
                    "SpeechSubscriptionKey": "key_global_123456789012345678901234",
                    "SpeechRegion": "southeastasia"
                }
            ]
        }"#;
        let mut cfg = StandaloneConfig::default();
        let summary = import_from_truefluent_config_json(sample, "memory", &mut cfg).unwrap();
        assert_eq!(summary.imported_count, 2);
        assert_eq!(summary.skipped_duplicates, 1);
        assert_eq!(cfg.subscriptions.len(), 2);
    }
}
