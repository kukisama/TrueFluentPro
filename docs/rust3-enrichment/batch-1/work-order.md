# 批次 1 施工单
> 日期：2026-04-29
> 路线图阶段：Phase 1 — 核心管线补全
> 本阶段进度：1/8

## 目标

升级 openai_image.rs 支持完整 3 路由模式（传统 /images/generations、传统 /images/edits multipart、Responses API）+ 候选 URL 回退机制。

## Spec 来源

| 文档 | 相关段落 |
|------|---------|
| Services/AiImageGenService.md | § 路由决策逻辑、§ 流程1-4、§ URL 候选回退 |
| Services/EndpointProfileUrlBuilder.md | § 图片 URL 候选列表 |

## Rust3 现状

- openai_image.rs 已有：
  - `build_url_candidates()` 基础版（仅 /images/generations）
  - `generate_via_responses_v2()` 基础版（固定单 URL，无候选回退）
  - `generate()` trait 实现（仅 /images/generations 路线）
- 缺失：
  - 路由决策（ShouldUseResponsesApi 逻辑）
  - /images/edits multipart 上传路线
  - 候选 URL 自动回退（404/405 → next candidate）
  - ImageGenerationResult 结构补全（ResponseId、token usage）
  - EditUrl 候选构建
  - Responses API 候选 URL 列表（多候选回退）
  - 图片解码（base64/url）通用解析

## 前置条件

- batch-0 ✅（typed enums: ImageEditMode, ImageApiRouteMode 已就位）

## 运行时假设

- 本批次不涉及 file_id 上传（→ batch-2）
- 本批次不涉及 Pipeline 实验路径（→ 未定）
- 仅需 unit test + mock HTTP（不需要真实 API 调通）

## 任务清单

### T-001: 补全 ImageGenerationResult 结构

- 现有代码: crates/tfp-core/src/models/api.rs 中 ImageGenResult
- 产出: 补充字段
- 契约:
  ```rust
  pub struct ImageGenResult {
      pub image_bytes: Vec<u8>,
      pub format: String,
      // ↓ 新增
      pub response_id: Option<String>,
      pub request_url: String,
      pub attempted_urls: Vec<String>,
      pub generate_seconds: f64,
      pub download_seconds: f64,
      pub actual_input_tokens: Option<u32>,
      pub actual_output_tokens: Option<u32>,
  }
  ```
- 业务逻辑: 纯字段新增，默认值为 0.0/None/空
- 测试: 原有测试需加 `..Default::default()` 或补新字段
- **自测**: cargo test -p tfp-core

### T-002: 路由决策函数

- 现有代码: openai_image.rs 无此逻辑
- 产出: 新增私有方法 `should_use_responses_api()`
- 契约:
  ```rust
  fn should_use_responses_api(&self, request: &ImageGenRequest) -> bool {
      // 1. ImageEditMode::V1Multipart → false
      // 2. request.text_model.is_some() → true
      // 3. 否则 → false
  }
  ```
- 依赖: MediaSettings.image_edit_mode 已由 batch-0 定义
- 测试: 3 个单元测试覆盖 3 条分支

### T-003: 候选 URL 构建（完整版）

- 现有代码: `build_url_candidates()` 仅 generations
- 产出: 重写为 3 个构建函数
- 契约:
  ```rust
  pub(crate) fn build_generate_urls(&self) -> Vec<String>
  pub(crate) fn build_edit_urls(&self) -> Vec<String>
  pub(crate) fn build_responses_urls(&self) -> Vec<String>
  ```
- 规则（按 endpoint_type）:
  - **AzureOpenAi**:
    - generate: `{base}/openai/v1/images/generations`
    - edit: `{base}/openai/v1/images/edits`
    - responses: `{base}/openai/v1/responses`
  - **ApiManagementGateway**:
    - generate: `{base}/v1/images/generations`, `{base}/images/generations?api-version={ver}`, `{base}/images/generations`
    - edit: `{base}/v1/images/edits`, `{base}/images/edits?api-version={ver}`, `{base}/images/edits`
    - responses: `{base}/v1/responses`, `{base}/responses?api-version={ver}`, `{base}/responses`
  - **其他（OpenAiCompatible 等）**:
    - generate: `{base}/v1/images/generations` 或 `{base}/images/generations`
    - edit: `{base}/v1/images/edits` 或 `{base}/images/edits`
    - responses: `{base}/v1/responses` 或 `{base}/responses`
- 测试: 3 × 3 = 9 个 URL 构建测试（每种 endpoint_type × 每种路由）

### T-004: 候选 URL 回退执行器

- 现有代码: generate() 只尝试单个 URL
- 产出: 新增私有 async helper
- 契约:
  ```rust
  async fn try_candidates(
      &self,
      urls: &[String],
      build_request: impl Fn(&str) -> reqwest::RequestBuilder,
  ) -> Result<(reqwest::Response, String, Vec<String>), ProviderError>
  ```
- 逻辑:
  1. 逐个 URL 发送请求
  2. 成功（2xx）→ 返回 (response, success_url, attempted_urls)
  3. 404/405 → 继续下一个
  4. 其他错误 → 直接返回错误
  5. 全部失败 → 返回最后一个错误
- 测试: 至少 3 个测试（成功/回退成功/全失败）

### T-005: 重写 generate() — 双路由

- 现有代码: generate() trait 实现
- 产出: 重写为路由决策 + 候选回退
- 契约:
  ```rust
  async fn generate(&self, request: &ImageGenRequest) -> Result<Vec<ImageGenResult>, ProviderError> {
      if self.should_use_responses_api(request) {
          self.generate_via_responses(request).await
      } else {
          self.generate_via_images_api(request).await
      }
  }
  ```
- 子方法:
  - `generate_via_images_api()` — 传统 /images/generations + 候选回退
  - `generate_via_responses()` — 升级版 Responses API + 候选回退
- 测试: 集成现有 mock 测试验证路由切换

### T-006: 图片编辑 multipart 请求构建

- 现有代码: 无
- 产出: 新增方法
- 契约:
  ```rust
  async fn edit_via_multipart(
      &self,
      request: &ImageGenRequest,
      reference_image_path: &str,
  ) -> Result<Vec<ImageGenResult>, ProviderError>
  ```
- 逻辑:
  1. 读取参考图文件字节
  2. 确定 MIME 类型（按扩展名）
  3. 构建 multipart form: image(二进制) + prompt + model + size + quality
  4. 使用 build_edit_urls() + try_candidates 发送
  5. 解析响应（复用响应解析逻辑）
- 依赖: reqwest multipart
- 测试: 1 个测试验证 multipart 构建正确

### T-007: 响应解析器统一化

- 现有代码: generate() 中内联解析 data[].b64_json
- 产出: 提取为独立函数
- 契约:
  ```rust
  fn parse_image_response(
      body: &serde_json::Value,
      request_url: &str,
      attempted_urls: Vec<String>,
  ) -> Result<Vec<ImageGenResult>, ProviderError>
  ```
- 支持格式:
  - `data[].b64_json` → base64 解码
  - `data[].url` → 暂存 URL 字符串（后续批次加 GET 下载）
  - `output[].type=="image_generation_call" && result` → base64（Responses API 格式）
- 提取: `response.id` → ImageGenResult.response_id
- 提取: `usage.input_tokens` / `usage.output_tokens` → 对应字段
- 测试: 4 个测试（b64/url/responses格式/带usage）

## 退出标准

- [ ] cargo check 0 errors/warnings
- [ ] cargo test 全绿（新增 ≥20 测试）
- [ ] `build_generate_urls` / `build_edit_urls` / `build_responses_urls` 对 3 种 endpoint_type 输出正确
- [ ] `should_use_responses_api` 3 分支有测试
- [ ] `parse_image_response` 支持 3 种格式
- [ ] 候选 URL 回退逻辑有测试（首个失败→第二个成功）
- [ ] ImageGenResult 新字段不破坏现有 JSON 合约（#[serde(default)]）

## 预估代码增量

~800 行（含测试）

## 设计决策记录

| 编号 | 决策 | 理由 |
|------|------|------|
| D-001 | try_candidates 返回 Response 而非解析后结果 | 调用方可能需要读取 headers (如 x-request-id) |
| D-002 | parse_image_response 接受 serde_json::Value 而非 bytes | 调用方可能需要检查 status_code 后再决定解析方式 |
| D-003 | data[].url 格式暂不做 GET 下载，只存 URL | 下载逻辑复杂（进度回调），留到后续批次 |
| D-004 | multipart edit 不含 file_id 缓存逻辑 | file_id 属于 batch-2 范围 |
| D-005 | 404/405 继续尝试，其他 4xx/5xx 直接报错 | 与 C# 行为对齐 |