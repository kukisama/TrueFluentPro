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

/// 保留上一帧与分数相位，使不同 WASAPI 回调分块得到同一条 PCM 流。
#[cfg(any(windows, test))]
struct PcmResampler {
    channels: usize,
    src_rate: u32,
    previous: Option<f32>,
    remaining: u32,
}

#[cfg(any(windows, test))]
impl PcmResampler {
    fn new(channels: usize, src_rate: u32) -> Self {
        Self { channels, src_rate, previous: None, remaining: src_rate }
    }

    fn push<T: Copy>(&mut self, interleaved: &[T], normalize: impl Fn(T) -> f32) -> Vec<u8> {
        if self.channels == 0 || self.src_rate == 0 { return Vec::new(); }
        let frames = interleaved.len() / self.channels;
        let mut out = Vec::with_capacity((frames * TARGET_SAMPLE_RATE as usize / self.src_rate as usize + 2) * 2);
        let mut sample = |x: f32| out.extend_from_slice(&((x.clamp(-1.0, 1.0) * 32767.0) as i16).to_le_bytes());
        for frame in interleaved.chunks_exact(self.channels) {
            let mono = frame.iter().copied().map(&normalize).sum::<f32>() / self.channels as f32;
            let previous = self.previous.replace(mono);
            if self.src_rate == TARGET_SAMPLE_RATE || previous.is_none() {
                sample(mono);
                continue;
            }
            let previous = previous.unwrap();
            while self.remaining <= TARGET_SAMPLE_RATE {
                let alpha = self.remaining as f32 / TARGET_SAMPLE_RATE as f32;
                sample(previous + (mono - previous) * alpha);
                self.remaining += self.src_rate;
            }
            self.remaining -= TARGET_SAMPLE_RATE;
        }
        out
    }
}

#[cfg(windows)]
fn pcm_callback<T: Copy>(mut converter: PcmResampler, normalize: impl Fn(T) -> f32,
    tx: mpsc::Sender<Vec<u8>>, stop: std::sync::mpsc::Sender<()>,
) -> impl FnMut(&[T], &cpal::InputCallbackInfo) {
    move |data, _| {
        let bytes = converter.push(data, &normalize);
        if !bytes.is_empty() && tx.try_send(bytes).is_err() {
            let _ = stop.send(());
        }
    }
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
    let error_stop = stop_tx.clone();

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
                    .map_err(|_| "无法枚举系统音频输入设备。".to_string())
                    .and_then(|mut devices| {
                        devices
                            .find(|d| d.name().map(|n| n.trim() == requested_name).unwrap_or(false))
                            .ok_or_else(|| format!("配置的麦克风「{requested_name}」当前不可用，请重新选择输入设备。"))
                    })
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
            let converter = PcmResampler::new(channels, sample_rate);
            let pcm_stop = error_stop.clone();
            let err_fn = move |err| {
                tracing::warn!(?err, "麦克风音频流采集警告");
                let _ = error_stop.send(());
            };

            let stream_res = match sample_format {
                SampleFormat::F32 => device.build_input_stream(
                    &config,
                    pcm_callback(converter, |x: f32| x, pcm_tx, pcm_stop),
                    err_fn,
                    None,
                ),
                SampleFormat::I16 => device.build_input_stream(
                    &config,
                    pcm_callback(converter, |x: i16| x as f32 / 32768.0, pcm_tx, pcm_stop),
                    err_fn,
                    None,
                ),
                SampleFormat::U16 => device.build_input_stream(
                    &config,
                    pcm_callback(converter, |x: u16| (x as f32 - 32768.0) / 32768.0, pcm_tx, pcm_stop),
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

            if !matches!(stop_rx.try_recv(), Err(std_mpsc::TryRecvError::Empty)) {
                return;
            }
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
        Err(_) => {
            let _ = stop_tx.send(());
            let _ = join.join();
            Err("启动麦克风超时，请检查音频设备状态。".into())
        }
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn irregular_wasapi_buffers_preserve_samples_and_duration() {
        let signal: Vec<f32> = (0..88200).map(|i| (i as f32 / 30.0).sin()).collect();
        let reference = PcmResampler::new(2, 44100).push(&signal, |x| x);
        let mut resampler = PcmResampler::new(2, 44100);
        let parts: Vec<_> = signal.chunks(514).flat_map(|p| resampler.push(p, |x| x)).collect();
        assert_eq!(reference.len(), 32000);
        assert_eq!(reference, parts);
    }
}
