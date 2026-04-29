//! Integration test for config export/import round-trip.
//!
//! Run with: cargo test -p tfp-storage --test config_roundtrip

use tfp_core::{AiEndpoint, AppConfig, EndpointType, ApiKeyHeaderMode};
use tfp_storage::Database;

#[tokio::test]
async fn test_config_export_import_roundtrip() {
    // 1. Create in-memory DB
    let db = Database::open_in_memory().unwrap();

    // 2. Build config with 2 endpoints
    let config = AppConfig {
        endpoints: vec![
            AiEndpoint {
                id: "ep-azure-001".into(),
                name: "Azure OpenAI Production".into(),
                endpoint_type: EndpointType::AzureOpenAi,
                url: "https://my-azure.openai.azure.com".into(),
                api_key: "sk-test-key-azure-12345".into(),
                enabled: true,
                api_version: Some("2025-04-01-preview".into()),
                auth_header_mode: ApiKeyHeaderMode::Auto,
                speech_region: "eastus".into(),
                ..AiEndpoint::default()
            },
            AiEndpoint {
                id: "ep-apim-002".into(),
                name: "APIM Gateway".into(),
                endpoint_type: EndpointType::ApiManagementGateway,
                url: "https://apim.azure-api.net/openai".into(),
                api_key: "apim-subscription-key-67890".into(),
                enabled: true,
                api_version: Some("2025-03-01-preview".into()),
                auth_header_mode: ApiKeyHeaderMode::ApiKeyHeader,
                ..AiEndpoint::default()
            },
        ],
        default_source_lang: "zh-Hans".into(),
        default_target_langs: vec!["en".into(), "ja".into()],
        ..AppConfig::default()
    };

    // 3. Save config to DB
    db.save_config(&config).await.unwrap();

    // 4. Load config back
    let loaded = db.load_config().await.unwrap();

    // 5. Verify round-trip integrity
    assert_eq!(loaded.endpoints.len(), 2, "Should have 2 endpoints after round-trip");
    assert_eq!(loaded.endpoints[0].id, "ep-azure-001");
    assert_eq!(loaded.endpoints[0].name, "Azure OpenAI Production");
    assert_eq!(loaded.endpoints[0].url, "https://my-azure.openai.azure.com");
    assert_eq!(loaded.endpoints[0].endpoint_type, EndpointType::AzureOpenAi);
    assert!(loaded.endpoints[0].enabled);

    assert_eq!(loaded.endpoints[1].id, "ep-apim-002");
    assert_eq!(loaded.endpoints[1].name, "APIM Gateway");
    assert_eq!(loaded.endpoints[1].endpoint_type, EndpointType::ApiManagementGateway);
    assert_eq!(loaded.endpoints[1].auth_header_mode, ApiKeyHeaderMode::ApiKeyHeader);

    assert_eq!(loaded.default_source_lang, "zh-Hans");
    assert_eq!(loaded.default_target_langs, vec!["en", "ja"]);

    // 6. Export as JSON and re-import on fresh DB
    let exported_json = serde_json::to_string(&loaded).unwrap();
    assert!(!exported_json.is_empty());

    let db2 = Database::open_in_memory().unwrap();
    let reimported: AppConfig = serde_json::from_str(&exported_json).unwrap();
    db2.save_config(&reimported).await.unwrap();

    let final_config = db2.load_config().await.unwrap();
    assert_eq!(final_config.endpoints.len(), 2);
    assert_eq!(final_config.endpoints[0].id, config.endpoints[0].id);
    assert_eq!(final_config.endpoints[1].id, config.endpoints[1].id);
    assert_eq!(final_config.default_target_langs, config.default_target_langs);
}

#[tokio::test]
async fn test_config_default_on_empty_db() {
    let db = Database::open_in_memory().unwrap();
    let config = db.load_config().await.unwrap();

    // Default config should be valid but empty
    assert!(config.endpoints.is_empty());
    assert_eq!(config.default_source_lang, "zh-Hans");
}

#[tokio::test]
async fn test_config_overwrite() {
    let db = Database::open_in_memory().unwrap();

    let config1 = AppConfig {
        endpoints: vec![AiEndpoint {
            id: "first".into(),
            name: "First".into(),
            enabled: true,
            ..AiEndpoint::default()
        }],
        ..AppConfig::default()
    };
    db.save_config(&config1).await.unwrap();

    let config2 = AppConfig {
        endpoints: vec![
            AiEndpoint { id: "second".into(), name: "Second".into(), enabled: true, ..AiEndpoint::default() },
            AiEndpoint { id: "third".into(), name: "Third".into(), enabled: false, ..AiEndpoint::default() },
        ],
        ..AppConfig::default()
    };
    db.save_config(&config2).await.unwrap();

    let loaded = db.load_config().await.unwrap();
    assert_eq!(loaded.endpoints.len(), 2);
    assert_eq!(loaded.endpoints[0].id, "second");
    assert_eq!(loaded.endpoints[1].id, "third");
    assert!(!loaded.endpoints[1].enabled);
}
