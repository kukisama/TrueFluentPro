# 批次 3 施工单
> 日期：2026-04-29
> 路线图阶段：Phase 1 — 核心管线补全
> 本阶段进度：3/8（参见 batch-progress.md）

## 目标

为 OpenAiVideoProvider 补充候选 URL 体系（支持 Azure/APIM/兼容端点）、完善创建-轮询-下载全流程的多 URL 回退能力、增加 generation_id 追踪和多路径下载、补充状态机单元测试覆盖关键路径。

## Spec 来源

| 文档 | 相关段落 |
|------|---------|
| Services/AiMediaServiceBase.md | 视频 URL (BuildVideoCreateUrl/PollUrl/DownloadUrl 清单) |
| Services/EndpointProfileUrlBuilder.md | 视频 URL 构建（通用框架）VideoApiMode.Videos vs SoraJobs |
| ViewModels/Settings/VideoGenSectionVM.md | 流程4: 参数选项动态加载（API 模式检测） |

## Rust3 现状

- **已有**：
  - OpenAiVideoProvider with generate()/poll_status() — 基本可用但每种操作只用 1 条 URL，无候选回退
  - detect_api_mode() — 已实现（sora/sora-2 -> Videos，其他 -> SoraJobs）
  - create_sora_jobs() — 硬编码 /openai/v1/video/generations/jobs
  - create_videos() — 硬编码 /v1/videos（multipart）
  - poll_status() — 尝试 sora_url 和 videos_url 两个但非标准候选体系
  - video_service.rs — run_video_generation() 含完整 create-poll-download 循环
  - video_util.rs — determine_video_api_mode() + download_video()
  - VideoGenRequest / VideoGenResult — 基本字段
- **缺失**：
  - build_video_create_urls(mode) — 候选 URL 列表（按 endpoint_type 和 api_mode 分）
  - build_video_poll_urls(video_id, mode) — 轮询候选 URL 列表
  - build_video_download_urls(video_id, generation_id) — 下载候选 URL（含 /content/video 备用）
  - try_candidates 复用（在 video 内联实现）
  - generation_id 从 poll 响应提取和追踪
  - VideoGenResult 增加 generation_id 字段
  - 下载使用候选 URL 回退
  - poll_status 重写为使用 candidate URL 体系
  - 状态机单元测试（验证 URL 构建 + 状态转换）
  - APIM 端点的视频 URL 支持

## 前置条件

- batch-0 PASS（VideoApiMode 枚举）
- batch-1 PASS（try_candidates 模式已在 openai_image 中验证可行）
- batch-2 PASS（确认 APIM 候选 URL 模式有效）

## 运行时假设

- SoraJobs 模式 URL 模式:
  - 创建: {base}/openai/v1/video/generations/jobs?api-version={ver}
  - 轮询: {base}/openai/v1/video/generations/jobs/{video_id}?api-version={ver}
  - 下载: {base}/openai/v1/video/generations/jobs/{video_id}/content/video?api-version={ver}
  - 下载备用: {base}/openai/v1/video/generations/{generation_id}/content/video?api-version={ver}
- Videos 模式 URL 模式:
  - 创建: {base}/v1/videos
  - 轮询: {base}/v1/videos/{video_id}
  - 下载: {base}/v1/videos/{video_id}/content（或直接从 poll 拿 URL）
- APIM 端点额外候选: 无 openai 前缀的路径作为回退
- 默认 api-version: 2025-03-01-preview
- **自测方法**: 纯单元测试验证 URL 构建 + 状态转换逻辑（无网络依赖）

## 任务清单

### T-001: VideoGenResult 补充 generation_id 字段

- 现有代码: crates/tfp-core/src/models/api.rs:132-139 (VideoGenResult)
- 产出: 新增字段
- 契约:
  在 VideoGenResult 中新增：
  #[serde(default)]
  pub generation_id: Option<String>,
- 业务逻辑: 部分 API 响应中 generations[].id 与顶层 id 不同, generation_id 用于构建下载 URL。
- 测试: 确保现有反序列化不因缺少此字段而失败
- **自测**: cargo test -p tfp-core

### T-002: build_video_create_urls(mode) 方法

- 现有代码: openai_video.rs 无此方法（create_sora_jobs/create_videos 各硬编码 1 条 URL）
- 产出: 新增 pub(crate) 方法
- 契约:
  pub(crate) fn build_video_create_urls(&self, mode: &VideoApiMode) -> Vec<String>
- 规则（按 endpoint_type + mode）:
  - SoraJobs + AzureOpenAi: ["{base}/openai/v1/video/generations/jobs?api-version={ver}"]
  - SoraJobs + APIM: ["{base}/v1/video/generations/jobs", "{base}/openai/v1/video/generations/jobs?api-version={ver}", "{base}/video/generations/jobs?api-version={ver}"]
  - SoraJobs + 其他: ["{base}/v1/video/generations/jobs"]（或 base 含 /v1 则 "{base}/video/generations/jobs"）
  - Videos + AzureOpenAi: ["{base}/openai/v1/videos"]
  - Videos + APIM: ["{base}/v1/videos", "{base}/openai/v1/videos"]
  - Videos + 其他: ["{base}/v1/videos"]（或 base 含 /v1 则 "{base}/videos"）
- Spec 参考: EndpointProfileUrlBuilder.md 视频 URL 构建; AiMediaServiceBase.md BuildVideoCreateUrl
- 测试: 6 个测试（3 endpoint_type x 2 mode）
- **自测**: cargo test -p tfp-providers

### T-003: build_video_poll_urls(video_id, mode) 方法

- 现有代码: poll_status() 中内联构建 2 条 URL
- 产出: 新增 pub(crate) 方法
- 契约:
  pub(crate) fn build_video_poll_urls(&self, video_id: &str, mode: &VideoApiMode) -> Vec<String>
- 规则:
  - SoraJobs: 在 create URL 路径基础上追加 /{video_id}
  - Videos: 在 create URL 路径基础上追加 /{video_id}
  - 每种 endpoint_type 的候选顺序与 create 一致
- Spec 参考: AiMediaServiceBase.md BuildVideoPollUrl
- 测试: 4 个测试（AzureOpenAi x SoraJobs, APIM x SoraJobs, AzureOpenAi x Videos, 其他 x Videos）
- **自测**: cargo test -p tfp-providers

### T-004: build_video_download_urls(video_id, generation_id, mode) 方法

- 现有代码: 无
- 产出: 新增 pub(crate) 方法
- 契约:
  pub(crate) fn build_video_download_urls(
      &self,
      video_id: &str,
      generation_id: Option<&str>,
      mode: &VideoApiMode,
  ) -> Vec<String>
- 规则:
  - SoraJobs + Azure/APIM:
    1. "{base}/openai/v1/video/generations/jobs/{video_id}/content/video?api-version={ver}"
    2. 如有 generation_id: "{base}/openai/v1/video/generations/{gen_id}/content/video?api-version={ver}"
  - Videos:
    1. "{base}/v1/videos/{video_id}/content"
  - APIM 追加无 openai 前缀的备用候选
- Spec 参考: AiMediaServiceBase.md BuildVideoDownloadUrl / BuildVideoDownloadUrlVideoContent / BuildVideoGenerationDownloadUrl
- 测试: 4 个测试（含 generation_id 有/无两种情况 x 2 mode）
- **自测**: cargo test -p tfp-providers

### T-005: 重写 generate() — 使用候选 URL 体系

- 现有代码: openai_video.rs VideoGenSlot::generate() -> create_sora_jobs/create_videos
- 产出: 重写 create 逻辑使用内联候选尝试
- 契约（不变，内部重构）:
  async fn generate(&self, request: &VideoGenRequest) -> Result<VideoGenResult, ProviderError>
- 业务逻辑:
  1. detect_api_mode -> 确定 SoraJobs 或 Videos
  2. build_video_create_urls(mode) -> 获取候选列表
  3. 构建 request body:
     - SoraJobs: JSON {"model", "prompt", "size", "n"}
     - Videos: JSON {"model", "prompt", "size", "duration", "n"} + 可选 reference_image（暂留 TODO）
  4. 对候选 URL 逐一尝试（与 openai_image try_candidates 相同模式: 404->下一候选, 401->Auth, 429->RateLimited）
  5. 解析响应: 提取 id（顶层）和可选 generations[0].id 作为 generation_id
  6. 返回 VideoGenResult with video_id + generation_id + status="pending"
- 注意: Videos 模式的 reference_image multipart 留为 TODO 注释（当前只走 JSON 路线），因为需要验证实际 API 是否接受 JSON
- 错误处理: 与 openai_image try_candidates 一致
- 测试: 2 个 URL 验证测试已在 T-002 覆盖
- **自测**: cargo test -p tfp-providers

### T-006: 重写 poll_status() — 使用候选 URL + generation_id 提取

- 现有代码: openai_video.rs poll_status() — 手动尝试 2 条 URL
- 产出: 重写为候选体系
- 契约（签名不变）:
  async fn poll_status(&self, video_id: &str, endpoint_id: &str) -> Result<VideoGenResult, ProviderError>
- 业务逻辑:
  1. 先用 SoraJobs mode 的 poll URLs 尝试, 如全部 404/网络错误 再用 Videos mode
  2. 对成功的响应 JSON 提取:
     - status: json["status"] 字符串 (pending/running/completed/failed/cancelled)
     - download_url: json["generations"][0]["url"] 或 json["output"]["url"] 或 json["video"]["url"]
     - generation_id: json["generations"][0]["id"]
  3. 返回 VideoGenResult 含 generation_id 和 status
- 测试: 1 个 poll URL 构建测试
- **自测**: cargo test -p tfp-providers

### T-007: 增强 download_video_file() — fallback URLs 支持

- 现有代码: video_service.rs download_video_file(url, data_dir, task_id)
- 产出: 扩展签名支持 fallback URLs
- 契约:
  async fn download_video_file(
      primary_url: &str,
      fallback_urls: &[String],
      data_dir: &Path,
      task_id: &str,
  ) -> Result<String, String>
- 业务逻辑:
  1. 尝试 primary_url 下载
  2. 如失败（非 2xx）, 逐一尝试 fallback_urls
  3. 第一个成功的写入文件
  4. 全部失败 -> 返回最后错误
- 测试: 编译通过即可（需网络 mock 才能真测下载）
- **自测**: cargo check -p tfp-media

### T-008: video_service 集成 — 传递 generation_id 构建下载候选

- 现有代码: video_service.rs run_video_generation() — 仅用 poll.download_url
- 产出: 修改下载逻辑为使用 fallback URLs
- 业务逻辑:
  当 poll 返回 completed + download_url:
  1. primary_url = poll.download_url
  2. 构建简单 fallback URLs:
     - 如有 generation_id 且 base URL 可推断: 追加 /content/video 变体
     - 否则: 空 fallback 列表
  3. 调用 download_video_file(primary, fallbacks, data_dir, task_id)
- 注意: 由于 video_service 不持有 endpoint 信息，fallback 构建是简化版（从 download_url 推导），不如 provider 层完整。这是可接受的折中。
- 测试: 编译通过即可
- **自测**: cargo check

## 技术决策记录

| 编号 | 决策 | Spec 依据 | 与 C# 差异 |
|------|------|-----------|-----------|
| D-001 | 视频 URL 不走 Profile 系统，直接按 endpoint_type 硬编码规则 | Spec 说 Profile 驱动，但当前 Rust3 的 Profile 系统尚未完善到视频层 | C# 完全 Profile 驱动；Rust3 先硬编码再迁移 |
| D-002 | SoraJobs 的 api-version 由 endpoint.api_version 提供 | AiMediaServiceBase.md api_version | C# 同 |
| D-003 | Videos 模式创建改用 JSON（非 multipart），reference_image 留 TODO | Spec 未明确区分格式 | C# 用 multipart；Rust3 先 JSON（更通用） |
| D-004 | generation_id 仅在 SoraJobs 下有意义 | AiMediaServiceBase.md BuildVideoGenerationDownloadUrl | C# 同 |
| D-005 | 不在此批次实现 VideoCapabilityResolver | VideoGenSectionVM.md 流程4，属设置层 | 设置层留到 batch-31 |

## 后续影响

- 本批次完成后，视频生成的端点测试（batch-5）可直接使用 generate+poll 验证连通性
- 本批次完成后，Center 视频轮次（batch-27）可调用 provider 的完整管线
- 注意: Videos 模式的 JSON vs multipart 切换需在真实 API 测试时验证
- 注意: download_video_file 的 fallback URL 构建较简化，完整 Profile 驱动需等 Profile 系统扩展

## 禁止事项

- 不要修改 openai_image.rs 的 try_candidates（它自洽，视频内联复制模式即可）
- 不要实现 VideoCapabilityResolver（scope creep，属 settings 域）
- 不要修改 VideoGenRequest 的公开字段（保持稳定）
- 不要实现视频帧提取（VideoFrameExtractorService 是另一个功能域）
- 不要给 video_service 增加对 Provider trait 的泛型依赖（保持当前具体类型 OpenAiVideoProvider）

## 退出标准

- [ ] cargo check 0 errors/warnings（Rust3 workspace）
- [ ] cargo test 全绿（新增 >= 15 测试）
- [ ] build_video_create_urls 对 2 种 mode x 3 种 endpoint_type = 6 组合输出正确
- [ ] build_video_poll_urls 对主要场景输出正确
- [ ] build_video_download_urls 含 generation_id 有/无两种情况
- [ ] generate() 使用候选 URL 体系（非硬编码单 URL）
- [ ] poll_status() 使用候选 URL 体系
- [ ] VideoGenResult.generation_id 字段存在且不破坏现有 JSON
- [ ] download_video_file 支持 fallback URLs
- [ ] 所有新方法签名与 Spec 语义一致

## 预估代码增量

~600 行（含测试）
