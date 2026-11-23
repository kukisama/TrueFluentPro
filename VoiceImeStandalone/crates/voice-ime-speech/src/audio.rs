//! 音频设备枚举与 16kHz / 16-bit / 单声道 PCM 实时采集。
//!
//! - Windows 生产环境通过 `cpal` (WASAPI) 枚举并打开指定麦克风，自动完成多声道混音与线性重采样至 16kHz PCM16LE；
//! - 抽象 `AudioCaptureProvider` trait，使单元测试与集成测试可在无声卡容器内注入确定性音频流。

use serde::{Deserialize, Serialize};
use tokio::sync::mpsc;

pub const TARGET_SAMPLE_RATE: u32 = 16_000;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AudioDeviceItem {
    pub device_id: String,
    pub display_name: String,
}

/// 停止麦克风采集的 RAII 守卫句柄。
pub trait ActiveCaptureStop: Send {
    fn stop(&mut self);
}

/// 可插拔麦克风采集提供者。
pub trait AudioCaptureProvider: Send + Sync {
    fn start_capture(
        &self,
        device_id: &str,
    ) -> Result<(mpsc::Receiver<Vec<u8>>, Box<dyn ActiveCaptureStop>), String>;
}

/// 枚举本机麦克风输入设备（首项始终附带「系统默认麦克风」选项）。
pub fn list_microphone_devices() -> Vec<AudioDeviceItem> {
    let mut items = vec![AudioDeviceItem {
        device_id: String::new(),
        display_name: "系统默认麦克风".into(),
    }];
    items.extend(platform_list_devices());
    items
}

#[derive(Default, Clone)]
pub struct SystemAudioProvider;

impl AudioCaptureProvider for SystemAudioProvider {
    fn start_capture(
        &self,
        device_id: &str,
    ) -> Result<(mpsc::Receiver<Vec<u8>>, Box<dyn ActiveCaptureStop>), String> {
        platform_start_capture(device_id)
    }
}

/// 将任意采样率与声道数的 `f32` 交错音频采样重采样并量化为 16kHz 单声道 PCM16LE 字节流。
pub fn downmix_and_resample_to_pcm16le(
    interleaved: &[f32],
    channels: usize,
    src_rate: u32,
    dst_rate: u32,
) -> Vec<u8> {
    if channels == 0 || interleaved.is_empty() || src_rate == 0 || dst_rate == 0 {
        return Vec::new();
    }
    let frames = interleaved.len() / channels;
    if frames == 0 {
        return Vec::new();
    }
    let mut mono = Vec::with_capacity(frames);
    for frame in interleaved.chunks_exact(channels) {
        let sum: f32 = frame.iter().copied().sum();
        mono.push(sum / channels as f32);
    }

    if src_rate == dst_rate {
        let mut out = Vec::with_capacity(mono.len() * 2);
        for s in mono {
            let q = (s.clamp(-1.0, 1.0) * 32767.0) as i16;
            out.extend_from_slice(&q.to_le_bytes());
        }
        return out;
    }

    let out_frames = ((frames as u64 * dst_rate as u64) / src_rate as u64).max(1) as usize;
    let ratio = src_rate as f64 / dst_rate as f64;
    let mut out = Vec::with_capacity(out_frames * 2);
    for i in 0..out_frames {
        let src_pos = i as f64 * ratio;
        let idx = (src_pos.floor() as usize).min(frames - 1);
        let next = (idx + 1).min(frames - 1);
        let frac = (src_pos - idx as f64) as f32;
        let sample = mono[idx] * (1.0 - frac) + mono[next] * frac;
        let q = (sample.clamp(-1.0, 1.0) * 32767.0) as i16;
        out.extend_from_slice(&q.to_le_bytes());
    }
    out
}

#[cfg(windows)]
fn platform_list_devices() -> Vec<AudioDeviceItem> {
    use cpal::traits::{DeviceTrait, HostTrait};
    let host = cpal::default_host();
    let mut list = Vec::new();
    if let Ok(devices) = host.input_devices() {
        for dev in devices {
            if let Ok(name) = dev.name() {
                let trimmed = name.trim();
                if !trimmed.is_empty() {
                    list.push(AudioDeviceItem {
                        device_id: trimmed.to_string(),
                        display_name: trimmed.to_string(),
                    });
                }
            }
        }
    }
    list
}

#[cfg(not(windows))]
fn platform_list_devices() -> Vec<AudioDeviceItem> {
    Vec::new()
}

#[cfg(windows)]
fn platform_start_capture(
    device_id: &str,
) -> Result<(mpsc::Receiver<Vec<u8>>, Box<dyn ActiveCaptureStop>), String> {
    use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
    use cpal::{SampleFormat, StreamConfig};
    use std::sync::mpsc as std_mpsc;

    let (pcm_tx, pcm_rx) = mpsc::channel::<Vec<u8>>(128);
    let (stop_tx, stop_rx) = std_mpsc::channel::<()>();
    let (init_tx, init_rx) = std_mpsc::channel::<Result<(), String>>();
    let requested_name = device_id.trim().to_string();

    // cpal::Stream 非 Send，固定在独立采集线程生命周期内持有并安全释放
    let join = std::thread::Builder::new()
        .name("voice-ime-wasapi-mic".into())
        .spawn(move || {
            let host = cpal::default_host();
            let device = if requested_name.is_empty() {
                host.default_input_device()
                    .ok_or_else(|| "未检测到可用的默认麦克风输入设备。".to_string())
            } else {
                host.input_devices()
                    .map_err(|_| "无法枚举系统音频输入设备。".to_string())?
                    .find(|d| d.name().map(|n| n.trim() == requested_name).unwrap_or(false))
                    .ok_or_else(|| format!("配置的麦克风「{requested_name}」当前不可用，请重新选择输入设备。"))
            };

            let device = match device {
                Ok(d) => d,
                Err(msg) => {
                    let _ = init_tx.send(Err(msg));
                    return;
                }
            };

            let supported = match device.default_input_config() {
                Ok(c) => c,
                Err(_) => {
                    let _ = init_tx.send(Err("无法读取麦克风采样格式，请检查系统麦克风权限。".into()));
                    return;
                }
            };

            let sample_format = supported.sample_format();
            let config: StreamConfig = supported.into();
            let channels = config.channels as usize;
            let sample_rate = config.sample_rate.0;
            let tx = pcm_tx;

            let err_fn = |err| {
                tracing::warn!(?err, "麦克风音频流采集警告");
            };

            let stream_res = match sample_format {
                SampleFormat::F32 => device.build_input_stream(
                    &config,
                    move |data: &[f32], _| {
                        let bytes = downmix_and_resample_to_pcm16le(data, channels, sample_rate, TARGET_SAMPLE_RATE);
                        if !bytes.is_empty() {
                            let _ = tx.try_send(bytes);
                        }
                    },
                    err_fn,
                    None,
                ),
                SampleFormat::I16 => device.build_input_stream(
                    &config,
                    move |data: &[i16], _| {
                        let floats: Vec<f32> = data.iter().map(|&v| v as f32 / 32768.0).collect();
                        let bytes = downmix_and_resample_to_pcm16le(&floats, channels, sample_rate, TARGET_SAMPLE_RATE);
                        if !bytes.is_empty() {
                            let _ = tx.try_send(bytes);
                        }
                    },
                    err_fn,
                    None,
                ),
                SampleFormat::U16 => device.build_input_stream(
                    &config,
                    move |data: &[u16], _| {
                        let floats: Vec<f32> = data
                            .iter()
                            .map(|&v| (v as f32 - 32768.0) / 32768.0)
                            .collect();
                        let bytes = downmix_and_resample_to_pcm16le(&floats, channels, sample_rate, TARGET_SAMPLE_RATE);
                        if !bytes.is_empty() {
                            let _ = tx.try_send(bytes);
                        }
                    },
                    err_fn,
                    None,
                ),
                _ => {
                    let _ = init_tx.send(Err("不支持当前麦克风的采样位深格式。".into()));
                    return;
                }
            };

            let stream = match stream_res {
                Ok(s) => s,
                Err(_) => {
                    let _ = init_tx.send(Err("无法打开麦克风音频流，请检查麦克风隐私权限。".into()));
                    return;
                }
            };

            if stream.play().is_err() {
                let _ = init_tx.send(Err("启动麦克风采集失败，请检查设备是否被独占占用。".into()));
                return;
            }

            let _ = init_tx.send(Ok(()));
            let _ = stop_rx.recv();
            drop(stream);
        })
        .map_err(|_| "无法创建麦克风采集线程。".to_string())?;

    match init_rx.recv_timeout(std::time::Duration::from_secs(5)) {
        Ok(Ok(())) => Ok((
            pcm_rx,
            Box::new(WasapiStopHandle {
                stop_tx: Some(stop_tx),
                join: Some(join),
            }),
        )),
        Ok(Err(msg)) => Err(msg),
        Err(_) => Err("启动麦克风超时，请检查音频设备状态。".into()),
    }
}

#[cfg(windows)]
struct WasapiStopHandle {
    stop_tx: Option<std::sync::mpsc::Sender<()>>,
    join: Option<std::thread::JoinHandle<()>>,
}

#[cfg(windows)]
impl ActiveCaptureStop for WasapiStopHandle {
    fn stop(&mut self) {
        if let Some(tx) = self.stop_tx.take() {
            let _ = tx.send(());
        }
        if let Some(j) = self.join.take() {
            let _ = j.join();
        }
    }
}

#[cfg(windows)]
impl Drop for WasapiStopHandle {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(not(windows))]
fn platform_start_capture(
    _device_id: &str,
) -> Result<(mpsc::Receiver<Vec<u8>>, Box<dyn ActiveCaptureStop>), String> {
    Err("当前非 Windows 环境未启用物理 WASAPI 麦克风。".into())
}
