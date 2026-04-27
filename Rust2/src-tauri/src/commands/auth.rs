use serde::{Deserialize, Serialize};
use tauri::{Emitter, Manager};
use base64::Engine;
use sha2::{Sha256, Digest};
use tokio::net::TcpListener;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

use crate::state::AppState;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  AAD (Microsoft Entra ID) 浏览器交互式登录
//
//  完整流程（对齐 C# AzureTokenProvider.LoginAutoAsync）：
//  1. 生成 PKCE code_verifier + code_challenge
//  2. 在本地启动一次性 HTTP 监听 (localhost:随机端口)
//  3. 打开系统浏览器到 Azure 授权 URL
//  4. 用户在浏览器完成登录后，Azure 重定向到 localhost:{port}/?code=xxx
//  5. 用本地监听捕获 code，交换成 access_token + refresh_token
//  6. 若 tenantId 为空 → 用 token 查 ARM /tenants 发现租户列表
//     - 单租户: 自动用该 tenant 重新走 OAuth 获取正确 token
//     - 多租户: 发事件让前端选择，再由 aad_select_tenant 完成
//  7. Token 保存到端点配置，事件通知前端
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

/// 浏览器登录第一步返回给前端的信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DeviceCodeResponse {
    pub user_code: String,
    pub verification_uri: String,
    pub message: String,
    pub expires_in: u64,
    pub interval: u64,
}

/// Token 获取成功后的信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AadTokenResult {
    pub access_token: String,
    pub token_type: String,
    pub expires_in: u64,
    pub scope: String,
}

/// 租户信息（对齐 C# AzureTenantInfo）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AzureTenantInfo {
    pub tenant_id: String,
    pub display_name: String,
    pub default_domain: String,
}

/// Azure AD Token 响应（内部）
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
/// Azure CLI well-known client ID (同 Azure.Identity SDK 的 DeveloperSignOnClientId)
const DEFAULT_CLIENT_ID: &str = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

/// 生成 PKCE code_verifier (43-128 字符的 URL-safe 随机串)
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

/// 从 code_verifier 计算 code_challenge (S256)
fn compute_code_challenge(verifier: &str) -> String {
    let hash = Sha256::digest(verifier.as_bytes());
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(hash)
}

/// 执行一次完整的 OAuth Authorization Code Flow + PKCE，返回 token 响应
async fn do_oauth_flow(
    tenant: &str,
    client_id: &str,
    scope: &str,
) -> Result<(AzureTokenResponse, String), String> {
    // 绑定本地随机端口
    let listener = TcpListener::bind("127.0.0.1:0")
        .await
        .map_err(|e| format!("无法绑定本地端口: {e}"))?;
    let local_port = listener.local_addr().map_err(|e| format!("{e}"))?.port();
    let redirect_uri = format!("http://localhost:{local_port}");

    // PKCE
    let code_verifier = generate_code_verifier();
    let code_challenge = compute_code_challenge(&code_verifier);

    // 构建授权 URL
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

    // 打开系统浏览器
    let _ = open::that(&auth_url);

    // 等待回调（超时 120 秒）
    let auth_code = match tokio::time::timeout(
        std::time::Duration::from_secs(120),
        wait_for_callback(listener),
    ).await {
        Ok(Ok(code)) => code,
        Ok(Err(e)) => return Err(format!("回调处理失败: {e}")),
        Err(_) => return Err("登录超时（120秒），请重试".into()),
    };

    // 用 code 换取 token
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

/// 用 refresh_token 换取新的 access_token（针对指定租户）
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

/// 查询 ARM tenants API，获取当前账号可访问的租户列表
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
                // 优先 displayName，回退 defaultDomain
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

/// 保存 token 到端点配置并发送成功事件
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
            ep.auth_mode = "aad".to_string();
            ep.auth_header_mode = "bearer".to_string();
            if let Some(tid) = tenant_id {
                if !tid.is_empty() {
                    ep.azure_tenant_id = tid.to_string();
                }
            }
        }
    }
    let _ = state_ref.persist_config().await;

    // 把 refresh_token 持久化到 SQLite（用于后续自动刷新，重启后仍有效，90天有效期）
    if let Some(rt) = refresh_token {
        let mut tokens = state_ref.refresh_tokens.write().await;
        tokens.insert(endpoint_id.to_string(), rt.to_string());
        drop(tokens);
        let _ = state_ref.persist_refresh_tokens().await;
    }

    // AAD token 更新后，重新注册 providers 让新 token 对所有能力（文字/图片/视频）立即生效
    {
        let config = state_ref.config.read().await;
        let endpoints = config.endpoints.clone();
        drop(config);
        crate::register_providers_from_config_async(&*state_ref, &endpoints).await;
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

/// 启动浏览器交互式登录（Authorization Code Flow + PKCE）
///
/// 完整流程（对齐 C# LoginAutoAsync）：
/// 1. 用 common/指定tenant 登录获取初始 token
/// 2. 若 tenantId 为空 → 用 refresh_token 换 ARM token → 查租户列表
///    - 单租户: 自动用 refresh_token 换该租户的 token
///    - 多租户: 发 "aad-tenant-selection" 事件让前端选择
/// 3. Token 保存到配置
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
        // Phase 1: OAuth 登录
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

        let access_token = body.access_token.clone().unwrap(); // 已在 do_oauth_flow 中验证
        let refresh_token_val = body.refresh_token.clone();

        // 如果用户指定了 tenantId，直接保存（无需发现）
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

        // Phase 2: 租户发现（tenantId 为空）
        // 用 refresh_token 换取 ARM scope 的 token 来查询租户
        let refresh_token_str = match &refresh_token_val {
            Some(rt) => rt.clone(),
            None => {
                // 没有 refresh_token，直接用初始 token（可能会因 scope 不匹配失败）
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

        // 用 refresh_token 换 ARM scope token
        let arm_body = match refresh_token_for_tenant("common", &client_id, &refresh_token_str, ARM_SCOPE).await {
            Ok(b) => b,
            Err(e) => {
                // ARM token 换取失败，回退：直接保存初始 token
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

        // 查询租户列表
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
            // 0 个租户：直接保存
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
            // 单租户：自动用 refresh_token 换取该租户的 cognitiveservices token
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
                    // refresh_token 失败（如需要 MFA），回退到浏览器重新认证该租户
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

        // 多租户：存储 refresh_token 到 state 并持久化，发事件让前端选择
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

/// 用户选择租户后，用已保存的 refresh_token 换取该租户的 token
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
        // 先尝试用 refresh_token 换取该租户的 token
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
                // refresh_token 失败（如需要 MFA），回退到浏览器重新认证该租户
                // 先通知前端「正在打开浏览器」，对齐 C# LoginAutoAsync(tenantId, clientId, forceInteractive: true)
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

/// 用 refresh_token 刷新 access_token（前端可在 token 快过期时调用）
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

/// 静默刷新 token（供启动时后台调用，不需要 AppHandle/事件通知）
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

    // 更新端点配置（保留 auth_mode=aad + tenant_id，确保下次启动能自动刷新）
    {
        let mut config = state.config.write().await;
        if let Some(ep) = config.endpoints.iter_mut().find(|e| e.id == endpoint_id) {
            ep.api_key = new_access.clone();
            ep.auth_mode = "aad".to_string();
            ep.auth_header_mode = "bearer".to_string();
            // 保持已保存的 tenant_id（不覆盖为空）
            if !tenant_id.is_empty() && ep.azure_tenant_id != tenant_id {
                ep.azure_tenant_id = tenant_id.to_string();
            }
        }
    }
    let _ = state.persist_config().await;

    // 更新 refresh_token（Azure 可能返回新的 refresh_token）
    if let Some(new_rt) = &body.refresh_token {
        let mut tokens = state.refresh_tokens.write().await;
        tokens.insert(endpoint_id.to_string(), new_rt.clone());
        drop(tokens);
        let _ = state.persist_refresh_tokens().await;
    }

    Ok(())
}

/// 在本地 TCP listener 上等待一次 HTTP 回调，解析 code 参数
async fn wait_for_callback(listener: TcpListener) -> Result<String, String> {
    let (mut stream, _) = listener.accept().await.map_err(|e| format!("accept 失败: {e}"))?;

    let mut buf = vec![0u8; 4096];
    let n = stream.read(&mut buf).await.map_err(|e| format!("读取失败: {e}"))?;
    let request = String::from_utf8_lossy(&buf[..n]);

    // 解析 GET /?code=xxx&... HTTP/1.1
    let code = request
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .and_then(|path| url::Url::parse(&format!("http://localhost{path}")).ok())
        .and_then(|u| u.query_pairs().find(|(k, _)| k == "code").map(|(_, v)| v.to_string()));

    // 检查是否有错误
    let error = request
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .and_then(|path| url::Url::parse(&format!("http://localhost{path}")).ok())
        .and_then(|u| u.query_pairs().find(|(k, _)| k == "error_description").map(|(_, v)| v.to_string()));

    // 回复浏览器一个友好的 HTML 页面
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
