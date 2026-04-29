use std::path::Path;
use tfp_core::{
    AudioFile, AudioLabBundle, AudioPlaybackInfo, AudioStageOutput,
    StudioSession, StudioTask,
};
use tfp_storage::Database;

use crate::audio_util::estimate_audio_duration_ms;
use crate::format::{format_srt, format_txt, format_vtt};

/// Import audio files from disk: compute sha256, estimate duration, persist to DB.
/// Returns the list of created file IDs.
pub async fn import_files(
    db: &Database,
    paths: &[String],
) -> Result<Vec<String>, String> {
    let mut file_ids = Vec::new();
    let now = chrono::Utc::now().to_rfc3339();
    let batch_id = uuid::Uuid::new_v4().to_string();

    for path_str in paths {
        let p = Path::new(path_str);
        if !p.exists() {
            return Err(format!("文件不存在: {}", path_str));
        }

        let display_name = p
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("unknown")
            .to_string();

        let metadata =
            std::fs::metadata(p).map_err(|e| format!("读取文件元数据失败: {e}"))?;
        let file_size_bytes = metadata.len() as i64;

        let file_bytes =
            std::fs::read(p).map_err(|e| format!("读取文件失败: {e}"))?;
        let sha256 = {
            use sha2::Digest;
            format!("{:x}", sha2::Sha256::digest(&file_bytes))
        };

        let duration_ms = estimate_audio_duration_ms(p, file_size_bytes);

        let file_id = uuid::Uuid::new_v4().to_string();
        let session_id = uuid::Uuid::new_v4().to_string();

        let audio_file = AudioFile {
            id: file_id.clone(),
            display_name: display_name.clone(),
            source_path: path_str.clone(),
            mp3_path: if path_str.to_lowercase().ends_with(".mp3") {
                Some(path_str.clone())
            } else {
                None
            },
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

        db.audiolab_insert_file(&audio_file)
            .await
            .map_err(|e| e.to_string())?;

        db.studio_create_session(&StudioSession {
            id: session_id.clone(),
            session_type: "audio".to_string(),
            name: display_name,
            directory_path: p
                .parent()
                .map(|pp| pp.to_string_lossy().to_string())
                .unwrap_or_default(),
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
        })
        .await
        .map_err(|e| e.to_string())?;

        file_ids.push(file_id);
    }
    Ok(file_ids)
}

/// Import from a realtime translation session.
/// Creates a new studio session linked to the source.
pub async fn import_from_realtime(
    db: &Database,
    realtime_session_id: &str,
) -> Result<String, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let file_id = uuid::Uuid::new_v4().to_string();
    let session_id = uuid::Uuid::new_v4().to_string();

    db.studio_create_session(&StudioSession {
        id: session_id.clone(),
        session_type: "audio".to_string(),
        name: format!(
            "实时翻译导入_{}",
            &realtime_session_id[..8.min(realtime_session_id.len())]
        ),
        directory_path: String::new(),
        canvas_mode: String::new(),
        media_kind: String::new(),
        is_deleted: false,
        created_at: now.clone(),
        updated_at: now.clone(),
        last_accessed_at: Some(now.clone()),
        source_session_id: Some(realtime_session_id.to_string()),
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
    })
    .await
    .map_err(|e| e.to_string())?;

    Ok(file_id)
}

/// Assemble an AudioLab workspace bundle (lazy-loaded from DB).
pub async fn get_bundle(
    db: &Database,
    session_id: &str,
) -> Result<AudioLabBundle, String> {
    let session = db
        .studio_get_session(session_id)
        .await
        .map_err(|e| e.to_string())?
        .ok_or_else(|| format!("找不到 session: {}", session_id))?;

    let transcript = db
        .audiolab_get_transcript(session_id)
        .await
        .map_err(|e| e.to_string())?;

    let file = if let Some(ref asset_id) = session.source_asset_id {
        db.audiolab_get_file(asset_id)
            .await
            .map_err(|e| e.to_string())?
    } else if let Some(ref t) = transcript {
        db.audiolab_get_file(&t.audio_file_id)
            .await
            .map_err(|e| e.to_string())?
    } else {
        None
    };

    let file = file.ok_or_else(|| "找不到关联音频文件".to_string())?;

    let segments = if let Some(ref t) = transcript {
        db.audiolab_get_segments(&t.id)
            .await
            .map_err(|e| e.to_string())?
    } else {
        vec![]
    };

    let auto_tags = db
        .audiolab_get_auto_tags(session_id)
        .await
        .map_err(|e| e.to_string())?;
    let stage_outputs = db
        .audiolab_get_stage_outputs(session_id)
        .await
        .map_err(|e| e.to_string())?;
    let research_topics = db
        .audiolab_get_research_topics(session_id)
        .await
        .map_err(|e| e.to_string())?;
    let custom_presets = db
        .audiolab_list_stage_presets()
        .await
        .map_err(|e| e.to_string())?;

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

/// Resolve the playback path for a session's audio.
pub async fn playback_open(
    db: &Database,
    session_id: &str,
) -> Result<AudioPlaybackInfo, String> {
    let session = db
        .studio_get_session(session_id)
        .await
        .map_err(|e| e.to_string())?
        .ok_or_else(|| format!("找不到 session: {}", session_id))?;

    let file = if let Some(ref asset_id) = session.source_asset_id {
        db.audiolab_get_file(asset_id)
            .await
            .map_err(|e| e.to_string())?
    } else {
        let transcript = db
            .audiolab_get_transcript(session_id)
            .await
            .map_err(|e| e.to_string())?;
        if let Some(ref t) = transcript {
            db.audiolab_get_file(&t.audio_file_id)
                .await
                .map_err(|e| e.to_string())?
        } else {
            None
        }
    };

    let file = file.ok_or_else(|| "找不到关联音频文件".to_string())?;
    let playback_path = file
        .mp3_path
        .clone()
        .unwrap_or_else(|| file.source_path.clone());

    let _ = db.audiolab_update_last_opened(&file.id).await;

    Ok(AudioPlaybackInfo {
        file_id: file.id,
        playback_path,
        duration_ms: file.duration_ms,
        display_name: file.display_name,
    })
}

/// Generate export content for a given session and format.
///
/// Supported targets: `"srt"`, `"vtt"`, `"txt"`, `"json"`, `"markdown"` (requires `stage_key`).
pub async fn export_content(
    db: &Database,
    session_id: &str,
    target: &str,
    stage_key: Option<&str>,
) -> Result<String, String> {
    match target {
        "srt" | "vtt" => {
            let transcript = db
                .audiolab_get_transcript(session_id)
                .await
                .map_err(|e| e.to_string())?
                .ok_or("无转录数据")?;
            let segments = db
                .audiolab_get_segments(&transcript.id)
                .await
                .map_err(|e| e.to_string())?;
            if target == "srt" {
                Ok(format_srt(&segments))
            } else {
                Ok(format_vtt(&segments))
            }
        }
        "txt" => {
            let transcript = db
                .audiolab_get_transcript(session_id)
                .await
                .map_err(|e| e.to_string())?
                .ok_or("无转录数据")?;
            let segments = db
                .audiolab_get_segments(&transcript.id)
                .await
                .map_err(|e| e.to_string())?;
            Ok(format_txt(&segments))
        }
        "json" => {
            let transcript = db
                .audiolab_get_transcript(session_id)
                .await
                .map_err(|e| e.to_string())?
                .ok_or("无转录数据")?;
            let segments = db
                .audiolab_get_segments(&transcript.id)
                .await
                .map_err(|e| e.to_string())?;
            serde_json::to_string_pretty(&segments).map_err(|e| e.to_string())
        }
        "markdown" => {
            let key = stage_key.ok_or("导出 markdown 需指定 stage_key")?;
            let outputs = db
                .audiolab_get_stage_outputs(session_id)
                .await
                .map_err(|e| e.to_string())?;
            Ok(outputs
                .iter()
                .find(|o| o.stage_key == key)
                .map(|o| o.content_markdown.clone())
                .unwrap_or_default())
        }
        _ => Err(format!("不支持的导出格式: {}", target)),
    }
}

/// Submit a task for a given session (generic audiolab task creation).
pub async fn submit_task(
    db: &Database,
    session_id: &str,
    task_type: &str,
    prompt: &str,
) -> Result<StudioTask, String> {
    let task_id = uuid::Uuid::new_v4().to_string();
    let now = chrono::Utc::now().to_rfc3339();

    let task = StudioTask {
        id: task_id,
        session_id: session_id.to_string(),
        task_type: task_type.to_string(),
        status: "pending".to_string(),
        prompt: prompt.to_string(),
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
    db.studio_upsert_task(&task)
        .await
        .map_err(|e| e.to_string())?;

    Ok(task)
}

/// Create a stage output placeholder (status = Processing).
pub async fn create_stage_output(
    db: &Database,
    session_id: &str,
    stage_key: &str,
    model_ref: Option<&str>,
) -> Result<AudioStageOutput, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let output = AudioStageOutput {
        id: uuid::Uuid::new_v4().to_string(),
        session_id: session_id.to_string(),
        stage_key: stage_key.to_string(),
        content_markdown: String::new(),
        status: "Processing".to_string(),
        error_message: None,
        model_ref: model_ref.map(|s| s.to_string()),
        generated_at: Some(now),
        custom_stage_key: if stage_key.starts_with("Custom:") {
            Some(stage_key.trim_start_matches("Custom:").to_string())
        } else {
            None
        },
        custom_is_mindmap: None,
    };
    db.audiolab_upsert_stage_output(&output)
        .await
        .map_err(|e| e.to_string())?;
    Ok(output)
}

/// Update stage content (edit-mode save).
pub async fn update_stage_content(
    db: &Database,
    session_id: &str,
    stage_key: &str,
    content: &str,
) -> Result<(), String> {
    let outputs = db
        .audiolab_get_stage_outputs(session_id)
        .await
        .map_err(|e| e.to_string())?;
    if let Some(existing) = outputs.iter().find(|o| o.stage_key == stage_key) {
        let mut updated = existing.clone();
        updated.content_markdown = content.to_string();
        updated.status = "Ready".to_string();
        db.audiolab_upsert_stage_output(&updated)
            .await
            .map_err(|e| e.to_string())?;
    } else {
        let output = AudioStageOutput {
            id: uuid::Uuid::new_v4().to_string(),
            session_id: session_id.to_string(),
            stage_key: stage_key.to_string(),
            content_markdown: content.to_string(),
            status: "Ready".to_string(),
            error_message: None,
            model_ref: None,
            generated_at: Some(chrono::Utc::now().to_rfc3339()),
            custom_stage_key: None,
            custom_is_mindmap: None,
        };
        db.audiolab_upsert_stage_output(&output)
            .await
            .map_err(|e| e.to_string())?;
    }
    Ok(())
}
