use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use tokio::sync::mpsc;
use tauri::Emitter;
use crate::storage::Database;
use crate::task_event_bus::{TaskEventBus, TaskBusEvent};

pub struct TaskEngine {
    kick_tx: mpsc::Sender<()>,
}

/// PR-1.8: 辅助 — 在状态变更后通知监控视图刷新
fn notify_monitor(app_handle: &tauri::AppHandle, bus: &TaskEventBus, event: TaskBusEvent) {
    bus.publish(event);
    // 节流由 TaskEventBus 的 last_emit 控制（见下方 emit_throttled）
    emit_throttled(app_handle);
}

/// PR-1.8: 500ms 节流 emit monitor-snapshot-update
/// 使用 thread-local 不可行（async），用简单的 AtomicU64 时间戳
static LAST_MONITOR_EMIT_MS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
fn emit_throttled(app_handle: &tauri::AppHandle) {
    let now_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64;
    let last = LAST_MONITOR_EMIT_MS.load(std::sync::atomic::Ordering::Relaxed);
    if now_ms.saturating_sub(last) >= 500 {
        LAST_MONITOR_EMIT_MS.store(now_ms, std::sync::atomic::Ordering::Relaxed);
        let _ = app_handle.emit("monitor-snapshot-update", serde_json::Value::Null);
    }
}

/// PR-1.5: 从 KV 读取并发上限（M-4: 拆分为独立的转录和 AI 上限）
async fn read_concurrency_limits(storage: &Database) -> (usize, usize) {
    let tc = storage.kv_get("monitor.max_transcription_concurrency").await
        .ok().flatten()
        .and_then(|v| v.parse::<usize>().ok())
        .unwrap_or(2);
    let ac = storage.kv_get("monitor.max_ai_concurrency").await
        .ok().flatten()
        .and_then(|v| v.parse::<usize>().ok())
        .unwrap_or(4);
    (tc.max(1), ac.max(1))
}

/// M-4: 判断任务是否为转录类型
fn is_transcription_task(task_type: &str) -> bool {
    task_type == "Transcription" || task_type == "audio_transcribe"
}

/// PR-1.5: 从 KV 读取超时（分钟 → 秒）
async fn read_timeout_secs(storage: &Database) -> u64 {
    let minutes = storage.kv_get("monitor.transcription_timeout_minutes").await
        .ok().flatten()
        .and_then(|v| v.parse::<u64>().ok())
        .unwrap_or(10);
    (minutes * 60).max(60)
}

impl TaskEngine {
    /// PR-1.5: 从 KV 动态读取并发上限 + 超时，支持热更新
    pub fn start_with_app(
        app_handle: tauri::AppHandle,
        storage: Arc<Database>,
    ) -> Self {
        let (kick_tx, mut kick_rx) = mpsc::channel::<()>(32);
        // M-4: 拆分为独立的转录和 AI 活跃计数器
        let active_transcription = Arc::new(AtomicUsize::new(0));
        let active_ai = Arc::new(AtomicUsize::new(0));

        tokio::spawn(async move {
            // O-25: 启动时恢复中断的任务
            match storage.recover_interrupted_tasks().await {
                Ok(count) if count > 0 => {
                    eprintln!("[TaskEngine] O-25: 已恢复 {count} 个中断任务为 Queued 状态");
                }
                Err(e) => {
                    eprintln!("[TaskEngine] O-25: recover_interrupted_tasks error: {e}");
                }
                _ => {}
            }

            // PR-4.7: 启动时标记 interrupted（last_heartbeat_at 过期的 Executing 任务）
            match storage.monitor_recover_interrupted().await {
                Ok(count) if count > 0 => {
                    eprintln!("[TaskEngine] PR-4.7: 已标记 {count} 个中断任务为 Interrupted");
                }
                Err(e) => eprintln!("[TaskEngine] monitor_recover_interrupted error: {e}"),
                _ => {}
            }

            loop {
                let _ = tokio::time::timeout(
                    std::time::Duration::from_secs(5),
                    kick_rx.recv(),
                ).await;

                // M-4: 每次循环从 KV 读取独立的转录和 AI 并发上限
                let (max_transcription, max_ai) = read_concurrency_limits(&storage).await;
                let timeout_secs = read_timeout_secs(&storage).await;

                loop {
                    // 取一条 Queued 任务
                    let task = match storage.get_next_queued_task().await {
                        Ok(Some(t)) => t,
                        Ok(None) => {
                            break; // 没有待执行任务
                        }
                        Err(e) => {
                            eprintln!("[TaskEngine] get_next_queued_task error: {e}");
                            break;
                        }
                    };

                    // M-4: 根据任务类型检查对应的并发计数器
                    let is_transcription = is_transcription_task(&task.task_type);
                    let (counter, limit) = if is_transcription {
                        (&active_transcription, max_transcription)
                    } else {
                        (&active_ai, max_ai)
                    };

                    let current = counter.load(Ordering::Acquire);
                    if current >= limit {
                        // 该类型并发已满，跳过（任务留在队列中等下次调度）
                        break;
                    }

                    // 标记 Executing
                    if let Err(e) = storage.update_task_status_new(&task.id, "Executing", None).await {
                        eprintln!("[TaskEngine] update_task_status error: {e}");
                        continue;
                    }

                    // M-4: 递增对应类型的活跃计数
                    counter.fetch_add(1, Ordering::Release);

                    // 发事件: Started
                    let _ = app_handle.emit("task-event", serde_json::json!({
                        "type": "TaskStarted",
                        "task_id": task.id,
                        "audio_item_id": task.audio_item_id,
                        "stage": task.stage,
                    }));
                    // PR-1.8: 通知监控视图
                    {
                        use tauri::Manager;
                        let bus = &app_handle.state::<crate::state::AppState>().task_event_bus;
                        notify_monitor(&app_handle, bus, TaskBusEvent::Started {
                            task_id: task.id.clone(),
                            stage: task.stage.clone(),
                        });
                    }

                    // P3-8: 在独立 tokio task 中执行（并发）
                    let app = app_handle.clone();
                    let db = storage.clone();
                    // M-4: 根据任务类型选择对应计数器
                    let active = if is_transcription {
                        active_transcription.clone()
                    } else {
                        active_ai.clone()
                    };
                    let task_timeout_secs = timeout_secs;
                    tokio::spawn(async move {
                        // M-4: 任务结束时递减对应类型的活跃计数
                        struct ActiveGuard(Arc<AtomicUsize>);
                        impl Drop for ActiveGuard {
                            fn drop(&mut self) { self.0.fetch_sub(1, Ordering::Release); }
                        }
                        let _guard = ActiveGuard(active);

                        let task_id = task.id.clone();
                        let stage = task.stage.clone();

                        // RV-O3: 计费记录 — Staging 阶段
                        let billing_id = uuid::Uuid::new_v4().to_string();
                        let billing_rec = crate::models::BillingRecord {
                            id: billing_id.clone(),
                            task_id: Some(task_id.clone()),
                            endpoint_id: String::new(), // execute_task_real 内部选择端点
                            model_id: String::new(),
                            prompt_tokens: 0,
                            completion_tokens: 0,
                            cost_usd: None,
                            created_at: chrono::Utc::now().to_rfc3339(),
                            status: "Staging".to_string(),
                        };
                        let _ = db.add_billing_record(&billing_rec).await;

                        // RV-O3: 计费记录 → Running
                        let _ = db.update_billing_status(&billing_id, "Running").await;

                        // B-03: 在执行前记录 started_at
                        let started_at = chrono::Utc::now().to_rfc3339();

                        // PR-1.5: 超时控制（从 KV 读取的值）
                        let start = std::time::Instant::now();
                        let timeout_dur = std::time::Duration::from_secs(task_timeout_secs);
                        let result = tokio::time::timeout(
                            timeout_dur,
                            execute_task_real(&app, &db, &task),
                        ).await;

                        let duration_ms = start.elapsed().as_millis() as i64;
                        let completed_at = chrono::Utc::now().to_rfc3339();

                        // PR-1.5: 将 timeout 转为统一错误 + 标记 Timeout 状态
                        let result = match result {
                            Ok(inner) => inner,
                            Err(_) => Err(format!(
                                "任务超时: 超过 {task_timeout_secs} 秒未完成"
                            ).into()),
                        };

                        match result {
                            Ok((result_text, prompt_tokens, completion_tokens)) => {
                                let _ = db.update_task_status_new(&task_id, "Completed", None).await;
                                let lc = crate::models::AudioLifecycleRow {
                                    id: format!("{}-{}", task.audio_item_id, stage),
                                    audio_item_id: task.audio_item_id.clone(),
                                    stage: stage.clone(),
                                    status: "Completed".to_string(),
                                    result_text: Some(result_text),
                                    result_json: None,
                                    model_id: None,
                                    token_used: Some((prompt_tokens + completion_tokens) as i64),
                                    error: None,
                                    started_at: Some(started_at.clone()),
                                    completed_at: Some(completed_at.clone()),
                                };
                                let _ = db.upsert_lifecycle(&lc).await;
                                let exec = crate::models::TaskExecutionRow {
                                    id: uuid::Uuid::new_v4().to_string(),
                                    task_id: task_id.clone(),
                                    attempt: task.retry_count + 1,
                                    status: "Completed".to_string(),
                                    error: None,
                                    prompt_tokens: Some(prompt_tokens as i64),
                                    completion_tokens: Some(completion_tokens as i64),
                                    duration_ms: Some(duration_ms),
                                    started_at: started_at.clone(),
                                    completed_at: Some(completed_at),
                                };
                                let _ = db.add_task_execution(&exec).await;

                                // RV-O3: 计费记录 → Landed（记录实际 token）→ Committed
                                let _ = db.update_billing_status(&billing_id, "Landed").await;
                                // 更新 token 数据
                                let _ = db.update_billing_tokens(
                                    &billing_id, prompt_tokens as i64, completion_tokens as i64,
                                ).await;
                                let _ = db.update_billing_status(&billing_id, "Committed").await;

                                // P3-10: DAG 级联失效 — 完成阶段后重置下游
                                if let Err(e) = db.invalidate_downstream_stages(&task.audio_item_id, &stage).await {
                                    eprintln!("[TaskEngine] invalidate_downstream error: {e}");
                                }

                                let _ = app.emit("task-event", serde_json::json!({
                                    "type": "TaskCompleted",
                                    "task_id": task_id,
                                    "stage": stage,
                                }));
                                // PR-1.8: 通知监控视图
                                {
                                    use tauri::Manager;
                                    let bus = &app.state::<crate::state::AppState>().task_event_bus;
                                    notify_monitor(&app, bus, TaskBusEvent::Completed {
                                        task_id: task_id.clone(),
                                        stage: stage.clone(),
                                    });
                                }
                            }
                            Err(err) => {
                                let err_msg = err.to_string();
                                if task.retry_count < task.max_retries {
                                    let _ = db.increment_retry_and_requeue(&task_id, &err_msg).await;
                                } else {
                                    let _ = db.update_task_status_new(&task_id, "Failed", Some(&err_msg)).await;
                                    let lc = crate::models::AudioLifecycleRow {
                                        id: format!("{}-{}", task.audio_item_id, stage),
                                        audio_item_id: task.audio_item_id.clone(),
                                        stage: stage.clone(),
                                        status: "Failed".to_string(),
                                        result_text: None,
                                        result_json: None,
                                        model_id: None,
                                        token_used: None,
                                        error: Some(err_msg.clone()),
                                        started_at: Some(started_at.clone()),
                                        completed_at: Some(completed_at.clone()),
                                    };
                                    let _ = db.upsert_lifecycle(&lc).await;
                                }
                                let exec = crate::models::TaskExecutionRow {
                                    id: uuid::Uuid::new_v4().to_string(),
                                    task_id: task_id.clone(),
                                    attempt: task.retry_count + 1,
                                    status: "Failed".to_string(),
                                    error: Some(err_msg.clone()),
                                    prompt_tokens: None,
                                    completion_tokens: None,
                                    duration_ms: Some(duration_ms),
                                    started_at: started_at.clone(),
                                    completed_at: Some(completed_at),
                                };
                                let _ = db.add_task_execution(&exec).await;

                                // RV-O3: 计费记录 → Failed（任务失败不计入 Committed）
                                let _ = db.update_billing_status(&billing_id, "Failed").await;

                                let _ = app.emit("task-event", serde_json::json!({
                                    "type": "TaskFailed",
                                    "task_id": task_id,
                                    "error": err_msg,
                                }));
                                // PR-1.8: 通知监控视图
                                {
                                    use tauri::Manager;
                                    let bus = &app.state::<crate::state::AppState>().task_event_bus;
                                    notify_monitor(&app, bus, TaskBusEvent::Failed {
                                        task_id: task_id.clone(),
                                        error: err_msg.clone(),
                                    });
                                }
                            }
                        }
                    });
                } // inner loop
            } // outer loop
        });

        TaskEngine { kick_tx }
    }

    /// 唤醒引擎处理新任务
    pub async fn kick(&self) {
        let _ = self.kick_tx.send(()).await;
    }
}

/// 根据 task_type 分派到真实 Provider 执行
/// 返回 (result_text, prompt_tokens, completion_tokens)
async fn execute_task_real(
    app_handle: &tauri::AppHandle,
    storage: &Arc<Database>,
    task: &crate::models::AudioTaskRow,
) -> Result<(String, u32, u32), Box<dyn std::error::Error + Send + Sync>> {
    use tauri::Manager;
    let state = app_handle.state::<crate::state::AppState>();

    match task.task_type.as_str() {
        "Transcription" => {
            // 读取音频文件数据
            let audio_item = storage.get_audio_item(&task.audio_item_id).await
                .map_err(|e| format!("Failed to get audio item: {e}"))?
                .ok_or("Audio item not found")?;

            let audio_data = std::fs::read(&audio_item.file_path)
                .map_err(|e| format!("Failed to read audio file: {e}"))?;

            let source_lang = &audio_item.source_lang;

            // 找到 STT provider
            let providers = state.providers.read().await;
            let config = state.config.read().await;
            let stt_ep = config.endpoints.iter()
                .find(|ep| ep.enabled && ep.endpoint_type == crate::models::EndpointType::AzureSpeech);

            if let Some(ep) = stt_ep {
                if let Some(stt) = providers.get_stt(&ep.id) {
                    drop(providers);
                    drop(config);
                    let segments = stt.transcribe(&audio_data, source_lang).await
                        .map_err(|e| format!("STT failed: {e}"))?;
                    let text = segments.iter().map(|s| s.text.clone()).collect::<Vec<_>>().join("\n");
                    Ok((text, 0, 0))
                } else {
                    Err("STT provider not found in registry".into())
                }
            } else {
                Err("No Azure Speech endpoint configured".into())
            }
        }
        "AiCompletion" => {
            // 需要前置阶段的结果作为输入
            let prev_content = get_previous_stage_content(storage, task).await?;

            let system_prompt = match task.stage.as_str() {
                "Summarized" => "你是一个专业的音频内容总结助手。请根据转录文本，生成结构化的要点总结。使用 Markdown 格式。",
                "MindMap" => "你是一个思维导图生成助手。请根据内容生成 JSON 格式的思维导图树: {\"label\": \"主题\", \"children\": [{\"label\": \"子主题\", \"children\": [...]}]}。只返回 JSON。",
                "Insight" => "你是一个深度分析助手。请从内容中挖掘关键洞察、隐含模式和可行建议。使用 Markdown 格式。",
                "Research" => "你是一个深度研究助手。请围绕内容主题进行延伸分析和背景研究，提供相关知识和参考。使用 Markdown 格式。",
                "PodcastScript" => "你是一个播客台本写作助手。请将内容改编为引人入胜的双人对话播客台本。格式: 主持人A: ... 主持人B: ...",
                "Translated" => "你是一个专业翻译助手。请将以下内容翻译为英文，保持原文格式和语义。",
                _ => "请处理以下内容。",
            };

            let providers = state.providers.read().await;
            let config = state.config.read().await;

            // 找到合适的 AI completion provider
            let ai_ep = config.endpoints.iter()
                .find(|ep| ep.enabled && matches!(
                    ep.endpoint_type,
                    crate::models::EndpointType::AzureOpenAi
                    | crate::models::EndpointType::OpenAiCompatible
                    | crate::models::EndpointType::ApiManagementGateway
                ))
                .cloned();

            let summary_model_id = config.ai.summary_model.model_id.clone();
            drop(config);

            if let Some(ep) = ai_ep {
                if let Some(ai) = providers.get_ai_completion(&ep.id) {
                    drop(providers);

                    let model = if summary_model_id.is_empty() {
                        ep.models.iter()
                            .find(|m| m.capabilities.contains(&crate::models::ModelCapability::Text))
                            .map(|m| m.model_id.clone())
                            .unwrap_or_else(|| "gpt-4.1".to_string())
                    } else {
                        summary_model_id
                    };

                    let request = crate::models::CompletionRequest {
                        messages: vec![
                            crate::models::ChatMessage { role: "system".into(), content: serde_json::Value::String(system_prompt.to_string()) },
                            crate::models::ChatMessage { role: "user".into(), content: serde_json::Value::String(prev_content) },
                        ],
                        model,
                        temperature: Some(0.7),
                        max_tokens: Some(4096),
                        endpoint_id: ep.id.clone(),
                    };

                    let resp = ai.complete(&request).await
                        .map_err(|e| format!("AI completion failed: {e}"))?;

                    let prompt_tokens = resp.usage.as_ref().map(|u| u.prompt_tokens).unwrap_or(0);
                    let completion_tokens = resp.usage.as_ref().map(|u| u.completion_tokens).unwrap_or(0);

                    Ok((resp.content, prompt_tokens, completion_tokens))
                } else {
                    Err("AI completion provider not found in registry".into())
                }
            } else {
                Err("No AI endpoint configured".into())
            }
        }
        "TTS" => {
            // 读取播客台本
            let script = get_stage_content(storage, &task.audio_item_id, "PodcastScript").await?;
            if script.is_empty() {
                return Err("PodcastScript stage has no content — please generate it first".into());
            }

            let providers = state.providers.read().await;
            let config = state.config.read().await;
            let tts_ep = config.endpoints.iter()
                .find(|ep| ep.enabled && ep.endpoint_type == crate::models::EndpointType::AzureSpeech);

            if let Some(ep) = tts_ep {
                if let Some(tts) = providers.get_tts(&ep.id) {
                    drop(providers);
                    drop(config);

                    // P3-7-FIX: 检测多发言人格式（"角色:" 或 "角色：" 开头的行）
                    let speakers = detect_speakers(&script);
                    let audio_bytes = if speakers.len() >= 2 {
                        tracing::info!("[TaskEngine] TTS: detected {} speakers, using multi-speaker", speakers.len());
                        tts.synthesize_multi_speaker(&script, &speakers, "wav").await
                            .map_err(|e| format!("TTS multi-speaker failed: {e}"))?
                    } else {
                        let voice = speakers.first()
                            .map(|(_, v)| v.as_str())
                            .unwrap_or("zh-CN-XiaoxiaoMultilingualNeural");
                        tts.synthesize(&script, voice, "wav").await
                            .map_err(|e| format!("TTS failed: {e}"))?
                    };

                    // 保存音频文件
                    let out_dir = app_handle.path().app_data_dir()
                        .unwrap_or_else(|_| std::path::PathBuf::from("."));
                    let _ = std::fs::create_dir_all(&out_dir);
                    let out_path = out_dir.join(format!("podcast_{}.wav", task.audio_item_id));
                    std::fs::write(&out_path, &audio_bytes)
                        .map_err(|e| format!("Failed to write TTS output: {e}"))?;

                    Ok((out_path.to_string_lossy().to_string(), 0, 0))
                } else {
                    Err("TTS provider not found in registry".into())
                }
            } else {
                Err("No Azure Speech endpoint configured".into())
            }
        }
        "ImageGeneration" => {
            let prompt = task.prompt_text.as_deref().unwrap_or("Generate an image");
            let providers = state.providers.read().await;
            let config = state.config.read().await;

            let img_ep = config.endpoints.iter()
                .find(|ep| ep.enabled && matches!(
                    ep.endpoint_type,
                    crate::models::EndpointType::AzureOpenAi
                    | crate::models::EndpointType::OpenAiCompatible
                    | crate::models::EndpointType::ApiManagementGateway
                ))
                .cloned();

            let img_model = config.media.image_model.model_id.clone();
            drop(config);

            if let Some(ep) = img_ep {
                if let Some(img) = providers.get_image_gen(&ep.id) {
                    drop(providers);
                    let model = if img_model.is_empty() { "gpt-image-2".to_string() } else { img_model };

                    let request = crate::models::ImageGenRequest {
                        prompt: prompt.to_string(),
                        width: 1024,
                        height: 1024,
                        model,
                        quality: Some("auto".into()),
                        output_format: Some("png".into()),
                        background: None,
                        n: None,
                        endpoint_id: ep.id.clone(),
                        text_model: None,
                        image_model: None,
                        previous_response_id: None,
                    };

                    let results = img.generate(&request).await
                        .map_err(|e| format!("Image gen failed: {e}"))?;

                    let summary = results.first()
                        .and_then(|r| r.revised_prompt.clone())
                        .unwrap_or_else(|| prompt.to_string());

                    Ok((summary, 0, 0))
                } else {
                    Err("Image gen provider not found".into())
                }
            } else {
                Err("No AI endpoint configured for image generation".into())
            }
        }
        other => {
            Err(format!("Unknown task type: {other}").into())
        }
    }
}

/// 获取前置阶段的内容用作当前阶段的输入
async fn get_previous_stage_content(
    storage: &Arc<Database>,
    task: &crate::models::AudioTaskRow,
) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
    let dep_stage = match task.stage.as_str() {
        "Summarized" | "Translated" => "Transcribed",
        "MindMap" | "Insight" => "Summarized",
        "Research" => "Insight",
        "PodcastScript" => "Summarized",
        _ => "Transcribed",
    };
    get_stage_content(storage, &task.audio_item_id, dep_stage).await
}

async fn get_stage_content(
    storage: &Arc<Database>,
    audio_item_id: &str,
    stage: &str,
) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
    let lifecycles = storage.get_audio_lifecycle(audio_item_id).await
        .map_err(|e| format!("Failed to get lifecycle: {e}"))?;
    let lc = lifecycles.iter().find(|l| l.stage == stage);
    match lc {
        Some(l) if l.status == "Completed" => {
            l.result_text.clone().ok_or_else(|| format!("Stage {stage} completed but has no result text").into())
        }
        Some(l) => Err(format!("Stage {stage} is not completed (status: {})", l.status).into()),
        None => Err(format!("Dependency stage {stage} has not been processed yet").into()),
    }
}

/// P3-7-FIX: 从播客台本中检测发言人角色
///
/// 扫描文本行中的 "角色名: " 或 "角色名：" 模式，提取去重的角色列表。
/// 为每个角色分配不同的语音（交替使用男女声）。
fn detect_speakers(script: &str) -> Vec<(String, String)> {
    let voices = [
        "zh-CN-XiaoxiaoMultilingualNeural",
        "zh-CN-YunxiMultilingualNeural",
        "zh-CN-XiaoyiNeural",
        "zh-CN-YunjianNeural",
    ];

    let mut seen: Vec<String> = Vec::new();
    for line in script.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        // 匹配 "标签: " 或 "标签：" 格式
        let label = if let Some(pos) = trimmed.find(": ") {
            let candidate = &trimmed[..pos];
            // 角色标签一般不超过 20 字符且不含换行
            if candidate.len() <= 40 && !candidate.contains('\n') {
                Some(candidate.to_string())
            } else {
                None
            }
        } else if let Some(pos) = trimmed.find("：") {
            let candidate = &trimmed[..pos];
            if candidate.len() <= 40 && !candidate.contains('\n') {
                Some(candidate.to_string())
            } else {
                None
            }
        } else {
            None
        };

        if let Some(l) = label {
            if !seen.contains(&l) {
                seen.push(l);
            }
        }
    }

    seen.into_iter()
        .enumerate()
        .map(|(i, label)| {
            let voice = voices[i % voices.len()].to_string();
            (label, voice)
        })
        .collect()
}
