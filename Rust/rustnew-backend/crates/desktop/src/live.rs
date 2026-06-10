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
//! 在 Windows / macOS / Linux 通用。系统声卡回环（捕获系统音频）目前仅
//! Windows（WASAPI）支持，留待后续通过 push stream 实现，故此处抽象出
//! [`AudioSource`]，当前仅落地 [`AudioSource::Microphone`]。

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{channel, Receiver, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};

use serde::Serialize;
use speech_sdk::audio::AudioConfig;
use speech_sdk::speech::{SpeechTranslationConfig, TranslationRecognizer};
use tauri::{AppHandle, Emitter};
use tfp_core::storage::{self, TranslationRecord};

/// 音频来源（为跨平台预留）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AudioSource {
    /// 默认麦克风（全平台）。
    Microphone,
    /// 系统声卡回环（仅 Windows，后续实现）。
    #[allow(dead_code)]
    SystemLoopback,
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
    let mut recognizer: Option<TranslationRecognizer> = None;
    while let Ok(cmd) = rx.recv() {
        match cmd {
            LiveCommand::Start(params) => {
                stop_recognition(&app, &running, &mut recognizer);
                if let Err(e) = start_recognition(&app, &db_tx, &running, &mut recognizer, *params) {
                    emit_status(&app, "error", &e);
                }
            }
            LiveCommand::Stop => stop_recognition(&app, &running, &mut recognizer),
            LiveCommand::Shutdown => {
                stop_recognition(&app, &running, &mut recognizer);
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
    slot: &mut Option<TranslationRecognizer>,
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

    // 2. 音频（默认麦克风，跨平台）
    let audio = AudioConfig::from_default_microphone_input()
        .map_err(|e| format!("打开麦克风失败：{e:?}"))?;

    // 3. 识别器
    let mut recognizer = TranslationRecognizer::from_config(&config, &audio)
        .map_err(|e| format!("创建识别器失败：{e:?}"))?;

    let filter = params.filter_modal_particles;

    // 中间结果
    {
        let app = app.clone();
        let (src, tgt) = (source_lang.clone(), target_lang.clone());
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
                    source: "mic".into(),
                },
            );
        });
    }

    // 最终结果（推前端 + 落库）
    {
        let app = app.clone();
        let db_tx = db_tx.clone();
        let (src, tgt) = (source_lang.clone(), target_lang.clone());
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
                    source: "mic".into(),
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

    running.store(true, Ordering::SeqCst);
    emit_status(app, "started", &format!("{source_lang} → {target_lang}"));
    *slot = Some(recognizer);
    Ok(())
}

/// 停止并释放 recognizer。
fn stop_recognition(
    app: &AppHandle,
    running: &Arc<AtomicBool>,
    slot: &mut Option<TranslationRecognizer>,
) {
    if let Some(mut recognizer) = slot.take() {
        if let Err(e) = pollster::block_on(recognizer.stop_continuous_recognition_async()) {
            tracing::warn!(error = ?e, "停止连续识别失败");
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
fn build_config(p: &StartParams) -> speech_sdk::error::Result<SpeechTranslationConfig> {
    if p.is_china {
        let host = format!("wss://{}.stt.speech.azure.cn", p.region);
        SpeechTranslationConfig::from_host_with_subscription(&host, &p.key)
    } else if !p.endpoint.trim().is_empty() {
        SpeechTranslationConfig::from_endpoint_with_subscription(&p.endpoint, &p.key)
    } else {
        SpeechTranslationConfig::from_subscription(&p.key, &p.region)
    }
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
