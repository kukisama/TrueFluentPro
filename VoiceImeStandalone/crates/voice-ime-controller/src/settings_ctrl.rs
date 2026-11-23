//! 设置页控制器：微软 Speech 终结点（21V 与 Global）管理、实时区域提示、STS 一键验证、麦克风与断句保存。

use serde::{Deserialize, Serialize};
use voice_ime_config::legacy_import::{default_truefluentpro_config_path, import_from_file};
use voice_ime_config::{
    parse_speech_endpoint, ConfigStore, LegacyImportSummary, SpeechSubscription,
};
use voice_ime_speech::{StsTokenCache, SubscriptionValidationOutcome};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SubscriptionDto {
    pub id: String,
    pub name: String,
    pub endpoint: String,
    pub region: String,
    pub cloud_label: String,
    pub masked_key: String,
    pub is_enabled: bool,
    pub display_title: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EndpointPreviewDto {
    pub valid: bool,
    pub region: String,
    pub cloud_label: String,
    pub sts_url: String,
    pub hint_text: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsPageSnapshot {
    pub active_subscription_id: String,
    pub subscriptions: Vec<SubscriptionDto>,
    pub microphone_device_id: String,
    pub enable_prewarm: bool,
    pub end_silence_timeout_seconds: f64,
    pub filter_modal_particles: bool,
    pub can_import_legacy: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GeneralSettingsUpdate {
    pub active_subscription_id: String,
    pub microphone_device_id: String,
    pub enable_prewarm: bool,
    pub end_silence_timeout_seconds: f64,
    pub filter_modal_particles: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpsertSubscriptionInput {
    pub id: Option<String>,
    pub name: String,
    pub endpoint: String,
    /// 新增时必填；编辑已有订阅时留空则保留原已保存密钥。
    pub subscription_key: String,
    pub is_enabled: bool,
}

/// 实时解析用户正在输入的终结点 URL，返回区域与云环境提示（对齐 C# `UpdateEndpointRegionHint`）。
pub fn preview_speech_endpoint(raw: &str) -> EndpointPreviewDto {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return EndpointPreviewDto {
            valid: false,
            region: String::new(),
            cloud_label: String::new(),
            sts_url: String::new(),
            hint_text: "支持国际版 https://<region>.api.cognitive.microsoft.com 与世纪互联 https://<region>.api.cognitive.azure.cn/sts/v1.0/issuetoken".into(),
        };
    }
    match parse_speech_endpoint(trimmed) {
        Ok(r) => EndpointPreviewDto {
            valid: true,
            region: r.region.clone(),
            cloud_label: r.cloud.display_label().into(),
            sts_url: r.sts_issue_token_url.clone(),
            hint_text: format!("✓ 已识别区域: {} ({})", r.region, r.cloud.display_label()),
        },
        Err(e) => EndpointPreviewDto {
            valid: false,
            region: String::new(),
            cloud_label: String::new(),
            sts_url: String::new(),
            hint_text: format!("✗ {e}"),
        },
    }
}

pub fn load_settings_snapshot(store: &ConfigStore) -> Result<SettingsPageSnapshot, String> {
    let cfg = store.load_readonly().map_err(|e| e.to_string())?;
    let subscriptions = cfg.subscriptions.iter().map(to_subscription_dto).collect();
    Ok(SettingsPageSnapshot {
        active_subscription_id: cfg.active_subscription_id.clone(),
        subscriptions,
        microphone_device_id: cfg.microphone_device_id,
        enable_prewarm: cfg.enable_prewarm,
        end_silence_timeout_seconds: cfg.end_silence_timeout_seconds,
        filter_modal_particles: cfg.filter_modal_particles,
        can_import_legacy: default_truefluentpro_config_path().is_some(),
    })
}

pub fn save_general_settings(
    store: &ConfigStore,
    update: GeneralSettingsUpdate,
) -> Result<SettingsPageSnapshot, String> {
    if !update.end_silence_timeout_seconds.is_finite()
        || !(0.2..=3.0).contains(&update.end_silence_timeout_seconds)
    {
        return Err("句尾静音时长必须在 0.2 到 3.0 秒之间。".into());
    }
    store.update(|cfg| {
        let id = update.active_subscription_id.trim();
        if !id.is_empty() && !cfg.subscriptions.iter().any(|sub| sub.id == id && sub.is_enabled) {
            return Err("该语音终结点已删除或停用，请刷新列表。".into());
        }
        cfg.active_subscription_id = id.to_string();
        cfg.microphone_device_id = update.microphone_device_id.trim().to_string();
        cfg.enable_prewarm = update.enable_prewarm;
        cfg.end_silence_timeout_seconds = (update.end_silence_timeout_seconds * 10.0).round() / 10.0;
        cfg.filter_modal_particles = update.filter_modal_particles;
        Ok(())
    }).map_err(|e| e.to_string())?;
    load_settings_snapshot(store)
}

pub fn upsert_subscription(
    store: &ConfigStore,
    input: UpsertSubscriptionInput,
) -> Result<SettingsPageSnapshot, String> {
    let name = input.name.trim();
    if name.is_empty() {
        return Err("请填写终结点名称（例如：东南亚国际版 / 华北三 21V）。".into());
    }
    let resolved = parse_speech_endpoint(&input.endpoint).map_err(|e| e.to_string())?;
    store.update(|cfg| {
    if let Some(id) = input.id.as_deref().filter(|s| !s.trim().is_empty()) {
        let existing = cfg
            .subscriptions
            .iter_mut()
            .find(|s| s.id == id)
            .ok_or_else(|| "未找到要更新的订阅记录。".to_string())?;
        existing.name = name.to_string();
        existing.endpoint = input.endpoint.trim().to_string();
        existing.service_region = resolved.region;
        existing.is_enabled = input.is_enabled;
        if !input.subscription_key.trim().is_empty() {
            let fresh = SpeechSubscription::create(name, &input.endpoint, &input.subscription_key)
                .map_err(|e| e.to_string())?;
            existing.encrypted_key = fresh.encrypted_key;
        }
    } else {
        let mut created = SpeechSubscription::create(name, &input.endpoint, &input.subscription_key)
            .map_err(|e| e.to_string())?;
        created.is_enabled = input.is_enabled;
        if cfg.active_subscription_id.trim().is_empty() {
            cfg.active_subscription_id = created.id.clone();
        }
        cfg.subscriptions.push(created);
    }

    Ok(())
    }).map_err(|e| e.to_string())?;
    load_settings_snapshot(store)
}

pub fn delete_subscription(
    store: &ConfigStore,
    subscription_id: &str,
) -> Result<SettingsPageSnapshot, String> {
    store.update(|cfg| {
        cfg.subscriptions.retain(|s| s.id != subscription_id);
        if cfg.active_subscription_id == subscription_id {
            cfg.active_subscription_id = cfg.subscriptions.iter().find(|s| s.is_enabled)
                .map(|s| s.id.clone()).unwrap_or_default();
        }
        Ok(())
    }).map_err(|e| e.to_string())?;
    load_settings_snapshot(store)
}

pub async fn test_subscription(
    store: &ConfigStore,
    sts: &StsTokenCache,
    saved_id: Option<String>,
    endpoint: String,
    plain_key: String,
) -> Result<SubscriptionValidationOutcome, String> {
    let saved = if let Some(id) = saved_id.filter(|s| !s.trim().is_empty()) {
        let cfg = store.load_readonly().map_err(|e| e.to_string())?;
        Some(cfg
            .subscriptions
            .into_iter()
            .find(|s| s.id == id)
            .ok_or_else(|| "未找到对应的已保存订阅。".to_string())?)
    } else {
        None
    };
    let ep_to_use = if !endpoint.trim().is_empty() {
        endpoint
    } else if let Some(sub) = &saved {
        sub.endpoint.clone()
    } else {
        return Err("请先填写微软 Speech 终结点地址。".into());
    };
    let key_to_use = if !plain_key.trim().is_empty() {
        plain_key.trim().to_string()
    } else if let Some(sub) = &saved {
        let requested = parse_speech_endpoint(&ep_to_use).map_err(|e| e.to_string())?;
        let original = sub.resolve_endpoint().map_err(|e| e.to_string())?;
        if requested.sts_issue_token_url != original.sts_issue_token_url {
            return Err("验证新的区域或终结点时，请填写该终结点的订阅密钥。".into());
        }
        sub.plain_subscription_key().map_err(|e| e.to_string())?
    } else {
        return Err("请填写订阅密钥，或在下方直接验证已保存的终结点。".into());
    };

    Ok(sts.validate_endpoint_and_key(&ep_to_use, &key_to_use).await)
}

pub fn import_legacy_subscriptions(
    store: &ConfigStore,
) -> Result<(LegacyImportSummary, SettingsPageSnapshot), String> {
    let path = default_truefluentpro_config_path()
        .ok_or_else(|| "未检测到本机译见 Pro 主程序配置文件。".to_string())?;
    let mut summary = None;
    store.update(|cfg| {
        summary = Some(import_from_file(&path, cfg)?);
        Ok(())
    }).map_err(|e| e.to_string())?;
    let snap = load_settings_snapshot(store)?;
    Ok((summary.expect("successful configuration update returns an import summary"), snap))
}

fn to_subscription_dto(sub: &SpeechSubscription) -> SubscriptionDto {
    let (region, cloud_label) = match sub.resolve_endpoint() {
        Ok(r) => (r.region, r.cloud.display_label().to_string()),
        Err(_) => (sub.service_region.clone(), "未识别".to_string()),
    };
    SubscriptionDto {
        id: sub.id.clone(),
        name: sub.name.clone(),
        endpoint: sub.endpoint.clone(),
        region,
        cloud_label,
        masked_key: sub.masked_key_hint(),
        is_enabled: sub.is_enabled,
        display_title: sub.display_title(),
    }
}
