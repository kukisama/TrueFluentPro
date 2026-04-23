# 02 — 插件化能力引擎与多 Provider 设计

> **文档定位**：本文回答"能力如何注册、Provider 如何抽象、管理员如何配置规则、多厂商如何路由"。  
> **读者**：后端开发工程师。  
> **前置阅读**：`01-总体架构与技术选型.md`。

---

## 1. 核心概念定义

| 概念 | 解释 | 示例 |
|------|------|------|
| **Capability（能力）** | 系统对外暴露的一种功能单元 | `chat`、`text.translate`、`speech.tts`、`image.generate`、`video.generate`、`speech.live.translate` |
| **Provider（供应商）** | 提供某种能力的具体后端实现 | Azure OpenAI、腾讯混元、阿里通义、DeepSeek、自建 vLLM |
| **Adapter（适配器）** | 把 Provider 的私有协议翻译成"我方 Canonical 协议"的代码 | `AzureChatAdapter`、`TencentTranslateAdapter` |
| **Transformation Rule（转换规则）** | 管理员定义的"能力→Provider"映射配置 | `text.translate + zh→en → tencent.tmt`；`chat + gpt-4o → azure.openai` |
| **Capability Registry（能力注册表）** | 运行时维护的"当前可用能力"列表，含启用/禁用状态 | 系统启动时从 DB 加载，Admin API 可热更新 |

---

## 2. 插座设计——Capability 生命周期

### 2.1 能力启停流程

```
管理员录入 Key
       │
       ▼
管理员创建/启用 Capability
       │
       ▼
CapabilityRegistry 加载
       │
       ├─ Provider 实现已注册？ → ✅ Capability 可用
       │
       └─ Provider 实现未注册？ → ❌ 标记 unavailable，用户端不可见
```

**关键原则**：

1. 每种 Capability 可以对应 **0~N 个 Provider**。
2. 0 个 Provider = 该能力处于 **关闭** 状态，用户 `/user/capabilities` 接口看不到它。
3. 管理员可以通过 Admin API 随时 **启用/禁用** 任意 Capability 或单个 Provider。
4. 启用/禁用是 **热生效** 的（配置写 DB → Registry 重新加载，无需重启）。

### 2.2 数据库模型

```sql
-- ═══ 能力定义 ═══
CREATE TABLE capabilities (
    id              TEXT PRIMARY KEY,         -- 'chat', 'text.translate', 'speech.tts', ...
    display_name    TEXT NOT NULL,             -- '智能对话', '文本翻译', '语音合成', ...
    category        TEXT NOT NULL,             -- 'ai', 'translate', 'speech', 'image', 'video'
    is_enabled      BOOLEAN NOT NULL DEFAULT FALSE,
    description     TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ═══ Provider 定义 ═══
CREATE TABLE providers (
    id              TEXT PRIMARY KEY,         -- 'azure.openai', 'tencent.tmt', 'alibaba.dashscope', ...
    vendor          TEXT NOT NULL,             -- 'azure', 'tencent', 'alibaba', 'self-hosted'
    display_name    TEXT NOT NULL,
    is_enabled      BOOLEAN NOT NULL DEFAULT FALSE,
    config_json     JSONB,                    -- Provider 级别配置 (region, endpoint, model list...)
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ═══ Capability ↔ Provider 绑定 (转换规则) ═══
CREATE TABLE capability_provider_bindings (
    id              SERIAL PRIMARY KEY,
    capability_id   TEXT NOT NULL REFERENCES capabilities(id),
    provider_id     TEXT NOT NULL REFERENCES providers(id),
    is_enabled      BOOLEAN NOT NULL DEFAULT TRUE,
    priority        INT NOT NULL DEFAULT 100,        -- 数值越小优先级越高
    weight          INT NOT NULL DEFAULT 100,         -- 灰度权重 (0-100)
    match_condition JSONB,                            -- 条件匹配 (语言对、模型名等)
    config_json     JSONB,                            -- 该绑定的特殊配置
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (capability_id, provider_id, match_condition)
);

-- 示例数据:
-- INSERT INTO capability_provider_bindings VALUES
--   (1, 'text.translate', 'azure.translator',  TRUE, 100, 80, '{"lang_pair":"*"}', NULL),
--   (2, 'text.translate', 'tencent.tmt',       TRUE, 100, 20, '{"lang_pair":"*"}', NULL),
--   (3, 'text.translate', 'tencent.tmt',       TRUE, 50,  100, '{"lang_pair":"*-yue"}', NULL),
--   (4, 'chat',           'azure.openai',      TRUE, 100, 100, '{"model":"gpt-4o*"}', NULL),
--   (5, 'chat',           'tencent.hunyuan',   TRUE, 100, 100, '{"model":"hunyuan*"}', NULL);

-- ═══ 凭据绑定 ═══
CREATE TABLE provider_credentials (
    id              SERIAL PRIMARY KEY,
    provider_id     TEXT NOT NULL REFERENCES providers(id),
    credential_key  TEXT NOT NULL,             -- 'api_key', 'secret_id', 'secret_key', 'endpoint', ...
    encrypted_value BYTEA NOT NULL,            -- AES-256-GCM 加密
    nonce           BYTEA NOT NULL,
    version         INT NOT NULL DEFAULT 1,    -- 密钥轮换版本
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (provider_id, credential_key, version)
);
```

---

## 3. Provider trait 体系

### 3.1 设计原则

1. **每种能力类别一个 trait**：`ChatProvider`、`TextTranslator`、`LiveSpeechTranslator`、`TtsProvider`、`ImageProvider`、`VideoProvider`。
2. **trait 方法只接受/返回 Canonical DTO**：永远不出现厂商私有类型。
3. **`ProviderError` 统一错误枚举**：所有厂商错误映射到 5 种 Canonical 错误码。
4. **`id()` 方法标识 Provider**：用于路由、审计、计费、日志。
5. **流式输出用 `BoxStream`**：SSE/WS 通用。

### 3.2 Trait 定义概览

```
┌──────────────────────────────────────────────────────────┐
│  providers crate — trait 定义                              │
│                                                          │
│  trait ChatProvider {                                     │
│      fn id() -> &str                                     │
│      async fn chat_stream(ChatRequest)                   │
│          -> Result<BoxStream<ChatChunk>, ProviderError>   │
│  }                                                       │
│                                                          │
│  trait TextTranslator {                                   │
│      fn id() -> &str                                     │
│      async fn translate(TextTranslateRequest)            │
│          -> Result<TextTranslateResponse, ProviderError>  │
│  }                                                       │
│                                                          │
│  trait LiveSpeechTranslator {                             │
│      fn id() -> &str                                     │
│      async fn start(LiveTranslateStart, AudioInStream)   │
│          -> Result<BoxStream<LiveChunkOut>, ProviderError>│
│  }                                                       │
│                                                          │
│  trait TtsProvider {                                      │
│      fn id() -> &str                                     │
│      async fn synthesize(TtsRequest)                     │
│          -> Result<AudioStream, ProviderError>            │
│  }                                                       │
│                                                          │
│  trait ImageProvider {                                    │
│      fn id() -> &str                                     │
│      async fn generate(ImageGenRequest)                  │
│          -> Result<ImageGenResponse, ProviderError>       │
│  }                                                       │
│                                                          │
│  trait VideoProvider {                                    │
│      fn id() -> &str                                     │
│      async fn submit(VideoGenRequest)                    │
│          -> Result<VideoJobId, ProviderError>             │
│      async fn poll(VideoJobId)                           │
│          -> Result<VideoGenStatus, ProviderError>         │
│  }                                                       │
│                                                          │
│  enum ProviderError {                                    │
│      Upstream(String),     // 上游非预期错误              │
│      RateLimited,          // 上游 429                   │
│      BadCredential,        // 凭据无效                   │
│      UnsupportedCapability,// 能力不支持                  │
│      Network(reqwest::Error),// 网络故障                  │
│  }                                                       │
└──────────────────────────────────────────────────────────┘
```

### 3.3 Canonical DTO 设计原则

> 引用自 `backend/前端零改动-多Provider后端适配.md` §3：  
> "取所有厂商能力的并集语义，剪掉差异，只暴露稳定字段。"

| DTO | 关键字段 | 说明 |
|-----|---------|------|
| `ChatRequest` | messages, model, temperature, max_tokens, stream, tenant_id | OpenAI 兼容格式 |
| `ChatChunk` | delta, finish_reason, usage | SSE 流式块 |
| `TextTranslateRequest` | text, source_lang (BCP-47), target_lang, tenant_id | 统一语言码 |
| `TextTranslateResponse` | translation, detected_source_lang | 去厂商信息 |
| `LiveTranslateStart` | source_lang, target_lang, sample_rate, tenant_id | WS 握手参数 |
| `LiveChunkOut` | Partial {src, dst, ts_ms} / Final {src, dst, start_ms, end_ms, speaker} | 实时字幕 |
| `TtsRequest` | text, voice_id, output_format, speed | 统一语音参数 |
| `ImageGenRequest` | prompt, size, n, quality | 统一图片参数 |
| `VideoGenRequest` | prompt, duration_sec, resolution | 统一视频参数 |

**语言码规范**：

- 所有对外 API 使用 **BCP-47**（`zh-CN`、`en-US`、`ja`）。
- 内部 `LangCode` 强类型，Adapter 负责双向映射：
  - `lang::to_tencent("zh-CN") → "zh"`
  - `lang::to_azure("zh-CN") → "zh-CN"`
  - `lang::to_alibaba("zh-CN") → "zh"`

---

## 4. Adapter 实现模式

### 4.1 每个 Adapter 的职责

```
Canonical DTO (输入)
       │
       ▼
┌────────────────────────────────┐
│  Adapter (e.g. TencentTmt)     │
│                                │
│  1. 语言码映射 (BCP-47 → 厂商) │
│  2. 从 CredentialBroker 取密钥  │
│  3. 构造厂商私有请求体          │
│  4. 签名/鉴权 (TC3/HMAC/Bearer)│
│  5. 发送请求到厂商              │
│  6. 解析厂商响应                │
│  7. 厂商错误 → ProviderError    │
│  8. 厂商响应 → Canonical DTO    │
└────────────────────────────────┘
       │
       ▼
Canonical DTO (输出)
```

### 4.2 计划中的 Adapter 矩阵

| 能力 | Azure | 腾讯 | 阿里 | 自建 |
|------|-------|------|------|------|
| **chat** | `AzureOpenAiChat` | `TencentHunyuanChat` | `AlibabaDashscopeChat` | `OpenAiCompatChat` (通用) |
| **text.translate** | `AzureTranslator` | `TencentTmt` | `AlibabaTranslate` | — |
| **speech.live.translate** | `AzureSpeechLive` | `TencentAsrTmt` (ASR+翻译拼接) | — | — |
| **speech.tts** | `AzureSpeechTts` | `TencentTts` | `AlibabaCosyvoice` | — |
| **speech.stt** | `AzureSpeechBatch` | `TencentAsr` | — | `WhisperLocal` |
| **image.generate** | `AzureDallE` | — | `AlibabaWanx` | — |
| **video.generate** | `AzureSora` | — | — | — |

> **每增加一个厂商 = 新增一个 Adapter 文件 + 一行注册代码 + 一条数据库路由记录。前端 0 改动。**

### 4.3 `OpenAiCompatChat` — 通用适配器

> 任何声称兼容 OpenAI 协议的服务（DeepSeek、Together、Groq、本地 vLLM）都可以用这一个 Adapter，只需配置不同的 `base_url` 和 `api_key`。

```
Provider 配置 (config_json):
{
    "base_url": "https://api.deepseek.com",
    "api_key_ref": "deepseek.api_key",
    "default_model": "deepseek-chat"
}
```

这样管理员在 Admin UI 里只需要填 URL + Key，就能接入任何 OpenAI 兼容服务，无需写代码。

---

## 5. 能力注册表 (CapabilityRegistry)

### 5.1 职责

1. **系统启动时**：从 `capabilities` + `providers` + `capability_provider_bindings` 三表加载当前配置。
2. **Admin API 修改时**：热重载变更部分（不重启进程）。
3. **对外暴露**：当前哪些能力可用、每个能力背后有哪些 Provider。
4. **对内供给**：为 CapabilityRouter 提供路由决策数据。

### 5.2 内存结构（伪代码）

```
CapabilityRegistry {
    capabilities: HashMap<CapabilityId, CapabilityInfo>,
    providers:    HashMap<ProviderId, ProviderInfo>,
    bindings:     HashMap<CapabilityId, Vec<Binding>>,

    fn is_capability_available(cap_id) -> bool
    fn get_user_capabilities(user_role) -> Vec<CapabilityInfo>
    fn get_bindings(cap_id) -> Vec<Binding>
    fn reload_from_db() -> Result<()>   // 热更新
}
```

### 5.3 用户查询自己可用能力

```
GET /v1/user/capabilities
Authorization: Bearer <jwt>

Response 200:
{
  "capabilities": [
    {
      "id": "chat",
      "display_name": "智能对话",
      "category": "ai",
      "available_models": ["gpt-4o", "gpt-4o-mini", "deepseek-chat"],
      "quota": { "used": 12000, "limit": 100000, "unit": "tokens" }
    },
    {
      "id": "text.translate",
      "display_name": "文本翻译",
      "category": "translate",
      "supported_languages": ["zh-CN", "en-US", "ja", "ko", ...],
      "quota": { "used": 5, "limit": 120, "unit": "minutes" }
    },
    ...
  ]
}
```

> 用户看到的是**能力**，不是 Provider。哪家厂商在背后提供服务，用户不需要知道。

---

## 6. 路由引擎 (CapabilityRouter)

### 6.1 路由决策流程

```
请求到达 Handler
       │
       ▼
提取: capability_id + tenant_id + 请求参数 (model, lang_pair 等)
       │
       ▼
CapabilityRouter.resolve(capability_id, tenant_id, params)
       │
       ├─ 1. 从 Registry 获取该 capability 的所有 bindings
       │
       ├─ 2. 过滤: is_enabled == true
       │
       ├─ 3. 条件匹配: match_condition 与请求参数对比
       │     - lang_pair: "zh-*" 匹配 "zh-CN→en"
       │     - model: "gpt-4o*" 匹配 "gpt-4o-mini"
       │     - tenant_id: 特定租户绑定
       │
       ├─ 4. 按 priority 排序 (数值小优先)
       │
       ├─ 5. 在同 priority 组内按 weight 加权随机 (灰度)
       │
       └─ 6. 返回选中的 Provider 实例
              │
              ▼
         调用 Adapter.translate(canonical_dto)
```

### 6.2 路由规则示例

| 规则 | 效果 |
|------|------|
| `text.translate, *, *, azure.translator, priority=100, weight=80` | 默认 80% 流量走 Azure |
| `text.translate, *, *, tencent.tmt, priority=100, weight=20` | 默认 20% 流量走腾讯 (灰度) |
| `text.translate, *, lang_pair=*-yue, tencent.tmt, priority=50, weight=100` | 粤语翻译强制走腾讯 (Azure 不支持) |
| `chat, tenant=vip-corp, model=gpt-4o*, azure.openai, priority=100, weight=100` | VIP 租户的 GPT-4o 走 Azure |
| `chat, *, model=deepseek*, self-hosted.deepseek, priority=100, weight=100` | DeepSeek 模型走自建 |

### 6.3 降级策略

当选中的 Provider 返回 `ProviderError::Upstream` 或 `ProviderError::RateLimited` 时：

1. 标记该 Provider 为 **degraded**（短时间内降低权重）。
2. 如果同 capability 有其他可用 Provider → **自动切换**，前端无感。
3. 如果无备选 → 返回 `503 Service Unavailable`。
4. 所有降级事件写入审计日志 + metrics，方便告警。

---

## 7. 管理员操作流程

### 7.1 录入新厂商 (以腾讯翻译为例)

```
步骤 1: 录入 Provider
  PUT /v1/admin/providers/tencent.tmt
  {
    "vendor": "tencent",
    "display_name": "腾讯机器翻译",
    "config_json": { "region": "ap-guangzhou" }
  }

步骤 2: 录入凭据
  PUT /v1/admin/credentials/tencent.tmt.secret_id
  { "provider_id": "tencent.tmt", "key": "secret_id", "value": "AKIDxxxx" }

  PUT /v1/admin/credentials/tencent.tmt.secret_key
  { "provider_id": "tencent.tmt", "key": "secret_key", "value": "xxxxxx" }

步骤 3: 绑定到能力 + 定义路由规则
  PUT /v1/admin/routing/new
  {
    "capability_id": "text.translate",
    "provider_id": "tencent.tmt",
    "is_enabled": true,
    "priority": 100,
    "weight": 20,
    "match_condition": { "lang_pair": "*" }
  }

步骤 4: 启用 Provider
  PUT /v1/admin/providers/tencent.tmt/toggle
  { "is_enabled": true }
```

> **全程通过 API**，无需重启、无需改代码。

### 7.2 禁用某个能力

```
PUT /v1/admin/capabilities/video.generate
{ "is_enabled": false }
```

→ 立即生效，用户侧该能力从可用列表消失，调用返回 `404 Not Found`。

### 7.3 切换厂商

```
-- 方法一：调整权重 (灰度迁移)
UPDATE capability_provider_bindings
SET weight = 0
WHERE capability_id = 'text.translate' AND provider_id = 'azure.translator';

UPDATE capability_provider_bindings
SET weight = 100
WHERE capability_id = 'text.translate' AND provider_id = 'tencent.tmt';

-- 方法二：直接禁用旧 Provider
PUT /v1/admin/providers/azure.translator/toggle
{ "is_enabled": false }
```

> **前端 0 改动，用户无感。**

---

## 8. 厂商特殊处理清单

| 厂商 | 鉴权方式 | Rust SDK | 特殊处理 |
|------|---------|---------|---------|
| **Azure OpenAI** | api-key 或 AAD Bearer | `reqwest` + OpenAI 兼容协议 | model → deployment 映射 |
| **Azure Translator** | subscription key + region | `reqwest` | — |
| **Azure Speech** | subscription key 或 AAD token | `tokio-tungstenite` (WS) | 二进制音频帧协议 |
| **Azure DALL-E / Sora** | api-key | `reqwest` | 异步轮询 (submit → poll) |
| **腾讯 TMT** | TC3-HMAC-SHA256 签名 | **自实现** (~80 行) | 语言码映射 |
| **腾讯混元** | TC3 签名 | 自实现 | SSE 格式略有差异 |
| **腾讯 ASR** | TC3 签名 + WS | 自实现 | 实时流需拼接翻译 |
| **阿里 DashScope** | Bearer api-key | `reqwest` + OpenAI 兼容 | model 名不同 |
| **阿里翻译** | 阿里云签名 v3 | 自实现 | — |
| **DeepSeek / Together / Groq** | Bearer api-key | `OpenAiCompatChat` 通用 | 仅改 base_url |
| **自建 vLLM / Ollama** | 无鉴权或 Bearer | `OpenAiCompatChat` 通用 | 内网 base_url |

---

## 9. 安全要点（多 Provider 特有）

| 风险 | 缓解方案 |
|------|---------|
| 跨厂商凭据混淆 | 凭据命名空间：`provider:{vendor}:{field}`，Adapter 只能访问自己的命名空间 |
| 错误信息泄漏厂商身份 | 错误响应只返回 Canonical code (`upstream_unavailable`)，厂商详情仅写审计日志 |
| 客户端指定 provider | **完全忽略**客户端的 provider 参数；路由只看 CapabilityRouter + 规则 |
| Provider 密钥轮换 | `provider_credentials.version` 字段，新旧 Key 并行 7 天 |
| 单 Provider 故障雪崩 | 降级策略自动切换备选，Circuit Breaker 短时间内跳过故障 Provider |
| 配额穿透到上游 | 本地配额检查在**调上游之前**，防止无效请求浪费上游 quota |

---

## 10. 新增 Provider 的标准检查清单

开发工程师每新增一个 Adapter，需确认：

- [ ] 实现对应 trait（`ChatProvider` / `TextTranslator` / ...）
- [ ] 只接受/返回 Canonical DTO，无厂商类型外溢
- [ ] 凭据通过 `CredentialBroker` 获取，不硬编码
- [ ] 语言码通过 `lang` 模块双向映射
- [ ] 厂商错误映射到 `ProviderError` 5 种 Canonical 错误
- [ ] 日志中不打印密钥（`secrecy::SecretString` 强制）
- [ ] 所有 HTTP 调用设置超时 (30s) 和重试 (≤2 次指数退避)
- [ ] 在 `providers/src/registry.rs` 注册新 Adapter
- [ ] 编写 Adapter 单元测试（用 `wiremock` mock 上游）
- [ ] 在 `migrations/` 中增加默认 Provider 定义 SQL
- [ ] 更新本文档的 Adapter 矩阵
