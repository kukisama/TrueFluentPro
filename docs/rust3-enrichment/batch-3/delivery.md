# 批次 3 交付报告
> 交付日期：2026-04-29

## 变更清单

### T-001: VideoGenResult 补充 generation_id 字段 ✅
- **文件**: `crates/tfp-core/src/models/api.rs:136-137`
- **变更**: 在 `VideoGenResult` struct 中新增 `#[serde(default)] pub generation_id: Option<String>`
- **验证**: 现有 JSON 反序列化不受影响（`test_video_gen_result_without_generation_id`）

### T-002: build_video_create_urls(mode) ✅
- **文件**: `crates/tfp-providers/src/openai_video.rs:48-96`
- **变更**: 新增 `pub(crate) fn build_video_create_urls(&self, mode: &VideoApiMode) -> Vec<String>`
- **覆盖**: Azure x SoraJobs, Azure x Videos, APIM x SoraJobs, APIM x Videos, Compat x SoraJobs (with/without /v1), Compat x Videos (with/without /v1)
- **测试**: 8 个测试覆盖所有组合

### T-003: build_video_poll_urls(video_id, mode) ✅
- **文件**: `crates/tfp-providers/src/openai_video.rs:98-112`
- **变更**: 新增方法，基于 create URLs 追加 `/{video_id}`，正确处理 query string
- **测试**: 4 个测试（Azure SoraJobs, APIM SoraJobs, Azure Videos, Compat Videos）

### T-004: build_video_download_urls(video_id, generation_id, mode) ✅
- **文件**: `crates/tfp-providers/src/openai_video.rs:114-188`
- **变更**: 新增方法，支持 SoraJobs/Videos 模式、generation_id 有无两种情况、3 种 endpoint_type
- **测试**: 5 个测试（Azure 无/有 gen_id, APIM 有 gen_id, Azure Videos, Compat Videos）

### T-005: 重写 generate() — 使用候选 URL 体系 ✅
- **文件**: `crates/tfp-providers/src/openai_video.rs:248-293`
- **变更**: 
  - 使用 `build_video_create_urls()` 替代硬编码 URL
  - 使用 `try_candidates()` 实现自动回退
  - SoraJobs 和 Videos 模式均走 JSON（Videos 的 multipart 留 TODO）
  - 从响应中提取 `generation_id`

### T-006: 重写 poll_status() — 使用候选 URL + generation_id 提取 ✅
- **文件**: `crates/tfp-providers/src/openai_video.rs:295-361`
- **变更**:
  - 先尝试 SoraJobs poll URLs，全 404 后尝试 Videos poll URLs
  - 正确处理 401/403/429 立即返回错误
  - 提取 `generation_id` 和 `download_url`（从 generations/output/video 三个位置）

### T-007: download_video_file 支持 fallback URLs ✅
- **文件**: `crates/tfp-media/src/video_service.rs:158-207`
- **变更**: 签名改为 `download_video_file(primary_url, fallback_urls, data_dir, task_id)`
  - 先尝试 primary_url，失败后逐一尝试 fallback_urls
  - 非 2xx 和网络错误均继续下一候选
  - 全部失败返回最后错误
- **测试**: 编译通过（需网络 mock 才能真测下载）

### T-008: video_service 集成 — 传递 generation_id 构建下载候选 ✅
- **文件**: `crates/tfp-media/src/video_service.rs:45, 83-96, 137-157`
- **变更**:
  - `run_video_generation` 追踪 `generation_id`（从 create 和 poll 响应中获取）
  - 新增 `build_download_fallbacks()` 从 download_url 推导备用候选
  - 下载时传入 fallback URLs
- **测试**: 3 个测试覆盖 `build_download_fallbacks()`

## 新增辅助方法
- `api_version()` — DRY helper 提取 api_version
- `try_candidates()` — 内联候选回退（与 openai_image 模式一致但独立实现）
- `extract_generation_id()` / `extract_download_url()` — 静态 JSON 提取方法

## 编译验证
```
cargo check --workspace → 0 errors, 0 warnings
```

## 测试验证
```
tfp-providers: 105 passed (was 80, +25 new)
tfp-media: 29 passed (was 26, +3 new)
tfp-core: 68 passed (unchanged)
Total new tests: 28
```

## 新增测试清单
| # | 测试名 | Crate |
|---|--------|-------|
| 1 | test_create_urls_azure_sora_jobs | tfp-providers |
| 2 | test_create_urls_azure_videos | tfp-providers |
| 3 | test_create_urls_apim_sora_jobs | tfp-providers |
| 4 | test_create_urls_apim_videos | tfp-providers |
| 5 | test_create_urls_compat_sora_jobs_with_v1 | tfp-providers |
| 6 | test_create_urls_compat_sora_jobs_no_v1 | tfp-providers |
| 7 | test_create_urls_compat_videos_with_v1 | tfp-providers |
| 8 | test_create_urls_compat_videos_no_v1 | tfp-providers |
| 9 | test_poll_urls_azure_sora_jobs | tfp-providers |
| 10 | test_poll_urls_apim_sora_jobs | tfp-providers |
| 11 | test_poll_urls_azure_videos | tfp-providers |
| 12 | test_poll_urls_compat_videos | tfp-providers |
| 13 | test_download_urls_azure_sora_jobs_no_gen_id | tfp-providers |
| 14 | test_download_urls_azure_sora_jobs_with_gen_id | tfp-providers |
| 15 | test_download_urls_apim_sora_jobs_with_gen_id | tfp-providers |
| 16 | test_download_urls_videos_mode | tfp-providers |
| 17 | test_download_urls_compat_videos | tfp-providers |
| 18 | test_extract_generation_id_present | tfp-providers |
| 19 | test_extract_generation_id_absent | tfp-providers |
| 20 | test_extract_download_url_generations | tfp-providers |
| 21 | test_extract_download_url_output | tfp-providers |
| 22 | test_extract_download_url_video | tfp-providers |
| 23 | test_extract_download_url_none | tfp-providers |
| 24 | test_video_gen_result_generation_id_serde | tfp-providers |
| 25 | test_video_gen_result_without_generation_id | tfp-providers |
| 26 | test_build_download_fallbacks_no_gen_id | tfp-media |
| 27 | test_build_download_fallbacks_with_gen_id_and_job_url | tfp-media |
| 28 | test_build_download_fallbacks_already_has_content | tfp-media |

## 变更文件
| 文件 | 操作 | 增量 |
|------|------|------|
| crates/tfp-core/src/models/api.rs | 修改 | +2 行 |
| crates/tfp-providers/src/openai_video.rs | 重写 | ~500 行（净增 ~200 行） |
| crates/tfp-media/src/video_service.rs | 重写 | ~220 行（净增 ~30 行） |