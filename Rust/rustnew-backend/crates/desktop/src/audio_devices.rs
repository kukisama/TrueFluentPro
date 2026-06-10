//! 音频设备枚举（跨平台，基于 cpal）。
//!
//! 据 Azure 官方文档 *how-to-select-audio-input-devices*：
//! `AudioConfig.FromMicrophoneInput()` 接受的是设备**友好名**（而非端点 ID）。
//! cpal 的 `Device::name()` 在各平台返回的正是可直接用于 Speech SDK 的友好名：
//! - Windows：如 `Microphone (Realtek(R) Audio)`；
//! - Linux：ALSA 名；
//! - macOS：设备名。
//!
//! 因此设备「ID」与「显示名」在这里取同一个值（cpal 设备名），既用于 UI 展示，
//! 也用于 `from_microphone_input(name)`。
//!
//! 系统回环（输出设备捕获）不在此模块范围：cpal 不直接支持 WASAPI loopback，
//! 留待后续按平台单独实现（Windows WASAPI loopback / Linux monitor / macOS 虚拟设备）。

use cpal::traits::{DeviceTrait, HostTrait};
use tfp_core::{AudioDeviceInfo, AudioDeviceType};

/// 枚举可用的输入设备（麦克风）。失败时返回空列表（不让设备问题阻断主流程）。
pub fn list_input_devices() -> Vec<AudioDeviceInfo> {
    let host = cpal::default_host();
    let mut out = Vec::new();
    match host.input_devices() {
        Ok(devices) => {
            for device in devices {
                if let Ok(name) = device.name() {
                    if name.trim().is_empty() {
                        continue;
                    }
                    out.push(AudioDeviceInfo {
                        device_id: name.clone(),
                        display_name: name,
                        device_type: AudioDeviceType::Capture,
                    });
                }
            }
        }
        Err(e) => {
            tracing::warn!(error = %e, "枚举输入设备失败");
        }
    }
    out
}

/// 枚举可用的输出设备（扬声器，供回环参考用）。失败时返回空列表。
pub fn list_output_devices() -> Vec<AudioDeviceInfo> {
    let host = cpal::default_host();
    let mut out = Vec::new();
    match host.output_devices() {
        Ok(devices) => {
            for device in devices {
                if let Ok(name) = device.name() {
                    if name.trim().is_empty() {
                        continue;
                    }
                    out.push(AudioDeviceInfo {
                        device_id: name.clone(),
                        display_name: name,
                        device_type: AudioDeviceType::Render,
                    });
                }
            }
        }
        Err(e) => {
            tracing::warn!(error = %e, "枚举输出设备失败");
        }
    }
    out
}

/// 当前平台是否支持系统回环采集（用于前端置灰）。
///
/// 委托给 [`crate::audio_loopback::loopback_supported`]：Windows 走 WASAPI
/// render-loopback 返回 `true`，其余平台返回 `false`。
pub fn loopback_supported() -> bool {
    crate::audio_loopback::loopback_supported()
}
