# 批次 4 施工单
> 日期：2026-04-29 | Phase 1 — 核心管线补全 | 进度 4/6 | 依赖：batch-3 ✅

## 目标

完善 AI 聊天 Provider（多 URL 候选回退、reasoning 链三字段兼容、image_generation tool 注入）和端点连通性测试（Video 能力测试实装、URL 模板系统改进）。

## Spec 来源
| 文档 | 相关段落 |
|------|---------|
| Services/AiInsightService.md | §流程2 SendRequestAsync 多 URL 回退; §流程4 BuildRequestBody 三协议; §流程5/6 SSE 解析; §推理链兼容性 |
| Services/EndpointBatchTestService.md | §流程6 TestVideoAsync; §流程3 TestTextAsync; §流程2 RunPrimaryThenFallbackAsync |
| Services/EndpointBatchTestModels-and-Interface.md | EndpointBatchTestStatus / LiveState 枚举; EndpointBatchTestItem DTO |

## Rust3 现状

**AI 聊天 (openai_chat.rs)**:
- ✅ complete() — 非流式，Responses API + Chat Completions 双协议
- ✅ complete_stream() — 流式，SSE 解析基础功能
- ✅ build_url() — 单 URL（无候选回退）
- ❌ 无多 URL 候选回退（仅 1 条 URL）
- ❌ reasoning 只检测 `reasoning_content` 一个字段（Spec 要求 3 字段 x 2 格式）
- ❌ 无 image_generation tool 注入
- ❌ 无 reasoning_effort / reasoning summary 支持
- ❌ 非流式 Responses API 解析不完整（缺 reasoning summary）

**端点测试 (test_runner.rs + test.rs)**:
- ✅ test_text() — 文字探活可用
- ✅ test_image() — 图片探活可用
- ✅ build_url_candidates() — 按 endpoint_type + capability 构建候选
- ✅ test_single_capability() — 单项测试 + URL 回退
- ✅ test_endpoint Tauri command — 并发测试 + 进度推送
- ❌ Video/SpeechToText/TextToSpeech 能力全部 skip
- ❌ 无 Video URL 候选构建

## 运行时假设
- Azure/APIM 端点通过 Responses API 聊天
- OpenAI Compatible 端点通过 Chat Completions API
- reasoning_content / reasoning / thinking 三字段 + 字符串/{text} 对象两种格式
- 视频探活：仅发起 create 请求，不轮询不下载
- **自测方法**: 单元测试验证 URL 构建 + reasoning 解析 + 请求体构建；无网络集成测试

## 任务清单

### T-001: OpenAiChatProvider 多 URL 候选回退 [Spec: AiInsightService.md §流程2]
- 位置: @crates/tfp-providers/src/openai_chat.rs:32-58
- 契约: `pub(crate) fn build_chat_urls(&self) -> Vec<String>` (替代 build_url)
- 逻辑:
  1. AzureOpenAi: ["{base}/openai/v1/responses"]
  2. APIM: ["{base}/v1/responses", "{base}/responses?api-version={ver}", "{base}/v1/chat/completions"]
  3. OpenAiCompatible: base 含 /v1 → ["{base}/chat/completions"]，否则 ["{base}/v1/chat/completions"]
  4. 其他同 OpenAiCompatible
- 测试: 4 个 URL 构建测试
- 备注: 现有 build_url() 保留为 `build_chat_urls()[0]` 委托，向后兼容

### T-002: try_candidates 内联到 complete()/complete_stream() [Spec: AiInsightService.md §流程2]
- 位置: @crates/tfp-providers/src/openai_chat.rs:117-326 (complete + complete_stream)
- 契约: 签名不变，内部使用候选回退
- 逻辑:
  1. build_chat_urls() 获取候选列表
  2. 对 complete()：逐候选 POST，404/405 → 下一个，成功/非重试错误 → 返回
  3. 对 complete_stream()：同上（SSE 响应的成功判断基于 HTTP 状态码）
  4. 每个 URL 自动检测是 responses API 还是 chat completions（基于 URL 是否含 /responses）
- 测试: 编译通过（真正回退需 mock server）

### T-003: reasoning 三字段兼容 [Spec: AiInsightService.md §推理链兼容性]
- 位置: @crates/tfp-providers/src/openai_chat.rs SSE 解析部分
- 契约: 新增 `fn try_read_reasoning(delta: &serde_json::Value) -> Option<String>`
- 逻辑:
  1. 依次检查 delta["reasoning"], delta["reasoning_content"], delta["thinking"]
  2. 每个字段支持两种值格式：直接字符串 和 {"text": "..."} 对象
  3. 返回第一个非空值
- 测试: 6 个测试（3 字段 x 2 格式）
- **自测**: cargo test -p tfp-providers

### T-004: CompletionRequest 增加 reasoning_effort 和 image_generation 字段 [Spec: AiInsightService.md §流程4]
- 位置: @crates/tfp-core/src/models/api.rs:149-156 (CompletionRequest)
- 契约: 新增字段
  ```
  #[serde(default)]
  pub reasoning_effort: Option<String>,  // "low" | "medium" | "high"
  #[serde(default)]
  pub enable_image_generation: bool,
  #[serde(default)]
  pub image_model_deployment: Option<String>,
  #[serde(default)]
  pub image_size: Option<String>,
  #[serde(default)]
  pub image_quality: Option<String>,
  ```
- 测试: 反序列化兼容测试（无新字段的 JSON 仍可解析）

### T-005: BuildRequestBody 完善 — reasoning_effort + image_generation tool [Spec: AiInsightService.md §流程4]
- 位置: @crates/tfp-providers/src/openai_chat.rs complete/complete_stream 的 body 构建部分
- 逻辑:
  1. Responses API + reasoning_effort: 追加 `"reasoning": {"effort": effort, "summary": "auto"}`
  2. Chat Completions + reasoning_effort: 追加 `"reasoning_effort": effort`
  3. enable_image_generation + Responses API: 追加 `"tools": [{"type": "image_generation"}]`
  4. image_model_deployment: 追加请求头 `x-ms-oai-image-generation-deployment`
- 测试: 2 个请求体构建测试（Responses + ChatCompletions）

### T-006: StreamChunk 增加 ImageGenerating / ImageResult 变体 [Spec: AiInsightService.md §流程5]
- 位置: @crates/tfp-providers/src/traits.rs:105-113 (StreamChunk)
- 契约: 新增变体
  ```
  ImageGenerating,
  ImageResult { base64_data: String, content_type: String },
  ReasoningSummary(String),
  ```
- 逻辑: 在 Responses API SSE 解析中:
  - `response.output_item.added` + type=="image_generation_call" → `ImageGenerating`
  - `response.completed` 含 image_generation_call.result → `ImageResult`
  - `response.reasoning_summary_text.delta` → `ReasoningSummary`
- 测试: StreamChunk 变体存在性测试

### T-007: 端点测试 — Video 能力测试实装 [Spec: EndpointBatchTestService.md §流程6]
- 位置: @src-tauri/src/commands/test_runner.rs:167 (目前 skip)
- 契约: `async fn test_video(client, url, model, endpoint, profile) -> TestResult`
- 逻辑:
  1. 构建 JSON body: {"model": model, "prompt": "A tiny red dot", "size": "1280x720", "n": 1}
  2. POST 到候选 URL
  3. 成功 → (true, "Video creation OK", None)
  4. 45 秒超时
- 备注: build_url_candidates 需扩展 Video 候选（T-008）

### T-008: build_url_candidates 扩展 Video 候选 [Spec: EndpointBatchTestService.md §流程6]
- 位置: @src-tauri/src/commands/test_runner.rs:61-81 (build_url_candidates)
- 契约: 扩展 `ModelCapability::Video` 分支
- 逻辑:
  - AzureOpenAi: ["{base}/openai/v1/video/generations/jobs?api-version={ver}"]
  - APIM: ["{base}/v1/video/generations/jobs", "{base}/openai/v1/video/generations/jobs?api-version={ver}"]
  - 其他: ["{base}/v1/video/generations/jobs"]
- 测试: 3 个 URL 候选测试

## 退出标准
- cargo check -p tfp-providers -p tfp-core -p truefluent-pro-r3 0 errors 0 warnings
- cargo test -p tfp-providers 全绿（含新增 ≥ 15 测试）
- build_chat_urls 对 4 种 endpoint_type 输出正确
- try_read_reasoning 对 3 字段 x 2 格式 = 6 种组合输出正确
- complete/complete_stream 使用候选 URL 回退（非单 URL）
- CompletionRequest 新字段不破坏现有 JSON
- Video 测试不再 skip
- 备注：本 batch 完成后，batch-5（配置持久化 + 模型发现）成为可能