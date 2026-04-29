# Rust3 充实工程 — 协作协议

## 参与者

| 角色 | Agent 文件 | 职责 |
|------|-----------|------|
| 严父架构师v3 | `.github/agents/strict-father-architect-v3.agent.md` | 读 Spec → 写施工单 → 审查交付 → **维护进度表** |
| 认真程序员v3 | `.github/agents/diligent-programmer-v3.agent.md` | 读施工单 → 实现代码 → 自测 → 交付 |

## 信箱目录

`
docs/rust3-enrichment/
├── README.md              ← 本文件
├── ROADMAP.md             # 38 轮路线图
├── batch-progress.md      # 逐轮进度 + 代码量追踪 + Phase 门卫检查
├── PLATFORM-NOTES.md      # 平台知识库
├── LESSONS.md             # 经验教训
├── current-batch.txt      # 当前批次编号
└── batch-{N}/
    ├── work-order.md      # 架构师 → 程序员
    ├── delivery.md        # 程序员 → 架构师
    └── review.md          # 架构师 → 程序员
`

## 工作流

`
用户对架构师说"继续"
    → 架构师读 batch-progress.md → 了解全局进度
    → 架构师读 current-batch.txt → 确定 batch N
    → 检查 batch-{N}/ 目录状态 → 决定动作
    → 写施工单 / 审查交付 / 推进批次
    → 更新 batch-progress.md

用户对程序员说"继续"
    → 程序员读 batch-progress.md → 了解全局进度
    → 程序员读 current-batch.txt → 确定 batch N
    → 检查 batch-{N}/ 目录状态 → 决定动作
    → 执行施工单 / 修复审查意见 / 等待下一批次
`

## 进度追踪机制

`batch-progress.md` 是全局进度的**唯一真相源**，包含三个部分：

1. **进度表** — 38 行，每行一个 batch，状态实时更新
2. **代码量追踪** — 每次审查通过后追加统计行
3. **Phase 门卫检查** — 5 个阶段转换点的检查清单

**状态图例**：⬜ 未开始 | 📝 施工单已下发 | 🔨 实施中 | 🔍 审查中 | ✅ 已通过 | ❌ 打回修复中

**维护责任**：
- 架构师负责更新（写施工单时、审查通过时、推进批次时）
- 程序员只读不写（启动时读取以了解上下文）

## 与 .exchange/ 的区别

| | .exchange/ (v2) | docs/rust3-enrichment/ (v3) |
|--|---|---|
| 目的 | C# 源码审查文档化 | Rust3 代码充实实施 |
| 输入 | C# 源文件 | C# Spec 文档 (.exchange/docs/) |
| 产出 | Spec 文档 | Rust3 代码 + 测试 |
| 验证 | 人工审查 | cargo test + API 自测 |
| 进度追踪 | batch-progress.md（27 批） | batch-progress.md（38 批） |
| 状态 | 27 批全部完成 ✅ | 38 批待开始 |

## Spec 文档引用约定

施工单引用 Spec 时使用格式：
`{目录}/{文件名} § {段落标题}`

例如：
- `Services/AiImageGenService.md § 流程3: Responses API 路由`
- `Models/AiEndpointAndConfig.md § ImageApiRouteMode`
- `ViewModels/MediaSessionViewModel.md § 消息管理（虚拟化分页）`

## 自测约定

- 单元测试：`cargo test -p {crate_name}` — 每批必须
- 集成测试：`cargo test --test {test_file} -- --ignored` — 涉及 API 的批次
- 前端检查：`cd Rust3 && npx tsc --noEmit` — 涉及前端的批次
- 全量检查：`cd Rust3/src-tauri && cargo check` — 每批必须

## 经验教训更新规则

任何一方发现以下情况，必须追加到 LESSONS.md：
- API 行为与文档不符
- Spec 描述与 C# 实际行为不符
- 平台特性踩坑（Tauri 2、Azure OpenAI 等）
- 自测发现的边界情况
