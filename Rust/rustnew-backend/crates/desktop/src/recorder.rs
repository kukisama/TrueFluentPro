//! 独立录音机：不依赖云识别，直接把系统回环 PCM 编码落盘，便于单测录音质量。
//!
//! 设计（避免“皇帝类”、便于跨平台）：
//! - 采集走 [`crate::audio_loopback`] 抽象（Windows = WASAPI render-loopback），
//!   编码走 [`tfp_core::audio_recorder`] 抽象（WAV / MP3），二者解耦；
//! - 本模块只负责把两端接起来并管理会话生命周期，不感知具体平台/容器。
//!
//! 录音格式取 [`tfp_core::RecorderSettings::format`]：采集层据此向 WASAPI 请求
//! 目标格式（autoconvert 重采样/混音），编码层据此写文件头，二者天然一致。

use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use tfp_core::audio_recorder::{open_recorder, SampleEncoder};
use tfp_core::RecorderSettings;

use crate::audio_loopback::{create_loopback_capture, LoopbackFormat, LoopbackHandle};

/// 录音机（由 Tauri 托管，单例；同一时刻仅一个会话）。
#[derive(Default)]
pub struct Recorder {
    inner: Mutex<Option<RecordingSession>>,
}

/// 进行中的录音会话。
struct RecordingSession {
    /// 回环采集句柄（停止时先停采集，确保不再写入编码器）。
    loopback: Box<dyn LoopbackHandle>,
    /// 编码器：采集线程写入、停止时收尾，故用 `Arc<Mutex<…>>` 共享。
    encoder: Arc<Mutex<Box<dyn SampleEncoder>>>,
    /// 输出文件路径（停止时回传给前端）。
    path: PathBuf,
}

impl Recorder {
    /// 开始录音：打开编码器并启动回环采集，把 PCM 持续写入文件。
    pub fn start(&self, path: PathBuf, settings: RecorderSettings) -> Result<(), String> {
        let mut guard = self.inner.lock().map_err(|e| e.to_string())?;
        if guard.is_some() {
            return Err("录音已在进行中".into());
        }

        let capture = create_loopback_capture()
            .ok_or_else(|| "当前平台不支持系统回环采集".to_string())?;
        let encoder = open_recorder(&path, &settings).map_err(|e| format!("创建录音文件失败：{e}"))?;
        let encoder = Arc::new(Mutex::new(encoder));

        let format = LoopbackFormat {
            sample_rate: settings.format.sample_rate,
            bits_per_sample: settings.format.bits_per_sample,
            channels: settings.format.channels,
        };

        let enc_for_sink = encoder.clone();
        let handle = capture
            .start(
                format,
                Box::new(move |bytes| {
                    if let Ok(mut enc) = enc_for_sink.lock() {
                        if let Err(e) = enc.write_pcm(bytes) {
                            tracing::warn!(error = %e, "录音写入失败");
                        }
                    }
                }),
            )
            .map_err(|e| format!("启动回环采集失败：{e}"))?;

        *guard = Some(RecordingSession {
            loopback: handle,
            encoder,
            path,
        });
        Ok(())
    }

    /// 停止录音：先停采集线程，再收尾编码器，返回输出文件路径。
    pub fn stop(&self) -> Result<String, String> {
        let mut guard = self.inner.lock().map_err(|e| e.to_string())?;
        let session = guard.take().ok_or_else(|| "当前没有进行中的录音".to_string())?;
        drop(guard);

        // 先停采集（join 采集线程），确保此后不再有写入，再安全收尾。
        session.loopback.stop();
        let mut enc = session.encoder.lock().map_err(|e| e.to_string())?;
        enc.finalize().map_err(|e| format!("录音收尾失败：{e}"))?;
        Ok(session.path.to_string_lossy().to_string())
    }

    /// 是否正在录音。
    pub fn is_recording(&self) -> bool {
        self.inner.lock().map(|g| g.is_some()).unwrap_or(false)
    }
}
