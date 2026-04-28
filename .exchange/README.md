# .exchange — 架构师 ↔ 程序员 协作信箱

## 协议

两个 agent 通过此目录交换工作指令和交付物。**用户只需说"继续"即可触发下一步。**

## 目录结构

`
.exchange/
├── current-batch.txt          # 当前批次编号，如 "batch-0"
├── batch-0/
│   ├── work-order.md          # 架构师 → 程序员 的施工单
│   ├── delivery.md            # 程序员 → 架构师 的交付报告
│   └── review.md              # 架构师 → 程序员 的审查结果
├── batch-1/
│   └── ...
└── batch-N/
    └── ...
`

## 工作流

`
用户对架构师说"开始" / "继续"
    → 架构师读取 current-batch.txt 确定当前批次
    → 架构师检查该批次目录：
        若无 work-order.md → 编写施工单
        若有 delivery.md 但无 review.md → 审查交付物
        若有 review.md 且全部 ✅ → 推进 current-batch.txt 到下一批次，编写新施工单
        若有 review.md 且有 ❌ → 等待程序员修复后重新提交 delivery.md

用户对程序员说"继续"
    → 程序员读取 current-batch.txt 确定当前批次
    → 程序员检查该批次目录：
        若有 work-order.md 但无 delivery.md → 按施工单实现，完成后写 delivery.md
        若有 review.md 且有 ❌ → 按审查意见修复，更新 delivery.md
        若有 review.md 且全部 ✅ → 告诉用户"本批次已通过，请让架构师推进下一批次"
`

## 文件格式约定

### work-order.md（架构师写）
`markdown
# 批次 N 施工单

## 目标
{一句话描述}

## 前置条件
{依赖的已通过批次}

## 任务清单
- [ ] T-001: {任务描述}
  - 读取: {C# 源文件路径}
  - 产出: {Rust3 目标文件路径}
  - 测试: {验收标准}
- [ ] T-002: ...

## 禁止事项
- {具体禁令}

## 退出标准
- {必须通过的命令}
`

### delivery.md（程序员写）
`markdown
# 批次 N 交付报告

## 完成状态
- [x] T-001: {证据: 文件路径:L行号}
- [x] T-002: ...

## 编译状态
{cargo check / tsc --noEmit 输出}

## 自检清单
- [x] 编译通过
- [x] 无死代码
- [x] 测试通过
- ...

## 已知局限
- {诚实描述}
`

### review.md（架构师写）
`markdown
# 批次 N 审查报告

## 审查结果
- ✅ T-001: {证据}
- ❌ T-002: {问题 + 修复方向}

## 编译验证
{独立运行的编译输出}

## 判定
✅ 通过 — 可推进下一批次
❌ 打回 — 需修复后重新提交 delivery.md
`
"@ | Set-Content -Path ".exchange\README.md" -Encoding UTF8
Write-Output "README created"
@"
# .exchange — 架构师与程序员协作信箱

两个 agent 通过此目录交换工作指令和交付物。用户只需说"继续"。

## 目录结构
- current-batch.txt: 当前批次编号
- batch-N/work-order.md: 架构师写的施工单
- batch-N/delivery.md: 程序员写的交付报告
- batch-N/review.md: 架构师写的审查结果

## 架构师流程
1. 读 current-batch.txt
2. 无 work-order.md -> 写施工单
3. 有 delivery.md 无 review.md -> 审查
4. review.md 全通过 -> 推进到下一批次

## 程序员流程
1. 读 current-batch.txt
2. 有 work-order.md 无 delivery.md -> 实现并写交付报告
3. 有 review.md 有打回项 -> 修复后更新 delivery.md
4. review.md 全通过 -> 告诉用户本批次已完成
