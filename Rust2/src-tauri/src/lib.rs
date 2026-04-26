mod commands;
mod models;
mod providers;
mod state;
mod storage;
mod task_engine;
mod image_pipeline;

use std::sync::Arc;
use tauri::Manager;

use models::{EndpointType, AiEndpoint};
use providers::{OpenAiChatProvider, OpenAiImageProvider, AzureSpeechProvider, AzureSttProvider, AzureTtsProvider};
use state::AppState;
use storage::Database;

/// 根据端点类型自动注册对应的 Provider
fn register_providers_from_config(state: &AppState, endpoints: &[AiEndpoint]) {
    let mut registry = state.providers.blocking_write();
    for ep in endpoints.iter().filter(|e| e.enabled) {
        match ep.endpoint_type {
            EndpointType::AzureOpenAi | EndpointType::OpenAiCompatible | EndpointType::ApiManagementGateway | EndpointType::Custom => {
                // OpenAI 兼容端点同时注册 Chat 和 Image 能力
                registry.register_ai_completion(Arc::new(OpenAiChatProvider::new(ep.clone())));
                registry.register_image_gen(Arc::new(OpenAiImageProvider::new(ep.clone())));
                tracing::info!("已注册 AI+Image Provider: {} ({})", ep.name, ep.id);
            }
            EndpointType::AzureSpeech => {
                registry.register_realtime_speech(Arc::new(AzureSpeechProvider::new(ep.clone())));
                registry.register_stt(Arc::new(AzureSttProvider::new(ep.clone())));
                registry.register_tts(Arc::new(AzureTtsProvider::new(ep.clone())));
                tracing::info!("已注册 Speech+STT+TTS Provider: {} ({})", ep.name, ep.id);
            }
            // Translator / DeepL 等留空 — 后续插件化
            _ => {
                tracing::debug!("端点 {} ({:?}) 暂无对应 Provider 实现", ep.name, ep.endpoint_type);
            }
        }
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
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
            commands::ai_complete,
            // 存储
            commands::get_translation_history,
            commands::validate_storage_connection,
            // 会话 & 消息
            commands::list_sessions,
            commands::create_session,
            commands::delete_session,
            commands::get_session_messages,
            commands::add_message,
            // 音频库 & 生命周期
            commands::list_audio_items,
            commands::add_audio_item,
            commands::delete_audio_item,
            commands::get_audio_lifecycle,
            commands::update_lifecycle_stage,
            // 任务引擎
            commands::submit_task,
            commands::cancel_task,
            commands::retry_task,
            commands::get_task_engine_stats,
            commands::list_tasks,
            commands::get_task_executions,
            // 系统
            commands::get_app_info,
            commands::refresh_providers,
            commands::ai_complete_stream,
            commands::test_endpoint,
            commands::get_vendor_profiles,
            commands::discover_models,
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
