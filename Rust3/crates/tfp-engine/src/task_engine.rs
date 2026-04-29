use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use tokio::sync::{mpsc, RwLock};
use tfp_core::{
    AppConfig, AudioAutoTag, AudioLifecycleRow, AudioResearchTopic, AudioStageOutput,
    AudioTaskRow, BillingRecord, CompletionRequest, ChatMessage, EventSink,
    ImageGenRequest, ModelCapability, TaskBusEvent, TaskExecutionRow, TaskFrontendEvent,
};
use tfp_providers::ProviderRegistry;
use tfp_storage::Database;

use crate::task_event_bus::TaskEventBus;

/// Task scheduling engine — dequeues tasks, dispatches to providers,
/// manages concurrency, billing, retries, and DAG cascades.
///
/// **Zero Tauri coupling.** All UI events go through the `EventSink` trait.
pub struct TaskEngine {
    kick_tx: mpsc::Sender<()>,
}

/// Dependencies injected into the task engine.
pub struct TaskEngineDeps {
    pub storage: Arc<Database>,
    pub config: Arc<RwLock<AppConfig>>,
    pub providers: Arc<RwLock<ProviderRegistry>>,
    pub bus: Arc<TaskEventBus>,
    pub sink: Arc<dyn EventSink>,
    /// Directory where TTS output WAV files are written.
    pub tts_output_dir: PathBuf,
}

// ── Helpers ──

/// 500ms-throttled emit of monitor-refresh via EventSink.
static LAST_MONITOR_EMIT_MS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

fn emit_throttled(sink: &dyn EventSink) {
    let now_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64;
    let last = LAST_MONITOR_EMIT_MS.load(std::sync::atomic::Ordering::Relaxed);
    if now_ms.saturating_sub(last) >= 500 {
        LAST_MONITOR_EMIT_MS.store(now_ms, std::sync::atomic::Ordering::Relaxed);
        sink.emit_monitor_refresh();
    }
}

/// Publish a bus event and throttle-emit to the frontend monitor view.
fn notify_monitor(sink: &dyn EventSink, bus: &TaskEventBus, event: TaskBusEvent) {
    bus.publish(event);
    emit_throttled(sink);
}

/// Read concurrency limits from KV store (split: transcription vs AI).
async fn read_concurrency_limits(storage: &Database) -> (usize, usize) {
    let tc = storage
        .kv_get("monitor.max_transcription_concurrency")
        .await
        .ok()
        .flatten()
        .and_then(|v| v.parse::<usize>().ok())
        .unwrap_or(2);
    let ac = storage
        .kv_get("monitor.max_ai_concurrency")
        .await
        .ok()
        .flatten()
        .and_then(|v| v.parse::<usize>().ok())
        .unwrap_or(4);
    (tc.max(1), ac.max(1))
}

/// Return true if the task type is a transcription task.
pub fn is_transcription_task(task_type: &str) -> bool {
    task_type == "Transcription" || task_type == "audio_transcribe"
}

/// Read task timeout from KV (stored as minutes, returned as seconds).
async fn read_timeout_secs(storage: &Database) -> u64 {
    let minutes = storage
        .kv_get("monitor.transcription_timeout_minutes")
        .await
        .ok()
        .flatten()
        .and_then(|v| v.parse::<u64>().ok())
        .unwrap_or(10);
    (minutes * 60).max(60)
}

impl TaskEngine {
    /// Start the task engine loop with injected dependencies (no Tauri coupling).
    pub fn start(deps: TaskEngineDeps) -> Self {
        let (kick_tx, mut kick_rx) = mpsc::channel::<()>(32);
        let active_transcription = Arc::new(AtomicUsize::new(0));
        let active_ai = Arc::new(AtomicUsize::new(0));

        let TaskEngineDeps {
            storage,
            config,
            providers,
            bus,
            sink,
            tts_output_dir,
        } = deps;

        tokio::spawn(async move {
            // Recover interrupted tasks on startup
            match storage.recover_interrupted_tasks().await {
                Ok(count) if count > 0 => {
                    tracing::info!("[TaskEngine] Recovered {count} interrupted tasks to Queued");
                }
                Err(e) => {
                    tracing::error!("[TaskEngine] recover_interrupted_tasks error: {e}");
                }
                _ => {}
            }

            // Mark stale Executing tasks as Interrupted (heartbeat expired)
            match storage.monitor_recover_interrupted().await {
                Ok(count) if count > 0 => {
                    tracing::info!("[TaskEngine] Marked {count} stale tasks as Interrupted");
                }
                Err(e) => tracing::error!("[TaskEngine] monitor_recover_interrupted error: {e}"),
                _ => {}
            }

            loop {
                // Wait up to 5s for a kick signal, then poll regardless
                let _ = tokio::time::timeout(
                    std::time::Duration::from_secs(5),
                    kick_rx.recv(),
                )
                .await;

                // Read concurrency/timeout config each iteration (hot-reload)
                let (max_transcription, max_ai) = read_concurrency_limits(&storage).await;
                let timeout_secs = read_timeout_secs(&storage).await;

                loop {
                    // Dequeue one Queued task
                    let task = match storage.get_next_queued_task().await {
                        Ok(Some(t)) => t,
                        Ok(None) => break,
                        Err(e) => {
                            tracing::error!("[TaskEngine] get_next_queued_task error: {e}");
                            break;
                        }
                    };

                    // Check concurrency limit for this task type
                    let is_transcription = is_transcription_task(&task.task_type);
                    let (counter, limit) = if is_transcription {
                        (&active_transcription, max_transcription)
                    } else {
                        (&active_ai, max_ai)
                    };

                    let current = counter.load(Ordering::Acquire);
                    if current >= limit {
                        break; // concurrency full — leave task in queue
                    }

                    // Mark task as Executing
                    if let Err(e) = storage.update_task_status_new(&task.id, "Executing", None).await {
                        tracing::error!("[TaskEngine] update_task_status error: {e}");
                        continue;
                    }

                    // Increment active counter for this task type
                    counter.fetch_add(1, Ordering::Release);

                    // Emit TaskStarted event
                    sink.emit_task_event(TaskFrontendEvent {
                        event_type: "TaskStarted".into(),
                        payload: serde_json::json!({
                            "task_id": task.id,
                            "audio_item_id": task.audio_item_id,
                            "stage": task.stage,
                        }),
                    });

                    // Notify monitor
                    notify_monitor(&*sink, &bus, TaskBusEvent::Started {
                        task_id: task.id.clone(),
                        stage: task.stage.clone(),
                    });

                    // Clone deps for the spawned task
                    let db = storage.clone();
                    let sink2 = sink.clone();
                    let bus2 = bus.clone();
                    let cfg = config.clone();
                    let provs = providers.clone();
                    let tts_dir = tts_output_dir.clone();
                    let active = if is_transcription {
                        active_transcription.clone()
                    } else {
                        active_ai.clone()
                    };
                    let task_timeout_secs = timeout_secs;

                    tokio::spawn(async move {
                        // RAII guard to decrement active counter when task finishes
                        struct ActiveGuard(Arc<AtomicUsize>);
                        impl Drop for ActiveGuard {
                            fn drop(&mut self) {
                                self.0.fetch_sub(1, Ordering::Release);
                            }
                        }
                        let _guard = ActiveGuard(active);

                        let task_id = task.id.clone();
                        let stage = task.stage.clone();

                        // Billing: Staging phase
                        let billing_id = uuid::Uuid::new_v4().to_string();
                        let billing_rec = BillingRecord {
                            id: billing_id.clone(),
                            task_id: Some(task_id.clone()),
                            endpoint_id: String::new(),
                            model_id: String::new(),
                            prompt_tokens: 0,
                            completion_tokens: 0,
                            cost_usd: None,
                            created_at: chrono::Utc::now().to_rfc3339(),
                            status: "Staging".to_string(),
                        };
                        let _ = db.add_billing_record(&billing_rec).await;

                        // Billing: Running
                        let _ = db.update_billing_status(&billing_id, "Running").await;

                        let started_at = chrono::Utc::now().to_rfc3339();

                        // Execute with timeout
                        let start = std::time::Instant::now();
                        let timeout_dur = std::time::Duration::from_secs(task_timeout_secs);
                        let result = tokio::time::timeout(
                            timeout_dur,
                            execute_task_real(&db, &cfg, &provs, &tts_dir, &task),
                        )
                        .await;

                        let duration_ms = start.elapsed().as_millis() as i64;
                        let completed_at = chrono::Utc::now().to_rfc3339();

                        // Convert timeout to a unified error
                        let result = match result {
                            Ok(inner) => inner,
                            Err(_) => Err(format!(
                                "Task timed out: exceeded {task_timeout_secs} seconds"
                            )
                            .into()),
                        };

                        match result {
                            Ok((result_text, prompt_tokens, completion_tokens)) => {
                                let _ = db.update_task_status_new(&task_id, "Completed", None).await;
                                let lc = AudioLifecycleRow {
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
                                let exec = TaskExecutionRow {
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

                                // Billing: Landed → record tokens → Committed
                                let _ = db.update_billing_status(&billing_id, "Landed").await;
                                let _ = db.update_billing_tokens(
                                    &billing_id,
                                    prompt_tokens as i64,
                                    completion_tokens as i64,
                                )
                                .await;
                                let _ = db.update_billing_status(&billing_id, "Committed").await;

                                // DAG cascade: invalidate downstream stages
                                if let Err(e) = db.invalidate_downstream_stages(&task.audio_item_id, &stage).await {
                                    tracing::error!("[TaskEngine] invalidate_downstream error: {e}");
                                }

                                // T-006: Sync StudioTask status
                                sync_studio_task_status(&db, &task, "completed", None).await;

                                sink2.emit_task_event(TaskFrontendEvent {
                                    event_type: "TaskCompleted".into(),
                                    payload: serde_json::json!({
                                        "task_id": task_id,
                                        "stage": stage,
                                    }),
                                });

                                notify_monitor(&*sink2, &bus2, TaskBusEvent::Completed {
                                    task_id: task_id.clone(),
                                    stage: stage.clone(),
                                });
                            }
                            Err(err) => {
                                let err_msg = err.to_string();
                                if task.retry_count < task.max_retries {
                                    let _ = db.increment_retry_and_requeue(&task_id, &err_msg).await;
                                } else {
                                    let _ = db.update_task_status_new(&task_id, "Failed", Some(&err_msg)).await;
                                    let lc = AudioLifecycleRow {
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

                                    // T-006: Sync StudioTask status on final failure
                                    sync_studio_task_status(&db, &task, "failed", Some(&err_msg)).await;
                                }
                                let exec = TaskExecutionRow {
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

                                // Billing: Failed (not committed)
                                let _ = db.update_billing_status(&billing_id, "Failed").await;

                                sink2.emit_task_event(TaskFrontendEvent {
                                    event_type: "TaskFailed".into(),
                                    payload: serde_json::json!({
                                        "task_id": task_id,
                                        "error": err_msg,
                                    }),
                                });

                                notify_monitor(&*sink2, &bus2, TaskBusEvent::Failed {
                                    task_id: task_id.clone(),
                                    error: err_msg.clone(),
                                });
                            }
                        }
                    });
                } // inner loop
            } // outer loop
        });

        TaskEngine { kick_tx }
    }

    /// Wake up the engine to process new tasks.
    pub async fn kick(&self) {
        let _ = self.kick_tx.send(()).await;
    }
}

// ── Task execution dispatch ──

/// Dispatch a task to the appropriate provider based on task_type.
/// Returns (result_text, prompt_tokens, completion_tokens).
async fn execute_task_real(
    storage: &Arc<Database>,
    config: &Arc<RwLock<AppConfig>>,
    providers: &Arc<RwLock<ProviderRegistry>>,
    tts_output_dir: &PathBuf,
    task: &AudioTaskRow,
) -> Result<(String, u32, u32), Box<dyn std::error::Error + Send + Sync>> {
    match task.task_type.as_str() {
        "Transcription" => {
            let audio_item = storage
                .get_audio_item(&task.audio_item_id)
                .await
                .map_err(|e| format!("Failed to get audio item: {e}"))?
                .ok_or("Audio item not found")?;

            let audio_data =
                std::fs::read(&audio_item.file_path).map_err(|e| format!("Failed to read audio file: {e}"))?;

            let source_lang = &audio_item.source_lang;

            let provs = providers.read().await;
            let cfg = config.read().await;
            let stt_ep = cfg
                .endpoints
                .iter()
                .find(|ep| ep.enabled && ep.endpoint_type == tfp_core::EndpointType::AzureSpeech);

            if let Some(ep) = stt_ep {
                if let Some(stt) = provs.get_stt(&ep.id) {
                    drop(provs);
                    drop(cfg);
                    let segments = stt
                        .transcribe(&audio_data, source_lang)
                        .await
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

            let provs = providers.read().await;
            let cfg = config.read().await;

            let ai_ep = cfg
                .endpoints
                .iter()
                .find(|ep| {
                    ep.enabled
                        && matches!(
                            ep.endpoint_type,
                            tfp_core::EndpointType::AzureOpenAi
                                | tfp_core::EndpointType::OpenAiCompatible
                                | tfp_core::EndpointType::ApiManagementGateway
                        )
                })
                .cloned();

            let summary_model_id = cfg.ai.summary_model.model_id.clone();
            drop(cfg);

            if let Some(ep) = ai_ep {
                if let Some(ai) = provs.get_ai_completion(&ep.id) {
                    drop(provs);

                    let model = if summary_model_id.is_empty() {
                        ep.models
                            .iter()
                            .find(|m| m.capabilities.contains(&ModelCapability::Text))
                            .map(|m| m.model_id.clone())
                            .unwrap_or_else(|| "gpt-4.1".to_string())
                    } else {
                        summary_model_id
                    };

                    let request = CompletionRequest {
                        messages: vec![
                            ChatMessage {
                                role: "system".into(),
                                content: serde_json::Value::String(system_prompt.to_string()),
                            },
                            ChatMessage {
                                role: "user".into(),
                                content: serde_json::Value::String(prev_content),
                            },
                        ],
                        model,
                        temperature: Some(0.7),
                        max_tokens: Some(4096),
                        endpoint_id: ep.id.clone(),
                        reasoning_effort: None,
                        enable_image_generation: false,
                        image_model_deployment: None,
                        image_size: None,
                        image_quality: None,
                    };

                    let resp = ai
                        .complete(&request)
                        .await
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
            let script = get_stage_content(storage, &task.audio_item_id, "PodcastScript").await?;
            if script.is_empty() {
                return Err("PodcastScript stage has no content — please generate it first".into());
            }

            let provs = providers.read().await;
            let cfg = config.read().await;
            let tts_ep = cfg
                .endpoints
                .iter()
                .find(|ep| ep.enabled && ep.endpoint_type == tfp_core::EndpointType::AzureSpeech);

            if let Some(ep) = tts_ep {
                if let Some(tts) = provs.get_tts(&ep.id) {
                    drop(provs);
                    drop(cfg);

                    // Detect multi-speaker format
                    let speakers = detect_speakers(&script);
                    let audio_bytes = if speakers.len() >= 2 {
                        tracing::info!("[TaskEngine] TTS: detected {} speakers, using multi-speaker", speakers.len());
                        tts.synthesize_multi_speaker(&script, &speakers, "wav")
                            .await
                            .map_err(|e| format!("TTS multi-speaker failed: {e}"))?
                    } else {
                        let voice = speakers
                            .first()
                            .map(|(_, v)| v.as_str())
                            .unwrap_or("zh-CN-XiaoxiaoMultilingualNeural");
                        tts.synthesize(&script, voice, "wav")
                            .await
                            .map_err(|e| format!("TTS failed: {e}"))?
                    };

                    // Save audio output file
                    let _ = std::fs::create_dir_all(tts_output_dir);
                    let out_path = tts_output_dir.join(format!("podcast_{}.wav", task.audio_item_id));
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
            let provs = providers.read().await;
            let cfg = config.read().await;

            let img_ep = cfg
                .endpoints
                .iter()
                .find(|ep| {
                    ep.enabled
                        && matches!(
                            ep.endpoint_type,
                            tfp_core::EndpointType::AzureOpenAi
                                | tfp_core::EndpointType::OpenAiCompatible
                                | tfp_core::EndpointType::ApiManagementGateway
                        )
                })
                .cloned();

            let img_model = cfg.media.image_model.model_id.clone();
            drop(cfg);

            if let Some(ep) = img_ep {
                if let Some(img) = provs.get_image_gen(&ep.id) {
                    drop(provs);
                    let model = if img_model.is_empty() {
                        "gpt-image-2".to_string()
                    } else {
                        img_model
                    };

                    let request = ImageGenRequest {
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
                        reference_image_path: None,
                        image_edit_mode: None,
                        uploaded_file_ids: vec![],
                    };

                    let results = img
                        .generate(&request)
                        .await
                        .map_err(|e| format!("Image gen failed: {e}"))?;

                    let summary = results
                        .first()
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
        "audio_stage" | "audio_auto_tags" => {
            // T-002/T-005: AudioLab stage execution with DAG check
            let session_id = &task.audio_item_id;
            let stage_key = parse_stage_key(&task);

            // DAG dependency check
            if task.task_type == "audio_stage" {
                check_audiolab_dependencies(storage, session_id, &stage_key).await?;
            }

            // Get AI provider
            let provs = providers.read().await;
            let cfg = config.read().await;
            let ai_ep = cfg
                .endpoints
                .iter()
                .find(|ep| {
                    ep.enabled
                        && matches!(
                            ep.endpoint_type,
                            tfp_core::EndpointType::AzureOpenAi
                                | tfp_core::EndpointType::OpenAiCompatible
                                | tfp_core::EndpointType::ApiManagementGateway
                        )
                })
                .cloned();
            let summary_model_id = cfg.ai.summary_model.model_id.clone();
            drop(cfg);

            if let Some(ep) = ai_ep {
                if let Some(ai) = provs.get_ai_completion(&ep.id) {
                    drop(provs);

                    let model = if summary_model_id.is_empty() {
                        ep.models
                            .iter()
                            .find(|m| m.capabilities.contains(&ModelCapability::Text))
                            .map(|m| m.model_id.clone())
                            .unwrap_or_else(|| "gpt-4.1".to_string())
                    } else {
                        summary_model_id
                    };

                    if task.task_type == "audio_auto_tags" {
                        // T-002: Auto-tags extraction
                        execute_auto_tags(storage, session_id, ai.clone(), &ep.id, &model).await
                    } else {
                        // T-002: Stage generation via stage_runner
                        let custom_prompt = task.prompt_text.as_deref()
                            .and_then(|p| p.split(";custom_prompt=").nth(1));
                        let result = tfp_audiolab::stage_runner::run_stage(
                            storage,
                            session_id,
                            &stage_key,
                            custom_prompt,
                            ai,
                            &ep.id,
                            &model,
                        )
                        .await
                        .map_err(|e| -> Box<dyn std::error::Error + Send + Sync> { e.into() })?;
                        Ok(result)
                    }
                } else {
                    Err("AI completion provider not found in registry".into())
                }
            } else {
                Err("No AI endpoint configured".into())
            }
        }
        "audio_research" => {
            // T-003: Research two-phase execution
            let session_id = &task.audio_item_id;
            let topic_id = parse_topic_id(&task);

            let provs = providers.read().await;
            let cfg = config.read().await;
            let ai_ep = cfg
                .endpoints
                .iter()
                .find(|ep| {
                    ep.enabled
                        && matches!(
                            ep.endpoint_type,
                            tfp_core::EndpointType::AzureOpenAi
                                | tfp_core::EndpointType::OpenAiCompatible
                                | tfp_core::EndpointType::ApiManagementGateway
                        )
                })
                .cloned();
            let summary_model_id = cfg.ai.summary_model.model_id.clone();
            drop(cfg);

            if let Some(ep) = ai_ep {
                if let Some(ai) = provs.get_ai_completion(&ep.id) {
                    drop(provs);

                    let model = if summary_model_id.is_empty() {
                        ep.models
                            .iter()
                            .find(|m| m.capabilities.contains(&ModelCapability::Text))
                            .map(|m| m.model_id.clone())
                            .unwrap_or_else(|| "gpt-4.1".to_string())
                    } else {
                        summary_model_id
                    };

                    execute_research(storage, session_id, &topic_id, ai, &ep.id, &model).await
                } else {
                    Err("AI completion provider not found in registry".into())
                }
            } else {
                Err("No AI endpoint configured".into())
            }
        }
        "audio_podcast_tts" => {
            // T-004: Podcast TTS from PodcastScript stage output
            let session_id = &task.audio_item_id;

            // Check PodcastScript stage output exists
            let outputs = storage
                .audiolab_get_stage_outputs(session_id)
                .await
                .map_err(|e| -> Box<dyn std::error::Error + Send + Sync> {
                    format!("Failed to get stage outputs: {e}").into()
                })?;

            let script_output = outputs
                .iter()
                .find(|o| o.stage_key == "PodcastScript" || o.stage_key == "podcast")
                .filter(|o| o.status == "Ready");

            let script = match script_output {
                Some(o) if !o.content_markdown.is_empty() => o.content_markdown.clone(),
                _ => return Err("请先生成播客台本 (PodcastScript stage not ready)".into()),
            };

            // Reuse TTS logic
            let provs = providers.read().await;
            let cfg = config.read().await;
            let tts_ep = cfg
                .endpoints
                .iter()
                .find(|ep| ep.enabled && ep.endpoint_type == tfp_core::EndpointType::AzureSpeech);

            if let Some(ep) = tts_ep {
                if let Some(tts) = provs.get_tts(&ep.id) {
                    drop(provs);
                    drop(cfg);

                    let speakers = detect_speakers(&script);
                    let audio_bytes = if speakers.len() >= 2 {
                        tts.synthesize_multi_speaker(&script, &speakers, "wav")
                            .await
                            .map_err(|e| format!("TTS multi-speaker failed: {e}"))?
                    } else {
                        let voice = speakers
                            .first()
                            .map(|(_, v)| v.as_str())
                            .unwrap_or("zh-CN-XiaoxiaoMultilingualNeural");
                        tts.synthesize(&script, voice, "wav")
                            .await
                            .map_err(|e| format!("TTS failed: {e}"))?
                    };

                    // Save audio file
                    let _ = std::fs::create_dir_all(tts_output_dir);
                    let out_path = tts_output_dir.join(format!("podcast_{}.wav", session_id));
                    std::fs::write(&out_path, &audio_bytes)
                        .map_err(|e| format!("Failed to write TTS output: {e}"))?;

                    // Update stage output for PodcastAudio
                    let audio_output = AudioStageOutput {
                        id: uuid::Uuid::new_v4().to_string(),
                        session_id: session_id.to_string(),
                        stage_key: "PodcastAudio".to_string(),
                        content_markdown: out_path.to_string_lossy().to_string(),
                        status: "Ready".to_string(),
                        error_message: None,
                        model_ref: None,
                        generated_at: Some(chrono::Utc::now().to_rfc3339()),
                        custom_stage_key: None,
                        custom_is_mindmap: None,
                    };
                    let _ = storage.audiolab_upsert_stage_output(&audio_output).await;

                    Ok((out_path.to_string_lossy().to_string(), 0, 0))
                } else {
                    Err("TTS provider not found in registry".into())
                }
            } else {
                Err("No Azure Speech endpoint configured".into())
            }
        }
        other => Err(format!("Unknown task type: {other}").into()),
    }
}

/// Get the content from the previous (dependency) stage for the current task.
async fn get_previous_stage_content(
    storage: &Arc<Database>,
    task: &AudioTaskRow,
) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
    let dep_stage = match task.stage.as_str() {
        "Summarized" | "Translated" | "Insight" | "PodcastScript" | "Research" => "Transcribed",
        "MindMap" => "Summarized",
        _ => "Transcribed",
    };
    get_stage_content(storage, &task.audio_item_id, dep_stage).await
}

/// Retrieve the completed result_text for a given audio item + stage.
async fn get_stage_content(
    storage: &Arc<Database>,
    audio_item_id: &str,
    stage: &str,
) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
    let lifecycles = storage
        .get_audio_lifecycle(audio_item_id)
        .await
        .map_err(|e| format!("Failed to get lifecycle: {e}"))?;
    let lc = lifecycles.iter().find(|l| l.stage == stage);
    match lc {
        Some(l) if l.status == "Completed" => l
            .result_text
            .clone()
            .ok_or_else(|| format!("Stage {stage} completed but has no result text").into()),
        Some(l) => Err(format!("Stage {stage} is not completed (status: {})", l.status).into()),
        None => Err(format!("Dependency stage {stage} has not been processed yet").into()),
    }
}

/// Detect speaker roles from a podcast script.
///
/// Scans lines for "Label: " or "Label：" patterns, deduplicates, and assigns
/// alternating male/female voices.
pub fn detect_speakers(script: &str) -> Vec<(String, String)> {
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
        let label = if let Some(pos) = trimmed.find(": ") {
            let candidate = &trimmed[..pos];
            if candidate.len() <= 40 && !candidate.contains('\n') {
                Some(candidate.to_string())
            } else {
                None
            }
        } else if let Some(pos) = trimmed.find('：') {
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

// ── T-005: DAG dependency definitions ──

/// Returns the list of prerequisite stage keys for a given audiolab stage.
pub fn audiolab_stage_dependencies(stage: &str) -> &'static [&'static str] {
    match stage {
        "Summarized" => &["Transcribed"],
        "MindMap" => &["Summarized"],
        "Insight" => &["Transcribed"],
        "PodcastScript" => &["Transcribed"],
        "PodcastAudio" => &["PodcastScript"],
        "Research" => &["Transcribed"],
        "Translated" => &["Transcribed"],
        _ => &["Transcribed"],
    }
}

/// Check if all prerequisite stages are completed for a given stage.
async fn check_audiolab_dependencies(
    storage: &Arc<Database>,
    session_id: &str,
    stage_key: &str,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let deps = audiolab_stage_dependencies(stage_key);
    if deps.is_empty() {
        return Ok(());
    }

    // Check lifecycle records for dependencies
    let lifecycles = storage
        .get_audio_lifecycle(session_id)
        .await
        .map_err(|e| format!("Failed to get lifecycle: {e}"))?;

    // Also check stage outputs for "Ready" status
    let outputs = storage
        .audiolab_get_stage_outputs(session_id)
        .await
        .map_err(|e| format!("Failed to get stage outputs: {e}"))?;

    for dep in deps {
        let lc_ok = lifecycles
            .iter()
            .any(|l| l.stage == *dep && l.status == "Completed");
        let output_ok = outputs
            .iter()
            .any(|o| o.stage_key == *dep && o.status == "Ready");

        if !lc_ok && !output_ok {
            return Err(format!("前置阶段未完成: {dep}").into());
        }
    }
    Ok(())
}

// ── T-002: Helper functions ──

/// Parse stage_key from task's prompt_text (format: "stage_key=XYZ;...")
fn parse_stage_key(task: &AudioTaskRow) -> String {
    task.prompt_text
        .as_deref()
        .unwrap_or("")
        .split(';')
        .find_map(|part| part.strip_prefix("stage_key="))
        .unwrap_or(&task.stage)
        .to_string()
}

/// Parse topic_id from task's prompt_text (format: "topic_id=XYZ;...")
fn parse_topic_id(task: &AudioTaskRow) -> String {
    task.prompt_text
        .as_deref()
        .unwrap_or("")
        .split(';')
        .find_map(|part| part.strip_prefix("topic_id="))
        .unwrap_or("")
        .to_string()
}

/// Parse studio_task_id from task's prompt_text (format: "...;studio_task_id=XYZ")
fn parse_studio_task_id(task: &AudioTaskRow) -> Option<String> {
    task.prompt_text
        .as_deref()?
        .split(';')
        .find_map(|part| part.strip_prefix("studio_task_id="))
        .map(|s| s.to_string())
}

/// T-002: Execute auto-tags extraction using AI
async fn execute_auto_tags(
    storage: &Arc<Database>,
    session_id: &str,
    ai: Arc<dyn tfp_providers::traits::AiCompletionSlot>,
    endpoint_id: &str,
    model: &str,
) -> Result<(String, u32, u32), Box<dyn std::error::Error + Send + Sync>> {
    // Load transcript content
    let transcript = storage
        .audiolab_get_transcript(session_id)
        .await
        .map_err(|e| format!("Failed to get transcript: {e}"))?
        .ok_or("No transcript for auto-tags")?;

    let segments = storage
        .audiolab_get_segments(&transcript.id)
        .await
        .map_err(|e| format!("Failed to get segments: {e}"))?;

    let transcript_text: String = segments
        .iter()
        .map(|s| s.text.as_str())
        .collect::<Vec<_>>()
        .join("\n");

    if transcript_text.is_empty() {
        return Err("Transcript has no text content".into());
    }

    // Call AI for tag extraction
    let system_prompt = tfp_audiolab::prompts::auto_tags_system_prompt();
    let user_prompt = tfp_audiolab::prompts::auto_tags_user_prompt(&transcript_text);

    let request = CompletionRequest {
        messages: vec![
            ChatMessage {
                role: "system".into(),
                content: serde_json::Value::String(system_prompt.to_string()),
            },
            ChatMessage {
                role: "user".into(),
                content: serde_json::Value::String(user_prompt),
            },
        ],
        model: model.to_string(),
        temperature: Some(0.3),
        max_tokens: Some(1024),
        endpoint_id: endpoint_id.to_string(),
        reasoning_effort: None,
        enable_image_generation: false,
        image_model_deployment: None,
        image_size: None,
        image_quality: None,
    };

    let resp = ai.complete(&request).await.map_err(|e| format!("AI completion failed: {e}"))?;
    let prompt_tokens = resp.usage.as_ref().map(|u| u.prompt_tokens).unwrap_or(0);
    let completion_tokens = resp.usage.as_ref().map(|u| u.completion_tokens).unwrap_or(0);

    // Parse tags and insert into DB
    let tags = tfp_audiolab::prompts::parse_auto_tags(&resp.content);
    let now = chrono::Utc::now().to_rfc3339();
    for tag in &tags {
        let auto_tag = AudioAutoTag {
            id: uuid::Uuid::new_v4().to_string(),
            session_id: session_id.to_string(),
            tag: tag.clone(),
            source: "ai".to_string(),
            created_at: now.clone(),
        };
        let _ = storage.audiolab_insert_auto_tag(&auto_tag).await;
    }

    let result_text = tags.join(", ");
    Ok((result_text, prompt_tokens, completion_tokens))
}

/// T-003: Execute research in two phases (topic planning + report generation).
async fn execute_research(
    storage: &Arc<Database>,
    session_id: &str,
    topic_id: &str,
    ai: Arc<dyn tfp_providers::traits::AiCompletionSlot>,
    endpoint_id: &str,
    model: &str,
) -> Result<(String, u32, u32), Box<dyn std::error::Error + Send + Sync>> {
    // Load transcript
    let transcript = storage
        .audiolab_get_transcript(session_id)
        .await
        .map_err(|e| format!("Failed to get transcript: {e}"))?
        .ok_or("No transcript for research")?;

    let segments = storage
        .audiolab_get_segments(&transcript.id)
        .await
        .map_err(|e| format!("Failed to get segments: {e}"))?;

    let transcript_text: String = segments
        .iter()
        .map(|s| s.text.as_str())
        .collect::<Vec<_>>()
        .join("\n");

    let mut total_prompt = 0u32;
    let mut total_completion = 0u32;

    // Check if we have a specific topic to research
    let topic = if !topic_id.is_empty() {
        storage
            .audiolab_get_research_topic(topic_id)
            .await
            .map_err(|e| format!("Failed to get research topic: {e}"))?
    } else {
        None
    };

    if let Some(topic) = topic {
        // Phase 2 only: generate report for existing topic
        let system_prompt = tfp_audiolab::prompts::research_phase2_system_prompt();
        let user_prompt = tfp_audiolab::prompts::research_phase2_user_prompt(
            &topic.title,
            &topic.description,
            &transcript_text,
        );

        let request = CompletionRequest {
            messages: vec![
                ChatMessage {
                    role: "system".into(),
                    content: serde_json::Value::String(system_prompt.to_string()),
                },
                ChatMessage {
                    role: "user".into(),
                    content: serde_json::Value::String(user_prompt),
                },
            ],
            model: model.to_string(),
            temperature: Some(0.7),
            max_tokens: Some(4096),
            endpoint_id: endpoint_id.to_string(),
            reasoning_effort: None,
            enable_image_generation: false,
            image_model_deployment: None,
            image_size: None,
            image_quality: None,
        };

        let resp = ai.complete(&request).await.map_err(|e| format!("Research report failed: {e}"))?;
        total_prompt += resp.usage.as_ref().map(|u| u.prompt_tokens).unwrap_or(0);
        total_completion += resp.usage.as_ref().map(|u| u.completion_tokens).unwrap_or(0);

        // Update research topic with report
        let _ = storage
            .audiolab_update_research_topic(topic_id, "completed", Some(&resp.content))
            .await;

        Ok((resp.content, total_prompt, total_completion))
    } else {
        // Phase 1: Plan research topics, then Phase 2: generate reports
        let system_prompt = tfp_audiolab::prompts::research_phase1_system_prompt();
        let user_prompt = tfp_audiolab::prompts::research_phase1_user_prompt(&transcript_text);

        let request = CompletionRequest {
            messages: vec![
                ChatMessage {
                    role: "system".into(),
                    content: serde_json::Value::String(system_prompt.to_string()),
                },
                ChatMessage {
                    role: "user".into(),
                    content: serde_json::Value::String(user_prompt),
                },
            ],
            model: model.to_string(),
            temperature: Some(0.7),
            max_tokens: Some(2048),
            endpoint_id: endpoint_id.to_string(),
            reasoning_effort: None,
            enable_image_generation: false,
            image_model_deployment: None,
            image_size: None,
            image_quality: None,
        };

        let resp = ai.complete(&request).await.map_err(|e| format!("Research planning failed: {e}"))?;
        total_prompt += resp.usage.as_ref().map(|u| u.prompt_tokens).unwrap_or(0);
        total_completion += resp.usage.as_ref().map(|u| u.completion_tokens).unwrap_or(0);

        // Parse topics and insert
        let topics = tfp_audiolab::prompts::parse_research_topics(&resp.content);
        let now = chrono::Utc::now().to_rfc3339();
        let mut reports = Vec::new();

        for (title, description) in &topics {
            let new_topic = AudioResearchTopic {
                id: uuid::Uuid::new_v4().to_string(),
                session_id: session_id.to_string(),
                title: title.clone(),
                description: description.clone(),
                status: "pending".to_string(),
                report_markdown: None,
                created_at: now.clone(),
            };
            let _ = storage.audiolab_insert_research_topic(&new_topic).await;

            // Phase 2: Generate report for each topic
            let p2_system = tfp_audiolab::prompts::research_phase2_system_prompt();
            let p2_user = tfp_audiolab::prompts::research_phase2_user_prompt(
                title,
                description,
                &transcript_text,
            );

            let p2_request = CompletionRequest {
                messages: vec![
                    ChatMessage {
                        role: "system".into(),
                        content: serde_json::Value::String(p2_system.to_string()),
                    },
                    ChatMessage {
                        role: "user".into(),
                        content: serde_json::Value::String(p2_user),
                    },
                ],
                model: model.to_string(),
                temperature: Some(0.7),
                max_tokens: Some(4096),
                endpoint_id: endpoint_id.to_string(),
                reasoning_effort: None,
                enable_image_generation: false,
                image_model_deployment: None,
                image_size: None,
                image_quality: None,
            };

            match ai.complete(&p2_request).await {
                Ok(p2_resp) => {
                    total_prompt += p2_resp.usage.as_ref().map(|u| u.prompt_tokens).unwrap_or(0);
                    total_completion += p2_resp.usage.as_ref().map(|u| u.completion_tokens).unwrap_or(0);
                    let _ = storage
                        .audiolab_update_research_topic(&new_topic.id, "completed", Some(&p2_resp.content))
                        .await;
                    reports.push(p2_resp.content);
                }
                Err(e) => {
                    let _ = storage
                        .audiolab_update_research_topic(&new_topic.id, "failed", None)
                        .await;
                    tracing::error!("[TaskEngine] Research report failed for '{}': {e}", title);
                }
            }
        }

        let combined = reports.join("\n\n---\n\n");
        Ok((combined, total_prompt, total_completion))
    }
}

// ── T-006: Sync StudioTask status on task completion/failure ──

/// After task completes or fails, sync back to studio_tasks if studio_task_id is present.
async fn sync_studio_task_status(storage: &Arc<Database>, task: &AudioTaskRow, status: &str, error: Option<&str>) {
    if let Some(studio_id) = parse_studio_task_id(task) {
        let _ = storage.studio_update_task_status(&studio_id, status, error).await;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── is_transcription_task tests ──

    #[test]
    fn test_is_transcription_task_match() {
        assert!(is_transcription_task("Transcription"));
        assert!(is_transcription_task("audio_transcribe"));
    }

    #[test]
    fn test_is_transcription_task_no_match() {
        assert!(!is_transcription_task("AiCompletion"));
        assert!(!is_transcription_task("TTS"));
        assert!(!is_transcription_task(""));
    }

    // ── detect_speakers tests ──

    #[test]
    fn test_detect_speakers_colon_format() {
        let input = "主持人A: 你好\n主持人B: 大家好";
        let result = detect_speakers(input);
        assert_eq!(result.len(), 2);
        assert_eq!(result[0].0, "主持人A");
        assert_eq!(result[1].0, "主持人B");
    }

    #[test]
    fn test_detect_speakers_fullwidth_colon() {
        let input = "张三：问题是\n李四：解答";
        let result = detect_speakers(input);
        assert_eq!(result.len(), 2);
        assert_eq!(result[0].0, "张三");
        assert_eq!(result[1].0, "李四");
    }

    #[test]
    fn test_detect_speakers_dedup() {
        let input = "A: line1\nA: line2\nB: line3";
        let result = detect_speakers(input);
        assert_eq!(result.len(), 2);
        assert_eq!(result[0].0, "A");
        assert_eq!(result[1].0, "B");
    }

    #[test]
    fn test_detect_speakers_empty() {
        let result = detect_speakers("");
        assert!(result.is_empty());
    }

    #[test]
    fn test_detect_speakers_no_colon() {
        let input = "纯文本没有冒号\n第二行";
        let result = detect_speakers(input);
        assert!(result.is_empty());
    }

    #[test]
    fn test_detect_speakers_voice_assignment() {
        let input = "A: hello\nB: world";
        let result = detect_speakers(input);
        assert_eq!(result[0].1, "zh-CN-XiaoxiaoMultilingualNeural");
        assert_eq!(result[1].1, "zh-CN-YunxiMultilingualNeural");
    }

    #[test]
    fn test_detect_speakers_long_label_ignored() {
        let long_label = "A".repeat(41);
        let input = format!("{long_label}: ignored\nShort: kept");
        let result = detect_speakers(&input);
        assert_eq!(result.len(), 1);
        assert_eq!(result[0].0, "Short");
    }

    #[test]
    fn test_detect_speakers_mixed_colons() {
        let input = "A: 你好\nB：世界";
        let result = detect_speakers(input);
        assert_eq!(result.len(), 2);
        assert_eq!(result[0].0, "A");
        assert_eq!(result[1].0, "B");
    }

    // ── T-005: DAG dependency tests ──

    #[test]
    fn test_dag_dependencies_correct() {
        assert_eq!(audiolab_stage_dependencies("Summarized"), &["Transcribed"]);
        assert_eq!(audiolab_stage_dependencies("MindMap"), &["Summarized"]);
        assert_eq!(audiolab_stage_dependencies("Insight"), &["Transcribed"]);
        assert_eq!(audiolab_stage_dependencies("PodcastScript"), &["Transcribed"]);
        assert_eq!(audiolab_stage_dependencies("PodcastAudio"), &["PodcastScript"]);
        assert_eq!(audiolab_stage_dependencies("Research"), &["Transcribed"]);
        assert_eq!(audiolab_stage_dependencies("Translated"), &["Transcribed"]);
    }

    #[test]
    fn test_dag_unknown_stage_defaults_to_transcribed() {
        assert_eq!(audiolab_stage_dependencies("Unknown"), &["Transcribed"]);
        assert_eq!(audiolab_stage_dependencies("Custom:MyStage"), &["Transcribed"]);
    }

    // ── T-002: parse_stage_key tests ──

    #[test]
    fn test_parse_stage_key_from_prompt() {
        let task = AudioTaskRow {
            id: "t1".into(),
            audio_item_id: "a1".into(),
            stage: "default".into(),
            task_type: "audio_stage".into(),
            status: "Queued".into(),
            priority: 0,
            retry_count: 0,
            max_retries: 3,
            progress: 0.0,
            prompt_text: Some("stage_key=Summarized;studio_task_id=abc".into()),
            result_text: None,
            error: None,
            submitted_at: "2026-01-01".into(),
            started_at: None,
            completed_at: None,
        };
        assert_eq!(parse_stage_key(&task), "Summarized");
    }

    #[test]
    fn test_parse_stage_key_no_prefix() {
        let task = AudioTaskRow {
            id: "t1".into(),
            audio_item_id: "a1".into(),
            stage: "Fallback".into(),
            task_type: "audio_stage".into(),
            status: "Queued".into(),
            priority: 0,
            retry_count: 0,
            max_retries: 3,
            progress: 0.0,
            prompt_text: Some("no_stage_key_here".into()),
            result_text: None,
            error: None,
            submitted_at: "2026-01-01".into(),
            started_at: None,
            completed_at: None,
        };
        assert_eq!(parse_stage_key(&task), "Fallback");
    }

    // ── T-006: parse_studio_task_id tests ──

    #[test]
    fn test_parse_studio_task_id_from_prompt() {
        let task = AudioTaskRow {
            id: "t1".into(),
            audio_item_id: "a1".into(),
            stage: "s".into(),
            task_type: "audio_stage".into(),
            status: "Queued".into(),
            priority: 0,
            retry_count: 0,
            max_retries: 3,
            progress: 0.0,
            prompt_text: Some("stage_key=Summarized;studio_task_id=abc-123".into()),
            result_text: None,
            error: None,
            submitted_at: "2026-01-01".into(),
            started_at: None,
            completed_at: None,
        };
        assert_eq!(parse_studio_task_id(&task), Some("abc-123".into()));
    }

    #[test]
    fn test_parse_studio_task_id_none() {
        let task = AudioTaskRow {
            id: "t1".into(),
            audio_item_id: "a1".into(),
            stage: "s".into(),
            task_type: "audio_stage".into(),
            status: "Queued".into(),
            priority: 0,
            retry_count: 0,
            max_retries: 3,
            progress: 0.0,
            prompt_text: Some("stage_key=Summarized".into()),
            result_text: None,
            error: None,
            submitted_at: "2026-01-01".into(),
            started_at: None,
            completed_at: None,
        };
        assert_eq!(parse_studio_task_id(&task), None);
    }

    // ── T-004: podcast_tts_requires_script test (logic check) ──

    #[test]
    fn test_podcast_tts_requires_script_stage_key_check() {
        // Verifies the stage key matching logic used in audio_podcast_tts branch
        let outputs = vec![
            AudioStageOutput {
                id: "o1".into(),
                session_id: "s1".into(),
                stage_key: "Summarized".into(),
                content_markdown: "content".into(),
                status: "Ready".into(),
                error_message: None,
                model_ref: None,
                generated_at: None,
                custom_stage_key: None,
                custom_is_mindmap: None,
            },
        ];
        // No PodcastScript → should not find
        let found = outputs
            .iter()
            .find(|o| o.stage_key == "PodcastScript" || o.stage_key == "podcast")
            .filter(|o| o.status == "Ready");
        assert!(found.is_none());
    }
}
