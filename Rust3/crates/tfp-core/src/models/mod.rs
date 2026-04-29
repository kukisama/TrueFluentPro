mod config;
mod settings;
mod api;
mod live;
mod studio;
mod center;
mod audiolab;
mod enums;
mod cloud;
mod common;

pub use config::*;
pub use settings::*;
pub use api::*;
pub use live::*;
pub use studio::*;
pub use center::*;
pub use audiolab::*;
pub use enums::*;
pub use cloud::*;
pub use common::*;

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
            auth_header_mode: ApiKeyHeaderMode::Auto,
            auth_mode: AzureAuthMode::ApiKey,
            ..AiEndpoint::default()
        };
        azure_ep.migrate_auth_header_mode();
        assert_eq!(azure_ep.auth_header_mode, ApiKeyHeaderMode::ApiKeyHeader);

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
            auth_header_mode: ApiKeyHeaderMode::Auto,
            auth_mode: AzureAuthMode::ApiKey,
            ..AiEndpoint::default()
        };
        oai_ep.migrate_auth_header_mode();
        assert_eq!(oai_ep.auth_header_mode, ApiKeyHeaderMode::Bearer);
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
            auth_header_mode: ApiKeyHeaderMode::ApiKeyHeader,
            auth_mode: AzureAuthMode::ApiKey,
            azure_tenant_id: "tid".into(),
            azure_client_id: "cid".into(),
            speech_subscription_key: "sk".into(),
            speech_region: "westus".into(),
            speech_endpoint: "https://speech.example.com".into(),
            ..AiEndpoint::default()
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
            reasoning_effort: None,
            enable_image_generation: false,
            image_model_deployment: None,
            image_size: None,
            image_quality: None,
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
            reference_image_path: None,
            image_edit_mode: None,
            uploaded_file_ids: vec![],
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
            (RealtimeEvent::AudioLevel { level: 0.75 }, "AudioLevel"),
            (RealtimeEvent::ReconnectAttempt { attempt: 1, delay_ms: 2000 }, "ReconnectAttempt"),
            (RealtimeEvent::ReconnectSuccess, "ReconnectSuccess"),
            (RealtimeEvent::Canceled {
                reason: "EndOfStream".into(),
                error_code: "ConnectionFailure".into(),
                error_details: "Connection was closed".into(),
            }, "Canceled"),
        ];
        for (evt, expected_type) in variants {
            let json = serde_json::to_value(&evt).unwrap();
            let obj = json.as_object().unwrap();
            assert!(obj.contains_key("type"), "missing 'type' key for {}", expected_type);
            // Unit variants like ReconnectSuccess have no "data" key
            if expected_type != "ReconnectSuccess" {
                assert!(obj.contains_key("data"), "missing 'data' key for {}", expected_type);
            }
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

    // ── Serde JSON contract tests (batch 41 — Studio) ──

    #[test]
    fn test_studio_session_json_fields() {
        let session = StudioSession {
            id: "s1".into(),
            session_type: "image".into(),
            name: "Test".into(),
            directory_path: "/tmp".into(),
            canvas_mode: "single".into(),
            media_kind: "image".into(),
            is_deleted: false,
            created_at: "2026-01-01T00:00:00Z".into(),
            updated_at: "2026-01-01T00:00:00Z".into(),
            last_accessed_at: None,
            source_session_id: None,
            source_session_name: None,
            source_session_directory_name: None,
            source_asset_id: None,
            source_asset_kind: None,
            source_asset_file_name: None,
            source_asset_path: None,
            source_preview_path: None,
            source_reference_role: None,
            message_count: 0,
            task_count: 0,
            asset_count: 0,
            latest_message_preview: None,
            legacy_source_path: None,
            import_batch_id: None,
            imported_at: None,
            is_legacy_import: false,
        };
        let json = serde_json::to_value(&session).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "id", "session_type", "name", "directory_path", "canvas_mode",
            "media_kind", "is_deleted", "created_at", "updated_at",
            "last_accessed_at", "source_session_id", "source_session_name",
            "source_session_directory_name", "source_asset_id", "source_asset_kind",
            "source_asset_file_name", "source_asset_path", "source_preview_path",
            "source_reference_role", "message_count", "task_count", "asset_count",
            "latest_message_preview", "legacy_source_path", "import_batch_id",
            "imported_at", "is_legacy_import",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
    }

    #[test]
    fn test_studio_message_json_fields() {
        let msg = StudioMessage {
            id: "m1".into(),
            session_id: "s1".into(),
            sequence_no: 1,
            role: "user".into(),
            content_type: "text".into(),
            text: "Hello".into(),
            reasoning_text: "".into(),
            prompt_tokens: Some(10),
            completion_tokens: Some(20),
            generate_seconds: Some(1.5),
            download_seconds: None,
            search_summary: None,
            timestamp: "2026-01-01T00:00:00Z".into(),
            is_deleted: false,
        };
        let json = serde_json::to_value(&msg).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "id", "session_id", "sequence_no", "role", "content_type", "text",
            "reasoning_text", "prompt_tokens", "completion_tokens",
            "generate_seconds", "download_seconds", "search_summary",
            "timestamp", "is_deleted",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
    }

    #[test]
    fn test_studio_task_json_fields() {
        let task = StudioTask {
            id: "t1".into(),
            session_id: "s1".into(),
            task_type: "image_gen".into(),
            status: "completed".into(),
            prompt: "a cat".into(),
            progress: 1.0,
            result_file_path: Some("/tmp/out.png".into()),
            error_message: None,
            has_reference_input: false,
            remote_video_id: None,
            remote_video_api_mode: None,
            remote_generation_id: None,
            remote_download_url: None,
            generate_seconds: Some(5.0),
            download_seconds: None,
            created_at: "2026-01-01T00:00:00Z".into(),
            updated_at: "2026-01-01T00:00:00Z".into(),
        };
        let json = serde_json::to_value(&task).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "id", "session_id", "task_type", "status", "prompt", "progress",
            "result_file_path", "error_message", "has_reference_input",
            "remote_video_id", "remote_video_api_mode", "remote_generation_id",
            "remote_download_url", "generate_seconds", "download_seconds",
            "created_at", "updated_at",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
    }

    #[test]
    fn test_studio_session_bundle_json_fields() {
        use std::collections::HashMap;
        let bundle = StudioSessionBundle {
            messages: vec![],
            media_refs: HashMap::new(),
            citations: HashMap::new(),
            attachments: HashMap::new(),
        };
        let json = serde_json::to_value(&bundle).unwrap();
        let obj = json.as_object().unwrap();
        for key in &["messages", "media_refs", "citations", "attachments"] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        assert!(obj["messages"].is_array(), "messages should be array");
        assert!(obj["media_refs"].is_object(), "media_refs should be object");
    }

    // ── Serde JSON contract tests (batch 41 — Center) ──

    #[test]
    fn test_center_workspace_json_fields() {
        let ws = CenterWorkspace {
            id: "ws1".into(),
            session_type: "canvas_image".into(),
            name: "Workspace".into(),
            is_deleted: false,
            created_at: "2026-01-01T00:00:00Z".into(),
            updated_at: "2026-01-01T00:00:00Z".into(),
            last_accessed_at: None,
            current_round_id: None,
            round_count: 0,
            asset_count: 0,
            has_running_task: false,
        };
        let json = serde_json::to_value(&ws).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "id", "session_type", "name", "is_deleted", "created_at", "updated_at",
            "last_accessed_at", "current_round_id", "round_count", "asset_count",
            "has_running_task",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
    }

    #[test]
    fn test_canvas_round_json_fields() {
        let round = CanvasRound {
            id: "r1".into(),
            session_id: "ws1".into(),
            round_index: 0,
            prompt: "sunset".into(),
            params_json: "{}".into(),
            model_ref: "dall-e-3".into(),
            created_at: "2026-01-01T00:00:00Z".into(),
            status: "completed".into(),
        };
        let json = serde_json::to_value(&round).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "id", "session_id", "round_index", "prompt", "params_json",
            "model_ref", "created_at", "status",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
    }

    #[test]
    fn test_center_asset_detail_json_fields() {
        let detail = CenterAssetDetail {
            id: "cra1".into(),
            round_id: "r1".into(),
            asset_id: "a1".into(),
            sequence: 0,
            is_selected: true,
            file_path: "/tmp/img.png".into(),
            preview_path: "/tmp/img_thumb.png".into(),
            kind: "image".into(),
            width: Some(1024),
            height: Some(1024),
            duration_ms: None,
            created_at: "2026-01-01T00:00:00Z".into(),
        };
        let json = serde_json::to_value(&detail).unwrap();
        let obj = json.as_object().unwrap();
        for key in &[
            "id", "round_id", "asset_id", "sequence", "is_selected",
            "file_path", "preview_path", "kind", "width", "height",
            "duration_ms", "created_at",
        ] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
    }

    #[test]
    fn test_video_capability_entry_json_fields() {
        let entry = VideoCapabilityEntry {
            aspect_ratio: "16:9".into(),
            resolution: "1920x1080".into(),
            duration_seconds: vec![5, 10],
            max_count: 4,
        };
        let json = serde_json::to_value(&entry).unwrap();
        let obj = json.as_object().unwrap();
        for key in &["aspect_ratio", "resolution", "duration_seconds", "max_count"] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        assert!(obj["duration_seconds"].is_array(), "duration_seconds should be array");
    }

    // ── Serde JSON contract tests (batch 41 — Video + pipeline) ──

    #[test]
    fn test_video_gen_request_json_fields() {
        let req = VideoGenRequest {
            prompt: "a sunset".into(),
            model: "sora".into(),
            endpoint_id: "ep1".into(),
            size: "1920x1080".into(),
            duration_seconds: 10,
            api_mode: None,
            reference_image_path: None,
            n: None,
        };
        let json = serde_json::to_value(&req).unwrap();
        let obj = json.as_object().unwrap();
        for key in &["prompt", "model", "endpoint_id", "size", "duration_seconds"] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        assert!(obj["duration_seconds"].is_number(), "duration_seconds should be number");
    }

    #[test]
    fn test_export_result_json_fields() {
        let result = ExportResult {
            copied: 5,
            failed: 1,
        };
        let json = serde_json::to_value(&result).unwrap();
        let obj = json.as_object().unwrap();
        for key in &["copied", "failed"] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
        assert!(obj["copied"].is_number(), "copied should be number");
        assert!(obj["failed"].is_number(), "failed should be number");
    }

    #[test]
    fn test_studio_reference_image_json_fields() {
        let img = StudioReferenceImage {
            id: "ri1".into(),
            session_id: "s1".into(),
            file_path: "/tmp/ref.png".into(),
            sort_order: 0,
            width: Some(512),
            height: Some(512),
            created_at: "2026-01-01T00:00:00Z".into(),
        };
        let json = serde_json::to_value(&img).unwrap();
        let obj = json.as_object().unwrap();
        for key in &["id", "session_id", "file_path", "sort_order", "width", "height", "created_at"] {
            assert!(obj.contains_key(*key), "missing field: {}", key);
        }
    }

    // ── Batch 45 — AiEndpoint methods + Settings defaults + enum serde ──

    fn make_endpoint(ep_type: EndpointType) -> AiEndpoint {
        AiEndpoint {
            id: "ep-test".into(),
            name: "Test".into(),
            endpoint_type: ep_type,
            url: "https://example.com".into(),
            enabled: true,
            ..AiEndpoint::default()
        }
    }

    // T-001
    #[test]
    fn test_ai_endpoint_is_azure() {
        assert!(make_endpoint(EndpointType::AzureOpenAi).is_azure());
        assert!(make_endpoint(EndpointType::ApiManagementGateway).is_azure());
        assert!(make_endpoint(EndpointType::AzureSpeech).is_azure());
        assert!(!make_endpoint(EndpointType::OpenAiCompatible).is_azure());
        assert!(!make_endpoint(EndpointType::AzureTranslator).is_azure());
        assert!(!make_endpoint(EndpointType::DeepL).is_azure());
        assert!(!make_endpoint(EndpointType::TencentCloud).is_azure());
        assert!(!make_endpoint(EndpointType::AlibabaCloud).is_azure());
        assert!(!make_endpoint(EndpointType::Custom).is_azure());
    }

    // T-002
    #[test]
    fn test_ai_endpoint_is_speech() {
        assert!(make_endpoint(EndpointType::AzureSpeech).is_speech());
        assert!(!make_endpoint(EndpointType::AzureOpenAi).is_speech());
        assert!(!make_endpoint(EndpointType::ApiManagementGateway).is_speech());
        assert!(!make_endpoint(EndpointType::OpenAiCompatible).is_speech());
        assert!(!make_endpoint(EndpointType::AzureTranslator).is_speech());
        assert!(!make_endpoint(EndpointType::DeepL).is_speech());
        assert!(!make_endpoint(EndpointType::TencentCloud).is_speech());
        assert!(!make_endpoint(EndpointType::AlibabaCloud).is_speech());
        assert!(!make_endpoint(EndpointType::Custom).is_speech());
    }

    #[test]
    fn test_first_model_with_capability() {
        let mut ep = make_endpoint(EndpointType::AzureOpenAi);
        ep.models = vec![
            AiModelEntry {
                model_id: "model-a".into(),
                display_name: "Model A".into(),
                deployment_name: None,
                capabilities: vec![ModelCapability::Text, ModelCapability::Image],
                group_name: None,
            },
            AiModelEntry {
                model_id: "model-b".into(),
                display_name: "Model B".into(),
                deployment_name: None,
                capabilities: vec![ModelCapability::Video],
                group_name: None,
            },
        ];
        let text_model = ep.first_model_with_capability(ModelCapability::Text);
        assert!(text_model.is_some());
        assert_eq!(text_model.unwrap().model_id, "model-a");

        let video_model = ep.first_model_with_capability(ModelCapability::Video);
        assert!(video_model.is_some());
        assert_eq!(video_model.unwrap().model_id, "model-b");

        let stt_model = ep.first_model_with_capability(ModelCapability::SpeechToText);
        assert!(stt_model.is_none());
    }

    // T-003
    #[test]
    fn test_effective_deployment() {
        let with_dep = AiModelEntry {
            model_id: "gpt-4o".into(),
            display_name: "GPT-4o".into(),
            deployment_name: Some("gpt-4o-dep".into()),
            capabilities: vec![ModelCapability::Text],
                group_name: None,
        };
        assert_eq!(with_dep.effective_deployment(), "gpt-4o-dep");

        let without_dep = AiModelEntry {
            model_id: "gpt-4o".into(),
            display_name: "GPT-4o".into(),
            deployment_name: None,
            capabilities: vec![ModelCapability::Text],
                group_name: None,
        };
        assert_eq!(without_dep.effective_deployment(), "gpt-4o");

        let empty_dep = AiModelEntry {
            model_id: "gpt-4o".into(),
            display_name: "GPT-4o".into(),
            deployment_name: Some("".into()),
            capabilities: vec![ModelCapability::Text],
                group_name: None,
        };
        assert_eq!(empty_dep.effective_deployment(), "");
    }

    // T-004
    #[test]
    fn test_migrate_auth_no_op_when_not_auto() {
        let mut ep = make_endpoint(EndpointType::AzureOpenAi);
        ep.auth_header_mode = ApiKeyHeaderMode::Bearer;
        ep.migrate_auth_header_mode();
        assert_eq!(ep.auth_header_mode, ApiKeyHeaderMode::Bearer);
    }

    // T-005
    #[test]
    fn test_media_settings_default() {
        let s = MediaSettings::default();
        assert_eq!(s.image_quality, "auto");
        assert_eq!(s.image_format, "png");
        assert_eq!(s.image_size, "1024x1024");
        assert_eq!(s.image_count, 1);
        assert_eq!(s.video_aspect_ratio, "16:9");
        assert_eq!(s.video_resolution, "720p");
        assert_eq!(s.video_seconds, 5);
        assert_eq!(s.video_poll_interval_ms, 3000);
    }

    #[test]
    fn test_storage_settings_default() {
        let s = StorageSettings::default();
        assert_eq!(s.batch_audio_container_name, "truefluentpro-audio");
        assert_eq!(s.batch_result_container_name, "truefluentpro-results");
        assert!(s.enable_recording);
        assert_eq!(s.recording_mp3_bitrate_kbps, 256);
        assert!(s.export_vtt_subtitles);
        assert!(!s.export_srt_subtitles);
    }

    #[test]
    fn test_recognition_settings_default() {
        let s = RecognitionSettings::default();
        assert!(s.filter_modal_particles);
        assert_eq!(s.max_history_items, 500);
        assert_eq!(s.realtime_max_length, 150);
        assert_eq!(s.chunk_duration_ms, 200);
        assert_eq!(s.initial_silence_timeout_seconds, 25);
        assert_eq!(s.end_silence_timeout_seconds, 1);
        assert_eq!(s.audio_activity_threshold, 600);
        assert!((s.audio_level_gain - 2.0).abs() < f64::EPSILON);
    }

    #[test]
    fn test_web_search_settings_default() {
        let s = WebSearchSettings::default();
        assert_eq!(s.provider_id, "duckduckgo");
        assert_eq!(s.trigger_mode, "auto");
        assert_eq!(s.max_results, 5);
        assert!(s.enable_intent_analysis);
        assert_eq!(s.mcp_tool_name, "web_search");
    }

    // T-006
    #[test]
    fn test_settings_serde_defaults_from_empty_json() {
        let media: MediaSettings = serde_json::from_str("{}").unwrap();
        assert_eq!(media.image_quality, "auto");
        assert_eq!(media.video_seconds, 5);

        let storage: StorageSettings = serde_json::from_str("{}").unwrap();
        assert!(storage.enable_recording);
        assert_eq!(storage.batch_audio_container_name, "truefluentpro-audio");

        let recog: RecognitionSettings = serde_json::from_str("{}").unwrap();
        assert_eq!(recog.chunk_duration_ms, 200);
        assert_eq!(recog.initial_silence_timeout_seconds, 25);

        let web: WebSearchSettings = serde_json::from_str("{}").unwrap();
        assert_eq!(web.provider_id, "duckduckgo");
        assert_eq!(web.max_results, 5);
    }

    // T-007
    #[test]
    fn test_video_api_mode_serde() {
        assert_eq!(serde_json::to_value(&VideoApiMode::SoraJobs).unwrap(), "sora_jobs");
        assert_eq!(serde_json::to_value(&VideoApiMode::Videos).unwrap(), "videos");
        let rt: VideoApiMode = serde_json::from_str("\"sora_jobs\"").unwrap();
        assert_eq!(rt, VideoApiMode::SoraJobs);
        let rt2: VideoApiMode = serde_json::from_str("\"videos\"").unwrap();
        assert_eq!(rt2, VideoApiMode::Videos);
    }

    #[test]
    fn test_audio_device_type_serde() {
        assert_eq!(serde_json::to_value(&AudioDeviceType::Input).unwrap(), "input");
        assert_eq!(serde_json::to_value(&AudioDeviceType::Output).unwrap(), "output");
        assert_eq!(serde_json::to_value(&AudioDeviceType::Loopback).unwrap(), "loopback");
        let rt: AudioDeviceType = serde_json::from_str("\"loopback\"").unwrap();
        assert_eq!(rt, AudioDeviceType::Loopback);
    }

    #[test]
    fn test_test_status_serde() {
        assert_eq!(serde_json::to_value(&TestStatus::Pending).unwrap(), "pending");
        assert_eq!(serde_json::to_value(&TestStatus::Running).unwrap(), "running");
        assert_eq!(serde_json::to_value(&TestStatus::Success).unwrap(), "success");
        assert_eq!(serde_json::to_value(&TestStatus::Failed).unwrap(), "failed");
        assert_eq!(serde_json::to_value(&TestStatus::Skipped).unwrap(), "skipped");
        let rt: TestStatus = serde_json::from_str("\"success\"").unwrap();
        assert_eq!(rt, TestStatus::Success);
    }

    #[test]
    fn test_task_status_serde() {
        assert_eq!(serde_json::to_value(&TaskStatus::Pending).unwrap(), "pending");
        assert_eq!(serde_json::to_value(&TaskStatus::Running).unwrap(), "running");
        assert_eq!(serde_json::to_value(&TaskStatus::Completed).unwrap(), "completed");
        assert_eq!(serde_json::to_value(&TaskStatus::Failed).unwrap(), "failed");
        assert_eq!(serde_json::to_value(&TaskStatus::Cancelled).unwrap(), "cancelled");
        let rt: TaskStatus = serde_json::from_str("\"cancelled\"").unwrap();
        assert_eq!(rt, TaskStatus::Cancelled);
    }

    #[test]
    fn test_model_capability_serde() {
        assert_eq!(serde_json::to_value(&ModelCapability::Text).unwrap(), "text");
        assert_eq!(serde_json::to_value(&ModelCapability::Image).unwrap(), "image");
        assert_eq!(serde_json::to_value(&ModelCapability::Video).unwrap(), "video");
        assert_eq!(serde_json::to_value(&ModelCapability::SpeechToText).unwrap(), "speech_to_text");
        assert_eq!(serde_json::to_value(&ModelCapability::TextToSpeech).unwrap(), "text_to_speech");
        let rt: ModelCapability = serde_json::from_str("\"speech_to_text\"").unwrap();
        assert_eq!(rt, ModelCapability::SpeechToText);
    }

    // ═══════ Batch-0 new tests (≥20) ═══════

    // ── T-001: config.rs typed enums serde round-trip ──

    #[test]
    fn test_b0_ai_provider_type_serde() {
        assert_eq!(serde_json::to_value(&AiProviderType::OpenAiCompatible).unwrap(), "open_ai_compatible");
        assert_eq!(serde_json::to_value(&AiProviderType::AzureOpenAi).unwrap(), "azure_open_ai");
        let rt: AiProviderType = serde_json::from_str("\"open_ai_compatible\"").unwrap();
        assert_eq!(rt, AiProviderType::OpenAiCompatible);
        let rt2: AiProviderType = serde_json::from_str("\"azure_open_ai\"").unwrap();
        assert_eq!(rt2, AiProviderType::AzureOpenAi);
    }

    #[test]
    fn test_b0_azure_auth_mode_serde() {
        assert_eq!(serde_json::to_value(&AzureAuthMode::ApiKey).unwrap(), "api_key");
        assert_eq!(serde_json::to_value(&AzureAuthMode::Aad).unwrap(), "aad");
        let rt: AzureAuthMode = serde_json::from_str("\"api_key\"").unwrap();
        assert_eq!(rt, AzureAuthMode::ApiKey);
        let rt2: AzureAuthMode = serde_json::from_str("\"aad\"").unwrap();
        assert_eq!(rt2, AzureAuthMode::Aad);
    }

    #[test]
    fn test_b0_api_key_header_mode_serde() {
        assert_eq!(serde_json::to_value(&ApiKeyHeaderMode::ApiKeyHeader).unwrap(), "api_key");
        assert_eq!(serde_json::to_value(&ApiKeyHeaderMode::Bearer).unwrap(), "bearer");
        assert_eq!(serde_json::to_value(&ApiKeyHeaderMode::Auto).unwrap(), "auto");
        let rt: ApiKeyHeaderMode = serde_json::from_str("\"api_key\"").unwrap();
        assert_eq!(rt, ApiKeyHeaderMode::ApiKeyHeader);
        let rt2: ApiKeyHeaderMode = serde_json::from_str("\"bearer\"").unwrap();
        assert_eq!(rt2, ApiKeyHeaderMode::Bearer);
    }

    #[test]
    fn test_b0_text_api_protocol_mode_serde() {
        assert_eq!(serde_json::to_value(&TextApiProtocolMode::Auto).unwrap(), "auto");
        assert_eq!(serde_json::to_value(&TextApiProtocolMode::ChatCompletionsV1).unwrap(), "chat_completions_v1");
        assert_eq!(serde_json::to_value(&TextApiProtocolMode::ChatCompletionsRaw).unwrap(), "chat_completions_raw");
        assert_eq!(serde_json::to_value(&TextApiProtocolMode::Responses).unwrap(), "responses");
        let rt: TextApiProtocolMode = serde_json::from_str("\"responses\"").unwrap();
        assert_eq!(rt, TextApiProtocolMode::Responses);
    }

    #[test]
    fn test_b0_image_api_route_mode_serde() {
        assert_eq!(serde_json::to_value(&ImageApiRouteMode::Auto).unwrap(), "auto");
        assert_eq!(serde_json::to_value(&ImageApiRouteMode::V1Images).unwrap(), "v1_images");
        assert_eq!(serde_json::to_value(&ImageApiRouteMode::ImagesRaw).unwrap(), "images_raw");
        let rt: ImageApiRouteMode = serde_json::from_str("\"v1_images\"").unwrap();
        assert_eq!(rt, ImageApiRouteMode::V1Images);
    }

    #[test]
    fn test_b0_image_edit_mode_serde() {
        assert_eq!(serde_json::to_value(&ImageEditMode::V1Multipart).unwrap(), "v1_multipart");
        assert_eq!(serde_json::to_value(&ImageEditMode::V2ResponsesApi).unwrap(), "v2_responses_api");
        let rt: ImageEditMode = serde_json::from_str("\"v1_multipart\"").unwrap();
        assert_eq!(rt, ImageEditMode::V1Multipart);
        // Default is V2ResponsesApi
        assert_eq!(ImageEditMode::default(), ImageEditMode::V2ResponsesApi);
    }

    #[test]
    fn test_b0_speech_capability_flag_serde() {
        assert_eq!(serde_json::to_value(&SpeechCapabilityFlag::RealtimeSpeechToText).unwrap(), "realtime_speech_to_text");
        assert_eq!(serde_json::to_value(&SpeechCapabilityFlag::BatchSpeechToText).unwrap(), "batch_speech_to_text");
        assert_eq!(serde_json::to_value(&SpeechCapabilityFlag::TextToSpeech).unwrap(), "text_to_speech");
        let rt: SpeechCapabilityFlag = serde_json::from_str("\"text_to_speech\"").unwrap();
        assert_eq!(rt, SpeechCapabilityFlag::TextToSpeech);
    }

    // ── T-005: enums.rs serde round-trip ──

    #[test]
    fn test_b0_processing_display_state_serde() {
        assert_eq!(serde_json::to_value(&ProcessingDisplayState::None).unwrap(), "none");
        assert_eq!(serde_json::to_value(&ProcessingDisplayState::Pending).unwrap(), "pending");
        assert_eq!(serde_json::to_value(&ProcessingDisplayState::Running).unwrap(), "running");
        assert_eq!(serde_json::to_value(&ProcessingDisplayState::Partial).unwrap(), "partial");
        assert_eq!(serde_json::to_value(&ProcessingDisplayState::Completed).unwrap(), "completed");
        assert_eq!(serde_json::to_value(&ProcessingDisplayState::Failed).unwrap(), "failed");
        assert_eq!(serde_json::to_value(&ProcessingDisplayState::Removed).unwrap(), "removed");
        let rt: ProcessingDisplayState = serde_json::from_str("\"partial\"").unwrap();
        assert_eq!(rt, ProcessingDisplayState::Partial);
    }

    #[test]
    fn test_b0_audio_source_mode_serde() {
        assert_eq!(serde_json::to_value(&AudioSourceMode::DefaultMic).unwrap(), "default_mic");
        assert_eq!(serde_json::to_value(&AudioSourceMode::CaptureDevice).unwrap(), "capture_device");
        assert_eq!(serde_json::to_value(&AudioSourceMode::Loopback).unwrap(), "loopback");
        let rt: AudioSourceMode = serde_json::from_str("\"loopback\"").unwrap();
        assert_eq!(rt, AudioSourceMode::Loopback);
    }

    #[test]
    fn test_b0_recording_mode_serde() {
        assert_eq!(serde_json::to_value(&RecordingMode::LoopbackOnly).unwrap(), "loopback_only");
        assert_eq!(serde_json::to_value(&RecordingMode::LoopbackWithMic).unwrap(), "loopback_with_mic");
        assert_eq!(serde_json::to_value(&RecordingMode::MicOnly).unwrap(), "mic_only");
        let rt: RecordingMode = serde_json::from_str("\"mic_only\"").unwrap();
        assert_eq!(rt, RecordingMode::MicOnly);
    }

    #[test]
    fn test_b0_service_mode_serde() {
        assert_eq!(serde_json::to_value(&ServiceMode::SelfHosted).unwrap(), "self_hosted");
        assert_eq!(serde_json::to_value(&ServiceMode::Cloud).unwrap(), "cloud");
        let rt: ServiceMode = serde_json::from_str("\"cloud\"").unwrap();
        assert_eq!(rt, ServiceMode::Cloud);
    }

    #[test]
    fn test_b0_batch_log_level_serde() {
        assert_eq!(serde_json::to_value(&BatchLogLevel::Off).unwrap(), "off");
        assert_eq!(serde_json::to_value(&BatchLogLevel::FailuresOnly).unwrap(), "failures_only");
        assert_eq!(serde_json::to_value(&BatchLogLevel::SuccessAndFailure).unwrap(), "success_and_failure");
        let rt: BatchLogLevel = serde_json::from_str("\"failures_only\"").unwrap();
        assert_eq!(rt, BatchLogLevel::FailuresOnly);
    }

    #[test]
    fn test_b0_transcription_api_mode_serde() {
        assert_eq!(serde_json::to_value(&TranscriptionApiMode::Batch).unwrap(), "batch");
        assert_eq!(serde_json::to_value(&TranscriptionApiMode::Fast).unwrap(), "fast");
        let rt: TranscriptionApiMode = serde_json::from_str("\"fast\"").unwrap();
        assert_eq!(rt, TranscriptionApiMode::Fast);
    }

    // ── T-006: cloud.rs serde tests ──

    #[test]
    fn test_b0_cloud_settings_default_from_empty() {
        let cs: CloudSettings = serde_json::from_str("{}").unwrap();
        assert_eq!(cs.mode, ServiceMode::SelfHosted);
        assert!(cs.backend_url.is_empty());
        assert!(cs.aad_tenant_id.is_empty());
    }

    #[test]
    fn test_b0_cloud_user_profile_roundtrip() {
        let profile = CloudUserProfile {
            user_id: "u1".into(),
            display_name: "Test User".into(),
            email: "user@example.com".into(),
            subscription: "pro".into(),
            is_admin: true,
            quotas: std::collections::HashMap::from([
                ("text".into(), QuotaInfo { used: 100, limit: 1000 }),
            ]),
        };
        let json = serde_json::to_string(&profile).unwrap();
        let restored: CloudUserProfile = serde_json::from_str(&json).unwrap();
        assert_eq!(restored.user_id, "u1");
        assert_eq!(restored.subscription, "pro");
        assert!(restored.is_admin);
        assert_eq!(restored.quotas["text"].remaining(), 900);
    }

    #[test]
    fn test_b0_quota_info_remaining() {
        let q = QuotaInfo { used: 75, limit: 100 };
        assert_eq!(q.remaining(), 25);
        let q2 = QuotaInfo { used: 100, limit: 100 };
        assert_eq!(q2.remaining(), 0);
        let q3 = QuotaInfo { used: 120, limit: 100 };
        assert_eq!(q3.remaining(), -20);
    }

    // ── T-007: common.rs model tests ──

    #[test]
    fn test_b0_subtitle_cue_display_text() {
        let cue = SubtitleCue { start_ms: 0, end_ms: 5000, text: "Hello world".into() };
        assert_eq!(cue.display_text(20), "Hello world");
        assert_eq!(cue.display_text(5), "Hello\u{2026}");
    }

    #[test]
    fn test_b0_subtitle_cue_range_text() {
        let cue = SubtitleCue { start_ms: 3661000, end_ms: 7322000, text: "x".into() };
        assert_eq!(cue.range_text(), "01:01:01 \u{2192} 02:02:02");
    }

    #[test]
    fn test_b0_model_option_display_string() {
        let opt = ModelOption {
            reference: ModelReference { endpoint_id: "ep1".into(), model_id: "m1".into() },
            endpoint_name: "Azure".into(),
            model_display_name: "GPT-4o".into(),
            endpoint_type: EndpointType::AzureOpenAi,
        };
        assert_eq!(opt.display_string(), "Azure / GPT-4o");
    }

    #[test]
    fn test_b0_azure_tenant_info_display_string() {
        let t1 = AzureTenantInfo { tenant_id: "tid-123".into(), display_name: "Contoso".into() };
        assert_eq!(t1.display_string(), "Contoso (tid-123)");
        let t2 = AzureTenantInfo { tenant_id: "tid-456".into(), display_name: String::new() };
        assert_eq!(t2.display_string(), "tid-456");
    }

    #[test]
    fn test_b0_review_sheet_preset_defaults() {
        let json = r#"{"name":"N","file_tag":"T","prompt":"P"}"#;
        let p: ReviewSheetPreset = serde_json::from_str(json).unwrap();
        assert!(p.include_in_batch);
        assert!(p.is_enabled);
    }

    // ── T-008: AppConfig cloud field ──

    #[test]
    fn test_b0_app_config_has_cloud_field() {
        let config = AppConfig::default();
        assert_eq!(config.cloud.mode, ServiceMode::SelfHosted);
        let json = serde_json::to_value(&config).unwrap();
        assert!(json.as_object().unwrap().contains_key("cloud"));
    }

    // ── Backward compat: empty JSON → defaults ──

    #[test]
    fn test_b0_ai_endpoint_from_minimal_json() {
        let json = r#"{"id":"x","name":"X","endpoint_type":"azure_open_ai","url":"https://e.com","api_key":"k","models":[],"enabled":true}"#;
        let ep: AiEndpoint = serde_json::from_str(json).unwrap();
        assert_eq!(ep.auth_header_mode, ApiKeyHeaderMode::ApiKeyHeader);
        assert_eq!(ep.auth_mode, AzureAuthMode::ApiKey);
        assert_eq!(ep.provider_type, AiProviderType::OpenAiCompatible);
        assert_eq!(ep.text_api_protocol_mode, TextApiProtocolMode::Auto);
        assert_eq!(ep.image_api_route_mode, ImageApiRouteMode::Auto);
        assert!(ep.speech_capabilities.is_empty());
        assert!(ep.profile_id.is_empty());
    }

    // ── T-004: new MediaSettings fields defaults ──

    #[test]
    fn test_b0_media_settings_new_fields_defaults() {
        let ms: MediaSettings = serde_json::from_str("{}").unwrap();
        assert_eq!(ms.image_model_name, "gpt-image-1");
        assert_eq!(ms.video_model_name, "sora-2");
        assert_eq!(ms.image_edit_mode, ImageEditMode::V2ResponsesApi);
        assert_eq!(ms.input_fidelity, "auto");
        assert!(ms.enable_chat_image_generation);
        assert_eq!(ms.video_width, 1280);
        assert_eq!(ms.video_height, 720);
        assert!(!ms.default_enable_studio_reasoning);
        assert!(!ms.default_enable_studio_web_search);
    }
}
