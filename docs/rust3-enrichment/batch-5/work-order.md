# Batch-5 施工单 — 配置持久化 + 模型发现计费

> Phase 1 最后一轮
> 对口 Spec: SettingsImportExportService.md, EndpointTemplateService.md, ImageBillingHelper.md, ImageAndBilling.md

## 任务清单

### T-001: 计费档位配置加载 (BillingTiersService)
- **文件**: `crates/tfp-core/src/billing.rs` (新建)
- **内容**:
  - 结构体: BillingTiersConfig, BillingTierModel, BillingTier
  - `load_billing_tiers()`: 从嵌入 JSON (billing-tiers.json) 加载
  - `snap_up()`: 给定 (model_id, w, h, quality) 找到 >= 的最小档位
  - `calculate_cost()`: Token 计费 or 固定计费
- **测试**: ≥4 (加载, snap_up 精确/向上, 成本计算)

### T-002: 图片模型能力目录 (ImageModelCatalog)
- **文件**: `crates/tfp-core/src/image_catalog.rs` (新建)
- **内容**:
  - 结构体: ImageModelCapabilities, ImageBillingModel, ResolutionConstraints, ImageModelsConfig
  - `load_image_models()`: 从嵌入 JSON (image-models.json) 加载
  - `get_model()`: 按 model_id 查找能力
  - `validate_size()`: FreeForm 约束检查 (MaxEdge, MinPixels, MaxPixels, AspectRatio, EdgeMultiple)
- **测试**: ≥4 (加载, 查找, FreeForm 验证通过/失败)

### T-003: 计费记录存储 (billing_repo)
- **文件**: `crates/tfp-storage/src/billing_repo.rs` (新建)
- **内容**:
  - `insert_billing_record()`: 写入 billing_records 表
  - `get_billing_summary()`: 聚合查询 (total tokens, cost, by_model 分组)
  - `get_billing_records()`: 按时间范围查询
  - `get_billing_by_endpoint()`: 按 endpoint_id 聚合
- **测试**: ≥4 (insert+query roundtrip, summary 聚合, 空库, 多模型)

### T-004: 配置导入导出增强
- **文件**: `src-tauri/src/commands/config.rs` (修改)
- **内容**:
  - `sanitize_config()`: 清除 AAD 凭据 (azure_tenant_id, azure_client_id)
  - `export_config` 调用 sanitize 后再序列化
  - `import_config` 增加: 验证包格式, endpoint ID 去重, ModelReference 有效性检查
  - `validate_model_references()`: 确保引用的 endpoint+model 存在
- **测试**: ≥3 (sanitize 清除, import去重, 引用验证)

### T-005: 端点模板服务
- **文件**: `crates/tfp-providers/src/template_service.rs` (新建)
- **内容**:
  - `get_templates()`: VendorProfile → EndpointTemplateDefinition 列表
  - `apply_template()`: 将模板默认值写入 AiEndpoint
  - `build_behavior_summary()`: 生成行为摘要文本
  - `build_inspection_report()`: 生成 Markdown 检查报告
- **测试**: ≥4 (get_templates 覆盖, apply_template, behavior_summary, inspection_report)

### T-006: 模型发现增强
- **文件**: `src-tauri/src/commands/test.rs` (修改)
- **内容**:
  - `discover_models` 增加: 从 VendorProfile.model_discovery_urls 构建候选 URL
  - 分离 Azure OpenAI 的 deployments API (GET /openai/deployments?api-version=...)
  - 解析 Azure deployment 格式 → DiscoveredModel
- **测试**: ≥2 (URL 构建, Azure deployment 解析)

### T-007: 图片计费命令
- **文件**: `src-tauri/src/commands/media.rs` (修改)
- **内容**:
  - `record_image_billing`: Tauri command, 计费记录写入
  - `get_billing_summary`: Tauri command, 获取计费汇总
  - 集成 billing_repo + billing_tiers
- **测试**: 由 billing_repo 测试覆盖

### T-008: 资产文件嵌入
- **文件**: `Rust3/src-tauri/assets/` (新建)
- **内容**:
  - 复制 billing-tiers.json 和 image-models.json
  - 在 billing.rs / image_catalog.rs 中用 include_str!() 嵌入
- **测试**: 加载测试覆盖

## 退出标准
- `cargo check --workspace` 0 errors 0 warnings
- `cargo test --workspace` 全绿, ≥15 新测试
- 配置 import/export round-trip + sanitize 测试通过
- 计费 snap_up + cost 计算测试通过
- 模型目录加载 + FreeForm 约束验证测试通过
