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

    // ── Serde JSON contract tests (batch 40) ──

    #[test]
    fn test_app_config_json_fields() {
        let config = AppConfig::default();
        let json = serde_json::to_value(&config).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "endpoints", "default_source_lang", "default_target_langs",
            "audio", "ui", "ai", "media", "storage", "recognition", "web_search",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        assert!(obj["endpoints"].is_array(), "endpoints should be array");
        assert!(obj["default_target_langs"].is_array(), "default_target_langs should be array");
    }

    #[test]
    fn test_ai_endpoint_json_fields() {
        let ep = AiEndpoint {
            id: "ep1".into(),
            name: "Test".into(),
            endpoint_type: EndpointType::AzureOpenAi,
            url: "https://example.com".into(),
            api_key: "key123".into(),
            api_version: Some("2024-02-01".into()),
            region: Some("eastus".into()),
            models: vec![],
            enabled: true,
            auth_header_mode: "api_key".into(),
            auth_mode: "api_key".into(),
            azure_tenant_id: "tid".into(),
            azure_client_id: "cid".into(),
            speech_subscription_key: "sk".into(),
            speech_region: "westus".into(),
            speech_endpoint: "https://speech.example.com".into(),
        };
        let json = serde_json::to_value(&ep).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "id", "name", "endpoint_type", "url", "api_key", "api_version",
            "region", "models", "enabled", "auth_header_mode", "auth_mode",
            "azure_tenant_id", "azure_client_id", "speech_subscription_key",
            "speech_region", "speech_endpoint",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        assert!(obj["endpoint_type"].is_string(), "endpoint_type should be string");
        assert!(obj["models"].is_array(), "models should be array");
    }

    #[test]
    fn test_vendor_profile_json_fields() {
        use std::collections::HashMap;
        let profile = VendorProfile {
            endpoint_type: EndpointType::AzureOpenAi,
            label: "Azure OpenAI".into(),
            badge: "Azure".into(),
            subtitle: "subtitle".into(),
            glyph: "\u{e753}".into(),
            default_auth_header: "api-key".into(),
            default_api_version: "2024-02-01".into(),
            supports_aad: true,
            supports_model_discovery: true,
            model_discovery_urls: vec!["https://example.com/models".into()],
            test_url_templates: HashMap::from([("text".into(), "/chat/completions".into())]),
            text_url_candidates: vec!["/openai/deployments/{model}/chat/completions".into()],
            image_url_candidates: vec![],
            video_url_candidates: vec![],
            audio_url_candidates: vec![],
            speech_url_candidates: vec![],
            text_protocol: "openai_chat".into(),
            supported_auth_modes: vec!["api_key".into(), "bearer".into()],
            raw_json: None,
        };
        let json = serde_json::to_value(&profile).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "endpoint_type", "label", "badge", "subtitle", "glyph",
            "default_auth_header", "default_api_version", "supports_aad",
            "supports_model_discovery", "model_discovery_urls", "test_url_templates",
            "text_url_candidates", "image_url_candidates", "video_url_candidates",
            "audio_url_candidates", "speech_url_candidates", "text_protocol",
            "supported_auth_modes",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        assert!(obj["test_url_templates"].is_object(), "test_url_templates should be object");
    }

    #[test]
    fn test_completion_request_json_fields() {
        let req = CompletionRequest {
            messages: vec![ChatMessage {
                role: "user".into(),
                content: serde_json::json!("Hello"),
            }],
            model: "gpt-4o".into(),
            temperature: Some(0.7),
            max_tokens: Some(1000),
            endpoint_id: "ep1".into(),
        };
        let json = serde_json::to_value(&req).unwrap();
        let obj = json.as_object().unwrap();
        for key in &["messages", "model", "temperature", "max_tokens", "endpoint_id"] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        assert!(obj["messages"].is_array(), "messages should be array");
    }

    #[test]
    fn test_image_gen_request_json_fields() {
        let req = ImageGenRequest {
            prompt: "a cat".into(),
            width: 1024,
            height: 1024,
            model: "dall-e-3".into(),
            quality: Some("hd".into()),
            output_format: Some("png".into()),
            background: Some("transparent".into()),
            n: Some(1),
            endpoint_id: "ep1".into(),
            text_model: None,
            image_model: None,
            previous_response_id: None,
        };
        let json = serde_json::to_value(&req).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "prompt", "width", "height", "model", "quality",
            "output_format", "background", "n", "endpoint_id",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
    }

    #[test]
    fn test_billing_record_json_fields() {
        let record = BillingRecord {
            id: "br1".into(),
            task_id: Some("t1".into()),
            endpoint_id: "ep1".into(),
            model_id: "gpt-4o".into(),
            prompt_tokens: 100,
            completion_tokens: 50,
            cost_usd: Some(0.005),
            created_at: "2026-04-29T00:00:00Z".into(),
            status: "Committed".into(),
        };
        let json = serde_json::to_value(&record).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "id", "task_id", "endpoint_id", "model_id", "prompt_tokens",
            "completion_tokens", "cost_usd", "created_at", "status",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
    }

    #[test]
    fn test_realtime_session_config_json_fields() {
        let config = RealtimeSessionConfig {
            source_lang: "en-US".into(),
            target_langs: vec!["zh-Hans".into(), "ja".into()],
            endpoint_id: "ep1".into(),
            enable_partial: true,
            profanity_filter: false,
            initial_silence_timeout_seconds: None,
            end_silence_timeout_seconds: None,
        };
        let json = serde_json::to_value(&config).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "source_lang", "target_langs", "endpoint_id", "enable_partial", "profanity_filter",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        assert!(obj["target_langs"].is_array(), "target_langs should be array");
    }

    #[test]
    fn test_realtime_event_all_variants_json() {
        let variants: Vec<(RealtimeEvent, &str)> = vec![
            (RealtimeEvent::SessionStarted { session_id: "s1".into() }, "SessionStarted"),
            (RealtimeEvent::Recognizing { text: "hi".into(), offset_ms: 0 }, "Recognizing"),
            (RealtimeEvent::Recognized { text: "hello".into(), duration_ms: 1000 }, "Recognized"),
            (RealtimeEvent::Translated {
                source_text: "hello".into(),
                translations: std::collections::HashMap::from([("zh".into(), "你好".into())]),
            }, "Translated"),
            (RealtimeEvent::SessionStopped { session_id: "s1".into() }, "SessionStopped"),
            (RealtimeEvent::Error { message: "fail".into() }, "Error"),
        ];
        for (evt, expected_type) in variants {
            let json = serde_json::to_value(&evt).unwrap();
            let obj = json.as_object().unwrap();
            assert!(obj.contains_key("type"), "missing 'type' key for {}", expected_type);
            assert!(obj.contains_key("data"), "missing 'data' key for {}", expected_type);
            assert_eq!(obj["type"].as_str().unwrap(), expected_type);
        }
    }

    #[test]
    fn test_endpoint_test_report_json_fields() {
        let report = EndpointTestReport {
            endpoint_id: "ep1".into(),
            endpoint_name: "Test EP".into(),
            endpoint_type_name: "azure_open_ai".into(),
            items: vec![EndpointTestItem {
                model_id: "gpt-4o".into(),
                capability: "text".into(),
                status: TestStatus::Success,
                summary: "OK".into(),
                detail: None,
                request_url: None,
                request_summary: None,
                duration_ms: 200,
                test_branch: None,
                urls_tried: vec!["https://example.com/chat".into()],
            }],
            duration_ms: 500,
            total_count: 1,
            success_count: 1,
            failed_count: 0,
            skipped_count: 0,
        };
        let json = serde_json::to_value(&report).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "endpoint_id", "endpoint_name", "endpoint_type_name", "items",
            "duration_ms", "total_count", "success_count", "failed_count", "skipped_count",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        let item = obj["items"].as_array().unwrap().first().unwrap();
        let item_obj = item.as_object().unwrap();
        for key in &["model_id", "capability", "status", "summary", "duration_ms", "urls_tried"] {
            assert!(item_obj.contains_key(*key), "item missing field: {}", key);
        }
    }

    #[test]
    fn test_task_engine_stats_json_fields() {
        let stats = TaskEngineStats::default();
        let json = serde_json::to_value(&stats).unwrap();
        let obj = json.as_object().unwrap();
        for key in &["queued", "executing", "completed", "failed", "cancelled", "total_tokens"] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
            assert!(obj[*key].is_number(), "{} should be number", key);
        }
    }

    #[test]
    fn test_translate_request_response_json_fields() {
        let req = TranslateRequest {
            text: "Hello".into(),
            source_lang: "en".into(),
            target_lang: "zh-Hans".into(),
            endpoint_id: Some("ep1".into()),
        };
        let json_req = serde_json::to_value(&req).unwrap();
        let req_obj = json_req.as_object().unwrap();
        for key in &["text", "source_lang", "target_lang"] {
            assert!(req_obj.contains_key(*key), "request missing field: {}", key);
        }

        let resp = TranslateResponse {
            translated_text: "你好".into(),
            source_lang: "en".into(),
            target_lang: "zh-Hans".into(),
            confidence: Some(0.99),
            provider: "azure".into(),
        };
        let json_resp = serde_json::to_value(&resp).unwrap();
        let resp_obj = json_resp.as_object().unwrap();
        for key in &["translated_text", "source_lang", "target_lang", "provider"] {
            assert!(resp_obj.contains_key(*key), "response missing field: {}", key);
        }
    }
}
