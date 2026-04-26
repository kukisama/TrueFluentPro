mod commands;
mod models;
mod providers;
mod state;
mod storage;

use std::sync::Arc;
use tauri::Manager;

use models::{EndpointType, AiEndpoint};
use providers::{OpenAiChatProvider, OpenAiImageProvider, AzureSpeechProvider};
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
                tracing::info!("已注册 Speech Provider: {} ({})", ep.name, ep.id);
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
            commands::get_batch_tasks,
            commands::validate_storage_connection,
            // 系统
            commands::get_app_info,
            commands::refresh_providers,
            commands::ai_complete_stream,
            commands::test_endpoint,
            commands::get_vendor_profiles,
            commands::discover_models,
        ])
        .run(tauri::generate_context!())
        .expect("启动 Tauri 应用失败");
}
