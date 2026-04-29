use serde::{Deserialize, Serialize};
use tauri::{Emitter, Manager};
use base64::Engine;
use sha2::{Sha256, Digest};
use tokio::net::TcpListener;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

use crate::state::AppState;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  AAD (Microsoft Entra ID) browser interactive login
//
//  Flow (aligned with C# AzureTokenProvider.LoginAutoAsync):
//  1. Generate PKCE code_verifier + code_challenge
//  2. Start a one-shot HTTP listener on localhost:random_port
//  3. Open system browser to Azure authorization URL
//  4. User completes login in browser, Azure redirects to localhost:{port}/?code=xxx
//  5. Exchange code for access_token + refresh_token
//  6. If tenantId is empty → use token to query ARM /tenants for tenant discovery
//     - Single tenant: auto switch to that tenant's token
//     - Multi tenant: emit event for frontend to select
//  7. Save token to endpoint config, notify frontend
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// Info returned to frontend at the start of browser login
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DeviceCodeResponse {
    pub user_code: String,
    pub verification_uri: String,
    pub message: String,
    pub expires_in: u64,
    pub interval: u64,
}

/// Token info after successful acquisition
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AadTokenResult {
    pub access_token: String,
    pub token_type: String,
    pub expires_in: u64,
    pub scope: String,
}

/// Tenant info (aligned with C# AzureTenantInfo)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AzureTenantInfo {
    pub tenant_id: String,
    pub display_name: String,
    pub default_domain: String,
}

/// Azure AD Token response (internal)
#[derive(Debug, Deserialize)]
struct AzureTokenResponse {
    access_token: Option<String>,
    refresh_token: Option<String>,
    token_type: Option<String>,
    expires_in: Option<u64>,
    scope: Option<String>,
    error: Option<String>,
    error_description: Option<String>,
}

const DEFAULT_SCOPE: &str = "https://cognitiveservices.azure.com/.default offline_access";
const ARM_SCOPE: &str = "https://management.azure.com/.default offline_access";
/// Azure CLI well-known client ID (same as Azure.Identity SDK DeveloperSignOnClientId)
const DEFAULT_CLIENT_ID: &str = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

/// Generate PKCE code_verifier (43-128 char URL-safe random string)
fn generate_code_verifier() -> String {
    let bytes: [u8; 32] = uuid::Uuid::new_v4().as_bytes()
        .iter()
        .chain(uuid::Uuid::new_v4().as_bytes().iter())
        .copied()
        .take(32)
        .collect::<Vec<u8>>()
        .try_into()
        .unwrap();
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(bytes)
}

/// Compute code_challenge from code_verifier (S256)
fn compute_code_challenge(verifier: &str) -> String {
    let hash = Sha256::digest(verifier.as_bytes());
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(hash)
}

/// Execute a full OAuth Authorization Code Flow + PKCE, return token response
async fn do_oauth_flow(
    tenant: &str,
    client_id: &str,
    scope: &str,
) -> Result<(AzureTokenResponse, String), String> {
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .map_err(|e| format!("无法绑定本地端口: {e}"))?;
    let local_port = listener.local_addr().map_err(|e| format!("{e}"))?.port();
    let redirect_uri = format!("http://localhost:{local_port}");

    // PKCE
    let code_verifier = generate_code_verifier();
    let code_challenge = compute_code_challenge(&code_verifier);

    let auth_url = format!(
        "https://login.microsoftonline.com/{}/oauth2/v2.0/authorize?\
         client_id={}&response_type=code&redirect_uri={}&scope={}\
         &code_challenge={}&code_challenge_method=S256&prompt=select_account\
         &x-client-SKU=msal.rust&x-client-Ver=1.0.0",
        tenant,
        urlencoding::encode(client_id),
        urlencoding::encode(&redirect_uri),
        urlencoding::encode(scope),
        urlencoding::encode(&code_challenge),
    );

    let _ = open::that(&auth_url);

    // Wait for callback (120s timeout)
    let auth_code = match tokio::time::timeout(
        std::time::Duration::from_secs(120),
        wait_for_callback(listener),
    ).await {
        Ok(Ok(code)) => code,
        Ok(Err(e)) => return Err(format!("回调处理失败: {e}")),
        Err(_) => return Err("登录超时（120秒），请重试".into()),
    };

    // Exchange code for token
    let token_url = format!(
        "https://login.microsoftonline.com/{}/oauth2/v2.0/token",
        tenant
    );
    let http = reqwest::Client::new();
    let resp = http
        .post(&token_url)
        .form(&[
            ("client_id", client_id),
            ("grant_type", "authorization_code"),
            ("code", auth_code.as_str()),
            ("redirect_uri", redirect_uri.as_str()),
            ("code_verifier", code_verifier.as_str()),
            ("scope", scope),
        ])
        .send()
        .await
        .map_err(|e| format!("请求 token 失败: {e}"))?;

    let body: AzureTokenResponse = resp.json().await
        .map_err(|e| format!("解析 token 响应失败: {e}"))?;

    if let Some(ref error) = body.error {
        let desc = body.error_description.as_deref().unwrap_or("");
        return Err(format!("{error}: {desc}"));
    }

    if body.access_token.is_none() {
        return Err("Azure 返回空 token".into());
    }

    Ok((body, redirect_uri))
}

/// Refresh access_token using refresh_token for a specific tenant
async fn refresh_token_for_tenant(
    tenant: &str,
    client_id: &str,
    refresh_token: &str,
    scope: &str,
) -> Result<AzureTokenResponse, String> {
    let token_url = format!(
        "https://login.microsoftonline.com/{}/oauth2/v2.0/token",
        tenant
    );
    let http = reqwest::Client::new();
    let resp = http
        .post(&token_url)
        .form(&[
            ("client_id", client_id),
            ("grant_type", "refresh_token"),
            ("refresh_token", refresh_token),
            ("scope", scope),
        ])
        .send()
        .await
        .map_err(|e| format!("刷新 token 失败: {e}"))?;

    let body: AzureTokenResponse = resp.json().await
        .map_err(|e| format!("解析刷新 token 响应失败: {e}"))?;

    if let Some(ref error) = body.error {
        let desc = body.error_description.as_deref().unwrap_or("");
        return Err(format!("{error}: {desc}"));
    }

    if body.access_token.is_none() {
        return Err("刷新 token 时 Azure 返回空 access_token".into());
    }

    Ok(body)
}

/// Query ARM tenants API for tenant list accessible by current account
async fn discover_tenants(access_token: &str) -> Result<Vec<AzureTenantInfo>, String> {
    let http = reqwest::Client::new();
    let resp = http
        .get("https://management.azure.com/tenants?api-version=2022-12-01")
        .header("Authorization", format!("Bearer {access_token}"))
        .send()
        .await
        .map_err(|e| format!("查询租户列表失败: {e}"))?;

    if !resp.status().is_success() {
        let status = resp.status();
        let body = resp.text().await.unwrap_or_default();
        return Err(format!("ARM tenants API 返回 {status}: {body}"));
    }

    let json: serde_json::Value = resp.json().await
        .map_err(|e| format!("解析租户列表响应失败: {e}"))?;

    let tenants = json.get("value")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter().filter_map(|item| {
                let tid = item.get("tenantId")?.as_str()?.to_string();
                let name = item.get("displayName")
                    .and_then(|n| n.as_str())
                    .filter(|s| !s.is_empty())
                    .or_else(|| item.get("defaultDomain").and_then(|d| d.as_str()))
                    .unwrap_or("")
                    .to_string();
                let domain = item.get("defaultDomain")
                    .and_then(|d| d.as_str())
                    .unwrap_or("")
                    .to_string();
                Some(AzureTenantInfo { tenant_id: tid, display_name: name, default_domain: domain })
            }).collect()
        })
        .unwrap_or_default();

    Ok(tenants)
}

/// Save token to endpoint config and emit success event
async fn save_token_and_notify(
    app: &tauri::AppHandle,
    endpoint_id: &str,
    access_token: &str,
    refresh_token: Option<&str>,
    tenant_id: Option<&str>,
    body: &AzureTokenResponse,
) {
    let state_ref: tauri::State<'_, AppState> = app.state();
    {
        let mut config = state_ref.config.write().await;
        if let Some(ep) = config.endpoints.iter_mut().find(|e| e.id == endpoint_id) {
            ep.api_key = access_token.to_string();
            ep.auth_mode = tfp_core::AzureAuthMode::Aad;
            ep.auth_header_mode = tfp_core::ApiKeyHeaderMode::Bearer;
            if let Some(tid) = tenant_id {
                if !tid.is_empty() {
                    ep.azure_tenant_id = tid.to_string();
                }
            }
        }
    }
    let _ = state_ref.persist_config().await;

    if let Some(rt) = refresh_token {
        let mut tokens = state_ref.refresh_tokens.write().await;
        tokens.insert(endpoint_id.to_string(), rt.to_string());
        drop(tokens);
        let _ = state_ref.persist_refresh_tokens().await;
    }

    // Re-register providers so new AAD token takes effect for all capabilities
    {
        let config = state_ref.config.read().await;
        let endpoints = config.endpoints.clone();
        drop(config);
        crate::register_providers_async(&state_ref, &endpoints).await;
    }

    let token_result = AadTokenResult {
        access_token: access_token.to_string(),
        token_type: body.token_type.clone().unwrap_or_else(|| "Bearer".into()),
        expires_in: body.expires_in.unwrap_or(3600),
        scope: body.scope.clone().unwrap_or_default(),
    };

    let _ = app.emit("aad-auth-result", serde_json::json!({
        "endpoint_id": endpoint_id,
        "success": true,
        "token": token_result,
    }));
}

/// Start browser interactive login (Authorization Code Flow + PKCE)
#[tauri::command]
pub async fn aad_start_device_code_flow(
    app: tauri::AppHandle,
    endpoint_id: String,
    tenant_id: String,
    client_id: String,
    scope: Option<String>,
) -> Result<DeviceCodeResponse, String> {
    let scope = scope.unwrap_or_else(|| DEFAULT_SCOPE.to_string());
    let client_id = if client_id.is_empty() { DEFAULT_CLIENT_ID.to_string() } else { client_id };
    let tenant = if tenant_id.is_empty() { "common".to_string() } else { tenant_id.clone() };
    let need_tenant_discovery = tenant_id.is_empty();

    let app_clone = app.clone();
    let endpoint_id_clone = endpoint_id.clone();

    tokio::spawn(async move {
        // Phase 1: OAuth login
        let (body, _redirect_uri) = match do_oauth_flow(&tenant, &client_id, &scope).await {
            Ok(r) => r,
            Err(e) => {
                let _ = app_clone.emit("aad-auth-result", serde_json::json!({
                    "endpoint_id": endpoint_id_clone,
                    "success": false,
                    "error": e,
                }));
                return;
            }
        };

        let access_token = body.access_token.clone().unwrap(); // validated in do_oauth_flow
        let refresh_token_val = body.refresh_token.clone();

        if !need_tenant_discovery {
            save_token_and_notify(
                &app_clone,
                &endpoint_id_clone,
                &access_token,
                refresh_token_val.as_deref(),
                Some(&tenant),
                &body,
            ).await;
            return;
        }

        // Phase 2: Tenant discovery (tenantId is empty)
        let refresh_token_str = match &refresh_token_val {
            Some(rt) => rt.clone(),
            None => {
                save_token_and_notify(
                    &app_clone,
                    &endpoint_id_clone,
                    &access_token,
                    None,
                    None,
                    &body,
                ).await;
                let _ = app_clone.emit("aad-auth-result", serde_json::json!({
                    "endpoint_id": endpoint_id_clone,
                    "success": true,
                    "warning": "无法获取 refresh_token，无法自动发现租户。请手动填写 Tenant ID。",
                }));
                return;
            }
        };

        // Exchange refresh_token for ARM scope token
        let arm_body = match refresh_token_for_tenant("common", &client_id, &refresh_token_str, ARM_SCOPE).await {
            Ok(b) => b,
            Err(e) => {
                save_token_and_notify(
                    &app_clone,
                    &endpoint_id_clone,
                    &access_token,
                    Some(&refresh_token_str),
                    None,
                    &body,
                ).await;
                let _ = app_clone.emit("aad-auth-result", serde_json::json!({
                    "endpoint_id": endpoint_id_clone,
                    "success": true,
                    "warning": format!("无法查询租户列表: {e}。请手动填写 Tenant ID。"),
                }));
                return;
            }
        };

        let arm_token = arm_body.access_token.unwrap();

        let tenants = match discover_tenants(&arm_token).await {
            Ok(t) => t,
            Err(e) => {
                save_token_and_notify(
                    &app_clone,
                    &endpoint_id_clone,
                    &access_token,
                    Some(&refresh_token_str),
                    None,
                    &body,
                ).await;
                let _ = app_clone.emit("aad-auth-result", serde_json::json!({
                    "endpoint_id": endpoint_id_clone,
                    "success": true,
                    "warning": format!("查询租户列表失败: {e}。请手动填写 Tenant ID。"),
                }));
                return;
            }
        };

        if tenants.is_empty() {
            save_token_and_notify(
                &app_clone,
                &endpoint_id_clone,
                &access_token,
                Some(&refresh_token_str),
                None,
                &body,
            ).await;
            return;
        }

        if tenants.len() == 1 {
            let target_tenant = &tenants[0].tenant_id;
            match refresh_token_for_tenant(target_tenant, &client_id, &refresh_token_str, &scope).await {
                Ok(tenant_body) => {
                    let new_access = tenant_body.access_token.clone().unwrap();
                    let new_refresh = tenant_body.refresh_token.clone();
                    save_token_and_notify(
                        &app_clone,
                        &endpoint_id_clone,
                        &new_access,
                        new_refresh.as_deref().or(Some(&refresh_token_str)),
                        Some(target_tenant),
                        &tenant_body,
                    ).await;
                }
                Err(_e) => {
                    let _ = app_clone.emit("aad-auth-result", serde_json::json!({
                        "endpoint_id": endpoint_id_clone,
                        "success": false,
                        "reauth": true,
                        "message": "需要额外验证（如 MFA），正在打开浏览器...",
                    }));
                    match do_oauth_flow(target_tenant, &client_id, &scope).await {
                        Ok((body, _)) => {
                            let new_access = body.access_token.clone().unwrap();
                            let new_refresh = body.refresh_token.clone();
                            save_token_and_notify(
                                &app_clone,
                                &endpoint_id_clone,
                                &new_access,
                                new_refresh.as_deref(),
                                Some(target_tenant),
                                &body,
                            ).await;
                        }
                        Err(e2) => {
                            let _ = app_clone.emit("aad-auth-result", serde_json::json!({
                                "endpoint_id": endpoint_id_clone,
                                "success": false,
                                "error": format!("切换到租户 {} 失败: {e2}", target_tenant),
                            }));
                        }
                    }
                }
            }
            return;
        }

        // Multi-tenant: store refresh_token and emit event for frontend selection
        {
            let state_ref: tauri::State<'_, AppState> = app_clone.state();
            let mut tokens = state_ref.refresh_tokens.write().await;
            tokens.insert(endpoint_id_clone.clone(), refresh_token_str);
            drop(tokens);
            let _ = state_ref.persist_refresh_tokens().await;
        }

        let _ = app_clone.emit("aad-tenant-selection", serde_json::json!({
            "endpoint_id": endpoint_id_clone,
            "tenants": tenants,
            "client_id": client_id,
            "scope": scope,
        }));
    });

    Ok(DeviceCodeResponse {
        user_code: String::new(),
        verification_uri: String::new(),
        message: "正在打开浏览器进行登录，请在浏览器中完成认证...".into(),
        expires_in: 120,
        interval: 0,
    })
}

/// User selected a tenant — exchange saved refresh_token for that tenant's token
#[tauri::command]
pub async fn aad_select_tenant(
    app: tauri::AppHandle,
    endpoint_id: String,
    tenant_id: String,
    client_id: String,
    scope: Option<String>,
) -> Result<(), String> {
    let scope = scope.unwrap_or_else(|| DEFAULT_SCOPE.to_string());
    let client_id = if client_id.is_empty() { DEFAULT_CLIENT_ID.to_string() } else { client_id };

    let state_ref: tauri::State<'_, AppState> = app.state();
    let refresh_token_str = {
        let tokens = state_ref.refresh_tokens.read().await;
        tokens.get(&endpoint_id).cloned()
    };

    let refresh_token_str = refresh_token_str
        .ok_or_else(|| "未找到该端点的 refresh_token，请重新登录".to_string())?;

    let app_clone = app.clone();
    let endpoint_id_clone = endpoint_id.clone();

    tokio::spawn(async move {
        match refresh_token_for_tenant(&tenant_id, &client_id, &refresh_token_str, &scope).await {
            Ok(tenant_body) => {
                let new_access = tenant_body.access_token.clone().unwrap();
                let new_refresh = tenant_body.refresh_token.clone();
                save_token_and_notify(
                    &app_clone,
                    &endpoint_id_clone,
                    &new_access,
                    new_refresh.as_deref().or(Some(&refresh_token_str)),
                    Some(&tenant_id),
                    &tenant_body,
                ).await;
            }
            Err(_e) => {
                let _ = app_clone.emit("aad-auth-result", serde_json::json!({
                    "endpoint_id": endpoint_id_clone,
                    "success": false,
                    "reauth": true,
                    "message": "需要额外验证（如 MFA），正在打开浏览器...",
                }));
                match do_oauth_flow(&tenant_id, &client_id, &scope).await {
                    Ok((body, _)) => {
                        let new_access = body.access_token.clone().unwrap();
                        let new_refresh = body.refresh_token.clone();
                        save_token_and_notify(
                            &app_clone,
                            &endpoint_id_clone,
                            &new_access,
                            new_refresh.as_deref(),
                            Some(&tenant_id),
                            &body,
                        ).await;
                    }
                    Err(e2) => {
                        let _ = app_clone.emit("aad-auth-result", serde_json::json!({
                            "endpoint_id": endpoint_id_clone,
                            "success": false,
                            "error": format!("切换到租户 {tenant_id} 失败: {e2}"),
                        }));
                    }
                }
            }
        }
    });

    Ok(())
}

/// Refresh access_token using refresh_token (frontend calls when token nears expiry)
#[tauri::command]
pub async fn aad_refresh_token(
    app: tauri::AppHandle,
    endpoint_id: String,
) -> Result<(), String> {
    let state_ref: tauri::State<'_, AppState> = app.state();

    let (refresh_token_str, tenant_id, client_id) = {
        let tokens = state_ref.refresh_tokens.read().await;
        let rt = tokens.get(&endpoint_id).cloned()
            .ok_or_else(|| "未找到该端点的 refresh_token，请重新登录".to_string())?;

        let config = state_ref.config.read().await;
        let ep = config.endpoints.iter().find(|e| e.id == endpoint_id)
            .ok_or_else(|| "端点不存在".to_string())?;

        let tid = if ep.azure_tenant_id.is_empty() { "common".to_string() } else { ep.azure_tenant_id.clone() };
        let cid = if ep.azure_client_id.is_empty() { DEFAULT_CLIENT_ID.to_string() } else { ep.azure_client_id.clone() };
        (rt, tid, cid)
    };

    let app_clone = app.clone();
    let endpoint_id_clone = endpoint_id.clone();

    tokio::spawn(async move {
        match refresh_token_for_tenant(&tenant_id, &client_id, &refresh_token_str, DEFAULT_SCOPE).await {
            Ok(body) => {
                let new_access = body.access_token.clone().unwrap();
                let new_refresh = body.refresh_token.clone();
                save_token_and_notify(
                    &app_clone,
                    &endpoint_id_clone,
                    &new_access,
                    new_refresh.as_deref().or(Some(&refresh_token_str)),
                    Some(&tenant_id),
                    &body,
                ).await;
            }
            Err(e) => {
                let _ = app_clone.emit("aad-auth-result", serde_json::json!({
                    "endpoint_id": endpoint_id_clone,
                    "success": false,
                    "error": format!("刷新 token 失败: {e}"),
                }));
            }
        }
    });

    Ok(())
}

/// Silent token refresh (called at startup, no AppHandle/events needed)
pub async fn refresh_token_silent(
    state: &AppState,
    endpoint_id: &str,
    tenant_id: &str,
    client_id: &str,
    refresh_token: &str,
) -> Result<(), String> {
    let body = refresh_token_for_tenant(tenant_id, client_id, refresh_token, DEFAULT_SCOPE).await?;

    let new_access = body.access_token.as_ref()
        .ok_or_else(|| "刷新后 Azure 返回空 access_token".to_string())?;

    {
        let mut config = state.config.write().await;
        if let Some(ep) = config.endpoints.iter_mut().find(|e| e.id == endpoint_id) {
            ep.api_key = new_access.clone();
            ep.auth_mode = tfp_core::AzureAuthMode::Aad;
            ep.auth_header_mode = tfp_core::ApiKeyHeaderMode::Bearer;
            if !tenant_id.is_empty() && ep.azure_tenant_id != tenant_id {
                ep.azure_tenant_id = tenant_id.to_string();
            }
        }
    }
    let _ = state.persist_config().await;

    if let Some(new_rt) = &body.refresh_token {
        let mut tokens = state.refresh_tokens.write().await;
        tokens.insert(endpoint_id.to_string(), new_rt.clone());
        drop(tokens);
        let _ = state.persist_refresh_tokens().await;
    }

    Ok(())
}

/// Wait for a single HTTP callback on the TCP listener, parse the code parameter
async fn wait_for_callback(listener: TcpListener) -> Result<String, String> {
    let (mut stream, _) = listener.accept().await.map_err(|e| format!("accept 失败: {e}"))?;

    let mut buf = vec![0u8; 4096];
    let n = stream.read(&mut buf).await.map_err(|e| format!("读取失败: {e}"))?;
    let request = String::from_utf8_lossy(&buf[..n]);

    // Parse GET /?code=xxx&... HTTP/1.1
    let code = request
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .and_then(|path| url::Url::parse(&format!("http://localhost{path}")).ok())
        .and_then(|u| u.query_pairs().find(|(k, _)| k == "code").map(|(_, v)| v.to_string()));

    let error = request
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .and_then(|path| url::Url::parse(&format!("http://localhost{path}")).ok())
        .and_then(|u| u.query_pairs().find(|(k, _)| k == "error_description").map(|(_, v)| v.to_string()));

    let html = if code.is_some() {
        "<html><body style='text-align:center;padding:60px;font-family:system-ui'>\
         <h2>\u{2705} 登录成功</h2><p>您可以关闭此窗口，返回应用继续使用。</p></body></html>"
    } else {
        "<html><body style='text-align:center;padding:60px;font-family:system-ui'>\
         <h2>\u{274c} 登录失败</h2><p>请关闭此窗口，返回应用重试。</p></body></html>"
    };
    let response = format!(
        "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        html.len(),
        html
    );
    let _ = stream.write_all(response.as_bytes()).await;
    let _ = stream.shutdown().await;

    match code {
        Some(c) => Ok(c),
        None => Err(error.unwrap_or_else(|| "回调中未包含 code 参数".into())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_generate_code_verifier_length() {
        let verifier = generate_code_verifier();
        assert!(
            verifier.len() >= 43 && verifier.len() <= 128,
            "verifier length {} not in PKCE range 43..=128", verifier.len(),
        );
    }

    #[test]
    fn test_generate_code_verifier_url_safe() {
        let verifier = generate_code_verifier();
        for c in verifier.chars() {
            assert!(
                c.is_ascii_alphanumeric() || c == '-' || c == '_',
                "verifier contains non-URL-safe char: '{c}'",
            );
        }
    }

    #[test]
    fn test_compute_code_challenge_s256() {
        // RFC 7636 Appendix B reference vector
        let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        let challenge = compute_code_challenge(verifier);
        assert_eq!(challenge, "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    #[test]
    fn test_compute_code_challenge_deterministic() {
        let verifier = generate_code_verifier();
        let c1 = compute_code_challenge(&verifier);
        let c2 = compute_code_challenge(&verifier);
        assert_eq!(c1, c2, "same verifier must produce same challenge");
        assert_ne!(c1, verifier, "challenge must differ from verifier");
    }
}
