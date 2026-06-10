//! 应用配置：终结点集合、模型角色绑定、提示词、界面与运行参数，并负责持久化。
//!
//! 配置文件路径与 C# 版保持一致：`%APPDATA%/TrueFluentPro/config.json`。

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::audio::AudioSettings;
use crate::endpoint::{AiEndpoint, ModelReference};
use crate::error::{CoreError, Result};
use crate::speech_resource::SpeechResource;

/// 功能分区 → 模型引用绑定。
///
/// 对应 C# `AiConfig` 中的 *ModelRef 字段（Insight/Summary/Quick/Review/Conversation/Intent）。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModelBindings {
    /// 洞察分析
    pub insight: Option<ModelReference>,
    /// 总结
    pub summary: Option<ModelReference>,
    /// 快问
    pub quick: Option<ModelReference>,
    /// 复盘
    pub review: Option<ModelReference>,
    /// 对话
    pub conversation: Option<ModelReference>,
    /// 意图识别
    pub intent: Option<ModelReference>,
}

/// 角色枚举：用于运行时按角色取模型。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModelRole {
    Insight,
    Summary,
    Quick,
    Review,
    Conversation,
    Intent,
}

impl ModelBindings {
    pub fn get(&self, role: ModelRole) -> Option<&ModelReference> {
        let r = match role {
            ModelRole::Insight => &self.insight,
            ModelRole::Summary => &self.summary,
            ModelRole::Quick => &self.quick,
            ModelRole::Review => &self.review,
            ModelRole::Conversation => &self.conversation,
            ModelRole::Intent => &self.intent,
        };
        r.as_ref().filter(|m| !m.is_empty())
    }
}

/// 洞察预设按钮。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PresetButton {
    pub name: String,
    pub prompt: String,
}

/// 复盘表预设。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReviewSheet {
    pub name: String,
    pub file_tag: String,
    pub prompt: String,
}

/// 提示词模板。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PromptTemplates {
    pub insight_system: String,
    pub review_system: String,
    pub insight_user_template: String,
    pub review_user_template: String,
}

impl Default for PromptTemplates {
    fn default() -> Self {
        Self {
            insight_system: "你是一个专业的会议/翻译分析助手。用户会提供实时翻译的历史记录，请根据用户的问题对内容进行分析。请用 Markdown 格式输出分析结果。".into(),
            review_system: "你是一个会议复盘助手。你会收到完整字幕，以及一个由用户提示明确指定的当前复盘目标。请严格围绕当前目标输出 Markdown 结果，引用内容时标注时间戳 [HH:MM:SS]。".into(),
            insight_user_template: "以下是翻译历史记录：\n\n{history}\n\n---\n\n用户问题：{question}".into(),
            review_user_template: "以下是会议字幕内容:\n\n{subtitle}\n\n---\n\n{prompt}".into(),
        }
    }
}

/// 界面主题。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Theme {
    #[default]
    Light,
    Dark,
}

/// 界面语言。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Language {
    #[default]
    Zh,
    En,
}

/// 通用运行设置。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GeneralSettings {
    pub theme: Theme,
    pub language: Language,
    /// 任务并发上限
    pub max_concurrent_tasks: usize,
    /// 默认源语言（实时翻译）。`auto` 表示自动识别。
    pub default_source_lang: String,
    /// 默认目标语言（实时翻译）
    pub default_target_lang: String,
    /// 过滤句末语气词（呢/吧/啊 等），对应 C# FilterModalParticles
    #[serde(default = "default_true")]
    pub filter_modal_particles: bool,
    /// 历史记录展示上限
    #[serde(default = "default_max_history")]
    pub max_history_items: usize,
}

fn default_true() -> bool {
    true
}

fn default_max_history() -> usize {
    15
}

impl Default for GeneralSettings {
    fn default() -> Self {
        Self {
            theme: Theme::Light,
            language: Language::Zh,
            max_concurrent_tasks: 3,
            default_source_lang: "en-US".into(),
            default_target_lang: "zh-Hans".into(),
            filter_modal_particles: true,
            max_history_items: 15,
        }
    }
}

/// 应用全量配置。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppConfig {
    #[serde(default)]
    pub endpoints: Vec<AiEndpoint>,
    #[serde(default)]
    pub model_bindings: ModelBindings,
    #[serde(default)]
    pub preset_buttons: Vec<PresetButton>,
    #[serde(default)]
    pub review_sheets: Vec<ReviewSheet>,
    #[serde(default)]
    pub prompts: PromptTemplates,
    #[serde(default)]
    pub general: GeneralSettings,
    /// 语音资源（实时/批量语音凭据）
    #[serde(default)]
    pub speech_resources: Vec<SpeechResource>,
    /// 当前激活的语音资源 ID
    #[serde(default)]
    pub active_speech_resource_id: String,
    /// 音频设备与采集路由设置
    #[serde(default)]
    pub audio: AudioSettings,
}

impl AppConfig {
    /// 配置目录：`%APPDATA%/TrueFluentPro`。
    pub fn config_dir() -> Result<PathBuf> {
        let base = dirs::config_dir()
            .ok_or_else(|| CoreError::Config("无法定位用户配置目录".into()))?;
        Ok(base.join("TrueFluentPro"))
    }

    /// 配置文件路径：`%APPDATA%/TrueFluentPro/config.json`。
    pub fn config_path() -> Result<PathBuf> {
        Ok(Self::config_dir()?.join("config.json"))
    }

    /// 从默认路径加载；文件不存在时返回默认配置（带内置预设）。
    pub fn load() -> Result<Self> {
        let path = Self::config_path()?;
        if path.exists() {
            Self::load_from(&path)
        } else {
            Ok(Self::with_defaults())
        }
    }

    /// 从指定路径加载。
    pub fn load_from(path: &Path) -> Result<Self> {
        let text = std::fs::read_to_string(path)?;
        let cfg: AppConfig = serde_json::from_str(&text)?;
        Ok(cfg)
    }

    /// 保存到默认路径（自动建目录）。
    pub fn save(&self) -> Result<()> {
        let path = Self::config_path()?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        self.save_to(&path)
    }

    /// 保存到指定路径。
    pub fn save_to(&self, path: &Path) -> Result<()> {
        let text = serde_json::to_string_pretty(self)?;
        std::fs::write(path, text)?;
        Ok(())
    }

    /// 启用的终结点。
    pub fn enabled_endpoints(&self) -> impl Iterator<Item = &AiEndpoint> {
        self.endpoints.iter().filter(|e| e.is_enabled)
    }

    /// 按 ID 查终结点。
    pub fn endpoint(&self, id: &str) -> Option<&AiEndpoint> {
        self.endpoints.iter().find(|e| e.id == id)
    }

    /// 按 ID 查语音资源。
    pub fn speech_resource(&self, id: &str) -> Option<&SpeechResource> {
        self.speech_resources.iter().find(|r| r.id == id)
    }

    /// 当前激活的语音资源：优先匹配 `active_speech_resource_id`，
    /// 否则回退到第一个启用且凭据有效的资源。
    pub fn active_speech_resource(&self) -> Option<&SpeechResource> {
        if !self.active_speech_resource_id.is_empty() {
            if let Some(r) = self.speech_resource(&self.active_speech_resource_id) {
                return Some(r);
            }
        }
        self.speech_resources
            .iter()
            .find(|r| r.is_enabled && r.is_valid())
            .or_else(|| self.speech_resources.first())
    }

    /// 默认配置（带内置预设按钮与复盘表）。
    pub fn with_defaults() -> Self {
        Self {
            preset_buttons: vec![
                PresetButton { name: "会议摘要".into(), prompt: "请对以上翻译记录进行会议摘要。总结会议的主要议题、关键讨论内容和结论。".into() },
                PresetButton { name: "知识点提取".into(), prompt: "请从以上翻译记录中提取核心知识点和专业术语，按主题分类整理。".into() },
                PresetButton { name: "行动项提取".into(), prompt: "请从以上翻译记录中提取所有行动项(Action Items)，包括待办事项、分工安排、承诺和截止时间。".into() },
                PresetButton { name: "情绪分析".into(), prompt: "请对以上翻译记录进行情绪分析，判断对话中各参与者的整体情绪倾向，标注情绪变化的关键节点。".into() },
            ],
            review_sheets: vec![
                ReviewSheet { name: "总结复盘".into(), file_tag: "summary".into(), prompt: "请基于字幕内容生成结构化会议总结，包含关键结论、行动项与风险点，标注时间戳 [HH:MM:SS]。".into() },
                ReviewSheet { name: "情绪复盘".into(), file_tag: "emotion".into(), prompt: "请分析对话情绪走向，指出情绪变化的关键时间点与可能原因，标注时间戳 [HH:MM:SS]。".into() },
                ReviewSheet { name: "客户顾虑".into(), file_tag: "customer".into(), prompt: "请识别客户提出的疑虑、问题与期望后续动作，按主题整理，标注时间戳 [HH:MM:SS]。".into() },
                ReviewSheet { name: "知识点复盘".into(), file_tag: "knowledge".into(), prompt: "请提取关键知识点与术语，并给出简要解释或背景说明，标注时间戳 [HH:MM:SS]。".into() },
            ],
            ..Default::default()
        }
    }
}
