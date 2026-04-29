# 批次 0 施工单
> 日期：2026-04-29
> 路线图阶段：Phase 1 — 核心管线补全
> 本阶段进度：0/8

## 目标

将 tfp-core models 中所有枚举升级为 typed enums，补齐 AiEndpoint/MediaSettings 缺失字段，新增 Spec 中定义的小型数据模型。

## Spec 来源

| 文档 | 相关段落 |
|------|---------|
| Models/AiEndpointAndConfig.md | § AiEndpoint 字段、§ AiModelEntry、§ MediaGenConfig 字段清单、§ EndpointTemplateDefinition |
| Models/EnumsAndSmallModels.md | § 全部（17 个枚举/小模型） |
| Models/CoreConfig.md | § AiConfig 内嵌枚举（8 个）、§ SpeechResource 内嵌枚举（3 个） |

## Rust3 现状

- config.rs: AiEndpoint 存在但 auth_header_mode/auth_mode 是 String 类型，缺 profile_id、provider_type、text_api_protocol_mode、image_api_route_mode、speech_capabilities 字段
- config.rs: AiModelEntry 存在但缺 group_name 字段
- settings.rs: MediaSettings 有基本图片/视频参数但缺大量 MediaGenConfig 字段
- api.rs: VideoApiMode 已有，AudioDeviceType 已有（但命名为 Input/Output/Loopback 而非 Capture/Render — 保留现有命名）
- 缺失：EndpointTemplateDefinition、ModelOption、CloudSettings、CloudUserProfile、SubtitleCue 等小模型
- 缺失：约 15 个 typed enum

## 前置条件

无（本批次是 Phase 1 首批）

## 运行时假设

- 本批次纯数据模型，无 API 调用
- **自测方法**：cargo test -p tfp-core 全绿 + cargo check -p tfp-core 0 errors/warnings

## 任务清单

### T-001: 新增 typed enums 到 config.rs

- Spec 参考: Models/CoreConfig.md § AiConfig 内嵌枚举
- 现有代码: crates/tfp-core/src/models/config.rs 中 EndpointType 附近
- 产出: 同文件新增枚举定义
- 契约:
  ```rust
  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum AiProviderType {
      #[default]
      OpenAiCompatible,
      AzureOpenAi,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum AzureAuthMode {
      #[default]
      ApiKey,
      #[serde(rename = "aad")]
      Aad,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum ApiKeyHeaderMode {
      #[default]
      Auto,
      #[serde(rename = "api_key")]
      ApiKeyHeader,
      Bearer,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum TextApiProtocolMode {
      #[default]
      Auto,
      ChatCompletionsV1,
      ChatCompletionsRaw,
      Responses,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum ImageApiRouteMode {
      #[default]
      Auto,
      V1Images,
      ImagesRaw,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum ImageEditMode {
      V1Multipart,
      #[default]
      V2ResponsesApi,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
  #[serde(rename_all = "snake_case")]
  pub enum SpeechCapabilityFlag {
      RealtimeSpeechToText,
      BatchSpeechToText,
      TextToSpeech,
  }
  ```
- 业务逻辑: 纯类型定义。SpeechCapabilityFlag 用 Vec 模式（和 ModelCapability 保持一致）而非 bitflags crate
- 测试: 每个枚举的 serde round-trip 测试
- **自测**: cargo test -p tfp-core

### T-002: 升级 AiEndpoint 字段类型 + 补齐缺失字段

- Spec 参考: Models/AiEndpointAndConfig.md § AiEndpoint 字段
- 现有代码: crates/tfp-core/src/models/config.rs:103-182
- 产出: 修改 AiEndpoint struct
- 契约: AiEndpoint 变更为：
  ```rust
  pub struct AiEndpoint {
      pub id: String,
      pub name: String,
      pub endpoint_type: EndpointType,
      pub url: String,
      #[serde(default)]
      pub api_key: String,
      pub api_version: Option<String>,
      pub region: Option<String>,
      #[serde(default)]
      pub models: Vec<AiModelEntry>,
      pub enabled: bool,
      // ↓ 从 String 升级为 typed enum（向后兼容已有 JSON）
      #[serde(default)]
      pub auth_header_mode: ApiKeyHeaderMode,
      #[serde(default)]
      pub auth_mode: AzureAuthMode,
      // ↓ 新增字段
      #[serde(default)]
      pub profile_id: String,
      #[serde(default)]
      pub provider_type: AiProviderType,
      #[serde(default)]
      pub text_api_protocol_mode: TextApiProtocolMode,
      #[serde(default)]
      pub image_api_route_mode: ImageApiRouteMode,
      #[serde(default)]
      pub speech_capabilities: Vec<SpeechCapabilityFlag>,
      // ↓ 已有字段（保持）
      #[serde(default)]
      pub azure_tenant_id: String,
      #[serde(default)]
      pub azure_client_id: String,
      #[serde(default)]
      pub speech_subscription_key: String,
      #[serde(default)]
      pub speech_region: String,
      #[serde(default)]
      pub speech_endpoint: String,
  }
  ```
- 业务逻辑:
  1. migrate_auth_header_mode() 方法签名不变，内部改为操作 enum 值：ApiKeyHeaderMode::Auto → 根据 is_azure() 设置为 ApiKeyHeader 或 Bearer
  2. Default impl 需对齐新字段
  3. **向后兼容**：现有 JSON 中 "auth_header_mode": "api_key" 必须能反序列化为 ApiKeyHeaderMode::ApiKeyHeader
- 测试: 更新 test_ai_endpoint_migrate_auth、test_ai_endpoint_json_fields、make_endpoint() helper
- **自测**: cargo test -p tfp-core

### T-003: 补齐 AiModelEntry.group_name

- Spec 参考: Models/AiEndpointAndConfig.md § AiModelEntry
- 现有代码: crates/tfp-core/src/models/config.rs:232-245
- 产出: 修改 AiModelEntry struct
- 契约:
  ```rust
  pub struct AiModelEntry {
      pub model_id: String,
      pub display_name: String,
      pub deployment_name: Option<String>,
      pub capabilities: Vec<ModelCapability>,
      #[serde(default)]
      pub group_name: Option<String>,  // 新增
  }
  ```
- 业务逻辑: 纯字段添加，默认 None
- 测试: 验证 group_name 在 JSON 中出现/缺失均可反序列化
- **自测**: cargo test -p tfp-core

### T-004: 补齐 MediaSettings 缺失字段

- Spec 参考: Models/AiEndpointAndConfig.md § MediaGenConfig 字段清单
- 现有代码: crates/tfp-core/src/models/settings.rs:30-75
- 产出: 修改 MediaSettings struct
- 契约: 新增字段：
  ```rust
  // 新增到 MediaSettings
  #[serde(default = "default_image_model_name")]
  pub image_model_name: String,           // "gpt-image-1"
  #[serde(default = "default_video_model_name")]
  pub video_model_name: String,           // "sora-2"
  #[serde(default)]
  pub image_edit_mode: ImageEditMode,     // V2ResponsesApi
  #[serde(default = "default_input_fidelity")]
  pub input_fidelity: String,            // "auto"
  #[serde(default = "default_true")]
  pub enable_chat_image_generation: bool, // true
  #[serde(default = "default_video_width")]
  pub video_width: u32,                  // 1280
  #[serde(default = "default_video_height")]
  pub video_height: u32,                 // 720
  #[serde(default)]
  pub default_enable_studio_reasoning: bool,    // false
  #[serde(default)]
  pub default_enable_studio_web_search: bool,   // false
  #[serde(default = "default_max_conversation_turns")]
  pub default_max_conversation_turns: u32,      // 20
  #[serde(default = "default_max_loaded_sessions")]
  pub max_loaded_sessions_in_memory: u32,       // 8
  #[serde(default)]
  pub output_directory: String,                 // ""
  ```
- 业务逻辑: 纯字段补齐 + 对应 default helpers。需从 config.rs 引入 ImageEditMode
- 测试: test_media_settings_default 更新 + 从 {} 反序列化验证
- **自测**: cargo test -p tfp-core

### T-005: 新增 enums 文件（通用处理状态和音频枚举）

- Spec 参考: Models/EnumsAndSmallModels.md § 全部
- 现有代码: 新建 crates/tfp-core/src/models/enums.rs
- 产出: crates/tfp-core/src/models/enums.rs + mod.rs 注册
- 契约:
  ```rust
  // ── 处理状态 ──
  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum ProcessingDisplayState {
      #[default]
      None,
      Pending,
      Running,
      Partial,
      Completed,
      Failed,
      Removed,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum StageContentState {
      #[default]
      Empty,
      Processing,
      Ready,
  }

  // ── 音频 ──
  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum AudioSourceMode {
      #[default]
      DefaultMic,
      CaptureDevice,
      Loopback,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum RecordingMode {
      #[default]
      LoopbackOnly,
      LoopbackWithMic,
      MicOnly,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum AudioPreProcessorPluginType {
      #[default]
      None,
      WebRtcApm,
  }

  // ── 文本编辑器 ──
  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
  #[serde(rename_all = "snake_case")]
  pub enum TextEditorType {
      Simple,
      Advanced,
  }

  // ── 配置级枚举 ──
  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum BatchLogLevel {
      #[default]
      Off,
      FailuresOnly,
      SuccessAndFailure,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum TranscriptionApiMode {
      #[default]
      Batch,
      Fast,
  }

  #[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
  #[serde(rename_all = "snake_case")]
  pub enum ServiceMode {
      #[default]
      SelfHosted,
      Cloud,
  }
  ```
- 业务逻辑: 纯类型定义
- 测试: 每个枚举至少 1 个 serde round-trip 测试
- **自测**: cargo test -p tfp-core

### T-006: 新增 cloud.rs（Cloud 相关小模型）

- Spec 参考: Models/EnumsAndSmallModels.md § Cloud 模型
- 现有代码: 新建 crates/tfp-core/src/models/cloud.rs
- 产出: crates/tfp-core/src/models/cloud.rs + mod.rs 注册
- 契约:
  ```rust
  use super::enums::ServiceMode;

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct CloudSettings {
      #[serde(default)]
      pub mode: ServiceMode,
      #[serde(default)]
      pub backend_url: String,
      #[serde(default)]
      pub aad_tenant_id: String,
      #[serde(default)]
      pub aad_client_id: String,
      #[serde(default)]
      pub aad_scope: String,
  }
  impl Default for CloudSettings { ... }

  #[derive(Debug, Clone, Serialize, Deserialize, Default)]
  pub struct CloudUserProfile {
      #[serde(default)]
      pub user_id: String,
      #[serde(default)]
      pub display_name: String,
      #[serde(default)]
      pub email: String,
      #[serde(default = "default_subscription")]
      pub subscription: String,  // "free"
      #[serde(default)]
      pub is_admin: bool,
      #[serde(default)]
      pub quotas: HashMap<String, QuotaInfo>,
  }

  #[derive(Debug, Clone, Serialize, Deserialize, Default)]
  pub struct QuotaInfo {
      pub used: i64,
      pub limit: i64,
  }
  impl QuotaInfo {
      pub fn remaining(&self) -> i64 { self.limit - self.used }
  }
  ```
- 业务逻辑: CloudSettings 要加入 AppConfig（新增 cloud: CloudSettings 字段）
- 测试: Default 值测试 + serde round-trip
- **自测**: cargo test -p tfp-core

### T-007: 新增 common.rs（通用小模型）

- Spec 参考: Models/EnumsAndSmallModels.md § 杂项小模型, Models/AiEndpointAndConfig.md § EndpointTemplateDefinition / ModelOption
- 现有代码: 新建 crates/tfp-core/src/models/common.rs
- 产出: crates/tfp-core/src/models/common.rs + mod.rs 注册
- 契约:
  ```rust
  use super::config::{EndpointType, ModelReference};

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct EndpointTemplateDefinition {
      pub profile_id: String,
      pub endpoint_type: EndpointType,
      pub display_name: String,
      #[serde(default)]
      pub subtitle: String,
      #[serde(default)]
      pub glyph: String,
      #[serde(default)]
      pub summary: String,
      #[serde(default)]
      pub default_name_prefix: String,
      #[serde(default)]
      pub default_api_version: String,
      #[serde(default)]
      pub icon_asset_path: String,
      #[serde(default)]
      pub supports_aad: bool,
  }

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct ModelOption {
      pub reference: ModelReference,
      pub endpoint_name: String,
      pub model_display_name: String,
      pub endpoint_type: EndpointType,
  }
  impl ModelOption {
      pub fn display_string(&self) -> String {
          format!("{} / {}", self.endpoint_name, self.model_display_name)
      }
  }

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct SubtitleCue {
      pub start_ms: i64,
      pub end_ms: i64,
      pub text: String,
  }
  impl SubtitleCue {
      pub fn display_text(&self, max_len: usize) -> String { ... }  // 截断 + "…"
      pub fn range_text(&self) -> String { ... }  // "HH:MM:SS → HH:MM:SS"
  }

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct ReviewSheetPreset {
      pub name: String,
      pub file_tag: String,
      pub prompt: String,
      #[serde(default = "default_true")]
      pub include_in_batch: bool,
      #[serde(default = "default_true")]
      pub is_enabled: bool,
  }

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct InsightPresetButton {
      pub name: String,
      pub prompt: String,
  }

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct UpdateInfo {
      pub latest_version: String,
      #[serde(default)]
      pub download_url: String,
      #[serde(default)]
      pub release_page_url: String,
      #[serde(default)]
      pub release_notes: String,
      #[serde(default)]
      pub asset_size: i64,
  }

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct AzureTenantInfo {
      pub tenant_id: String,
      #[serde(default)]
      pub display_name: String,
  }
  impl AzureTenantInfo {
      pub fn display_string(&self) -> String { ... }
  }

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct ReviewTimeLink {
      pub time_ms: i64,
      pub time_text: String,
      pub label: String,
  }

  #[derive(Debug, Clone, Serialize, Deserialize)]
  pub struct AudioFileProcessingSnapshot {
      pub audio_path: String,
      pub state: ProcessingDisplayState,
      #[serde(default)]
      pub badge_text: String,
      #[serde(default)]
      pub detail_text: String,
  }
  ```
- 业务逻辑: SubtitleCue.display_text 截断为 max_len 字符 + "…", range_text 格式化时间
- 测试: 各 struct serde round-trip + SubtitleCue display_text 截断逻辑
- **自测**: cargo test -p tfp-core

### T-008: 更新 mod.rs 和 AppConfig 集成

- Spec 参考: Models/CoreConfig.md § AzureSpeechConfig 主类字段（cloud 引用）
- 现有代码: crates/tfp-core/src/models/mod.rs:1-16, config.rs AppConfig struct
- 产出: mod.rs 新增 3 个 mod 注册 + AppConfig 新增 cloud 字段
- 契约:
  ```rust
  // mod.rs 新增:
  mod enums;
  mod cloud;
  mod common;
  pub use enums::*;
  pub use cloud::*;
  pub use common::*;

  // AppConfig 新增:
  #[serde(default)]
  pub cloud: CloudSettings,
  ```
- 业务逻辑: 保证所有新类型从 tfp_core::models 可公开访问
- 测试: 更新 test_app_config_roundtrip + test_app_config_json_fields
- **自测**: cargo test -p tfp-core

### T-009: 更新所有受影响的现有测试

- Spec 参考: N/A（维护性任务）
- 现有代码: crates/tfp-core/src/models/mod.rs tests module
- 产出: 修改现有测试以适配新字段类型
- 契约: 所有涉及 AiEndpoint 构造的测试（make_endpoint() helper、test_ai_endpoint_migrate_auth、test_ai_endpoint_json_fields）需更新字段类型
- 业务逻辑:
  - auth_header_mode: "api_key".into() → auth_header_mode: ApiKeyHeaderMode::ApiKeyHeader
  - auth_mode: "api_key".into() → auth_mode: AzureAuthMode::ApiKey
  - make_endpoint() helper 补齐新字段的默认值
  - test_ai_endpoint_migrate_auth: 用 enum 值验证
  - test_migrate_auth_no_op_when_not_auto: 用 enum 值
- 测试: 原有 44 个测试全部通过 + 新增测试通过
- **自测**: cargo test -p tfp-core

## 技术决策记录

| 编号 | 决策 | Spec 依据 | 与 C# 差异（如有） |
|------|------|-----------|-------------------|
| D-001 | SpeechCapability 用 Vec\<SpeechCapabilityFlag\> 而非 bitflags | 与 ModelCapability 保持一致 | C# 用 [Flags] enum，Rust 用 Vec + contains() |
| D-002 | auth_header_mode/auth_mode 从 String 升级为 typed enum | CoreConfig.md § AiConfig 枚举 | 同语义，更安全 |
| D-003 | ApiKeyHeaderMode::ApiKeyHeader 序列化为 "api_key" | 与 Rust2 已有 JSON 数据兼容 | C# 枚举名 ApiKeyHeader，序列化为 ApiKeyHeader |
| D-004 | ImageEditMode default = V2ResponsesApi | AiEndpointAndConfig.md § MediaGenConfig | 与 C# 默认值一致 |
| D-005 | AudioDeviceType 保持 Input/Output/Loopback 命名 | 已有代码和前端协议已建立 | C# 用 Capture/Render |
| D-006 | SubtitleCue 用 start_ms/end_ms (i64) 替代 TimeSpan | Rust 无 TimeSpan 类型，毫秒更简洁 | C# 用 TimeSpan |
| D-007 | 不引入 bitflags crate | 保持依赖最小化，Vec 模式已足够 | N/A |

## 后续影响

- 本批次完成后，batch-1（图片生成多路由）可直接使用 ImageApiRouteMode、ImageEditMode、TextApiProtocolMode 枚举做路由决策
- batch-4（AI 聊天完善）可使用 TextApiProtocolMode
- batch-6（配置持久化）可使用 EndpointTemplateDefinition、CloudSettings
- ⚠️ 注意：AiConfig 中的 PresetButtons/ReviewSheets 列表已在 common.rs 定义了元素类型，但完整配置结构将在 batch-4/batch-6 中补齐

## 禁止事项

- ❌ 不要添加 bitflags 依赖
- ❌ 不要改动 api.rs 中已有的 VideoApiMode（它已被其他 crate 使用）
- ❌ 不要改动 AudioDeviceType 枚举的 Input/Output/Loopback 命名
- ❌ 不要在本批次实现任何业务方法（仅数据定义 + 简单 helper）
- ❌ 不要改动 studio.rs/center.rs/audiolab.rs/live.rs 中的现有代码

## 退出标准

- cargo check -p tfp-core — 0 errors, 0 warnings
- cargo test -p tfp-core — 全绿（≥ 44 原有 + ≥ 20 新增 ≈ 64+ tests）
- 从 {} 空 JSON 反序列化 AppConfig 不 panic（backward compat 验证）
- cargo check on workspace root — 0 errors（其他 crate 不被破坏）
