//! Tauri IPC 命令薄壳（所有业务逻辑委托给 `voice-ime-controller`，保持单一职责）。

use serde::Serialize;
use voice_ime_config::{LanguageDraft, LegacyImportSummary};
use voice_ime_controller::install_ctrl::{query_installation_status, run_installation_action};
use voice_ime_controller::language_ctrl::{
    load_language_snapshot, save_language_draft, LanguagePageSnapshot,
};
use voice_ime_controller::settings_ctrl::{
    delete_subscription, import_legacy_subscriptions, load_settings_snapshot,
    preview_speech_endpoint, save_general_settings as ctrl_save_general, test_subscription,
    upsert_subscription, EndpointPreviewDto, GeneralSettingsUpdate, SettingsPageSnapshot,
    UpsertSubscriptionInput,
};
use voice_ime_controller::usage_ctrl::{
    load_session_text_page as ctrl_session_text, load_usage_report as ctrl_usage_report,
};
use voice_ime_controller::ToolsAppContext;
use voice_ime_speech::SubscriptionValidationOutcome;
use voice_ime_storage::{SessionTextPage, UsageReport};
use voice_ime_win32::{InstallOperationResult, InstallStateSummary};

pub struct ManagedToolsState {
    pub initial_tab: String,
    pub ctx: ToolsAppContext,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LegacyImportResponse {
    pub summary: LegacyImportSummary,
    pub snapshot: SettingsPageSnapshot,
}

#[cfg(not(target_os = "linux"))]
use tauri::State;

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub fn get_initial_tab(state: State<'_, ManagedToolsState>) -> String {
    state.initial_tab.clone()
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub fn load_settings_page(state: State<'_, ManagedToolsState>) -> Result<SettingsPageSnapshot, String> {
    load_settings_snapshot(&state.ctx.config_store)
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn list_input_devices() -> Result<Vec<voice_ime_speech::AudioDeviceItem>, String> {
    tokio::task::spawn_blocking(voice_ime_speech::list_microphone_devices)
        .await.map_err(|_| "麦克风设备枚举异常中断。".to_string())
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub fn preview_endpoint_url(endpoint: String) -> EndpointPreviewDto {
    preview_speech_endpoint(&endpoint)
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn save_general_settings(
    state: State<'_, ManagedToolsState>,
    update: GeneralSettingsUpdate,
) -> Result<SettingsPageSnapshot, String> {
    let store = state.ctx.config_store.clone();
    run_blocking(move || ctrl_save_general(&store, update)).await
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn upsert_speech_subscription(
    state: State<'_, ManagedToolsState>,
    input: UpsertSubscriptionInput,
) -> Result<SettingsPageSnapshot, String> {
    let store = state.ctx.config_store.clone();
    run_blocking(move || upsert_subscription(&store, input)).await
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn delete_speech_subscription(
    state: State<'_, ManagedToolsState>,
    subscription_id: String,
) -> Result<SettingsPageSnapshot, String> {
    let store = state.ctx.config_store.clone();
    run_blocking(move || delete_subscription(&store, &subscription_id)).await
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn test_speech_subscription(
    state: State<'_, ManagedToolsState>,
    saved_id: Option<String>,
    endpoint: String,
    subscription_key: String,
) -> Result<SubscriptionValidationOutcome, String> {
    test_subscription(
        &state.ctx.config_store,
        state.ctx.sts_cache(),
        saved_id,
        endpoint,
        subscription_key,
    )
    .await
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn import_legacy_truefluent_subscriptions(
    state: State<'_, ManagedToolsState>,
) -> Result<LegacyImportResponse, String> {
    let store = state.ctx.config_store.clone();
    let (summary, snapshot) = run_blocking(move || import_legacy_subscriptions(&store)).await?;
    Ok(LegacyImportResponse { summary, snapshot })
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub fn load_language_page(state: State<'_, ManagedToolsState>) -> Result<LanguagePageSnapshot, String> {
    load_language_snapshot(&state.ctx.config_store)
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn save_language_page(
    state: State<'_, ManagedToolsState>,
    draft: LanguageDraft,
) -> Result<LanguagePageSnapshot, String> {
    let store = state.ctx.config_store.clone();
    run_blocking(move || save_language_draft(&store, draft)).await
}

#[cfg(not(target_os = "linux"))]
async fn run_blocking<T: Send + 'static>(operation: impl FnOnce() -> Result<T, String> + Send + 'static) -> Result<T, String> {
    tokio::task::spawn_blocking(operation).await.map_err(|_| "配置操作异常中断。".to_string())?
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn load_usage_report(
    state: State<'_, ManagedToolsState>,
    days: u32,
) -> Result<UsageReport, String> {
    let svc = state.ctx.report_service.clone();
    tokio::task::spawn_blocking(move || ctrl_usage_report(&svc, days))
        .await
        .map_err(|_| "统计读取任务异常中断。".to_string())?
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn load_session_original_text(
    state: State<'_, ManagedToolsState>,
    session_id: String,
    after_sequence: i64,
) -> Result<SessionTextPage, String> {
    let svc = state.ctx.report_service.clone();
    tokio::task::spawn_blocking(move || ctrl_session_text(&svc, &session_id, after_sequence))
        .await
        .map_err(|_| "原文读取任务异常中断。".to_string())?
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn query_install_status(
    state: State<'_, ManagedToolsState>,
) -> Result<InstallStateSummary, String> {
    let installer = state.ctx.installer.clone();
    tokio::task::spawn_blocking(move || query_installation_status(&installer))
        .await.map_err(|_| "输入法状态查询异常中断。".to_string())
}

#[cfg(not(target_os = "linux"))]
#[tauri::command]
pub async fn manage_ime_installation(
    state: State<'_, ManagedToolsState>,
    install: bool,
    confirmed_elevation: bool,
) -> Result<InstallOperationResult, String> {
    Ok(run_installation_action(&state.ctx.installer, install, confirmed_elevation).await)
}
