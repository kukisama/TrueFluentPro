# Batch-4 交付报告 — AI Chat 多URL候选 + 端点测试增强

## 交付摘要

| 指标 | 值 |
|------|-----|
| 任务数 | 8 (T-001 ~ T-008) |
| 修改文件 | 9 |
| 新增行 (净) | ~550 |
| 新增测试 | 19 |
| 编译 | ✅ 0 errors, 0 warnings (workspace) |
| 测试 | ✅ 346 passed, 0 failed |

## 按任务交付

### T-001: CompletionRequest 新增字段
- **文件**: `crates/tfp-core/src/models/api.rs`
- **内容**: 新增 `reasoning_effort`, `enable_image_generation`, `image_model_deployment`, `image_size`, `image_quality` 字段
- **影响修复**: 6 个构造点 (openai_translation.rs, streaming.rs, task_engine.rs, image_pipeline.rs, media.rs, mod.rs test)

### T-002: build_chat_urls() 多URL候选
- **文件**: `crates/tfp-providers/src/openai_chat.rs`
- **内容**: Azure/APIM 生成 `/responses` + `/chat/completions` 双候选; Compatible/Custom 生成 `/chat/completions`
- **测试**: 4 tests (azure 2候选, apim 2候选, compatible 1候选, custom)

### T-003: try_send_candidates() 候选URL遍历
- **文件**: `crates/tfp-providers/src/openai_chat.rs`
- **内容**: 迭代候选URL, 404/405→下一个, 401/403→Auth error, 429→RateLimited

### T-004: 请求体构建 (Responses / ChatCompletions)
- **文件**: `crates/tfp-providers/src/openai_chat.rs`
- **内容**: `build_responses_body()` (input 格式 + image tool + reasoning_effort), `build_chat_completions_body()` (messages 格式)
- **测试**: build_responses_input, build_chat_completions_body 测试

### T-005: try_read_reasoning() 多格式推理兼容
- **文件**: `crates/tfp-providers/src/openai_chat.rs`
- **内容**: 3 字段名 (reasoning, reasoning_content, thinking) × 2 格式 (string, {text: "..."} object)
- **测试**: 6 tests 覆盖全部组合

### T-006: StreamChunk 新增变体
- **文件**: `crates/tfp-providers/src/traits.rs`, `crates/tfp-chat/src/streaming.rs`
- **内容**: `ReasoningSummary(String)`, `ImageGenerating`, `ImageResult { base64_data, content_type }`
- **影响修复**: 2 个 match 表达式增加新分支, 前端事件格式保持 JSON

### T-007: test_video() 视频能力测试
- **文件**: `src-tauri/src/commands/test_runner.rs`
- **内容**: 新增 `test_video()` 函数, SoraJobs + Videos 双模式测试
- **测试**: 3 tests (video URL 候选, 不同 VideoApiMode)

### T-008: build_url_candidates() 扩展
- **文件**: `src-tauri/src/commands/test_runner.rs`
- **内容**: Video 能力的 URL 候选生成, 集成到 run_batch_tests 主流程

## 修改文件清单

| 文件 | 变更类型 |
|------|----------|
| `crates/tfp-core/src/models/api.rs` | 新增 5 字段 |
| `crates/tfp-core/src/models/mod.rs` | 测试修复 |
| `crates/tfp-providers/src/openai_chat.rs` | 完全重写 (~530行) |
| `crates/tfp-providers/src/openai_translation.rs` | 字段补齐 |
| `crates/tfp-providers/src/traits.rs` | 3 新 enum 变体 |
| `crates/tfp-chat/src/streaming.rs` | 字段补齐 + match 分支 |
| `crates/tfp-engine/src/task_engine.rs` | 字段补齐 |
| `crates/tfp-media/src/image_pipeline.rs` | 字段补齐 |
| `src-tauri/src/commands/test_runner.rs` | test_video + URL候选 |
| `src-tauri/src/commands/media.rs` | 字段补齐 |
