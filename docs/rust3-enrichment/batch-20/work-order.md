# 批次 20 施工单
> 日期：2026-04-30
> 路线图阶段：Phase 5 — 基础设施 + 收尾
> 本阶段进度：4/4（P5 最后一轮）

## 目标
消除所有编译警告、注册缺失 Tauri 命令、修复前端 TS 错误、补齐领域集成测试（每域 ≥1 个 #[ignore] 集成测试）、更新 PLATFORM-NOTES.md，使项目达到 Phase 5 完成标准。

## Spec 来源
| 文档 | 相关段落 |
|------|---------|
| 无特定 Spec | 本轮为工程收尾，覆盖编译质量、测试覆盖、命令注册 |
| PLATFORM-NOTES.md | 平台知识整理（历次 Phase 门卫检查延后项） |

## Rust3 现状
- **编译**: cargo check 0 errors, 7 warnings（4 个 tfp-providers + 3 个 src-tauri）
- **测试**: 582 passed, 2 ignored（覆盖 40+ 模块，但无跨域集成测试）
- **前端**: 2 个 pre-existing tsc errors in LiveTranslationView.tsx（synthesizeSpeech API 未定义）
- **缺失命令注册**: transcribe_audio、synthesize_speech、list_voices 已定义但未注册到 invoke_handler
- **缺失前端 API**: synthesizeSpeech 未定义在 tauri-api.ts 中
- **集成测试**: 仅 common.rs（加载 Rust2 endpoints）和 duckduckgo live search 2 个 #[ignore] 测试

## 前置条件
- batch-19 通过（控件库 + i18n 完整）

## 运行时假设
- 目标 API：多域（图片/聊天/语音/存储）— 集成测试标记 #[ignore]
- 认证方式：从 Rust2 SQLite 读取已有 API key
- 参数约束：无新增
- **自测方法**：cargo check 0 warnings + cargo test 全绿 + tsc --noEmit 0 errors + cargo test --ignored 尝试运行

## 任务清单

### T-001: 消除 tfp-providers 4 个警告
- Spec 参考: 无（工程质量）
- 现有代码: `crates/tfp-providers/src/registration.rs:13-14`（未使用 OpenAiSttProvider/OpenAiTtsProvider import）; `crates/tfp-providers/src/azure_tts.rs`（未使用 serde::Deserialize + dead_code build_styled_ssml）
- 产出: 修改 registration.rs + azure_tts.rs + 确认 azure_stt.rs/openai_stt.rs/openai_tts.rs 的 serde::Deserialize 是否真正使用
- 具体修改:
  1. `registration.rs:13` — 删除 `use crate::openai_stt::OpenAiSttProvider;`（未在函数体中使用，OpenAI STT/TTS 目前注册用的是 Azure 路径）
  2. `registration.rs:14` — 删除 `use crate::openai_tts::OpenAiTtsProvider;`
  3. `azure_tts.rs` 的 serde::Deserialize — 检查是否实际用于反序列化结构体。如果仅在测试中使用，保留但确认无警告。如果完全未使用，删除。
  4. `build_styled_ssml` — 该函数被 #[cfg(test)] 测试调用但非 prod 代码调用。两个选择：
     a. 在 TTS synthesize() 方法中实际使用它（正确做法：将 build_ssml 逻辑改为调用 build_styled_ssml）
     b. 或在函数上加 `#[cfg(test)]` + `#[allow(dead_code)]`
     **选择 a**：如果 build_styled_ssml 是 build_ssml 的增强版，应在 synthesize() 中调用它。如果两者逻辑不同，选 b。
- 测试: `cargo check -p tfp-providers` 0 warnings
- **自测**: `cargo check --workspace` 0 warnings（tfp-providers 部分）

### T-002: 消除 src-tauri 3 个警告 — 注册缺失的 Tauri 命令
- Spec 参考: 无（命令注册完整性）
- 现有代码: `src-tauri/src/commands/audio.rs:199,222,248`（transcribe_audio、synthesize_speech、list_voices）; `src-tauri/src/lib.rs` invoke_handler
- 产出: 修改 `src-tauri/src/lib.rs`
- 具体修改:
  1. 在 invoke_handler 的 Audio 区块（约 L203 `commands::list_audio_devices` 之后）添加:
     ```rust
     // Audio transcription & synthesis
     commands::transcribe_audio,
     commands::synthesize_speech,
     commands::list_voices,
     ```
  2. 确认这 3 个函数的 mod.rs 导出路径正确（audio.rs 已在 mod.rs 中 `pub use audio::*;`）
- 测试: `cargo check` 0 warnings（src-tauri 部分）
- **自测**: `cargo check --workspace` 总计 0 warnings

### T-003: 修复前端 TS 错误 — synthesizeSpeech API
- Spec 参考: SpeechSynthesisService.md（TTS 命令签名）
- 现有代码: `src/lib/tauri-api.ts`（缺失 synthesizeSpeech）; `src/views/LiveTranslationView.tsx:70,80`（调用 synthesizeSpeech）
- 产出: 修改 `src/lib/tauri-api.ts` 和 `src/views/LiveTranslationView.tsx`
- 具体修改:
  1. 在 `tauri-api.ts` 中添加:
     ```ts
     synthesizeSpeech: (endpointId: string, text: string, voice: string, format: string, outputPath: string) =>
       invoke<string>("synthesize_speech", { endpointId, text, voice, format, outputPath }),
     listVoices: (endpointId: string, locale: string) =>
       invoke<VoiceInfo[]>("list_voices", { endpointId, locale }),
     transcribeAudio: (endpointId: string, audioPath: string, lang: string) =>
       invoke<TranscriptSegment[]>("transcribe_audio", { endpointId, audioPath, lang }),
     ```
  2. 确保 VoiceInfo 和 TranscriptSegment 接口已在 tauri-api.ts 中定义（检查现有定义）
  3. 修复 LiveTranslationView.tsx:70 的 unused variable warning（`speakSegment` declared but unused）
     - 如果 speakSegment 是 TTS 播放功能 → 保留但使用它（绑定到 UI）
     - 如果确实未使用 → 删除或注释掉
- 测试: `npx tsc --noEmit` 0 errors
- **自测**: tsc 编译通过

### T-004: 集成测试 — 图片生成域
- Spec 参考: AiImageGenService.md（图片生成流程）
- 现有代码: 新建
- 产出: `Rust3/crates/tfp-providers/tests/image_integration.rs`
- 契约:
  ```rust
  #[test]
  #[ignore] // requires API key — run with: cargo test -p tfp-providers --test image_integration -- --ignored
  fn test_image_generation_live() {
      // 1. load_rust2_endpoints() → find azure_openai endpoint
      // 2. Create OpenAiImageProvider
      // 3. Call generate_image with small/low-cost params (1024x1024, low quality)
      // 4. Assert: response contains image data or URL
  }
  ```
- 业务逻辑:
  1. 从 Rust2 SQLite 加载 endpoint（复用 common.rs 的 load_rust2_endpoints 逻辑）
  2. 构造 ImageGenRequest（prompt="A simple red circle on white background", size="1024x1024", quality="low"）
  3. 调用 generate_image()
  4. 验证返回 ImageGenResponse 且有 image data
- 测试: `cargo test -p tfp-providers --test image_integration -- --ignored`
- **自测**: 需要 API key，标记 #[ignore]

### T-005: 集成测试 — AI 聊天域
- Spec 参考: AiInsightService.md（聊天补全流程）
- 现有代码: 新建
- 产出: `Rust3/crates/tfp-providers/tests/chat_integration.rs`
- 契约:
  ```rust
  #[test]
  #[ignore] // requires API key
  fn test_chat_completion_live() {
      // 1. load endpoint → OpenAiChatProvider
      // 2. Call complete() with simple prompt
      // 3. Assert: response has non-empty content
  }
  ```
- 业务逻辑:
  1. 加载 endpoint
  2. 构造 AiCompletionRequest（messages=[{role:"user", content:"Say hello"}], max_tokens=50）
  3. 调用 complete()
  4. 验证 content 非空
- 测试: `cargo test -p tfp-providers --test chat_integration -- --ignored`
- **自测**: 需要 API key，标记 #[ignore]

### T-006: 集成测试 — 配置 round-trip 域
- Spec 参考: SettingsImportExportService.md
- 现有代码: 新建
- 产出: `Rust3/crates/tfp-storage/tests/config_roundtrip.rs`
- 契约:
  ```rust
  #[test]
  fn test_config_export_import_roundtrip() {
      // 1. Create temp DB
      // 2. Insert config with 2 endpoints
      // 3. export_config() → JSON string
      // 4. import_config(json) on fresh DB
      // 5. Assert: configs match (modulo sensitive fields)
  }
  ```
- 业务逻辑:
  1. 创建临时 SQLite DB
  2. 写入 AppConfig（含 2 个 endpoint + 设置）
  3. 读回 → export 为 JSON → 解析 → 重新 import
  4. 验证字段完整（endpoint 数量、名称、URL 一致）
- 测试: `cargo test -p tfp-storage --test config_roundtrip`（不需要 API key）
- **自测**: `cargo test -p tfp-storage --test config_roundtrip`

### T-007: 集成测试 — 任务引擎域
- Spec 参考: BatchProcessingViewModel.md（任务执行流程）
- 现有代码: 新建
- 产出: `Rust3/crates/tfp-engine/tests/engine_integration.rs`
- 契约:
  ```rust
  #[test]
  fn test_task_submit_and_complete() {
      // 1. Create in-memory TaskEngine deps (mock providers)
      // 2. Submit a simple task
      // 3. Assert: task status transitions from Queued → Running → Completed
  }
  ```
- 业务逻辑:
  1. 创建内存 DB + mock providers
  2. 提交任务 via task_event_bus
  3. 等待完成（poll status 或 subscribe events）
  4. 验证 status == Completed
- 测试: `cargo test -p tfp-engine --test engine_integration`
- **自测**: 纯本地逻辑，不需要 API key

### T-008: 更新 PLATFORM-NOTES.md
- Spec 参考: 无（知识库维护）
- 现有代码: `docs/rust3-enrichment/PLATFORM-NOTES.md`（28 行）
- 产出: 更新 PLATFORM-NOTES.md
- 具体追加内容:
  1. **Azure Speech SDK** 段落：实时翻译 WebSocket 协议细节、SDK 版本、DLL 依赖
  2. **OpenAI Realtime** 段落：WebSocket 端点 URL、Session 配置参数
  3. **Tauri 2 补充**：generate_handler! 中命令必须全部注册否则 dead_code 警告、事件命名约定
  4. **AudioLab** 段落：8 阶段名称 + 执行顺序
  5. **Batch Processing** 段落：Package 状态机（Draft→Queued→Running→Completed/Failed）
  6. **Studio/Center** 段落：分页加载策略（before cursor）、分支（fork）创建规则
  7. **已知限制** 段落：APIM 文件操作限制（仅 upload）、图片编辑限制（仅 multipart binary）
- 测试: 无（文档）
- **自测**: 目测内容完整

## 技术决策记录
| 编号 | 决策 | Spec 依据 | 与 C# 差异 |
|------|------|-----------|-----------|
| D-01 | OpenAiSttProvider/OpenAiTtsProvider 从 registration.rs 移除未使用 import（保留 provider 实现文件，仅清理注册代码中的 unused import） | 当前无 OpenAI STT/TTS 端点类型注册逻辑 | C# 有更多端点类型支持 |
| D-02 | build_styled_ssml 选择标记 #[allow(dead_code)] 而非重构调用链 | 函数已有测试覆盖，后续 batch 可能使用 | 保守处理 |
| D-03 | 集成测试放在 crate 级 tests/ 目录（不是 src-tauri/tests/） | Rust 标准实践：integration tests per crate | C# 有独立测试项目 |
| D-04 | synthesizeSpeech 前端 API 补齐以修复 tsc 错误 | batch-8 施工单要求的 TTS 前端接口 | 遗漏修复 |

## 后续影响
- 本批次是最后一轮，完成后执行 Phase 5 完成检查
- 所有 Phase 门卫检查延后的 PLATFORM-NOTES.md 将在本轮补齐
- 代码达到"零警告、零错误、有集成测试"的发布就绪状态

## 禁止事项
- 禁止删除任何现有测试
- 禁止修改现有业务逻辑（仅注册、导入、文档）
- 禁止引入新的 crate 依赖
- 禁止在集成测试中硬编码 API key（必须从 Rust2 DB 动态读取）

## 退出标准
- cargo check --workspace **0 errors, 0 warnings**
- cargo test --workspace **全绿**（≥590 tests，含新增集成测试中非 #[ignore] 的）
- tsc --noEmit **0 errors**（含修复 LiveTranslationView.tsx）
- PLATFORM-NOTES.md 更新至 ≥50 行有效内容
- **自测通过**：cargo test -p tfp-providers --test image_integration -- --ignored ✅（如 API key 可用）
