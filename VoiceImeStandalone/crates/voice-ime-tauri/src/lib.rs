//! Tauri 2 工具窗入口与启动页签路由解析。

pub mod commands;

/// 解析命令行参数（支持 `--tab settings|usage|language` 以及兼容旧开关 `--voice-ime-settings|--voice-ime-usage|--voice-ime-language`）。
pub fn parse_initial_tab_from_args<I, S>(args: I) -> String
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let list: Vec<String> = args.into_iter().map(|s| s.as_ref().to_string()).collect();
    for (idx, item) in list.iter().enumerate() {
        match item.as_str() {
            "--tab" => {
                if let Some(next) = list.get(idx + 1) {
                    return match next.to_ascii_lowercase().as_str() {
                        "usage" => "usage".into(),
                        "language" => "language".into(),
                        _ => "settings".into(),
                    };
                }
            }
            "--voice-ime-usage" => return "usage".into(),
            "--voice-ime-language" => return "language".into(),
            "--voice-ime-settings" => return "settings".into(),
            _ => {}
        }
    }
    "settings".into()
}

#[cfg(not(target_os = "linux"))]
pub fn run() {
    use voice_ime_controller::ToolsAppContext;

    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "warn,voice_ime_tauri_lib=info".into()),
        )
        .init();

    let initial_tab = parse_initial_tab_from_args(std::env::args().skip(1));
    let state = commands::ManagedToolsState {
        initial_tab,
        ctx: ToolsAppContext::new_production(),
    };

    tauri::Builder::default()
        .manage(state)
        .invoke_handler(tauri::generate_handler![
            commands::get_initial_tab,
            commands::load_settings_page,
            commands::preview_endpoint_url,
            commands::save_general_settings,
            commands::upsert_speech_subscription,
            commands::delete_speech_subscription,
            commands::test_speech_subscription,
            commands::import_legacy_truefluent_subscriptions,
            commands::load_language_page,
            commands::save_language_page,
            commands::load_usage_report,
            commands::load_session_original_text,
            commands::query_install_status,
            commands::manage_ime_installation,
        ])
        .run(tauri::generate_context!())
        .expect("启动译见pro 输入法工具窗失败");
}

#[cfg(target_os = "linux")]
pub fn run() {
    eprintln!("TrueFluentVoiceImeTools 桌面窗口在 Windows WebView2 环境下运行。");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_cli_tab_switches_accurately() {
        assert_eq!(parse_initial_tab_from_args(["--tab", "usage"]), "usage");
        assert_eq!(parse_initial_tab_from_args(["--tab", "language"]), "language");
        assert_eq!(parse_initial_tab_from_args(["--voice-ime-usage"]), "usage");
        assert_eq!(parse_initial_tab_from_args([] as [&str; 0]), "settings");
    }
}
