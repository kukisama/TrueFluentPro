# 作战剧本 (Ops Playbook)

> 版本：1.0 | 日期：2026-05-02
> 适用范围：TrueFluentPro 全项目 AI Agent 协作工作流

---

## 一、设计哲学

本系统解决三个问题：

1. **信箱目录混乱** — 旧系统用 `batch-0/` `batch-1/` 编号，切换项目要改目录，批次号无语义
2. **角色协作低效** — 人类在两个 agent 之间手动来回切换、复制粘贴上下文
3. **轮次失控** — 返工产生额外轮次，总工期不断膨胀

核心原则：

| 原则 | 含义 |
|------|------|
| **事件驱动信箱** | 每个工作单元以事件名命名独立目录，而非编号 |
| **三角协作** | 督工（决策）+ 工匠（执行）+ 参谋（挑战），互补互制 |
| **一词触发** | 人类只需说"继续"，agent 自动根据文件状态决定行为 |
| **轮次守恒** | 返工不增加轮次，合并到下一轮处理 |
| **目标先行** | 开工前必须定义终极目标，借鉴 DDD 限界上下文思想 |

---

## 二、信箱结构

```
.ops/
├── manifest.md              # 全局事件注册表（真相源）
├── active.txt               # 当前活跃事件名（一行文本）
└── events/
    ├── {event-name}/        # 事件目录（如 "add-image-pipeline"）
    │   ├── goal.md          # 终极目标定义（DDD 风格）
    │   ├── plan.md          # 作战计划（仅复杂任务）
    │   ├── round-01/
    │   │   ├── order.md     # 督工 → 工匠 的施工单
    │   │   ├── delivery.md  # 工匠 → 督工 的交付报告
    │   │   ├── challenge.md # 参谋的攻击性审查（可选）
    │   │   └── verdict.md   # 督工的最终裁决
    │   ├── round-02/
    │   │   └── ...
    │   └── closure.md       # 结案报告
    └── {another-event}/
        └── ...
```

### 2.1 为什么用事件名而非编号

| 旧系统 | 新系统 |
|--------|--------|
| `batch-47/work-order.md` | `events/add-video-pipeline/round-03/order.md` |
| 需要打开文件才知道在做什么 | 目录名本身就是摘要 |
| 所有工作混在一个编号序列 | 每个事件独立、可并行 |
| 切换项目要改信箱根目录 | `.ops/events/` 永远是同一个入口 |

### 2.2 事件命名规范

```
{动词}-{名词}-{可选修饰}
```

示例：
- `add-image-pipeline` — 新增图片处理管线
- `fix-auth-header-bug` — 修复认证头 bug
- `refactor-provider-registry` — 重构 Provider 注册表
- `enrich-tts-ssml` — 充实 TTS SSML 支持

禁止：
- ❌ `task-1`、`work-20260502` — 无语义
- ❌ `做完那个功能` — 用英文 kebab-case

### 2.3 active.txt

一行文本，指向当前活跃事件：

```
add-image-pipeline
```

切换事件 = 修改这一行。所有 agent 的自动启动协议从这个文件开始。

---

## 三、角色定义

### 3.1 铁面督工 (Taskmaster)

**Agent 文件**：`.github/agents/taskmaster.agent.md`

| 属性 | 描述 |
|------|------|
| **性格** | 冷静、精确、效率至上。不废话，不重复，每句话都有行动价值 |
| **先天偏见** | 认为所有交付物都有问题直到被证明没有。不信任口头声明，只信任证据 |
| **权限** | read + write + search + execute |
| **决策权** | ✅ 完整 — 审查判定、轮次调整、参谋意见取舍都由他定 |

**职责矩阵**：

| 场景 | 行为 |
|------|------|
| 简单任务（≤2 文件，<100 行） | 独自完成：分析 → 实现 → 自检 → 写 closure.md |
| 复杂任务 | 定义目标 → 拆轮次 → 写施工单 → 审查交付 → 做裁决 |
| 收到工匠交付 | 逐项验证（编译、测试、代码审查），产出 verdict.md |
| 门禁节点 | 可选择性召唤参谋，但最终决策权在自己手里 |
| 轮次失控 | 合并返工到下一轮，绝不增加总轮次 |

**与架构师 v3 的关系**：督工是 v3 架构师的进化版，增加了独立完成简单任务的能力和轮次控制机制。

### 3.2 执行工匠 (Executor)

**Agent 文件**：`.github/agents/executor.agent.md`

| 属性 | 描述 |
|------|------|
| **性格** | 严谨、诚实、有匠心。宁可多花时间做对，不赶工交差 |
| **先天偏见** | 假设施工单是对的，但保留质疑权。发现不合理时必须提出 |
| **权限** | read + write + search + execute |
| **决策权** | ❌ 有限 — 只能在施工单范围内做技术决策，不能改需求 |

**职责矩阵**：

| 场景 | 行为 |
|------|------|
| 收到施工单 | 预审 → 实现 → 自检 → 交付（附证据） |
| 施工单有误 | 在 delivery.md 的 `## ⚠️ 异议` 中说明，按修正方案实现 |
| 被打回 | 修复上轮问题 + 完成本轮新任务（合并处理） |
| 编译/测试失败 | 修到通过为止，不允许 "known issue" 式逃避 |

**与程序员 v3 的关系**：工匠是 v3 程序员的进化版，增加了对施工单的预审义务和合并轮次的能力。

### 3.3 毒舌参谋 (Challenger)

**Agent 文件**：`.github/agents/challenger.agent.md`

| 属性 | 描述 |
|------|------|
| **性格** | 尖酸、刻薄、永远假设最坏情况。用攻击暴露弱点 |
| **先天偏见** | 认为所有代码都有安全漏洞、所有设计都有盲点、所有测试都不够 |
| **权限** | read + search（只读！不碰代码） |
| **决策权** | ❌ 零 — 只提意见，不做决定。督工可以忽略他 |

**职责矩阵**：

| 场景 | 行为 |
|------|------|
| 被督工召唤 | 对交付物进行攻击性审查，写 challenge.md |
| 发现问题 | 用最尖锐的语言描述风险，附带攻击路径或反例 |
| 没发现问题 | 必须明确说"我找不到问题"，不能假装发现了什么 |
| 被督工忽略 | 接受。参谋不对结果负责，督工负 |

**与审查员的关系**：参谋不是传统审查员。审查员追求全面覆盖，参谋追求找到最致命的那一个弱点。

---

## 四、工作流

### 4.1 完整流程图

```
人类说"继续"
    │
    ▼
督工读 .ops/active.txt → 找到活跃事件
    │
    ├─ 无 goal.md → 提示人类："请定义目标"
    │
    ├─ 有 goal.md，无 plan.md → 评估复杂度
    │   │
    │   ├─ 简单 → 路径 A：督工独自完成
    │   │   └─ 实现 → 自检 → 写 closure.md → ✅ 完成
    │   │
    │   └─ 复杂 → 路径 B：写 plan.md（预设 N 轮）
    │
    ├─ 有 plan.md → 检查当前轮次
    │   │
    │   ├─ 当前轮无 order.md → 督工写 order.md
    │   │
    │   ├─ 有 order.md，无 delivery.md → ⏳ 等工匠（不是督工的回合）
    │   │
    │   ├─ 有 delivery.md，无 verdict.md → 督工审查
    │   │   │
    │   │   ├─ [可选] 召唤参谋 → 读 challenge.md
    │   │   │
    │   │   └─ 写 verdict.md
    │   │       ├─ ✅ 通过 → 推进到下一轮（或写 closure.md）
    │   │       └─ ❌ 打回 → 下一轮 order 合并修复+新任务
    │   │
    │   └─ 所有轮次完成 → 写 closure.md → ✅ 事件关闭
    │
    └─ 有 closure.md → 事件已完成，清除 active.txt
```

### 4.2 路径 A：简单任务（督工独自完成）

**判断标准**：
- 改动 ≤ 2 个文件
- 变更 < 100 行
- 不涉及架构决策
- 无跨模块影响

**流程**：
```
goal.md → [督工直接实现] → closure.md
```

无 plan.md、无 round 目录、无 order/delivery 文件。督工自己做了自己检查。

### 4.3 路径 B：复杂任务（三角协作）

**流程**：
```
goal.md → plan.md（预设 N 轮）
  → round-01/order.md   → round-01/delivery.md → round-01/verdict.md ✅
  → round-02/order.md   → round-02/delivery.md → round-02/challenge.md → round-02/verdict.md ✅
  → ...
  → closure.md
```

**角色轮转**：

```
人类："继续" → 督工（写 order）
人类："继续" → 工匠（读 order → 写 delivery）
人类："继续" → 督工（读 delivery → 写 verdict）
          [可选：人类："继续" → 参谋（写 challenge）→ 督工读后再做 verdict]
重复直到所有轮次完成
```

### 4.4 轮次守恒原则（核心机制）

> **返工不增加轮次。返工内容合并到下一轮的 order 中。**

示例：

| 原始计划 | 实际执行 |
|---------|---------|
| round-01: Provider 注册 | round-01: Provider 注册 → ✅ 通过 |
| round-02: HTTP 客户端 | round-02: HTTP 客户端 → ❌ 打回（序列化错误） |
| round-03: 错误处理 | round-03: **修复序列化** + 错误处理 → ✅ |
| round-04: 集成测试 | round-04: 集成测试 → ✅ |

注意 round-03 的 order 包含两部分：
1. `## 返工修复`：上轮被打回的具体修复项
2. `## 本轮新任务`：原计划中的 round-03 内容

这样总轮次始终 ≤ 4，不会因为打回变成 5 轮、6 轮。

**例外**：如果某轮同时被打回且本轮新任务量很大（合并后 > 2000 行），督工可以决定只做修复不加新任务——但必须在 plan.md 中更新，并注明"轮次溢出：原因 xxx"。

### 4.5 门禁机制（参谋介入时机）

参谋不是每轮都介入。督工根据以下条件决定是否召唤：

| 条件 | 是否召唤参谋 |
|------|------------|
| 涉及安全相关代码（认证、密钥、路径操作） | ✅ 必须 |
| 阶段性里程碑（plan.md 中标记的 checkpoint） | ✅ 必须 |
| 工匠交付质量一直很好 | ❌ 跳过 |
| 纯 UI 样式调整 | ❌ 跳过 |
| 督工自己对某处有疑虑但不确定 | ✅ 建议（用参谋帮自己脑暴） |

**参谋的 challenge.md 不阻塞流程**。督工读完后可以：
- 采纳全部 → 打回工匠修复
- 采纳部分 → 只打回采纳的项
- 全部忽略 → 在 verdict.md 中注明"参谋意见已阅，不采纳，原因 xxx"

---

## 五、目标定义（goal.md 规范）

每个事件必须以 goal.md 开始。借鉴 DDD 限界上下文和 Spec 文档思路。

### 5.1 goal.md 模板

```markdown
# 事件目标：{event-name}

> 创建日期：{YYYY-MM-DD}
> 创建者：{人类 / 督工}
> 预估复杂度：简单 / 复杂（N 轮）

## 限界上下文
{这个功能属于哪个领域？边界在哪里？}

## 终极目标
{一句话描述完成后的状态}

## 验收标准
- [ ] {可验证的条件 1}
- [ ] {可验证的条件 2}
- [ ] {可验证的条件 3}

## 非功能性要求
- 性能：{如有}
- 安全：{如有}
- 兼容性：{如有}

## 参考资料
| 资源 | 位置 |
|------|------|
| Spec 文档 | {路径} |
| 现有代码 | {路径} |
| 平台约束 | {如 PLATFORM-NOTES.md 中的已知限制} |

## 不做什么（显式排除）
- {明确排除的范围}
```

### 5.2 为什么目标先行

| 没有 goal.md | 有 goal.md |
|-------------|------------|
| 做着做着范围膨胀 | 验收标准锁死范围 |
| 做完了不知道算不算完 | 逐项勾选验收标准 |
| 返工时不知道标准是什么 | goal.md 是不变的锚点 |

---

## 六、文件格式规范

### 6.1 plan.md

```markdown
# 作战计划：{event-name}

> 预设轮次：{N}
> 创建日期：{YYYY-MM-DD}

## 轮次规划

| 轮次 | 目标 | 预估行数 | 门禁 | 状态 |
|------|------|---------|------|------|
| round-01 | {目标} | ~{N} | — | ⏳ |
| round-02 | {目标} | ~{N} | — | — |
| round-03 | {目标} | ~{N} | 🐍 参谋审查 | — |
| round-04 | {目标 + 集成测试} | ~{N} | 🐍 参谋审查 | — |

## 依赖关系
{轮次之间的依赖}

## 风险预判
{已知可能导致返工的点}
```

### 6.2 order.md（施工单）

```markdown
# Round {N} 施工单

> 日期：{YYYY-MM-DD}
> 事件：{event-name}
> 本轮目标：{一句话}

## 返工修复（如有）
| 上轮打回项 | 修复要求 |
|-----------|---------|
| {verdict 中的 ❌ 项} | {具体修复指令} |

## 本轮新任务
- [ ] T-001: {描述}
  - 产出: {文件路径}
  - 契约: `fn xxx(a: A) -> Result<B>`
  - 关键逻辑: {步骤}
  - 测试: {要求}

## 退出标准
- [ ] cargo check 0 errors
- [ ] cargo test 全绿
- [ ] {自测要求}

## 禁止事项
- {列表}
```

### 6.3 delivery.md（交付报告）

```markdown
# Round {N} 交付报告

> 日期：{YYYY-MM-DD}

## ⚠️ 施工单异议（如有）
| 施工单要求 | 问题 | 我的修正 |
|-----------|------|---------|

## 任务完成
- [x] T-001: {描述} — 证据: {文件:行号}

## 编译验证
{cargo check 实际输出}

## 测试验证
{cargo test 实际输出}

## 变更文件
| 文件 | 操作 | 行数 |
|------|------|------|
```

### 6.4 challenge.md（攻击性审查）

```markdown
# Round {N} 毒舌审查

> 审查日期：{YYYY-MM-DD}
> 审查立场：假设一切都会出错

## 致命发现（如有）
🔴 {必须修复的问题 + 攻击路径}

## 严重质疑
🟡 {可能出问题的地方 + 最坏情况}

## 挑刺
🟢 {不影响功能但让人不爽的地方}

## 无法攻破的部分
✅ {承认做得好的地方 — 参谋也要诚实}
```

### 6.5 verdict.md（最终裁决）

```markdown
# Round {N} 裁决

> 日期：{YYYY-MM-DD}

## 判定：✅ 通过 / ❌ 打回

## 逐项审查
- ✅ T-001: {证据}
- ❌ T-002: {问题 + 修复方向}

## 参谋意见处理（如有）
| 参谋意见 | 采纳？ | 理由 |
|---------|--------|------|

## 编译/测试验证
{实际运行结果}

## 下一步
{通过：推进到 round-N+1 / 写 closure.md}
{打回：修复项将合并到 round-N+1 的 order 中}
```

### 6.6 closure.md（结案报告）

```markdown
# 事件结案：{event-name}

> 完成日期：{YYYY-MM-DD}
> 实际轮次：{N}（预设 {M}）

## 验收标准检查
- [x] {goal.md 中的条件 1}
- [x] {goal.md 中的条件 2}

## 代码量变更
| 区域 | 变更前 | 变更后 | 增量 |
|------|--------|--------|------|

## 经验教训
{什么做得好、什么可以改进}
```

---

## 七、自动触发协议

### 7.1 核心机制

每个 agent 收到"继续"后，执行以下自动发现逻辑：

```
读 .ops/active.txt → 事件名
  → 读 .ops/events/{事件名}/ 的文件结构
    → 根据文件存在性判断当前阶段
      → 执行对应行为
```

### 7.2 督工的自动启动

```python
if not exists("active.txt"):
    → "没有活跃事件。请创建 goal.md 或指定事件名"

event = read("active.txt")

if not exists(f"events/{event}/goal.md"):
    → "事件 {event} 缺少目标定义。请先写 goal.md"

if not exists(f"events/{event}/plan.md"):
    → 评估复杂度
    if 简单:
        → 直接实现 + 写 closure.md
    else:
        → 写 plan.md

current_round = 找最新未完成的 round 目录

if not exists(f"round-{N}/order.md"):
    → 写 order.md

if exists("order.md") and not exists("delivery.md"):
    → "等待工匠交付。不是我的回合。"

if exists("delivery.md") and not exists("verdict.md"):
    → 审查交付 → 写 verdict.md
    if 需要参谋:
        → "请让参谋审查后再来找我"

if exists("verdict.md") and verdict == 通过:
    if 还有下一轮:
        → 推进到下一轮 → 写 order.md
    else:
        → 写 closure.md

if exists("verdict.md") and verdict == 打回:
    → 写下一轮 order.md（合并修复+新任务）
```

### 7.3 工匠的自动启动

```python
event = read("active.txt")
current_round = 找最新的 round 目录

if exists("order.md") and not exists("delivery.md"):
    → 读 order.md → 预审 → 实现 → 自检 → 写 delivery.md

if exists("verdict.md") and verdict == 打回:
    → "上轮被打回。等待督工写包含修复的新 order。"

else:
    → "不是我的回合。"
```

### 7.4 参谋的自动启动

```python
event = read("active.txt")
current_round = 找最新的 round 目录

if exists("delivery.md") and not exists("challenge.md") and not exists("verdict.md"):
    → 攻击性审查 → 写 challenge.md

else:
    → "不是我的回合。"
```

### 7.5 自动化脚本

将以下脚本保存为 `.ops/loop.ps1`，用于无人值守循环：

```powershell
# 三角协作自动循环
# 用法：.\.ops\loop.ps1
# Ctrl+C 随时中断

param(
    [int]$MaxRounds = 100,
    [int]$PauseSec = 2
)

$logFile = ".ops\loop-$(Get-Date -Format 'yyyyMMdd_HHmmss').log"

function Log($msg, $color = "White") {
    $ts = Get-Date -Format 'HH:mm:ss'
    $line = "[$ts] $msg"
    Write-Host $line -ForegroundColor $color
    $line | Out-File -Append $logFile -Encoding utf8
}

for ($i = 1; $i -le $MaxRounds; $i++) {
    Log "═══ 轮次 $i ═══" "Cyan"

    # 督工回合
    Log "→ 铁面督工" "Yellow"
    copilot -p "继续" --agent "铁面督工" --model "claude-opus-4.6" --yolo
    Start-Sleep $PauseSec

    # 检查是否需要工匠（通过检测 active event 中是否有未交付的 order）
    $active = Get-Content ".ops\active.txt" -ErrorAction SilentlyContinue
    if ($active) {
        $eventDir = ".ops\events\$active"
        $rounds = Get-ChildItem "$eventDir\round-*" -Directory -ErrorAction SilentlyContinue |
                  Sort-Object Name
        $latest = $rounds | Select-Object -Last 1

        if ($latest) {
            $hasOrder = Test-Path "$($latest.FullName)\order.md"
            $hasDelivery = Test-Path "$($latest.FullName)\delivery.md"

            if ($hasOrder -and -not $hasDelivery) {
                Log "→ 执行工匠" "Green"
                copilot -p "继续" --agent "执行工匠" --model "claude-opus-4.6" --yolo
                Start-Sleep $PauseSec

                # 检查是否需要参谋
                $hasDeliveryNow = Test-Path "$($latest.FullName)\delivery.md"
                $hasChallenge = Test-Path "$($latest.FullName)\challenge.md"
                # 读 plan.md 判断本轮是否标记了门禁（简化：总是尝试参谋）
                if ($hasDeliveryNow -and -not $hasChallenge) {
                    Log "→ 毒舌参谋（可选）" "Red"
                    copilot -p "继续" --agent "毒舌参谋" --model "claude-opus-4.6" --yolo
                    Start-Sleep $PauseSec
                }
            }
        }

        # 检查事件是否已关闭
        if (Test-Path "$eventDir\closure.md") {
            Log "✅ 事件 '$active' 已关闭" "Green"
            break
        }
    }
}

Log "循环结束（共 $i 轮）" "Cyan"
```

---

## 八、Manifest 管理

`.ops/manifest.md` 是所有事件的注册表：

```markdown
# 作战清单

## 活跃事件

| 事件 | 状态 | 当前轮次 | 预设轮次 | 创建日期 |
|------|------|---------|---------|---------|

## 已完成事件

| 事件 | 类型 | 实际轮次 | 完成日期 |
|------|------|---------|---------|
```

**状态枚举**：

| 状态 | 含义 |
|------|------|
| 📋 规划中 | 有 goal.md，还没有 plan.md |
| 🔄 执行中 | 正在跑轮次 |
| ⏸️ 暂停 | 人类决定暂停 |
| ✅ 完成 | 有 closure.md |

---

## 九、与旧系统的关系

| 旧系统 | 新系统 | 迁移策略 |
|--------|--------|---------|
| `.exchange/batch-N/` | `.ops/events/{name}/round-N/` | 旧事件保留不动，新事件用新系统 |
| `docs/rust3-enrichment/batch-N/` | 同上 | 同上 |
| `batch-progress.md` | `manifest.md` + 各事件 `plan.md` | manifest 是新的全局真相源 |
| `current-batch.txt` | `active.txt` | 语义相同，位置不同 |

旧系统的 agent（严父架构师 v1/v2/v3、认真程序员 v1/v2/v3）继续可用，只是不再用于新事件。

---

## 十、快速启动指南

### 10.1 创建新事件（人类操作）

```powershell
# 1. 创建事件目录
$event = "add-image-pipeline"
New-Item -ItemType Directory ".ops\events\$event" -Force

# 2. 写目标（可以很简单）
@"
# 事件目标：$event
## 终极目标
实现图片生成 Pipeline，支持 DALL-E 3 和 gpt-image-1。
## 验收标准
- [ ] 单张图片生成 API 调用成功
- [ ] 错误处理覆盖超时和 400/429
- [ ] 集成测试通过（#[ignore]）
"@ | Set-Content ".ops\events\$event\goal.md" -Encoding utf8

# 3. 激活事件
$event | Set-Content ".ops\active.txt" -Encoding utf8

# 4. 开始工作
# 对任何 agent 说"继续"
```

### 10.2 日常工作（人类只需说"继续"）

```
人类："继续"  →  agent 自动发现工作  →  执行  →  等待下一个"继续"
```

### 10.3 切换事件

```powershell
"fix-auth-bug" | Set-Content ".ops\active.txt" -Encoding utf8
# 然后继续说"继续"
```

### 10.4 查看全局状态

```powershell
# 看 manifest
Get-Content .ops\manifest.md

# 看当前事件
$e = Get-Content .ops\active.txt
Get-ChildItem ".ops\events\$e" -Recurse
```

---

## 附录 A：角色互动示例

### 场景：工匠交付质量不佳

```
Round-02 结果：
  工匠：交付 delivery.md（序列化有 bug）
  督工：打回 verdict.md
  → Round-03 的 order.md 包含：
    ## 返工修复
    - T-002-fix: 修复 JSON 序列化（verdict round-02 ❌ 项）
    ## 本轮新任务
    - T-003: 实现错误处理链
```

### 场景：参谋发现督工没发现的问题

```
Round-03 结果：
  工匠：交付 delivery.md（看起来没问题）
  督工：准备通过
  → 督工召唤参谋
  参谋：challenge.md — "🔴 路径拼接没做 sanitize，可以目录遍历"
  督工：采纳 → verdict.md 打回，附带参谋的发现
```

### 场景：参谋过度挑刺

```
参谋：challenge.md — "🟡 这个变量名不够优雅"
督工：verdict.md — "参谋意见：变量命名。不采纳，不影响功能和可维护性。"
```
