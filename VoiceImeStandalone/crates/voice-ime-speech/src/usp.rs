//! 微软 Speech USP (Unified Speech Protocol) WebSocket 帧编解码与路由构建。
//!
//! 严格遵循微软开源 `cognitive-services-speech-sdk-js` 的协议规范：
//! - 支持单语种 STT (`/speech/recognition/conversation/cognitiveservices/v1`)；
//! - 支持自动多语种检测 Continuous LID (`/stt/speech/universal/v2`)；
//! - 支持实时语音翻译 (`/stt/speech/universal/v2` 与 `/speech/translation/cognitiveservices/v1`)；
//! - 同时兼容国际版 Global (`.microsoft.com`) 与世纪互联 21Vianet (`.azure.cn`)。

use chrono::{SecondsFormat, Utc};
use serde_json::{json, Value};
use url::Url;
use voice_ime_config::RuntimeSpeechSessionConfig;

pub const CONTINUOUS_LID_LANGUAGES: &[&str] = &[
    "zh-CN", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES",
];

/// 将 UI 选择的目标语言规范化为微软 Speech Translation 服务标准目标语言代码。
pub fn normalize_translation_target_language(code: &str) -> String {
    let trimmed = code.trim();
    match trimmed.to_ascii_lowercase().as_str() {
        "zh-cn" | "zh-hans" | "zh-sg" => "zh-Hans".to_string(),
        "zh-tw" | "zh-hant" => "zh-Hant".to_string(),
        "zh-hk" | "yue" | "yue-cn" => "yue".to_string(),
        "en-us" | "en-gb" | "en" => "en".to_string(),
        "ja-jp" | "ja" => "ja".to_string(),
        "ko-kr" | "ko" => "ko".to_string(),
        "fr-fr" | "fr" => "fr".to_string(),
        "de-de" | "de" => "de".to_string(),
        "es-es" | "es" => "es".to_string(),
        "ru-ru" | "ru" => "ru".to_string(),
        _ => trimmed
            .split('-')
            .next()
            .unwrap_or("en")
            .to_string(),
    }
}

/// 根据会话快照构建目标 WebSocket URL（同时兼容 Global、21Vianet 与本地回环测试）。
pub fn build_speech_ws_url(cfg: &RuntimeSpeechSessionConfig) -> Result<String, String> {
    let is_auto = cfg.recognition_language.eq_ignore_ascii_case("auto");

    if cfg.enable_translation {
        let target = normalize_translation_target_language(&cfg.target_language);
        if is_auto {
            // 自动源语言 + 实时翻译：走 Universal v2 协议
            let mut u = Url::parse(&cfg.endpoint.universal_v2_ws_url)
                .map_err(|e| format!("构建语音翻译 URL 失败：{e}"))?;
            u.query_pairs_mut().append_pair("format", "simple").append_pair("lidEnabled", "true")
                .append_pair("from", "en-US").append_pair("to", &target).append_pair("scenario", "conversation");
            return Ok(u.to_string());
        } else {
            // 显式源语言 + 实时翻译：走标准 s2s translation v1 协议（全球与 21V 全区域通用）
            let mut u = Url::parse(&cfg.endpoint.translation_v1_ws_url)
                .map_err(|e| format!("构建语音翻译 URL 失败：{e}"))?;
            u.query_pairs_mut()
                .append_pair("from", &cfg.recognition_language)
                .append_pair("to", &target)
                .append_pair("EndSilenceTimeoutMs", &cfg.end_silence_ms.to_string())
                .append_pair("InitialSilenceTimeoutMs", "300000");
            return Ok(u.to_string());
        }
    }

    if is_auto {
        // 自动多语种连续识别 Continuous LID：走 Universal v2
        let mut u = Url::parse(&cfg.endpoint.universal_v2_ws_url)
            .map_err(|e| format!("构建自动语种识别 URL 失败：{e}"))?;
        u.query_pairs_mut().append_pair("format", "simple").append_pair("lidEnabled", "true");
        Ok(u.to_string())
    } else {
        // 指定语种实时识别：走标准 STT v1 conversation
        let mut u = Url::parse(&cfg.endpoint.stt_v1_ws_url)
            .map_err(|e| format!("构建语音识别 URL 失败：{e}"))?;
        u.query_pairs_mut()
            .append_pair("language", &cfg.recognition_language)
            .append_pair("format", "simple")
            .append_pair("EndSilenceTimeoutMs", &cfg.end_silence_ms.to_string())
            .append_pair("InitialSilenceTimeoutMs", "300000");
        Ok(u.to_string())
    }
}

pub fn now_usp_timestamp() -> String {
    Utc::now().to_rfc3339_opts(SecondsFormat::Millis, true)
}

/// 编码 USP 文本帧（`speech.config` / `speech.context`）。
pub fn format_usp_text_message(path: &str, request_id: &str, json_body: &Value) -> String {
    let ts = now_usp_timestamp();
    let body = serde_json::to_string(json_body).unwrap_or_else(|_| "{}".to_string());
    format!(
        "Path: {path}\r\nX-RequestId: {request_id}\r\nX-Timestamp: {ts}\r\nContent-Type: application/json; charset=utf-8\r\n\r\n{body}"
    )
}

/// 编码 USP 二进制音频帧（2 字节大端头部长度 + ASCII 头部 + 音频字节）。
pub fn format_usp_audio_message(
    request_id: &str,
    content_type: &str,
    audio_payload: &[u8],
) -> Vec<u8> {
    let ts = now_usp_timestamp();
    let header = format!(
        "Path: audio\r\nX-RequestId: {request_id}\r\nX-Timestamp: {ts}\r\nContent-Type: {content_type}\r\n"
    );
    let header_bytes = header.as_bytes();
    let header_len = header_bytes.len() as u16;
    let mut frame = Vec::with_capacity(2 + header_bytes.len() + audio_payload.len());
    frame.extend_from_slice(&header_len.to_be_bytes());
    frame.extend_from_slice(header_bytes);
    frame.extend_from_slice(audio_payload);
    frame
}

/// 构建 16kHz / 16-bit / 单声道流式 WAV RIFF 44 字节头（流长度置 0 表示无限流式）。
pub fn build_pcm16_16khz_mono_wav_header() -> [u8; 44] {
    let sample_rate: u32 = 16_000;
    let channels: u16 = 1;
    let bits_per_sample: u16 = 16;
    let byte_rate: u32 = sample_rate * (channels as u32) * (bits_per_sample as u32 / 8);
    let block_align: u16 = channels * (bits_per_sample / 8);

    let mut h = [0u8; 44];
    h[0..4].copy_from_slice(b"RIFF");
    h[4..8].copy_from_slice(&0u32.to_le_bytes());
    h[8..12].copy_from_slice(b"WAVE");
    h[12..16].copy_from_slice(b"fmt ");
    h[16..20].copy_from_slice(&16u32.to_le_bytes());
    h[20..22].copy_from_slice(&1u16.to_le_bytes()); // PCM = 1
    h[22..24].copy_from_slice(&channels.to_le_bytes());
    h[24..28].copy_from_slice(&sample_rate.to_le_bytes());
    h[28..32].copy_from_slice(&byte_rate.to_le_bytes());
    h[32..34].copy_from_slice(&block_align.to_le_bytes());
    h[34..36].copy_from_slice(&bits_per_sample.to_le_bytes());
    h[36..40].copy_from_slice(b"data");
    h[40..44].copy_from_slice(&0u32.to_le_bytes());
    h
}

/// 构建 `speech.config` 系统元信息 JSON。
pub fn build_speech_config_json() -> Value {
    json!({
        "context": {
            "system": {
                "name": "SpeechSDK",
                "version": "1.48.2",
                "build": "Rust-Standalone",
                "lang": "Rust"
            },
            "os": {
                "platform": std::env::consts::OS,
                "name": "Windows",
                "version": "11"
            },
            "audio": {
                "source": {
                    "type": "Microphones",
                    "samplerate": "16000",
                    "bitspersample": "16",
                    "channelcount": "1"
                }
            }
        }
    })
}

/// 构建 `speech.context` 识别/语种检测/翻译上下文 JSON。
pub fn build_speech_context_json(cfg: &RuntimeSpeechSessionConfig) -> Value {
    let is_auto = cfg.recognition_language.eq_ignore_ascii_case("auto");
    let mut ctx = serde_json::Map::new();

    ctx.insert(
        "phraseDetection".into(),
        json!({
            "initialSilenceTimeout": 300_000,
            "trailingSilenceTimeout": cfg.end_silence_ms,
            "mode": "Conversation"
        }),
    );

    ctx.insert(
        "phraseOutput".into(),
        json!({
            "interimResults": { "resultType": "Auto" },
            "phraseResults": { "resultType": "Always" }
        }),
    );

    if is_auto {
        ctx.insert(
            "languageId".into(),
            json!({
                "languages": CONTINUOUS_LID_LANGUAGES,
                "onSuccess": { "action": "Recognize" },
                "onUnknown": { "action": "None" },
                "mode": "DetectContinuous",
                "priority": "PrioritizeLatency"
            }),
        );
    }

    if cfg.enable_translation {
        let target = normalize_translation_target_language(&cfg.target_language);
        ctx.insert(
            "translation".into(),
            json!({
                "targetLanguages": [target],
                "onPassthrough": { "action": "None" },
                "onSuccess": { "action": "None" },
                "output": { "includePassThroughResults": true, "interimResults": { "mode": "Always" } }
            }),
        );
        if let Some(phrase) = ctx.get_mut("phraseDetection") {
            phrase["onSuccess"] = json!({ "action": "Translate" });
            phrase["onInterim"] = json!({ "action": "Translate" });
        }
        if is_auto {
            ctx.insert("phraseOutput".into(), json!({
                "interimResults": { "resultType": "None" },
                "phraseResults": { "resultType": "None" }
            }));
        }
    }

    Value::Object(ctx)
}

/// 解析服务端下发的 USP 文本帧，返回 `(path_lowercase, json_body)`。
pub fn parse_usp_incoming_text(raw: &str) -> Option<(String, Value)> {
    let (header_block, body) = raw
        .split_once("\r\n\r\n")
        .or_else(|| raw.split_once("\n\n"))?;
    let mut path = String::new();
    for line in header_block.lines() {
        if let Some((k, v)) = line.split_once(':') {
            if k.trim().eq_ignore_ascii_case("Path") {
                path = v.trim().to_ascii_lowercase();
                break;
            }
        }
    }
    if path.is_empty() {
        return None;
    }
    let json_val = if body.trim().is_empty() {
        Value::Null
    } else {
        serde_json::from_str::<Value>(body).ok()?
    };
    Some((path, json_val))
}

/// 从识别/翻译响应 JSON 中解析出的语义事件。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ParsedUspEvent {
    TurnStarted,
    TurnEnded,
    /// STT 模式识别结果（`is_final`: false=hypothesis, true=phrase）
    Recognition {
        text: String,
        is_final: bool,
    },
    /// 翻译模式结果（包含原文 `source_text` 与目标语译文 `translated_text`）
    Translation {
        source_text: String,
        translated_text: Option<String>,
        is_final: bool,
    },
    Ignored,
}

pub fn interpret_usp_message(
    path: &str,
    body: &Value,
    translation_mode: bool,
    target_lang: &str,
) -> ParsedUspEvent {
    match path {
        "turn.start" => ParsedUspEvent::TurnStarted,
        "turn.end" => ParsedUspEvent::TurnEnded,
        "speech.hypothesis" | "speech.fragment" => {
            let text = extract_text(body);
            if translation_mode {
                ParsedUspEvent::Translation {
                    source_text: text,
                    translated_text: None,
                    is_final: false,
                }
            } else {
                ParsedUspEvent::Recognition {
                    text,
                    is_final: false,
                }
            }
        }
        "speech.phrase" => {
            if !successful_status(body, "RecognitionStatus") {
                return ParsedUspEvent::Ignored;
            }
            let text = extract_phrase_display_text(body);
            if translation_mode {
                let translated = extract_translation_text(body, target_lang);
                ParsedUspEvent::Translation {
                    source_text: text,
                    translated_text: translated,
                    is_final: true,
                }
            } else {
                ParsedUspEvent::Recognition {
                    text,
                    is_final: true,
                }
            }
        }
        "translation.hypothesis" if translation_mode => {
            let source_text = extract_text(body);
            let translated_text = extract_translation_text(body, target_lang);
            ParsedUspEvent::Translation {
                source_text,
                translated_text,
                is_final: false,
            }
        }
        "translation.response" if translation_mode => {
            if let Some(phrase) = body.get("SpeechPhrase").filter(|v| v.is_object()) {
                if !successful_status(phrase, "RecognitionStatus") {
                    return ParsedUspEvent::Ignored;
                }
                return ParsedUspEvent::Translation {
                    source_text: extract_phrase_display_text(phrase),
                    translated_text: extract_translation_text(body, target_lang),
                    is_final: true,
                };
            }
            if let Some(hypothesis) = body.get("SpeechHypothesis").filter(|v| v.is_object()) {
                return ParsedUspEvent::Translation {
                    source_text: extract_text(hypothesis),
                    translated_text: extract_translation_text(body, target_lang),
                    is_final: false,
                };
            }
            ParsedUspEvent::Ignored
        }
        "translation.phrase" if translation_mode => {
            if !successful_status(body, "RecognitionStatus") {
                return ParsedUspEvent::Ignored;
            }
            let source_text = extract_phrase_display_text(body);
            let translated_text = extract_translation_text(body, target_lang);
            ParsedUspEvent::Translation {
                source_text,
                translated_text,
                is_final: true,
            }
        }
        _ => ParsedUspEvent::Ignored,
    }
}

fn extract_text(v: &Value) -> String {
    for key in ["Text", "DisplayText", "text"] {
        if let Some(s) = v.get(key).and_then(|x| x.as_str()) {
            return s.to_string();
        }
    }
    String::new()
}

fn extract_phrase_display_text(v: &Value) -> String {
    if let Some(s) = v.get("DisplayText").and_then(|x| x.as_str()) {
        if !s.is_empty() {
            return s.to_string();
        }
    }
    if let Some(s) = v
        .pointer("/NBest/0/Display")
        .and_then(|x| x.as_str())
    {
        if !s.is_empty() {
            return s.to_string();
        }
    }
    extract_text(v)
}

fn extract_translation_text(v: &Value, expected_target: &str) -> Option<String> {
    let norm_target = normalize_translation_target_language(expected_target);
    let result = v.get("Translation").unwrap_or(v);
    if !successful_status(result, "TranslationStatus") { return None; }
    let arr = result.get("Translations")?.as_array()?;
    for item in arr {
        let lang = item.get("Language").and_then(Value::as_str).unwrap_or_default();
        if lang.eq_ignore_ascii_case(&norm_target) || lang.eq_ignore_ascii_case(expected_target) {
            let text = extract_text(item);
            if !text.trim().is_empty() { return Some(text); }
        }
    }
    None
}

fn successful_status(v: &Value, key: &str) -> bool {
    match v.get(key) {
        None => true,
        Some(Value::String(s)) => s.eq_ignore_ascii_case("Success"),
        Some(Value::Number(n)) => n.as_u64() == Some(0),
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_stt_and_translation_usp_frames() {
        let raw_hyp = "Path: speech.hypothesis\r\nX-RequestId: abc\r\nContent-Type: application/json\r\n\r\n{\"Text\":\"正在说话\"}";
        let (path, body) = parse_usp_incoming_text(raw_hyp).unwrap();
        assert_eq!(
            interpret_usp_message(&path, &body, false, "en-US"),
            ParsedUspEvent::Recognition {
                text: "正在说话".into(),
                is_final: false
            }
        );

        let raw_trans = "Path: translation.phrase\r\nX-RequestId: abc\r\n\r\n{\"RecognitionStatus\":\"Success\",\"Text\":\"你好世界\",\"Translation\":{\"Translations\":[{\"Language\":\"en\",\"Text\":\"Hello world.\"}]}}";
        let (t_path, t_body) = parse_usp_incoming_text(raw_trans).unwrap();
        assert_eq!(
            interpret_usp_message(&t_path, &t_body, true, "en-US"),
            ParsedUspEvent::Translation {
                source_text: "你好世界".into(),
                translated_text: Some("Hello world.".into()),
                is_final: true
            }
        );
        let v2 = json!({"SpeechPhrase": {"RecognitionStatus":"Success", "DisplayText":"你好"},
            "TranslationStatus":"Success", "Translations":[{"Language":"en","DisplayText":"Hello."}]});
        assert_eq!(interpret_usp_message("translation.response", &v2, true, "en-US"),
            ParsedUspEvent::Translation { source_text: "你好".into(), translated_text: Some("Hello.".into()), is_final: true });
        assert_eq!(interpret_usp_message("translation.response", &v2, true, "ja-JP"),
            ParsedUspEvent::Translation { source_text: "你好".into(), translated_text: None, is_final: true });
    }
}
