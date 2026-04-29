# batch-5 交付报告
> 日期：2026-04-29 | Phase 1 最后一轮

## 主题：配置持久化 + 模型发现计费

### 施工单 8 项任务完成情况

| 任务 | 描述 | 文件 | 状态 |
|------|------|------|------|
| T-001 | 计费层级配置 (billing.rs) | `crates/tfp-core/src/billing.rs` (新建) | ✅ |
| T-002 | 图片模型目录 (image_catalog.rs) | `crates/tfp-core/src/image_catalog.rs` (新建) | ✅ |
| T-003 | 计费仓储 (billing_repo.rs) | `crates/tfp-storage/src/billing_repo.rs` (新建) | ✅ |
| T-004 | 配置导入导出增强 | `src-tauri/src/commands/config.rs` (修改) | ✅ |
| T-005 | 端点模板服务 | `crates/tfp-providers/src/template_service.rs` (新建) | ✅ |
| T-006 | 模型自动发现增强 | `src-tauri/src/commands/test.rs` (修改) | ✅ |
| T-007 | 计费 Tauri 命令 | `src-tauri/src/commands/media.rs` + `src-tauri/src/lib.rs` (修改) | ✅ |
| T-008 | 资产文件嵌入 | `src-tauri/assets/billing-tiers.json`, `image-models.json` (新建) | ✅ |

### 新增文件 (6)

| 文件 | 行数 | 说明 |
|------|------|------|
| `crates/tfp-core/src/billing.rs` | 197 | 计费层级: load_billing_tiers, snap_up, calculate_cost, 8 tests |
| `crates/tfp-core/src/image_catalog.rs` | 243 | 图片模型目录: load_image_models, get_model, validate_freeform_size, 8 tests |
| `crates/tfp-storage/src/billing_repo.rs` | 219 | 计费 CRUD: insert, summary, by_range, by_endpoint, 5 tests |
| `crates/tfp-providers/src/template_service.rs` | 225 | 模板服务: get_templates, apply_template, inspection_report, 5 tests |
| `src-tauri/assets/billing-tiers.json` | - | gpt-image-1.5/2 各分辨率+质量的计费规则 |
| `src-tauri/assets/image-models.json` | - | 图片模型能力矩阵 (尺寸/质量/格式约束) |

### 修改文件 (7)

| 文件 | 变更 |
|------|------|
| `crates/tfp-core/src/lib.rs` | +2 行 (billing, image_catalog 模块声明) |
| `crates/tfp-providers/src/lib.rs` | +1 行 (template_service 模块声明) |
| `crates/tfp-storage/src/lib.rs` | +1 行 (billing_repo 模块声明) |
| `src-tauri/src/commands/config.rs` | +104 行 (sanitize_config, dedup_endpoint_ids, validate_model_references, 增强 export/import, 3 tests) |
| `src-tauri/src/commands/test.rs` | +79 行 (profile-defined URLs, parse_azure_deployments, 2 tests) |
| `src-tauri/src/commands/media.rs` | +42 行 (3 个 billing Tauri 命令) |
| `src-tauri/src/lib.rs` | +3 行 (注册 billing 命令) |

### 关键设计决策

1. **JSON 嵌入**: billing-tiers.json / image-models.json 使用 `include_str!()` 编译时嵌入，零 I/O 开销
2. **snap_up 算法**: 找像素面积 ≥ 请求面积的最小 tier；无匹配则退回最大 tier
3. **计费双模式**: token 模式 (gpt-image-2: tokens × price/M) vs fixed_per_image 模式 (gpt-image-1.5: 固定价格)
4. **配置净化**: 导出时清除 AAD 凭据；导入时去重 ID、迁移旧 auth、验证模型引用
5. **模板服务**: VendorProfile → EndpointTemplateDefinition 映射 + 行为摘要 + 巡检报告

### 测试统计

- **新增测试**: 31 (billing 8 + image_catalog 8 + billing_repo 5 + template_service 5 + config 3 + test 2)
- **全域测试**: 377 (batch-4 为 346)
- **全部通过**: ✅

### 代码量

| 区域 | batch-4 | batch-5 | 增量 |
|------|---------|---------|------|
| Rust crates | 16,383 | 17,271 | +888 |
| src-tauri | 6,882 | 7,110 | +228 |
| 前端 TS/TSX | 9,195 | 9,195 | ±0 |
| **总计** | **32,460** | **33,576** | **+1,116** |
