use tauri::State;
use tfp_core::{BatchBucketNav, BatchPackage, BatchSubtaskView, ReviewSheetPreset};
use tfp_engine::BatchCoordinator;

use crate::state::AppState;

#[tauri::command]
pub async fn batch_create_package(
    state: State<'_, AppState>,
    session_id: String,
    audio_file_id: String,
    display_name: String,
    include_subtitle: bool,
) -> Result<BatchPackage, String> {
    let config = state.config.read().await;
    let sheets: Vec<ReviewSheetPreset> = config
        .batch
        .review_sheets
        .clone();
    drop(config);

    BatchCoordinator::create_package(
        &state.db,
        &session_id,
        &audio_file_id,
        &display_name,
        &sheets,
        include_subtitle,
    )
    .await
}

#[tauri::command]
pub async fn batch_start(
    state: State<'_, AppState>,
    package_ids: Vec<String>,
    include_subtitle: bool,
) -> Result<u32, String> {
    let engine_guard = state.task_engine.read().await;
    let engine = engine_guard
        .as_ref()
        .ok_or_else(|| "Task engine not started".to_string())?;

    let config = state.config.read().await;
    let sheets: Vec<ReviewSheetPreset> = config
        .batch
        .review_sheets
        .clone();
    drop(config);

    BatchCoordinator::start_batch(&state.db, engine, &package_ids, &sheets, include_subtitle).await
}

#[tauri::command]
pub async fn batch_stop(
    state: State<'_, AppState>,
    package_ids: Vec<String>,
) -> Result<(), String> {
    for pkg_id in &package_ids {
        BatchCoordinator::pause_package(&state.db, pkg_id).await?;
    }
    Ok(())
}

#[tauri::command]
pub async fn batch_pause_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    BatchCoordinator::pause_package(&state.db, &package_id).await
}

#[tauri::command]
pub async fn batch_resume_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    let engine_guard = state.task_engine.read().await;
    let engine = engine_guard
        .as_ref()
        .ok_or_else(|| "Task engine not started".to_string())?;
    BatchCoordinator::resume_package(&state.db, engine, &package_id).await
}

#[tauri::command]
pub async fn batch_remove_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    BatchCoordinator::remove_package(&state.db, &package_id).await
}

#[tauri::command]
pub async fn batch_restore_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    BatchCoordinator::restore_package(&state.db, &package_id).await
}

#[tauri::command]
pub async fn batch_get_bucket_nav(
    state: State<'_, AppState>,
) -> Result<Vec<BatchBucketNav>, String> {
    BatchCoordinator::get_bucket_nav(&state.db).await
}

#[tauri::command]
pub async fn batch_get_packages(
    state: State<'_, AppState>,
    bucket_key: String,
) -> Result<Vec<BatchPackage>, String> {
    BatchCoordinator::get_packages_for_bucket(&state.db, &bucket_key).await
}

#[tauri::command]
pub async fn batch_get_subtasks(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<Vec<BatchSubtaskView>, String> {
    BatchCoordinator::get_subtasks_for_package(&state.db, &package_id).await
}

#[tauri::command]
pub async fn batch_regenerate_package(
    state: State<'_, AppState>,
    package_id: String,
) -> Result<(), String> {
    let engine_guard = state.task_engine.read().await;
    let engine = engine_guard
        .as_ref()
        .ok_or_else(|| "Task engine not started".to_string())?;
    BatchCoordinator::regenerate_package(&state.db, engine, &package_id).await
}

#[tauri::command]
pub async fn batch_regenerate_subtask(
    state: State<'_, AppState>,
    queue_item_id: String,
) -> Result<(), String> {
    let engine_guard = state.task_engine.read().await;
    let engine = engine_guard
        .as_ref()
        .ok_or_else(|| "Task engine not started".to_string())?;
    BatchCoordinator::regenerate_subtask(&state.db, engine, &queue_item_id).await
}
