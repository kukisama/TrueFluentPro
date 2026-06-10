//! 领域模型：任务队列、音频生命周期、实时翻译记录。
//!
//! 移植自 C# 的 `AudioLifecycleStage` / `AudioTaskStatus` 等，
//! 用 Rust 枚举重构，附带前端友好的 serde 标签。

use serde::{Deserialize, Serialize};

/// 任务状态。对应任务监控的「待处理 / 进行中 / 已完成 / 失败」分桶。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskStatus {
    /// 已提交，等待调度
    #[default]
    Pending,
    /// 执行中
    Running,
    /// 已完成
    Completed,
    /// 执行失败
    Failed,
    /// 用户取消
    Cancelled,
}

/// 任务种类。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskKind {
    /// 文件转写
    Transcribe,
    /// AI 总结
    Summarize,
    /// 思维导图
    MindMap,
    /// 顿悟/洞察
    Insight,
    /// 深度研究
    Research,
    /// 播客台本
    PodcastScript,
    /// 文本翻译
    Translate,
    /// 播客音频（TTS）
    PodcastAudio,
}

/// 音频生命周期阶段。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AudioStage {
    Transcribed,
    Summarized,
    MindMap,
    Insight,
    PodcastScript,
    PodcastAudio,
    Translated,
    Research,
}

/// 任务记录（任务监控面板的一行）。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskRecord {
    pub id: String,
    pub kind: TaskKind,
    pub title: String,
    pub status: TaskStatus,
    /// 0.0 ~ 1.0
    #[serde(default)]
    pub progress: f32,
    #[serde(default)]
    pub created_at: i64,
    #[serde(default)]
    pub updated_at: i64,
    #[serde(default)]
    pub error_message: Option<String>,
    /// 消耗 token（可选统计）
    #[serde(default)]
    pub tokens_used: Option<u64>,
}

/// 实时翻译的一条记录（识别原文 + 译文）。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TranslationEntry {
    pub id: String,
    /// 原文（识别结果）
    pub source_text: String,
    /// 译文
    #[serde(default)]
    pub translated_text: String,
    /// 是否为中间（未定稿）结果
    #[serde(default)]
    pub is_partial: bool,
    /// 时间戳（毫秒）
    #[serde(default)]
    pub timestamp_ms: i64,
    /// 说话人标识（如有）
    #[serde(default)]
    pub speaker: Option<String>,
}
