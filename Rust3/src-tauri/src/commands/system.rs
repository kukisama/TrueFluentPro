use std::path::PathBuf;

use tauri::{Manager, State};
use tfp_core::{BillingRecord, BillingSummary};

use crate::state::AppState;

#[derive(serde::Serialize)]
pub struct AppInfo {
    pub version: String,
    pub platform: String,
    pub arch: String,
    pub data_dir: String,
}

#[tauri::command]
pub async fn get_app_info(app: tauri::AppHandle) -> Result<AppInfo, String> {
    let data_dir = app
        .path()
        .app_data_dir()
        .map(|p| p.to_string_lossy().to_string())
        .map_err(|e| e.to_string())?;

    Ok(AppInfo {
        version: env!("CARGO_PKG_VERSION").to_string(),
        platform: std::env::consts::OS.to_string(),
        arch: std::env::consts::ARCH.to_string(),
        data_dir,
    })
}

// ── Sandboxed file I/O ──

/// Validate that the requested path is within the app data directory.
fn validate_sandboxed_path(app: &tauri::AppHandle, path: &str) -> Result<PathBuf, String> {
    let requested = PathBuf::from(path)
        .canonicalize()
        .or_else(|_| {
            let p = PathBuf::from(path);
            if let Some(parent) = p.parent() {
                std::fs::create_dir_all(parent).ok();
                parent
                    .canonicalize()
                    .map(|cp| cp.join(p.file_name().unwrap_or_default()))
            } else {
                Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidInput,
                    "invalid path",
                ))
            }
        })
        .map_err(|e| format!("Failed to resolve path: {e}"))?;

    let data_dir = app
        .path()
        .app_data_dir()
        .map_err(|e| format!("Cannot get app data dir: {e}"))?
        .canonicalize()
        .map_err(|e| format!("Cannot resolve data dir: {e}"))?;

    if !requested.starts_with(&data_dir) {
        return Err(format!(
            "Access denied: path '{}' is outside app data dir '{}'",
            requested.display(),
            data_dir.display()
        ));
    }
    Ok(requested)
}

#[tauri::command]
pub async fn write_text_file(
    app: tauri::AppHandle,
    path: String,
    content: String,
) -> Result<(), String> {
    let safe = validate_sandboxed_path(&app, &path)?;
    std::fs::write(&safe, &content).map_err(|e| format!("Failed to write file: {e}"))
}

#[tauri::command]
pub async fn read_text_file(
    app: tauri::AppHandle,
    path: String,
) -> Result<String, String> {
    let safe = validate_sandboxed_path(&app, &path)?;
    std::fs::read_to_string(&safe).map_err(|e| format!("Failed to read file: {e}"))
}

// ── Billing queries ──

#[tauri::command]
pub async fn get_billing_records(
    state: State<'_, AppState>,
    limit: Option<u32>,
) -> Result<Vec<BillingRecord>, String> {
    state
        .db
        .get_billing_records(limit.unwrap_or(100))
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_billing_summary(
    state: State<'_, AppState>,
) -> Result<BillingSummary, String> {
    state.db.get_billing_summary().await.map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_app_info_json_fields() {
        let info = AppInfo {
            version: "1.0.0".into(),
            platform: "windows".into(),
            arch: "x86_64".into(),
            data_dir: "C:\\data".into(),
        };
        let json: serde_json::Value = serde_json::to_value(&info).unwrap();
        let obj = json.as_object().unwrap();
        assert!(obj.contains_key("version"));
        assert!(obj.contains_key("platform"));
        assert!(obj.contains_key("arch"));
        assert!(obj.contains_key("data_dir"));
        assert_eq!(json["version"], "1.0.0");
        assert_eq!(json["platform"], "windows");
        assert_eq!(json["arch"], "x86_64");
        assert_eq!(json["data_dir"], "C:\\data");
    }
}
