//! worker — 后台线程，持有并操作所有 Speech SDK 对象。
//!
//! 所有 SDK 句柄都留在这个线程，UI 线程仅通过命令通道交互，
//! 因此无需要求 SDK 类型实现 Send。

use std::sync::mpsc::{Receiver, Sender};
use std::thread::{self, JoinHandle};

use speech_sdk::audio::{AudioConfig, AudioStreamFormat, PushAudioInputStream};
use speech_sdk::speech::{
    AutoDetectSourceLanguageConfig, Connection, GradingSystem, Granularity, PhraseListGrammar,
    PronunciationAssessmentConfig, PronunciationAssessmentResult, SpeechConfig, SpeechRecognizer,
    SpeechSynthesizer, SpeechTranslationConfig, TranslationRecognizer,
};

/// 一行日志（worker → UI）。
pub struct LogLine(pub String);

/// UI → worker 命令。
pub enum Command {
    CreateConfig {
        key: String,
        region: String,
        /// 中国版使用的 host（如 wss://chinanorth3.stt.speech.azure.cn）。
        host: Option<String>,
        source_lang: String,
        target_langs: Vec<String>,
        is_china: bool,
    },
    RecognizeOnceMic,
    RecognizeOnceWav(String),
    StartContinuous,
    StopContinuous,
    /// TTS：把文字合成到 WAV 文件（全自动，无需麦克风）。
    SynthesizeToWav { text: String, out_path: String },
    /// TTS：把 SSML 合成到 WAV 文件。
    SynthesizeSsmlToWav { ssml: String, out_path: String },
    /// TTS：异步合成文字到 WAV（speak_text_async）。
    SynthesizeTextAsync { text: String, out_path: String },
    /// TTS：合成并打印事件（started/synthesizing/word_boundary/viseme/bookmark/completed）。
    SynthesizeWithEvents { text: String, out_path: String },
    /// TTS：列出可用嗓音（locale 空串=全部）。
    ListVoices { locale: String },
    /// 纯语音转文字（不翻译），识别一个 WAV 文件。
    RecognizeWavStt(String),
    /// 纯 STT 连续识别一个 WAV 文件（回调收集所有分段，验证连续模式）。
    RecognizeWavSttContinuous(String),
    /// 纯 STT 推流识别：把 WAV 的 PCM 分块写入 push stream 后识别。
    RecognizeWavSttPushStream(String),
    /// 自动语言检测：在候选语言间识别一个 WAV 文件，输出检测到的语言。
    RecognizeWavAutoDetect { path: String, candidates: Vec<String> },
    /// 短语列表语法：向识别器添加一组短语后识别 WAV。
    RecognizeWavWithPhrases { path: String, phrases: Vec<String> },
    /// Connection 对象：显式 open/close 连接并监听 connected/disconnected 事件，识别 WAV。
    RecognizeWavWithConnection(String),
    /// 发音评估：以参考文本评估 WAV 发音，输出准确/流利/完整/总分。
    AssessPronunciation { path: String, reference_text: String },
    /// TTS：合成到默认扬声器（会发声，仅 GUI 手动）。
    SynthesizeToSpeaker { text: String },
    /// 闭环自测：TTS 合成固定文字到临时 WAV，再用 STT 识别回来。
    SelfTestClosedLoop,
}

/// 创建配置时保存的凭据，供 TTS/STT 复用以构建 SpeechConfig。
#[derive(Clone)]
struct Creds {
    key: String,
    region: String,
    source_lang: String,
    is_china: bool,
}

/// worker 线程句柄，Drop 时通知线程退出。
pub struct WorkerHandle {
    join: Option<JoinHandle<()>>,
}

impl WorkerHandle {
    pub fn spawn(cmd_rx: Receiver<Command>, log_tx: Sender<LogLine>) -> Self {
        let join = thread::spawn(move || {
            let mut worker = Worker::new(log_tx);
            while let Ok(cmd) = cmd_rx.recv() {
                worker.handle(cmd);
            }
        });
        Self { join: Some(join) }
    }
}

impl Drop for WorkerHandle {
    fn drop(&mut self) {
        if let Some(j) = self.join.take() {
            let _ = j.join();
        }
    }
}

/// 无界面自测：在当前线程依次执行给定命令，所有日志直接打印到 stdout。
/// 用于 `--selftest`，便于在无人值守下验证 TTS / 纯 STT / 闭环自测。
pub fn run_headless(commands: Vec<Command>) {
    let (log_tx, log_rx) = std::sync::mpsc::channel::<LogLine>();
    let mut worker = Worker::new(log_tx);
    for cmd in commands {
        worker.handle(cmd);
        while let Ok(LogLine(line)) = log_rx.try_recv() {
            println!("{line}");
        }
    }
    // 兜底排空剩余日志
    while let Ok(LogLine(line)) = log_rx.try_recv() {
        println!("{line}");
    }
}

struct Worker {
    log_tx: Sender<LogLine>,
    config: Option<SpeechTranslationConfig>,
    continuous: Option<TranslationRecognizer>,
    creds: Option<Creds>,
}

impl Worker {
    fn new(log_tx: Sender<LogLine>) -> Self {
        Self {
            log_tx,
            config: None,
            continuous: None,
            creds: None,
        }
    }

    fn log(&self, s: impl Into<String>) {
        let _ = self.log_tx.send(LogLine(s.into()));
    }

    fn handle(&mut self, cmd: Command) {
        match cmd {
            Command::CreateConfig {
                key,
                region,
                host,
                source_lang,
                target_langs,
                is_china,
            } => self.create_config(key, region, host, source_lang, target_langs, is_china),
            Command::RecognizeOnceMic => self.recognize_once(None),
            Command::RecognizeOnceWav(path) => self.recognize_once(Some(path)),
            Command::StartContinuous => self.start_continuous(),
            Command::StopContinuous => self.stop_continuous(),
            Command::SynthesizeToWav { text, out_path } => {
                self.synthesize_to_wav(&text, &out_path)
            }
            Command::SynthesizeSsmlToWav { ssml, out_path } => {
                self.synthesize_ssml_to_wav(&ssml, &out_path)
            }
            Command::SynthesizeTextAsync { text, out_path } => {
                self.synthesize_text_async(&text, &out_path)
            }
            Command::SynthesizeWithEvents { text, out_path } => {
                self.synthesize_with_events(&text, &out_path)
            }
            Command::ListVoices { locale } => self.list_voices(&locale),
            Command::RecognizeWavStt(path) => self.recognize_wav_stt(&path),
            Command::RecognizeWavSttContinuous(path) => self.recognize_wav_stt_continuous(&path),
            Command::RecognizeWavSttPushStream(path) => self.recognize_wav_stt_push_stream(&path),
            Command::RecognizeWavAutoDetect { path, candidates } => {
                self.recognize_wav_auto_detect(&path, &candidates)
            }
            Command::RecognizeWavWithPhrases { path, phrases } => {
                self.recognize_wav_with_phrases(&path, &phrases)
            }
            Command::RecognizeWavWithConnection(path) => {
                self.recognize_wav_with_connection(&path)
            }
            Command::AssessPronunciation { path, reference_text } => {
                self.assess_pronunciation(&path, &reference_text)
            }
            Command::SynthesizeToSpeaker { text } => self.synthesize_to_speaker(&text),
            Command::SelfTestClosedLoop => self.self_test_closed_loop(),
        }
    }

    /// 用保存的凭据构建一个基础 SpeechConfig（供 TTS/STT 使用）。
    ///
    /// 中国 21Vianet 的 STT 与 TTS 是不同端点：
    /// STT 在 `wss://{region}.stt.speech.azure.cn`，TTS 在 `wss://{region}.tts.speech.azure.cn`，
    /// 因此按 `for_synthesis` 选择正确的 host。
    fn build_speech_config(&self, for_synthesis: bool) -> Result<SpeechConfig, String> {
        let creds = self.creds.as_ref().ok_or("请先创建配置".to_string())?;
        if creds.is_china {
            let kind = if for_synthesis { "tts" } else { "stt" };
            let host = format!("wss://{}.{}.speech.azure.cn", creds.region, kind);
            SpeechConfig::from_host_with_subscription(&host, &creds.key)
                .map_err(|e| format!("{e:?}"))
        } else {
            SpeechConfig::from_subscription(&creds.key, &creds.region)
                .map_err(|e| format!("{e:?}"))
        }
    }

    /// 按源语言推导一个合理的合成语音名。
    fn default_voice(lang: &str) -> &'static str {
        if lang.starts_with("zh") {
            "zh-CN-XiaoxiaoNeural"
        } else if lang.starts_with("ja") {
            "ja-JP-NanamiNeural"
        } else {
            "en-US-AriaNeural"
        }
    }

    /// TTS：合成文字到 WAV 文件。
    fn synthesize_to_wav(&mut self, text: &str, out_path: &str) {
        let voice = {
            let lang = self
                .creds
                .as_ref()
                .map(|c| c.source_lang.clone())
                .unwrap_or_else(|| "zh-CN".into());
            Self::default_voice(&lang).to_string()
        };

        let mut config = match self.build_speech_config(true) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return;
            }
        };
        if let Err(e) = config.set_speech_synthesis_voice_name(&voice) {
            self.log(format!("[错误] 设置语音失败：{e:?}"));
            return;
        }

        let audio = match AudioConfig::from_wav_file_output(out_path) {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 创建 WAV 输出失败：{e:?}"));
                return;
            }
        };

        let synth = match SpeechSynthesizer::from_config(&config, &audio) {
            Ok(s) => s,
            Err(e) => {
                self.log(format!("[错误] 创建合成器失败：{e:?}"));
                return;
            }
        };

        self.log(format!("→ TTS 合成（语音 {voice}）：{text}"));
        match synth.speak_text(text) {
            Ok(r) => self.log(format!(
                "✓ 合成完成：{} 字节，约 {:.1} 秒，已写入 {}",
                r.audio_length,
                r.audio_duration_ticks as f64 / 1e7,
                out_path
            )),
            Err(e) => self.log(format!("[错误] 合成失败：{e:?}")),
        }
    }

    /// 构建带嗓音的合成器（config/audio 一并返回以保证生命周期）。
    fn make_synthesizer(
        &mut self,
        out_path: &str,
    ) -> Option<(SpeechConfig, AudioConfig, SpeechSynthesizer, String)> {
        let voice = {
            let lang = self
                .creds
                .as_ref()
                .map(|c| c.source_lang.clone())
                .unwrap_or_else(|| "zh-CN".into());
            Self::default_voice(&lang).to_string()
        };
        let mut config = match self.build_speech_config(true) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return None;
            }
        };
        if let Err(e) = config.set_speech_synthesis_voice_name(&voice) {
            self.log(format!("[错误] 设置语音失败：{e:?}"));
            return None;
        }
        let audio = match AudioConfig::from_wav_file_output(out_path) {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 创建 WAV 输出失败：{e:?}"));
                return None;
            }
        };
        let synth = match SpeechSynthesizer::from_config(&config, &audio) {
            Ok(s) => s,
            Err(e) => {
                self.log(format!("[错误] 创建合成器失败：{e:?}"));
                return None;
            }
        };
        Some((config, audio, synth, voice))
    }

    /// TTS：合成 SSML 到 WAV。
    fn synthesize_ssml_to_wav(&mut self, ssml: &str, out_path: &str) {
        let Some((_cfg, _audio, synth, voice)) = self.make_synthesizer(out_path) else {
            return;
        };
        self.log(format!("→ TTS 合成 SSML（默认语音 {voice}，SSML 内可覆盖）"));
        match synth.speak_ssml(ssml) {
            Ok(r) => self.log(format!(
                "✓ SSML 合成完成：{} 字节，约 {:.1} 秒，已写入 {}",
                r.audio_length,
                r.audio_duration_ticks as f64 / 1e7,
                out_path
            )),
            Err(e) => self.log(format!("[错误] SSML 合成失败：{e:?}")),
        }
    }

    /// TTS：异步合成文字到 WAV（speak_text_async）。
    fn synthesize_text_async(&mut self, text: &str, out_path: &str) {
        let Some((_cfg, _audio, synth, voice)) = self.make_synthesizer(out_path) else {
            return;
        };
        self.log(format!("→ TTS 异步合成（语音 {voice}）：{text}"));
        match pollster::block_on(synth.speak_text_async(text)) {
            Ok(r) => self.log(format!(
                "✓ 异步合成完成：{} 字节，约 {:.1} 秒，已写入 {}",
                r.audio_length,
                r.audio_duration_ticks as f64 / 1e7,
                out_path
            )),
            Err(e) => self.log(format!("[错误] 异步合成失败：{e:?}")),
        }
    }

    /// TTS：合成并打印事件（started/synthesizing/word_boundary/viseme/bookmark/completed）。
    fn synthesize_with_events(&mut self, text: &str, out_path: &str) {
        let Some((_cfg, _audio, mut synth, voice)) = self.make_synthesizer(out_path) else {
            return;
        };

        use std::sync::atomic::{AtomicU32, Ordering};
        use std::sync::Arc;
        let synthesizing_count = Arc::new(AtomicU32::new(0));
        let word_count = Arc::new(AtomicU32::new(0));
        let viseme_count = Arc::new(AtomicU32::new(0));

        let log_tx = self.log_tx.clone();
        let _ = synth.set_synthesis_started_cb(move |_| {
            let _ = log_tx.send(LogLine("  · 事件 started：开始合成".into()));
        });
        {
            let c = synthesizing_count.clone();
            let _ = synth.set_synthesizing_cb(move |a| {
                c.fetch_add(1, Ordering::Relaxed);
                let _ = &a; // 仅计数，避免刷屏
            });
        }
        {
            let log_tx = self.log_tx.clone();
            let c = word_count.clone();
            let _ = synth.set_word_boundary_cb(move |a| {
                let n = c.fetch_add(1, Ordering::Relaxed) + 1;
                if n <= 5 {
                    let _ = log_tx.send(LogLine(format!(
                        "  · 词边界 #{n}：text_offset={} len={} type={}",
                        a.text_offset, a.word_length, a.boundary_type
                    )));
                }
            });
        }
        {
            let c = viseme_count.clone();
            let _ = synth.set_viseme_cb(move |_| {
                c.fetch_add(1, Ordering::Relaxed);
            });
        }
        {
            let log_tx = self.log_tx.clone();
            let _ = synth.set_bookmark_cb(move |a| {
                let _ = log_tx.send(LogLine(format!(
                    "  · 书签：{} @ {:.2}s",
                    a.text,
                    a.audio_offset_ticks as f64 / 1e7
                )));
            });
        }
        {
            let log_tx = self.log_tx.clone();
            let _ = synth.set_synthesis_completed_cb(move |a| {
                let _ = log_tx.send(LogLine(format!(
                    "  · 事件 completed：共 {} 字节",
                    a.result.audio_length
                )));
            });
        }

        self.log(format!("→ TTS 合成并监听事件（语音 {voice}）：{text}"));
        match synth.speak_text(text) {
            Ok(r) => self.log(format!(
                "✓ 合成完成：{} 字节；synthesizing 块数={}，词边界={}，viseme={}",
                r.audio_length,
                synthesizing_count.load(Ordering::Relaxed),
                word_count.load(Ordering::Relaxed),
                viseme_count.load(Ordering::Relaxed),
            )),
            Err(e) => self.log(format!("[错误] 合成失败：{e:?}")),
        }
    }

    /// TTS：列出可用嗓音。
    fn list_voices(&mut self, locale: &str) {
        // 嗓音查询不需要音频输出，临时用一个内存输出。
        let tmp = std::env::temp_dir().join(format!("tfp_voices_{}.wav", std::process::id()));
        let out_path = tmp.to_string_lossy().to_string();
        let Some((_cfg, _audio, synth, _voice)) = self.make_synthesizer(&out_path) else {
            return;
        };
        let shown_locale = if locale.is_empty() { "全部" } else { locale };
        self.log(format!("→ 查询嗓音列表（locale={shown_locale}）"));
        match synth.get_voices_list(locale) {
            Ok(voices) => {
                self.log(format!("✓ 共 {} 个嗓音，前 15 个：", voices.len()));
                for v in voices.iter().take(15) {
                    self.log(format!(
                        "    {} [{}] {}",
                        v.short_name, v.locale, v.local_name
                    ));
                }
            }
            Err(e) => self.log(format!("[错误] 查询嗓音失败：{e:?}")),
        }
        let _ = std::fs::remove_file(&out_path);
    }

    /// 纯 STT：识别一个 WAV 文件（不翻译）。
    fn recognize_wav_stt(&mut self, path: &str) {
        let lang = self
            .creds
            .as_ref()
            .map(|c| c.source_lang.clone())
            .unwrap_or_else(|| "zh-CN".into());

        let mut config = match self.build_speech_config(false) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return;
            }
        };
        if let Err(e) = config.set_speech_recognition_language(&lang) {
            self.log(format!("[错误] 设置识别语言失败：{e:?}"));
            return;
        }

        let audio = match AudioConfig::from_wav_file_input(path) {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 打开 WAV 失败：{e:?}"));
                return;
            }
        };

        let recognizer = match SpeechRecognizer::from_config(&config, &audio) {
            Ok(r) => r,
            Err(e) => {
                self.log(format!("[错误] 创建识别器失败：{e:?}"));
                return;
            }
        };

        self.log(format!("→ 纯 STT 识别 WAV（语言 {lang}）：{path}"));
        match pollster::block_on(recognizer.recognize_once_async()) {
            Ok(result) => {
                if result.text.is_empty() {
                    self.log(format!("（未识别到文本，reason={:?}）", result.reason));
                } else {
                    self.log(format!("✓ 识别结果：{}", result.text));
                }
            }
            Err(e) => self.log(format!("[错误] 识别失败：{e:?}")),
        }
    }

    /// 自动语言检测：在候选语言间识别一个 WAV 文件。
    fn recognize_wav_auto_detect(&mut self, path: &str, candidates: &[String]) {
        let config = match self.build_speech_config(false) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return;
            }
        };

        let cand_refs: Vec<&str> = candidates.iter().map(|s| s.as_str()).collect();
        let auto_cfg = match AutoDetectSourceLanguageConfig::from_languages(&cand_refs) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] 创建自动检测配置失败：{e:?}"));
                return;
            }
        };

        let audio = match AudioConfig::from_wav_file_input(path) {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 打开 WAV 失败：{e:?}"));
                return;
            }
        };

        let recognizer = match SpeechRecognizer::from_auto_detect_source_language_config(
            &config, &auto_cfg, &audio,
        ) {
            Ok(r) => r,
            Err(e) => {
                self.log(format!("[错误] 创建识别器失败：{e:?}"));
                return;
            }
        };

        self.log(format!(
            "→ 自动语言检测识别 WAV（候选 {}）：{path}",
            candidates.join("/")
        ));
        match pollster::block_on(recognizer.recognize_once_async()) {
            Ok(result) => {
                let detected = result.detected_language().unwrap_or_default();
                if result.text.is_empty() {
                    self.log(format!("（未识别到文本，reason={:?}）", result.reason));
                } else {
                    self.log(format!(
                        "✓ 检测到语言：{}　识别结果：{}",
                        if detected.is_empty() { "(未知)" } else { &detected },
                        result.text
                    ));
                }
            }
            Err(e) => self.log(format!("[错误] 识别失败：{e:?}")),
        }
    }

    /// 短语列表语法：向识别器添加一组短语后识别 WAV。
    fn recognize_wav_with_phrases(&mut self, path: &str, phrases: &[String]) {
        let lang = self
            .creds
            .as_ref()
            .map(|c| c.source_lang.clone())
            .unwrap_or_else(|| "zh-CN".into());

        let mut config = match self.build_speech_config(false) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return;
            }
        };
        if let Err(e) = config.set_speech_recognition_language(&lang) {
            self.log(format!("[错误] 设置识别语言失败：{e:?}"));
            return;
        }

        let audio = match AudioConfig::from_wav_file_input(path) {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 打开 WAV 失败：{e:?}"));
                return;
            }
        };

        let recognizer = match SpeechRecognizer::from_config(&config, &audio) {
            Ok(r) => r,
            Err(e) => {
                self.log(format!("[错误] 创建识别器失败：{e:?}"));
                return;
            }
        };

        let grammar = match PhraseListGrammar::from_recognizer(&recognizer) {
            Ok(g) => g,
            Err(e) => {
                self.log(format!("[错误] 创建短语列表失败：{e:?}"));
                return;
            }
        };
        for p in phrases {
            if let Err(e) = grammar.add_phrase(p) {
                self.log(format!("[错误] 添加短语 “{p}” 失败：{e:?}"));
                return;
            }
        }

        self.log(format!(
            "→ 短语列表识别 WAV（语言 {lang}，短语 {}）：{path}",
            phrases.join("/")
        ));
        match pollster::block_on(recognizer.recognize_once_async()) {
            Ok(result) => {
                if result.text.is_empty() {
                    self.log(format!("（未识别到文本，reason={:?}）", result.reason));
                } else {
                    self.log(format!("✓ 识别结果：{}", result.text));
                }
            }
            Err(e) => self.log(format!("[错误] 识别失败：{e:?}")),
        }
    }

    /// Connection 对象：显式 open/close 并监听 connected/disconnected 事件，识别 WAV。
    fn recognize_wav_with_connection(&mut self, path: &str) {
        use std::sync::atomic::{AtomicBool, Ordering};
        use std::sync::Arc;

        let lang = self
            .creds
            .as_ref()
            .map(|c| c.source_lang.clone())
            .unwrap_or_else(|| "zh-CN".into());

        let mut config = match self.build_speech_config(false) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return;
            }
        };
        if let Err(e) = config.set_speech_recognition_language(&lang) {
            self.log(format!("[错误] 设置识别语言失败：{e:?}"));
            return;
        }

        let audio = match AudioConfig::from_wav_file_input(path) {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 打开 WAV 失败：{e:?}"));
                return;
            }
        };

        let recognizer = match SpeechRecognizer::from_config(&config, &audio) {
            Ok(r) => r,
            Err(e) => {
                self.log(format!("[错误] 创建识别器失败：{e:?}"));
                return;
            }
        };

        let mut connection = match Connection::from_recognizer(&recognizer) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] 获取连接对象失败：{e:?}"));
                return;
            }
        };

        let connected = Arc::new(AtomicBool::new(false));
        let disconnected = Arc::new(AtomicBool::new(false));
        {
            let connected = connected.clone();
            let tx = self.log_tx.clone();
            let _ = connection.set_connected_cb(move |sid| {
                connected.store(true, Ordering::Relaxed);
                let _ = tx.send(LogLine(format!("[连接] connected（session={sid}）")));
            });
        }
        {
            let disconnected = disconnected.clone();
            let tx = self.log_tx.clone();
            let _ = connection.set_disconnected_cb(move |sid| {
                disconnected.store(true, Ordering::Relaxed);
                let _ = tx.send(LogLine(format!("[连接] disconnected（session={sid}）")));
            });
        }

        self.log(format!("→ Connection 显式连接并识别 WAV（语言 {lang}）：{path}"));
        if let Err(e) = connection.open(false) {
            self.log(format!("[错误] 打开连接失败：{e:?}"));
            return;
        }

        match pollster::block_on(recognizer.recognize_once_async()) {
            Ok(result) => {
                if result.text.is_empty() {
                    self.log(format!("（未识别到文本，reason={:?}）", result.reason));
                } else {
                    self.log(format!("✓ 识别结果：{}", result.text));
                }
            }
            Err(e) => self.log(format!("[错误] 识别失败：{e:?}")),
        }

        let _ = connection.close();
        // 给 disconnected 事件一点时间触发。
        Self::wait_done(&disconnected, 2);
        self.log(format!(
            "✓ Connection 测试结束（connected={}，disconnected={}）",
            connected.load(Ordering::Relaxed),
            disconnected.load(Ordering::Relaxed)
        ));
    }

    /// 发音评估：以参考文本评估 WAV 发音，输出各项分数。
    fn assess_pronunciation(&mut self, path: &str, reference_text: &str) {
        let lang = self
            .creds
            .as_ref()
            .map(|c| c.source_lang.clone())
            .unwrap_or_else(|| "zh-CN".into());

        let mut config = match self.build_speech_config(false) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return;
            }
        };
        if let Err(e) = config.set_speech_recognition_language(&lang) {
            self.log(format!("[错误] 设置识别语言失败：{e:?}"));
            return;
        }

        let audio = match AudioConfig::from_wav_file_input(path) {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 打开 WAV 失败：{e:?}"));
                return;
            }
        };

        let recognizer = match SpeechRecognizer::from_config(&config, &audio) {
            Ok(r) => r,
            Err(e) => {
                self.log(format!("[错误] 创建识别器失败：{e:?}"));
                return;
            }
        };

        let pa_config = match PronunciationAssessmentConfig::new(
            reference_text,
            GradingSystem::HundredMark,
            Granularity::Phoneme,
            false,
        ) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] 创建发音评估配置失败：{e:?}"));
                return;
            }
        };
        if let Err(e) = pa_config.apply_to_recognizer(&recognizer) {
            self.log(format!("[错误] 应用发音评估配置失败：{e:?}"));
            return;
        }

        self.log(format!(
            "→ 发音评估 WAV（语言 {lang}，参考文本「{reference_text}」）：{path}"
        ));
        match pollster::block_on(recognizer.recognize_once_async()) {
            Ok(result) => {
                self.log(format!("  识别文本：{}", result.text));
                match PronunciationAssessmentResult::from_result(&result) {
                    Ok(pa) => {
                        let prosody = pa
                            .prosody_score
                            .map(|v| format!("，韵律 {v:.1}"))
                            .unwrap_or_default();
                        self.log(format!(
                            "✓ 发音评估：准确 {:.1}　流利 {:.1}　完整 {:.1}　总分 {:.1}{}",
                            pa.accuracy_score,
                            pa.fluency_score,
                            pa.completeness_score,
                            pa.pronunciation_score,
                            prosody
                        ));
                    }
                    Err(e) => self.log(format!("[错误] 解析评估结果失败：{e:?}")),
                }
            }
            Err(e) => self.log(format!("[错误] 识别失败：{e:?}")),
        }
    }

    /// 构建纯 STT 配置（含识别语言），返回 (config, lang)。
    fn build_stt_config(&self) -> Result<(SpeechConfig, String), String> {
        let lang = self
            .creds
            .as_ref()
            .map(|c| c.source_lang.clone())
            .unwrap_or_else(|| "zh-CN".into());
        let mut config = self.build_speech_config(false)?;
        config
            .set_speech_recognition_language(&lang)
            .map_err(|e| format!("设置识别语言失败：{e:?}"))?;
        Ok((config, lang))
    }

    /// 等待 done 标志置位，最多 timeout_secs 秒。
    fn wait_done(done: &std::sync::atomic::AtomicBool, timeout_secs: u64) {
        use std::sync::atomic::Ordering;
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(timeout_secs);
        while !done.load(Ordering::Relaxed) && std::time::Instant::now() < deadline {
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
    }

    /// 纯 STT 连续识别一个 WAV 文件，回调收集所有分段。
    fn recognize_wav_stt_continuous(&mut self, path: &str) {
        use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
        use std::sync::Arc;

        let (config, lang) = match self.build_stt_config() {
            Ok(v) => v,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return;
            }
        };
        let audio = match AudioConfig::from_wav_file_input(path) {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 打开 WAV 失败：{e:?}"));
                return;
            }
        };
        let mut recognizer = match SpeechRecognizer::from_config(&config, &audio) {
            Ok(r) => r,
            Err(e) => {
                self.log(format!("[错误] 创建识别器失败：{e:?}"));
                return;
            }
        };

        let done = Arc::new(AtomicBool::new(false));
        let count = Arc::new(AtomicU32::new(0));

        let tx = self.log_tx.clone();
        let c = count.clone();
        let _ = recognizer.set_recognized_cb(move |ev| {
            if !ev.result.text.is_empty() {
                let n = c.fetch_add(1, Ordering::Relaxed) + 1;
                let _ = tx.send(LogLine(format!("[最终#{n}] {}", ev.result.text)));
            }
        });
        let tx = self.log_tx.clone();
        let d = done.clone();
        let _ = recognizer.set_canceled_cb(move |ev| {
            let _ = tx.send(LogLine(format!("[取消] reason={:?}", ev.reason)));
            d.store(true, Ordering::Relaxed);
        });
        let tx = self.log_tx.clone();
        let d = done.clone();
        let _ = recognizer.set_session_stopped_cb(move || {
            let _ = tx.send(LogLine("[会话] 已停止".into()));
            d.store(true, Ordering::Relaxed);
        });

        self.log(format!("→ 纯 STT 连续识别 WAV（语言 {lang}）：{path}"));
        if let Err(e) = pollster::block_on(recognizer.start_continuous_recognition_async()) {
            self.log(format!("[错误] 启动连续识别失败：{e:?}"));
            return;
        }
        Self::wait_done(&done, 30);
        if let Err(e) = pollster::block_on(recognizer.stop_continuous_recognition_async()) {
            self.log(format!("[警告] 停止连续识别：{e:?}"));
        }
        self.log(format!(
            "✓ 连续识别结束，共 {} 个最终分段",
            count.load(Ordering::Relaxed)
        ));
    }

    /// 纯 STT 推流识别：解析 WAV 的 PCM，分块写入 push stream 后识别。
    fn recognize_wav_stt_push_stream(&mut self, path: &str) {
        use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
        use std::sync::Arc;

        let (sps, bits, ch, pcm) = match parse_wav_pcm(path) {
            Ok(v) => v,
            Err(e) => {
                self.log(format!("[错误] 解析 WAV 失败：{e}"));
                return;
            }
        };
        self.log(format!(
            "→ 推流识别：PCM {sps}Hz/{bits}bit/{ch}ch，共 {} 字节",
            pcm.len()
        ));

        let (config, lang) = match self.build_stt_config() {
            Ok(v) => v,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return;
            }
        };

        let format = match AudioStreamFormat::from_pcm(sps, bits as u8, ch as u8) {
            Ok(f) => f,
            Err(e) => {
                self.log(format!("[错误] 创建音频格式失败：{e:?}"));
                return;
            }
        };
        let stream = match PushAudioInputStream::create(&format) {
            Ok(s) => s,
            Err(e) => {
                self.log(format!("[错误] 创建推流失败：{e:?}"));
                return;
            }
        };
        let audio = match AudioConfig::from_stream(&stream) {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 绑定推流失败：{e:?}"));
                return;
            }
        };
        let mut recognizer = match SpeechRecognizer::from_config(&config, &audio) {
            Ok(r) => r,
            Err(e) => {
                self.log(format!("[错误] 创建识别器失败：{e:?}"));
                return;
            }
        };

        let done = Arc::new(AtomicBool::new(false));
        let count = Arc::new(AtomicU32::new(0));
        let tx = self.log_tx.clone();
        let c = count.clone();
        let _ = recognizer.set_recognized_cb(move |ev| {
            if !ev.result.text.is_empty() {
                let n = c.fetch_add(1, Ordering::Relaxed) + 1;
                let _ = tx.send(LogLine(format!("[最终#{n}] {}", ev.result.text)));
            }
        });
        let tx = self.log_tx.clone();
        let d = done.clone();
        let _ = recognizer.set_canceled_cb(move |ev| {
            let _ = tx.send(LogLine(format!("[取消] reason={:?}", ev.reason)));
            d.store(true, Ordering::Relaxed);
        });
        let tx = self.log_tx.clone();
        let d = done.clone();
        let _ = recognizer.set_session_stopped_cb(move || {
            let _ = tx.send(LogLine("[会话] 已停止".into()));
            d.store(true, Ordering::Relaxed);
        });

        self.log(format!("→ 纯 STT 推流识别（语言 {lang}）"));
        if let Err(e) = pollster::block_on(recognizer.start_continuous_recognition_async()) {
            self.log(format!("[错误] 启动连续识别失败：{e:?}"));
            return;
        }

        // 分块写入 PCM（每块约 0.1 秒），最后关闭流。
        let chunk = (sps as usize * (bits as usize / 8) * ch as usize / 10).max(3200);
        for block in pcm.chunks(chunk) {
            if let Err(e) = stream.write(block) {
                self.log(format!("[错误] 推流写入失败：{e:?}"));
                break;
            }
        }
        let _ = stream.close();

        Self::wait_done(&done, 30);
        if let Err(e) = pollster::block_on(recognizer.stop_continuous_recognition_async()) {
            self.log(format!("[警告] 停止连续识别：{e:?}"));
        }
        self.log(format!(
            "✓ 推流识别结束，共 {} 个最终分段",
            count.load(Ordering::Relaxed)
        ));
    }

    /// TTS：合成到默认扬声器（会发声）。
    fn synthesize_to_speaker(&mut self, text: &str) {
        let voice = {
            let lang = self
                .creds
                .as_ref()
                .map(|c| c.source_lang.clone())
                .unwrap_or_else(|| "zh-CN".into());
            Self::default_voice(&lang).to_string()
        };
        let mut config = match self.build_speech_config(true) {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] {e}"));
                return;
            }
        };
        if let Err(e) = config.set_speech_synthesis_voice_name(&voice) {
            self.log(format!("[错误] 设置语音失败：{e:?}"));
            return;
        }
        let audio = match AudioConfig::from_default_speaker_output() {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 打开扬声器失败：{e:?}"));
                return;
            }
        };
        let synth = match SpeechSynthesizer::from_config(&config, &audio) {
            Ok(s) => s,
            Err(e) => {
                self.log(format!("[错误] 创建合成器失败：{e:?}"));
                return;
            }
        };
        self.log(format!("→ TTS 合成到扬声器（语音 {voice}）：{text}"));
        match synth.speak_text(text) {
            Ok(r) => self.log(format!("✓ 已播放：{} 字节", r.audio_length)),
            Err(e) => self.log(format!("[错误] 播放失败：{e:?}")),
        }
    }

    /// 闭环自测：TTS 合成固定文字到临时 WAV，再 STT 识别回来。
    fn self_test_closed_loop(&mut self) {
        let lang = self
            .creds
            .as_ref()
            .map(|c| c.source_lang.clone())
            .unwrap_or_else(|| "zh-CN".into());
        let text = if lang.starts_with("zh") {
            "你好世界，这是一次闭环自测。"
        } else if lang.starts_with("ja") {
            "こんにちは世界、これはクローズドループのセルフテストです。"
        } else {
            "Hello world, this is a closed loop self test."
        };

        let mut tmp = std::env::temp_dir();
        tmp.push(format!("tfp_selftest_{}.wav", std::process::id()));
        let out_path = tmp.to_string_lossy().to_string();

        self.log("==== 闭环自测开始 ====");
        self.synthesize_to_wav(text, &out_path);

        // 合成失败则文件可能不存在
        if !std::path::Path::new(&out_path).exists() {
            self.log("[错误] 闭环自测中止：合成文件不存在");
            return;
        }

        self.recognize_wav_stt(&out_path);
        self.log(format!("期望文本：{text}"));
        self.log("==== 闭环自测结束 ====");
        let _ = std::fs::remove_file(&out_path);
    }

    fn create_config(
        &mut self,
        key: String,
        region: String,
        host: Option<String>,
        source_lang: String,
        target_langs: Vec<String>,
        is_china: bool,
    ) {
        // 切换配置前停止可能在跑的连续识别
        if self.continuous.is_some() {
            self.stop_continuous();
        }

        let cfg_result = if is_china {
            match &host {
                Some(h) => {
                    self.log(format!("使用中国版 host：{h}"));
                    SpeechTranslationConfig::from_host_with_subscription(h, &key)
                }
                None => {
                    self.log("[错误] 中国版缺少 host");
                    return;
                }
            }
        } else {
            self.log(format!("使用国际版 region：{region}"));
            SpeechTranslationConfig::from_subscription(&key, &region)
        };

        let mut config = match cfg_result {
            Ok(c) => c,
            Err(e) => {
                self.log(format!("[错误] 创建配置失败：{e:?}"));
                return;
            }
        };

        if let Err(e) = config.set_speech_recognition_language(&source_lang) {
            self.log(format!("[错误] 设置源语言失败：{e:?}"));
            return;
        }
        for lang in &target_langs {
            if let Err(e) = config.add_target_language(lang) {
                self.log(format!("[错误] 添加目标语言 {lang} 失败：{e:?}"));
                return;
            }
        }

        self.log(format!(
            "✓ 配置就绪：{source_lang} → {}",
            target_langs.join(", ")
        ));
        self.config = Some(config);
        self.creds = Some(Creds {
            key,
            region,
            source_lang,
            is_china,
        });
    }

    fn recognize_once(&mut self, wav_path: Option<String>) {
        let config = match &self.config {
            Some(c) => c,
            None => {
                self.log("[错误] 请先创建配置");
                return;
            }
        };

        let audio = match &wav_path {
            Some(path) => match AudioConfig::from_wav_file_input(path) {
                Ok(a) => a,
                Err(e) => {
                    self.log(format!("[错误] 打开 WAV 失败：{e:?}"));
                    return;
                }
            },
            None => match AudioConfig::from_default_microphone_input() {
                Ok(a) => a,
                Err(e) => {
                    self.log(format!("[错误] 打开麦克风失败：{e:?}"));
                    return;
                }
            },
        };

        let recognizer = match TranslationRecognizer::from_config(config, &audio) {
            Ok(r) => r,
            Err(e) => {
                self.log(format!("[错误] 创建识别器失败：{e:?}"));
                return;
            }
        };

        match pollster::block_on(recognizer.recognize_once_async()) {
            Ok(result) => {
                self.log(format!("识别原文：{}", result.base.text));
                if result.translations.is_empty() {
                    self.log("（无翻译结果，可能未识别到语音）");
                } else {
                    for (lang, text) in &result.translations {
                        self.log(format!("  → {lang}: {text}"));
                    }
                }
            }
            Err(e) => self.log(format!("[错误] 识别失败：{e:?}")),
        }
    }

    fn start_continuous(&mut self) {
        if self.continuous.is_some() {
            self.log("连续识别已在运行");
            return;
        }
        let config = match &self.config {
            Some(c) => c,
            None => {
                self.log("[错误] 请先创建配置");
                return;
            }
        };

        let audio = match AudioConfig::from_default_microphone_input() {
            Ok(a) => a,
            Err(e) => {
                self.log(format!("[错误] 打开麦克风失败：{e:?}"));
                return;
            }
        };

        let mut recognizer = match TranslationRecognizer::from_config(config, &audio) {
            Ok(r) => r,
            Err(e) => {
                self.log(format!("[错误] 创建识别器失败：{e:?}"));
                return;
            }
        };

        // 注册回调：实时把识别/翻译结果推到日志通道
        let tx = self.log_tx.clone();
        let _ = recognizer.set_recognizing_cb(move |ev| {
            let mut s = format!("[中间] {}", ev.result.base.text);
            for (lang, text) in &ev.result.translations {
                s.push_str(&format!("  → {lang}: {text}"));
            }
            let _ = tx.send(LogLine(s));
        });

        let tx = self.log_tx.clone();
        let _ = recognizer.set_recognized_cb(move |ev| {
            let mut s = format!("[最终] {}", ev.result.base.text);
            for (lang, text) in &ev.result.translations {
                s.push_str(&format!("  → {lang}: {text}"));
            }
            let _ = tx.send(LogLine(s));
        });

        let tx = self.log_tx.clone();
        let _ = recognizer.set_canceled_cb(move |ev| {
            let _ = tx.send(LogLine(format!(
                "[取消] reason={:?} code={:?} {}",
                ev.reason, ev.error_code, ev.error_details
            )));
        });

        let tx = self.log_tx.clone();
        let _ = recognizer.set_session_started_cb(move |_ev| {
            let _ = tx.send(LogLine("[会话] 已开始".into()));
        });

        let tx = self.log_tx.clone();
        let _ = recognizer.set_session_stopped_cb(move |_ev| {
            let _ = tx.send(LogLine("[会话] 已停止".into()));
        });

        if let Err(e) = pollster::block_on(recognizer.start_continuous_recognition_async()) {
            self.log(format!("[错误] 启动连续识别失败：{e:?}"));
            return;
        }

        self.log("✓ 连续识别已启动，请持续说话…");
        self.continuous = Some(recognizer);
    }

    fn stop_continuous(&mut self) {
        if let Some(mut recognizer) = self.continuous.take() {
            if let Err(e) = pollster::block_on(recognizer.stop_continuous_recognition_async()) {
                self.log(format!("[错误] 停止连续识别失败：{e:?}"));
            } else {
                self.log("✓ 连续识别已停止");
            }
        } else {
            self.log("当前没有正在运行的连续识别");
        }
    }
}

/// 解析 WAV 文件，返回 (采样率, 位深, 声道数, 裸 PCM 字节)。
///
/// 仅支持标准未压缩 PCM（fmt 块 audioFormat=1）。遍历 RIFF 块以正确定位
/// `fmt ` 与 `data`，避免对固定 44 字节头的假设。
fn parse_wav_pcm(path: &str) -> Result<(u32, u16, u16, Vec<u8>), String> {
    let bytes = std::fs::read(path).map_err(|e| format!("读取文件失败：{e}"))?;
    if bytes.len() < 12 || &bytes[0..4] != b"RIFF" || &bytes[8..12] != b"WAVE" {
        return Err("不是有效的 WAV（缺少 RIFF/WAVE 头）".into());
    }

    let mut pos = 12usize;
    let mut sps = 0u32;
    let mut bits = 0u16;
    let mut ch = 0u16;
    let mut pcm: Option<Vec<u8>> = None;

    let rd_u16 = |b: &[u8]| u16::from_le_bytes([b[0], b[1]]);
    let rd_u32 = |b: &[u8]| u32::from_le_bytes([b[0], b[1], b[2], b[3]]);

    while pos + 8 <= bytes.len() {
        let id = &bytes[pos..pos + 4];
        let size = rd_u32(&bytes[pos + 4..pos + 8]) as usize;
        let body = pos + 8;
        if body + size > bytes.len() {
            break;
        }
        if id == b"fmt " && size >= 16 {
            let fmt = &bytes[body..body + size];
            ch = rd_u16(&fmt[2..4]);
            sps = rd_u32(&fmt[4..8]);
            bits = rd_u16(&fmt[14..16]);
        } else if id == b"data" {
            pcm = Some(bytes[body..body + size].to_vec());
        }
        // 块按偶数字节对齐
        pos = body + size + (size & 1);
    }

    let pcm = pcm.ok_or("WAV 缺少 data 块")?;
    if sps == 0 || bits == 0 || ch == 0 {
        return Err("WAV 缺少有效 fmt 块".into());
    }
    Ok((sps, bits, ch, pcm))
}

