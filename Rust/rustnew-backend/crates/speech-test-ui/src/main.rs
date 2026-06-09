//! speech-test-ui — egui 桌面小窗，用于逐项测试 speech-sdk FFI 交付物。
//!
//! 设计：
//! - SDK 对象（config / recognizer）全部留在单独的 worker 线程，UI 线程通过
//!   命令通道下发指令，worker 通过日志通道回传文本，避免 Send/线程安全问题。
//! - 凭据从 C# 端配置 `%APPDATA%\TrueFluentPro\config.json` 的 SpeechResources
//!   读取，不在源码/界面里硬编码密钥。
//! - 支持国际版（from_subscription(key, region)）与中国版（from_host_with_subscription）。

use std::sync::mpsc::{Receiver, Sender};

use eframe::egui;
use serde::Deserialize;

mod worker;
use worker::{Command, LogLine, WorkerHandle};

// ─── 配置读取 ────────────────────────────────────────────────────────

#[derive(Debug, Clone, Deserialize)]
struct SpeechResource {
    #[serde(default, rename = "Name")]
    name: String,
    #[serde(default, rename = "ServiceRegion")]
    service_region: String,
    #[serde(default, rename = "Endpoint")]
    endpoint: String,
    #[serde(default, rename = "SubscriptionKey")]
    subscription_key: String,
}

#[derive(Debug, Deserialize)]
struct AppConfig {
    #[serde(default, rename = "SpeechResources")]
    speech_resources: Vec<SpeechResource>,
}

fn load_resources() -> Result<Vec<SpeechResource>, String> {
    let dir = dirs::config_dir().ok_or("无法定位 APPDATA 目录")?;
    let path = dir.join("TrueFluentPro").join("config.json");
    let raw = std::fs::read_to_string(&path)
        .map_err(|e| format!("读取 {} 失败: {e}", path.display()))?;
    let cfg: AppConfig =
        serde_json::from_str(&raw).map_err(|e| format!("解析 config.json 失败: {e}"))?;
    Ok(cfg.speech_resources)
}

/// 判断是否为 Azure 中国（世纪互联）资源。
fn is_china(res: &SpeechResource) -> bool {
    res.endpoint.contains(".azure.cn")
        || res.service_region.starts_with("china")
}

/// 推导中国版实时语音的 host（STT/翻译共用）。
fn china_host(region: &str) -> String {
    format!("wss://{region}.stt.speech.azure.cn")
}

// ─── 应用状态 ────────────────────────────────────────────────────────

struct App {
    resources: Vec<SpeechResource>,
    selected: usize,
    source_lang: String,
    target_langs: String,
    wav_path: String,
    tts_text: String,
    tts_out_path: String,
    tts_ssml: String,
    voices_locale: String,
    logs: Vec<String>,
    cmd_tx: Sender<Command>,
    log_rx: Receiver<LogLine>,
    _worker: WorkerHandle,
    load_error: Option<String>,
    /// GUI 模式下同时把日志追写到文件，便于外部读取判断效果。
    log_file: Option<std::path::PathBuf>,
}

impl App {
    fn new() -> Self {
        let (resources, load_error) = match load_resources() {
            Ok(r) => (r, None),
            Err(e) => (Vec::new(), Some(e)),
        };

        let (cmd_tx, cmd_rx) = std::sync::mpsc::channel::<Command>();
        let (log_tx, log_rx) = std::sync::mpsc::channel::<LogLine>();

        let worker = WorkerHandle::spawn(cmd_rx, log_tx);

        // 日志文件：固定在临时目录，每次启动覆盖。
        let log_file = {
            let mut p = std::env::temp_dir();
            p.push("tfp_speech_ui.log");
            // 清空旧内容
            let _ = std::fs::write(&p, b"");
            println!("日志文件：{}", p.display());
            Some(p)
        };

        Self {
            resources,
            selected: 0,
            source_lang: "zh-CN".into(),
            target_langs: "en".into(),
            wav_path: String::new(),
            tts_text: "你好，这是一段语音合成测试。".into(),
            tts_out_path: String::new(),
            tts_ssml: "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'><voice name='zh-CN-XiaoxiaoNeural'>大家好<bookmark mark='mid'/>，这是 <emphasis level='strong'>SSML</emphasis> 合成测试。</voice></speak>".into(),
            voices_locale: "zh-CN".into(),
            logs: vec!["就绪。请选择资源并点击「创建配置」。".into()],
            cmd_tx,
            log_rx,
            _worker: worker,
            load_error,
            log_file,
        }
    }

    fn push_log(&mut self, s: impl Into<String>) {
        let s = s.into();
        // 追写到日志文件（带时间戳）
        if let Some(path) = &self.log_file {
            use std::io::Write;
            if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
                let ts = now_hms();
                let _ = writeln!(f, "[{ts}] {s}");
            }
        }
        self.logs.push(s);
        if self.logs.len() > 500 {
            let overflow = self.logs.len() - 500;
            self.logs.drain(0..overflow);
        }
    }

    fn send(&mut self, cmd: Command) {
        if let Err(e) = self.cmd_tx.send(cmd) {
            self.push_log(format!("[错误] 无法发送命令: {e}"));
        }
    }

    /// 解析 TTS 输出路径：留空则用临时目录，并回填到输入框。
    fn resolve_tts_out(&mut self) -> String {
        if self.tts_out_path.trim().is_empty() {
            let mut p = std::env::temp_dir();
            p.push("tfp_tts_out.wav");
            let s = p.to_string_lossy().to_string();
            self.tts_out_path = s.clone();
            s
        } else {
            self.tts_out_path.trim().to_string()
        }
    }

    fn build_create_cmd(&self) -> Result<Command, String> {
        let res = self
            .resources
            .get(self.selected)
            .ok_or("未选择资源")?
            .clone();
        if res.subscription_key.is_empty() {
            return Err("该资源缺少密钥".into());
        }
        let targets: Vec<String> = self
            .target_langs
            .split(|c| c == ',' || c == ' ')
            .map(|s| s.trim())
            .filter(|s| !s.is_empty())
            .map(|s| s.to_string())
            .collect();
        if targets.is_empty() {
            return Err("目标语言不能为空".into());
        }

        Ok(Command::CreateConfig {
            key: res.subscription_key.clone(),
            region: res.service_region.clone(),
            host: if is_china(&res) {
                Some(china_host(&res.service_region))
            } else {
                None
            },
            source_lang: self.source_lang.trim().to_string(),
            target_langs: targets,
            is_china: is_china(&res),
        })
    }
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        // 拉取 worker 日志
        while let Ok(line) = self.log_rx.try_recv() {
            self.push_log(line.0);
        }
        // 持续重绘以便实时显示日志
        ctx.request_repaint_after(std::time::Duration::from_millis(100));

        egui::TopBottomPanel::top("top").show(ctx, |ui| {
            ui.add_space(4.0);
            ui.heading("Speech SDK FFI 逐项测试");
            ui.add_space(4.0);
        });

        egui::SidePanel::left("controls")
            .resizable(false)
            .min_width(300.0)
            .show(ctx, |ui| {
                if let Some(err) = &self.load_error {
                    ui.colored_label(egui::Color32::from_rgb(220, 80, 80), err);
                    ui.separator();
                }

                ui.label("语音资源（来自 config.json）：");
                if self.resources.is_empty() {
                    ui.label("（无可用资源）");
                } else {
                    let current = self
                        .resources
                        .get(self.selected)
                        .map(|r| {
                            let tag = if is_china(r) { "中国" } else { "国际" };
                            format!("{} [{}] {}", r.name, tag, r.service_region)
                        })
                        .unwrap_or_default();
                    egui::ComboBox::from_id_salt("res_combo")
                        .width(260.0)
                        .selected_text(current)
                        .show_ui(ui, |ui| {
                            for (i, r) in self.resources.iter().enumerate() {
                                let tag = if is_china(r) { "中国" } else { "国际" };
                                let label =
                                    format!("{} [{}] {}", r.name, tag, r.service_region);
                                ui.selectable_value(&mut self.selected, i, label);
                            }
                        });
                }

                ui.add_space(6.0);
                ui.horizontal(|ui| {
                    ui.label("源语言：");
                    ui.text_edit_singleline(&mut self.source_lang);
                });
                ui.horizontal(|ui| {
                    ui.label("目标语言：");
                    ui.text_edit_singleline(&mut self.target_langs);
                });
                ui.small("多个目标语言用逗号分隔，如 en,ja");

                ui.separator();

                if ui.button("① 创建配置").clicked() {
                    match self.build_create_cmd() {
                        Ok(cmd) => {
                            self.push_log("→ 创建配置…");
                            self.send(cmd);
                        }
                        Err(e) => self.push_log(format!("[错误] {e}")),
                    }
                }

                if ui.button("② 麦克风识别一次").clicked() {
                    self.push_log("→ 麦克风识别一次（请说话）…");
                    self.send(Command::RecognizeOnceMic);
                }

                ui.horizontal(|ui| {
                    if ui.button("③ 开始连续识别").clicked() {
                        self.push_log("→ 开始连续识别…");
                        self.send(Command::StartContinuous);
                    }
                    if ui.button("停止").clicked() {
                        self.push_log("→ 停止连续识别…");
                        self.send(Command::StopContinuous);
                    }
                });

                ui.separator();
                ui.label("WAV 文件路径：");
                ui.text_edit_singleline(&mut self.wav_path);
                if ui.button("④ WAV 文件识别一次").clicked() {
                    let path = self.wav_path.trim().to_string();
                    if path.is_empty() {
                        self.push_log("[错误] 请填写 WAV 文件路径");
                    } else {
                        self.push_log(format!("→ 识别 WAV：{path}"));
                        self.send(Command::RecognizeOnceWav(path));
                    }
                }
                if ui.button("④′ 纯 STT 识别 WAV（不翻译）").clicked() {
                    let path = self.wav_path.trim().to_string();
                    if path.is_empty() {
                        self.push_log("[错误] 请填写 WAV 文件路径");
                    } else {
                        self.push_log(format!("→ 纯 STT 识别 WAV：{path}"));
                        self.send(Command::RecognizeWavStt(path));
                    }
                }
                if ui.button("④″ 纯 STT 连续识别 WAV").clicked() {
                    let path = self.wav_path.trim().to_string();
                    if path.is_empty() {
                        self.push_log("[错误] 请填写 WAV 文件路径");
                    } else {
                        self.push_log(format!("→ 纯 STT 连续识别 WAV：{path}"));
                        self.send(Command::RecognizeWavSttContinuous(path));
                    }
                }
                if ui.button("④‴ 纯 STT 推流识别 WAV（push stream）").clicked() {
                    let path = self.wav_path.trim().to_string();
                    if path.is_empty() {
                        self.push_log("[错误] 请填写 WAV 文件路径");
                    } else {
                        self.push_log(format!("→ 纯 STT 推流识别 WAV：{path}"));
                        self.send(Command::RecognizeWavSttPushStream(path));
                    }
                }
                if ui.button("④⁗ 自动语言检测识别 WAV（zh-CN/en-US/ja-JP）").clicked() {
                    let path = self.wav_path.trim().to_string();
                    if path.is_empty() {
                        self.push_log("[错误] 请填写 WAV 文件路径");
                    } else {
                        self.push_log(format!("→ 自动语言检测识别 WAV：{path}"));
                        self.send(Command::RecognizeWavAutoDetect {
                            path,
                            candidates: vec![
                                "zh-CN".to_string(),
                                "en-US".to_string(),
                                "ja-JP".to_string(),
                            ],
                        });
                    }
                }
                if ui.button("④⁵ 短语列表识别 WAV（提升专有名词）").clicked() {
                    let path = self.wav_path.trim().to_string();
                    if path.is_empty() {
                        self.push_log("[错误] 请填写 WAV 文件路径");
                    } else {
                        self.push_log(format!("→ 短语列表识别 WAV：{path}"));
                        self.send(Command::RecognizeWavWithPhrases {
                            path,
                            phrases: vec![
                                "词边界".to_string(),
                                "口型".to_string(),
                                "自测".to_string(),
                            ],
                        });
                    }
                }
                if ui.button("④⁶ Connection 显式连接识别 WAV（open/close 事件）").clicked() {
                    let path = self.wav_path.trim().to_string();
                    if path.is_empty() {
                        self.push_log("[错误] 请填写 WAV 文件路径");
                    } else {
                        self.push_log(format!("→ Connection 识别 WAV：{path}"));
                        self.send(Command::RecognizeWavWithConnection(path));
                    }
                }
                if ui.button("④⁷ 发音评估 WAV（参考文本打分）").clicked() {
                    let path = self.wav_path.trim().to_string();
                    if path.is_empty() {
                        self.push_log("[错误] 请填写 WAV 文件路径");
                    } else {
                        self.push_log(format!("→ 发音评估 WAV：{path}"));
                        self.send(Command::AssessPronunciation {
                            path,
                            reference_text: "事件回调自测包含词边界与口型".to_string(),
                        });
                    }
                }

                ui.separator();
                ui.label("⑤ 语音合成（TTS）文本：");
                ui.text_edit_multiline(&mut self.tts_text);
                ui.label("输出 WAV 路径（留空则存到临时目录）：");
                ui.text_edit_singleline(&mut self.tts_out_path);
                if ui.button("⑤ 合成到 WAV 文件").clicked() {
                    let text = self.tts_text.trim().to_string();
                    if text.is_empty() {
                        self.push_log("[错误] 请填写要合成的文本");
                    } else {
                        let out = self.resolve_tts_out();
                        self.push_log(format!("→ TTS 合成到：{out}"));
                        self.send(Command::SynthesizeToWav { text, out_path: out });
                    }
                }
                if ui.button("⑤′ 异步合成到 WAV（speak_text_async）").clicked() {
                    let text = self.tts_text.trim().to_string();
                    if text.is_empty() {
                        self.push_log("[错误] 请填写要合成的文本");
                    } else {
                        let out = self.resolve_tts_out();
                        self.push_log(format!("→ TTS 异步合成到：{out}"));
                        self.send(Command::SynthesizeTextAsync { text, out_path: out });
                    }
                }
                if ui.button("⑤″ 合成并监听事件（词边界/viseme/书签）").clicked() {
                    let text = self.tts_text.trim().to_string();
                    if text.is_empty() {
                        self.push_log("[错误] 请填写要合成的文本");
                    } else {
                        let out = self.resolve_tts_out();
                        self.push_log("→ TTS 合成并监听事件…");
                        self.send(Command::SynthesizeWithEvents { text, out_path: out });
                    }
                }
                if ui.button("⑤⁗ 合成到扬声器（会发声）").clicked() {
                    let text = self.tts_text.trim().to_string();
                    if text.is_empty() {
                        self.push_log("[错误] 请填写要合成的文本");
                    } else {
                        self.push_log("→ TTS 合成到扬声器…");
                        self.send(Command::SynthesizeToSpeaker { text });
                    }
                }

                ui.separator();
                ui.label("⑤‴ SSML（含书签/强调，演示 SSML 合成）：");
                ui.text_edit_multiline(&mut self.tts_ssml);
                if ui.button("⑤‴ 合成 SSML 到 WAV").clicked() {
                    let ssml = self.tts_ssml.trim().to_string();
                    if ssml.is_empty() {
                        self.push_log("[错误] 请填写 SSML");
                    } else {
                        let out = self.resolve_tts_out();
                        self.push_log(format!("→ TTS 合成 SSML 到：{out}"));
                        self.send(Command::SynthesizeSsmlToWav { ssml, out_path: out });
                    }
                }

                ui.separator();
                ui.horizontal(|ui| {
                    ui.label("嗓音 locale：");
                    ui.text_edit_singleline(&mut self.voices_locale);
                });
                ui.small("留空表示全部；如 zh-CN、en-US");
                if ui.button("⑦ 查询嗓音列表").clicked() {
                    let locale = self.voices_locale.trim().to_string();
                    self.push_log("→ 查询嗓音列表…");
                    self.send(Command::ListVoices { locale });
                }

                ui.separator();
                if ui.button("⑥ 闭环自测（TTS → STT，无需麦克风）").clicked() {
                    self.push_log("→ 闭环自测…");
                    self.send(Command::SelfTestClosedLoop);
                }

                ui.separator();
                if ui.button("清空日志").clicked() {
                    self.logs.clear();
                }
            });

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.label("日志：");
            egui::ScrollArea::vertical()
                .auto_shrink([false, false])
                .stick_to_bottom(true)
                .show(ui, |ui| {
                    for line in &self.logs {
                        ui.monospace(line);
                    }
                });
        });
    }
}

fn main() -> eframe::Result<()> {
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    // 无界面自测：speech-test-ui --selftest [资源名或序号]
    // 依次执行 创建配置 → TTS 合成到 WAV → 纯 STT 识别该 WAV → 闭环自测，全部打印到 stdout。
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|a| a == "--selftest") {
        run_selftest(&args);
        return Ok(());
    }

    let native_options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default().with_inner_size([960.0, 620.0]),
        ..Default::default()
    };

    eframe::run_native(
        "Speech SDK FFI 测试",
        native_options,
        Box::new(|cc| {
            // 使用支持中文的字体
            install_cjk_font(&cc.egui_ctx);
            Ok(Box::new(App::new()))
        }),
    )
}

/// 无界面自测：speech-test-ui --selftest [资源名或序号]
fn run_selftest(args: &[String]) {
    let resources = match load_resources() {
        Ok(r) => r,
        Err(e) => {
            eprintln!("[错误] 读取资源失败：{e}");
            return;
        }
    };
    if resources.is_empty() {
        eprintln!("[错误] config.json 中没有 SpeechResources");
        return;
    }

    // 解析 --selftest 后面的可选参数：资源名或序号；缺省取第一个。
    let sel_arg = args
        .iter()
        .position(|a| a == "--selftest")
        .and_then(|i| args.get(i + 1))
        .map(|s| s.as_str());

    let idx = match sel_arg {
        None => 0,
        Some(s) => {
            if let Ok(n) = s.parse::<usize>() {
                n.min(resources.len() - 1)
            } else {
                resources.iter().position(|r| r.name == s).unwrap_or(0)
            }
        }
    };

    let res = resources[idx].clone();
    if res.subscription_key.is_empty() {
        eprintln!("[错误] 资源 {} 缺少密钥", res.name);
        return;
    }

    let tag = if is_china(&res) { "中国" } else { "国际" };
    println!(
        "==== 选用资源：{} [{}] {} ====",
        res.name, tag, res.service_region
    );

    let source_lang = "zh-CN".to_string();
    let create = Command::CreateConfig {
        key: res.subscription_key.clone(),
        region: res.service_region.clone(),
        host: if is_china(&res) {
            Some(china_host(&res.service_region))
        } else {
            None
        },
        source_lang: source_lang.clone(),
        target_langs: vec!["en".to_string()],
        is_china: is_china(&res),
    };

    // 临时 TTS 输出文件
    let mut wav = std::env::temp_dir();
    wav.push("tfp_selftest_tts.wav");
    let wav_path = wav.to_string_lossy().to_string();

    let commands = vec![
        create,
        Command::SynthesizeToWav {
            text: "你好，这是一次无界面自测。".to_string(),
            out_path: wav_path.clone(),
        },
        Command::SynthesizeSsmlToWav {
            ssml: "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'><voice name='zh-CN-XiaoxiaoNeural'>SSML 自测<bookmark mark='end'/>。</voice></speak>".to_string(),
            out_path: wav_path.clone(),
        },
        Command::SynthesizeTextAsync {
            text: "异步合成自测。".to_string(),
            out_path: wav_path.clone(),
        },
        Command::SynthesizeWithEvents {
            text: "事件回调自测，包含词边界与口型。".to_string(),
            out_path: wav_path.clone(),
        },
        Command::ListVoices {
            locale: "zh-CN".to_string(),
        },
        Command::RecognizeWavStt(wav_path.clone()),
        Command::RecognizeWavSttContinuous(wav_path.clone()),
        Command::RecognizeWavSttPushStream(wav_path.clone()),
        Command::RecognizeWavAutoDetect {
            path: wav_path.clone(),
            candidates: vec![
                "zh-CN".to_string(),
                "en-US".to_string(),
                "ja-JP".to_string(),
            ],
        },
        Command::RecognizeWavWithPhrases {
            path: wav_path.clone(),
            phrases: vec![
                "词边界".to_string(),
                "口型".to_string(),
                "自测".to_string(),
            ],
        },
        Command::RecognizeWavWithConnection(wav_path.clone()),
        Command::AssessPronunciation {
            path: wav_path.clone(),
            reference_text: "事件回调自测包含词边界与口型".to_string(),
        },
        Command::SelfTestClosedLoop,
    ];

    worker::run_headless(commands);
    let _ = std::fs::remove_file(&wav_path);
    println!("==== 自测结束 ====");
}

/// 返回本地时间 HH:MM:SS 字符串（UTC+8，仅用于日志时间戳）。
fn now_hms() -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    let secs = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let local = secs + 8 * 3600;
    let h = (local / 3600) % 24;
    let m = (local / 60) % 60;
    let s = local % 60;
    format!("{h:02}:{m:02}:{s:02}")
}

/// 加载系统中文字体（微软雅黑），避免中文显示为方块。
fn install_cjk_font(ctx: &egui::Context) {
    let candidates = [
        "C:/Windows/Fonts/msyh.ttc",
        "C:/Windows/Fonts/msyh.ttf",
        "C:/Windows/Fonts/simhei.ttf",
    ];
    for path in candidates {
        if let Ok(bytes) = std::fs::read(path) {
            let mut fonts = egui::FontDefinitions::default();
            fonts
                .font_data
                .insert("cjk".to_owned(), egui::FontData::from_owned(bytes));
            fonts
                .families
                .entry(egui::FontFamily::Proportional)
                .or_default()
                .insert(0, "cjk".to_owned());
            fonts
                .families
                .entry(egui::FontFamily::Monospace)
                .or_default()
                .insert(0, "cjk".to_owned());
            ctx.set_fonts(fonts);
            return;
        }
    }
}
