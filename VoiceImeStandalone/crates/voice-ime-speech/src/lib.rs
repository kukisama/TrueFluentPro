//! 传统微软 Speech 实时语音识别与语音翻译引擎（原生支持中国区 21Vianet 与国际版 Global）。
//!
//! 模块职责：
//! - [`sts`]：`POST .../sts/v1.0/issueToken` 订阅连通性测试与 ******
//! - [`usp`]：微软 Speech USP WebSocket 协议帧封装（`speech.config`、`speech.context`、WAV/PCM 音频帧、识别与翻译结果解析）；
//! - [`audio`]：麦克风设备枚举与 16kHz / 16-bit / 单声道 PCM 采集重采样；
//! - [`particle_filter`]：中文句尾语气词过滤；
//! - [`session`]：统一实时会话状态机（支持 STT、Continuous LID 自动语种、实时语音翻译及后台零费用连接预热）。

pub mod audio;
pub mod particle_filter;
pub mod session;
pub mod sts;
pub mod usp;

pub use audio::{list_microphone_devices, AudioCaptureProvider, AudioDeviceItem, SystemAudioProvider};
pub use particle_filter::filter_modal_particles;
pub use session::{
    PrewarmedSpeechHandle, SpeechEngineFactory, SpeechEvent, SpeechSessionHandle,
    UspSpeechEngineFactory,
};
pub use sts::{StsTokenCache, SubscriptionValidationOutcome};
pub use usp::normalize_translation_target_language;
