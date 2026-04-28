use tauri::{Emitter, State};

use crate::models::*;
use crate::state::AppState;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  实时翻译命令（PR-1: 数据层 + 会话管理）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 获取当前活跃的翻译会话（F5 刷新恢复用）
#[tauri::command]
pub async fn live_get_active_session(
    state: State<'_, AppState>,
) -> Result<Option<TranslationSession>, String> {
    state.db
        .live_get_active_session()
        .await
        .map_err(|e| e.to_string())
}

/// 获取最近 N 条翻译片段（刷新恢复 + 分页加载）
#[tauri::command]
pub async fn live_get_recent_segments(
    state: State<'_, AppState>,
    session_id: String,
    limit: Option<u32>,
) -> Result<Vec<TranslationSegment>, String> {
    let limit = limit.unwrap_or(200);
    state.db
        .live_get_recent_segments(&session_id, limit)
        .await
        .map_err(|e| e.to_string())
}

/// 给翻译片段添加书签
#[tauri::command]
pub async fn live_bookmark_segment(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    segment_id: String,
    note: Option<String>,
) -> Result<(), String> {
    state.db
        .live_bookmark_segment(&segment_id, note.as_deref())
        .await
        .map_err(|e| e.to_string())?;
    let _ = app.emit("segment-updated", serde_json::json!({
        "segment_id": segment_id,
        "is_bookmarked": true,
        "bookmark_note": note,
    }));
    Ok(())
}

/// 取消翻译片段的书签
#[tauri::command]
pub async fn live_unbookmark_segment(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    segment_id: String,
) -> Result<(), String> {
    state.db
        .live_unbookmark_segment(&segment_id)
        .await
        .map_err(|e| e.to_string())?;
    let _ = app.emit("segment-updated", serde_json::json!({
        "segment_id": segment_id,
        "is_bookmarked": false,
        "bookmark_note": null,
    }));
    Ok(())
}

/// 返回支持的语言列表（按 provider 类型派生）
///
/// Azure Speech SDK 支持的翻译语言子集（来源: C# SpeechTranslationService.AutoDetectSourceLanguages
/// + Azure Speech 服务文档 https://learn.microsoft.com/azure/ai-services/speech-service/language-support）
#[tauri::command]
pub async fn live_list_supported_languages(
    provider: String,
) -> Result<Vec<SupportedLanguage>, String> {
    // 根据 provider 类型返回不同语言列表
    // azure_speech: Speech SDK 翻译支持的语言
    // openai_realtime: OpenAI Realtime API 支持的语言
    // 默认: azure_speech 列表
    let langs = match provider.as_str() {
        "openai_realtime" => openai_realtime_languages(),
        _ => azure_speech_languages(),
    };
    Ok(langs)
}

/// Azure Speech SDK 翻译支持的语言列表
/// 精简为中英日韩 + auto（对齐 C# ConfigViewModel.SourceLanguages）
fn azure_speech_languages() -> Vec<SupportedLanguage> {
    vec![
        // 源语言（语音识别）使用 locale 格式
        sl("auto", "自动检测", "source"),
        sl("zh-CN", "中文（简体）", "source"),
        sl("en-US", "English", "source"),
        sl("ja-JP", "日本語", "source"),
        sl("ko-KR", "한국어", "source"),
        // 目标语言（翻译输出）使用 BCP-47 short code
        sl("zh-Hans", "中文（简体）", "target"),
        sl("en", "English", "target"),
        sl("ja", "日本語", "target"),
        sl("ko", "한국어", "target"),
    ]
}

/// OpenAI Realtime API 支持的语言（精简为中英日韩）
fn openai_realtime_languages() -> Vec<SupportedLanguage> {
    vec![
        sl("auto", "自动检测", "source"),
        sl("zh", "中文", "both"),
        sl("en", "English", "both"),
        sl("ja", "日本語", "both"),
        sl("ko", "한국어", "both"),
    ]
}

fn sl(code: &str, label: &str, kind: &str) -> SupportedLanguage {
    SupportedLanguage {
        code: code.to_string(),
        label: label.to_string(),
        kind: kind.to_string(),
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  PR-4: 历史会话浏览 + 字幕导出 + 清空
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 列出历史翻译会话（分页）
#[tauri::command]
pub async fn live_list_sessions(
    state: State<'_, AppState>,
    limit: Option<u32>,
    offset: Option<u32>,
) -> Result<Vec<TranslationSession>, String> {
    let limit = limit.unwrap_or(50);
    let offset = offset.unwrap_or(0);
    state.db
        .live_list_sessions(limit, offset)
        .await
        .map_err(|e| e.to_string())
}

/// 获取指定会话的所有片段（历史回看）
#[tauri::command]
pub async fn live_get_session_segments(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<Vec<TranslationSegment>, String> {
    // 复用 recent_segments 但取全部 (limit=99999)
    state.db
        .live_get_recent_segments(&session_id, 99999)
        .await
        .map_err(|e| e.to_string())
}

/// 导出字幕文件（SRT 或 VTT 格式）
#[tauri::command]
pub async fn live_export_subtitles(
    state: State<'_, AppState>,
    session_id: String,
    format: String,
    include_translation: bool,
    output_path: String,
) -> Result<String, String> {
    let segments = state.db
        .live_get_recent_segments(&session_id, 99999)
        .await
        .map_err(|e| e.to_string())?;

    if segments.is_empty() {
        return Err("没有可导出的片段".to_string());
    }

    let content = match format.as_str() {
        "vtt" => build_vtt(&segments, include_translation),
        _ => build_srt(&segments, include_translation),
    };

    std::fs::write(&output_path, &content)
        .map_err(|e| format!("写入文件失败: {e}"))?;

    Ok(output_path)
}

/// 清空指定会话的所有片段
#[tauri::command]
pub async fn live_clear_session_segments(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<(), String> {
    state.db
        .live_clear_session_segments(&session_id)
        .await
        .map_err(|e| e.to_string())
}

// ── 字幕格式化 ──

fn build_srt(segments: &[TranslationSegment], include_translation: bool) -> String {
    let mut out = String::new();
    for (i, seg) in segments.iter().enumerate() {
        let idx = i + 1;
        let start = segment_timestamp_srt(seg, false);
        let end = segment_timestamp_srt(seg, true);
        out.push_str(&format!("{idx}\n{start} --> {end}\n"));
        out.push_str(&seg.original_text);
        out.push('\n');
        if include_translation && !seg.translated_text.is_empty() {
            out.push_str(&seg.translated_text);
            out.push('\n');
        }
        out.push('\n');
    }
    out
}

fn build_vtt(segments: &[TranslationSegment], include_translation: bool) -> String {
    let mut out = "WEBVTT\n\n".to_string();
    for seg in segments {
        let start = segment_timestamp_vtt(seg, false);
        let end = segment_timestamp_vtt(seg, true);
        out.push_str(&format!("{start} --> {end}\n"));
        out.push_str(&seg.original_text);
        out.push('\n');
        if include_translation && !seg.translated_text.is_empty() {
            out.push_str(&seg.translated_text);
            out.push('\n');
        }
        out.push('\n');
    }
    out
}

/// 将 segment 的 started_at / ended_at 转为 SRT 时间戳 (HH:MM:SS,mmm)
fn segment_timestamp_srt(seg: &TranslationSegment, use_end: bool) -> String {
    let ts_str = if use_end {
        seg.ended_at.as_deref().or(seg.started_at.as_deref()).unwrap_or("00:00:00")
    } else {
        seg.started_at.as_deref().unwrap_or("00:00:00")
    };
    // 尝试解析 "YYYY-MM-DD HH:MM:SS" 格式
    if let Some(time_part) = ts_str.split(' ').last() {
        if time_part.contains(':') {
            return format!("{time_part},000");
        }
    }
    format!("{ts_str},000")
}

/// 将 segment 的 started_at / ended_at 转为 VTT 时间戳 (HH:MM:SS.mmm)
fn segment_timestamp_vtt(seg: &TranslationSegment, use_end: bool) -> String {
    segment_timestamp_srt(seg, use_end).replace(',', ".")
}
