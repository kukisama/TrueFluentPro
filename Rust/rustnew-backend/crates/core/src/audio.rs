//! 音频设备与采集路由模型。
//!
//! 移植自 C# `Models/AudioSourceMode.cs`、`RecordingMode.cs`、`AudioDeviceType.cs`、
//! `AudioDeviceInfo.cs`，以及 `AzureSpeechConfig` 中与音频设备相关的字段。
//! 去掉 MVVM 的 ObservableObject 样板，改用纯数据 + serde。
//!
//! 跨平台说明：
//! - 设备枚举与「按设备采集」在不同平台实现不同，模型本身保持平台无关；
//! - 系统回环（Loopback）仅部分平台支持，是否可用由桌面层在运行时探测。

use serde::{Deserialize, Serialize};

/// 识别音源模式（对齐 C# `AudioSourceMode`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AudioSourceMode {
    /// 默认麦克风（全平台）。
    #[default]
    DefaultMic,
    /// 指定的输入设备（麦克风）。
    CaptureDevice,
    /// 系统声卡回环（捕获扬声器输出，仅部分平台支持）。
    Loopback,
}

/// 录制来源模式（对齐 C# `RecordingMode`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum RecordingMode {
    /// 仅系统回环。
    #[default]
    LoopbackOnly,
    /// 系统回环 + 麦克风混音。
    LoopbackWithMic,
    /// 仅麦克风。
    MicOnly,
}

/// 音频设备方向（对齐 C# `AudioDeviceType`）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum AudioDeviceType {
    /// 采集设备（麦克风）。
    #[default]
    Capture,
    /// 渲染设备（扬声器，用于回环参考）。
    Render,
}

/// 一个音频设备的描述（对齐 C# `AudioDeviceInfo`）。
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioDeviceInfo {
    /// 平台设备 ID（Windows 音频终结点 ID / Linux ALSA 名 / macOS UID）。
    pub device_id: String,
    /// 友好显示名。
    pub display_name: String,
    /// 设备方向。
    #[serde(default)]
    pub device_type: AudioDeviceType,
}

/// 音频设备与路由设置（持久化在 `AppConfig` 中）。
///
/// 对齐 C# `AzureSpeechConfig` 中的：
/// `AudioSourceMode` / `RecordingMode` / `SelectedAudioDeviceId` /
/// `SelectedOutputDeviceId` / `UseInputForRecognition` / `UseOutputForRecognition`。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioSettings {
    /// 识别音源模式。
    #[serde(default)]
    pub source_mode: AudioSourceMode,
    /// 录制来源模式。
    #[serde(default)]
    pub recording_mode: RecordingMode,
    /// 选中的输入设备（麦克风）ID。空串表示默认设备。
    #[serde(default)]
    pub selected_input_device_id: String,
    /// 选中的输出设备（扬声器，用于回环）ID。空串表示默认设备。
    #[serde(default)]
    pub selected_output_device_id: String,
    /// 是否把麦克风用于识别（CaptureDevice 模式下的混音开关）。
    #[serde(default = "default_true")]
    pub use_input_for_recognition: bool,
    /// 是否把系统回环用于识别（CaptureDevice 模式下的混音开关）。
    #[serde(default)]
    pub use_output_for_recognition: bool,

    /// 两路同时识别时的混音策略。
    #[serde(default)]
    pub mix_mode: MixMode,
    /// 麦克风路增益（混音前施加，1.0 = 原样）。
    #[serde(default = "default_gain")]
    pub mic_gain: f32,
    /// 回环路增益（混音前施加，1.0 = 原样）。
    #[serde(default = "default_gain")]
    pub loopback_gain: f32,
    /// 推送给云端识别的音频格式（默认 16k/16bit/mono 裸 PCM）。
    #[serde(default)]
    pub recognition_format: AudioFormat,
    /// 本地高保真录音设置（独立于识别链路）。
    #[serde(default)]
    pub recorder: RecorderSettings,
}

fn default_true() -> bool {
    true
}

fn default_gain() -> f32 {
    1.0
}

impl Default for AudioSettings {
    fn default() -> Self {
        Self {
            source_mode: AudioSourceMode::DefaultMic,
            recording_mode: RecordingMode::LoopbackOnly,
            selected_input_device_id: String::new(),
            selected_output_device_id: String::new(),
            use_input_for_recognition: true,
            use_output_for_recognition: false,
            mix_mode: MixMode::default(),
            mic_gain: 1.0,
            loopback_gain: 1.0,
            recognition_format: AudioFormat::recognition_default(),
            recorder: RecorderSettings::default(),
        }
    }
}

/// 双路（mic + loopback）混音策略。
///
/// 当前仅 `DownmixMono` 落地；其余取值为未来扩展（如双声道参考流 / VAD 选源）预留，
/// 以便跨平台或高级管线接入时替换实现而不改配置结构。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum MixMode {
    /// 两路相加后下混为单声道（防削顶）。对齐 C# `Pcm16AudioMixer.MixMono`。
    #[default]
    DownmixMono,
    /// 仅取麦克风路（忽略回环）。
    MicOnly,
    /// 仅取回环路（忽略麦克风）。
    LoopbackOnly,
}

/// PCM 音频格式描述（采样率 / 位深 / 声道数）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioFormat {
    /// 采样率（Hz）。
    pub sample_rate: u32,
    /// 位深（bit），当前管线按 16 处理。
    pub bits_per_sample: u16,
    /// 声道数（1 = 单声道，2 = 立体声）。
    pub channels: u16,
}

impl AudioFormat {
    /// 云端识别默认格式：16kHz / 16bit / 单声道。
    pub const fn recognition_default() -> Self {
        Self {
            sample_rate: 16_000,
            bits_per_sample: 16,
            channels: 1,
        }
    }

    /// 高保真录音默认格式：48kHz / 16bit / 立体声。
    pub const fn recording_default() -> Self {
        Self {
            sample_rate: 48_000,
            bits_per_sample: 16,
            channels: 2,
        }
    }

    /// 每秒字节数（用于缓冲/时长估算）。
    pub const fn bytes_per_second(&self) -> u32 {
        self.sample_rate * (self.bits_per_sample as u32 / 8) * self.channels as u32
    }
}

impl Default for AudioFormat {
    fn default() -> Self {
        Self::recognition_default()
    }
}

/// 录音输出容器格式。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum RecordingContainer {
    /// 无损 WAV（PCM）。体积大、无需额外编码器。
    Wav,
    /// MP3（有损压缩）。体积小，适合存档与上传。
    #[default]
    Mp3,
}

impl RecordingContainer {
    /// 对应文件扩展名（不含点）。
    pub const fn extension(&self) -> &'static str {
        match self {
            RecordingContainer::Wav => "wav",
            RecordingContainer::Mp3 => "mp3",
        }
    }
}

/// 本地录音机设置（高保真存档，独立于识别链路）。
///
/// 对齐 C# `AzureSpeechConfig` 的 `EnableRecording` / `RecordingMp3BitrateKbps`，
/// 并额外暴露采样率 / 位深 / 声道，便于单独测试录音质量。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecorderSettings {
    /// 是否启用录音。
    #[serde(default = "default_true")]
    pub enabled: bool,
    /// 输出容器格式。
    #[serde(default)]
    pub container: RecordingContainer,
    /// 录音采样率 / 位深 / 声道。
    #[serde(default = "AudioFormat::recording_default")]
    pub format: AudioFormat,
    /// MP3 码率（kbps），仅 `Mp3` 容器时生效。对齐 C# 默认 256。
    #[serde(default = "default_mp3_bitrate")]
    pub mp3_bitrate_kbps: u32,
}

fn default_mp3_bitrate() -> u32 {
    256
}

impl Default for RecorderSettings {
    fn default() -> Self {
        Self {
            enabled: true,
            container: RecordingContainer::Mp3,
            format: AudioFormat::recording_default(),
            mp3_bitrate_kbps: 256,
        }
    }
}

impl AudioSettings {
    /// 识别路由：返回 `(enable_loopback, enable_mic)`。
    ///
    /// 对齐 C# `SpeechTranslationService.GetRecognitionRouting()`：
    /// - `Loopback` 模式：严格只走回环；
    /// - `DefaultMic` 模式：只走麦克风；
    /// - `CaptureDevice` 模式：按两个 `use_*_for_recognition` 开关。
    pub fn recognition_routing(&self) -> (bool, bool) {
        match self.source_mode {
            AudioSourceMode::Loopback => (true, false),
            AudioSourceMode::DefaultMic => (false, true),
            AudioSourceMode::CaptureDevice => {
                (self.use_output_for_recognition, self.use_input_for_recognition)
            }
        }
    }
}
