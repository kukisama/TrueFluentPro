# 前端零改动、多 Provider 后端适配 —— 同时支持腾讯翻译 + 微软 Speech 翻译

> 承接 `落地设计-多云AI网关.md`。本篇专门回答：
> **"如果上游 API 形态完全不一样（腾讯翻译 vs 微软 Speech 翻译），我希望客户端只写一套代码、所有差异都让后端吃掉，怎么设计？"**

---

## 0. 一句话答案

> **前端只认一套"我方契约"（Canonical Contract），不认任何厂商。后端负责"契约 ↔ 厂商" 的双向翻译，并把厂商选择权完全移到后端配置里。**
>
> 这样：
> - 前端代码 0 改动，**永远**只调用 `POST /v1/translate`、`WS /v1/realtime/translate`。
> - 加一个新厂商 = 后端加一个 `Adapter` 类 + 一行路由配置。
> - 切换厂商 = 后端改一条数据库记录或环境变量，**前端无感**。

这就是经典的 **Anti-Corruption Layer + Strategy + Capability-based Routing** 三件套。下文把它落到代码级别。

---

## 1. 难点拆解：为什么"只改 URL"不够

腾讯翻译和微软 Speech 翻译在协议层面差异极大：

| 维度 | 腾讯 TMT `TextTranslate` | 微软 Speech Translation |
| --- | --- | --- |
| 协议 | HTTPS POST + JSON，TC3-HMAC 签名 | **WebSocket** 流式，二进制音频帧 |
| 输入 | 一段文本 | 16k/16bit/单声道 PCM 音频块 |
| 输出 | 一段译文 | 流式 Partial + Final 字幕，含时间戳 |
| 鉴权 | SecretId + SecretKey + TC3 签名 | Subscription Key 或 AAD token |
| 错误码 | `Code:"AuthFailure.SignatureFailure"` | HTTP 401 / WS close code 1007 |
| 语言代码 | `zh` / `en` / `ja` | `zh-CN` / `en-US` / `ja-JP` |

**如果把这种差异穿透到客户端，那就是灾难**：每加一家厂商都要改 UI、改 ViewModel、改本地缓存格式。本文给出的方案是：**让客户端永远只看到自家协议**，差异全部在后端 Adapter 里消化。

---

## 2. 核心设计：三段式 + 能力路由

```
       ┌────────────────────────────────────────────────────┐
       │  前端 (Avalonia / MSAL.NET)                         │
       │  ──────────────────────────────────────────────── │
       │  ITranslationService    ← 唯一接口，永远不变       │
       │     ├─ TranslateText(text, src, tgt)              │
       │     └─ TranslateSpeechStream(audio, src, tgt)     │
       └──────────────┬─────────────────────────────────────┘
                      │  Canonical Contract (你自己定的)
                      │  POST /v1/translate
                      │  WS   /v1/realtime/translate
                      ▼
       ┌────────────────────────────────────────────────────┐
       │  后端网关                                           │
       │  ──────────────────────────────────────────────── │
       │  ① Endpoint: 解析 Canonical 请求                   │
       │  ② Capability Router: 按能力 + 租户/灰度选 Adapter │
       │     - text.translate.zh→en  → TencentTextAdapter  │
       │     - speech.translate.live → AzureSpeechAdapter  │
       │  ③ Adapter (Anti-Corruption Layer)                │
       │     - 输入：Canonical DTO                          │
       │     - 输出：厂商私有协议 + 鉴权                    │
       │     - 反向把厂商响应翻译回 Canonical Chunk         │
       │  ④ Provider SDK / 自签 HTTP / WS 客户端            │
       └──────────────┬─────────────────────────────────────┘
                      ▼
              腾讯 TMT API           微软 Speech WS
```

**三个不可妥协的边界**：
1. **客户端 ↔ 网关**：只走 Canonical Contract（你自己的协议）。
2. **网关 ↔ Adapter**：只走 Canonical DTO（内部强类型对象）。
3. **Adapter ↔ 上游**：才会出现厂商私有协议，但**绝不**外溢。

---

## 3. Canonical Contract（你定的"我方协议"）

设计原则：**取所有厂商能力的并集语义，剪掉差异，只暴露稳定字段**。

### 3.1 文本翻译 — REST

```http
POST /v1/translate
Authorization: ******
Content-Type: application/json

{
  "kind": "text",
  "source_lang": "auto",     // BCP-47 或 "auto"
  "target_lang": "en",
  "text": "你好，世界"
}
```

```http
200 OK
{
  "translation": "Hello, world",
  "detected_source_lang": "zh",
  "provider_hint": "tencent",   // 仅 debug/审计可见，前端忽略
  "request_id": "01J..."
}
```

> **关键约定**：
> - 语言代码统一用 BCP-47（`zh`、`en-US`、`ja`），后端负责映射到厂商私有码。
> - `provider_hint` 是调试字段，**前端不能**依赖它做分支逻辑——一旦依赖就破契约。
> - 错误统一映射成自家 `code`：`auth_required` / `quota_exceeded` / `unsupported_language` / `upstream_unavailable` / `internal`，把厂商错误细节藏在 `details` 里。

### 3.2 语音翻译 — WebSocket

```
WS  /v1/realtime/translate
Sec-WebSocket-Protocol: bearer.<jwt>, tfp.translate.v1
Query: ?source_lang=zh-CN&target_lang=en-US&format=pcm16k
```

**消息帧类型（统一 JSON 信令 + 二进制音频）**：

```jsonc
// → Server: 第一帧（控制）
{ "type": "start",
  "source_lang": "zh-CN",
  "target_lang": "en-US",
  "audio": { "encoding": "pcm", "sample_rate": 16000, "channels": 1 }
}

// → Server: 二进制 PCM 块（可任意切片）
<binary>

// → Server: 结束
{ "type": "end" }

// ← Client: 实时部分结果
{ "type": "partial", "src": "你好世", "dst": "Hello wor", "ts_ms": 1234 }

// ← Client: 最终段结果（带时间戳，可入字幕）
{ "type": "final", "src": "你好世界", "dst": "Hello world",
  "start_ms": 0, "end_ms": 2200, "speaker": "S1" }

// ← Client: 错误（统一码）
{ "type": "error", "code": "upstream_unavailable", "message": "..." }

// ← Client: 心跳
{ "type": "pong" }
```

> **为什么走 WS 而不是 SSE？** 语音翻译需要**双向**流（前端持续推音频、后端持续吐字幕）。SSE 只能服务端→客户端单向，HTTP/2 的 trailers 兼容性又差。WS 是工业标准方案。

### 3.3 客户端接口（C#）

```csharp
// Services/ITranslationService.cs        ← 这就是前端的"全部"代码面
public interface ITranslationService
{
    Task<TranslationResult> TranslateTextAsync(
        string text, string sourceLang, string targetLang, CancellationToken ct = default);

    Task<IAsyncDisposable> StartLiveTranslationAsync(
        LiveTranslationOptions opts,
        Func<TranslationChunk, Task> onChunk,
        CancellationToken ct = default);
}

public record TranslationResult(string Translation, string? DetectedSourceLang);
public record LiveTranslationOptions(string SourceLang, string TargetLang, int SampleRate = 16000);
public abstract record TranslationChunk
{
    public sealed record Partial(string Src, string Dst, int TsMs) : TranslationChunk;
    public sealed record Final(string Src, string Dst, int StartMs, int EndMs, string? Speaker) : TranslationChunk;
}
```

> **里程碑**：这套接口**一次定义，永久不变**。腾讯/微软/火山/阿里来多少家，都不影响它。前端唯一的"差异感知点"就是设置里那个"翻译服务"下拉框 —— 但下拉框的 **value 也只是一个字符串**，传给后端，前端不解释。

---

## 4. 后端 Adapter 落地（Rust 版，沿用上一篇的 axum 栈）

### 4.1 `Translator` trait（已在落地设计文档出现，这里展开真实可运行代码）

```rust
use async_trait::async_trait;
use futures::stream::BoxStream;

/// Canonical 内部 DTO — 所有 Adapter 的输入输出
#[derive(Debug, Clone)]
pub struct TextTranslateRequest {
    pub source_lang: LangCode,    // 强类型，已规范化
    pub target_lang: LangCode,
    pub text: String,
    pub tenant_id: TenantId,
    pub request_id: RequestId,
}

#[derive(Debug, Clone)]
pub struct TextTranslateResponse {
    pub translation: String,
    pub detected_source_lang: Option<LangCode>,
}

#[derive(Debug, Clone)]
pub struct LiveTranslateStart {
    pub source_lang: LangCode,
    pub target_lang: LangCode,
    pub sample_rate: u32,
    pub tenant_id: TenantId,
}

#[derive(Debug, Clone)]
pub enum LiveChunkOut {
    Partial { src: String, dst: String, ts_ms: u32 },
    Final   { src: String, dst: String, start_ms: u32, end_ms: u32, speaker: Option<String> },
}

#[derive(Debug)]
pub enum LiveChunkIn {
    Audio(bytes::Bytes),
    End,
}

#[async_trait]
pub trait TextTranslator: Send + Sync {
    fn id(&self) -> &'static str;
    async fn translate(&self, req: TextTranslateRequest)
        -> Result<TextTranslateResponse, ProviderError>;
}

#[async_trait]
pub trait LiveSpeechTranslator: Send + Sync {
    fn id(&self) -> &'static str;
    /// 返回一个"上传任务 + 下行 Stream"，由 endpoint 把音频喂给前者，把后者推回客户端
    async fn start(
        &self,
        start: LiveTranslateStart,
        audio_in: BoxStream<'static, LiveChunkIn>,
    ) -> Result<BoxStream<'static, Result<LiveChunkOut, ProviderError>>, ProviderError>;
}
```

### 4.2 腾讯文本翻译 Adapter（私有协议 ↔ Canonical）

```rust
pub struct TencentTextTranslator {
    http: reqwest::Client,
    region: String,
    creds: Arc<dyn SecretSource>,
}

#[async_trait]
impl TextTranslator for TencentTextTranslator {
    fn id(&self) -> &'static str { "tencent.tmt" }

    async fn translate(&self, req: TextTranslateRequest)
        -> Result<TextTranslateResponse, ProviderError>
    {
        // 1. Canonical 语言码 → 腾讯私有码
        let src = lang::to_tencent(&req.source_lang); // "auto"/"zh"/"en"/...
        let tgt = lang::to_tencent(&req.target_lang);

        // 2. 取凭据（永远从 broker 拿，不缓存在 Adapter 里）
        let secret_id  = self.creds.must_get(
            &format!("tenant:{}:provider:tencent:secret_id", req.tenant_id)).await?;
        let secret_key = self.creds.must_get(
            &format!("tenant:{}:provider:tencent:secret_key", req.tenant_id)).await?;

        // 3. 拼请求体（厂商私有 schema）
        let body = serde_json::json!({
            "SourceText": req.text,
            "Source": src,
            "Target": tgt,
            "ProjectId": 0,
        });

        // 4. TC3-HMAC-SHA256 签名（80 行实现，见落地设计文档 §FAQ Q4）
        let signed = tc3::sign(
            "POST", "tmt.tencentcloudapi.com", "/", "TextTranslate", "2018-03-21",
            &self.region, &body, secret_id.expose_secret(), secret_key.expose_secret(),
        );

        // 5. 调上游
        let resp = self.http.post("https://tmt.tencentcloudapi.com/")
            .headers(signed.headers)
            .body(signed.body)
            .send().await?
            .error_for_status()?
            .json::<TencentResponse>().await?;

        // 6. 厂商错误 → 我方统一错误码
        if let Some(err) = resp.response.error {
            return Err(map_tencent_error(err));   // AuthFailure.* → BadCredential 等
        }

        // 7. 厂商响应 → Canonical
        Ok(TextTranslateResponse {
            translation: resp.response.target_text.unwrap_or_default(),
            detected_source_lang: resp.response.source.and_then(|s| lang::from_tencent(&s)),
        })
    }
}
```

### 4.3 微软 Speech Live 翻译 Adapter（WS ↔ Canonical）

```rust
pub struct AzureSpeechLiveTranslator {
    creds: Arc<dyn SecretSource>,
    region: String,
}

#[async_trait]
impl LiveSpeechTranslator for AzureSpeechLiveTranslator {
    fn id(&self) -> &'static str { "azure.speech.live" }

    async fn start(
        &self,
        start: LiveTranslateStart,
        mut audio_in: BoxStream<'static, LiveChunkIn>,
    ) -> Result<BoxStream<'static, Result<LiveChunkOut, ProviderError>>, ProviderError>
    {
        // 1. 取 token（优先 AAD，回退 subscription key）
        let token = acquire_speech_token(&*self.creds, &start.tenant_id, &self.region).await?;

        // 2. 组 Microsoft 私有 WS URL
        let url = format!(
            "wss://{region}.s2s.speech.microsoft.com/speech/translation/cognitiveservices/v1\
             ?language={src}&to={tgt}&format=detailed",
            region = self.region,
            src = lang::to_azure(&start.source_lang),  // zh-CN / en-US ...
            tgt = lang::to_azure(&start.target_lang),
        );

        let (ws_stream, _) = tokio_tungstenite::connect_async(
            tokio_tungstenite::tungstenite::http::Request::builder()
                .uri(&url)
                .header("Authorization", format!("Bearer {}", token.expose_secret()))
                .body(()).unwrap()
        ).await.map_err(|e| ProviderError::Upstream(e.to_string()))?;

        let (mut up_sink, up_stream) = ws_stream.split();

        // 3. 上行：把 Canonical 音频流转成 MS 协议帧
        tokio::spawn(async move {
            // MS 要求先发一个 'speech.config' JSON 帧
            up_sink.send(ms_config_frame(&start)).await.ok();
            while let Some(chunk) = audio_in.next().await {
                match chunk {
                    LiveChunkIn::Audio(bytes) => {
                        // 包成 MS audio frame (path:audio + binary body)
                        up_sink.send(ms_audio_frame(bytes)).await.ok();
                    }
                    LiveChunkIn::End => {
                        up_sink.send(ms_end_frame()).await.ok();
                        break;
                    }
                }
            }
        });

        // 4. 下行：MS 私有事件 → Canonical Chunk
        let out = up_stream.filter_map(|msg| async move {
            let msg = msg.ok()?;
            let ev: MsTranslationEvent = parse_ms_frame(msg)?;
            match ev {
                MsTranslationEvent::Partial { text, translation, offset_ms, .. } =>
                    Some(Ok(LiveChunkOut::Partial {
                        src: text, dst: translation, ts_ms: offset_ms })),
                MsTranslationEvent::Final { text, translation, offset_ms, duration_ms, speaker } =>
                    Some(Ok(LiveChunkOut::Final {
                        src: text, dst: translation,
                        start_ms: offset_ms, end_ms: offset_ms + duration_ms,
                        speaker })),
                MsTranslationEvent::Error(code, m) =>
                    Some(Err(map_azure_error(code, m))),
                _ => None,
            }
        }).boxed();

        Ok(out)
    }
}
```

### 4.4 Capability Router —— 路由不在前端，在后端

```rust
/// 路由决策的输入：租户 + 能力名 + 语言对
/// 输出：具体 Provider 实现
pub struct CapabilityRouter {
    text_translators: HashMap<&'static str, Arc<dyn TextTranslator>>,
    live_translators: HashMap<&'static str, Arc<dyn LiveSpeechTranslator>>,
    rules: TenantRoutingRules,   // 从 DB 加载的规则
}

impl CapabilityRouter {
    pub fn pick_text(&self, tenant: &TenantId, src: &LangCode, tgt: &LangCode)
        -> Arc<dyn TextTranslator>
    {
        // 规则示例：
        //   tenant=A → 默认 azure.translator
        //   tenant=B → 默认 tencent.tmt
        //   语言对包含 "yue" (粤语) → 强制 tencent.tmt（azure 不支持）
        //   灰度：5% 流量 → tencent.tmt
        let id = self.rules.resolve_text(tenant, src, tgt);
        self.text_translators[id].clone()
    }

    pub fn pick_live(&self, tenant: &TenantId, src: &LangCode, tgt: &LangCode)
        -> Arc<dyn LiveSpeechTranslator>
    {
        // 当前只有 azure；将来加 tencent.asr+tmt 拼接版时只改这里
        let id = self.rules.resolve_live(tenant, src, tgt);
        self.live_translators[id].clone()
    }
}
```

> **`TenantRoutingRules`** 建议存 Postgres：
> ```sql
> CREATE TABLE provider_routing (
>   tenant_id   TEXT NOT NULL,
>   capability  TEXT NOT NULL,         -- 'text.translate' / 'live.translate'
>   match_lang  TEXT,                  -- 'zh-*' / '*-yue' / NULL
>   provider_id TEXT NOT NULL,         -- 'tencent.tmt' / 'azure.translator'
>   weight      INT  NOT NULL DEFAULT 100,
>   PRIMARY KEY (tenant_id, capability, match_lang, provider_id)
> );
> ```
> 改厂商就是一条 `UPDATE`，前端 0 感知。

### 4.5 Endpoint —— 把 Canonical 和 Adapter 粘起来

```rust
async fn translate_text(
    State(s): State<AppState>,
    Extension(claims): Extension<AadClaims>,
    Json(req): Json<api::TextRequest>,
) -> Result<Json<api::TextResponse>, ApiError> {
    let canonical = TextTranslateRequest {
        source_lang: LangCode::parse(&req.source_lang)?,
        target_lang: LangCode::parse(&req.target_lang)?,
        text: req.text,
        tenant_id: claims.tid.clone().into(),
        request_id: RequestId::new(),
    };

    let provider = s.router.pick_text(
        &canonical.tenant_id, &canonical.source_lang, &canonical.target_lang);

    let resp = provider.translate(canonical).await?;

    Ok(Json(api::TextResponse {
        translation: resp.translation,
        detected_source_lang: resp.detected_source_lang.map(|l| l.to_string()),
        provider_hint: provider.id().to_string(),
    }))
}

async fn translate_live_ws(
    ws: WebSocketUpgrade,
    State(s): State<AppState>,
    Extension(claims): Extension<AadClaims>,
    Query(q): Query<LiveQuery>,
) -> impl IntoResponse {
    ws.protocols(["tfp.translate.v1"])
      .on_upgrade(move |socket| handle_live(socket, s, claims, q))
}

async fn handle_live(socket: WebSocket, s: AppState, claims: AadClaims, q: LiveQuery) {
    let (mut ws_tx, mut ws_rx) = socket.split();

    let start = LiveTranslateStart {
        source_lang: q.source_lang.parse().unwrap(),
        target_lang: q.target_lang.parse().unwrap(),
        sample_rate: q.sample_rate.unwrap_or(16000),
        tenant_id: claims.tid.into(),
    };

    // 1. 把客户端 WS 入消息流转成 Adapter 期望的 LiveChunkIn 流
    let (audio_tx, audio_rx) = tokio::sync::mpsc::channel(64);
    let audio_in = tokio_stream::wrappers::ReceiverStream::new(audio_rx).boxed();

    tokio::spawn(async move {
        while let Some(Ok(msg)) = ws_rx.next().await {
            match msg {
                Message::Binary(b) => { let _ = audio_tx.send(LiveChunkIn::Audio(b)).await; }
                Message::Text(t) if t.contains("\"end\"") => {
                    let _ = audio_tx.send(LiveChunkIn::End).await; break;
                }
                Message::Close(_) => break,
                _ => {}
            }
        }
    });

    // 2. 选 Adapter
    let provider = s.router.pick_live(&start.tenant_id, &start.source_lang, &start.target_lang);

    // 3. 启动并把下行 Canonical Chunk 序列化回客户端
    match provider.start(start, audio_in).await {
        Ok(mut out) => {
            while let Some(item) = out.next().await {
                let frame = match item {
                    Ok(LiveChunkOut::Partial { src, dst, ts_ms }) =>
                        json!({"type":"partial","src":src,"dst":dst,"ts_ms":ts_ms}),
                    Ok(LiveChunkOut::Final { src, dst, start_ms, end_ms, speaker }) =>
                        json!({"type":"final","src":src,"dst":dst,
                               "start_ms":start_ms,"end_ms":end_ms,"speaker":speaker}),
                    Err(e) => json!({"type":"error","code":e.canonical_code(),
                                     "message":e.to_string()}),
                };
                if ws_tx.send(Message::Text(frame.to_string())).await.is_err() { break; }
            }
        }
        Err(e) => {
            let _ = ws_tx.send(Message::Text(json!({
                "type":"error","code":e.canonical_code(),"message":e.to_string()
            }).to_string())).await;
        }
    }
    let _ = ws_tx.close().await;
}
```

---

## 5. 客户端怎么写（一次性，永远不动）

仓库里现存 `Services/Cloud/CloudApiClient.cs` 已经是 baseUrl + JWT 的样子。新增 `TranslationService`：

```csharp
public sealed class TranslationService : ITranslationService
{
    private readonly HttpClient _http;
    private readonly ICloudAuthService _auth;
    private readonly Uri _baseUri;       // 来自 CloudSettings.GatewayBaseUrl

    public async Task<TranslationResult> TranslateTextAsync(
        string text, string src, string tgt, CancellationToken ct = default)
    {
        var token = await _auth.AcquireAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_baseUri, "/v1/translate"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new {
            kind = "text", source_lang = src, target_lang = tgt, text
        });
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<TextResp>(cancellationToken: ct);
        return new TranslationResult(dto!.translation, dto.detected_source_lang);
    }

    public async Task<IAsyncDisposable> StartLiveTranslationAsync(
        LiveTranslationOptions opts,
        Func<TranslationChunk, Task> onChunk,
        CancellationToken ct = default)
    {
        var token = await _auth.AcquireAccessTokenAsync(ct);
        var ws = new ClientWebSocket();
        // WS 不能走 Authorization header → 走 Sec-WebSocket-Protocol 子协议
        ws.Options.AddSubProtocol($"bearer.{token}");
        ws.Options.AddSubProtocol("tfp.translate.v1");
        var url = new UriBuilder(_baseUri) {
            Scheme = _baseUri.Scheme == "https" ? "wss" : "ws",
            Path = "/v1/realtime/translate",
            Query = $"source_lang={opts.SourceLang}&target_lang={opts.TargetLang}&sample_rate={opts.SampleRate}"
        }.Uri;
        await ws.ConnectAsync(url, ct);

        // 启动接收循环
        _ = Task.Run(async () => {
            var buf = new byte[8192];
            while (ws.State == WebSocketState.Open) {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                var json = JsonDocument.Parse(buf.AsMemory(0, r.Count));
                var type = json.RootElement.GetProperty("type").GetString();
                TranslationChunk? chunk = type switch {
                    "partial" => new TranslationChunk.Partial(
                        json.RootElement.GetProperty("src").GetString()!,
                        json.RootElement.GetProperty("dst").GetString()!,
                        json.RootElement.GetProperty("ts_ms").GetInt32()),
                    "final"   => new TranslationChunk.Final(
                        json.RootElement.GetProperty("src").GetString()!,
                        json.RootElement.GetProperty("dst").GetString()!,
                        json.RootElement.GetProperty("start_ms").GetInt32(),
                        json.RootElement.GetProperty("end_ms").GetInt32(),
                        json.RootElement.TryGetProperty("speaker", out var sp) ? sp.GetString() : null),
                    _ => null,
                };
                if (chunk is not null) await onChunk(chunk);
            }
        }, ct);

        return new LiveSession(ws);  // 实现 SendAudio + DisposeAsync
    }
}
```

> **关键**：未来加腾讯实时语音翻译、火山翻译、阿里翻译……**这个文件一行都不用改**。

---

## 6. "改一家厂商"完整流程演示

### 场景：从微软 Speech 切到"腾讯 ASR + 腾讯翻译"组合做实时语音翻译

**后端要做的**：
1. 新增文件 `providers/src/live/tencent_asr_tmt.rs`，实现 `LiveSpeechTranslator`：
   - 内部用腾讯实时 ASR WS 把 PCM → 中文文本片段；
   - 每条片段调腾讯 TMT → 译文；
   - 包成 `LiveChunkOut::Partial / Final`。
2. 在 `main.rs` 注册：
   ```rust
   live_translators.insert("tencent.live", Arc::new(TencentLiveTranslator::new(...)));
   ```
3. 数据库改路由：
   ```sql
   UPDATE provider_routing
      SET provider_id = 'tencent.live'
    WHERE tenant_id = 'demo' AND capability = 'live.translate';
   ```

**前端要做的**：✅ **什么都不用做**。

**用户感知**：除了字幕风格（标点、断句习惯）变了，UI 没区别。

---

## 7. 安全要点（多 Provider 特有）

| 风险 | 缓解 |
| --- | --- |
| **跨厂商凭据混淆** | Secret key 命名空间用 `tenant:{tid}:provider:{vendor}:{field}`，Adapter 只能拿自己的命名空间 |
| **错误信息泄漏厂商身份** | 错误响应只返回 Canonical code，详细 vendor 错误**仅**写审计日志，不回传客户端 |
| **客户端尝试越权指定 provider** | 即便客户端在 body 里塞 `"provider":"xxx"`，后端**完全忽略**这个字段；路由只看 `Capability Router + 租户规则` |
| **WS 子协议泄漏 token** | `bearer.<jwt>` 子协议只在握手时通过 TLS 传输，握手后就废弃；JWT 的 `aud` 必须包含 `wss://gateway/...` |
| **音频源越权** | 后端校验 `samplerate ≤ 48000` 且单帧 ≤ 32 KiB，防止打爆上游 quota |
| **失败降级** | Adapter 返回 `upstream_unavailable` 时，Capability Router 可按规则**自动**切到备用 provider 并打 metrics，前端无感 |
| **审计完备** | 每次请求记录 `tenant / user_oid / provider_id / latency / canonical_code / vendor_code / chars / audio_ms`，方便事后追责和对账 |

---

## 8. 测试策略（让 Adapter 不会回归）

1. **契约测试（必须）**：在 `crates/api-contract` 里维护 OpenAPI + AsyncAPI（WS）规范，前后端都从它生成代码/校验，破契约 CI 直接红。
2. **Adapter 单元测试**：用 `wiremock` mock 上游响应，覆盖：成功、auth 失败、限流、超时、半响应、unicode、零长度。
3. **黄金路径集成测试**：起两个 Adapter（fake + real），用同一组 Canonical 输入断言**输出不变**。这保证"换厂商不影响客户端"。
4. **Property-based 测试**：用 `proptest` 随机生成语言码 + 文本，检验 `lang::to_xxx ↔ lang::from_xxx` 互逆。
5. **客户端只测自家协议**：客户端永远不知道腾讯/微软的存在，测试用本地 mock 网关即可。

---

## 9. 与现有 .NET 后端的快速过渡（不重写 Rust 也能用）

如果暂时不上 Rust，**完全相同的设计**可以直接落到 `backend/TrueFluentPro.Api`：

```
backend/TrueFluentPro.Api/
├── Contracts/                       ← Canonical DTO
│   ├── TextTranslateRequest.cs
│   └── LiveChunk.cs
├── Providers/
│   ├── ITextTranslator.cs
│   ├── ILiveSpeechTranslator.cs
│   ├── Tencent/
│   │   ├── TencentTextTranslator.cs        // HttpClient + TC3 签名
│   │   └── TencentLiveTranslator.cs
│   └── Azure/
│       ├── AzureTextTranslator.cs
│       └── AzureSpeechLiveTranslator.cs    // ClientWebSocket
├── Routing/
│   └── CapabilityRouter.cs                 // 读 provider_routing 表
└── Endpoints/
    ├── TranslateEndpoints.cs               // POST /v1/translate
    └── LiveTranslateEndpoint.cs            // WS /v1/realtime/translate
```

DI（Program.cs）：
```csharp
builder.Services.AddSingleton<ITextTranslator, TencentTextTranslator>();
builder.Services.AddSingleton<ITextTranslator, AzureTextTranslator>();
builder.Services.AddSingleton<ILiveSpeechTranslator, AzureSpeechLiveTranslator>();
builder.Services.AddSingleton<CapabilityRouter>();

app.MapPost("/v1/translate", TranslateEndpoints.TextAsync)
   .RequireAuthorization();
app.MapGet("/v1/realtime/translate", LiveTranslateEndpoint.WebSocketAsync)
   .RequireAuthorization();
```

> **结论**：Rust 与 .NET 在这个设计下**只是语言不同，架构 100% 一致**。前端 `ITranslationService` 一次写完，后端可以随便换语言、换厂商。

---

## 10. 给开发的 Definition of Done

- [ ] Canonical Contract（OpenAPI + AsyncAPI/WS schema）已发布到 `docs/contracts/`，前后端 PR 必须更新它。
- [ ] 至少 2 个 `TextTranslator` 实现（Azure + Tencent）+ 1 个 `LiveSpeechTranslator` 实现（Azure），且都通过同一组黑盒 Canonical 测试。
- [ ] `provider_routing` 表 + 管理 API（按租户/语言/灰度切换）。
- [ ] 错误码映射表 `vendor_error → canonical_code` 在代码里集中维护，单元测试覆盖每个分支。
- [ ] 客户端 `ITranslationService` 实现 + 单元测试（mock 网关），**0 处出现 "tencent" / "azure" 字符串**。
- [ ] 审计日志、metrics、降级规则上线 dashboard。

---

> **一句话总结**：客户端只认"你家的协议"，不认厂商；厂商差异由后端 Adapter 吃掉；切换厂商靠路由表，不靠改代码。这就是"前端零改动、后端做苦力"的标准范式。
