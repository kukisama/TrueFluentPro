use tauri::{Emitter, State};
use crate::state::AppState;
use tfp_core::{
    AudioFile, AudioLabBundle, AudioPlaybackInfo,
    AudioAutoTag, AudioResearchTopic, AudioStagePreset,
    StudioTask,
};

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  听析中心（AudioLab）命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 导入音频文件：计算 sha256 / 提取 duration / 落库 audio_files + 新建 studio_sessions 行
#[tauri::command]
pub async fn audiolab_import_files(
    state: State<'_, AppState>,
    paths: Vec<String>,
) -> Result<Vec<String>, String> {
    tfp_audiolab::services::import_files(&state.db, &paths).await
}

/// 列出音频文件库
#[tauri::command]
pub async fn audiolab_list_files(
    state: State<'_, AppState>,
    limit: Option<i64>,
    offset: Option<i64>,
    search: Option<String>,
    sort: Option<String>,
) -> Result<Vec<AudioFile>, String> {
    state.db.audiolab_list_files(
        limit.unwrap_or(50),
        offset.unwrap_or(0),
        search.as_deref(),
        sort.as_deref(),
    ).await.map_err(|e| e.to_string())
}

/// 获取单个音频文件
#[tauri::command]
pub async fn audiolab_get_file(
    state: State<'_, AppState>,
    file_id: String,
) -> Result<Option<AudioFile>, String> {
    state.db.audiolab_get_file(&file_id).await.map_err(|e| e.to_string())
}

/// 移除音频文件
#[tauri::command]
pub async fn audiolab_remove_file(
    state: State<'_, AppState>,
    file_id: String,
    delete_source: bool,
) -> Result<(), String> {
    if delete_source {
        if let Ok(Some(f)) = state.db.audiolab_get_file(&file_id).await {
            let _ = std::fs::remove_file(&f.source_path);
            if let Some(mp3) = &f.mp3_path {
                let _ = std::fs::remove_file(mp3);
            }
        }
    }
    state.db.audiolab_remove_file(&file_id).await.map_err(|e| e.to_string())
}

/// 获取工作区 bundle（懒加载）
#[tauri::command]
pub async fn audiolab_get_bundle(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<AudioLabBundle, String> {
    tfp_audiolab::services::get_bundle(&state.db, &session_id).await
}

/// 打开音频文件用于播放
#[tauri::command]
pub async fn audiolab_playback_open(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<AudioPlaybackInfo, String> {
    tfp_audiolab::services::playback_open(&state.db, &session_id).await
}

/// 启动转录任务
#[tauri::command]
#[allow(unused_variables)]
pub async fn audiolab_start_transcription(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_id: String,
    audio_file_id: String,
    parser_kind: String,
    model_ref: Option<String>,
) -> Result<String, String> {
    let task = tfp_audiolab::services::submit_task(
        &state.db,
        &session_id,
        "audio_transcribe",
        &format!("parser_kind={},audio_file_id={}", parser_kind, audio_file_id),
    ).await?;

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task.id,
        "session_id": session_id,
        "task_type": "audio_transcribe",
        "status": "pending",
    }));

    Ok(task.id)
}

/// 列出运行中任务
#[tauri::command]
pub async fn audiolab_list_running_tasks(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<Vec<StudioTask>, String> {
    state.db.audiolab_list_running_tasks(&session_id).await.map_err(|e| e.to_string())
}

/// 列出阶段预设
#[tauri::command]
pub async fn audiolab_list_stage_presets(
    state: State<'_, AppState>,
) -> Result<Vec<AudioStagePreset>, String> {
    state.db.audiolab_list_stage_presets().await.map_err(|e| e.to_string())
}

/// 新增/更新阶段预设
#[tauri::command]
pub async fn audiolab_upsert_stage_preset(
    state: State<'_, AppState>,
    preset: AudioStagePreset,
) -> Result<(), String> {
    let mut preset = preset;
    if preset.id.is_empty() {
        preset.id = uuid::Uuid::new_v4().to_string();
    }
    state.db.audiolab_upsert_stage_preset(&preset).await.map_err(|e| e.to_string())
}

/// 删除阶段预设
#[tauri::command]
pub async fn audiolab_delete_stage_preset(
    state: State<'_, AppState>,
    stage: String,
) -> Result<(), String> {
    state.db.audiolab_delete_stage_preset(&stage).await.map_err(|e| e.to_string())
}

/// 启动阶段生成任务
#[tauri::command]
pub async fn audiolab_start_stage(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_id: String,
    stage_key: String,
    model_ref: Option<String>,
) -> Result<String, String> {
    tfp_audiolab::services::create_stage_output(
        &state.db, &session_id, &stage_key, model_ref.as_deref(),
    ).await?;

    let task = tfp_audiolab::services::submit_task(
        &state.db, &session_id, "audio_stage", &format!("stage_key={}", stage_key),
    ).await?;

    // Kick task engine
    if let Some(engine) = state.task_engine.read().await.as_ref() {
        engine.kick().await;
    }

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task.id,
        "session_id": session_id,
        "task_type": "audio_stage",
        "stage_key": stage_key,
        "status": "pending",
    }));

    Ok(task.id)
}

/// 更新阶段内容（编辑模式保存）
#[tauri::command]
pub async fn audiolab_update_stage_content(
    state: State<'_, AppState>,
    session_id: String,
    stage_key: String,
    content: String,
) -> Result<(), String> {
    tfp_audiolab::services::update_stage_content(
        &state.db, &session_id, &stage_key, &content,
    ).await
}

/// 启动播客 TTS 合成
#[tauri::command]
pub async fn audiolab_start_podcast_tts(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_id: String,
    _voice_lib_ref: Option<String>,
) -> Result<String, String> {
    let task = tfp_audiolab::services::submit_task(
        &state.db, &session_id, "audio_podcast_tts", "podcast_tts",
    ).await?;

    // Kick task engine
    if let Some(engine) = state.task_engine.read().await.as_ref() {
        engine.kick().await;
    }

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task.id,
        "session_id": session_id,
        "task_type": "audio_podcast_tts",
        "status": "pending",
    }));

    Ok(task.id)
}

/// 生成自动标签
#[tauri::command]
#[allow(unused_variables)]
pub async fn audiolab_generate_auto_tags(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_id: String,
    model_ref: Option<String>,
) -> Result<String, String> {
    let task = tfp_audiolab::services::submit_task(
        &state.db, &session_id, "audio_auto_tags", "generate_auto_tags",
    ).await?;

    // Kick task engine
    if let Some(engine) = state.task_engine.read().await.as_ref() {
        engine.kick().await;
    }

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task.id,
        "session_id": session_id,
        "task_type": "audio_auto_tags",
        "status": "pending",
    }));

    Ok(task.id)
}

/// 手动添加标签
#[tauri::command]
pub async fn audiolab_add_manual_tag(
    state: State<'_, AppState>,
    session_id: String,
    tag: String,
) -> Result<AudioAutoTag, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let t = AudioAutoTag {
        id: uuid::Uuid::new_v4().to_string(),
        session_id,
        tag,
        source: "manual".to_string(),
        created_at: now,
    };
    state.db.audiolab_insert_auto_tag(&t).await.map_err(|e| e.to_string())?;
    Ok(t)
}

/// 删除标签
#[tauri::command]
pub async fn audiolab_remove_auto_tag(
    state: State<'_, AppState>,
    tag_id: String,
) -> Result<(), String> {
    state.db.audiolab_remove_tag(&tag_id).await.map_err(|e| e.to_string())
}

/// 添加研究 topic
#[tauri::command]
pub async fn audiolab_add_research_topic(
    state: State<'_, AppState>,
    session_id: String,
    title: String,
    description: String,
) -> Result<AudioResearchTopic, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let topic = AudioResearchTopic {
        id: uuid::Uuid::new_v4().to_string(),
        session_id,
        title,
        description,
        status: "pending".to_string(),
        report_markdown: None,
        created_at: now,
    };
    state.db.audiolab_insert_research_topic(&topic).await.map_err(|e| e.to_string())?;
    Ok(topic)
}

/// 启动研究报告生成
#[tauri::command]
#[allow(unused_variables)]
pub async fn audiolab_start_research(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    topic_id: String,
    model_ref: Option<String>,
) -> Result<String, String> {
    let task = tfp_audiolab::services::submit_task(
        &state.db, &topic_id, "audio_research", &format!("topic_id={}", topic_id),
    ).await?;

    // Kick task engine
    if let Some(engine) = state.task_engine.read().await.as_ref() {
        engine.kick().await;
    }

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task.id,
        "session_id": topic_id,
        "task_type": "audio_research",
        "status": "pending",
    }));

    Ok(task.id)
}

/// 删除研究 topic
#[tauri::command]
pub async fn audiolab_remove_research_topic(
    state: State<'_, AppState>,
    topic_id: String,
) -> Result<(), String> {
    state.db.audiolab_delete_research_topic(&topic_id).await.map_err(|e| e.to_string())
}

/// 重命名说话人
#[tauri::command]
pub async fn audiolab_rename_speaker(
    state: State<'_, AppState>,
    transcript_id: String,
    old_index: i64,
    new_label: String,
) -> Result<(), String> {
    state.db.audiolab_rename_speaker(&transcript_id, old_index, &new_label).await.map_err(|e| e.to_string())
}

/// 更新单个段落
#[tauri::command]
pub async fn audiolab_update_segment(
    state: State<'_, AppState>,
    segment_id: String,
    text: Option<String>,
    speaker: Option<String>,
    start_ms: Option<i64>,
    end_ms: Option<i64>,
) -> Result<(), String> {
    state.db.audiolab_update_segment(&segment_id, text.as_deref(), speaker.as_deref(), start_ms, end_ms).await.map_err(|e| e.to_string())
}

/// 导出（字幕/文本/Markdown）
#[tauri::command]
pub async fn audiolab_export(
    state: State<'_, AppState>,
    session_id: String,
    target: String,
    stage_key: Option<String>,
    output_path: String,
) -> Result<(), String> {
    let content = tfp_audiolab::services::export_content(
        &state.db, &session_id, &target, stage_key.as_deref(),
    ).await?;
    std::fs::write(&output_path, &content).map_err(|e| format!("写入文件失败: {e}"))?;
    Ok(())
}

/// 从实时翻译会话导入到听析中心
#[tauri::command]
pub async fn audiolab_import_from_realtime(
    state: State<'_, AppState>,
    realtime_session_id: String,
) -> Result<String, String> {
    tfp_audiolab::services::import_from_realtime(&state.db, &realtime_session_id).await
}

// ── 字幕格式化工具函数 — now in tfp_audiolab::format ──

#[cfg(test)]
mod tests {
    use tfp_core::AudioSegment;
    use std::path::Path;

    fn make_seg(seq: i64, start_ms: i64, end_ms: i64, text: &str) -> AudioSegment {
        AudioSegment {
            id: format!("s{seq}"),
            transcript_id: "t1".into(),
            sequence: seq,
            speaker: "Speaker 1".into(),
            speaker_index: 0,
            start_ms,
            end_ms,
            text: text.into(),
            confidence: None,
        }
    }

    #[test]
    fn test_delegates_ms_to_srt() {
        assert_eq!(tfp_audiolab::format::ms_to_srt_time(0), "00:00:00,000");
        assert_eq!(tfp_audiolab::format::ms_to_srt_time(3661234), "01:01:01,234");
    }

    #[test]
    fn test_delegates_ms_to_vtt() {
        assert_eq!(tfp_audiolab::format::ms_to_vtt_time(0), "00:00:00.000");
    }

    #[test]
    fn test_delegates_estimate_duration() {
        assert_eq!(
            tfp_audiolab::audio_util::estimate_audio_duration_ms(Path::new("test.wav"), 32044),
            1000
        );
    }

    #[test]
    fn test_delegates_format_srt() {
        let segs = vec![make_seg(1, 0, 1500, "Hello world")];
        let out = tfp_audiolab::format::format_srt(&segs);
        assert!(out.contains("00:00:00,000 --> 00:00:01,500"));
    }

    #[test]
    fn test_delegates_format_vtt() {
        let segs = vec![make_seg(1, 0, 1500, "Hello world")];
        let out = tfp_audiolab::format::format_vtt(&segs);
        assert!(out.starts_with("WEBVTT"));
    }
}
