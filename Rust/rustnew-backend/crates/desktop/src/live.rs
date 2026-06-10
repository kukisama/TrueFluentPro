//! 实时翻译引擎。
//!
//! Microsoft Speech SDK 的 `SpeechTranslationConfig` / `TranslationRecognizer`
//! 都不是 `Send`，因此所有 SDK 句柄都留在一个专属的引擎线程上，外部仅通过
//! channel 投递命令。识别回调运行在 SDK 内部线程，回调里：
//! - 通过 `AppHandle::emit` 把中间/最终结果推给前端；
//! - 把最终结果通过另一条 channel 投递给独立的「DB 写入线程」落库
//!   （`rusqlite::Connection` 非 Send，不能跨线程共享，故单独起线程持有）。
//!
//! 跨平台：麦克风采集走 `AudioConfig::from_default_microphone_input()`，
//! 在 Windows / macOS / Linux 通用。系统声卡回环（捕获系统音频）通过
//! [`crate::audio_loopback`] 抽象出的 `LoopbackCapture` 落地：Windows 走
//! WASAPI render-loopback，把 16k/16bit/单声道 PCM 写入 push stream 供识别。

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{channel, Receiver, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};

use serde::Serialize;
use speech_sdk::audio::{AudioConfig, AudioStreamFormat, PushAudioInputStream};
use speech_sdk::speech::{SpeechTranslationConfig, TranslationRecognizer};
use tauri::{AppHandle, Emitter};
use tfp_core::storage::{self, TranslationRecord};

use crate::audio_loopback::{
    create_loopback_capture, LoopbackFormat, LoopbackHandle, LOOPBACK_BITS, LOOPBACK_CHANNELS,
    LOOPBACK_SAMPLE_RATE,
};

/// 一次实时翻译会话：识别器 + （回环模式下的）采集句柄。
///
/// 停止时必须**先停识别器、再停采集**，避免采集线程持有的 push stream
/// 在识别器仍在读取时被释放。
struct LiveSession {
    recognizer: TranslationRecognizer,
    /// 回环采集句柄（仅回环模式存在）。
    loopback: Option<Box<dyn LoopbackHandle>>,
}


/// 启动一次实时翻译所需的参数。
#[derive(Debug, Clone)]
pub struct StartParams {
    pub key: String,
    pub region: String,
    pub endpoint: String,
    pub is_china: bool,
    /// 源语言，`auto` 表示自动（当前用启发式映射，见 [`resolve_source_lang`]）。
    pub source_lang: String,
    /// 目标语言（如 `zh-Hans`）。
    pub target_lang: String,
    pub filter_modal_particles: bool,
    /// 选中的输入设备「友好名」（空串=默认麦克风）。仅在非回环模式下生效。
    pub input_device_name: String,
    /// 是否使用系统回环作为识别音源（当前未实现回环采集，置 true 时回退默认麦克风并提示）。
    pub use_loopback: bool,
}

/// UI → 引擎线程命令。
enum LiveCommand {
    Start(Box<StartParams>),
    Stop,
    Shutdown,
}

/// 引擎 → DB 写入线程消息。
enum DbMsg {
    Insert(Box<TranslationRecord>),
    Shutdown,
}

/// 推送给前端的一条翻译（中间或最终）。
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct LivePayload {
    id: String,
    original: String,
    translated: String,
    source_lang: String,
    target_lang: String,
    is_partial: bool,
    created_at: String,
    /// 音频来源标识：`mic` / `loopback`。
    source: String,
}

/// 推送给前端的状态变化。
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct StatusPayload {
    /// `started` / `stopped` / `error` / `sessionStarted` / `sessionStopped`
    state: String,
    message: String,
}

/// 实时翻译器句柄（由 Tauri 托管）。
pub struct LiveTranslator {
    cmd_tx: Sender<LiveCommand>,
    running: Arc<AtomicBool>,
    engine: Option<JoinHandle<()>>,
    db: Option<JoinHandle<()>>,
}

impl LiveTranslator {
    /// 启动引擎线程与 DB 写入线程。
    pub fn spawn(app: AppHandle) -> Self {
        let (cmd_tx, cmd_rx) = channel::<LiveCommand>();
        let (db_tx, db_rx) = channel::<DbMsg>();
        let running = Arc::new(AtomicBool::new(false));

        let db_join = thread::Builder::new()
            .name("tfp-live-db".into())
            .spawn(move || db_loop(db_rx))
            .expect("启动 DB 写入线程失败");

        let running_engine = running.clone();
        let db_tx_for_shutdown = db_tx.clone();
        let engine_join = thread::Builder::new()
            .name("tfp-live-engine".into())
            .spawn(move || {
                engine_loop(cmd_rx, app, db_tx, running_engine);
                // 引擎退出后通知 DB 线程收尾
                let _ = db_tx_for_shutdown.send(DbMsg::Shutdown);
            })
            .expect("启动实时翻译引擎线程失败");

        Self {
            cmd_tx,
            running,
            engine: Some(engine_join),
            db: Some(db_join),
        }
    }

    pub fn start(&self, params: StartParams) -> Result<(), String> {
        self.cmd_tx
            .send(LiveCommand::Start(Box::new(params)))
            .map_err(|e| e.to_string())
    }

    pub fn stop(&self) -> Result<(), String> {
        self.cmd_tx.send(LiveCommand::Stop).map_err(|e| e.to_string())
    }

    pub fn is_running(&self) -> bool {
        self.running.load(Ordering::SeqCst)
    }
}

impl Drop for LiveTranslator {
    fn drop(&mut self) {
        let _ = self.cmd_tx.send(LiveCommand::Shutdown);
        if let Some(j) = self.engine.take() {
            let _ = j.join();
        }
        if let Some(j) = self.db.take() {
            let _ = j.join();
        }
    }
}

/// DB 写入线程：独占一个 SQLite 连接，串行写入翻译历史。
fn db_loop(rx: Receiver<DbMsg>) {
    let conn = match storage::open_connection() {
        Ok(c) => Some(c),
        Err(e) => {
            tracing::error!(error = %e, "翻译历史数据库打开失败，最终结果将不会落库");
            None
        }
    };
    while let Ok(msg) = rx.recv() {
        match msg {
            DbMsg::Insert(rec) => {
                if let Some(c) = &conn {
                    if let Err(e) = storage::insert_translation(c, &rec) {
                        tracing::warn!(error = %e, "写入翻译历史失败");
                    }
                }
            }
            DbMsg::Shutdown => break,
        }
    }
}

/// 引擎线程主循环：持有 recognizer，串行处理命令。
fn engine_loop(
    rx: Receiver<LiveCommand>,
    app: AppHandle,
    db_tx: Sender<DbMsg>,
    running: Arc<AtomicBool>,
) {
    let mut session: Option<LiveSession> = None;
    while let Ok(cmd) = rx.recv() {
        match cmd {
            LiveCommand::Start(params) => {
                stop_recognition(&app, &running, &mut session);
                if let Err(e) = start_recognition(&app, &db_tx, &running, &mut session, *params) {
                    emit_status(&app, "error", &e);
                }
            }
            LiveCommand::Stop => stop_recognition(&app, &running, &mut session),
            LiveCommand::Shutdown => {
                stop_recognition(&app, &running, &mut session);
                break;
            }
        }
    }
}

/// 构建配置 + recognizer 并启动连续识别。
fn start_recognition(
    app: &AppHandle,
    db_tx: &Sender<DbMsg>,
    running: &Arc<AtomicBool>,
    slot: &mut Option<LiveSession>,
    params: StartParams,
) -> Result<(), String> {
    let source_lang = resolve_source_lang(&params.source_lang, &params.target_lang);
    let target_lang = params.target_lang.clone();

    // 1. 构建配置（中国区用 host，其余用 region / endpoint）
    let mut config = build_config(&params).map_err(|e| format!("创建语音配置失败：{e}"))?;
    config
        .set_speech_recognition_language(&source_lang)
        .map_err(|e| format!("设置源语言失败：{e:?}"))?;
    config
        .add_target_language(&target_lang)
        .map_err(|e| format!("添加目标语言失败：{e:?}"))?;

    // 2. 音频音源选择
    //    回环：用 WASAPI render-loopback 把系统音频以 16k/16bit/单声道 PCM 写入
    //          push stream（采集句柄随会话保存，停止时先停识别再停采集）。
    //    非回环：input_device_name 为空 => 默认麦克风；非空 => 按友好名打开指定麦克风。
    let source_label = if params.use_loopback { "loopback" } else { "mic" };
    // 回环模式下，采集器与 push stream 先暂存，待识别启动后再开始喂数据，避免积压。
    let mut pending_loopback: Option<(Box<dyn crate::audio_loopback::LoopbackCapture>, PushAudioInputStream)> = None;
    let audio = if params.use_loopback {
        let capture = create_loopback_capture()
            .ok_or_else(|| "当前平台不支持系统回环采集".to_string())?;
        let format = AudioStreamFormat::from_pcm(
            LOOPBACK_SAMPLE_RATE,
            LOOPBACK_BITS as u8,
            LOOPBACK_CHANNELS as u8,
        )
        .map_err(|e| format!("创建回环音频格式失败：{e:?}"))?;
        let stream = PushAudioInputStream::create(&format)
            .map_err(|e| format!("创建回环推流失败：{e:?}"))?;
        let audio = AudioConfig::from_stream(&stream)
            .map_err(|e| format!("绑定回环推流失败：{e:?}"))?;
        pending_loopback = Some((capture, stream));
        audio
    } else if params.input_device_name.trim().is_empty() {
        AudioConfig::from_default_microphone_input()
            .map_err(|e| format!("打开麦克风失败：{e:?}"))?
    } else {
        AudioConfig::from_microphone_input(&params.input_device_name)
            .map_err(|e| format!("打开指定麦克风「{}」失败：{e:?}", params.input_device_name))?
    };

    // 3. 识别器
    let mut recognizer = TranslationRecognizer::from_config(&config, &audio)
        .map_err(|e| format!("创建识别器失败：{e:?}"))?;

    let filter = params.filter_modal_particles;

    // 中间结果
    {
        let app = app.clone();
        let (src, tgt) = (source_lang.clone(), target_lang.clone());
        let source = source_label.to_string();
        let _ = recognizer.set_recognizing_cb(move |ev| {
            let original = ev.result.base.text.clone();
            if original.trim().is_empty() {
                return;
            }
            let translated = pick_translation(&ev.result.translations, &tgt);
            let _ = app.emit(
                "live:partial",
                LivePayload {
                    id: String::new(),
                    original,
                    translated,
                    source_lang: src.clone(),
                    target_lang: tgt.clone(),
                    is_partial: true,
                    created_at: now_rfc3339(),
                    source: source.clone(),
                },
            );
        });
    }

    // 最终结果（推前端 + 落库）
    {
        let app = app.clone();
        let db_tx = db_tx.clone();
        let (src, tgt) = (source_lang.clone(), target_lang.clone());
        let source = source_label.to_string();
        let _ = recognizer.set_recognized_cb(move |ev| {
            let original = ev.result.base.text.clone();
            if original.trim().is_empty() {
                return;
            }
            let mut translated = pick_translation(&ev.result.translations, &tgt);
            if filter {
                translated = filter_modal_particles(&translated);
            }
            let rec = TranslationRecord::now(
                original.clone(),
                translated.clone(),
                Some(src.clone()),
                Some(tgt.clone()),
            );
            let _ = app.emit(
                "live:final",
                LivePayload {
                    id: rec.id.clone(),
                    original,
                    translated,
                    source_lang: src.clone(),
                    target_lang: tgt.clone(),
                    is_partial: false,
                    created_at: rec.created_at.clone(),
                    source: source.clone(),
                },
            );
            let _ = db_tx.send(DbMsg::Insert(Box::new(rec)));
        });
    }

    // 取消 / 错误
    {
        let app = app.clone();
        let _ = recognizer.set_canceled_cb(move |ev| {
            emit_status(
                &app,
                "error",
                &format!("识别取消：{:?} / {:?} {}", ev.reason, ev.error_code, ev.error_details),
            );
        });
    }

    // 会话事件
    {
        let app = app.clone();
        let _ = recognizer.set_session_started_cb(move |_| emit_status(&app, "sessionStarted", ""));
    }
    {
        let app = app.clone();
        let _ = recognizer.set_session_stopped_cb(move |_| emit_status(&app, "sessionStopped", ""));
    }

    // 4. 启动连续识别（在本引擎线程阻塞等待启动完成）
    pollster::block_on(recognizer.start_continuous_recognition_async())
        .map_err(|e| format!("启动连续识别失败：{e:?}"))?;

    // 5. 回环模式：识别已就绪后再开始采集，把 PCM 写入 push stream。
    //    push stream 为 Send，直接 move 进采集回调（单一所有者，运行在采集线程）。
    let loopback_handle = if let Some((capture, stream)) = pending_loopback {
        match capture.start(
            LoopbackFormat::recognition(),
            Box::new(move |bytes| {
                let _ = stream.write(bytes);
            }),
        ) {
            Ok(handle) => Some(handle),
            Err(e) => {
                // 采集启动失败：回滚已启动的识别器，避免空跑。
                let _ = pollster::block_on(recognizer.stop_continuous_recognition_async());
                return Err(format!("启动回环采集失败：{e}"));
            }
        }
    } else {
        None
    };

    running.store(true, Ordering::SeqCst);
    emit_status(app, "started", &format!("{source_lang} → {target_lang}"));
    *slot = Some(LiveSession {
        recognizer,
        loopback: loopback_handle,
    });
    Ok(())
}

/// 停止并释放会话：先停识别器，再停采集（释放 push stream）。
fn stop_recognition(
    app: &AppHandle,
    running: &Arc<AtomicBool>,
    slot: &mut Option<LiveSession>,
) {
    if let Some(mut session) = slot.take() {
        if let Err(e) = pollster::block_on(session.recognizer.stop_continuous_recognition_async()) {
            tracing::warn!(error = ?e, "停止连续识别失败");
        }
        // 识别器已停，再停采集线程，确保 push stream 不会在识别期间被释放。
        if let Some(handle) = session.loopback.take() {
            handle.stop();
        }
        running.store(false, Ordering::SeqCst);
        emit_status(app, "stopped", "");
    }
}

/// 从翻译结果集中挑出目标语言文本；找不到则返回任意一条。
fn pick_translation(translations: &std::collections::HashMap<String, String>, target: &str) -> String {
    if let Some(t) = translations.get(target) {
        return t.clone();
    }
    // 大小写/区域变体兜底
    for (lang, text) in translations {
        if lang.eq_ignore_ascii_case(target) {
            return text.clone();
        }
    }
    translations.values().next().cloned().unwrap_or_default()
}

/// `auto` 源语言的启发式映射（TranslationRecognizer 暂不支持连续语种检测，
/// 后续可接入 AutoDetectSourceLanguageConfig 做真正的 LID）。
fn resolve_source_lang(source: &str, target: &str) -> String {
    if !source.eq_ignore_ascii_case("auto") && !source.is_empty() {
        return source.to_string();
    }
    if target.to_ascii_lowercase().starts_with("zh") {
        "en-US".to_string()
    } else {
        "zh-CN".to_string()
    }
}

/// 构建 `SpeechTranslationConfig`。
///
/// 对齐 C# `SpeechTranslationService.CreateSpeechConfigAsync`：
/// - 中国区：用 `wss://<region>.stt.speech.azure.cn` host；
/// - 非中国区（API Key 模式）：**优先用区域** `from_subscription(key, region)`，
///   终结点字段只作区域来源；管理/认知服务域名（`*.api.cognitive.microsoft.com`、
///   `*.cognitiveservices.azure.com`）不是语音识别端点，不能直接喂 `from_endpoint`；
/// - 仅当填的是**真正的自定义语音端点**（wss/stt/tts.speech 等）才用 `from_endpoint`。
fn build_config(p: &StartParams) -> speech_sdk::error::Result<SpeechTranslationConfig> {
    if p.is_china {
        let host = format!("wss://{}.stt.speech.azure.cn", p.region);
        SpeechTranslationConfig::from_host_with_subscription(&host, &p.key)
    } else if is_custom_speech_endpoint(&p.endpoint) {
        SpeechTranslationConfig::from_endpoint_with_subscription(&p.endpoint, &p.key)
    } else if !p.region.trim().is_empty() {
        SpeechTranslationConfig::from_subscription(&p.key, &p.region)
    } else if !p.endpoint.trim().is_empty() {
        // 无区域可用时的兜底：尽量用终结点（可能是私有/自定义端点）
        SpeechTranslationConfig::from_endpoint_with_subscription(&p.endpoint, &p.key)
    } else {
        SpeechTranslationConfig::from_subscription(&p.key, &p.region)
    }
}

/// 判断是否为「真正的自定义语音端点」。
///
/// 只有 speech 专用端点（`wss://`、`*.stt.speech.*`、`*.tts.speech.*`、
/// `*.speech.microsoft.com`）才直接喂给 SDK 的 `from_endpoint`；
/// 认知服务管理域名（`*.api.cognitive.microsoft.com`、`*.cognitiveservices.azure.com`）
/// 不是识别端点，应改走 `from_subscription(key, region)`。
fn is_custom_speech_endpoint(endpoint: &str) -> bool {
    let e = endpoint.trim().to_ascii_lowercase();
    if e.is_empty() {
        return false;
    }
    if e.contains("api.cognitive.microsoft.com") || e.contains("cognitiveservices.azure.com") {
        return false;
    }
    e.starts_with("wss://")
        || e.starts_with("ws://")
        || e.contains(".stt.speech.")
        || e.contains(".tts.speech.")
        || e.contains("speech.microsoft.com")
}

/// 句末语气词过滤（best-effort，对应 C# FilterModalParticles 的轻量版）。
fn filter_modal_particles(s: &str) -> String {
    const PARTICLES: &[char] = &['呢', '吧', '啊', '呀', '啦', '嘛', '哦', '喔', '噢', '嗯', '咯', '唉'];
    let mut chars: Vec<char> = s.trim_end().chars().collect();
    while chars.len() > 2 {
        match chars.last() {
            Some(&last) if PARTICLES.contains(&last) => {
                chars.pop();
            }
            Some(&last) if matches!(last, '。' | '！' | '？' | '，' | '、' | '.' | '!' | '?') => {
                // 跳过结尾标点，继续看前一个字符是否为语气词
                if chars.len() >= 2 && PARTICLES.contains(&chars[chars.len() - 2]) {
                    chars.pop(); // 去标点
                    chars.pop(); // 去语气词
                } else {
                    break;
                }
            }
            _ => break,
        }
    }
    chars.into_iter().collect()
}

fn now_rfc3339() -> String {
    chrono::Utc::now().to_rfc3339()
}

fn emit_status(app: &AppHandle, state: &str, message: &str) {
    let _ = app.emit(
        "live:status",
        StatusPayload {
            state: state.to_string(),
            message: message.to_string(),
        },
    );
}

#[cfg(test)]
mod tests {
    use super::is_custom_speech_endpoint;

    #[test]
    fn management_endpoint_is_not_custom_speech() {
        // 截图里用户填的就是这个管理域名 → 不应直接喂 from_endpoint
        assert!(!is_custom_speech_endpoint(
            "https://southeastasia.api.cognitive.microsoft.com"
        ));
        assert!(!is_custom_speech_endpoint(
            "https://myres.cognitiveservices.azure.com"
        ));
    }

    #[test]
    fn empty_endpoint_is_not_custom_speech() {
        assert!(!is_custom_speech_endpoint(""));
        assert!(!is_custom_speech_endpoint("   "));
    }

    #[test]
    fn real_speech_endpoint_is_custom() {
        assert!(is_custom_speech_endpoint(
            "wss://southeastasia.stt.speech.microsoft.com/speech/universal/v2"
        ));
        assert!(is_custom_speech_endpoint(
            "https://southeastasia.tts.speech.microsoft.com"
        ));
    }
}
