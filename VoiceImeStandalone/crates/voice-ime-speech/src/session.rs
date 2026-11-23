//! 统一实时语音会话（STT + Continuous LID + 实时语音翻译 + 后台零费用连接预热）。

use std::sync::Arc;
use std::time::{Duration, Instant};

use futures::{SinkExt, StreamExt};
use tokio::net::TcpStream;
use tokio::sync::{mpsc, oneshot};
use tokio_tungstenite::tungstenite::client::IntoClientRequest;
use tokio_tungstenite::tungstenite::http::HeaderValue;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::{connect_async, MaybeTlsStream, WebSocketStream};
use uuid::Uuid;
use voice_ime_config::RuntimeSpeechSessionConfig;

use crate::audio::{AudioCaptureProvider, SystemAudioProvider};
use crate::particle_filter::filter_modal_particles;
use crate::sts::StsTokenCache;
use crate::usp::{
    build_pcm16_16khz_mono_wav_header, build_speech_config_json, build_speech_context_json,
    build_speech_ws_url, format_usp_audio_message, format_usp_text_message, interpret_usp_message,
    parse_usp_incoming_text, ParsedUspEvent,
};

const START_TIMEOUT: Duration = Duration::from_secs(8);
const PREWARM_MAX_IDLE: Duration = Duration::from_secs(45);

type WsStream = WebSocketStream<MaybeTlsStream<TcpStream>>;

/// 会话向守护进程 Actor 投递的标准化语音事件。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SpeechEvent {
    /// 麦克风与云端通道均已就绪（对应向 TSF 广播 `R`）。
    Ready,
    /// 原文识别快照（供用量统计、隐私原文记录、60s 无字超时及翻译模式句首目标锁定使用）。
    SourceRecognized { text: String, is_final: bool },
    /// 最终应写入目标输入框的文本（STT 模式含 `Partial` 与 `Final`；翻译模式仅输出成功译文 `Final`）。
    OutputText { text: String, is_final: bool },
    /// 面向用户展示的脱敏失败消息。
    Failed { message: String },
}

/// 零费用后台预热句柄：预先完成 STS 鉴权、TLS 握手与 WebSocket 建连，尚未打开麦克风。
pub struct PrewarmedSpeechHandle {
    pub fingerprint: String,
    created_at: Instant,
    ws: WsStream,
    request_id: String,
    config: RuntimeSpeechSessionConfig,
}

impl PrewarmedSpeechHandle {
    pub fn is_fresh(&self, expected_fingerprint: &str) -> bool {
        self.fingerprint == expected_fingerprint && self.created_at.elapsed() < PREWARM_MAX_IDLE
    }
}

/// 运行中语音会话控制句柄（停止与等待麦克风及网络彻底释放）。
pub struct SpeechSessionHandle {
    stop_tx: Option<oneshot::Sender<()>>,
    released_rx: oneshot::Receiver<()>,
}

impl SpeechSessionHandle {
    pub fn from_channels(
        stop_tx: oneshot::Sender<()>,
        released_rx: oneshot::Receiver<()>,
    ) -> Self {
        Self {
            stop_tx: Some(stop_tx),
            released_rx,
        }
    }

    pub async fn stop_and_wait_released(mut self) {
        if let Some(tx) = self.stop_tx.take() {
            let _ = tx.send(());
        }
        let _ = self.released_rx.await;
    }
}

/// 抽象语音引擎工厂（便于守护进程 Actor 单测注入模拟引擎，生产注入 `UspSpeechEngineFactory`）。
pub trait SpeechEngineFactory: Send + Sync {
    fn prewarm(
        &self,
        fingerprint: String,
        cfg: RuntimeSpeechSessionConfig,
    ) -> tokio::task::JoinHandle<Option<PrewarmedSpeechHandle>>;

    fn start_session(
        &self,
        cfg: RuntimeSpeechSessionConfig,
        prewarmed: Option<PrewarmedSpeechHandle>,
        events_tx: mpsc::UnboundedSender<SpeechEvent>,
    ) -> SpeechSessionHandle;
}

/// 生产环境传统微软 Speech USP 引擎工厂（同时支持 Global 与 21Vianet）。
pub struct UspSpeechEngineFactory {
    sts: StsTokenCache,
    audio: Arc<dyn AudioCaptureProvider>,
    /// 是否强制先拉取 STS Token（生产环境默认开启以提前校验密钥并缓存 JWT）。
    use_sts_token: bool,
}

impl Default for UspSpeechEngineFactory {
    fn default() -> Self {
        Self::new(StsTokenCache::new(), Arc::new(SystemAudioProvider), true)
    }
}

impl UspSpeechEngineFactory {
    pub fn new(
        sts: StsTokenCache,
        audio: Arc<dyn AudioCaptureProvider>,
        use_sts_token: bool,
    ) -> Self {
        Self {
            sts,
            audio,
            use_sts_token,
        }
    }

    pub fn sts_cache(&self) -> &StsTokenCache {
        &self.sts
    }
}

impl SpeechEngineFactory for UspSpeechEngineFactory {
    fn prewarm(
        &self,
        fingerprint: String,
        cfg: RuntimeSpeechSessionConfig,
    ) -> tokio::task::JoinHandle<Option<PrewarmedSpeechHandle>> {
        let sts = self.sts.clone();
        let use_sts = self.use_sts_token;
        tokio::spawn(async move {
            let connect_fut = open_usp_websocket(&sts, &cfg, use_sts);
            match tokio::time::timeout(START_TIMEOUT, connect_fut).await {
                Ok(Ok((ws, request_id))) => Some(PrewarmedSpeechHandle {
                    fingerprint,
                    created_at: Instant::now(),
                    ws,
                    request_id,
                    config: cfg,
                }),
                _ => None,
            }
        })
    }

    fn start_session(
        &self,
        cfg: RuntimeSpeechSessionConfig,
        prewarmed: Option<PrewarmedSpeechHandle>,
        events_tx: mpsc::UnboundedSender<SpeechEvent>,
    ) -> SpeechSessionHandle {
        let (stop_tx, stop_rx) = oneshot::channel::<()>();
        let (released_tx, released_rx) = oneshot::channel::<()>();
        let sts = self.sts.clone();
        let audio = Arc::clone(&self.audio);
        let use_sts = self.use_sts_token;

        tokio::spawn(async move {
            run_usp_session(cfg, prewarmed, sts, audio, use_sts, stop_rx, events_tx).await;
            let _ = released_tx.send(());
        });

        SpeechSessionHandle {
            stop_tx: Some(stop_tx),
            released_rx,
        }
    }
}

async fn open_usp_websocket(
    sts: &StsTokenCache,
    cfg: &RuntimeSpeechSessionConfig,
    use_sts_token: bool,
) -> Result<(WsStream, String), String> {
    let bearer_opt = if use_sts_token {
        Some(
            sts.get_or_issue_token(&cfg.endpoint, &cfg.subscription_key)
                .await?,
        )
    } else {
        None
    };

    let ws_url = build_speech_ws_url(cfg)?;
    let connection_id = Uuid::new_v4().simple().to_string();
    let request_id = Uuid::new_v4().simple().to_string();

    let mut request = ws_url
        .into_client_request()
        .map_err(|_| "语音识别连接地址无效。".to_string())?;

    let headers = request.headers_mut();
    if let Some(token) = bearer_opt {
        let mut header = HeaderValue::from_str(&format!("Bearer {token}"))
            .map_err(|_| "语音服务返回的访问令牌格式无效。".to_string())?;
        header.set_sensitive(true);
        headers.insert("Authorization", header);
    } else {
        let mut header = HeaderValue::from_str(&cfg.subscription_key)
            .map_err(|_| "订阅密钥包含非法请求头字符。".to_string())?;
        header.set_sensitive(true);
        headers.insert("Ocp-Apim-Subscription-Key", header);
    }
    if let Ok(hv) = HeaderValue::from_str(&connection_id) {
        headers.insert("X-ConnectionId", hv);
    }

    let (mut ws, _) = connect_async(request)
        .await
        .map_err(|_| "无法连接到微软 Speech 实时语音服务，请检查网络、区域与密钥。".to_string())?;

    // 发送 speech.config 与 speech.context 握手帧（尚未发送音频数据，零计费预热）
    let cfg_msg = format_usp_text_message("speech.config", &request_id, &build_speech_config_json());
    ws.send(Message::Text(cfg_msg.into()))
        .await
        .map_err(|_| "发送语音配置握手失败。".to_string())?;

    let ctx_msg =
        format_usp_text_message("speech.context", &request_id, &build_speech_context_json(cfg));
    ws.send(Message::Text(ctx_msg.into()))
        .await
        .map_err(|_| "发送语音上下文握手失败。".to_string())?;

    Ok((ws, request_id))
}

async fn run_usp_session(
    cfg: RuntimeSpeechSessionConfig,
    prewarmed: Option<PrewarmedSpeechHandle>,
    sts: StsTokenCache,
    audio_provider: Arc<dyn AudioCaptureProvider>,
    use_sts: bool,
    mut stop_rx: oneshot::Receiver<()>,
    events_tx: mpsc::UnboundedSender<SpeechEvent>,
) {
    // 1. 优先复用新鲜预热连接；若无预热或已过期，则在 8s 预算内现场建连
    let connect = async {
        if let Some(warm) = prewarmed {
            if warm.created_at.elapsed() < PREWARM_MAX_IDLE && warm.config == cfg {
                return Ok((warm.ws, warm.request_id));
            }
        }
        tokio::time::timeout(START_TIMEOUT, open_usp_websocket(&sts, &cfg, use_sts))
            .await
            .map_err(|_| "语音输入启动超时，请检查网络及终结点配置后重试。".to_string())?
    };
    let ws_pair = tokio::select! {
        biased;
        _ = &mut stop_rx => return,
        result = connect => result,
    };

    let (ws, request_id) = match ws_pair {
        Ok(pair) => pair,
        Err(msg) => {
            let _ = events_tx.send(SpeechEvent::Failed { message: msg });
            return;
        }
    };

    // WASAPI 初始化是阻塞操作。等线程返回并释放采集后，才允许下一次录音。
    let device_id = cfg.microphone_device_id.clone();
    let mut opening = tokio::task::spawn_blocking(move || audio_provider.start_capture(&device_id));
    let capture = tokio::select! {
        biased;
        _ = &mut stop_rx => {
            if let Ok(Ok((_, mut capture))) = opening.await {
                capture.stop();
            }
            return;
        }
        result = &mut opening => result.unwrap_or_else(|_| Err("麦克风初始化异常中断。".into())),
    };
    let (mut pcm_rx, mut capture_stop) = match capture {
        Ok(pair) => pair,
        Err(msg) => {
            let _ = events_tx.send(SpeechEvent::Failed { message: msg });
            return;
        }
    };

    let (mut ws_sink, mut ws_stream) = ws.split();

    // 3. 发送 44 字节 WAV RIFF 流头，随后立即宣告 R (Ready)
    let wav_hdr = build_pcm16_16khz_mono_wav_header();
    let wav_frame = format_usp_audio_message(&request_id, "audio/x-wav", &wav_hdr);
    let sent = tokio::select! {
        biased;
        _ = &mut stop_rx => {
            capture_stop.stop();
            return;
        }
        result = tokio::time::timeout(START_TIMEOUT, ws_sink.send(Message::Binary(wav_frame.into()))) => {
            matches!(result, Ok(Ok(())))
        }
    };
    if !sent {
        capture_stop.stop();
        let _ = events_tx.send(SpeechEvent::Failed {
            message: "语音连接已失效，请重新按 F8 开始录音。".into(),
        });
        return;
    }

    let _ = events_tx.send(SpeechEvent::Ready);

    // 4. 双向异步循环：推送 16kHz PCM 块 & 解析识别/翻译结果
    let translation_mode = cfg.enable_translation;
    let target_lang = cfg.target_language.clone();
    let filter_particles = cfg.filter_modal_particles;

    loop {
        tokio::select! {
            _ = &mut stop_rx => {
                // 先停采集，发送 0 字节结束帧并优雅关闭连接
                capture_stop.stop();
                let eof_frame = format_usp_audio_message(&request_id, "audio/pcm", &[]);
                let _ = tokio::time::timeout(
                    Duration::from_millis(250),
                    ws_sink.send(Message::Binary(eof_frame.into())),
                ).await;
                let _ = tokio::time::timeout(Duration::from_millis(150), ws_sink.close()).await;
                return;
            }
            maybe_pcm = pcm_rx.recv() => {
                match maybe_pcm {
                    Some(chunk) => {
                        let audio_msg = format_usp_audio_message(&request_id, "audio/pcm", &chunk);
                        let sent = tokio::select! {
                            biased;
                            _ = &mut stop_rx => {
                                capture_stop.stop();
                                return;
                            }
                            result = tokio::time::timeout(START_TIMEOUT, ws_sink.send(Message::Binary(audio_msg.into()))) => {
                                matches!(result, Ok(Ok(())))
                            }
                        };
                        if !sent {
                            capture_stop.stop();
                            let _ = events_tx.send(SpeechEvent::Failed {
                                message: "语音识别连接中断，请检查网络后重试。".into(),
                            });
                            return;
                        }
                    }
                    None => {
                        capture_stop.stop();
                        let _ = events_tx.send(SpeechEvent::Failed {
                            message: "麦克风输入流已断开，请检查音频设备后重新录音。".into(),
                        });
                        return;
                    }
                }
            }
            maybe_msg = ws_stream.next() => {
                match maybe_msg {
                    Some(Ok(Message::Text(raw_txt))) => {
                        if let Some((path, body)) = parse_usp_incoming_text(&raw_txt) {
                            match interpret_usp_message(&path, &body, translation_mode, &target_lang) {
                                ParsedUspEvent::Recognition { mut text, is_final } => {
                                    if is_final && filter_particles {
                                        text = filter_modal_particles(&text);
                                    }
                                    let _ = events_tx.send(SpeechEvent::SourceRecognized {
                                        text: text.clone(),
                                        is_final,
                                    });
                                    let _ = events_tx.send(SpeechEvent::OutputText { text, is_final });
                                }
                                ParsedUspEvent::Translation { source_text, translated_text, is_final } => {
                                    let _ = events_tx.send(SpeechEvent::SourceRecognized {
                                        text: source_text.clone(),
                                        is_final,
                                    });
                                    if is_final && !source_text.trim().is_empty() {
                                        match translated_text.map(|t| {
                                            if filter_particles { filter_modal_particles(&t) } else { t }
                                        }) {
                                            Some(t) if !t.trim().is_empty() => {
                                                let _ = events_tx.send(SpeechEvent::OutputText {
                                                    text: t,
                                                    is_final: true,
                                                });
                                            }
                                            _ => {
                                                // 严格对齐现有契约：翻译失败或空译文绝不悄悄回退为输入原文
                                                capture_stop.stop();
                                                let _ = events_tx.send(SpeechEvent::Failed {
                                                    message: "微软语音翻译未返回有效译文，本次输入已停止；不会改为输入原文。".into(),
                                                });
                                                return;
                                            }
                                        }
                                    }
                                }
                                ParsedUspEvent::TurnStarted | ParsedUspEvent::TurnEnded | ParsedUspEvent::Ignored => {}
                            }
                        }
                    }
                    Some(Ok(Message::Close(_))) | None => {
                        capture_stop.stop();
                        let _ = events_tx.send(SpeechEvent::Failed {
                            message: "云端语音会话已结束，请按 F8 重新开始录音。".into(),
                        });
                        return;
                    }
                    Some(Ok(_)) => {}
                    Some(Err(_)) => {
                        capture_stop.stop();
                        let _ = events_tx.send(SpeechEvent::Failed {
                            message: "语音识别网络通信中断，请检查网络后重试。".into(),
                        });
                        return;
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::audio::ActiveCaptureStop;
    use tokio::net::TcpListener;
    use tokio_tungstenite::accept_async;
    use voice_ime_config::parse_speech_endpoint;

    struct DummyStop;
    impl ActiveCaptureStop for DummyStop {
        fn stop(&mut self) {}
    }

    struct MockMicProvider;
    impl AudioCaptureProvider for MockMicProvider {
        fn start_capture(
            &self,
            _device_id: &str,
        ) -> Result<(mpsc::Receiver<Vec<u8>>, Box<dyn ActiveCaptureStop>), String> {
            let (tx, rx) = mpsc::channel(8);
            tokio::runtime::Handle::current().spawn(async move {
                let _ = tx.send(vec![0u8; 640]).await;
                tokio::time::sleep(Duration::from_secs(5)).await;
            });
            Ok((rx, Box::new(DummyStop)))
        }
    }

    fn loopback_config(addr: std::net::SocketAddr) -> RuntimeSpeechSessionConfig {
        let endpoint = parse_speech_endpoint(&format!("http://{addr}")).unwrap();
        RuntimeSpeechSessionConfig {
            subscription_id: "sub1".into(),
            subscription_name: "Test".into(),
            subscription_key: "0123456789abcdef0123456789abcdef".into(),
            endpoint,
            microphone_device_id: String::new(),
            recognition_language: "zh-CN".into(),
            enable_translation: false,
            target_language: "en-US".into(),
            end_silence_ms: 800,
            enable_prewarm: true,
            filter_modal_particles: false,
        }
    }

    #[tokio::test]
    async fn stopping_during_websocket_handshake_releases_before_opening_microphone() {
        struct MustNotOpen(std::sync::atomic::AtomicBool);
        impl AudioCaptureProvider for MustNotOpen {
            fn start_capture(&self, _: &str) -> Result<(mpsc::Receiver<Vec<u8>>, Box<dyn ActiveCaptureStop>), String> {
                self.0.store(true, std::sync::atomic::Ordering::SeqCst);
                Err("不应开麦".into())
            }
        }
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let audio = Arc::new(MustNotOpen(false.into()));
        let factory = UspSpeechEngineFactory::new(StsTokenCache::new(), audio.clone(), false);
        let (ev_tx, mut ev_rx) = mpsc::unbounded_channel();
        let handle = factory.start_session(loopback_config(listener.local_addr().unwrap()), None, ev_tx);
        let (_socket, _) = tokio::time::timeout(Duration::from_secs(2), listener.accept()).await.unwrap().unwrap();
        tokio::time::timeout(Duration::from_secs(1), handle.stop_and_wait_released()).await.unwrap();
        assert_eq!(ev_rx.recv().await, None);
        assert!(!audio.0.load(std::sync::atomic::Ordering::SeqCst));
    }

    #[tokio::test]
    async fn end_to_end_usp_websocket_stt_loopback() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let cfg = loopback_config(listener.local_addr().unwrap());
        tokio::spawn(async move {
            let (tcp, _) = listener.accept().await.unwrap();
            let mut ws = accept_async(tcp).await.unwrap();
            // 接收 speech.config, speech.context, wav header, first pcm chunk
            for _ in 0..4 {
                let _ = ws.next().await;
            }
            let hyp = "Path: speech.hypothesis\r\nX-RequestId: 1\r\n\r\n{\"Text\":\"你好\"}";
            ws.send(Message::Text(hyp.into())).await.unwrap();
            let fin = "Path: speech.phrase\r\nX-RequestId: 1\r\n\r\n{\"RecognitionStatus\":\"Success\",\"DisplayText\":\"你好世界。\"}";
            ws.send(Message::Text(fin.into())).await.unwrap();
            tokio::time::sleep(Duration::from_millis(300)).await;
        });

        let factory = UspSpeechEngineFactory::new(
            StsTokenCache::new(),
            Arc::new(MockMicProvider),
            false,
        );
        let (ev_tx, mut ev_rx) = mpsc::unbounded_channel();
        let handle = factory.start_session(cfg, None, ev_tx);

        let mut outputs = Vec::new();
        while let Ok(Some(ev)) = tokio::time::timeout(Duration::from_secs(3), ev_rx.recv()).await {
            if let SpeechEvent::OutputText { text, is_final } = ev {
                outputs.push((text, is_final));
                if is_final {
                    break;
                }
            }
        }
        handle.stop_and_wait_released().await;

        assert_eq!(
            outputs,
            vec![("你好".to_string(), false), ("你好世界。".to_string(), true)]
        );
    }
}
