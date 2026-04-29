//! Integration test for AI chat completion via live Azure OpenAI endpoint.
//!
//! Run with: cargo test -p tfp-providers --test chat_integration -- --ignored

use tfp_core::{AiEndpoint, EndpointType, CompletionRequest, ChatMessage};
use tfp_providers::{OpenAiChatProvider, AiCompletionSlot};

mod helpers;

#[tokio::test]
#[ignore] // requires API key — run with: cargo test -p tfp-providers --test chat_integration -- --ignored
async fn test_chat_completion_live() {
    let endpoints = helpers::load_rust2_endpoints();
    let ep = endpoints
        .iter()
        .find(|e| {
            e.endpoint_type == "azure_open_ai"
                || e.endpoint_type == "api_management_gateway"
                || e.endpoint_type == "open_ai_compatible"
        })
        .expect("no AI endpoint found — ensure Rust2 DB exists with configured endpoints");

    // Find a text model from the endpoint's model list
    let models: Vec<String> = ep.extra.get("models")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|m| m.get("model_id").and_then(|id| id.as_str()).map(|s| s.to_string()))
                .collect()
        })
        .unwrap_or_default();

    let model = models.first()
        .cloned()
        .unwrap_or_else(|| "gpt-4o".to_string());

    let ai_endpoint = AiEndpoint {
        id: ep.id.clone(),
        name: ep.name.clone(),
        endpoint_type: match ep.endpoint_type.as_str() {
            "azure_open_ai" => EndpointType::AzureOpenAi,
            "api_management_gateway" => EndpointType::ApiManagementGateway,
            _ => EndpointType::OpenAiCompatible,
        },
        url: ep.url.clone(),
        api_key: ep.api_key.clone(),
        api_version: ep.extra.get("api_version").and_then(|v| v.as_str()).map(|s| s.to_string()),
        enabled: true,
        ..AiEndpoint::default()
    };

    let provider = OpenAiChatProvider::new(ai_endpoint);

    let request = CompletionRequest {
        messages: vec![ChatMessage {
            role: "user".into(),
            content: serde_json::Value::String("Reply with exactly: HELLO_WORLD".into()),
        }],
        model,
        max_tokens: Some(50),
        temperature: None,
        endpoint_id: ep.id.clone(),
        reasoning_effort: None,
        enable_image_generation: false,
        image_model_deployment: None,
        image_size: None,
        image_quality: None,
    };

    let result = provider.complete(&request).await;
    match result {
        Ok(response) => {
            assert!(
                !response.content.is_empty(),
                "Expected non-empty response content"
            );
            println!("✅ Chat completion successful: \"{}\"", &response.content[..response.content.len().min(80)]);
        }
        Err(e) => {
            let msg = format!("{e}");
            if msg.contains("429") || msg.contains("quota") || msg.contains("404") {
                println!("⚠️ Chat completion skipped (quota/deployment): {msg}");
            } else {
                panic!("Chat completion failed unexpectedly: {e}");
            }
        }
    }
}
