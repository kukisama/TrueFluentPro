use serde::Serialize;
use tauri::{Emitter, Manager, WebviewUrl, WebviewWindowBuilder};

pub(crate) const SUBTITLE_LABEL: &str = "floating-subtitle";
pub(crate) const INSIGHT_LABEL: &str = "floating-insight";

#[derive(Clone, Serialize)]
struct FloatingWindowState {
    window: String,
    open: bool,
}

#[tauri::command]
pub async fn live_show_floating_subtitle(app: tauri::AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(SUBTITLE_LABEL) {
        win.set_focus().map_err(|e| e.to_string())?;
    } else {
        WebviewWindowBuilder::new(&app, SUBTITLE_LABEL, WebviewUrl::App("floating-subtitle.html".into()))
            .title("浮动字幕")
            .inner_size(1000.0, 96.0)
            .decorations(false)
            .transparent(true)
            .always_on_top(true)
            .skip_taskbar(true)
            .resizable(false)
            .build()
            .map_err(|e| e.to_string())?;
        let _ = app.emit(
            "floating-window-state-changed",
            FloatingWindowState { window: "subtitle".into(), open: true },
        );
    }
    Ok(())
}

#[tauri::command]
pub async fn live_hide_floating_subtitle(app: tauri::AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(SUBTITLE_LABEL) {
        win.close().map_err(|e| e.to_string())?;
        let _ = app.emit(
            "floating-window-state-changed",
            FloatingWindowState { window: "subtitle".into(), open: false },
        );
    }
    Ok(())
}

#[tauri::command]
pub async fn live_toggle_floating_subtitle(app: tauri::AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(SUBTITLE_LABEL) {
        let visible = win.is_visible().map_err(|e| e.to_string())?;
        if visible {
            win.hide().map_err(|e| e.to_string())?;
        } else {
            win.show().map_err(|e| e.to_string())?;
            win.set_focus().map_err(|e| e.to_string())?;
        }
    } else {
        live_show_floating_subtitle(app).await?;
    }
    Ok(())
}

#[tauri::command]
pub async fn live_show_floating_insight(app: tauri::AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(INSIGHT_LABEL) {
        win.set_focus().map_err(|e| e.to_string())?;
    } else {
        WebviewWindowBuilder::new(&app, INSIGHT_LABEL, WebviewUrl::App("floating-insight.html".into()))
            .title("浮动洞察")
            .inner_size(520.0, 400.0)
            .min_inner_size(320.0, 200.0)
            .resizable(true)
            .always_on_top(true)
            .build()
            .map_err(|e| e.to_string())?;
        let _ = app.emit(
            "floating-window-state-changed",
            FloatingWindowState { window: "insight".into(), open: true },
        );
    }
    Ok(())
}

#[tauri::command]
pub async fn live_hide_floating_insight(app: tauri::AppHandle) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(INSIGHT_LABEL) {
        win.close().map_err(|e| e.to_string())?;
        let _ = app.emit(
            "floating-window-state-changed",
            FloatingWindowState { window: "insight".into(), open: false },
        );
    }
    Ok(())
}

// ── Helper functions (pub(crate), for future callers) ──

#[derive(Clone, Serialize)]
struct SubtitlePayload {
    source_text: String,
    translated_text: String,
}

#[derive(Clone, Serialize)]
struct InsightPayload {
    markdown: String,
    streaming: bool,
}

pub(crate) fn emit_subtitle_update(
    app: &tauri::AppHandle,
    source_text: &str,
    translated_text: &str,
) {
    let _ = app.emit(
        "subtitle-update",
        SubtitlePayload {
            source_text: source_text.into(),
            translated_text: translated_text.into(),
        },
    );
}

#[allow(dead_code)]
pub(crate) fn emit_insight_update(
    app: &tauri::AppHandle,
    markdown: &str,
    streaming: bool,
) {
    let _ = app.emit(
        "insight-update",
        InsightPayload {
            markdown: markdown.into(),
            streaming,
        },
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_window_label_constants() {
        assert_eq!(SUBTITLE_LABEL, "floating-subtitle");
        assert_eq!(INSIGHT_LABEL, "floating-insight");
    }

    #[test]
    fn test_subtitle_payload_serde() {
        let p = SubtitlePayload {
            source_text: "hello".into(),
            translated_text: "你好".into(),
        };
        let v = serde_json::to_value(&p).unwrap();
        let obj = v.as_object().unwrap();
        assert_eq!(obj["source_text"], "hello");
        assert_eq!(obj["translated_text"], "你好");
    }

    #[test]
    fn test_insight_payload_serde() {
        let p = InsightPayload {
            markdown: "# Title".into(),
            streaming: true,
        };
        let v = serde_json::to_value(&p).unwrap();
        let obj = v.as_object().unwrap();
        assert_eq!(obj["markdown"], "# Title");
        assert_eq!(obj["streaming"], true);
    }
}
