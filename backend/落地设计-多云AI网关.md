# 多云 AI 网关 —— 可落地实施设计文档

> 上一篇 `架构设计-多云资源中转网关.md` 回答了**这是什么架构**。
> 本篇回答**怎么把它真正建出来**：用什么语言、用哪些现成轮子、前后端如何对接、如何保证安全、开发人员据此就能开工。

---

## 0. TL;DR （给决策者看的一页）

| 维度 | 推荐 | 备选 / 不推荐 |
| --- | --- | --- |
| **后端语言** | **Rust**（首选，本文核心方案） | Go（次选，团队无 Rust 经验时）；**不推荐**继续 .NET 9/10（会与现有 `backend/TrueFluentPro.Api` 重复） |
| **Web 框架** | `axum 0.8` + `tower` 中间件栈 | `actix-web`、`poem` |
| **HTTP 客户端** | `reqwest 0.12`（rustls + http/2） | `hyper` 直接用 |
| **AAD / OIDC 校验** | `jsonwebtoken 9.x` + `jwks-client-rs` | `openidconnect` crate |
| **配置管理** | `figment` + `dotenvy` | `config` crate |
| **可观测性** | `tracing` + `tracing-opentelemetry` + `opentelemetry-otlp` | — |
| **异步运行时** | `tokio 1.x` | — |
| **数据库** | `sqlx 0.8`（Postgres / SQLite，编译期检查 SQL） | `sea-orm` |
| **密钥库** | Azure Key Vault → `azure_security_keyvault_secrets` (官方 SDK)；本地 `aes-gcm` 加密落 DB | `vault-rs`（HashiCorp） |
| **限流 / 配额** | `tower-governor` + Redis (`redis 0.27`) 计数 | `governor` 直接用 |
| **WebSocket** | `axum::extract::ws` + `tokio-tungstenite` | — |
| **OpenAI 兼容上游** | `async-openai 0.27`（社区维护，成熟） | 自己撸 reqwest |
| **Azure SDK** | `azure_identity` + `azure_security_keyvault_*` + `azure_storage_blobs` | — |
| **腾讯云 SDK** | **无官方 Rust SDK**，需用 reqwest 直拼 + TC3-HMAC-SHA256 自签名（约 80 行） | — |
| **前端调用** | 现有 .NET/Avalonia 客户端 → `Refit`/`HttpClient` 走 OAuth2 PKCE → MSAL 拿 token → Bearer 调网关 | — |
| **部署** | 单二进制（musl 静态链接） + `distroless`/`scratch` Docker 镜像，~15 MB | — |

**为什么不是 .NET？** —— 仓库里 `backend/TrueFluentPro.Api` 已经是 .NET 10 实现，再做一遍意义不大。这份设计假设**新服务以 Rust 重写网关层**，与既有 .NET 后端**并存或逐步替代**。如果团队 0 Rust 经验且不打算学，请直接用 .NET 在现有项目上扩展，本文 §3 起的所有抽象同样适用，只是把 crate 换成 NuGet 包。

---

## 1. 语言选型论证

### 1.1 候选语言矩阵

| 语言 | 性能 | 内存安全 | 并发模型 | 生态成熟度（针对本场景） | 学习曲线 | 部署体积 | 综合 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **Rust** | ★★★★★ | ★★★★★ | async/await + tokio | ★★★★（OpenAI/AAD/腾讯都有可用方案） | ★★（陡峭） | ★★★★★（10–20 MB 静态） | **首选** |
| **Go** | ★★★★ | ★★★（有 GC，无数据竞争编译检查） | goroutine | ★★★★★（云 SDK 最全） | ★★★★（平缓） | ★★★★（15–30 MB） | 次选 |
| **.NET 10** | ★★★★ | ★★★★ | async/await | ★★★★★（Azure 一等公民） | ★★★★ | ★★（含 runtime 80+ MB） | **已存在**，不重写 |
| **Node/TS** | ★★★ | ★★ | event loop | ★★★★ | ★★★★★ | ★★★ | 不推荐（CPU 密集差） |
| **Python** | ★★ | ★★ | asyncio / GIL | ★★★★★（AI 生态） | ★★★★★ | ★★ | 不推荐（吞吐与冷启动差） |

### 1.2 为什么 Rust 是最佳选择

1. **网关是典型的 IO 密集 + 高并发 + 长尾延迟敏感** 场景。Rust 的零成本异步 + 无 GC，让 P99 抖动比 Go/JVM/.NET 低一个数量级。
2. **多 Provider 适配器要求的"数据搬运 + 协议转换"**，Rust 的 `serde` + 类型系统让"AzureChatChunk → 内部 ChatChunk → SSE"这类链路几乎不可能写错。
3. **凭据中介对内存安全极度敏感**：密钥泄漏一次就全军覆没。Rust 的 `secrecy::SecretString`、`zeroize::Zeroize` 能在编译期就杜绝"日志里把 SecretKey 打出来"这类低级失误。
4. **部署体积小、冷启动快**：单文件 musl 静态二进制 + distroless 镜像 ≈ 15 MB；Lambda/Knative/Fly.io 冷启动 < 50 ms，对边缘部署友好。
5. **现成轮子已经够用** —— 见 §2。

### 1.3 什么时候应该改用 Go

- 团队没有 Rust 经验且 6 个月内无法配 1–2 名 Rust 工程师。
- 需要快速对接 **大量** 国内云厂商，而它们都只有 Go SDK（如华为云、UCloud）。
- 团队已经有成熟的 Go 微服务体系（gRPC、Service Mesh、可观测）想复用。

> Go 版本可以无痛照搬本文的所有架构，只是把 crate 换成：`gin/echo` + `golang-jwt/jwt` + `go-redis` + 各家官方 Go SDK。

---

## 2. 现成轮子盘点（"不要重复造")

| 需求 | 现成方案 | 说明 / 何时不用 |
| --- | --- | --- |
| **整套 LLM Gateway 直接拿来用** | [LiteLLM](https://github.com/BerriAI/litellm)（Python）、[Portkey](https://github.com/Portkey-AI/gateway)（TS）、[One-API](https://github.com/songquanpeng/one-api)（Go）、[Helicone](https://github.com/Helicone/helicone)（TS） | **如果你的目标只是 LLM 代理，直接用 LiteLLM/One-API 部署就行**，不要自己写。下面方案适用于"需要 LLM + 语音 + 图像 + 视频 + 翻译 + 自定义业务"的全栈网关。 |
| **APIM AI Gateway**（Azure 托管） | Azure 原生，开箱即用 token 计量、PII redaction、语义缓存 | 强绑定 Azure，且费用对中小团队不友好 |
| **OpenAI 兼容 SDK** | `async-openai` (Rust)、`openai-go`、`openai` (Python) | 任何"声称兼容 OpenAI 协议"的上游（DeepSeek、Together、Groq、本地 vLLM）都能直接用 |
| **Azure SDK for Rust** | `azure_identity`、`azure_security_keyvault_secrets`、`azure_storage_blobs` | 微软**官方**，2024 起 GA |
| **腾讯云 SDK** | ❌ **无官方 Rust SDK**。社区有 `tencentcloud-sdk-rust`（不活跃）。**生产建议自己实现 TC3-HMAC-SHA256 签名**（80 行 + `hmac`/`sha2` crate） | 翻译只需 `TextTranslate` 一个接口，自签更简单可控 |
| **AAD JWT 校验** | `jsonwebtoken` + 缓存 JWKS（自己实现 5 分钟 TTL）或 `jwks-client-rs` | 不要用 `oauth2` crate 做服务端校验，那是给客户端用的 |
| **OAuth2 PKCE 客户端**（前端用） | `oauth2` crate（Rust）；.NET 用 **MSAL.NET**（已在 `Services/Cloud/CloudAuthService.cs`） | 现有 .NET 客户端不动 |
| **限流** | `tower-governor`（进程内）；分布式用 Redis Lua 脚本 | 进程内不够时上 Redis |
| **熔断 / 重试** | `tower::retry` + `tower::timeout`；自己写指数退避 ≈ 30 行 | 不要引入 `failsafe`，过度设计 |
| **可观测性** | `tracing` + `tracing-opentelemetry` + OTLP exporter | 直接对接 Grafana/Tempo/Datadog |
| **Prompt 注入防护** | [LLM Guard](https://github.com/protectai/llm-guard)（Python，HTTP 调用） | Rust 侧只做正则黑名单兜底 |

---

## 3. 系统架构（落地版）

```
┌─────────────────────────────────────────────────────────────┐
│  现有 Avalonia / .NET 客户端 (TrueFluentPro 桌面端)         │
│  - MSAL.NET 拿 AAD access_token (PKCE flow)                │
│  - 通过 ICloudApiClient → Bearer token 调下面网关          │
└───────────────────────────┬─────────────────────────────────┘
                            │ HTTPS + Bearer JWT
                            ▼
┌─────────────────────────────────────────────────────────────┐
│  Rust 网关  (axum + tokio)         单二进制 / Docker        │
│                                                             │
│  ┌───────────────── tower middleware stack ──────────────┐ │
│  │ trace_layer → cors → request_id → auth(JWT)          │ │
│  │   → tenant_extract → quota(governor+redis)           │ │
│  │   → audit → timeout → handler                        │ │
│  └───────────────────────────────────────────────────────┘ │
│                                                             │
│  ┌──── routing ──────────────────────────────────────────┐ │
│  │ POST /v1/chat/completions          (OpenAI 兼容)     │ │
│  │ POST /v1/translate                                    │ │
│  │ POST /v1/audio/transcriptions                         │ │
│  │ POST /v1/audio/speech                                 │ │
│  │ POST /v1/images/generations                           │ │
│  │ GET  /v1/models                                       │ │
│  │ WS   /v1/realtime/translate                           │ │
│  └───────────────────────────────────────────────────────┘ │
│                                                             │
│  ┌──── core ─────────────────────────────────────────────┐ │
│  │ Provider 抽象层 (trait + 工厂)                       │ │
│  │   ├─ ChatProvider  : Azure / OpenAI / DeepSeek / 混元│ │
│  │   ├─ Translator    : Azure / Tencent / Volc          │ │
│  │   ├─ Transcriber   : Azure Speech / Whisper          │ │
│  │   └─ Tts / ImageGen / VideoGen ...                   │ │
│  │                                                       │ │
│  │ CredentialBroker  (env → Postgres/AES → KeyVault)    │ │
│  │ TenantRouter      (按租户/灰度/配额选 Provider)      │ │
│  │ UsageRecorder     (token / 字符 / 秒数 → Postgres)   │ │
│  └───────────────────────────────────────────────────────┘ │
└────┬───────────────────┬────────────────────┬───────────────┘
     │                   │                    │
     ▼                   ▼                    ▼
 Azure OpenAI       腾讯云 TMT          Azure Speech / 自建 Whisper
 Azure Translator   腾讯混元            Azure Blob (大文件)
 Azure Foundry      Volc Ark            ...
```

### 3.1 项目目录建议

```
backend-rust/
├── Cargo.toml
├── crates/
│   ├── gateway/                  # 二进制 crate
│   │   ├── src/
│   │   │   ├── main.rs           # axum 启动 + 路由装配
│   │   │   ├── config.rs         # figment 配置加载
│   │   │   ├── middleware/
│   │   │   │   ├── auth.rs       # JWT 校验 + 用户/租户提取
│   │   │   │   ├── quota.rs      # Redis 配额扣减
│   │   │   │   └── audit.rs      # 请求/响应审计
│   │   │   ├── routes/
│   │   │   │   ├── chat.rs
│   │   │   │   ├── translate.rs
│   │   │   │   ├── speech.rs
│   │   │   │   └── realtime_ws.rs
│   │   │   └── error.rs
│   │   └── tests/                # 集成测试
│   ├── providers/                # Provider 抽象 + 实现
│   │   ├── src/
│   │   │   ├── lib.rs            # trait + 工厂
│   │   │   ├── chat/
│   │   │   │   ├── azure.rs
│   │   │   │   ├── openai_compat.rs   # 通用 OpenAI 兼容
│   │   │   │   └── tencent_hunyuan.rs
│   │   │   ├── translate/
│   │   │   │   ├── azure.rs
│   │   │   │   └── tencent.rs
│   │   │   └── speech/...
│   ├── credential-broker/        # 凭据三级链
│   │   └── src/
│   │       ├── env.rs
│   │       ├── db.rs             # AES-256-GCM + Argon2id
│   │       └── keyvault.rs       # azure_security_keyvault_secrets
│   ├── domain/                   # 内部统一模型 (DTO/契约)
│   │   └── src/lib.rs
│   └── observability/
│       └── src/lib.rs            # tracing 初始化
├── migrations/                   # sqlx migrations
└── Dockerfile
```

> 用 **Cargo workspace + 多 crate**，让 `providers` 可以独立编译/测试，未来还能拆成内部 git 依赖给别的服务复用。

---

## 4. 核心代码骨架（开发人员可直接拷贝起步）

### 4.1 `Cargo.toml`（gateway crate）

```toml
[package]
name = "gateway"
version = "0.1.0"
edition = "2024"

[dependencies]
# Web
axum = { version = "0.8", features = ["macros", "ws"] }
tower = { version = "0.5", features = ["limit", "timeout", "retry"] }
tower-http = { version = "0.6", features = ["trace", "cors", "request-id", "compression-gzip"] }
tower-governor = "0.4"

# Async
tokio = { version = "1.41", features = ["full"] }
tokio-stream = "0.1"
futures = "0.3"

# HTTP client
reqwest = { version = "0.12", default-features = false, features = ["rustls-tls", "json", "stream", "http2"] }

# Serialization
serde = { version = "1", features = ["derive"] }
serde_json = "1"

# Auth
jsonwebtoken = "9"
jwks-client-rs = "0.5"

# Crypto / secrets
aes-gcm = "0.10"
argon2 = "0.5"
secrecy = { version = "0.10", features = ["serde"] }
zeroize = { version = "1", features = ["derive"] }

# Storage
sqlx = { version = "0.8", features = ["runtime-tokio-rustls", "postgres", "sqlite", "macros", "uuid", "chrono"] }
redis = { version = "0.27", features = ["tokio-comp", "connection-manager"] }

# Config
figment = { version = "0.10", features = ["env", "toml"] }
dotenvy = "0.15"

# Observability
tracing = "0.1"
tracing-subscriber = { version = "0.3", features = ["env-filter", "json"] }
tracing-opentelemetry = "0.28"
opentelemetry = "0.27"
opentelemetry-otlp = { version = "0.27", features = ["grpc-tonic"] }

# Errors
thiserror = "2"
anyhow = "1"

# Domain crates
providers = { path = "../providers" }
credential-broker = { path = "../credential-broker" }
domain = { path = "../domain" }
```

### 4.2 Provider 抽象（`providers/src/lib.rs`）

```rust
use async_trait::async_trait;
use futures::stream::BoxStream;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatRequest {
    pub messages: Vec<ChatMessage>,
    pub model: String,
    pub temperature: Option<f32>,
    pub max_tokens: Option<u32>,
    pub stream: bool,
    /// 由 gateway 注入，provider 不可信任客户端给的 tenant
    #[serde(skip_deserializing)]
    pub tenant_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: String,
    pub content: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct ChatChunk {
    pub delta: String,
    pub finish_reason: Option<String>,
    pub usage: Option<TokenUsage>,
}

#[derive(Debug, Clone, Copy, Serialize)]
pub struct TokenUsage { pub prompt: u32, pub completion: u32 }

#[async_trait]
pub trait ChatProvider: Send + Sync {
    fn id(&self) -> &'static str;
    async fn chat_stream(
        &self,
        req: ChatRequest,
    ) -> Result<BoxStream<'static, Result<ChatChunk, ProviderError>>, ProviderError>;
}

#[derive(Debug, thiserror::Error)]
pub enum ProviderError {
    #[error("upstream error: {0}")]
    Upstream(String),
    #[error("rate limited")]
    RateLimited,
    #[error("invalid credential")]
    BadCredential,
    #[error("network: {0}")]
    Network(#[from] reqwest::Error),
}

/// 工厂：按租户/模型/灰度选择具体 Provider。
pub trait ChatProviderFactory: Send + Sync {
    fn resolve(&self, tenant_id: &str, model: &str)
        -> std::sync::Arc<dyn ChatProvider>;
}
```

> **关键设计点**
> - `tenant_id` 用 `#[serde(skip_deserializing)]`，**永远从 JWT 注入，不接受客户端传值** —— 防止租户越权。
> - `BoxStream` 让 SSE / 非 SSE 共用一个抽象。
> - `ProviderError` 在 axum 层统一映射成 HTTP 状态，避免上游异常细节泄露给前端。

### 4.3 AAD JWT 校验中间件（`gateway/src/middleware/auth.rs`）

```rust
use axum::{
    extract::{Request, State},
    http::{header, StatusCode},
    middleware::Next,
    response::Response,
};
use jsonwebtoken::{decode, decode_header, Algorithm, DecodingKey, Validation};
use serde::Deserialize;
use std::sync::Arc;

#[derive(Clone)]
pub struct AuthState {
    pub jwks: Arc<jwks_client_rs::JwksClient>,
    pub expected_audience: String, // your-api://app-id
    pub expected_issuer: String,   // https://login.microsoftonline.com/{tid}/v2.0
}

#[derive(Debug, Deserialize, Clone)]
pub struct AadClaims {
    pub oid: String,            // 用户对象 ID（稳定）
    pub tid: String,            // 租户 ID
    pub preferred_username: Option<String>,
    pub scp: Option<String>,    // 委派权限 scopes
    pub roles: Option<Vec<String>>, // 应用角色
    pub exp: usize,
}

pub async fn require_aad_jwt(
    State(state): State<AuthState>,
    mut req: Request,
    next: Next,
) -> Result<Response, StatusCode> {
    let token = req
        .headers()
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
        .ok_or(StatusCode::UNAUTHORIZED)?;

    let header = decode_header(token).map_err(|_| StatusCode::UNAUTHORIZED)?;
    let kid = header.kid.ok_or(StatusCode::UNAUTHORIZED)?;
    let jwk = state.jwks.get(&kid).await.map_err(|_| StatusCode::UNAUTHORIZED)?;
    let key = DecodingKey::from_jwk(&jwk).map_err(|_| StatusCode::UNAUTHORIZED)?;

    let mut v = Validation::new(Algorithm::RS256);
    v.set_audience(&[state.expected_audience.as_str()]);
    v.set_issuer(&[state.expected_issuer.as_str()]);
    v.validate_exp = true;
    v.leeway = 30; // 时钟偏差容忍

    let data = decode::<AadClaims>(token, &key, &v)
        .map_err(|_| StatusCode::UNAUTHORIZED)?;

    req.extensions_mut().insert(data.claims);
    Ok(next.run(req).await)
}
```

> **安全要点（按出现频率排）**
> 1. **必须校验 `aud` 和 `iss`**：很多教程只验签名，结果别家 AAD 应用的 token 也能进。
> 2. **JWKS 必须缓存**：jwks-client-rs 默认 5 分钟 TTL，别每次请求拉一次。
> 3. **`oid` 才是稳定用户标识**，不要用 `sub`（同一用户在不同应用的 sub 不同）。
> 4. **`tid` 必须落审计**：跨租户事故几乎都因为没记 tid。
> 5. **leeway 30 秒**：移动客户端时钟偏差很常见，不留就会出现莫名其妙的 401。

### 4.4 凭据三级链（`credential-broker/src/lib.rs`）

```rust
use secrecy::{ExposeSecret, SecretString};
use async_trait::async_trait;

#[async_trait]
pub trait SecretSource: Send + Sync {
    /// key 的格式约定：`tenant:{tid}:provider:{provider}:{field}`
    /// 例如 `tenant:contoso:provider:tencent:secret_key`
    async fn get(&self, key: &str) -> anyhow::Result<Option<SecretString>>;
}

pub struct ChainedSecretSource {
    sources: Vec<Box<dyn SecretSource>>, // env → db → keyvault
}

impl ChainedSecretSource {
    pub fn new(sources: Vec<Box<dyn SecretSource>>) -> Self { Self { sources } }
}

#[async_trait]
impl SecretSource for ChainedSecretSource {
    async fn get(&self, key: &str) -> anyhow::Result<Option<SecretString>> {
        for s in &self.sources {
            if let Some(v) = s.get(key).await? { return Ok(Some(v)); }
        }
        Ok(None)
    }
}
```

> 三级源的实现要点：
> - **EnvSecretSource**：`std::env::var(KEY)`，把找到的字符串立刻包进 `SecretString`，原 String drop 时被 zeroize。
> - **DbSecretSource**：SQLite/Postgres 表 `secrets(key TEXT PK, ciphertext BYTEA, nonce BYTEA, version INT)`；用 AES-256-GCM 加密，**KEK 来自启动时的 env 变量** `MASTER_KEY_BASE64`，DEK 一密钥一 nonce。
> - **KeyVaultSecretSource**：`azure_security_keyvault_secrets::SecretClient`，配合 `DefaultAzureCredential`（在 AKS 用 Workload Identity，本地用 `az login`）。
> - **任何 Provider 实现里禁止 `println!`/`tracing::info!` 把 SecretString 解包**。`secrecy` 让你必须显式 `.expose_secret()`，code review 一眼能抓到。

### 4.5 路由装配（`gateway/src/main.rs` 节选）

```rust
#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let cfg = config::load()?;
    observability::init(&cfg.otlp_endpoint)?;

    let state = AppState::build(&cfg).await?;

    let app = axum::Router::new()
        .route("/v1/chat/completions", axum::routing::post(routes::chat::completions))
        .route("/v1/translate",         axum::routing::post(routes::translate::translate))
        .route("/v1/audio/transcriptions", axum::routing::post(routes::speech::transcribe))
        .route("/v1/realtime/translate",   axum::routing::any(routes::realtime_ws::handler))
        .route("/healthz",              axum::routing::get(|| async { "ok" }))
        .layer(axum::middleware::from_fn_with_state(state.auth.clone(), middleware::auth::require_aad_jwt))
        .layer(axum::middleware::from_fn_with_state(state.quota.clone(), middleware::quota::check_quota))
        .layer(tower_http::trace::TraceLayer::new_for_http())
        .layer(tower_http::cors::CorsLayer::very_permissive()) // 生产收紧到具体 origin
        .with_state(state);

    let listener = tokio::net::TcpListener::bind(&cfg.bind_addr).await?;
    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown_signal())
        .await?;
    Ok(())
}
```

### 4.6 一个完整的处理函数示例（`routes/chat.rs`）

```rust
pub async fn completions(
    State(state): State<AppState>,
    axum::Extension(claims): axum::Extension<AadClaims>,
    axum::Json(mut req): axum::Json<ChatRequest>,
) -> Result<axum::response::Response, ApiError> {
    // 1. 强制注入 tenant，永远不信任客户端
    req.tenant_id = claims.tid.clone();

    // 2. 工厂选 provider
    let provider = state.chat_factory.resolve(&claims.tid, &req.model);

    // 3. 流式响应 → SSE
    let stream = provider.chat_stream(req).await?;
    let sse_stream = stream.map(|chunk| {
        chunk
            .map(|c| axum::response::sse::Event::default()
                .json_data(&c).unwrap())
            .map_err(|e| std::io::Error::other(e.to_string()))
    });

    Ok(axum::response::Sse::new(sse_stream)
        .keep_alive(axum::response::sse::KeepAlive::default())
        .into_response())
}
```

---

## 5. 前端如何调用（既有 .NET / Avalonia 客户端）

### 5.1 现状对接

仓库里 `Services/Cloud/CloudApiClient.cs` + `CloudAuthService.cs` 已经实现了 MSAL.NET PKCE 登录 + 把 access_token 透传到后端。**对接新 Rust 网关只需要改一个 baseUrl**：

```csharp
// Services/Cloud/CloudApiClient.cs
private readonly string _baseUrl = cloudSettings.BackendBaseUrl
    ?? "https://gateway.your-domain.com";

// 调用方式不变（Refit 风格也可）
var resp = await _http.PostAsJsonAsync(
    $"{_baseUrl}/v1/chat/completions",
    new { messages, model = "gpt-4o-mini", stream = true },
    ct);
```

> 注意：Rust 网关应当**故意保留 OpenAI 兼容协议**（`/v1/chat/completions`、SSE `data: {...}\n\n`），这样：
> - 客户端可以无缝复用 `OpenAI` SDK；
> - 第三方工具（Cherry Studio、LobeChat 等）可以直接当 OpenAI endpoint 用。

### 5.2 Token 获取流程（前端侧无变化）

```
[Avalonia 客户端]
    └─ MSAL.NET PublicClientApplication
        ├─ AcquireTokenInteractive(scopes: ["api://gateway/.default"])
        ├─ 浏览器 PKCE 跳转 → AAD → 回调
        └─ 缓存 access_token (有效期 ~1h) + refresh_token

[每次 API 调用]
    └─ AcquireTokenSilent (优先) → 失败 fallback Interactive
    └─ HttpClient.DefaultRequestHeaders.Authorization = Bearer {token}
```

> AAD 应用注册要点（在 Azure Portal）：
> 1. **暴露 API**：创建 Application ID URI `api://<client-id>`，定义 scope 如 `chat.invoke`、`translate.invoke`。
> 2. **客户端应用**注册另外一个 AAD App，给它**预授权**上面的 scope（避免用户首次同意弹窗）。
> 3. **Redirect URI** 用 `http://localhost`（PKCE flow，MSAL.NET 会随机端口）。
> 4. **绝不**在客户端配 client_secret —— 桌面应用是 public client。

### 5.3 流式响应在 Avalonia 侧的消费

```csharp
using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
resp.EnsureSuccessStatusCode();
using var stream = await resp.Content.ReadAsStreamAsync(ct);
using var reader = new StreamReader(stream);

string? line;
while ((line = await reader.ReadLineAsync(ct)) != null)
{
    if (!line.StartsWith("data: ")) continue;
    var json = line.AsSpan(6);
    if (json.SequenceEqual("[DONE]")) break;
    var chunk = JsonSerializer.Deserialize<ChatChunk>(json);
    onDelta?.Invoke(chunk!.Delta);  // PostIfActive 到 UI
}
```

> 这与现有 `AiInsightService.StreamChatAsync` 的实现完全一致，**0 改动**。

### 5.4 WebSocket 实时翻译

```
ws(s)://gateway.your-domain.com/v1/realtime/translate?from=zh&to=en
Headers: Authorization: Bearer {token}    (浏览器 WS 不支持 header → 用 Sec-WebSocket-Protocol 把 token 带上)

Client → Server : 二进制 PCM 16k chunks
Server → Client : {"type":"partial","text":"..."}
                  {"type":"final","text":"..."}
                  {"type":"error","code":"quota_exceeded"}
```

Rust 端 `axum::extract::ws::WebSocketUpgrade` 直接撑得起，配合 `tokio_stream` 做背压。

---

## 6. 安全清单（**部署前必过**）

### 6.1 网络层
- [ ] **强制 TLS 1.2+**（Caddy/Nginx 前置或 axum 直接用 `axum-server` + rustls）。
- [ ] **HSTS** 至少 6 个月，含子域。
- [ ] **CORS** 白名单到具体 origin（不要 `*`）；WS 同源校验。
- [ ] **公网入口**前面挂 WAF（Cloudflare/Azure Front Door），至少开 OWASP Core Rule Set。

### 6.2 认证授权
- [ ] JWT 校验 `aud + iss + exp + signature`，缺一不可。
- [ ] **永远从 token 里取 `tid`/`oid`**，**绝不**信任请求体里的 tenant_id/user_id。
- [ ] 应用角色 (`roles`) 做 RBAC，scope (`scp`) 做能力门禁，二者**双重**检查。
- [ ] 敏感操作（管理 API）要求 ACR ≥ "MFA"。

### 6.3 凭据 / 密钥
- [ ] Master KEK **只**通过环境变量注入；容器编排层（K8s Secret + Workload Identity）落地。
- [ ] 密钥 **永不** 落日志、永不落 trace span、永不出现在错误响应里。`secrecy::SecretString` 强制。
- [ ] DB 加密用 **AES-256-GCM**，**禁止 ECB/CBC**；nonce 一密一变，存数据库里同一行。
- [ ] KeyVault 启用 **purge protection** 和 **soft delete**。
- [ ] 凭据**轮换**：每张密钥都有 `version` 字段，新旧并行 7 天。

### 6.4 上游调用
- [ ] 所有 reqwest client 设 **`tcp_keepalive` + `pool_idle_timeout` + 总超时 30s**，避免连接耗尽。
- [ ] 上游响应**严格 JSON schema 校验**后再回传，防止注入到下游 (Reflected XSS 在 SSE 里也成立)。
- [ ] 上游 4xx **不要原样回传**，重写成自己的 ErrorCode；上游 5xx 重试 ≤ 2 次（指数退避 + jitter）。
- [ ] 调用 Azure / 腾讯**全程 HTTPS**，证书验证不可关。

### 6.5 业务面
- [ ] **配额扣减**走 Redis Lua（原子）；本地 fallback `governor` 防 Redis 挂掉时雪崩。
- [ ] **审计日志**：tenant_id / user_oid / endpoint / model / token_in / token_out / latency / status，结构化 JSON 落 Loki/CLP。
- [ ] **PII / 提示词注入**：在进上游前过一遍黑名单（手机号/身份证/SQL 关键字），可选接 LLM Guard。
- [ ] **回压**：任何 stream handler 必须能感知客户端断开 (`tokio::select!` + `req.is_disconnected()`)，否则上游会被白白消耗。
- [ ] **速率上限**：每用户/每租户/每 IP 三层；按 token 数而不是按请求数。

### 6.6 供应链
- [ ] `cargo deny` 在 CI 里跑：拒绝未审计 license、CVE、yanked 版本。
- [ ] `cargo audit` 每天 cron。
- [ ] Docker 用 **distroless** 或 `gcr.io/distroless/cc` 基础镜像；非 root 用户运行；`--read-only` 文件系统。
- [ ] SBOM (`cargo cyclonedx`) 随发布产物上传。

---

## 7. 部署与发布

### 7.1 Dockerfile（musl 静态二进制，~15 MB 镜像）

```dockerfile
# syntax=docker/dockerfile:1.7
FROM rust:1.83-alpine AS build
RUN apk add --no-cache musl-dev openssl-dev openssl-libs-static pkgconfig
WORKDIR /src
COPY . .
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/src/target \
    cargo build --release --bin gateway --target x86_64-unknown-linux-musl && \
    cp target/x86_64-unknown-linux-musl/release/gateway /gateway

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /gateway /gateway
USER nonroot
EXPOSE 8080
ENTRYPOINT ["/gateway"]
```

### 7.2 K8s 部署关键点

- 用 **Azure Workload Identity** 绑定 ServiceAccount → AAD App，KeyVault 访问免密钥。
- `livenessProbe` 走 `/healthz`；`readinessProbe` 同时检查 Redis/DB 连接。
- HPA 用 `requests_per_second`（Prometheus adapter）而不是 CPU。
- Pod Security Standard = `restricted`。

### 7.3 与现有 .NET 后端的过渡策略

> 仓库里现存 `backend/TrueFluentPro.Api`（.NET 10 minimal API）已实现了类似功能。建议**渐进迁移**而不是大爆炸：

| 阶段 | 内容 | 风险 |
| --- | --- | --- |
| **P1** | Rust 网关只接 `/v1/chat/completions` 一个端点，前端按租户灰度 5% → 50% → 100% | 低，可秒级回滚 |
| **P2** | 翻译、TTS 端点逐个迁移 | 低 |
| **P3** | WS 实时翻译迁移（最难，背压/重连/AAD 子协议） | 中 |
| **P4** | .NET 后端只保留管理面（Admin API、配额报表），数据面全部下线 | — |

---

## 8. 开发里程碑（给 PM 看的）

| 周 | 交付物 | 谁 |
| --- | --- | --- |
| W1 | Cargo workspace 骨架 + AAD JWT 中间件跑通 + `/healthz` | 后端 1 |
| W2 | `ChatProvider` trait + Azure / OpenAI 兼容两种实现 + SSE 路由 | 后端 1 |
| W3 | 凭据三级链 + KeyVault 集成 + 配置加载 | 后端 2 |
| W4 | `Translator` trait + Azure & 腾讯实现（TC3 签名） | 后端 2 |
| W5 | 配额中间件 (Redis) + 审计日志 + tracing/OTLP | 后端 1 |
| W6 | WS 实时翻译 + 客户端断开背压 | 后端 1 |
| W7 | 集成测试（mock 上游）+ 负载测试 (`drill` / `oha`) | QA + 后端 |
| W8 | Dockerize + K8s manifests + 灰度上线 5% | DevOps |

**人力估算**：2 名 Rust 中级工程师 + 0.5 DevOps，**8 周**到生产可用 MVP。

---

## 9. 常见问答

**Q1：为什么不用 gRPC 而用 HTTP+JSON？**
前端是浏览器/桌面客户端，gRPC-Web 的复杂度不值。内部 service-to-service 调用如果将来需要可以再加。

**Q2：Rust 团队招聘难怎么办？**
- LLM Gateway 这类业务**对 Rust 高级特性需求很低**（基本只用 async + trait + serde），中级工程师 1 个月能上手。
- 如果实在招不到，**用 Go 重做这份设计**：把 axum→`gin/echo`，sqlx→`sqlc`，tokio→goroutine，其它一一对应，本文 §3 起的架构 100% 不变。

**Q3：能否完全用 LiteLLM 替代？**
- 如果只是 LLM 代理 + 多 Provider，**可以**。直接 Helm 部署，省 8 周。
- 一旦你需要：**自有审计/计费体系、复杂租户路由、WS 实时翻译、与 Avalonia 客户端的 PKCE 紧耦合**，那 LiteLLM 不够，需要自己做。
- 折中方案：**用 Rust 网关做"前置层"**（认证、配额、审计、WS、翻译），把 LLM 部分**透传**给后面的 LiteLLM 实例，二者各取所长。

**Q4：腾讯云为什么不用 SDK？**
- 没有官方 Rust SDK；社区版本不更新。
- 你只用 `TextTranslate`、`ChatCompletions` 等 1–2 个接口，TC3-HMAC-SHA256 签名 ~80 行 Rust 就能搞定，反而比拖一个 5 MB 的 SDK 干净。
- 示例：
  ```rust
  // sign(secret_key, key_date, "tc3_request") → signing_key
  let k_date    = hmac_sha256(format!("TC3{}", secret_key).as_bytes(), date.as_bytes());
  let k_service = hmac_sha256(&k_date,    b"tmt");
  let k_signing = hmac_sha256(&k_service, b"tc3_request");
  let signature = hex::encode(hmac_sha256(&k_signing, string_to_sign.as_bytes()));
  ```

**Q5：日志里万一打出了密钥怎么办？**
- `secrecy::SecretString` 实现的 `Debug` 永远输出 `[REDACTED]`，`Display` 直接 panic。
- 在 CI 里加 `cargo clippy -- -D clippy::print_stdout -D clippy::dbg_macro`。
- 部署后再加一道：日志收集端（Vector/Fluent Bit）跑正则脱敏 `(?i)(api[_-]?key|secret|password)["'\s:=]+[\w\-+/=]+`。

---

## 10. 参考资料

- Microsoft — *Trusted Subsystem Pattern*: https://learn.microsoft.com/azure/architecture/patterns/trusted-subsystem
- Microsoft — *On-Behalf-Of flow*: https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow
- Microsoft — *Workload Identity Federation*: https://learn.microsoft.com/entra/workload-id/workload-identity-federation
- Azure SDK for Rust: https://github.com/Azure/azure-sdk-for-rust
- LiteLLM: https://github.com/BerriAI/litellm
- Portkey Gateway: https://github.com/Portkey-AI/gateway
- One-API: https://github.com/songquanpeng/one-api
- async-openai (Rust): https://github.com/64bit/async-openai
- axum: https://github.com/tokio-rs/axum
- 腾讯云 TC3 签名规范: https://cloud.tencent.com/document/api/551/15616

---

## 附录 A — 与现有仓库的复用清单

| 现有资产 | 在新方案里如何使用 |
| --- | --- |
| `Services/Cloud/CloudAuthService.cs`（MSAL.NET） | **不动**，作为 token 提供方 |
| `Services/Cloud/CloudApiClient.cs` | 改 baseUrl 指向 Rust 网关 |
| `Models/Cloud/CloudSettings.cs` | 增加 `GatewayBaseUrl` 字段 |
| `backend/TrueFluentPro.Api`（.NET） | 渐进下线，按 §7.3 表 P1→P4 节奏 |
| `deploy/docker-compose.yml` | 增加 `gateway-rust` service；保留 `db`/`cache` profile |
| `deploy/.env.template` | 增加 `MASTER_KEY_BASE64`、`AAD_AUDIENCE`、`AAD_ISSUER` 等 |

---

> **结语**：这份文档之后，开发团队应当能够：
> 1. 起 `cargo new` workspace，把 §3.1 目录 + §4.1 `Cargo.toml` 复制进去就能编译；
> 2. 按 §8 的 8 周里程碑分配到人；
> 3. 上线前对照 §6 安全清单逐项打钩；
> 4. 与现有 Avalonia 客户端用 §5 的方式无痛对接。
>
> 如需进一步落地某一模块（如完整的 Tencent TC3 签名实现、Redis Lua 配额脚本、Workload Identity 配置 YAML），可以在本仓库新开 issue 跟进。
