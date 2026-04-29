# 批次 1 审查报告
> 审查日期：2026-04-29

## 逐项审查
- ✅ T-001: ImageGenResult 补全 — crates/tfp-core/src/models/api.rs:81-103，新增 response_id、request_url、attempted_urls、generate_seconds、download_seconds、actual_input_tokens、actual_output_tokens，#[serde(default)] 确保向后兼容
- ✅ T-002: 路由决策函数 — crates/tfp-providers/src/openai_image.rs:30-41，3 分支逻辑正确（V1Multipart→false, text_model.is_some()→true, 默认→false）
- ✅ T-003: 候选 URL 构建（完整版）— openai_image.rs:44-134，3 个函数 × 3 种 endpoint_type 覆盖完整，APIM 使用配置 api_version 优先、fallback 到默认值
- ✅ T-004: 候选 URL 回退执行器 — openai_image.rs:136-182，try_candidates 正确实现 404/405→继续、401/403→Auth 错误、429→RateLimited、其他→Network 错误
- ✅ T-005: 重写 generate() — 双路由 — openai_image.rs:514-523 trait impl 正确路由到 generate_via_images_api (L185-235) 或 generate_via_responses (L238-291)
- ✅ T-006: 图片编辑 multipart — openai_image.rs:294-401，正确构建 multipart form（image + prompt + model + size + quality + background），内联重试逻辑合理（form 不可 Clone）
- ✅ T-007: 响应解析器统一化 — openai_image.rs:410-481，parse_image_response 支持 3 种格式（data[].b64_json, data[].url, output[].image_generation_call），正确提取 usage 和 response_id

## 编译验证
```
cargo check --workspace
    Finished `dev` profile [unoptimized + debuginfo] target(s) in 0.55s
```
0 errors, 0 warnings ✅

## 测试验证
```
cargo test --workspace
test result: ok. 286 passed; 0 failed; 1 ignored; 0 measured
```
全绿 ✅（新增 21 测试，总计 287 = 286 passed + 1 ignored）

## 前端类型检查
```
npx tsc --noEmit → exit code 0
```

## Spec 一致性
| Spec 段落 | 代码位置 | 一致？ |
|-----------|---------|--------|
| AiImageGenService § ShouldUseResponsesApi | openai_image.rs:30-41 | ✅ |
| AiImageGenService § 流程1 路由判定 | openai_image.rs:514-523 | ✅ |
| AiImageGenService § 流程3 multipart | openai_image.rs:294-401 | ✅ |
| AiImageGenService § 流程4 URL 回退 | openai_image.rs:136-182 | ✅ |
| EndpointProfileUrlBuilder § 图片 URL 候选 | openai_image.rs:44-134 | ✅ |

## 施工单异议评估
| 异议 | 合理？ | 评价 |
|------|--------|------|
| image_edit_mode 放入 ImageGenRequest 而非从 MediaSettings 读取 | ✅ 合理 | Provider 层不应直接访问全局配置，请求级参数更清晰 |
| APIM 使用配置 api_version 优先 | ✅ 合理 | 与 Spec "api-version 来自端点配置" 一致 |
| try_candidates + parse_image_response_from_reqwest 桥接 | ✅ 合理 | 保持设计决策 D-001 不变 |

## 运行时可达性
```
generate_image (Tauri cmd) → providers.get_image_gen() → OpenAiImageProvider.generate()
    → should_use_responses_api() → generate_via_images_api() 或 generate_via_responses()
        → try_candidates() → parse_image_response_from_reqwest()
```
✅ 完整链路可达

## 判定
✅ 通过

## 进度更新
- batch-progress.md 已更新：✅
- 代码量已追加：✅（crates: 13602 | tauri: 6087 | frontend: 8527 | total: 28216 | tests: 287）
- current-batch.txt 已推进到：batch-2
- Phase 门卫检查：不需要（Phase 1 第 2/8 批次）
