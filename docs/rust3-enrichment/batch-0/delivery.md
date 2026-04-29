# 批次 0 交付报告
> 完成时间：2026-04-29
> 执行者：程序员 (Copilot)

## 交付摘要

批次 0 (Models补全) 全部 9 个任务已完成并通过验证。

## 完成任务清单

| 编号 | 任务 | 状态 |
|------|------|------|
| T-001 | 新增 7 个 typed enums 到 config.rs | ✅ |
| T-002 | AiEndpoint 字段升级 (String→enum + 5 新字段) | ✅ |
| T-003 | AiModelEntry.group_name 字段 | ✅ |
| T-004 | MediaSettings 新增 11 字段 | ✅ |
| T-005 | 创建 enums.rs (9 个枚举) | ✅ |
| T-006 | 创建 cloud.rs (CloudSettings/CloudUserProfile/QuotaInfo) | ✅ |
| T-007 | 创建 common.rs (9 个小模型) | ✅ |
| T-008 | mod.rs 注册 + AppConfig.cloud 字段 | ✅ |
| T-009 | 全工作区测试修复 | ✅ |

## 产出文件

### 新建
- `crates/tfp-core/src/models/enums.rs` — 9 个处理/音频/配置枚举
- `crates/tfp-core/src/models/cloud.rs` — CloudSettings, CloudUserProfile, QuotaInfo
- `crates/tfp-core/src/models/common.rs` — 9 个工具模型 (EndpointTemplateDefinition, ModelOption, SubtitleCue 等)

### 修改
- `crates/tfp-core/src/models/config.rs` — 新增 7 typed enums + AiEndpoint 升级
- `crates/tfp-core/src/models/settings.rs` — MediaSettings 新增 11 字段
- `crates/tfp-core/src/models/mod.rs` — 模块注册 + 24 个新测试
- `crates/tfp-providers/src/auth.rs` — enum 匹配替代字符串匹配
- `crates/tfp-providers/src/test_helpers.rs` — 使用 enum + ..Default
- `crates/tfp-providers/src/openai_image.rs` — 测试端点构造
- `crates/tfp-providers/src/openai_video.rs` — 测试端点构造
- `crates/tfp-providers/src/openai_translation.rs` — 测试端点构造
- `crates/tfp-providers/src/registration.rs` — make_endpoint
- `crates/tfp-media/src/image_pipeline.rs` — auth 枚举匹配
- `src-tauri/src/commands/auth.rs` — enum 赋值
- `src-tauri/src/commands/test_runner.rs` — resolve_auth_header 重写
- `src-tauri/src/lib.rs` — enum 比较

## 验证结果

```
cargo check:   0 errors, 0 warnings
cargo test:    265 passed, 0 failed (其中 tfp-core 68 tests)
新增测试:      24 个 (batch-0 serde/logic tests)
```

## 退出标准核验

| 标准 | 状态 |
|------|------|
| cargo check 0 errors/warnings | ✅ |
| cargo test 全绿 | ✅ |
| 新增 ≥20 测试 | ✅ (24) |
| 所有枚举 serde 可逆 | ✅ |
| AiEndpoint 从最小 JSON 可反序列化 | ✅ |
| 向后兼容：空 JSON → defaults | ✅ |
| CloudSettings 集成到 AppConfig | ✅ |

## 技术备注

- `ApiKeyHeaderMode::ApiKeyHeader` 通过 `#[serde(rename = "api_key")]` 保持向后兼容
- `AzureAuthMode::Aad` 通过 `#[serde(rename = "aad")]` 保持向后兼容
- SpeechCapabilityFlag 使用 Vec 模式（与 ModelCapability 一致）
- ImageEditMode 默认值为 V2ResponsesApi
- SubtitleCue 使用 i64 毫秒