use std::sync::Arc;
use std::sync::atomic::{AtomicI64, Ordering};
use tauri::{Emitter, State};

use crate::models::*;
use crate::state::AppState;
use crate::providers::ProviderInfo;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  翻译命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn translate_text(
    state: State<'_, AppState>,
    request: TranslateRequest,
) -> Result<TranslateResponse, String> {
    let providers = state.providers.read().await;
    let provider_id = request
        .endpoint_id
        .as_deref()
        .unwrap_or("default");
    let provider = providers
        .get_text_translation(provider_id)
        .ok_or_else(|| format!("未找到翻译 Provider: {provider_id}"))?;

    provider
        .translate(&request)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn start_realtime_translation(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    mut config: RealtimeSessionConfig,
) -> Result<String, String> {
    // S-1: 如果前端未传入超时参数，从全局 RecognitionSettings 填充
    {
        let app_config = state.config.read().await;
        if config.initial_silence_timeout_seconds.is_none() {
            config.initial_silence_timeout_seconds = Some(app_config.recognition.initial_silence_timeout_seconds);
        }
        if config.end_silence_timeout_seconds.is_none() {
            config.end_silence_timeout_seconds = Some(app_config.recognition.end_silence_timeout_seconds);
        }
    }

    let providers = state.providers.read().await;
    let provider = providers
        .get_realtime_speech(&config.endpoint_id)
        .ok_or_else(|| {
            format!(
                "未找到实时语音翻译 Provider: {}。请在设置中添加 Azure Speech 类型端点。",
                config.endpoint_id
            )
        })?;

    let (mut rx, handle) = provider
        .create_session(&config)
        .await
        .map_err(|e| e.to_string())?;

    let session_id = uuid::Uuid::new_v4().to_string();
    let sid = session_id.clone();

    // 创建翻译会话记录
    let db = state.db.clone();
    let target_langs_json = serde_json::to_string(&config.target_langs).unwrap_or_default();
    let ts_session = TranslationSession {
        id: sid.clone(),
        started_at: chrono::Utc::now().format("%Y-%m-%d %H:%M:%S").to_string(),
        stopped_at: None,
        source_lang: config.source_lang.clone(),
        target_langs: target_langs_json,
        provider: config.endpoint_id.clone(),
        status: "active".to_string(),
    };
    db.live_create_session(&ts_session).await.map_err(|e| e.to_string())?;

    {
        let mut sessions = state.active_speech_sessions.write().await;
        sessions.insert(sid.clone(), handle);
    }

    // 获取当前最大 sequence
    let max_seq = db.live_get_max_sequence(&sid).await.unwrap_or(0);
    let sequence_counter = Arc::new(AtomicI64::new(max_seq));

    // 获取识别设置（用于语气词过滤）
    let recognition = {
        let cfg = state.config.read().await;
        cfg.recognition.clone()
    };

    // S-3: no_response_restart 配置
    let no_response_restart_enabled = recognition.enable_no_response_restart;
    let no_response_restart_secs = recognition.no_response_restart_seconds as u64;

    let db_for_spawn = db.clone();
    let sid_for_spawn = sid.clone();
    let app_for_subtitle = app.clone();
    tauri::async_runtime::spawn(async move {
        let timeout_duration = std::time::Duration::from_secs(
            if no_response_restart_enabled && no_response_restart_secs > 0 {
                no_response_restart_secs
            } else {
                // 超大值，等同于禁用
                u64::MAX / 2
            }
        );

        loop {
            let event = if no_response_restart_enabled {
                match tokio::time::timeout(timeout_duration, rx.recv()).await {
                    Ok(Some(event)) => event,
                    Ok(None) => break, // channel 关闭
                    Err(_) => {
                        // S-3: 超时 — 发射重连标记通知前端
                        let _ = app.emit("realtime-event", &RealtimeEvent::SessionStopped {
                            session_id: format!("no_response_restart:{}", sid_for_spawn),
                        });
                        // 继续等待下一条事件（SDK 可能自行恢复）
                        continue;
                    }
                }
            } else {
                match rx.recv().await {
                    Some(event) => event,
                    None => break,
                }
            };

            // 对 final 事件（Recognized / Translated）落库
            if let Some(seg) = extract_final_segment(
                &event, &sid_for_spawn, &sequence_counter, &recognition,
            ) {
                // PR-3: 同步推送到字幕悬浮窗
                super::floating::emit_subtitle_update(
                    &app_for_subtitle,
                    &seg.original_text,
                    &seg.translated_text,
                );
                let _ = db_for_spawn.live_insert_segment(&seg).await;
            }
            let _ = app.emit("realtime-event", &event);
        }
    });

    Ok(sid)
}

/// 从 RealtimeEvent 中提取 final segment（仅 Recognized / Translated 类型）
fn extract_final_segment(
    event: &RealtimeEvent,
    session_id: &str,
    sequence_counter: &Arc<AtomicI64>,
    recognition: &RecognitionSettings,
) -> Option<TranslationSegment> {
    match event {
        RealtimeEvent::Recognized { text, .. } => {
            let original = text.clone();
            if original.is_empty() {
                return None;
            }
            let original = if recognition.filter_modal_particles {
                filter_modal_particles(&original)
            } else {
                original
            };
            if original.trim().is_empty() {
                return None;
            }
            let seq = sequence_counter.fetch_add(1, Ordering::SeqCst) + 1;
            let now = chrono::Utc::now().format("%Y-%m-%d %H:%M:%S").to_string();
            Some(TranslationSegment {
                id: uuid::Uuid::new_v4().to_string(),
                session_id: session_id.to_string(),
                sequence: seq,
                original_text: original,
                translated_text: String::new(),
                target_lang: String::new(),
                started_at: Some(now.clone()),
                ended_at: Some(now),
                is_bookmarked: false,
                bookmark_note: None,
                audio_path: None,
                raw_event_json: serde_json::to_string(event).ok(),
            })
        }
        RealtimeEvent::Translated { source_text, translations } => {
            if source_text.is_empty() && translations.is_empty() {
                return None;
            }
            let original = if recognition.filter_modal_particles {
                filter_modal_particles(source_text)
            } else {
                source_text.clone()
            };
            // 取第一个翻译结果
            let (target_lang, translated_text) = translations.iter().next()
                .map(|(k, v)| (k.clone(), v.clone()))
                .unwrap_or_default();
            if original.trim().is_empty() && translated_text.trim().is_empty() {
                return None;
            }
            let seq = sequence_counter.fetch_add(1, Ordering::SeqCst) + 1;
            let now = chrono::Utc::now().format("%Y-%m-%d %H:%M:%S").to_string();
            Some(TranslationSegment {
                id: uuid::Uuid::new_v4().to_string(),
                session_id: session_id.to_string(),
                sequence: seq,
                original_text: original,
                translated_text,
                target_lang,
                started_at: Some(now.clone()),
                ended_at: Some(now),
                is_bookmarked: false,
                bookmark_note: None,
                audio_path: None,
                raw_event_json: serde_json::to_string(event).ok(),
            })
        }
        _ => None,
    }
}

/// 语气词过滤（对齐 C# ModalParticleFillers 列表）
fn filter_modal_particles(text: &str) -> String {
    static FILLERS: &[&str] = &[
        "啊", "呀", "吧", "啦", "嘛", "呢", "哦", "呐", "哈", "呵", "嗯", "唉", "哎",
        "那个", "这个", "就是", "然后", "就是说", "怎么说", "你知道", "对吧", "是吧",
        "呃", "额", "嗯嗯", "啊啊", "哦哦",
    ];
    let mut result = text.to_string();
    for filler in FILLERS {
        // 移除句首的语气词（后跟逗号或空格或无后续）
        let start_pattern = format!("{}，", filler);
        if result.starts_with(&start_pattern) {
            result = result[start_pattern.len()..].to_string();
        } else if result.starts_with(filler) && result.len() == filler.len() {
            result.clear();
        }
        // 移除句末的语气词
        let end_pattern = format!("，{}", filler);
        if result.ends_with(&end_pattern) {
            let new_len = result.len() - end_pattern.len();
            result.truncate(new_len);
        } else if result.ends_with(filler) {
            let new_len = result.len() - filler.len();
            result.truncate(new_len);
        }
    }
    result.trim().to_string()
}

#[tauri::command]
pub async fn stop_realtime_translation(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<(), String> {
    let handle = {
        let mut sessions = state.active_speech_sessions.write().await;
        sessions.remove(&session_id)
    };

    if let Some(h) = handle {
        h.stop().await.map_err(|e| e.to_string())?;
    }

    // 标记会话为已停止
    let now = chrono::Utc::now().format("%Y-%m-%d %H:%M:%S").to_string();
    state.db
        .live_stop_session(&session_id, &now)
        .await
        .map_err(|e| e.to_string())?;

    Ok(())
}

/// O-34: 返回支持的语言列表（保留旧接口兼容）
#[tauri::command]
pub async fn get_supported_languages() -> Result<Vec<(String, String)>, String> {
    Ok(vec![
        ("zh-Hans".into(), "中文（简体）".into()),
        ("zh-Hant".into(), "中文（繁體）".into()),
        ("en".into(), "English".into()),
        ("ja".into(), "日本語".into()),
        ("ko".into(), "한국어".into()),
        ("fr".into(), "Français".into()),
        ("de".into(), "Deutsch".into()),
        ("es".into(), "Español".into()),
        ("ru".into(), "Русский".into()),
        ("pt".into(), "Português".into()),
        ("it".into(), "Italiano".into()),
        ("ar".into(), "العربية".into()),
        ("hi".into(), "हिन्दी".into()),
        ("th".into(), "ภาษาไทย".into()),
        ("vi".into(), "Tiếng Việt".into()),
    ])
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Provider 查询命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn list_providers(state: State<'_, AppState>) -> Result<Vec<ProviderInfo>, String> {
    let providers = state.providers.read().await;
    Ok(providers.list_providers())
}
