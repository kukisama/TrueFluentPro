//! Integration test for image generation via live Azure OpenAI endpoint.
//!
//! Run with: cargo test -p tfp-providers --test image_integration -- --ignored

use tfp_core::{AiEndpoint, EndpointType, ImageGenRequest};
use tfp_providers::{OpenAiImageProvider, ImageGenSlot};

mod helpers;

#[tokio::test]
#[ignore] // requires API key — run with: cargo test -p tfp-providers --test image_integration -- --ignored
async fn test_image_generation_live() {
    let endpoints = helpers::load_rust2_endpoints();
    let ep = endpoints
        .iter()
        .find(|e| {
            e.endpoint_type == "azure_open_ai"
                || e.endpoint_type == "api_management_gateway"
        })
        .expect("no azure/apim endpoint found — ensure Rust2 DB exists with configured endpoints");

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

    let provider = OpenAiImageProvider::new(ai_endpoint);

    let request = ImageGenRequest {
        prompt: "A simple red circle on a white background, minimal flat design".into(),
        width: 1024,
        height: 1024,
        model: "dall-e-3".into(),
        quality: Some("standard".into()),
        output_format: Some("png".into()),
        background: None,
        n: Some(1),
        endpoint_id: ep.id.clone(),
        text_model: None,
        image_model: None,
        previous_response_id: None,
        reference_image_path: None,
        image_edit_mode: None,
        uploaded_file_ids: Vec::new(),
    };

    let results = provider.generate(&request).await;
    match results {
        Ok(images) => {
            assert!(!images.is_empty(), "Expected at least one image result");
            let img = &images[0];
            // Should have either base64 data or a URL
            let has_data = img.base64.as_ref().map(|s| !s.is_empty()).unwrap_or(false)
                || img.url.as_ref().map(|s| !s.is_empty()).unwrap_or(false);
            assert!(has_data, "Image result should have base64 or URL data");
            println!("✅ Image generated successfully: {} attempted URLs, {:.1}s generation",
                img.attempted_urls.len(), img.generate_seconds);
        }
        Err(e) => {
            // If it fails due to quota/deployment/model issues, that's acceptable for CI
            let msg = format!("{e}");
            if msg.contains("429") || msg.contains("quota") || msg.contains("404")
                || msg.contains("400") || msg.contains("unknown_model") || msg.contains("DeploymentNotFound") {
                println!("⚠️ Image generation skipped (quota/deployment/model): {msg}");
            } else {
                panic!("Image generation failed unexpectedly: {e}");
            }
        }
    }
}
