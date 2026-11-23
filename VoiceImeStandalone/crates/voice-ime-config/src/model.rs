//! 独立版语音输入法配置模型与运行时会话快照。

use serde::{Deserialize, Serialize};

use crate::endpoint::{ResolvedSpeechEndpoint, SpeechEndpointError, SpeechSubscription};

/// 语言下拉选项（与现有 `VoiceImeLanguageViewModel` 语言表完全对齐）。
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LanguageOption {
    pub code: String,
    pub name: String,
}

/// 内置源语言列表（含 `auto` 连续语种自动识别）。
pub fn builtin_source_languages() -> Vec<LanguageOption> {
    vec![
        LanguageOption { code: "auto".into(), name: "自动检测（中/英/日/韩/法/德/西）".into() },
        LanguageOption { code: "zh-CN".into(), name: "中文（简体，普通话）".into() },
        LanguageOption { code: "zh-TW".into(), name: "中文（繁体，台湾）".into() },
        LanguageOption { code: "zh-HK".into(), name: "中文（粤语，香港）".into() },
        LanguageOption { code: "en-US".into(), name: "英语（美国）".into() },
        LanguageOption { code: "en-GB".into(), name: "英语（英国）".into() },
        LanguageOption { code: "ja-JP".into(), name: "日语".into() },
        LanguageOption { code: "ko-KR".into(), name: "韩语".into() },
        LanguageOption { code: "fr-FR".into(), name: "法语".into() },
        LanguageOption { code: "de-DE".into(), name: "德语".into() },
        LanguageOption { code: "es-ES".into(), name: "西班牙语".into() },
        LanguageOption { code: "ru-RU".into(), name: "俄语".into() },
    ]
}

/// 内置翻译目标语言列表（不含 `auto`，对齐微软 Speech Translation 目标语种代码）。
pub fn builtin_target_languages() -> Vec<LanguageOption> {
    builtin_source_languages()
        .into_iter()
        .filter(|opt| opt.code != "auto")
        .collect()
}

/// 语言与翻译页签专用的三字段草稿对象。
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LanguageDraft {
    pub recognition_language: String,
    pub enable_translation: bool,
    pub target_language: String,
}

/// 独立语音输入法主配置（持久化为 `voice-ime-config.json`）。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StandaloneConfig {
    #[serde(default = "default_schema_version")]
    pub schema_version: u32,
    /// 切换到输入法时后台零费用预热 STS 令牌与连接（默认开启）
    #[serde(default = "default_true")]
    pub enable_prewarm: bool,
    /// 当前选中的微软 Speech 订阅 ID（为空时默认选第一条已启用订阅）
    #[serde(default)]
    pub active_subscription_id: String,
    /// 微软 Speech 订阅列表（支持中国区 21Vianet 与国际版 Global）
    #[serde(default)]
    pub subscriptions: Vec<SpeechSubscription>,
    /// 麦克风设备名称（空字符串代表系统默认麦克风）
    #[serde(default)]
    pub microphone_device_id: String,
    /// 识别语言（默认 `auto`）
    #[serde(default = "default_auto_lang")]
    pub recognition_language: String,
    /// 是否开启“翻译后输入”（开启后逐句翻译，只向目标输入框插入译文）
    #[serde(default)]
    pub enable_translation: bool,
    /// 翻译目标语言（默认 `en-US`）
    #[serde(default = "default_target_lang")]
    pub target_language: String,
    /// 句尾静音断句时长（0.2 ~ 3.0 秒，默认 0.8 秒）
    #[serde(default = "default_end_silence")]
    pub end_silence_timeout_seconds: f64,
    /// 是否过滤句末语气词（啊/呢/吧/呃等，默认关闭以忠实保留原话，可在断句页开启）
    #[serde(default)]
    pub filter_modal_particles: bool,
}

fn default_schema_version() -> u32 {
    1
}
fn default_true() -> bool {
    true
}
fn default_auto_lang() -> String {
    "auto".to_string()
}
fn default_target_lang() -> String {
    "en-US".to_string()
}
fn default_end_silence() -> f64 {
    0.8
}

impl Default for StandaloneConfig {
    fn default() -> Self {
        Self {
            schema_version: 1,
            enable_prewarm: true,
            active_subscription_id: String::new(),
            subscriptions: Vec::new(),
            microphone_device_id: String::new(),
            recognition_language: default_auto_lang(),
            enable_translation: false,
            target_language: default_target_lang(),
            end_silence_timeout_seconds: default_end_silence(),
            filter_modal_particles: false,
        }
    }
}

/// 一轮录音冻结的不可变语音会话参数快照。
#[derive(Debug, Clone, PartialEq)]
pub struct RuntimeSpeechSessionConfig {
    pub subscription_id: String,
    pub subscription_name: String,
    pub subscription_key: String,
    pub endpoint: ResolvedSpeechEndpoint,
    pub microphone_device_id: String,
    pub recognition_language: String,
    pub enable_translation: bool,
    pub target_language: String,
    pub end_silence_ms: u32,
    pub enable_prewarm: bool,
    pub filter_modal_particles: bool,
}

impl StandaloneConfig {
    /// 查找当前生效的微软 Speech 订阅。
    pub fn resolve_active_subscription(&self) -> Option<&SpeechSubscription> {
        if !self.active_subscription_id.trim().is_empty() {
            return self
                .subscriptions
                .iter()
                .find(|s| s.id == self.active_subscription_id && s.is_enabled);
        }
        self.subscriptions.iter().find(|s| s.is_enabled)
    }

    /// 校验配置并冻结为单轮录音不可变快照（严格对齐 C# `CreateRuntimeConfiguration` 边界）。
    pub fn freeze_runtime_session(&self) -> Result<RuntimeSpeechSessionConfig, String> {
        if !self.end_silence_timeout_seconds.is_finite()
            || !(0.2..=3.0).contains(&self.end_silence_timeout_seconds)
        {
            return Err("输入法分句停顿应在 0.2 到 3.0 秒之间。".to_string());
        }

        let source_lang = if self.recognition_language.trim().is_empty() {
            "auto".to_string()
        } else {
            self.recognition_language.trim().to_string()
        };
        let target_lang = if self.target_language.trim().is_empty() {
            "en-US".to_string()
        } else {
            self.target_language.trim().to_string()
        };
        if self.enable_translation && target_lang.eq_ignore_ascii_case("auto") {
            return Err("请选择输入法的翻译目标语言，不能使用自动检测。".to_string());
        }

        let sub = self.resolve_active_subscription().ok_or_else(|| {
            if self.subscriptions.is_empty() {
                "尚未配置微软 Speech 语音终结点，请点击浮条齿轮图标在「设置 → 语音终结点」中添加。".to_string()
            } else {
                "当前选中的语音终结点不可用或已禁用，请在输入法设置中重新选择。".to_string()
            }
        })?;

        let endpoint = sub
            .resolve_endpoint()
            .map_err(|e: SpeechEndpointError| e.to_string())?;
        let subscription_key = sub
            .plain_subscription_key()
            .map_err(|_| "当前微软 Speech 终结点的密钥无效或无法解密，请重新填写保存。".to_string())?;

        let end_silence_ms = (self.end_silence_timeout_seconds * 1000.0).round().clamp(200.0, 3000.0) as u32;

        Ok(RuntimeSpeechSessionConfig {
            subscription_id: sub.id.clone(),
            subscription_name: sub.name.clone(),
            subscription_key,
            endpoint,
            microphone_device_id: self.microphone_device_id.trim().to_string(),
            recognition_language: source_lang,
            enable_translation: self.enable_translation,
            target_language: target_lang,
            end_silence_ms,
            enable_prewarm: self.enable_prewarm,
            filter_modal_particles: self.filter_modal_particles,
        })
    }

    /// 生成可用于比较预热会话是否仍然有效的脱敏指纹（含语言、翻译开关、终结点、静音时长）。
    pub fn prewarm_fingerprint(&self) -> Option<String> {
        let snap = self.freeze_runtime_session().ok()?;
        Some(format!(
            "{}|{}|{}|{}|{}|{}|{}",
            snap.subscription_id,
            snap.endpoint.sts_issue_token_url,
            snap.microphone_device_id,
            snap.recognition_language,
            snap.enable_translation,
            snap.target_language,
            snap.end_silence_ms
        ))
    }
}
