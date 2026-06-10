//! 前端可调用的 Tauri 命令。
//!
//! 第一阶段提供配置读写与 UI 状态；翻译 / 听析 / 任务命令在后续阶段补充。

use tauri::State;
use tfp_core::config::{Language, Theme};
use tfp_core::speech_resource::new_resource_id;
use tfp_core::storage::{self, TranslationRecord};
use tfp_core::{AppConfig, SpeechResource};

use crate::live::{LiveTranslator, StartParams};
use crate::state::AppState;

/// 命令统一错误：转成字符串返回前端。
type CmdResult<T> = std::result::Result<T, String>;

/// 读取当前配置。
#[tauri::command]
pub fn load_config(state: State<'_, AppState>) -> CmdResult<AppConfig> {
    let cfg = state.config.lock().map_err(|e| e.to_string())?;
    Ok(cfg.clone())
}

/// 保存配置（写入内存状态并持久化到磁盘）。
#[tauri::command]
pub fn save_config(state: State<'_, AppState>, config: AppConfig) -> CmdResult<()> {
    config.save().map_err(|e| e.to_string())?;
    let mut guard = state.config.lock().map_err(|e| e.to_string())?;
    *guard = config;
    Ok(())
}

/// 仅更新主题与语言（界面切换的轻量命令）。
#[tauri::command]
pub fn set_ui_prefs(
    state: State<'_, AppState>,
    theme: Theme,
    language: Language,
) -> CmdResult<()> {
    let mut guard = state.config.lock().map_err(|e| e.to_string())?;
    guard.general.theme = theme;
    guard.general.language = language;
    let snapshot = guard.clone();
    drop(guard);
    snapshot.save().map_err(|e| e.to_string())?;
    Ok(())
}

/// 列出启用的终结点（精简信息，供下拉/概览）。
#[tauri::command]
pub fn list_endpoints(state: State<'_, AppState>) -> CmdResult<Vec<EndpointSummary>> {
    let cfg = state.config.lock().map_err(|e| e.to_string())?;
    Ok(cfg
        .endpoints
        .iter()
        .map(|e| EndpointSummary {
            id: e.id.clone(),
            name: e.name.clone(),
            kind: e.kind.display_name().to_string(),
            is_enabled: e.is_enabled,
            model_count: e.models.len(),
        })
        .collect())
}

/// 终结点概览（前端列表用）。
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EndpointSummary {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub is_enabled: bool,
    pub model_count: usize,
}

// ───────────────────────── 实时翻译 ─────────────────────────

/// 启动实时翻译（读取当前激活语音资源 + 源/目标语言）。
#[tauri::command]
pub fn start_live_translation(
    state: State<'_, AppState>,
    live: State<'_, LiveTranslator>,
) -> CmdResult<()> {
    let cfg = state.config.lock().map_err(|e| e.to_string())?;
    let res = cfg
        .active_speech_resource()
        .ok_or_else(|| "未配置语音资源，请先在「设置」中添加 Microsoft 语音密钥".to_string())?;
    if !res.is_valid() {
        return Err("语音资源凭据不完整（需要密钥 + 区域或终结点）".into());
    }
    let params = StartParams {
        key: res.subscription_key.clone(),
        region: res.effective_region().unwrap_or_default(),
        endpoint: res.endpoint.clone(),
        is_china: res.is_china(),
        source_lang: cfg.general.default_source_lang.clone(),
        target_lang: cfg.general.default_target_lang.clone(),
        filter_modal_particles: cfg.general.filter_modal_particles,
    };
    drop(cfg);
    live.start(params)
}

/// 停止实时翻译。
#[tauri::command]
pub fn stop_live_translation(live: State<'_, LiveTranslator>) -> CmdResult<()> {
    live.stop()
}

/// 查询是否正在识别。
#[tauri::command]
pub fn is_live_running(live: State<'_, LiveTranslator>) -> CmdResult<bool> {
    Ok(live.is_running())
}

// ───────────────────────── 翻译历史 ─────────────────────────

/// 列出翻译历史（默认最近 50 条）。
#[tauri::command]
pub fn list_translation_history(
    limit: Option<i64>,
    offset: Option<i64>,
) -> CmdResult<Vec<TranslationRecord>> {
    let conn = storage::open_connection().map_err(|e| e.to_string())?;
    storage::list_translations(&conn, limit.unwrap_or(50), offset.unwrap_or(0))
        .map_err(|e| e.to_string())
}

/// 清空翻译历史（软删除）。
#[tauri::command]
pub fn clear_translation_history() -> CmdResult<usize> {
    let conn = storage::open_connection().map_err(|e| e.to_string())?;
    storage::clear_translations(&conn).map_err(|e| e.to_string())
}

// ───────────────────────── 语音资源配置 ─────────────────────────

/// 列出全部语音资源。
#[tauri::command]
pub fn list_speech_resources(state: State<'_, AppState>) -> CmdResult<Vec<SpeechResource>> {
    let cfg = state.config.lock().map_err(|e| e.to_string())?;
    Ok(cfg.speech_resources.clone())
}

/// 当前激活的语音资源 ID（无则空串）。
#[tauri::command]
pub fn active_speech_resource_id(state: State<'_, AppState>) -> CmdResult<String> {
    let cfg = state.config.lock().map_err(|e| e.to_string())?;
    Ok(cfg
        .active_speech_resource()
        .map(|r| r.id.clone())
        .unwrap_or_default())
}

/// 新增或更新语音资源（按 id upsert；id 为空时自动生成）。
#[tauri::command]
pub fn save_speech_resource(
    state: State<'_, AppState>,
    mut resource: SpeechResource,
) -> CmdResult<SpeechResource> {
    if resource.id.trim().is_empty() {
        resource.id = new_resource_id();
    }
    let mut cfg = state.config.lock().map_err(|e| e.to_string())?;
    if let Some(slot) = cfg.speech_resources.iter_mut().find(|r| r.id == resource.id) {
        *slot = resource.clone();
    } else {
        cfg.speech_resources.push(resource.clone());
    }
    if cfg.active_speech_resource_id.is_empty() {
        cfg.active_speech_resource_id = resource.id.clone();
    }
    let snapshot = cfg.clone();
    drop(cfg);
    snapshot.save().map_err(|e| e.to_string())?;
    Ok(resource)
}

/// 删除语音资源。
#[tauri::command]
pub fn delete_speech_resource(state: State<'_, AppState>, id: String) -> CmdResult<()> {
    let mut cfg = state.config.lock().map_err(|e| e.to_string())?;
    cfg.speech_resources.retain(|r| r.id != id);
    if cfg.active_speech_resource_id == id {
        cfg.active_speech_resource_id = cfg
            .speech_resources
            .first()
            .map(|r| r.id.clone())
            .unwrap_or_default();
    }
    let snapshot = cfg.clone();
    drop(cfg);
    snapshot.save().map_err(|e| e.to_string())
}

/// 设置激活语音资源。
#[tauri::command]
pub fn set_active_speech_resource(state: State<'_, AppState>, id: String) -> CmdResult<()> {
    let mut cfg = state.config.lock().map_err(|e| e.to_string())?;
    if cfg.speech_resource(&id).is_none() {
        return Err("指定的语音资源不存在".into());
    }
    cfg.active_speech_resource_id = id;
    let snapshot = cfg.clone();
    drop(cfg);
    snapshot.save().map_err(|e| e.to_string())
}

/// 设置实时翻译的源/目标语言。
#[tauri::command]
pub fn set_languages(state: State<'_, AppState>, source: String, target: String) -> CmdResult<()> {
    let mut cfg = state.config.lock().map_err(|e| e.to_string())?;
    cfg.general.default_source_lang = source;
    cfg.general.default_target_lang = target;
    let snapshot = cfg.clone();
    drop(cfg);
    snapshot.save().map_err(|e| e.to_string())
}
