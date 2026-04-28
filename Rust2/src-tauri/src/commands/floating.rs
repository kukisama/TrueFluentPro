//! PR-3: 悬浮窗口管理命令
//!
//! 字幕悬浮窗 + 洞察悬浮窗 — Tauri WebviewWindow 子窗口
//! 对照 C#: FloatingSubtitleManager.cs / FloatingInsightManager.cs

use tauri::{AppHandle, Manager, Emitter};
use tauri::webview::WebviewWindowBuilder;

const SUBTITLE_LABEL: &str = "floating-subtitle";
const INSIGHT_LABEL: &str = "floating-insight";

/// 显示字幕悬浮窗（已存在则 focus）
#[tauri::command]
pub async fn live_show_floating_subtitle(app: AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(SUBTITLE_LABEL) {
        win.show().map_err(|e| e.to_string())?;
        win.set_focus().map_err(|e| e.to_string())?;
        let _ = app.emit("floating-window-state-changed", serde_json::json!({
            "window": "subtitle", "open": true
        }));
        return Ok(());
    }
    // 创建子窗口
    let url = tauri::WebviewUrl::App("floating-subtitle.html".into());
    let _win = WebviewWindowBuilder::new(&app, SUBTITLE_LABEL, url)
        .title("浮动字幕")
        .inner_size(1000.0, 96.0)
        .min_inner_size(400.0, 80.0)
        .decorations(false)
        .transparent(true)
        .always_on_top(true)
        .skip_taskbar(true)
        .resizable(false)
        .build()
        .map_err(|e| e.to_string())?;

    let _ = app.emit("floating-window-state-changed", serde_json::json!({
        "window": "subtitle", "open": true
    }));
    Ok(())
}

/// 隐藏字幕悬浮窗
#[tauri::command]
pub async fn live_hide_floating_subtitle(app: AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(SUBTITLE_LABEL) {
        win.close().map_err(|e| e.to_string())?;
    }
    let _ = app.emit("floating-window-state-changed", serde_json::json!({
        "window": "subtitle", "open": false
    }));
    Ok(())
}

/// 切换字幕悬浮窗
#[tauri::command]
pub async fn live_toggle_floating_subtitle(app: AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(SUBTITLE_LABEL) {
        if win.is_visible().unwrap_or(false) {
            live_hide_floating_subtitle(app).await
        } else {
            win.show().map_err(|e| e.to_string())?;
            win.set_focus().map_err(|e| e.to_string())?;
            let _ = app.emit("floating-window-state-changed", serde_json::json!({
                "window": "subtitle", "open": true
            }));
            Ok(())
        }
    } else {
        live_show_floating_subtitle(app).await
    }
}

/// 显示洞察悬浮窗（已存在则 focus）
#[tauri::command]
pub async fn live_show_floating_insight(app: AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(INSIGHT_LABEL) {
        win.show().map_err(|e| e.to_string())?;
        win.set_focus().map_err(|e| e.to_string())?;
        let _ = app.emit("floating-window-state-changed", serde_json::json!({
            "window": "insight", "open": true
        }));
        return Ok(());
    }
    let url = tauri::WebviewUrl::App("floating-insight.html".into());
    let _win = WebviewWindowBuilder::new(&app, INSIGHT_LABEL, url)
        .title("浮动洞察")
        .inner_size(520.0, 400.0)
        .min_inner_size(320.0, 200.0)
        .decorations(false)
        .transparent(true)
        .always_on_top(true)
        .skip_taskbar(true)
        .resizable(true)
        .build()
        .map_err(|e| e.to_string())?;

    let _ = app.emit("floating-window-state-changed", serde_json::json!({
        "window": "insight", "open": true
    }));
    Ok(())
}

/// 隐藏洞察悬浮窗
#[tauri::command]
pub async fn live_hide_floating_insight(app: AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(INSIGHT_LABEL) {
        win.close().map_err(|e| e.to_string())?;
    }
    let _ = app.emit("floating-window-state-changed", serde_json::json!({
        "window": "insight", "open": false
    }));
    Ok(())
}

/// 更新字幕内容（由 translate.rs 的事件循环在收到 final segment 时调用）
pub fn emit_subtitle_update(app: &AppHandle, source_text: &str, translated_text: &str) {
    let _ = app.emit("subtitle-update", serde_json::json!({
        "source_text": source_text,
        "translated_text": translated_text,
        "source_label": "🔊 全部字幕",
    }));
}

/// 更新洞察内容（流式）
#[allow(dead_code)]
pub fn emit_insight_update(app: &AppHandle, markdown: &str, streaming: bool) {
    let _ = app.emit("insight-update", serde_json::json!({
        "markdown": markdown,
        "streaming": streaming,
    }));
}
