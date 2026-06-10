//! 译见 Pro 桌面应用库入口。

mod audio_devices;
mod audio_loopback;
mod commands;
mod live;
mod recorder;
mod state;

use tauri::Manager;
use tfp_core::AppConfig;

use crate::live::LiveTranslator;
use crate::recorder::Recorder;
use crate::state::AppState;

/// 启动 Tauri 应用。
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // 初始化日志
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "info,tfp_core=debug,tfp_desktop_lib=debug".into()),
        )
        .init();

    // 加载配置（不存在则用内置默认）
    let config = AppConfig::load().unwrap_or_else(|err| {
        tracing::warn!(%err, "加载配置失败，使用默认配置");
        AppConfig::with_defaults()
    });

    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .manage(AppState::new(config))
        .manage(Recorder::default())
        .setup(|app| {
            // 实时翻译引擎需要 AppHandle 才能向前端推事件，故在 setup 中创建。
            let translator = LiveTranslator::spawn(app.handle().clone());
            app.manage(translator);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::load_config,
            commands::save_config,
            commands::set_ui_prefs,
            commands::list_endpoints,
            commands::list_endpoint_profiles,
            commands::list_ai_endpoints,
            commands::build_endpoint_from_profile,
            commands::save_ai_endpoint,
            commands::delete_ai_endpoint,
            commands::test_ai_endpoint,
            commands::start_live_translation,
            commands::stop_live_translation,
            commands::is_live_running,
            commands::list_translation_history,
            commands::clear_translation_history,
            commands::list_speech_resources,
            commands::active_speech_resource_id,
            commands::save_speech_resource,
            commands::delete_speech_resource,
            commands::set_active_speech_resource,
            commands::set_languages,
            commands::list_audio_input_devices,
            commands::list_audio_output_devices,
            commands::audio_loopback_supported,
            commands::get_audio_settings,
            commands::set_audio_settings,
            commands::start_recording,
            commands::stop_recording,
            commands::is_recording,
        ])
        .run(tauri::generate_context!())
        .expect("Tauri 应用启动失败");
}
