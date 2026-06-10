//! 前端可调用的 Tauri 命令。
//!
//! 第一阶段提供配置读写与 UI 状态；翻译 / 听析 / 任务命令在后续阶段补充。

use tauri::State;
use tfp_core::config::{Language, Theme};
use tfp_core::speech_resource::new_resource_id;
use tfp_core::storage::{self, TranslationRecord};
use tfp_core::{AiEndpoint, AppConfig, AudioDeviceInfo, AudioSettings, SpeechResource};

use crate::audio_devices;
use crate::live::{LiveTranslator, StartParams};
use crate::recorder::Recorder;
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

/// 厂商资料包概览（创建终结点时的「选择厂商」选择器用）。
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EndpointProfileSummary {
    pub id: String,
    pub vendor: String,
    pub kind: String,
    pub display_name: String,
    pub subtitle: String,
    pub glyph: String,
    pub summary: String,
    pub icon_asset_path: String,
}

/// 列出内置厂商资料包（对齐 C# 模板选择器）。
#[tauri::command]
pub fn list_endpoint_profiles(state: State<'_, AppState>) -> CmdResult<Vec<EndpointProfileSummary>> {
    Ok(state
        .profile_catalog
        .all()
        .iter()
        .map(|p| EndpointProfileSummary {
            id: p.id.clone(),
            vendor: p.vendor.clone(),
            kind: p.endpoint_type.display_name().to_string(),
            display_name: p.display_name.clone(),
            subtitle: p.subtitle.clone(),
            glyph: p.glyph.clone(),
            summary: p.summary.clone(),
            icon_asset_path: p.icon_asset_path.clone(),
        })
        .collect())
}

/// 列出全部 AI 终结点（完整对象，供终结点配置选项卡 CRUD 用）。
#[tauri::command]
pub fn list_ai_endpoints(state: State<'_, AppState>) -> CmdResult<Vec<AiEndpoint>> {
    let cfg = state.config.lock().map_err(|e| e.to_string())?;
    Ok(cfg.endpoints.clone())
}

/// 按厂商资料包创建一个「套好默认模板」的新终结点（未落盘，供前端编辑）。
///
/// 对齐 C# 创建终结点流程：选厂商 → `ApplyTemplate` 注入默认认证/协议/版本 → 用户填地址密钥。
#[tauri::command]
pub fn build_endpoint_from_profile(
    state: State<'_, AppState>,
    profile_id: String,
) -> CmdResult<AiEndpoint> {
    let profile = state
        .profile_catalog
        .find(&profile_id)
        .ok_or_else(|| format!("未找到厂商资料包：{profile_id}"))?;
    let mut endpoint = AiEndpoint {
        id: new_resource_id(),
        name: profile.default_name_prefix.clone(),
        ..AiEndpoint::default()
    };
    profile.apply_to(&mut endpoint);
    Ok(endpoint)
}

/// 新增或更新 AI 终结点（按 id upsert；id 为空时自动生成）。
#[tauri::command]
pub fn save_ai_endpoint(
    state: State<'_, AppState>,
    mut endpoint: AiEndpoint,
) -> CmdResult<AiEndpoint> {
    if endpoint.id.trim().is_empty() {
        endpoint.id = new_resource_id();
    }
    let mut cfg = state.config.lock().map_err(|e| e.to_string())?;
    if let Some(slot) = cfg.endpoints.iter_mut().find(|e| e.id == endpoint.id) {
        *slot = endpoint.clone();
    } else {
        cfg.endpoints.push(endpoint.clone());
    }
    let snapshot = cfg.clone();
    drop(cfg);
    snapshot.save().map_err(|e| e.to_string())?;
    Ok(endpoint)
}

/// 删除 AI 终结点。
#[tauri::command]
pub fn delete_ai_endpoint(state: State<'_, AppState>, id: String) -> CmdResult<()> {
    let mut cfg = state.config.lock().map_err(|e| e.to_string())?;
    cfg.endpoints.retain(|e| e.id != id);
    let snapshot = cfg.clone();
    drop(cfg);
    snapshot.save().map_err(|e| e.to_string())
}

/// 对指定 AI 终结点执行连通性测试（仅文字，无需消费端）。
///
/// 复刻 C# `EndpointBatchTestService` 的核心：按资料包声明的 URL 候选发起极短文本请求，
/// 验证地址/鉴权/路由是否打通。返回逐项结果供「测试结果窗体」展示。
#[tauri::command]
pub async fn test_ai_endpoint(
    state: State<'_, AppState>,
    id: String,
) -> CmdResult<Vec<tfp_core::EndpointTestItem>> {
    // 先在锁内克隆出终结点与资料包，再 await（避免持锁跨越异步边界）。
    let (endpoint, profile) = {
        let cfg = state.config.lock().map_err(|e| e.to_string())?;
        let endpoint = cfg
            .endpoints
            .iter()
            .find(|e| e.id == id)
            .cloned()
            .ok_or_else(|| format!("未找到终结点：{id}"))?;
        let profile = state.profile_catalog.find(&endpoint.profile_id).cloned();
        (endpoint, profile)
    };

    Ok(tfp_core::test_endpoint_text(&endpoint, profile.as_ref()).await)
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
        input_device_name: match cfg.audio.source_mode {
            tfp_core::AudioSourceMode::CaptureDevice => {
                cfg.audio.selected_input_device_id.clone()
            }
            _ => String::new(),
        },
        use_loopback: matches!(cfg.audio.source_mode, tfp_core::AudioSourceMode::Loopback),
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

// ───────────────────── 音频设备 ─────────────────────

/// 列出输入设备（麦克风）。
#[tauri::command]
pub fn list_audio_input_devices() -> CmdResult<Vec<AudioDeviceInfo>> {
    Ok(audio_devices::list_input_devices())
}

/// 列出输出设备（扬声器，供回环参考）。
#[tauri::command]
pub fn list_audio_output_devices() -> CmdResult<Vec<AudioDeviceInfo>> {
    Ok(audio_devices::list_output_devices())
}

/// 当前平台是否支持系统回环采集（前端据此置灰）。
#[tauri::command]
pub fn audio_loopback_supported() -> CmdResult<bool> {
    Ok(audio_devices::loopback_supported())
}

/// 读取音频设置。
#[tauri::command]
pub fn get_audio_settings(state: State<'_, AppState>) -> CmdResult<AudioSettings> {
    let cfg = state.config.lock().map_err(|e| e.to_string())?;
    Ok(cfg.audio.clone())
}

/// 保存音频设置。
#[tauri::command]
pub fn set_audio_settings(state: State<'_, AppState>, audio: AudioSettings) -> CmdResult<()> {
    let mut cfg = state.config.lock().map_err(|e| e.to_string())?;
    cfg.audio = audio;
    let snapshot = cfg.clone();
    drop(cfg);
    snapshot.save().map_err(|e| e.to_string())
}

// ───────────────────── 独立录音机（不依赖云识别） ─────────────────────

/// 开始录音：按当前音频设置里的录音配置，把系统回环写入 `path` 指定的文件。
#[tauri::command]
pub fn start_recording(
    state: State<'_, AppState>,
    recorder: State<'_, Recorder>,
    path: String,
) -> CmdResult<()> {
    let settings = {
        let cfg = state.config.lock().map_err(|e| e.to_string())?;
        cfg.audio.recorder.clone()
    };
    recorder.start(std::path::PathBuf::from(path), settings)
}

/// 停止录音，返回输出文件路径。
#[tauri::command]
pub fn stop_recording(recorder: State<'_, Recorder>) -> CmdResult<String> {
    recorder.stop()
}

/// 是否正在录音。
#[tauri::command]
pub fn is_recording(recorder: State<'_, Recorder>) -> CmdResult<bool> {
    Ok(recorder.is_recording())
}
