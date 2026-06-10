//! 系统回环采集（按平台抽象）。
//!
//! 设计目标（避免“皇帝类”）：
//! - 主流程只依赖 [`LoopbackCapture`] / [`LoopbackHandle`] 两个 trait，
//!   不关心底层用的是 WASAPI / PulseAudio monitor / CoreAudio 虚拟设备。
//! - 各平台实现放在独立 `cfg` 模块里；新增平台只需补一个实现 + 在
//!   [`create_loopback_capture`] 里登记，无需改动调用方。
//! - 采集统一输出 16kHz / 16bit / 单声道小端 PCM（识别推流的目标格式），
//!   平台层负责重采样（Windows 走 WASAPI autoconvert）。
//!
//! 当前仅落地 Windows（WASAPI render-loopback）。其它平台返回
//! [`LoopbackError::Unsupported`]，前端据 [`loopback_supported`] 置灰。

/// 回环采集统一输出：采样率（Hz）。
pub const LOOPBACK_SAMPLE_RATE: u32 = 16_000;
/// 回环采集统一输出：位深（bit）。
pub const LOOPBACK_BITS: u16 = 16;
/// 回环采集统一输出：声道数。
pub const LOOPBACK_CHANNELS: u16 = 1;

/// 回环采集的目标 PCM 格式。
///
/// 识别走 [`LoopbackFormat::recognition`]（16k/16bit/单声道）；独立录音可按
/// [`tfp_core::RecorderSettings`] 请求高保真格式（如 48k/16bit/立体声）。
/// 平台层（WASAPI autoconvert）负责把设备原始格式重采样/混音到此目标。
#[derive(Debug, Clone, Copy)]
pub struct LoopbackFormat {
    /// 采样率（Hz）。
    pub sample_rate: u32,
    /// 位深（当前仅支持 16-bit）。
    pub bits_per_sample: u16,
    /// 声道数。
    pub channels: u16,
}

impl LoopbackFormat {
    /// 识别推流目标格式：16kHz / 16bit / 单声道。
    pub const fn recognition() -> Self {
        Self {
            sample_rate: LOOPBACK_SAMPLE_RATE,
            bits_per_sample: LOOPBACK_BITS,
            channels: LOOPBACK_CHANNELS,
        }
    }
}

/// 回环采集错误。
#[derive(Debug, thiserror::Error)]
pub enum LoopbackError {
    /// 当前平台尚未实现系统回环采集。
    #[error("当前平台不支持系统回环采集")]
    Unsupported,
    /// 初始化阶段失败（设备枚举 / 客户端初始化等）。
    #[error("回环采集初始化失败：{0}")]
    Init(String),
    /// 运行阶段失败（读取缓冲等）。
    #[error("回环采集运行失败：{0}")]
    Runtime(String),
}

/// PCM 数据回调：每次产生一块 16kHz/16bit/单声道小端 PCM 字节。
///
/// 要求 `Send`，以便采集在后台线程产生数据后回调到使用方。
pub type PcmSink = Box<dyn FnMut(&[u8]) + Send>;

/// 回环采集器抽象。不同平台提供不同实现。
pub trait LoopbackCapture: Send {
    /// 启动后台采集，把 `format` 格式的 PCM 推给 `sink`，直到返回的句柄被 `stop` 或析构。
    fn start(
        &self,
        format: LoopbackFormat,
        sink: PcmSink,
    ) -> Result<Box<dyn LoopbackHandle>, LoopbackError>;
}

/// 采集会话句柄。`stop`（或析构）时停止采集并回收后台线程。
pub trait LoopbackHandle: Send {
    /// 主动停止采集并等待后台线程退出。
    fn stop(self: Box<Self>);
}

/// 取得当前平台的回环采集器实现；不支持的平台返回 `None`。
pub fn create_loopback_capture() -> Option<Box<dyn LoopbackCapture>> {
    #[cfg(windows)]
    {
        Some(Box::new(windows_impl::WasapiLoopback))
    }
    #[cfg(not(windows))]
    {
        None
    }
}

/// 当前平台是否支持系统回环采集（用于前端置灰）。
pub fn loopback_supported() -> bool {
    cfg!(windows)
}

#[cfg(windows)]
mod windows_impl {
    use super::{
        LoopbackCapture, LoopbackError, LoopbackFormat, LoopbackHandle, PcmSink,
    };
    use std::collections::VecDeque;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::mpsc;
    use std::sync::Arc;
    use std::thread::JoinHandle;
    use std::time::Duration;
    use wasapi::{
        initialize_mta, Direction, SampleType, StreamMode, WaveFormat,
    };

    /// Windows WASAPI render-loopback 采集器。
    pub struct WasapiLoopback;

    impl LoopbackCapture for WasapiLoopback {
        fn start(
            &self,
            format: LoopbackFormat,
            sink: PcmSink,
        ) -> Result<Box<dyn LoopbackHandle>, LoopbackError> {
            let stop = Arc::new(AtomicBool::new(false));
            let stop_thread = stop.clone();
            // 用一次性通道把“初始化是否成功”从采集线程回传，
            // 这样 start() 能同步反馈错误（设备不存在 / 客户端初始化失败等）。
            let (init_tx, init_rx) = mpsc::channel::<Result<(), LoopbackError>>();

            let join = std::thread::Builder::new()
                .name("wasapi-loopback".into())
                .spawn(move || {
                    capture_thread(format, stop_thread, sink, init_tx);
                })
                .map_err(|e| LoopbackError::Init(format!("无法创建采集线程：{e}")))?;

            match init_rx.recv() {
                Ok(Ok(())) => Ok(Box::new(WasapiHandle {
                    stop,
                    join: Some(join),
                })),
                Ok(Err(e)) => {
                    let _ = join.join();
                    Err(e)
                }
                Err(_) => {
                    let _ = join.join();
                    Err(LoopbackError::Init("采集线程提前退出".into()))
                }
            }
        }
    }

    /// 采集会话句柄：置位停止标志并 join 后台线程。
    struct WasapiHandle {
        stop: Arc<AtomicBool>,
        join: Option<JoinHandle<()>>,
    }

    impl WasapiHandle {
        fn shutdown(&mut self) {
            self.stop.store(true, Ordering::Relaxed);
            if let Some(join) = self.join.take() {
                let _ = join.join();
            }
        }
    }

    impl LoopbackHandle for WasapiHandle {
        fn stop(mut self: Box<Self>) {
            self.shutdown();
        }
    }

    impl Drop for WasapiHandle {
        fn drop(&mut self) {
            self.shutdown();
        }
    }

    /// 后台采集线程主体：初始化 WASAPI，再轮询读取 PCM 并回调。
    ///
    /// 所有 WASAPI 对象都是 `!Send`，因此完整生命周期都留在本线程内。
    fn capture_thread(
        format: LoopbackFormat,
        stop: Arc<AtomicBool>,
        mut sink: PcmSink,
        init_tx: mpsc::Sender<Result<(), LoopbackError>>,
    ) {
        // 采用轮询（Polling）而非事件驱动：回环在“无声音播放”时事件不触发，
        // 轮询可避免静音期采集线程卡死。
        let setup = (|| -> Result<_, LoopbackError> {
            // COM 初始化（MTA）。回环采集跑在独立线程，不会影响 UI 线程。
            initialize_mta()
                .ok()
                .map_err(|e| LoopbackError::Init(format!("COM 初始化失败：{e}")))?;

            let enumerator = wasapi::DeviceEnumerator::new()
                .map_err(|e| LoopbackError::Init(format!("创建设备枚举器失败：{e}")))?;
            // 回环采集的是“默认输出设备”正在播放的内容，故取 Render 设备。
            let device = enumerator
                .get_default_device(&Direction::Render)
                .map_err(|e| LoopbackError::Init(format!("获取默认输出设备失败：{e}")))?;
            let mut audio_client = device
                .get_iaudioclient()
                .map_err(|e| LoopbackError::Init(format!("创建音频客户端失败：{e}")))?;

            // 目标格式由调用方指定。autoconvert 让 WASAPI 负责重采样/混音到目标格式。
            let desired = WaveFormat::new(
                format.bits_per_sample as usize,
                format.bits_per_sample as usize,
                &SampleType::Int,
                format.sample_rate as usize,
                format.channels as usize,
                None,
            );
            let (def_time, _min_time) = audio_client
                .get_device_period()
                .map_err(|e| LoopbackError::Init(format!("读取设备周期失败：{e}")))?;
            let mode = StreamMode::PollingShared {
                autoconvert: true,
                buffer_duration_hns: def_time,
            };
            // Render 设备 + Direction::Capture + Shared ⇒ wasapi 自动启用 LOOPBACK 标志。
            audio_client
                .initialize_client(&desired, &Direction::Capture, &mode)
                .map_err(|e| LoopbackError::Init(format!("初始化回环客户端失败：{e}")))?;

            let capture_client = audio_client
                .get_audiocaptureclient()
                .map_err(|e| LoopbackError::Init(format!("获取采集客户端失败：{e}")))?;
            audio_client
                .start_stream()
                .map_err(|e| LoopbackError::Init(format!("启动采集流失败：{e}")))?;

            Ok((audio_client, capture_client))
        })();

        let (audio_client, capture_client) = match setup {
            Ok(v) => {
                let _ = init_tx.send(Ok(()));
                v
            }
            Err(e) => {
                let _ = init_tx.send(Err(e));
                return;
            }
        };

        let mut deque: VecDeque<u8> = VecDeque::new();
        // 轮询间隔取设备周期的一半左右，兼顾时延与 CPU 占用。
        let poll_interval = Duration::from_millis(10);
        while !stop.load(Ordering::Relaxed) {
            // 把当前可读的所有数据包尽量读空，再统一回调。
            loop {
                match capture_client.get_next_packet_size() {
                    Ok(Some(frames)) if frames > 0 => {
                        if let Err(e) = capture_client.read_from_device_to_deque(&mut deque) {
                            tracing::warn!(error = %e, "回环读取失败，停止采集");
                            stop.store(true, Ordering::Relaxed);
                            break;
                        }
                    }
                    Ok(_) => break,
                    Err(e) => {
                        tracing::warn!(error = %e, "回环获取包大小失败，停止采集");
                        stop.store(true, Ordering::Relaxed);
                        break;
                    }
                }
            }
            if !deque.is_empty() {
                let chunk: Vec<u8> = deque.drain(..).collect();
                sink(&chunk);
            }
            std::thread::sleep(poll_interval);
        }

        let _ = audio_client.stop_stream();
    }
}
