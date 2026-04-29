use serde::{Deserialize, Serialize};

// ── Processing state enums ──

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ProcessingDisplayState {
    #[default]
    None,
    Pending,
    Running,
    Partial,
    Completed,
    Failed,
    Removed,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum StageContentState {
    #[default]
    Empty,
    Processing,
    Ready,
}

// ── Audio enums ──

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum AudioSourceMode {
    #[default]
    DefaultMic,
    CaptureDevice,
    Loopback,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum RecordingMode {
    #[default]
    LoopbackOnly,
    LoopbackWithMic,
    MicOnly,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum AudioPreProcessorPluginType {
    #[default]
    None,
    WebRtcApm,
}

// ── Text editor ──

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TextEditorType {
    Simple,
    Advanced,
}

// ── Config-level enums ──

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum BatchLogLevel {
    #[default]
    Off,
    FailuresOnly,
    SuccessAndFailure,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum TranscriptionApiMode {
    #[default]
    Batch,
    Fast,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ServiceMode {
    #[default]
    SelfHosted,
    Cloud,
}
