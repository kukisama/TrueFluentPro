mod commands;
mod state;
mod tauri_event_sink;

use std::sync::Arc;
use tauri::Manager;
use tfp_core::AiEndpoint;
use tfp_providers::register_providers;

use state::AppState;
use tfp_storage::Database;

/// Register providers synchronously (setup context only — no async runtime)
fn register_providers_sync(state: &AppState, endpoints: &[AiEndpoint]) {
    let mut registry = state.providers.blocking_write();
    registry.clear();
    register_providers(&mut registry, endpoints);
}

/// Register providers asynchronously (for use inside tokio runtime)
pub(crate) async fn register_providers_async(state: &AppState, endpoints: &[AiEndpoint]) {
    let mut registry = state.providers.write().await;
    registry.clear();
    register_providers(&mut registry, endpoints);
}

pub fn run() {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "truefluent_pro_r3_lib=debug,info".into()),
        )
        .init();

    tracing::info!("TrueFluentPro R3 starting");

    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let data_dir = app
                .path()
                .app_data_dir()
                .expect("cannot resolve app data dir");
            std::fs::create_dir_all(&data_dir).expect("cannot create data dir");
            let db_path = data_dir.join("truefluent.db");
            tracing::info!("Database path: {}", db_path.display());

            let db = Database::open(&db_path).expect("cannot open database");
            let app_state = AppState::new(db);

            {
                let config = app_state.config.blocking_read();
                register_providers_sync(&app_state, &config.endpoints);
            }

            app.manage(app_state);
            // Startup: auto-refresh all AAD endpoint tokens in background
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                let state_ref: &AppState = handle.state::<AppState>().inner();
                let endpoints_to_refresh: Vec<(String, String, String)> = {
                    let tokens = state_ref.refresh_tokens.read().await;
                    let config = state_ref.config.read().await;
                    config.endpoints.iter()
                        .filter(|ep| ep.auth_mode == tfp_core::AzureAuthMode::Aad && tokens.contains_key(&ep.id))
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

                // Re-register providers after AAD token refresh
                let config = state_ref.config.read().await;
                let endpoints = config.endpoints.clone();
                drop(config);
                register_providers_async(state_ref, &endpoints).await;
                tracing::info!("AAD 启动刷新后已重新注册 providers");
            });

            // Start the task engine in background
            {
                let handle = app.handle().clone();
                let state_ref: &AppState = handle.state::<AppState>().inner();
                let db_arc = state_ref.db.clone();
                let config_arc = state_ref.config.clone();
                let providers_arc = state_ref.providers.clone();
                let bus_arc = state_ref.task_event_bus.clone();
                let sink = Arc::new(tauri_event_sink::TauriEventSink::new(handle.clone()));

                let data_dir = handle
                    .path()
                    .app_data_dir()
                    .unwrap_or_else(|_| std::path::PathBuf::from("."));

                let handle2 = handle.clone();
                tauri::async_runtime::spawn(async move {
                    let deps = tfp_engine::TaskEngineDeps {
                        storage: db_arc.clone(),
                        config: config_arc,
                        providers: providers_arc,
                        bus: bus_arc,
                        sink,
                        tts_output_dir: data_dir,
                    };
                    let engine = tfp_engine::TaskEngine::start(deps);
                    let st: &AppState = handle2.state::<AppState>().inner();
                    *st.task_engine.write().await = Some(engine);
                    tracing::info!("Task engine started");
                    commands::studio::studio_resume_interrupted_video_tasks(handle2, db_arc).await;
                });
            }

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            // Config CRUD
            commands::get_config,
            commands::update_config,
            commands::add_endpoint,
            commands::remove_endpoint,
            commands::update_endpoint,
            commands::export_config,
            commands::import_config,
            commands::validate_storage_connection,
            // Provider management
            commands::list_providers,
            commands::refresh_providers,
            commands::get_vendor_profiles,
            // System
            commands::get_app_info,
            commands::write_text_file,
            commands::read_text_file,
            commands::get_billing_records,
            commands::get_billing_summary,
            // Translation
            commands::translate_text,
            commands::get_supported_languages,
            commands::start_realtime_translation,
            commands::stop_realtime_translation,
            // Sessions
            commands::list_sessions,
            commands::create_session,
            commands::delete_session,
            commands::rename_session,
            commands::get_session_messages,
            commands::add_session_message,
            commands::get_translation_history,
            // AI Completion
            commands::ai_complete,
            commands::ai_complete_stream,
            // Endpoint testing
            commands::test_endpoint,
            commands::discover_models,
            // Image generation
            commands::generate_image,
            commands::save_image,
            commands::list_saved_images,
            // Video generation
            commands::generate_video,
            // Prompt optimization
            commands::optimize_prompt,
            // Live translation
            commands::live_get_active_session,
            commands::live_get_recent_segments,
            commands::live_bookmark_segment,
            commands::live_unbookmark_segment,
            commands::live_list_supported_languages,
            commands::live_list_sessions,
            commands::live_get_session_segments,
            commands::live_export_subtitles,
            commands::live_clear_session_segments,
            // Floating windows
            commands::live_show_floating_subtitle,
            commands::live_hide_floating_subtitle,
            commands::live_toggle_floating_subtitle,
            commands::live_show_floating_insight,
            commands::live_hide_floating_insight,
            // Audio library & task engine
            commands::list_audio_devices,
            commands::list_audio_items,
            commands::add_audio_item,
            commands::delete_audio_item,
            commands::get_audio_lifecycle,
            commands::update_lifecycle_stage,
            commands::submit_task,
            commands::cancel_task,
            commands::retry_task,
            commands::get_task_engine_stats,
            commands::update_task_engine_config,
            commands::cleanup_expired_tasks,
            commands::list_tasks,
            commands::get_task_executions,
            // Monitor
            commands::monitor_get_snapshot,
            commands::monitor_set_bucket,
            commands::monitor_list_executions,
            commands::monitor_get_execution_detail,
            commands::monitor_cancel_task,
            commands::monitor_get_settings,
            commands::monitor_update_settings,
            commands::monitor_cleanup_completed,
            commands::monitor_refresh,
            commands::monitor_retry_task,
            commands::monitor_batch_cancel,
            commands::monitor_batch_delete,
            commands::monitor_export_csv,
            commands::monitor_get_archived_snapshot,
            commands::monitor_save_ui_state,
            commands::monitor_load_ui_state,
            // Studio
            commands::studio_list_sessions,
            commands::studio_get_session,
            commands::studio_create_session,
            commands::studio_rename_session,
            commands::studio_soft_delete_session,
            commands::studio_get_session_bundle,
            commands::studio_append_message,
            commands::studio_get_messages_before,
            commands::studio_list_running_tasks,
            commands::studio_chat_stream,
            commands::studio_start_image_task,
            commands::studio_start_video_task,
            commands::studio_cancel_task,
            commands::studio_add_reference_image,
            commands::studio_delete_reference_image,
            commands::studio_list_reference_images,
            // Center (media center)
            commands::center_list_workspaces,
            commands::center_create_workspace,
            commands::center_rename_workspace,
            commands::center_soft_delete_workspace,
            commands::center_get_workspace_bundle,
            commands::center_list_rounds,
            commands::center_get_round,
            commands::center_set_active_round,
            commands::center_start_image_round,
            commands::center_start_video_round,
            commands::center_select_assets,
            commands::center_delete_assets,
            commands::center_export_assets,
            commands::center_list_running_tasks,
            commands::center_get_round_assets,
            commands::video_get_capabilities,
            // AudioLab (听析中心)
            commands::audiolab_import_files,
            commands::audiolab_list_files,
            commands::audiolab_get_file,
            commands::audiolab_remove_file,
            commands::audiolab_get_bundle,
            commands::audiolab_start_transcription,
            commands::audiolab_list_running_tasks,
            commands::audiolab_list_stage_presets,
            commands::audiolab_upsert_stage_preset,
            commands::audiolab_delete_stage_preset,
            commands::audiolab_playback_open,
            commands::audiolab_start_stage,
            commands::audiolab_update_stage_content,
            commands::audiolab_start_podcast_tts,
            commands::audiolab_generate_auto_tags,
            commands::audiolab_add_manual_tag,
            commands::audiolab_remove_auto_tag,
            commands::audiolab_add_research_topic,
            commands::audiolab_start_research,
            commands::audiolab_remove_research_topic,
            commands::audiolab_rename_speaker,
            commands::audiolab_update_segment,
            commands::audiolab_export,
            commands::audiolab_import_from_realtime,
            // AAD authentication
            commands::aad_start_device_code_flow,
            commands::aad_select_tenant,
            commands::aad_refresh_token,
            // Image pipeline
            commands::run_image_pipeline,
            commands::get_image_model_catalog,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
