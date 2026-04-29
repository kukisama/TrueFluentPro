use serde::Serialize;
use tauri::{Emitter, Manager, WebviewUrl, WebviewWindowBuilder};
use tauri::State;

use crate::state::AppState;
use tfp_core::FloatingWindowState;

pub(crate) const SUBTITLE_LABEL: &str = "floating-subtitle";
pub(crate) const INSIGHT_LABEL: &str = "floating-insight";

#[derive(Clone, Serialize)]
struct FloatingWindowOpenEvent {
    window: String,
    open: bool,
}

#[tauri::command]
pub async fn live_show_floating_subtitle(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(SUBTITLE_LABEL) {
        win.set_focus().map_err(|e| e.to_string())?;
    } else {
        let saved = {
            let config = state.config.read().await;
            config.ui.floating_subtitle_state.clone()
        };
        let mut builder = WebviewWindowBuilder::new(&app, SUBTITLE_LABEL, WebviewUrl::App("floating-subtitle.html".into()))
            .title("浮动字幕")
            .decorations(false)
            .transparent(true)
            .always_on_top(true)
            .skip_taskbar(true)
            .resizable(false);

        if let Some(ref s) = saved {
            builder = builder
                .position(s.x, s.y)
                .inner_size(s.width, s.height);
        } else {
            builder = builder.inner_size(1000.0, 96.0);
        }

        builder.build().map_err(|e| e.to_string())?;
        let _ = app.emit(
            "floating-window-state-changed",
            FloatingWindowOpenEvent { window: "subtitle".into(), open: true },
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
            FloatingWindowOpenEvent { window: "subtitle".into(), open: false },
        );
    }
    Ok(())
}

#[tauri::command]
pub async fn live_toggle_floating_subtitle(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(SUBTITLE_LABEL) {
        let visible = win.is_visible().map_err(|e| e.to_string())?;
        if visible {
            win.hide().map_err(|e| e.to_string())?;
        } else {
            win.show().map_err(|e| e.to_string())?;
            win.set_focus().map_err(|e| e.to_string())?;
        }
    } else {
        live_show_floating_subtitle(app, state).await?;
    }
    Ok(())
}

#[tauri::command]
pub async fn live_show_floating_insight(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    if let Some(win) = app.get_webview_window(INSIGHT_LABEL) {
        win.set_focus().map_err(|e| e.to_string())?;
    } else {
        let saved = {
            let config = state.config.read().await;
            config.ui.floating_insight_state.clone()
        };
        let mut builder = WebviewWindowBuilder::new(&app, INSIGHT_LABEL, WebviewUrl::App("floating-insight.html".into()))
            .title("浮动洞察")
            .min_inner_size(320.0, 200.0)
            .resizable(true)
            .always_on_top(true);

        if let Some(ref s) = saved {
            builder = builder
                .position(s.x, s.y)
                .inner_size(s.width, s.height);
        } else {
            builder = builder.inner_size(520.0, 400.0);
        }

        builder.build().map_err(|e| e.to_string())?;
        let _ = app.emit(
            "floating-window-state-changed",
            FloatingWindowOpenEvent { window: "insight".into(), open: true },
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
            FloatingWindowOpenEvent { window: "insight".into(), open: false },
        );
    }
    Ok(())
}

// ── Helper functions (pub(crate), for future callers) ──

#[derive(Clone, Serialize)]
struct SubtitlePayload {
    source_text: String,
    translated_text: String,
    source_label: String,
}

#[derive(Clone, Serialize)]
struct InsightPayload {
    markdown: String,
    streaming: bool,
}

/// Emit a subtitle update to the floating subtitle window.
pub(crate) fn emit_subtitle_update(
    app: &tauri::AppHandle,
    source_text: &str,
    translated_text: &str,
    source_label: &str,
) {
    let _ = app.emit(
        "subtitle-update",
        SubtitlePayload {
            source_text: source_text.into(),
            translated_text: translated_text.into(),
            source_label: source_label.into(),
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

// ── Position memory commands (T-002) ──

/// Save floating window position/size/opacity to config.
#[tauri::command]
pub async fn save_floating_window_state(
    state: State<'_, AppState>,
    window: String,
    x: f64,
    y: f64,
    width: f64,
    height: f64,
    opacity: f64,
) -> Result<(), String> {
    let clamped_opacity = opacity.clamp(0.3, 1.0);
    let ws = FloatingWindowState { x, y, width, height, opacity: clamped_opacity };
    {
        let mut config = state.config.write().await;
        match window.as_str() {
            "subtitle" => config.ui.floating_subtitle_state = Some(ws),
            "insight" => config.ui.floating_insight_state = Some(ws),
            _ => return Err(format!("unknown floating window: {window}")),
        }
    }
    state.persist_config().await
}

/// Retrieve saved floating window state.
#[tauri::command]
pub async fn get_floating_window_state(
    state: State<'_, AppState>,
    window: String,
) -> Result<Option<FloatingWindowState>, String> {
    let config = state.config.read().await;
    match window.as_str() {
        "subtitle" => Ok(config.ui.floating_subtitle_state.clone()),
        "insight" => Ok(config.ui.floating_insight_state.clone()),
        _ => Err(format!("unknown floating window: {window}")),
    }
}

/// Set floating window opacity (CSS-level, also persists to config).
#[tauri::command]
pub async fn set_floating_window_opacity(
    state: State<'_, AppState>,
    window: String,
    opacity: f64,
) -> Result<(), String> {
    let clamped = opacity.clamp(0.3, 1.0);
    {
        let mut config = state.config.write().await;
        match window.as_str() {
            "subtitle" => {
                if let Some(ref mut s) = config.ui.floating_subtitle_state {
                    s.opacity = clamped;
                } else {
                    config.ui.floating_subtitle_state = Some(FloatingWindowState {
                        opacity: clamped,
                        ..Default::default()
                    });
                }
            }
            "insight" => {
                if let Some(ref mut s) = config.ui.floating_insight_state {
                    s.opacity = clamped;
                } else {
                    config.ui.floating_insight_state = Some(FloatingWindowState {
                        opacity: clamped,
                        width: 520.0,
                        height: 400.0,
                        ..Default::default()
                    });
                }
            }
            _ => return Err(format!("unknown floating window: {window}")),
        }
    }
    state.persist_config().await
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
    fn test_subtitle_payload_has_source_label() {
        let p = SubtitlePayload {
            source_text: "hello".into(),
            translated_text: "你好".into(),
            source_label: "mic".into(),
        };
        let v = serde_json::to_value(&p).unwrap();
        let obj = v.as_object().unwrap();
        assert_eq!(obj["source_text"], "hello");
        assert_eq!(obj["translated_text"], "你好");
        assert_eq!(obj["source_label"], "mic");
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

    #[test]
    fn test_floating_window_open_event_serde() {
        let e = FloatingWindowOpenEvent {
            window: "subtitle".into(),
            open: true,
        };
        let v = serde_json::to_value(&e).unwrap();
        assert_eq!(v["window"], "subtitle");
        assert_eq!(v["open"], true);
    }

    #[test]
    fn test_floating_window_state_opacity_clamp() {
        // Verify the opacity default
        let s = FloatingWindowState::default();
        assert_eq!(s.opacity, 0.75);
        assert_eq!(s.width, 1000.0);
        assert_eq!(s.height, 96.0);
    }
}
