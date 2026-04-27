use tauri::{Manager, State};

use crate::models::*;
use crate::state::AppState;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  系统信息命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

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

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  文件读写（沙箱保护）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn write_text_file(app: tauri::AppHandle, path: String, content: String) -> Result<(), String> {
    let safe = validate_sandboxed_path(&app, &path)?;
    std::fs::write(&safe, &content).map_err(|e| format!("写入文件失败: {e}"))
}

#[tauri::command]
pub async fn read_text_file(app: tauri::AppHandle, path: String) -> Result<String, String> {
    let safe = validate_sandboxed_path(&app, &path)?;
    std::fs::read_to_string(&safe).map_err(|e| format!("读取文件失败: {e}"))
}

/// B-01 修复: 沙箱校验 — 只允许 app_data_dir 范围内读写
fn validate_sandboxed_path(app: &tauri::AppHandle, path: &str) -> Result<std::path::PathBuf, String> {
    use std::path::PathBuf;
    let requested = PathBuf::from(path)
        .canonicalize()
        .or_else(|_| {
            let p = PathBuf::from(path);
            if let Some(parent) = p.parent() {
                std::fs::create_dir_all(parent).ok();
                parent.canonicalize().map(|cp| cp.join(p.file_name().unwrap_or_default()))
            } else {
                Err(std::io::Error::new(std::io::ErrorKind::InvalidInput, "无效路径"))
            }
        })
        .map_err(|e| format!("路径解析失败: {e}"))?;

    let data_dir = app.path().app_data_dir()
        .map_err(|e| format!("无法获取数据目录: {e}"))?
        .canonicalize()
        .map_err(|e| format!("数据目录解析失败: {e}"))?;

    if !requested.starts_with(&data_dir) {
        return Err(format!(
            "安全拒绝: 路径 '{}' 不在应用数据目录 '{}' 范围内",
            requested.display(),
            data_dir.display()
        ));
    }
    Ok(requested)
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P3.5: 计费查询
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn get_billing_records(
    state: State<'_, AppState>,
    limit: Option<u32>,
) -> Result<Vec<BillingRecord>, String> {
    state.db.get_billing_records(limit.unwrap_or(100)).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_billing_summary(
    state: State<'_, AppState>,
) -> Result<BillingSummary, String> {
    state.db.get_billing_summary().await.map_err(|e| e.to_string())
}
