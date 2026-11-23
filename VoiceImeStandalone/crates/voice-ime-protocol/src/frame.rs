//! 二进制字节帧编解码契约（严格对齐阶段 1 `Common.h` 与 `PipeClient.cpp`）。
//!
//! 线格式：`[4 字节 little-endian 长度 N (1..=65536)] [1 字节 Kind] [N-1 字节严格 UTF-8 载荷]`。

use thiserror::Error;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

/// 单帧最大载荷字节数（含 1 字节 Kind）。
pub const MAXIMUM_FRAME_BYTES: usize = 65536;

/// 与阶段 1 `StandaloneIdentity.h` 严格对应的跨进程固定常量。
pub struct StandaloneIdentityContract;

impl StandaloneIdentityContract {
    pub const CLSID: &'static str = "{7D4F62C1-8B34-4E2A-9C51-3F8A21D70E64}";
    pub const PROFILE_GUID: &'static str = "{C28419E3-5A76-4B18-8F29-6E03B5A94C82}";
    pub const OWNER_VALUE: &'static str = "TrueFluentVoiceIme.Standalone.v1";
    pub const DISPLAY_NAME: &'static str = "译见pro语音输入法";
    pub const CONFIG_REG_KEY: &'static str = r"Software\TrueFluentVoiceIme";
    pub const UI_REG_KEY: &'static str = r"Software\TrueFluentVoiceIme\UI";
    pub const INSTALL_SUBDIR: &'static str = "TrueFluentVoiceIme";
    pub const SHARED_PIPE_PREFIX: &'static str = "TrueFluentVoiceIme.Shared.";
    pub const DLL_FILE_NAME: &'static str = "VoiceImeStandalone.dll";
    pub const HELPER_FILE_NAME: &'static str = "VoiceImeRegister.exe";
    pub const HOST_EXE_NAME: &'static str = "TrueFluentVoiceImeHost.exe";
    pub const TOOLS_EXE_NAME: &'static str = "TrueFluentVoiceImeTools.exe";

    /// 构建当前桌面会话与用户 SID 隔离的共享命名管道名（不含 `\\.\pipe\` 前缀）。
    pub fn shared_pipe_name(session_id: u32, sid: &str) -> String {
        format!("{}{}.{}", Self::SHARED_PIPE_PREFIX, session_id, sid)
    }
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum ProtocolError {
    #[error("帧长度越界: {0}")]
    InvalidFrameLength(usize),
    #[error("载荷不是严格的 UTF-8 文本")]
    InvalidUtf8,
    #[error("未知的上行命令类型: {0:?}")]
    UnknownClientKind(char),
    #[error("未知的下行消息类型: {0:?}")]
    UnknownServerKind(char),
    #[error("十进制无符号整数格式无效")]
    InvalidDecimal,
    #[error("服务端帧信封格式无效")]
    InvalidServerEnvelope,
    #[error("管道 I/O 错误: {0}")]
    Io(String),
}

/// C++ TSF 客户端发往 Rust 守护进程的上行命令。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ClientCommand {
    /// `H`：连接握手，携带客户端 UI 线程 ID（`1..=u32::MAX`）。
    Hello { thread_id: u32 },
    /// `L`：目标进程在前台且已选中本输入法（触发显示悬浮条及后台零费用预热）。
    Selected { generation: u64 },
    /// `B`：目标输入框通过 InputScope/读写门禁并绑定当前 `generation`（`generation > 0`）。
    BindTarget { generation: u64 },
    /// `U`：目标输入框失焦、切换上下文或只读，解绑指定 `generation`。
    UnbindTarget { generation: u64 },
    /// `T`：用户按下 F8 或点击悬浮条麦克风，切换开始/停止录音。
    Toggle { generation: u64 },
    /// `D`：用户切换到其他输入法或进入受抑制窗口（隐藏悬浮条并停止当前录音）。
    Deselected { generation: u64 },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClientFrame {
    pub command: ClientCommand,
}

impl ClientFrame {
    /// 从原始 `(kind, text)` 解析客户端上行命令（严格校验十进制无前导符号及数值范围）。
    pub fn parse(kind: char, text: &str) -> Result<Self, ProtocolError> {
        let value = parse_strict_u64(text)?;
        let command = match kind {
            'H' => {
                if value == 0 || value > u32::MAX as u64 {
                    return Err(ProtocolError::InvalidDecimal);
                }
                ClientCommand::Hello {
                    thread_id: value as u32,
                }
            }
            'L' => ClientCommand::Selected { generation: value },
            'B' => ClientCommand::BindTarget { generation: value },
            'U' => ClientCommand::UnbindTarget { generation: value },
            'T' => ClientCommand::Toggle { generation: value },
            'D' => ClientCommand::Deselected { generation: value },
            other => return Err(ProtocolError::UnknownClientKind(other)),
        };
        Ok(Self { command })
    }

    pub fn encode(&self) -> Result<Vec<u8>, ProtocolError> {
        let (kind, num) = match self.command {
            ClientCommand::Hello { thread_id } => ('H', thread_id as u64),
            ClientCommand::Selected { generation } => ('L', generation),
            ClientCommand::BindTarget { generation } => ('B', generation),
            ClientCommand::UnbindTarget { generation } => ('U', generation),
            ClientCommand::Toggle { generation } => ('T', generation),
            ClientCommand::Deselected { generation } => ('D', generation),
        };
        encode_frame_bytes(kind, &num.to_string())
    }
}

/// Rust 守护进程发往 C++ TSF 客户端的下行帧类型。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ServerKind {
    /// `I`：空闲状态同步。
    Idle,
    /// `C`：正在建立语音识别连接。
    Connecting,
    /// `R`：麦克风与云端识别已就绪，开始录音。
    Recording,
    /// `P`：当前句临时识别文本（修订 TSF Composition）。
    Partial,
    /// `F`：当前句定稿文本（提交并结束 TSF Composition）。
    Final,
    /// `E`：脱敏错误提示状态。
    Error,
    /// `S`：录音已停止。
    Stopped,
}

impl ServerKind {
    pub fn as_char(self) -> char {
        match self {
            ServerKind::Idle => 'I',
            ServerKind::Connecting => 'C',
            ServerKind::Recording => 'R',
            ServerKind::Partial => 'P',
            ServerKind::Final => 'F',
            ServerKind::Error => 'E',
            ServerKind::Stopped => 'S',
        }
    }

    pub fn from_char(ch: char) -> Result<Self, ProtocolError> {
        match ch {
            'I' => Ok(ServerKind::Idle),
            'C' => Ok(ServerKind::Connecting),
            'R' => Ok(ServerKind::Recording),
            'P' => Ok(ServerKind::Partial),
            'F' => Ok(ServerKind::Final),
            'E' => Ok(ServerKind::Error),
            'S' => Ok(ServerKind::Stopped),
            other => Err(ProtocolError::UnknownServerKind(other)),
        }
    }
}

/// 守护进程发往客户端的带栅栏信封帧：`<epoch>:<target_generation>:<text>`。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerFrame {
    pub kind: ServerKind,
    pub epoch: u64,
    pub target_generation: u64,
    pub text: String,
}

impl ServerFrame {
    pub fn status(kind: ServerKind, epoch: u64, message: impl Into<String>) -> Self {
        Self {
            kind,
            epoch,
            target_generation: 0,
            text: message.into(),
        }
    }

    pub fn text_result(
        final_result: bool,
        epoch: u64,
        target_generation: u64,
        text: impl Into<String>,
    ) -> Self {
        Self {
            kind: if final_result {
                ServerKind::Final
            } else {
                ServerKind::Partial
            },
            epoch,
            target_generation,
            text: text.into(),
        }
    }

    /// 编码为 `<epoch>:<target_generation>:<text>` 二进制帧（对齐 `Common.h::SharedPayload`）。
    pub fn encode(&self) -> Result<Vec<u8>, ProtocolError> {
        let is_text = matches!(self.kind, ServerKind::Partial | ServerKind::Final);
        if is_text && self.target_generation == 0 {
            return Err(ProtocolError::InvalidServerEnvelope);
        }
        if !is_text && self.target_generation != 0 {
            return Err(ProtocolError::InvalidServerEnvelope);
        }
        let payload = format!("{}:{}:{}", self.epoch, self.target_generation, self.text);
        encode_frame_bytes(self.kind.as_char(), &payload)
    }

    /// 解析服务端下行帧（供协议黑盒回归测试与客户端校验复用）。
    pub fn decode_payload(kind_char: char, payload: &str) -> Result<Self, ProtocolError> {
        let kind = ServerKind::from_char(kind_char)?;
        let (epoch_str, rest) = payload
            .split_once(':')
            .ok_or(ProtocolError::InvalidServerEnvelope)?;
        let (target_str, text_part) = rest
            .split_once(':')
            .ok_or(ProtocolError::InvalidServerEnvelope)?;
        let epoch = parse_strict_u64(epoch_str)?;
        let target_generation = parse_strict_u64(target_str)?;
        let is_text = matches!(kind, ServerKind::Partial | ServerKind::Final);
        if is_text && target_generation == 0 {
            return Err(ProtocolError::InvalidServerEnvelope);
        }
        if !is_text && target_generation != 0 {
            return Err(ProtocolError::InvalidServerEnvelope);
        }
        Ok(Self {
            kind,
            epoch,
            target_generation,
            text: if is_text {
                text_part.to_string()
            } else {
                text_part.to_string()
            },
        })
    }
}

pub fn parse_strict_u64(text: &str) -> Result<u64, ProtocolError> {
    if text.is_empty() || text.len() > 20 || !text.bytes().all(|b| b.is_ascii_digit()) {
        return Err(ProtocolError::InvalidDecimal);
    }
    text.parse::<u64>().map_err(|_| ProtocolError::InvalidDecimal)
}

pub fn encode_frame_bytes(kind: char, text: &str) -> Result<Vec<u8>, ProtocolError> {
    if !kind.is_ascii() {
        return Err(ProtocolError::InvalidUtf8);
    }
    let body_len = 1usize
        .checked_add(text.len())
        .ok_or(ProtocolError::InvalidFrameLength(usize::MAX))?;
    if body_len > MAXIMUM_FRAME_BYTES {
        return Err(ProtocolError::InvalidFrameLength(body_len));
    }
    let mut out = Vec::with_capacity(4 + body_len);
    out.extend_from_slice(&(body_len as u32).to_le_bytes());
    out.push(kind as u8);
    out.extend_from_slice(text.as_bytes());
    Ok(out)
}

pub async fn read_raw_frame<R: AsyncRead + Unpin>(
    reader: &mut R,
) -> Result<(char, String), ProtocolError> {
    let mut header = [0u8; 4];
    reader
        .read_exact(&mut header)
        .await
        .map_err(|e| ProtocolError::Io(e.to_string()))?;
    let len = u32::from_le_bytes(header) as usize;
    if !(1..=MAXIMUM_FRAME_BYTES).contains(&len) {
        return Err(ProtocolError::InvalidFrameLength(len));
    }
    let mut buf = vec![0u8; len];
    reader
        .read_exact(&mut buf)
        .await
        .map_err(|e| ProtocolError::Io(e.to_string()))?;
    let kind = buf[0] as char;
    let text = std::str::from_utf8(&buf[1..]).map_err(|_| ProtocolError::InvalidUtf8)?;
    Ok((kind, text.to_string()))
}

pub async fn write_raw_frame<W: AsyncWrite + Unpin>(
    writer: &mut W,
    bytes: &[u8],
) -> Result<(), ProtocolError> {
    writer
        .write_all(bytes)
        .await
        .map_err(|e| ProtocolError::Io(e.to_string()))?;
    writer
        .flush()
        .await
        .map_err(|e| ProtocolError::Io(e.to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strict_decimal_rejects_signs_and_overflow() {
        assert_eq!(parse_strict_u64("0"), Ok(0));
        assert_eq!(parse_strict_u64("18446744073709551615"), Ok(u64::MAX));
        assert!(parse_strict_u64("+7").is_err());
        assert!(parse_strict_u64(" 7").is_err());
        assert!(parse_strict_u64("18446744073709551616").is_err());
        assert!(parse_strict_u64("").is_err());
    }

    #[test]
    fn server_envelope_preserves_colons_in_text() {
        let frame = ServerFrame::text_result(false, 7, 18, "正文:冒号:Emoji😀");
        let encoded = frame.encode().unwrap();
        let len = u32::from_le_bytes(encoded[0..4].try_into().unwrap()) as usize;
        assert_eq!(len, encoded.len() - 4);
        let kind = encoded[4] as char;
        let payload = std::str::from_utf8(&encoded[5..]).unwrap();
        let decoded = ServerFrame::decode_payload(kind, payload).unwrap();
        assert_eq!(decoded.kind, ServerKind::Partial);
        assert_eq!(decoded.epoch, 7);
        assert_eq!(decoded.target_generation, 18);
        assert_eq!(decoded.text, "正文:冒号:Emoji😀");
    }

    #[test]
    fn status_and_text_target_invariants_enforced() {
        assert!(ServerFrame::text_result(true, 1, 0, "bad").encode().is_err());
        let mut bad_status = ServerFrame::status(ServerKind::Recording, 1, "");
        bad_status.target_generation = 3;
        assert!(bad_status.encode().is_err());
    }
}
