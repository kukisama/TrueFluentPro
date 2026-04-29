//! Blob Storage Service — Azure Storage REST API for container management, file upload, and SAS generation.

use base64::{engine::general_purpose::STANDARD as B64, Engine};
use chrono::Utc;
use hmac::{Hmac, Mac};
use reqwest::Client;
use sha2::Sha256;
use std::path::Path;
use tfp_core::ProviderError;

type HmacSha256 = Hmac<Sha256>;

/// Parsed Azure Storage connection string components.
#[derive(Debug, Clone)]
pub struct StorageAccount {
    pub account_name: String,
    pub account_key: String,
    pub endpoint_suffix: String,
}

/// Parse an Azure Storage connection string into account components.
pub fn parse_connection_string(conn_str: &str) -> Result<StorageAccount, ProviderError> {
    let mut account_name = None;
    let mut account_key = None;
    let mut endpoint_suffix = "core.windows.net".to_string();

    for part in conn_str.split(';') {
        let part = part.trim();
        if part.is_empty() {
            continue;
        }
        if let Some((k, v)) = part.split_once('=') {
            match k.trim() {
                "AccountName" => account_name = Some(v.trim().to_string()),
                "AccountKey" => {
                    // AccountKey may contain '=' in base64; rejoin remaining
                    let idx = part.find('=').unwrap() + 1;
                    account_key = Some(part[idx..].trim().to_string());
                }
                "EndpointSuffix" => endpoint_suffix = v.trim().to_string(),
                _ => {}
            }
        }
    }

    let account_name = account_name
        .ok_or_else(|| ProviderError::Internal("Missing AccountName in connection string".into()))?;
    let account_key = account_key
        .ok_or_else(|| ProviderError::Internal("Missing AccountKey in connection string".into()))?;

    Ok(StorageAccount {
        account_name,
        account_key,
        endpoint_suffix,
    })
}

/// Normalize a string to a valid Azure Blob container name (lowercase, alnum + dash, 3-63 chars).
pub fn normalize_container_name(name: &str, fallback: &str) -> String {
    let normalized: String = name
        .to_lowercase()
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() { c } else { '-' })
        .collect();

    // Trim leading/trailing dashes and collapse consecutive dashes
    let mut result = String::new();
    let mut prev_dash = true; // treat start as if preceded by dash to trim leading
    for c in normalized.chars() {
        if c == '-' {
            if !prev_dash {
                result.push(c);
            }
            prev_dash = true;
        } else {
            result.push(c);
            prev_dash = false;
        }
    }

    // Trim trailing dashes
    while result.ends_with('-') {
        result.pop();
    }

    // Enforce 3-63 char length
    if result.len() < 3 {
        result = fallback.to_string();
    }
    if result.len() > 63 {
        result.truncate(63);
        while result.ends_with('-') {
            result.pop();
        }
    }

    result
}

/// Get the audio content type based on file extension.
pub fn get_audio_content_type(path: &str) -> &'static str {
    match Path::new(path)
        .extension()
        .and_then(|e| e.to_str())
        .map(|e| e.to_lowercase())
        .as_deref()
    {
        Some("wav") => "audio/wav",
        Some("mp3") => "audio/mpeg",
        Some("ogg") => "audio/ogg",
        Some("flac") => "audio/flac",
        Some("m4a") => "audio/mp4",
        Some("aac") => "audio/aac",
        Some("wma") => "audio/x-ms-wma",
        Some("webm") => "audio/webm",
        _ => "application/octet-stream",
    }
}

/// Blob storage operations using Azure Storage REST API.
pub struct BlobStorageService;

impl BlobStorageService {
    /// Create a container if it doesn't already exist.
    pub async fn get_or_create_container(
        conn_str: &str,
        container_name: &str,
    ) -> Result<(), ProviderError> {
        let acct = parse_connection_string(conn_str)?;
        let container = normalize_container_name(container_name, "truefluentpro-audio");
        let url = format!(
            "https://{}.blob.{}/{}?restype=container",
            acct.account_name, acct.endpoint_suffix, container,
        );

        let date = Utc::now().format("%a, %d %b %Y %H:%M:%S GMT").to_string();
        let string_to_sign = format!(
            "PUT\n\n\n\n\n\n\n\n\n\n\n\nx-ms-blob-type:BlockBlob\nx-ms-date:{}\nx-ms-version:2020-10-02\n/{}/{}\nrestype:container",
            date, acct.account_name, container,
        );
        let auth = sign_request(&acct.account_name, &acct.account_key, &string_to_sign)?;

        let client = Client::new();
        let resp = client
            .put(&url)
            .header("x-ms-date", &date)
            .header("x-ms-version", "2020-10-02")
            .header("x-ms-blob-type", "BlockBlob")
            .header("Authorization", &auth)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        let status = resp.status().as_u16();
        // 201 = Created, 409 = Conflict (already exists)
        if status == 201 || status == 409 {
            Ok(())
        } else {
            let body = resp.text().await.unwrap_or_default();
            Err(ProviderError::Internal(format!(
                "Create container failed ({}): {}",
                status, body
            )))
        }
    }

    /// Upload an audio file to blob storage. Returns the blob name.
    pub async fn upload_audio(
        conn_str: &str,
        container_name: &str,
        audio_path: &str,
    ) -> Result<String, ProviderError> {
        let acct = parse_connection_string(conn_str)?;
        let container = normalize_container_name(container_name, "truefluentpro-audio");

        let file_name = Path::new(audio_path)
            .file_name()
            .and_then(|n| n.to_str())
            .unwrap_or("audio.wav");

        let blob_name = format!("{}/{}", uuid::Uuid::new_v4(), file_name);
        let content_type = get_audio_content_type(audio_path);

        let body = tokio::fs::read(audio_path)
            .await
            .map_err(|e| ProviderError::Internal(format!("Read audio file failed: {}", e)))?;

        let content_length = body.len();
        let url = format!(
            "https://{}.blob.{}/{}/{}",
            acct.account_name, acct.endpoint_suffix, container, blob_name,
        );

        let date = Utc::now().format("%a, %d %b %Y %H:%M:%S GMT").to_string();
        let string_to_sign = format!(
            "PUT\n\n\n{}\n\n{}\n\n\n\n\n\n\nx-ms-blob-type:BlockBlob\nx-ms-date:{}\nx-ms-version:2020-10-02\n/{}/{}/{}",
            content_length, content_type, date, acct.account_name, container, blob_name,
        );
        let auth = sign_request(&acct.account_name, &acct.account_key, &string_to_sign)?;

        let client = Client::new();
        let resp = client
            .put(&url)
            .header("x-ms-date", &date)
            .header("x-ms-version", "2020-10-02")
            .header("x-ms-blob-type", "BlockBlob")
            .header("Content-Type", content_type)
            .header("Content-Length", content_length.to_string())
            .header("Authorization", &auth)
            .body(body)
            .send()
            .await
            .map_err(|e| ProviderError::Network(e.to_string()))?;

        let status = resp.status().as_u16();
        if status == 201 {
            Ok(blob_name)
        } else {
            let resp_body = resp.text().await.unwrap_or_default();
            Err(ProviderError::Internal(format!(
                "Upload blob failed ({}): {}",
                status, resp_body
            )))
        }
    }

    /// Generate a read-only SAS URL for a blob, valid for the specified duration.
    pub async fn create_blob_read_sas(
        conn_str: &str,
        container_name: &str,
        blob_name: &str,
        valid_secs: u64,
    ) -> Result<String, ProviderError> {
        let acct = parse_connection_string(conn_str)?;
        let container = normalize_container_name(container_name, "truefluentpro-audio");

        let start = Utc::now() - chrono::Duration::minutes(5);
        let expiry = Utc::now() + chrono::Duration::seconds(valid_secs as i64);

        let st = start.format("%Y-%m-%dT%H:%M:%SZ").to_string();
        let se = expiry.format("%Y-%m-%dT%H:%M:%SZ").to_string();
        let sp = "r"; // read
        let sr = "b"; // blob
        let sv = "2020-10-02";

        let string_to_sign = format!(
            "{sp}\n{st}\n{se}\n/blob/{account}/{container}/{blob}\n\n\n{sv}\n\n\n\n\n",
            sp = sp,
            st = st,
            se = se,
            account = acct.account_name,
            container = container,
            blob = blob_name,
            sv = sv,
        );

        let key_bytes = B64
            .decode(&acct.account_key)
            .map_err(|e| ProviderError::Internal(format!("Decode account key: {}", e)))?;
        let mut mac = HmacSha256::new_from_slice(&key_bytes)
            .map_err(|e| ProviderError::Internal(format!("HMAC init: {}", e)))?;
        mac.update(string_to_sign.as_bytes());
        let sig = B64.encode(mac.finalize().into_bytes());

        let encoded_sig = urlencoding::encode(&sig);
        let sas_url = format!(
            "https://{}.blob.{}/{}/{}?sp={}&st={}&se={}&sv={}&sr={}&sig={}",
            acct.account_name,
            acct.endpoint_suffix,
            container,
            blob_name,
            sp,
            urlencoding::encode(&st),
            urlencoding::encode(&se),
            sv,
            sr,
            encoded_sig,
        );

        Ok(sas_url)
    }
}

/// Sign an Azure Storage REST request using SharedKey scheme.
fn sign_request(
    account_name: &str,
    account_key: &str,
    string_to_sign: &str,
) -> Result<String, ProviderError> {
    let key_bytes = B64
        .decode(account_key)
        .map_err(|e| ProviderError::Internal(format!("Decode account key: {}", e)))?;
    let mut mac = HmacSha256::new_from_slice(&key_bytes)
        .map_err(|e| ProviderError::Internal(format!("HMAC init: {}", e)))?;
    mac.update(string_to_sign.as_bytes());
    let sig = B64.encode(mac.finalize().into_bytes());
    Ok(format!("SharedKey {}:{}", account_name, sig))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalize_container_name_basic() {
        assert_eq!(normalize_container_name("My-Container_1", "fb"), "my-container-1");
    }

    #[test]
    fn normalize_container_name_strips_leading_trailing_dashes() {
        assert_eq!(normalize_container_name("---hello---", "fb"), "hello");
    }

    #[test]
    fn normalize_container_name_short_falls_back() {
        assert_eq!(normalize_container_name("ab", "fallback"), "fallback");
    }

    #[test]
    fn normalize_container_name_long_truncates() {
        let long_name = "a".repeat(100);
        let result = normalize_container_name(&long_name, "fb");
        assert!(result.len() <= 63);
    }

    #[test]
    fn normalize_container_name_collapses_consecutive_dashes() {
        assert_eq!(normalize_container_name("a--b---c", "fb"), "a-b-c");
    }

    #[test]
    fn normalize_container_name_special_chars() {
        assert_eq!(normalize_container_name("Hello World! @#$", "fb"), "hello-world");
    }

    #[test]
    fn get_audio_content_type_returns_correct_mime() {
        assert_eq!(get_audio_content_type("test.wav"), "audio/wav");
        assert_eq!(get_audio_content_type("test.mp3"), "audio/mpeg");
        assert_eq!(get_audio_content_type("test.ogg"), "audio/ogg");
        assert_eq!(get_audio_content_type("test.flac"), "audio/flac");
        assert_eq!(get_audio_content_type("test.m4a"), "audio/mp4");
        assert_eq!(get_audio_content_type("test.aac"), "audio/aac");
        assert_eq!(get_audio_content_type("test.wma"), "audio/x-ms-wma");
        assert_eq!(get_audio_content_type("test.webm"), "audio/webm");
        assert_eq!(get_audio_content_type("test.xyz"), "application/octet-stream");
        assert_eq!(get_audio_content_type("noext"), "application/octet-stream");
    }

    #[test]
    fn parse_connection_string_valid() {
        let cs = "DefaultEndpointsProtocol=https;AccountName=myacc;AccountKey=abc123==;EndpointSuffix=core.windows.net";
        let acct = parse_connection_string(cs).unwrap();
        assert_eq!(acct.account_name, "myacc");
        assert_eq!(acct.account_key, "abc123==");
        assert_eq!(acct.endpoint_suffix, "core.windows.net");
    }

    #[test]
    fn parse_connection_string_missing_name() {
        let cs = "AccountKey=abc123==";
        assert!(parse_connection_string(cs).is_err());
    }

    #[test]
    fn parse_connection_string_missing_key() {
        let cs = "AccountName=myacc";
        assert!(parse_connection_string(cs).is_err());
    }

    #[test]
    fn parse_connection_string_default_suffix() {
        let cs = "AccountName=test;AccountKey=key123==";
        let acct = parse_connection_string(cs).unwrap();
        assert_eq!(acct.endpoint_suffix, "core.windows.net");
    }
}
