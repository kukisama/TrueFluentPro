# 批次 2 施工单
> 日期：2026-04-29
> 路线图阶段：Phase 1 — 核心管线补全
> 本阶段进度：2/8（参见 batch-progress.md）

## 目标

为 OpenAiImageProvider 补充 Files API 上传能力 + Responses API 图片编辑路线（file_id 引用方式），实现完整的 "有参考图" 图片生成路由，并将 FileIdCache 上移至 AppState 使其在 Tauri 命令层可复用。

## Spec 来源

| 文档 | 相关段落 |
|------|---------|
| Services/AiImageGenService.md | § 流程2: Responses API 编辑图片（SendImageEditV2RequestAsync） |
| Services/AiImageGenService.md | § 流程5: 文件上传（UploadFileAsync） |
| Services/AiImageGenService.md | § 流程1 步骤 2: 路由判定（有参考图分支） |
| Services/EndpointProfileUrlBuilder.md | § BuildFileUploadUrlCandidates |

## Rust3 现状

- **已有**：
  - FileIdCache 在 crates/tfp-media/src/file_cache.rs — 完整实现，含 SHA256 键、12h TTL、evict_expired
  - upload_file_bytes() 在 crates/tfp-media/src/image_pipeline.rs:334-370 — 仅供 pipeline 内部使用
  - OpenAiImageProvider batch-1 新增的 edit_via_multipart() — V1 multipart 路线（可用）
  - should_use_responses_api() — 已判定是否走 Responses API
  - build_responses_urls() — Responses API 候选 URL
  - generate() 路由 — 当前仅 images_api / responses，未处理 "有参考图" 场景
- **缺失**：
  - build_file_upload_urls() — 文件上传 URL 候选列表
  - upload_file() 方法 — Provider 级文件上传（带候选 URL 回退）
  - edit_via_responses_api() — Responses API 编辑（构建 input_image file_id 引用）
  - generate() "有参考图" 路由分支
  - FileIdCache 在 AppState 中的共享实例
  - Tauri 命令层的 upload→generate 编排

## 前置条件

- batch-0 ✅（ImageEditMode 枚举）
- batch-1 ✅（should_use_responses_api、build_responses_urls、edit_via_multipart、try_candidates）

## 运行时假设

- 文件上传目标：POST {base}/v1/files 或 {base}/openai/v1/files
- form-data 字段：file（二进制 Part）+ purpose=assistants
- APIM 限制：purpose 只能是 assistants（参见 PLATFORM-NOTES.md）
- Responses API 编辑：model = 文本模型，x-ms-oai-image-generation-deployment = 图片模型
- input 结构：[{"type":"input_text","text":...}, {"type":"input_image","file_id":...}]
- 本批次不涉及 mask 图（仅 reference 图）
- **自测方法**：如有 Rust2 数据库中的 APIM endpoint，可用 #[ignore] 集成测试验证上传

## 任务清单

### T-001: 补充 ImageGenRequest 字段 — 预上传 file_id

- 现有代码: crates/tfp-core/src/models/api.rs:58-79（ImageGenRequest）
- 产出: 新增字段
- 契约:
  ```rust
  // 在 ImageGenRequest 中新增：
  #[serde(default)]
  pub uploaded_file_ids: Vec<String>,
  ```
- 业务逻辑: 纯字段新增。前端/命令层在调用 generate 之前先上传图片取得 file_ids，填入此字段。Provider 层不负责上传（解耦）。
- 测试: 确保现有 JSON 反序列化不因缺少此字段而失败（#[serde(default)] 保障）
- **自测**: cargo test -p tfp-core

### T-002: build_file_upload_urls()

- 现有代码: openai_image.rs 无此方法
- 产出: 新增 pub(crate) 方法
- 契约:
  ```rust
  pub(crate) fn build_file_upload_urls(&self) -> Vec<String>
  ```
- 规则（按 endpoint_type）:
  - **AzureOpenAi**: ["{base}/openai/v1/files"]
  - **ApiManagementGateway**: ["{base}/v1/files", "{base}/openai/v1/files"]
  - **其他**: ["{base}/v1/files"] 或 base 已含 /v1 则 ["{base}/files"]
- Spec 参考: EndpointProfileUrlBuilder.md § BuildFileUploadUrlCandidates
- 测试: 3 个测试（每种 endpoint_type）
- **自测**: cargo test -p tfp-providers

### T-003: upload_file() 方法

- 现有代码: 无（pipeline 中有类似但不可复用）
- 产出: 新增 pub async 方法
- 契约:
  ```rust
  pub async fn upload_file(
      &self,
      file_path: &str,
      file_bytes: &[u8],
  ) -> Result<String, ProviderError>
  ```
- 业务逻辑（Spec § 流程5）:
  1. 确定文件名（从 path 提取）
  2. 确定 MIME（复用 mime_from_extension）
  3. 构建 multipart form: file（Part with 文件名+MIME）+ purpose=assistants
  4. 使用 build_file_upload_urls() 获取候选 URL
  5. 逐候选 URL 尝试（内联重试，因 multipart 不可 Clone）
  6. 解析响应 JSON → 提取 id 字段
  7. 返回 file_id 字符串
- 错误处理: 404/405→继续，401/403→Auth，429→RateLimited，无 id 字段→Internal
- 测试: 1 个单元测试验证参数构建（无网络）
- **自测**: #[ignore] 集成测试 — upload real file to APIM（如有 endpoint）

### T-004: edit_via_responses_api() — Responses API 编辑（有参考图 + file_id）

- 现有代码: generate_via_responses() 仅处理纯文生图
- 产出: 新增私有 async 方法
- 契约:
  ```rust
  async fn edit_via_responses_api(
      &self,
      request: &ImageGenRequest,
  ) -> Result<Vec<ImageGenResult>, ProviderError>
  ```
- 业务逻辑（Spec § 流程2）:
  1. 确定 text_model（request.text_model 或 fallback request.model）
  2. 确定 image_model（request.image_model 或 fallback request.model）
  3. 构建 input 数组:
     - {"type": "input_text", "text": request.prompt}
     - 对 request.uploaded_file_ids 中每个 fid: {"type": "input_image", "file_id": fid}
  4. 构建 body: {model: text_model, input: [...], tools: [{"type":"image_generation"}]}
  5. 可选: previous_response_id
  6. Headers: x-ms-oai-image-generation-deployment: image_model
  7. 使用 build_responses_urls() + try_candidates 发送
  8. 解析响应（复用 parse_image_response_from_reqwest）
- 与 generate_via_responses 差异: input 包含 file_id 引用，不只是纯文本
- 测试: 1 个单元测试验证 JSON body 构建正确
- **自测**: #[ignore] 集成测试 — upload file + edit via responses（如有 endpoint）

### T-005: 重写 generate() 路由 — 完整 "有参考图" 分支

- 现有代码: openai_image.rs 的 ImageGenSlot::generate() 实现
- 产出: 扩展路由逻辑
- 契约（与 Spec § 流程1 步骤 2 对齐）:
  ```rust
  async fn generate(&self, request: &ImageGenRequest) -> Result<Vec<ImageGenResult>, ProviderError> {
      let has_reference = request.reference_image_path.is_some()
          || !request.uploaded_file_ids.is_empty();

      if has_reference {
          if self.should_use_responses_api(request) {
              self.edit_via_responses_api(request).await
          } else {
              let path = request.reference_image_path.as_deref()
                  .ok_or(ProviderError::Internal("reference_image_path required for V1 edit".into()))?;
              self.edit_via_multipart(request, path).await
          }
      } else {
          if self.should_use_responses_api(request) {
              self.generate_via_responses(request).await
          } else {
              self.generate_via_images_api(request).await
          }
      }
  }
  ```
- 路由逻辑归纳（4 条路径）:
  1. 有参考图 + Responses → edit_via_responses_api
  2. 有参考图 + 非Responses → edit_via_multipart
  3. 无参考图 + Responses → generate_via_responses
  4. 无参考图 + 非Responses → generate_via_images_api
- 测试: 4 个路由测试（mock 验证调用了正确的内部方法）
- **自测**: cargo test -p tfp-providers

### T-006: AppState 集成 FileIdCache + upload_image_file Tauri 命令

- 现有代码:
  - src-tauri/src/state.rs — AppState 定义
  - src-tauri/src/commands/media.rs — generate_image 命令
- 产出:
  1. AppState 新增 file_id_cache: Arc<FileIdCache> 字段
  2. 新增 Tauri 命令 upload_image_file
- 契约:
  ```rust
  // state.rs 新增字段
  pub file_id_cache: Arc<tfp_media::FileIdCache>,

  // commands/media.rs 新增命令
  #[tauri::command]
  pub async fn upload_image_file(
      state: State<'_, AppState>,
      endpoint_id: String,
      file_path: String,
  ) -> Result<String, String>
  ```
- 业务逻辑:
  1. 读取文件字节 (tokio::fs::read)
  2. 查 FileIdCache — 命中则直接返回 cached file_id
  3. 未命中 → 获取 provider → 调用 provider.upload_file(path, bytes)
  4. 写入 FileIdCache
  5. 返回 file_id
- **设计决策**: 在 ImageGenSlot trait 上新增 upload_file 方法（带默认实现返回 Unsupported），仅 OpenAiImageProvider 实现。
- 测试: 编译通过即可（需要 AppState mock 才能测命令，超出范围）
- **自测**: cargo check（含 src-tauri）

### T-007: 前端类型同步

- 现有代码: src/lib/types.ts 的 ImageGenRequest 接口
- 产出: 新增字段 + 新增 upload 函数声明
- 契约:
  ```typescript
  // types.ts ImageGenRequest 新增：
  uploaded_file_ids?: string[];

  // tauri-api.ts 或 types.ts 新增：
  export async function uploadImageFile(endpointId: string, filePath: string): Promise<string>;
  ```
- 测试: npx tsc --noEmit 通过
- **自测**: tsc

## 技术决策记录

| 编号 | 决策 | Spec 依据 | 与 C# 差异 |
|------|------|-----------|-----------|
| D-001 | upload_file 放在 ImageGenSlot trait（带默认 Unsupported 实现） | Spec: UploadImageFileAsync 是 service 的 public 方法 | C# 同 class；Rust trait 扩展 |
| D-002 | 文件上传在 Tauri 命令层编排（先 upload 再 generate） | Spec § 流程2 串行 upload→build→send | C# service 内部串行；Rust 命令层编排 |
| D-003 | FileIdCache 实例存在 AppState | Spec: FileIdCache 是 DI 注入单例 | C# 构造函数注入；Rust State 共享 |
| D-004 | uploaded_file_ids 通过 request 传递 | Spec § 流程2 步骤 2 | C# service 自行上传；Rust 职责分离 |
| D-005 | 不支持 mask 图上传 | Spec § 流程2 仅提到 reference 图 | C# 有 mask 但 API 不支持 |

## 后续影响

- 本批次完成后，batch-3（视频生成）可参考 try_candidates 模式
- 本批次完成后，Pipeline 的 step_upload 可改为调用 provider.upload_file() + AppState cache
- ⚠️ edit_via_responses_api 的真实 API 调通依赖有效的 APIM endpoint + 已部署的 gpt-image-1

## 禁止事项

- 不要移动 FileIdCache 到其他 crate（保持在 tfp-media，通过 AppState 共享即可）
- 不要给 tfp-providers 增加对 tfp-media 的依赖（会造成循环依赖）
- 不要实现 mask 图上传（scope creep）
- 不要实现进度回调（Spec 的 onProgress，留到 Pipeline 相关批次）
- 不要修改现有 pipeline 中的 upload_file_bytes（它自洽，保持不动）

## 退出标准

- [ ] cargo check 0 errors/warnings（Rust3 workspace）
- [ ] cargo test 全绿（新增 ≥10 测试）
- [ ] build_file_upload_urls 对 3 种 endpoint_type 输出正确
- [ ] upload_file() 编译通过，签名正确（trait 方法 + OpenAiImageProvider 实现）
- [ ] edit_via_responses_api() 构建正确的 JSON body（含 input_image file_id）
- [ ] generate() 4 条路由分支均有测试覆盖
- [ ] AppState.file_id_cache 存在且编译通过
- [ ] upload_image_file Tauri 命令存在且编译通过
- [ ] npx tsc --noEmit 0 errors
- [ ] ImageGenRequest 新字段不破坏现有 JSON 合约

## 预估代码增量

~700 行（含测试）