use serde::{Deserialize, Serialize};
use tauri::State;

use crate::state::AppState;
use tfp_core::MonitorGlobalStats;
use tfp_storage::monitor_repo::{MonitorTaskRow, MonitorExecutionRow};

// ── DTO structures ──

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MonitorBucket {
    pub key: String,
    pub title: String,
    pub icon: String,
    pub count: i64,
    pub is_danger: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MonitorTaskItem {
    pub id: String,
    pub short_task_id: String,
    pub audio_item_id: String,
    pub audio_file_name: String,
    pub stage: String,
    pub stage_display_name: String,
    pub stage_color: String,
    pub task_type: String,
    pub status: String,
    pub status_display_name: String,
    pub priority: i64,
    pub retry_count: i64,
    pub progress: f64,
    pub error_message: Option<String>,
    pub progress_message: Option<String>,
    pub submitted_at: String,
    pub started_at: Option<String>,
    pub finished_at: Option<String>,
    pub elapsed_time: String,
    pub params_snapshot_json: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MonitorExecutionRecord {
    pub id: String,
    pub task_id: String,
    pub status: String,
    pub status_display_name: String,
    pub billable: bool,
    pub billable_display: String,
    pub model_name: Option<String>,
    pub tokens_in: Option<i64>,
    pub tokens_out: Option<i64>,
    pub tokens_display: String,
    pub duration_ms: Option<i64>,
    pub duration_display: String,
    pub error_message: Option<String>,
    pub cancel_reason: Option<String>,
    pub started_at: String,
    pub finished_at: Option<String>,
    pub time_display: String,
    pub has_debug_data: bool,
    pub debug_prompt: Option<String>,
    pub debug_response: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MonitorSettings {
    pub max_transcription_concurrency: i64,
    pub max_ai_concurrency: i64,
    pub transcription_timeout_minutes: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MonitorSnapshot {
    pub buckets: Vec<MonitorBucket>,
    pub current_bucket: String,
    pub current_bucket_tasks: Vec<MonitorTaskItem>,
    pub global_stats: MonitorGlobalStats,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MonitorUiState {
    pub active_bucket: String,
    pub sort_column: String,
    pub sort_ascending: bool,
    pub selected_task_id: Option<String>,
}

// ── Helper functions ──

fn stage_display(stage: &str) -> &str {
    match stage {
        "Transcribed" => "转录",
        "Summarized" => "总结",
        "MindMap" => "脑图",
        "Insight" => "顿悟",
        "PodcastScript" => "播客",
        "PodcastAudio" => "播客音频",
        "Research" => "研究",
        "Translated" => "翻译",
        "ImageGen" => "图片",
        "VideoGen" => "视频",
        _ => stage,
    }
}

fn stage_color(stage: &str) -> &str {
    match stage {
        "Transcribed" => "#4FC3F7",
        "Summarized" => "#81C784",
        "MindMap" => "#CE93D8",
        "Insight" => "#FFB74D",
        "PodcastScript" => "#F06292",
        "PodcastAudio" => "#E57373",
        "Research" => "#64B5F6",
        "Translated" => "#4DB6AC",
        "ImageGen" => "#FF8A65",
        "VideoGen" => "#BA68C8",
        _ => "#90A4AE",
    }
}

fn status_display(status: &str, progress_message: Option<&str>) -> String {
    match status {
        "Queued" => "排队中".to_string(),
        "Executing" => {
            if let Some(msg) = progress_message {
                if !msg.is_empty() {
                    return msg.to_string();
                }
            }
            "执行中".to_string()
        }
        "Completed" => "已完成".to_string(),
        "Failed" => "失败".to_string(),
        "Cancelled" => "已取消".to_string(),
        "Timeout" => "超时".to_string(),
        "Interrupted" => "已中断".to_string(),
        _ => status.to_string(),
    }
}

fn execution_status_display(status: &str) -> &str {
    match status {
        "Running" => "执行中",
        "Completed" => "✓ 完成",
        "Failed" => "✗ 失败",
        "Cancelled" => "已取消",
        "Interrupted" => "中断",
        "Timeout" => "超时",
        _ => status,
    }
}

fn compute_elapsed_time(started_at: Option<&str>, finished_at: Option<&str>) -> String {
    let Some(start_str) = started_at else {
        return "--".to_string();
    };
    let Ok(start) = chrono::DateTime::parse_from_rfc3339(start_str) else {
        return "--".to_string();
    };
    let end = if let Some(fin_str) = finished_at {
        chrono::DateTime::parse_from_rfc3339(fin_str)
            .unwrap_or_else(|_| chrono::Utc::now().into())
    } else {
        chrono::Utc::now().into()
    };
    let elapsed = end.signed_duration_since(start);
    let total_secs = elapsed.num_seconds().max(0);
    if total_secs >= 3600 {
        format!("{}:{:02}:{:02}", total_secs / 3600, (total_secs % 3600) / 60, total_secs % 60)
    } else {
        format!("{}:{:02}", total_secs / 60, total_secs % 60)
    }
}

fn duration_display(duration_ms: Option<i64>) -> String {
    match duration_ms {
        Some(ms) if ms >= 60000 => format!("{}:{:02}", ms / 60000, (ms / 1000) % 60),
        Some(ms) => format!("{:.1}s", ms as f64 / 1000.0),
        None => "--".to_string(),
    }
}

fn tokens_display(tokens_in: Option<i64>, tokens_out: Option<i64>) -> String {
    if tokens_in.is_some() || tokens_out.is_some() {
        format!("入 {} / 出 {}", tokens_in.unwrap_or(0), tokens_out.unwrap_or(0))
    } else {
        "--".to_string()
    }
}

fn build_buckets(counts: &std::collections::HashMap<String, i64>) -> Vec<MonitorBucket> {
    vec![
        MonitorBucket {
            key: "pending".into(),
            title: "排队中".into(),
            icon: "fa-regular fa-clock".into(),
            count: *counts.get("Queued").unwrap_or(&0),
            is_danger: false,
        },
        MonitorBucket {
            key: "running".into(),
            title: "执行中".into(),
            icon: "fa-solid fa-arrows-rotate".into(),
            count: *counts.get("Executing").unwrap_or(&0),
            is_danger: false,
        },
        MonitorBucket {
            key: "completed".into(),
            title: "已完成".into(),
            icon: "fa-solid fa-check".into(),
            count: *counts.get("Completed").unwrap_or(&0),
            is_danger: false,
        },
        MonitorBucket {
            key: "failed".into(),
            title: "失败".into(),
            icon: "fa-solid fa-triangle-exclamation".into(),
            count: *counts.get("Failed").unwrap_or(&0)
                 + *counts.get("Timeout").unwrap_or(&0)
                 + *counts.get("Interrupted").unwrap_or(&0),
            is_danger: (*counts.get("Failed").unwrap_or(&0)
                      + *counts.get("Timeout").unwrap_or(&0)
                      + *counts.get("Interrupted").unwrap_or(&0)) > 0,
        },
        MonitorBucket {
            key: "cancelled".into(),
            title: "已取消".into(),
            icon: "fa-regular fa-circle-xmark".into(),
            count: *counts.get("Cancelled").unwrap_or(&0),
            is_danger: false,
        },
    ]
}

fn bucket_key_to_status(key: &str) -> &str {
    match key {
        "pending" => "Queued",
        "running" => "Executing",
        "completed" => "Completed",
        "failed" => "Failed,Timeout,Interrupted",
        "cancelled" => "Cancelled",
        _ => "Queued",
    }
}

fn map_task_row(t: MonitorTaskRow) -> MonitorTaskItem {
    let short_id = if t.id.len() > 12 {
        format!("{}..", &t.id[..12])
    } else {
        t.id.clone()
    };
    let elapsed = compute_elapsed_time(t.started_at.as_deref(), t.completed_at.as_deref());
    MonitorTaskItem {
        id: t.id.clone(),
        short_task_id: short_id,
        audio_item_id: t.audio_item_id.clone(),
        audio_file_name: t.audio_file_name.clone().unwrap_or_else(|| t.audio_item_id.clone()),
        stage: t.stage.clone(),
        stage_display_name: stage_display(&t.stage).to_string(),
        stage_color: stage_color(&t.stage).to_string(),
        task_type: t.task_type.clone(),
        status: t.status.clone(),
        status_display_name: status_display(&t.status, t.progress_message.as_deref()),
        priority: t.priority,
        retry_count: t.retry_count,
        progress: t.progress,
        error_message: t.error.clone(),
        progress_message: t.progress_message.clone(),
        submitted_at: t.submitted_at.clone(),
        started_at: t.started_at.clone(),
        finished_at: t.completed_at.clone(),
        elapsed_time: elapsed,
        params_snapshot_json: t.prompt_text.clone(),
    }
}

fn csv_quote(field: &str) -> String {
    let escaped = field.replace('"', "\"\"");
    format!("\"{}\"", escaped)
}

fn map_exec_to_record(e: MonitorExecutionRow) -> MonitorExecutionRecord {
    let has_debug = e.debug_prompt.is_some() || e.debug_response.is_some();
    MonitorExecutionRecord {
        id: e.id.clone(),
        task_id: e.task_id.clone(),
        status: e.status.clone(),
        status_display_name: execution_status_display(&e.status).to_string(),
        billable: e.billable,
        billable_display: if e.billable { "是".into() } else { "否".into() },
        model_name: e.model_name.clone(),
        tokens_in: e.tokens_in,
        tokens_out: e.tokens_out,
        tokens_display: tokens_display(e.tokens_in, e.tokens_out),
        duration_ms: e.duration_ms,
        duration_display: duration_display(e.duration_ms),
        error_message: e.error_message.clone(),
        cancel_reason: e.cancel_reason.clone(),
        started_at: e.started_at.clone(),
        finished_at: e.finished_at.clone(),
        time_display: if e.started_at.len() >= 19 {
            e.started_at[11..19].to_string()
        } else {
            e.started_at.clone()
        },
        has_debug_data: has_debug,
        debug_prompt: e.debug_prompt,
        debug_response: e.debug_response,
    }
}

// ── Tauri commands ──

#[tauri::command]
pub async fn monitor_get_snapshot(
    state: State<'_, AppState>,
    bucket: Option<String>,
    sort_column: Option<String>,
    sort_ascending: Option<bool>,
) -> Result<MonitorSnapshot, String> {
    let db = &state.db;
    let current_bucket = bucket.unwrap_or_else(|| "pending".into());
    let sort_col = sort_column.unwrap_or_else(|| "SubmittedAt".into());
    let sort_asc = sort_ascending.unwrap_or(false);

    let counts = db.monitor_get_status_counts().await.map_err(|e| e.to_string())?;
    let buckets = build_buckets(&counts);

    let status_filter = bucket_key_to_status(&current_bucket);
    let tasks = db
        .monitor_get_tasks_by_status(status_filter, &sort_col, sort_asc)
        .await
        .map_err(|e| e.to_string())?;

    let task_items: Vec<MonitorTaskItem> = tasks.into_iter().map(map_task_row).collect();
    let global_stats = db.monitor_get_global_stats().await.map_err(|e| e.to_string())?;

    Ok(MonitorSnapshot {
        buckets,
        current_bucket,
        current_bucket_tasks: task_items,
        global_stats,
    })
}

#[tauri::command]
pub async fn monitor_set_bucket(
    state: State<'_, AppState>,
    bucket_key: String,
    sort_column: Option<String>,
    sort_ascending: Option<bool>,
) -> Result<Vec<MonitorTaskItem>, String> {
    let db = &state.db;
    let sort_col = sort_column.unwrap_or_else(|| "SubmittedAt".into());
    let sort_asc = sort_ascending.unwrap_or(false);
    let status_filter = bucket_key_to_status(&bucket_key);

    let tasks = db
        .monitor_get_tasks_by_status(status_filter, &sort_col, sort_asc)
        .await
        .map_err(|e| e.to_string())?;

    Ok(tasks.into_iter().map(map_task_row).collect())
}

#[tauri::command]
pub async fn monitor_list_executions(
    state: State<'_, AppState>,
    task_id: String,
) -> Result<Vec<MonitorExecutionRecord>, String> {
    let db = &state.db;
    let execs = db.monitor_get_executions(&task_id).await.map_err(|e| e.to_string())?;
    Ok(execs.into_iter().map(map_exec_to_record).collect())
}

#[tauri::command]
pub async fn monitor_get_execution_detail(
    state: State<'_, AppState>,
    execution_id: String,
) -> Result<MonitorExecutionRecord, String> {
    let db = &state.db;
    let e = db.monitor_get_execution_by_id(&execution_id).await.map_err(|e| e.to_string())?;
    Ok(map_exec_to_record(e))
}

#[tauri::command]
pub async fn monitor_cancel_task(
    state: State<'_, AppState>,
    task_id: String,
    reason: Option<String>,
) -> Result<(), String> {
    let db = &state.db;
    let cancel_reason = reason.unwrap_or_else(|| "user_cancel".to_string());
    db.monitor_cancel_task(&task_id, &cancel_reason).await.map_err(|e| e.to_string())?;

    let exec_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now().to_rfc3339();
    db.monitor_insert_execution(
        &exec_id, &task_id, "Cancelled", false, None, None, None, None,
        Some(&cancel_reason), None, None, &now, Some(&now),
    ).await.map_err(|e| e.to_string())?;

    Ok(())
}

#[tauri::command]
pub async fn monitor_get_settings(
    state: State<'_, AppState>,
) -> Result<MonitorSettings, String> {
    let db = &state.db;
    let tc = db.kv_get("monitor.max_transcription_concurrency").await
        .ok().flatten()
        .and_then(|v| v.parse::<i64>().ok())
        .unwrap_or(2);
    let ac = db.kv_get("monitor.max_ai_concurrency").await
        .ok().flatten()
        .and_then(|v| v.parse::<i64>().ok())
        .unwrap_or(4);
    let tt = db.kv_get("monitor.transcription_timeout_minutes").await
        .ok().flatten()
        .and_then(|v| v.parse::<i64>().ok())
        .unwrap_or(10);
    Ok(MonitorSettings {
        max_transcription_concurrency: tc,
        max_ai_concurrency: ac,
        transcription_timeout_minutes: tt,
    })
}

#[tauri::command]
pub async fn monitor_update_settings(
    state: State<'_, AppState>,
    max_transcription_concurrency: Option<i64>,
    max_ai_concurrency: Option<i64>,
    transcription_timeout_minutes: Option<i64>,
) -> Result<(), String> {
    let db = &state.db;
    if let Some(v) = max_transcription_concurrency {
        let clamped = v.clamp(1, 20);
        db.kv_set("monitor.max_transcription_concurrency", &clamped.to_string())
            .await.map_err(|e| e.to_string())?;
    }
    if let Some(v) = max_ai_concurrency {
        let clamped = v.clamp(1, 20);
        db.kv_set("monitor.max_ai_concurrency", &clamped.to_string())
            .await.map_err(|e| e.to_string())?;
    }
    if let Some(v) = transcription_timeout_minutes {
        let clamped = v.clamp(1, 60);
        db.kv_set("monitor.transcription_timeout_minutes", &clamped.to_string())
            .await.map_err(|e| e.to_string())?;
    }
    Ok(())
}

#[tauri::command]
pub async fn monitor_cleanup_completed(
    state: State<'_, AppState>,
    older_than_days: Option<u32>,
) -> Result<u32, String> {
    let days = older_than_days.unwrap_or(7);
    state.db.monitor_cleanup_completed(days).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn monitor_refresh(
    state: State<'_, AppState>,
) -> Result<MonitorSnapshot, String> {
    monitor_get_snapshot(state, None, None, None).await
}

#[tauri::command]
pub async fn monitor_retry_task(
    state: State<'_, AppState>,
    task_id: String,
) -> Result<String, String> {
    let db = &state.db;
    let new_task_id = db.monitor_retry_task(&task_id).await.map_err(|e| e.to_string())?;
    let engine = state.task_engine.read().await;
    if let Some(ref eng) = *engine {
        eng.kick().await;
    }
    Ok(new_task_id)
}

#[tauri::command]
pub async fn monitor_batch_cancel(
    state: State<'_, AppState>,
    task_ids: Vec<String>,
) -> Result<u32, String> {
    let db = &state.db;
    let mut count = 0u32;
    for tid in &task_ids {
        if db.monitor_cancel_task(tid, "batch_cancel").await.is_ok() {
            count += 1;
        }
    }
    Ok(count)
}

#[tauri::command]
pub async fn monitor_batch_delete(
    state: State<'_, AppState>,
    task_ids: Vec<String>,
) -> Result<u32, String> {
    let db = &state.db;
    db.monitor_batch_delete(&task_ids).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn monitor_export_csv(
    state: State<'_, AppState>,
    file_path: String,
    status_filter: Option<String>,
    include_debug: Option<bool>,
) -> Result<String, String> {
    let db = &state.db;
    let tasks = if let Some(ref status) = status_filter {
        db.monitor_get_tasks_by_status(status, "SubmittedAt", false)
            .await.map_err(|e| e.to_string())?
    } else {
        db.monitor_get_all_tasks().await.map_err(|e| e.to_string())?
    };

    let include_dbg = include_debug.unwrap_or(false);
    let mut csv = String::new();
    csv.push_str("\"任务ID\",\"音频\",\"阶段\",\"状态\",\"发起时间\",\"开始时间\",\"完成时间\",\"错误信息\"");
    if include_dbg {
        csv.push_str(",\"调试提示词\",\"调试响应\"");
    }
    csv.push('\n');

    for t in &tasks {
        let stage_name = stage_display(&t.stage);
        let status_name = status_display(&t.status, t.progress_message.as_deref());
        let audio_name = t.audio_file_name.as_deref().unwrap_or(&t.audio_item_id);
        let error = t.error.as_deref().unwrap_or("");

        csv.push_str(&format!(
            "{},{},{},{},{},{},{},{}",
            csv_quote(&t.id),
            csv_quote(audio_name),
            csv_quote(stage_name),
            csv_quote(&status_name),
            csv_quote(&t.submitted_at),
            csv_quote(t.started_at.as_deref().unwrap_or("")),
            csv_quote(t.completed_at.as_deref().unwrap_or("")),
            csv_quote(error),
        ));
        if include_dbg {
            let (debug_prompt, debug_response) = match db.monitor_get_latest_execution_debug(&t.id).await {
                Ok(Some((p, r))) => (p, r),
                _ => (String::new(), String::new()),
            };
            csv.push_str(&format!(",{},{}", csv_quote(&debug_prompt), csv_quote(&debug_response)));
        }
        csv.push('\n');
    }

    std::fs::write(&file_path, &csv)
        .map_err(|e| format!("写入 CSV 失败: {e}"))?;
    Ok(file_path)
}

#[tauri::command]
pub async fn monitor_get_archived_snapshot(
    state: State<'_, AppState>,
    date_from: String,
    date_to: String,
) -> Result<MonitorSnapshot, String> {
    let db = &state.db;
    let tasks = db.monitor_get_tasks_by_date_range(&date_from, &date_to)
        .await.map_err(|e| e.to_string())?;

    let mut counts = std::collections::HashMap::new();
    for t in &tasks {
        *counts.entry(t.status.clone()).or_insert(0i64) += 1;
    }
    let buckets = build_buckets(&counts);

    let task_items: Vec<MonitorTaskItem> = tasks.into_iter().map(map_task_row).collect();
    let global_stats = db.monitor_get_global_stats().await.map_err(|e| e.to_string())?;

    Ok(MonitorSnapshot {
        buckets,
        current_bucket: "all".into(),
        current_bucket_tasks: task_items,
        global_stats,
    })
}

#[tauri::command]
pub async fn monitor_save_ui_state(
    state: State<'_, AppState>,
    ui_state: MonitorUiState,
) -> Result<(), String> {
    let json = serde_json::to_string(&ui_state).map_err(|e| e.to_string())?;
    state.db.kv_set("monitor.ui_state", &json).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn monitor_load_ui_state(
    state: State<'_, AppState>,
) -> Result<Option<MonitorUiState>, String> {
    let val = state.db.kv_get("monitor.ui_state").await.map_err(|e| e.to_string())?;
    match val {
        Some(json) => {
            let ui: MonitorUiState = serde_json::from_str(&json).map_err(|e| e.to_string())?;
            Ok(Some(ui))
        }
        None => Ok(None),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    #[test]
    fn test_stage_display() {
        assert_eq!(stage_display("Transcribed"), "转录");
        assert_eq!(stage_display("Summarized"), "总结");
        assert_eq!(stage_display("MindMap"), "脑图");
        assert_eq!(stage_display("Insight"), "顿悟");
        assert_eq!(stage_display("PodcastScript"), "播客");
        assert_eq!(stage_display("PodcastAudio"), "播客音频");
        assert_eq!(stage_display("Research"), "研究");
        assert_eq!(stage_display("Translated"), "翻译");
        assert_eq!(stage_display("ImageGen"), "图片");
        assert_eq!(stage_display("VideoGen"), "视频");
        // Unknown stage returns original text
        assert_eq!(stage_display("UnknownStage"), "UnknownStage");
        assert_eq!(stage_display("Custom"), "Custom");
    }

    #[test]
    fn test_stage_color() {
        assert_eq!(stage_color("Transcribed"), "#4FC3F7");
        assert_eq!(stage_color("Summarized"), "#81C784");
        assert_eq!(stage_color("MindMap"), "#CE93D8");
        assert_eq!(stage_color("Insight"), "#FFB74D");
        assert_eq!(stage_color("Research"), "#64B5F6");
        // Unknown stage returns default grey
        assert_eq!(stage_color("UnknownStage"), "#90A4AE");
    }

    #[test]
    fn test_status_display() {
        assert_eq!(status_display("Queued", None), "排队中");
        assert_eq!(status_display("Executing", None), "执行中");
        assert_eq!(status_display("Executing", Some("处理中 50%")), "处理中 50%");
        assert_eq!(status_display("Executing", Some("")), "执行中");
        assert_eq!(status_display("Completed", None), "已完成");
        assert_eq!(status_display("Failed", None), "失败");
        assert_eq!(status_display("Cancelled", None), "已取消");
        assert_eq!(status_display("Timeout", None), "超时");
        assert_eq!(status_display("Interrupted", None), "已中断");
        // Unknown returns original
        assert_eq!(status_display("CustomStatus", None), "CustomStatus");
    }

    #[test]
    fn test_execution_status_display() {
        assert_eq!(execution_status_display("Running"), "执行中");
        assert_eq!(execution_status_display("Completed"), "✓ 完成");
        assert_eq!(execution_status_display("Failed"), "✗ 失败");
        assert_eq!(execution_status_display("Cancelled"), "已取消");
        assert_eq!(execution_status_display("Interrupted"), "中断");
        assert_eq!(execution_status_display("Timeout"), "超时");
        // Unknown returns original
        assert_eq!(execution_status_display("Custom"), "Custom");
    }

    #[test]
    fn test_duration_display() {
        assert_eq!(duration_display(None), "--");
        assert_eq!(duration_display(Some(500)), "0.5s");
        assert_eq!(duration_display(Some(1000)), "1.0s");
        assert_eq!(duration_display(Some(65000)), "1:05");
        assert_eq!(duration_display(Some(120000)), "2:00");
        assert_eq!(duration_display(Some(0)), "0.0s");
    }

    #[test]
    fn test_tokens_display() {
        assert_eq!(tokens_display(None, None), "--");
        assert_eq!(tokens_display(Some(100), Some(50)), "入 100 / 出 50");
        assert_eq!(tokens_display(Some(100), None), "入 100 / 出 0");
        assert_eq!(tokens_display(None, Some(50)), "入 0 / 出 50");
    }

    #[test]
    fn test_compute_elapsed_time() {
        // No start → "--"
        assert_eq!(compute_elapsed_time(None, None), "--");
        // 5 seconds apart
        assert_eq!(
            compute_elapsed_time(
                Some("2024-01-01T00:00:00+00:00"),
                Some("2024-01-01T00:00:05+00:00"),
            ),
            "0:05"
        );
        // 1 hour
        assert_eq!(
            compute_elapsed_time(
                Some("2024-01-01T00:00:00+00:00"),
                Some("2024-01-01T01:00:00+00:00"),
            ),
            "1:00:00"
        );
        // Invalid start date → "--"
        assert_eq!(compute_elapsed_time(Some("not-a-date"), None), "--");
    }

    #[test]
    fn test_build_buckets() {
        let mut counts = HashMap::new();
        counts.insert("Queued".into(), 3);
        counts.insert("Completed".into(), 5);
        counts.insert("Failed".into(), 2);

        let buckets = build_buckets(&counts);
        assert_eq!(buckets.len(), 5);
        // pending bucket
        assert_eq!(buckets[0].key, "pending");
        assert_eq!(buckets[0].count, 3);
        // completed bucket
        assert_eq!(buckets[2].key, "completed");
        assert_eq!(buckets[2].count, 5);
        // failed bucket (aggregates Failed+Timeout+Interrupted)
        assert_eq!(buckets[3].key, "failed");
        assert_eq!(buckets[3].count, 2);
        assert!(buckets[3].is_danger);
        // cancelled bucket
        assert_eq!(buckets[4].key, "cancelled");
        assert_eq!(buckets[4].count, 0);
        assert!(!buckets[4].is_danger);
    }
}
