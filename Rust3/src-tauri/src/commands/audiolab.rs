use std::path::Path;
use sha2::{Sha256, Digest};
use tauri::{Emitter, State};
use crate::state::AppState;
use tfp_core::{
    AudioFile, AudioLabBundle, AudioPlaybackInfo, AudioStageOutput,
    AudioAutoTag, AudioResearchTopic, AudioStagePreset,
    AudioSegment, StudioSession, StudioTask,
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
    let mut file_ids = Vec::new();
    let now = chrono::Utc::now().to_rfc3339();
    let batch_id = uuid::Uuid::new_v4().to_string();

    for path_str in &paths {
        let p = Path::new(path_str);
        if !p.exists() {
            return Err(format!("文件不存在: {}", path_str));
        }

        let display_name = p.file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("unknown")
            .to_string();

        let metadata = std::fs::metadata(p).map_err(|e| format!("读取文件元数据失败: {e}"))?;
        let file_size_bytes = metadata.len() as i64;

        let file_bytes = std::fs::read(p).map_err(|e| format!("读取文件失败: {e}"))?;
        let sha256 = format!("{:x}", Sha256::digest(&file_bytes));

        let duration_ms = estimate_audio_duration_ms(p, file_size_bytes);

        let file_id = uuid::Uuid::new_v4().to_string();
        let session_id = uuid::Uuid::new_v4().to_string();

        let audio_file = AudioFile {
            id: file_id.clone(),
            display_name: display_name.clone(),
            source_path: path_str.clone(),
            mp3_path: if path_str.to_lowercase().ends_with(".mp3") { Some(path_str.clone()) } else { None },
            sample_rate: 16000,
            channels: 1,
            duration_ms,
            file_size_bytes,
            sha256,
            imported_at: now.clone(),
            last_opened_at: None,
            is_legacy_import: false,
            legacy_source_path: None,
            import_batch_id: Some(batch_id.clone()),
            session_id: Some(session_id.clone()),
        };

        state.db.audiolab_insert_file(&audio_file).await.map_err(|e| e.to_string())?;

        state.db.studio_create_session(&StudioSession {
            id: session_id.clone(),
            session_type: "audio".to_string(),
            name: display_name,
            directory_path: p.parent().map(|pp| pp.to_string_lossy().to_string()).unwrap_or_default(),
            canvas_mode: String::new(),
            media_kind: String::new(),
            is_deleted: false,
            created_at: now.clone(),
            updated_at: now.clone(),
            last_accessed_at: Some(now.clone()),
            source_session_id: None,
            source_session_name: None,
            source_session_directory_name: None,
            source_asset_id: Some(file_id.clone()),
            source_asset_kind: Some("audio_file".to_string()),
            source_asset_file_name: None,
            source_asset_path: None,
            source_preview_path: None,
            source_reference_role: None,
            message_count: 0,
            task_count: 0,
            asset_count: 0,
            latest_message_preview: None,
            legacy_source_path: None,
            import_batch_id: Some(batch_id.clone()),
            imported_at: Some(now.clone()),
            is_legacy_import: false,
        }).await.map_err(|e| e.to_string())?;

        file_ids.push(file_id);
    }
    Ok(file_ids)
}

/// 简易音频时长估算
fn estimate_audio_duration_ms(path: &Path, file_size_bytes: i64) -> i64 {
    let ext = path.extension().and_then(|e| e.to_str()).unwrap_or("").to_lowercase();
    match ext.as_str() {
        "wav" => {
            let bytes_per_sec = 32000_i64; // 16kHz, 16-bit mono = 32000 bytes/sec
            if file_size_bytes > 44 {
                ((file_size_bytes - 44) * 1000) / bytes_per_sec
            } else {
                0
            }
        }
        "mp3" => {
            let bytes_per_sec = 16000_i64;
            (file_size_bytes * 1000) / bytes_per_sec
        }
        _ => {
            let bytes_per_sec = 16000_i64;
            (file_size_bytes * 1000) / bytes_per_sec
        }
    }
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
    let session = state.db.studio_get_session(&session_id).await.map_err(|e| e.to_string())?
        .ok_or_else(|| format!("找不到 session: {}", session_id))?;

    let transcript = state.db.audiolab_get_transcript(&session_id).await.map_err(|e| e.to_string())?;

    let file = if let Some(ref asset_id) = session.source_asset_id {
        state.db.audiolab_get_file(asset_id).await.map_err(|e| e.to_string())?
    } else if let Some(ref t) = transcript {
        state.db.audiolab_get_file(&t.audio_file_id).await.map_err(|e| e.to_string())?
    } else {
        None
    };

    let file = file.ok_or_else(|| "找不到关联音频文件".to_string())?;

    let segments = if let Some(ref t) = transcript {
        state.db.audiolab_get_segments(&t.id).await.map_err(|e| e.to_string())?
    } else {
        vec![]
    };

    let auto_tags = state.db.audiolab_get_auto_tags(&session_id).await.map_err(|e| e.to_string())?;
    let stage_outputs = state.db.audiolab_get_stage_outputs(&session_id).await.map_err(|e| e.to_string())?;
    let research_topics = state.db.audiolab_get_research_topics(&session_id).await.map_err(|e| e.to_string())?;
    let custom_presets = state.db.audiolab_list_stage_presets().await.map_err(|e| e.to_string())?;

    Ok(AudioLabBundle {
        file,
        transcript,
        segments,
        auto_tags,
        stage_outputs,
        research_topics,
        custom_presets,
    })
}

/// 打开音频文件用于播放
#[tauri::command]
pub async fn audiolab_playback_open(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<AudioPlaybackInfo, String> {
    let session = state.db.studio_get_session(&session_id).await.map_err(|e| e.to_string())?
        .ok_or_else(|| format!("找不到 session: {}", session_id))?;
    let file = if let Some(ref asset_id) = session.source_asset_id {
        state.db.audiolab_get_file(asset_id).await.map_err(|e| e.to_string())?
    } else {
        let transcript = state.db.audiolab_get_transcript(&session_id).await.map_err(|e| e.to_string())?;
        if let Some(ref t) = transcript {
            state.db.audiolab_get_file(&t.audio_file_id).await.map_err(|e| e.to_string())?
        } else {
            None
        }
    };
    let file = file.ok_or_else(|| "找不到关联音频文件".to_string())?;

    let playback_path = file.mp3_path.clone().unwrap_or_else(|| file.source_path.clone());

    let _ = state.db.audiolab_update_last_opened(&file.id).await;

    Ok(AudioPlaybackInfo {
        file_id: file.id,
        playback_path,
        duration_ms: file.duration_ms,
        display_name: file.display_name,
    })
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
    let task_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now().to_rfc3339();

    let task = StudioTask {
        id: task_id.clone(),
        session_id: session_id.clone(),
        task_type: "audio_transcribe".to_string(),
        status: "pending".to_string(),
        prompt: format!("parser_kind={},audio_file_id={}", parser_kind, audio_file_id),
        progress: 0.0,
        result_file_path: None,
        error_message: None,
        has_reference_input: false,
        remote_video_id: None,
        remote_video_api_mode: None,
        remote_generation_id: None,
        remote_download_url: None,
        generate_seconds: None,
        download_seconds: None,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.studio_upsert_task(&task).await.map_err(|e| e.to_string())?;

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task_id,
        "session_id": session_id,
        "task_type": "audio_transcribe",
        "status": "pending",
    }));

    Ok(task_id)
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
    let task_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now().to_rfc3339();

    let output = AudioStageOutput {
        id: uuid::Uuid::new_v4().to_string(),
        session_id: session_id.clone(),
        stage_key: stage_key.clone(),
        content_markdown: String::new(),
        status: "Processing".to_string(),
        error_message: None,
        model_ref: model_ref.clone(),
        generated_at: Some(now.clone()),
        custom_stage_key: if stage_key.starts_with("Custom:") {
            Some(stage_key.trim_start_matches("Custom:").to_string())
        } else {
            None
        },
        custom_is_mindmap: None,
    };
    state.db.audiolab_upsert_stage_output(&output).await.map_err(|e| e.to_string())?;

    let task = StudioTask {
        id: task_id.clone(),
        session_id: session_id.clone(),
        task_type: "audio_stage".to_string(),
        status: "pending".to_string(),
        prompt: format!("stage_key={}", stage_key),
        progress: 0.0,
        result_file_path: None,
        error_message: None,
        has_reference_input: false,
        remote_video_id: None,
        remote_video_api_mode: None,
        remote_generation_id: None,
        remote_download_url: None,
        generate_seconds: None,
        download_seconds: None,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.studio_upsert_task(&task).await.map_err(|e| e.to_string())?;

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task_id,
        "session_id": session_id,
        "task_type": "audio_stage",
        "stage_key": stage_key,
        "status": "pending",
    }));

    Ok(task_id)
}

/// 更新阶段内容（编辑模式保存）
#[tauri::command]
pub async fn audiolab_update_stage_content(
    state: State<'_, AppState>,
    session_id: String,
    stage_key: String,
    content: String,
) -> Result<(), String> {
    let outputs = state.db.audiolab_get_stage_outputs(&session_id).await.map_err(|e| e.to_string())?;
    if let Some(existing) = outputs.iter().find(|o| o.stage_key == stage_key) {
        let mut updated = existing.clone();
        updated.content_markdown = content;
        updated.status = "Ready".to_string();
        state.db.audiolab_upsert_stage_output(&updated).await.map_err(|e| e.to_string())?;
    } else {
        let output = AudioStageOutput {
            id: uuid::Uuid::new_v4().to_string(),
            session_id,
            stage_key,
            content_markdown: content,
            status: "Ready".to_string(),
            error_message: None,
            model_ref: None,
            generated_at: Some(chrono::Utc::now().to_rfc3339()),
            custom_stage_key: None,
            custom_is_mindmap: None,
        };
        state.db.audiolab_upsert_stage_output(&output).await.map_err(|e| e.to_string())?;
    }
    Ok(())
}

/// 启动播客 TTS 合成
#[tauri::command]
pub async fn audiolab_start_podcast_tts(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_id: String,
    _voice_lib_ref: Option<String>,
) -> Result<String, String> {
    let task_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now().to_rfc3339();

    let task = StudioTask {
        id: task_id.clone(),
        session_id: session_id.clone(),
        task_type: "audio_podcast_tts".to_string(),
        status: "pending".to_string(),
        prompt: "podcast_tts".to_string(),
        progress: 0.0,
        result_file_path: None,
        error_message: None,
        has_reference_input: false,
        remote_video_id: None,
        remote_video_api_mode: None,
        remote_generation_id: None,
        remote_download_url: None,
        generate_seconds: None,
        download_seconds: None,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.studio_upsert_task(&task).await.map_err(|e| e.to_string())?;

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task_id,
        "session_id": session_id,
        "task_type": "audio_podcast_tts",
        "status": "pending",
    }));

    Ok(task_id)
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
    let task_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now().to_rfc3339();

    let task = StudioTask {
        id: task_id.clone(),
        session_id: session_id.clone(),
        task_type: "audio_auto_tags".to_string(),
        status: "pending".to_string(),
        prompt: "generate_auto_tags".to_string(),
        progress: 0.0,
        result_file_path: None,
        error_message: None,
        has_reference_input: false,
        remote_video_id: None,
        remote_video_api_mode: None,
        remote_generation_id: None,
        remote_download_url: None,
        generate_seconds: None,
        download_seconds: None,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.studio_upsert_task(&task).await.map_err(|e| e.to_string())?;

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task_id,
        "session_id": session_id,
        "task_type": "audio_auto_tags",
        "status": "pending",
    }));

    Ok(task_id)
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
    let task_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now().to_rfc3339();

    let task = StudioTask {
        id: task_id.clone(),
        session_id: topic_id.clone(),
        task_type: "audio_research".to_string(),
        status: "pending".to_string(),
        prompt: format!("topic_id={}", topic_id),
        progress: 0.0,
        result_file_path: None,
        error_message: None,
        has_reference_input: false,
        remote_video_id: None,
        remote_video_api_mode: None,
        remote_generation_id: None,
        remote_download_url: None,
        generate_seconds: None,
        download_seconds: None,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.studio_upsert_task(&task).await.map_err(|e| e.to_string())?;

    let _ = app.emit("audiolab-task-update", serde_json::json!({
        "task_id": task_id,
        "session_id": topic_id,
        "task_type": "audio_research",
        "status": "pending",
    }));

    Ok(task_id)
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
    let content = match target.as_str() {
        "srt" | "vtt" => {
            let transcript = state.db.audiolab_get_transcript(&session_id).await.map_err(|e| e.to_string())?
                .ok_or("无转录数据")?;
            let segments = state.db.audiolab_get_segments(&transcript.id).await.map_err(|e| e.to_string())?;
            if target == "srt" {
                format_srt(&segments)
            } else {
                format_vtt(&segments)
            }
        }
        "txt" => {
            let transcript = state.db.audiolab_get_transcript(&session_id).await.map_err(|e| e.to_string())?
                .ok_or("无转录数据")?;
            let segments = state.db.audiolab_get_segments(&transcript.id).await.map_err(|e| e.to_string())?;
            segments.iter().map(|s| format!("[{}] {}", s.speaker, s.text)).collect::<Vec<_>>().join("\n")
        }
        "json" => {
            let transcript = state.db.audiolab_get_transcript(&session_id).await.map_err(|e| e.to_string())?
                .ok_or("无转录数据")?;
            let segments = state.db.audiolab_get_segments(&transcript.id).await.map_err(|e| e.to_string())?;
            serde_json::to_string_pretty(&segments).map_err(|e| e.to_string())?
        }
        "markdown" => {
            if let Some(key) = stage_key {
                let outputs = state.db.audiolab_get_stage_outputs(&session_id).await.map_err(|e| e.to_string())?;
                outputs.iter().find(|o| o.stage_key == key)
                    .map(|o| o.content_markdown.clone())
                    .unwrap_or_default()
            } else {
                return Err("导出 markdown 需指定 stage_key".to_string());
            }
        }
        _ => return Err(format!("不支持的导出格式: {}", target)),
    };

    std::fs::write(&output_path, &content).map_err(|e| format!("写入文件失败: {e}"))?;
    Ok(())
}

/// 从实时翻译会话导入到听析中心
#[tauri::command]
pub async fn audiolab_import_from_realtime(
    state: State<'_, AppState>,
    realtime_session_id: String,
) -> Result<String, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let file_id = uuid::Uuid::new_v4().to_string();
    let session_id = uuid::Uuid::new_v4().to_string();

    state.db.studio_create_session(&StudioSession {
        id: session_id.clone(),
        session_type: "audio".to_string(),
        name: format!("实时翻译导入_{}", &realtime_session_id[..8.min(realtime_session_id.len())]),
        directory_path: String::new(),
        canvas_mode: String::new(),
        media_kind: String::new(),
        is_deleted: false,
        created_at: now.clone(),
        updated_at: now.clone(),
        last_accessed_at: Some(now.clone()),
        source_session_id: Some(realtime_session_id),
        source_session_name: None,
        source_session_directory_name: None,
        source_asset_id: None,
        source_asset_kind: None,
        source_asset_file_name: None,
        source_asset_path: None,
        source_preview_path: None,
        source_reference_role: None,
        message_count: 0,
        task_count: 0,
        asset_count: 0,
        latest_message_preview: None,
        legacy_source_path: None,
        import_batch_id: None,
        imported_at: Some(now),
        is_legacy_import: false,
    }).await.map_err(|e| e.to_string())?;

    Ok(file_id)
}

// ── 字幕格式化工具函数 ──

fn format_srt(segments: &[AudioSegment]) -> String {
    let mut out = String::new();
    for (i, s) in segments.iter().enumerate() {
        out.push_str(&format!("{}\n", i + 1));
        out.push_str(&format!("{} --> {}\n", ms_to_srt_time(s.start_ms), ms_to_srt_time(s.end_ms)));
        out.push_str(&format!("{}\n\n", s.text));
    }
    out
}

fn format_vtt(segments: &[AudioSegment]) -> String {
    let mut out = String::from("WEBVTT\n\n");
    for s in segments {
        out.push_str(&format!("{} --> {}\n", ms_to_vtt_time(s.start_ms), ms_to_vtt_time(s.end_ms)));
        out.push_str(&format!("{}\n\n", s.text));
    }
    out
}

fn ms_to_srt_time(ms: i64) -> String {
    let h = ms / 3_600_000;
    let m = (ms % 3_600_000) / 60_000;
    let s = (ms % 60_000) / 1_000;
    let millis = ms % 1_000;
    format!("{:02}:{:02}:{:02},{:03}", h, m, s, millis)
}

fn ms_to_vtt_time(ms: i64) -> String {
    let h = ms / 3_600_000;
    let m = (ms % 3_600_000) / 60_000;
    let s = (ms % 60_000) / 1_000;
    let millis = ms % 1_000;
    format!("{:02}:{:02}:{:02}.{:03}", h, m, s, millis)
}

#[cfg(test)]
mod tests {
    use super::*;
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
    fn test_ms_to_srt_time_zero() {
        assert_eq!(ms_to_srt_time(0), "00:00:00,000");
    }

    #[test]
    fn test_ms_to_srt_time_complex() {
        assert_eq!(ms_to_srt_time(3661234), "01:01:01,234");
        assert_eq!(ms_to_srt_time(999), "00:00:00,999");
        assert_eq!(ms_to_srt_time(60000), "00:01:00,000");
    }

    #[test]
    fn test_ms_to_vtt_time_zero() {
        assert_eq!(ms_to_vtt_time(0), "00:00:00.000");
    }

    #[test]
    fn test_ms_to_vtt_time_complex() {
        assert_eq!(ms_to_vtt_time(3661234), "01:01:01.234");
    }

    #[test]
    fn test_estimate_audio_duration_wav() {
        // WAV: bytes_per_sec = 16000 * 1 * 2 = 32000
        // (32044 - 44) * 1000 / 32000 = 1000
        assert_eq!(estimate_audio_duration_ms(Path::new("test.wav"), 32044), 1000);
        // Header only
        assert_eq!(estimate_audio_duration_ms(Path::new("test.wav"), 44), 0);
        // Smaller than header
        assert_eq!(estimate_audio_duration_ms(Path::new("test.wav"), 20), 0);
    }

    #[test]
    fn test_estimate_audio_duration_mp3() {
        // MP3: bytes_per_sec = 16000
        // 16000 * 1000 / 16000 = 1000
        assert_eq!(estimate_audio_duration_ms(Path::new("test.mp3"), 16000), 1000);
    }

    #[test]
    fn test_estimate_audio_duration_unknown_ext() {
        // Unknown ext uses same default formula as mp3
        assert_eq!(estimate_audio_duration_ms(Path::new("test.flac"), 16000), 1000);
    }

    #[test]
    fn test_format_srt_basic() {
        let segs = vec![
            make_seg(1, 0, 1500, "Hello world"),
            make_seg(2, 2000, 4000, "Second line"),
        ];
        let out = format_srt(&segs);
        assert!(out.contains("1\n"));
        assert!(out.contains("00:00:00,000 --> 00:00:01,500\n"));
        assert!(out.contains("Hello world\n"));
        assert!(out.contains("2\n"));
        assert!(out.contains("00:00:02,000 --> 00:00:04,000\n"));
        assert!(out.contains("Second line\n"));
    }

    #[test]
    fn test_format_vtt_basic() {
        let segs = vec![
            make_seg(1, 0, 1500, "Hello world"),
            make_seg(2, 2000, 4000, "Second line"),
        ];
        let out = format_vtt(&segs);
        assert!(out.starts_with("WEBVTT\n\n"));
        assert!(out.contains("00:00:00.000 --> 00:00:01.500\n"));
        assert!(out.contains("Hello world\n"));
        assert!(out.contains("00:00:02.000 --> 00:00:04.000\n"));
        assert!(out.contains("Second line\n"));
    }

    #[test]
    fn test_format_srt_empty() {
        assert_eq!(format_srt(&[]), "");
    }
}
