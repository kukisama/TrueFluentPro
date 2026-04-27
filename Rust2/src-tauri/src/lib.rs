mod commands;
mod models;
mod profile_loader;
mod providers;
mod state;
mod storage;
mod task_engine;
mod image_pipeline;

use std::sync::Arc;
use tauri::Manager;

use models::{EndpointType, AiEndpoint};
use providers::{OpenAiChatProvider, OpenAiImageProvider, OpenAiTranslationProvider, OpenAiRealtimeProvider, AzureSpeechProvider, AzureSttProvider, AzureTtsProvider};
use state::AppState;
use storage::Database;

/// 根据端点类型自动注册对应的 Provider（async 版本，在 tokio runtime 内调用）
async fn register_providers_from_config_async(state: &AppState, endpoints: &[AiEndpoint]) {
    let mut registry = state.providers.write().await;
    registry.clear();
    for ep in endpoints.iter().filter(|e| e.enabled) {
        match ep.endpoint_type {
            EndpointType::AzureOpenAi | EndpointType::OpenAiCompatible | EndpointType::ApiManagementGateway | EndpointType::Custom => {
                registry.register_ai_completion(Arc::new(OpenAiChatProvider::new(ep.clone())));
                registry.register_image_gen(Arc::new(OpenAiImageProvider::new(ep.clone())));
                // B-05: TextTranslation（基于 AI Completion 的翻译适配器）
                registry.register_text_translation(Arc::new(OpenAiTranslationProvider::new(ep.clone())));
                // P3-1: OpenAI Realtime WebSocket 翻译
                registry.register_realtime_speech(Arc::new(OpenAiRealtimeProvider::new(ep.clone())));
                tracing::info!("已注册 AI+Image+Translation+Realtime Provider: {} ({})", ep.name, ep.id);
            }
            EndpointType::AzureSpeech => {
                registry.register_realtime_speech(Arc::new(AzureSpeechProvider::new(ep.clone())));
                registry.register_stt(Arc::new(AzureSttProvider::new(ep.clone())));
                registry.register_tts(Arc::new(AzureTtsProvider::new(ep.clone())));
                tracing::info!("已注册 Speech+STT+TTS Provider: {} ({})", ep.name, ep.id);
            }
            _ => {
                tracing::debug!("端点 {} ({:?}) 暂无对应 Provider 实现", ep.name, ep.endpoint_type);
            }
        }
    }
}

/// 根据端点类型自动注册对应的 Provider（sync 版本，仅在 setup 同步上下文使用）
fn register_providers_from_config(state: &AppState, endpoints: &[AiEndpoint]) {
    let mut registry = state.providers.blocking_write();
    for ep in endpoints.iter().filter(|e| e.enabled) {
        match ep.endpoint_type {
            EndpointType::AzureOpenAi | EndpointType::OpenAiCompatible | EndpointType::ApiManagementGateway | EndpointType::Custom => {
                registry.register_ai_completion(Arc::new(OpenAiChatProvider::new(ep.clone())));
                registry.register_image_gen(Arc::new(OpenAiImageProvider::new(ep.clone())));
                registry.register_text_translation(Arc::new(OpenAiTranslationProvider::new(ep.clone())));
                registry.register_realtime_speech(Arc::new(OpenAiRealtimeProvider::new(ep.clone())));
                tracing::info!("已注册 AI+Image+Translation+Realtime Provider: {} ({})", ep.name, ep.id);
            }
            EndpointType::AzureSpeech => {
                registry.register_realtime_speech(Arc::new(AzureSpeechProvider::new(ep.clone())));
                registry.register_stt(Arc::new(AzureSttProvider::new(ep.clone())));
                registry.register_tts(Arc::new(AzureTtsProvider::new(ep.clone())));
                tracing::info!("已注册 Speech+STT+TTS Provider: {} ({})", ep.name, ep.id);
            }
            _ => {
                tracing::debug!("端点 {} ({:?}) 暂无对应 Provider 实现", ep.name, ep.endpoint_type);
            }
        }
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // O-26: CrashLogger — panic hook 记录崩溃日志
    {
        let default_hook = std::panic::take_hook();
        std::panic::set_hook(Box::new(move |info| {
            // 写入崩溃日志到临时目录
            let crash_dir = std::env::temp_dir().join("TrueFluentPro-crash-logs");
            let _ = std::fs::create_dir_all(&crash_dir);
            let ts = chrono::Local::now().format("%Y%m%d_%H%M%S");
            let path = crash_dir.join(format!("crash_{ts}.log"));
            let mut msg = format!("=== TrueFluentPro Crash Report ===\nTime: {}\n", chrono::Local::now().to_rfc3339());
            if let Some(loc) = info.location() {
                msg.push_str(&format!("Location: {}:{}:{}\n", loc.file(), loc.line(), loc.column()));
            }
            msg.push_str(&format!("Payload: {info}\n"));
            msg.push_str(&format!("Backtrace:\n{:?}\n", std::backtrace::Backtrace::force_capture()));
            let _ = std::fs::write(&path, &msg);
            eprintln!("[CrashLogger] 崩溃日志已写入: {}", path.display());
            // 调用默认 hook（打印到 stderr）
            default_hook(info);
        }));
    }

    // 初始化日志
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "truefluent_pro_lib=debug,info".into()),
        )
        .init();

    tracing::info!("译见 Pro 启动");

    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            // 确定数据目录并打开数据库
            let data_dir = app.path().app_data_dir().expect("无法获取数据目录");
            std::fs::create_dir_all(&data_dir).expect("无法创建数据目录");
            let db_path = data_dir.join("truefluent.db");
            tracing::info!("数据库路径: {}", db_path.display());

            let db = Database::open(&db_path).expect("无法打开数据库");
            let app_state = AppState::new(db);

            // 从已保存的配置自动注册 Provider
            {
                let config = app_state.config.blocking_read();
                register_providers_from_config(&app_state, &config.endpoints);
            }

            app.manage(app_state);

            // 启动后台任务引擎
            let state: tauri::State<'_, AppState> = app.state();
            let db_arc = state.db.clone();
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                let state_ref: &AppState = handle.state::<AppState>().inner();
                // 安全: AppState 通过 Tauri State 管理，生命周期跟随 app
                let state_arc = Arc::new(tokio::sync::RwLock::new(()));
                let _ = state_arc; // placeholder
                let engine = task_engine::TaskEngine::start_with_app(handle.clone(), db_arc);
                let mut te = state_ref.task_engine.write().await;
                *te = Some(engine);
                tracing::info!("任务引擎已启动");
            });

            // 启动时自动刷新所有 AAD 端点的 token（后台静默执行）
            let handle2 = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                let state_ref: &AppState = handle2.state::<AppState>().inner();
                let endpoints_to_refresh: Vec<(String, String, String)> = {
                    let tokens = state_ref.refresh_tokens.read().await;
                    let config = state_ref.config.read().await;
                    config.endpoints.iter()
                        .filter(|ep| ep.auth_mode == "aad" && tokens.contains_key(&ep.id))
                        .map(|ep| {
                            let tid = if ep.azure_tenant_id.is_empty() { "common".to_string() } else { ep.azure_tenant_id.clone() };
                            let cid = if ep.azure_client_id.is_empty() { "04b07795-8ddb-461a-bbee-02f9e1bf7b46".to_string() } else { ep.azure_client_id.clone() };
                            (ep.id.clone(), tid, cid)
                        })
                        .collect()
                };

                for (endpoint_id, tenant_id, client_id) in endpoints_to_refresh {
                    let rt = {
                        let tokens = state_ref.refresh_tokens.read().await;
                        tokens.get(&endpoint_id).cloned()
                    };
                    let Some(refresh_token) = rt else { continue };

                    match commands::auth::refresh_token_silent(state_ref, &endpoint_id, &tenant_id, &client_id, &refresh_token).await {
                        Ok(_) => tracing::info!("AAD token 自动刷新成功: {endpoint_id}"),
                        Err(e) => tracing::warn!("AAD token 自动刷新失败 ({endpoint_id}): {e}"),
                    }
                }

                // AAD token 刷新后，重新注册 providers 让新 token 对所有能力生效
                let config = state_ref.config.read().await;
                let endpoints = config.endpoints.clone();
                drop(config);
                register_providers_from_config_async(state_ref, &endpoints).await;
                tracing::info!("AAD 启动刷新后已重新注册 providers");
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            // 配置
            commands::get_config,
            commands::update_config,
            commands::add_endpoint,
            commands::remove_endpoint,
            commands::update_endpoint,
            // 翻译
            commands::translate_text,
            commands::start_realtime_translation,
            commands::stop_realtime_translation,
            // Provider
            commands::list_providers,
            // AI 媒体
            commands::generate_image,
            commands::save_image,
            commands::list_saved_images,
            commands::ai_complete,
            // 存储
            commands::get_translation_history,
            commands::validate_storage_connection,
            // 会话 & 消息
            commands::list_sessions,
            commands::create_session,
            commands::delete_session,
            commands::rename_session,
            commands::get_session_messages,
            commands::add_message,
            commands::get_supported_languages,
            commands::optimize_prompt,
            // 音频库 & 生命周期
            commands::list_audio_items,
            commands::add_audio_item,
            commands::delete_audio_item,
            commands::get_audio_lifecycle,
            commands::update_lifecycle_stage,
            commands::list_audio_devices,
            // 任务引擎
            commands::submit_task,
            commands::cancel_task,
            commands::retry_task,
            commands::get_task_engine_stats,
            commands::update_task_engine_config,
            commands::cleanup_expired_tasks,
            commands::list_tasks,
            commands::get_task_executions,
            // 系统
            commands::get_app_info,
            commands::refresh_providers,
            commands::ai_complete_stream,
            commands::test_endpoint,
            commands::get_vendor_profiles,
            commands::discover_models,
            // AAD 认证
            commands::aad_start_device_code_flow,
            commands::aad_select_tenant,
            commands::aad_refresh_token,
            // 配置导入/导出
            commands::export_config,
            commands::import_config,
            commands::write_text_file,
            commands::read_text_file,
            // 计费
            commands::get_billing_records,
            commands::get_billing_summary,
            // 图片管道
            commands::run_image_pipeline,
            commands::get_image_model_catalog,
            // 视频（预留）
            commands::generate_video,
        ])
        .run(tauri::generate_context!())
        .expect("启动 Tauri 应用失败");
}
