//! 跨进程协议与投递栅栏核心层（零系统依赖，纯确定性逻辑）。
//!
//! 职责单一：
//! - [`frame`]：C++ 原生 TSF 客户端与 Rust 守护进程之间的 4-byte LE 二进制帧编解码；
//! - [`utf16_slice`]：跨输入框切换时的 UTF-16 边界冻结与代理对安全切片（防止截断 Emoji / 扩展汉字）；
//! - [`fence`]：`Epoch + Generation + TargetRevision` 三重投递栅栏与翻译模式句首目标 FIFO 队列。

pub mod fence;
pub mod frame;
pub mod utf16_slice;

pub use fence::{DeliveryFence, PeerId, TargetBinding, TranslationTargetQueue};
pub use frame::{
    read_raw_frame, write_raw_frame, ClientCommand, ClientFrame, ProtocolError, ServerFrame,
    ServerKind, StandaloneIdentityContract, MAXIMUM_FRAME_BYTES,
};
pub use utf16_slice::{slice_from_utf16_boundary, utf16_len};
