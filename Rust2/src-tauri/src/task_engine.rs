use std::sync::Arc;
use tokio::sync::mpsc;
use tauri::Emitter;
use crate::storage::Database;
use serde::{Deserialize, Serialize};

/// 任务引擎 — 后台轮询 audio_task_queue，按优先级执行
/// 对标 C# AudioLabViewModel.TaskEngine
///
/// 设计要点:
/// - 单一消费者循环，避免并发冲突
/// - 通过 mpsc 接收 kick 信号（新任务提交时踢一下）
/// - 失败自动重试（retry_count < max_retries）
/// - 每次执行记录 task_executions 表
/// - 真实调用 Provider (STT/Chat/TTS)

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
pub enum TaskEvent {
    Started { task_id: String, stage: String },
    Progress { task_id: String, progress: f64 },
    Completed { task_id: String, stage: String },
    Failed { task_id: String, error: String },
    Cancelled { task_id: String },
}

pub struct TaskEngine {
    kick_tx: mpsc::Sender<()>,
}

impl TaskEngine {
    /// 通过 Tauri AppHandle 启动，可以访问 State<AppState> 里的 providers
    pub fn start_with_app(
        app_handle: tauri::AppHandle,
        storage: Arc<Database>,
    ) -> Self {
        let (kick_tx, mut kick_rx) = mpsc::channel::<()>(32);

        tokio::spawn(async move {
            loop {
                // 等待 kick 信号或 5 秒轮询
                let _ = tokio::time::timeout(
                    std::time::Duration::from_secs(5),
                    kick_rx.recv(),
                ).await;

                // 取一条 Queued 任务
                let task = match storage.get_next_queued_task() {
                    Ok(Some(t)) => t,
                    Ok(None) => continue,
                    Err(e) => {
                        eprintln!("[TaskEngine] get_next_queued_task error: {e}");
                        continue;
                    }
                };

                // 标记 Executing
                if let Err(e) = storage.update_task_status_new(&task.id, "Executing", None) {
                    eprintln!("[TaskEngine] update_task_status error: {e}");
                    continue;
                }

                // 发事件: Started
                let _ = app_handle.emit("task-event", serde_json::json!({
                    "type": "TaskStarted",
                    "task_id": task.id,
                    "audio_item_id": task.audio_item_id,
                    "stage": task.stage,
                }));

                let task_id = task.id.clone();
                let stage = task.stage.clone();

                // 执行任务
                let start = std::time::Instant::now();
                let result = execute_task_real(&app_handle, &storage, &task).await;
                let duration_ms = start.elapsed().as_millis() as i64;

                match result {
                    Ok((result_text, prompt_tokens, completion_tokens)) => {
                        let _ = storage.update_task_status_new(&task_id, "Completed", None);
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
                            started_at: None,
                            completed_at: Some(chrono::Utc::now().to_rfc3339()),
                        };
                        let _ = storage.upsert_lifecycle(&lc);
                        let exec = crate::models::TaskExecutionRow {
                            id: uuid::Uuid::new_v4().to_string(),
                            task_id: task_id.clone(),
                            attempt: task.retry_count + 1,
                            status: "Completed".to_string(),
                            error: None,
                            prompt_tokens: Some(prompt_tokens as i64),
                            completion_tokens: Some(completion_tokens as i64),
                            duration_ms: Some(duration_ms),
                            started_at: chrono::Utc::now().to_rfc3339(),
                            completed_at: Some(chrono::Utc::now().to_rfc3339()),
                        };
                        let _ = storage.add_task_execution(&exec);

                        // 发事件: Completed
                        let _ = app_handle.emit("task-event", serde_json::json!({
                            "type": "TaskCompleted",
                            "task_id": task_id,
                            "stage": stage,
                        }));
                    }
                    Err(err) => {
                        let err_msg = err.to_string();
                        if task.retry_count < task.max_retries {
                            let _ = storage.update_task_status_new(&task_id, "Queued", Some(&err_msg));
                        } else {
                            let _ = storage.update_task_status_new(&task_id, "Failed", Some(&err_msg));
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
                                started_at: None,
                                completed_at: Some(chrono::Utc::now().to_rfc3339()),
                            };
                            let _ = storage.upsert_lifecycle(&lc);
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
                            started_at: chrono::Utc::now().to_rfc3339(),
                            completed_at: Some(chrono::Utc::now().to_rfc3339()),
                        };
                        let _ = storage.add_task_execution(&exec);

                        // 发事件: Failed
                        let _ = app_handle.emit("task-event", serde_json::json!({
                            "type": "TaskFailed",
                            "task_id": task_id,
                            "error": err_msg,
                        }));
                    }
                }
            }
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
            let audio_item = storage.get_audio_item(&task.audio_item_id)
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
            let prev_content = get_previous_stage_content(storage, task)?;

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
                            crate::models::ChatMessage { role: "system".into(), content: system_prompt.into() },
                            crate::models::ChatMessage { role: "user".into(), content: prev_content },
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
            let script = get_stage_content(storage, &task.audio_item_id, "PodcastScript")?;
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
                    let voice = "zh-CN-XiaoxiaoMultilingualNeural";
                    let audio_bytes = tts.synthesize(&script, voice, "wav").await
                        .map_err(|e| format!("TTS failed: {e}"))?;

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
fn get_previous_stage_content(
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
    get_stage_content(storage, &task.audio_item_id, dep_stage)
}

fn get_stage_content(
    storage: &Arc<Database>,
    audio_item_id: &str,
    stage: &str,
) -> Result<String, Box<dyn std::error::Error + Send + Sync>> {
    let lifecycles = storage.get_audio_lifecycle(audio_item_id)
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
