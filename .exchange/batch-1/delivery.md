# 批次 1 交付报告
> 提交日期：2026-04-28

## 任务完成状态
- [x] T-001: tfp-core 模型定义 — 证据：`crates/tfp-core/src/models/config.rs`、`settings.rs`、`api.rs`
- [x] T-002: tfp-core 统一错误类型 — 证据：`crates/tfp-core/src/error.rs`:L1-L76
- [x] T-003: tfp-core lib.rs 模块导出 — 证据：`crates/tfp-core/src/lib.rs`:L1-L4
- [x] T-004: tfp-core Cargo.toml 依赖 — 证据：`crates/tfp-core/Cargo.toml`:L6-L8
- [x] T-005: tfp-storage Cargo.toml 依赖 — 证据：`crates/tfp-storage/Cargo.toml`:L6-L15
- [x] T-006: tfp-storage 数据库连接+迁移 — 证据：`crates/tfp-storage/src/db.rs` + 7 个 SQL 迁移文件
- [x] T-007: tfp-storage KV 存储 repo — 证据：`crates/tfp-storage/src/config_repo.rs`:L1-L81
- [x] T-008: tfp-storage 会话 repo — 证据：`crates/tfp-storage/src/session_repo.rs`:L1-L230
- [x] T-009: tfp-storage lib.rs 模块导出 — 证据：`crates/tfp-storage/src/lib.rs`:L1-L5
- [x] T-010: tfp-providers Cargo.toml 依赖 — 证据：`crates/tfp-providers/Cargo.toml`:L6-L10
- [x] T-011: tfp-providers 能力 trait 定义 — 证据：`crates/tfp-providers/src/traits.rs`:L1-L127（7 个 trait + StreamChunk + RealtimeSessionHandle）
- [x] T-012: tfp-providers ProviderRegistry — 证据：`crates/tfp-providers/src/registry.rs`:L1-L246（7 slot HashMap + CRUD + list_providers 合并能力）
- [x] T-013: tfp-providers lib.rs 模块导出 — 证据：`crates/tfp-providers/src/lib.rs`:L1-L4

## 编译状态
`cargo check -p tfp-core -p tfp-storage -p tfp-providers` 输出：
```
Checking tfp-core v0.1.0
Checking tfp-storage v0.1.0
Checking tfp-providers v0.1.0
Finished `dev` profile [unoptimized + debuginfo] target(s) in 1.09s
```
0 errors, 0 warnings.

`cargo test -p tfp-core -p tfp-storage -p tfp-providers` 输出：
```
tfp-core:      5 passed (test_app_config_roundtrip, test_endpoint_type_serde, test_ai_endpoint_migrate_auth, test_realtime_event_tagged_serde, test_error_serialize)
tfp-storage:   8 passed (test_open_in_memory, test_migration_idempotent, test_schema_version, test_kv_roundtrip, test_kv_get_missing, test_config_roundtrip, test_session_crud, test_message_crud)
tfp-providers: 4 passed (test_register_and_get, test_get_missing, test_list_providers_merges_capabilities, test_clear)
Total: 17 passed, 0 failed
```

## 自检清单
- [x] 编译通过（0 errors, 0 warnings）
- [x] 无死代码（所有 pub 类型被测试或被其他 crate 引用）
- [x] 测试通过（17/17 全绿）
- [x] 无中文硬编码（`insight_system_prompt` 默认值改为空字符串 `""`）
- [x] 单文件不超过 400 行（最大 251 行：config.rs）
- [x] 变更日志每条有证据
- [x] 无 `#[allow(dead_code)]`
- [x] 无 `let _ = conn.execute_batch("ALTER...")` 反模式
- [x] 迁移使用版本化 schema_version 表（V1-V7）
- [x] serde 字段名与 Rust2 一致（`#[serde(rename_all = "snake_case")]`）
- [x] VideoGenSlot（第 7 个 trait）已新增

## 已知局限
- **From<rusqlite::Error> 未放在 tfp-core**: 施工单 T-002 要求 `impl From<rusqlite::Error> for AppError`，但 T-004 明确限定 tfp-core 仅依赖 serde/serde_json/thiserror，不含 rusqlite。Rust 孤儿规则禁止在 tfp-storage 中为外部 crate 的类型实现 From。因此改用 `pub(crate) fn map_db_err(e: rusqlite::Error) -> AppError` 辅助函数，功能等价，调用点统一在 `db.rs`。
- **models.rs 拆分为 models/ 模块目录**: 因 400 行限制，将 models 拆为 `config.rs`（251 行）+ `settings.rs`（185 行）+ `api.rs`（223 行）+ `mod.rs`（94 行）。对外接口不变：`tfp_core::models::*` 全部 re-export，`use tfp_core::AppConfig` 等路径与施工单一致。
- **tokio dev-dependencies**: tfp-storage 测试需要 `#[tokio::test]`，已添加 `[dev-dependencies] tokio = { features = ["sync", "macros", "rt"] }`。
- **migrate() 重构**: 原设计在 Mutex 内调用 `blocking_lock()` 会在 tokio runtime 中 panic。改为 `run_migrations(&Connection)` 在 Mutex 包装之前执行，避免 runtime 冲突。

## 新增/修改的文件清单
- `crates/tfp-core/Cargo.toml` — 添加 serde, serde_json, thiserror 依赖
- `crates/tfp-core/src/lib.rs` — 替换为 models + error 模块导出
- `crates/tfp-core/src/models/mod.rs` — 模块入口 + 4 个单元测试
- `crates/tfp-core/src/models/config.rs` — 核心配置类型（AppConfig, AiEndpoint, EndpointType 等）
- `crates/tfp-core/src/models/settings.rs` — 子配置类型（MediaSettings, RecognitionSettings 等）
- `crates/tfp-core/src/models/api.rs` — 请求/响应类型（TranslateRequest, RealtimeEvent, ImageGenRequest 等）
- `crates/tfp-core/src/error.rs` — AppError + ProviderError + From impls + 1 个测试
- `crates/tfp-storage/Cargo.toml` — 添加 tfp-core, rusqlite, serde, tokio, sha2, tracing 依赖
- `crates/tfp-storage/src/lib.rs` — db + config_repo + session_repo 模块导出
- `crates/tfp-storage/src/db.rs` — Database struct + open/open_in_memory + 版本化迁移 + 3 个测试
- `crates/tfp-storage/src/migrations/v1.sql` — V1 核心表（kv_store, sessions, messages 等）
- `crates/tfp-storage/src/migrations/v2.sql` — V2 媒体表（media_sessions, media_items, audio_sessions）
- `crates/tfp-storage/src/migrations/v3.sql` — V3 附件表（message_attachments 等）
- `crates/tfp-storage/src/migrations/v4.sql` — V4 创作工坊 8 张表
- `crates/tfp-storage/src/migrations/v5.sql` — V5 实时翻译 2 张表
- `crates/tfp-storage/src/migrations/v6.sql` — V6 媒体中心 2 张表 + ALTER
- `crates/tfp-storage/src/migrations/v7.sql` — V7 听析中心 7 张表 + ALTER
- `crates/tfp-storage/src/config_repo.rs` — KV 存储 + AppConfig load/save + 3 个测试
- `crates/tfp-storage/src/session_repo.rs` — Session/Message CRUD + SHA256 去重 + 2 个测试
- `crates/tfp-providers/Cargo.toml` — 添加 tfp-core, async-trait, tokio, serde 依赖
- `crates/tfp-providers/src/lib.rs` — traits + registry 模块导出
- `crates/tfp-providers/src/traits.rs` — 7 个 Provider trait + ProviderCapability + StreamChunk
- `crates/tfp-providers/src/registry.rs` — ProviderRegistry（7 slot） + ProviderInfo + 4 个测试
