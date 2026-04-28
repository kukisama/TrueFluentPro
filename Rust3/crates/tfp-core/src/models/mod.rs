mod config;
mod settings;
mod api;
mod live;
mod studio;
mod center;
mod audiolab;

pub use config::*;
pub use settings::*;
pub use api::*;
pub use live::*;
pub use studio::*;
pub use center::*;
pub use audiolab::*;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_app_config_roundtrip() {
        let config = AppConfig::default();
        let json = serde_json::to_string(&config).unwrap();
        let restored: AppConfig = serde_json::from_str(&json).unwrap();
        assert_eq!(restored.default_source_lang, "zh-Hans");
        assert_eq!(restored.default_target_langs, vec!["en"]);
        assert_eq!(restored.audio.sample_rate, 16000);
        assert_eq!(restored.ui.theme, "dark");
        assert_eq!(restored.ai.max_conversation_turns, 20);
        assert!(restored.ai.insight_system_prompt.is_empty());
        assert!(restored.endpoints.is_empty());
    }

    #[test]
    fn test_endpoint_type_serde() {
        let et = EndpointType::AzureOpenAi;
        let json = serde_json::to_value(&et).unwrap();
        assert_eq!(json, "azure_open_ai");

        let et2 = EndpointType::ApiManagementGateway;
        let json2 = serde_json::to_value(&et2).unwrap();
        assert_eq!(json2, "api_management_gateway");

        let et3 = EndpointType::OpenAiCompatible;
        let json3 = serde_json::to_value(&et3).unwrap();
        assert_eq!(json3, "open_ai_compatible");
    }

    #[test]
    fn test_ai_endpoint_migrate_auth() {
        let mut azure_ep = AiEndpoint {
            id: "ep1".into(),
            name: "Azure".into(),
            endpoint_type: EndpointType::AzureOpenAi,
            url: "https://example.com".into(),
            api_key: "key".into(),
            api_version: None,
            region: None,
            models: vec![],
            enabled: true,
            auth_header_mode: "auto".into(),
            auth_mode: "api_key".into(),
            azure_tenant_id: String::new(),
            azure_client_id: String::new(),
            speech_subscription_key: String::new(),
            speech_region: String::new(),
            speech_endpoint: String::new(),
        };
        azure_ep.migrate_auth_header_mode();
        assert_eq!(azure_ep.auth_header_mode, "api_key");

        let mut oai_ep = AiEndpoint {
            id: "ep2".into(),
            name: "OpenAI".into(),
            endpoint_type: EndpointType::OpenAiCompatible,
            url: "https://api.openai.com".into(),
            api_key: "key".into(),
            api_version: None,
            region: None,
            models: vec![],
            enabled: true,
            auth_header_mode: "auto".into(),
            auth_mode: "api_key".into(),
            azure_tenant_id: String::new(),
            azure_client_id: String::new(),
            speech_subscription_key: String::new(),
            speech_region: String::new(),
            speech_endpoint: String::new(),
        };
        oai_ep.migrate_auth_header_mode();
        assert_eq!(oai_ep.auth_header_mode, "bearer");
    }

    #[test]
    fn test_realtime_event_tagged_serde() {
        let evt = RealtimeEvent::SessionStarted {
            session_id: "s1".into(),
        };
        let json = serde_json::to_value(&evt).unwrap();
        assert_eq!(json["type"], "SessionStarted");
        assert_eq!(json["data"]["session_id"], "s1");

        let evt2 = RealtimeEvent::Translated {
            source_text: "hello".into(),
            translations: [("zh".into(), "你好".into())].into_iter().collect(),
        };
        let json2 = serde_json::to_value(&evt2).unwrap();
        assert_eq!(json2["type"], "Translated");
        assert_eq!(json2["data"]["source_text"], "hello");
    }
}
