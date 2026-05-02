# 作战剧本 (Ops Playbook)

> 版本：3.1 | 日期：2026-05-02
> 适用范围：TrueFluentPro 全项目 AI Agent 协作工作流

---

## 〇、铁律（凌驾于一切规则之上）

### 铁律一：无交互执行

所有 Agent 运行在 **CLI 非交互模式**下。人类只输入 `{ID} 继续`，不做任何其他交互。

- **禁止**：要求确认、等待输入、弹出选择、需要按键继续
- **必须**：所有命令带 `--yes`/`--force`/`-y`/`--quiet`/`--non-interactive`
- **如果**某操作必须交互才能完成 → 换一种不需要交互的实现方式
- **违反此律** = Agent 永久阻塞 = 任务死亡

### 铁律二：破坏性操作路径验证

执行删除、覆盖、移动操作前，**必须验证目标路径存在、完整、且是预期位置。**

> 血的教训：变量为空 → 路径退化为父目录 → `rm -rf` 清空整个项目。

执行标准：

| 规则 | 示例 |
|------|------|
| 拼接路径前每个变量段非空 | `if (-not $sub) { throw "empty" }` |
| 删除前 `Test-Path` | `if (Test-Path $x) { Remove-Item $x }` |
| 递归删除目标至少深度 2 层 | ✅ `.ops/xk9m2f/round-01` · ❌ `.ops/` |
| 禁止对项目根执行递归删除 | 永远不 `Remove-Item . -Recurse` |
| 用字面量优于拼接变量 | ✅ `Remove-Item ".ops/xk9m2f/tmp"` |

**这两条铁律适用于所有模式（工作流模式 + 普通模式）。无例外。**

---

## 一、核心机制：ID 路由

### 1.1 问题

- 多窗口并行处理不同任务 → 需要隔离
- Agent 不继承上下文 → 需要定位到正确的工作目录
- 事件语义命名 → 解析复杂、可能冲突

### 1.2 方案：会话 ID

**人类在调度层预生成一个短随机字符串作为会话 ID，后续每次交互都带上它。**

```
# 创建任务（第一句话）
xk9m2f 创建一个小孩子学习英语的应用

# 后续推进（每次都带 ID）
xk9m2f 继续

# 另一个任务（不同 ID，不冲突）
p7wq4n 修复图片生成超时问题
p7wq4n 继续
```

### 1.3 路由规则

Agent 解析 prompt 的第一个 token：

```
if 第一个token匹配 [a-z0-9]{4,12} 且 prompt 有后续内容:
    → 工作流模式：ID = 第一个token，指令 = 剩余内容
    → 工作目录 = .ops/{ID}/

else:
    → 普通模式：作为一个认真负责的 agent 正常回答
```

### 1.4 目录结构

```
.ops/
├── loop.ps1                 # 自动循环脚本
├── README.md                # 本文件
│
├── xk9m2f/                  # 会话 A
│   ├── goal.md              # "创建一个小孩子学习英语的应用"
│   ├── plan.md
│   ├── round-01/
│   │   ├── order.md
│   │   ├── delivery.md
│   │   └── verdict.md
│   └── closure.md           # 有此文件 = 完成
│
└── p7wq4n/                  # 会话 B（并行，互不干扰）
    ├── goal.md              # "修复图片生成超时问题"
    └── closure.md           # 简单任务，督工独自搞定
```

### 1.5 为什么用随机 ID 而不是语义命名

| 对比 | 语义命名 | 随机 ID |
|------|---------|---------|
| 冲突 | 可能重名 | 不可能 |
| 解析 | 需要从 prompt 提取事件名 | 第一个 token 就是 |
| 调度 | 复杂 | 生成 ID → 每次传 → 完事 |
| 可读性 | 目录名可读 | goal.md 里可读 |
| 编程实现 | 需要 NLP 提取 | `$id = -join ((48..57)+(97..122) | Get-Random -Count 6 | %{[char]$_})` |

---

## 二、角色定义

### 2.1 铁面督工 (Taskmaster)

| 属性 | 值 |
|------|---|
| 文件 | `.github/agents/taskmaster.agent.md` |
| 性格 | 冷静、零信任、只信证据 |
| 权限 | read + write + search + execute |
| 决策权 | ✅ 完整 |

**双模式行为**：

| 模式 | 触发条件 | 行为 |
|------|---------|------|
| 工作流模式 | prompt 以 `{ID} xxx` 开头 | 定位 `.ops/{ID}/`，按事件状态行动 |
| 普通模式 | 无 ID 前缀 | 作为有经验的技术负责人正常回答 |

### 2.2 执行工匠 (Executor)

| 属性 | 值 |
|------|---|
| 文件 | `.github/agents/executor.agent.md` |
| 性格 | 严谨、诚实、匠心 |
| 权限 | read + write + search + execute |
| 决策权 | ❌ 有限 |

**双模式行为**：同上。工作流模式下只做"有 order 无 delivery"的事。

### 2.3 毒舌参谋 (Challenger)

| 属性 | 值 |
|------|---|
| 文件 | `.github/agents/challenger.agent.md` |
| 性格 | 尖酸、有罪推定 |
| 权限 | read + search（只读） |
| 决策权 | ❌ 零 |

**双模式行为**：同上。工作流模式下只做"有 delivery 无 challenge 无 verdict"的攻击性审查。

---

## 三、工作流

### 3.1 创建事件（第一句话）

```
人类："xk9m2f 创建一个小孩子学习英语的应用"

督工解析：
  ID = "xk9m2f"
  指令 = "创建一个小孩子学习英语的应用"
  
动作：
  1. mkdir .ops/xk9m2f/
  2. 写 goal.md（指令原文 + 验收标准）
  3. 评估复杂度
     ├─ 简单 → 直接实现 + closure.md
     └─ 复杂 → plan.md + round-01/order.md
```

### 3.2 后续推进

```
人类："xk9m2f 继续"

督工解析：
  ID = "xk9m2f"
  指令 = "继续"
  
动作：
  1. 读 .ops/xk9m2f/ 的文件状态
  2. 根据状态执行对应行为（见下表）
```

### 3.3 状态机

| 当前状态 | 督工行动 | 工匠行动 | 参谋行动 |
|---------|---------|---------|---------|
| 无目录 | 创建 + goal.md | — | — |
| 有 goal，无 plan/round | 评估复杂度 | — | — |
| 有 order，无 delivery | ⏳ 等工匠 | **执行** | — |
| 有 delivery，无 verdict | **审查** | — | **攻击**（如有门禁） |
| 有 challenge，无 verdict | **读 challenge + 写 verdict** | — | — |
| verdict ✅ + 有下一轮 | 写下一轮 order | — | — |
| verdict ✅ + 最后一轮 | 写 closure | — | — |
| verdict ❌ | 写下一轮 order（含修复） | — | — |
| 有 closure | "已完成" | "已完成" | "已完成" |

### 3.4 路径 A：简单任务

```
xk9m2f 修复 Bearer 头重复
  → 督工：建目录 + goal.md + 直接实现 + closure.md
  → 一次完成
```

### 3.5 路径 B：复杂任务

```
xk9m2f 创建小孩学英语应用
  → 督工：goal.md + plan.md + round-01/order.md
  
xk9m2f 继续  → 工匠：delivery.md
xk9m2f 继续  → [参谋：challenge.md]（如有门禁）
xk9m2f 继续  → 督工：verdict.md + round-02/order.md
xk9m2f 继续  → 工匠：delivery.md
...
xk9m2f 继续  → 督工：closure.md ✅
```

### 3.6 轮次守恒

返工不加轮次。被打回的修复合并到下一轮 order。

---

## 四、上下文传递

### 4.1 原则

Agent 不继承对话。每次都是新的。靠文件获取上下文。

### 4.2 读取链路

```
agent 收到 "xk9m2f 继续"
  → 定位 .ops/xk9m2f/
  → 读 goal.md（终极目标）
  → 读 plan.md（全局规划 + 已完成摘要）
  → 读最新 round 的文件
  → 判断缺什么文件 → 执行对应行为
```

### 4.3 plan.md 中的"已完成轮次摘要"

```markdown
## 已完成轮次摘要
- round-01: 搭建了项目骨架和路由结构
- round-02: 完成词汇数据库和卡片组件
```

这是跨轮上下文传递的核心。后续 agent 无需读历史 round 文件。

---

## 五、调度层实现

### 5.1 生成 ID

```powershell
function New-OpsId { -join ((48..57)+(97..122) | Get-Random -Count 6 | ForEach-Object {[char]$_}) }

# 使用
$id = New-OpsId   # 例如 "xk9m2f"
```

### 5.2 启动事件

```powershell
$id = New-OpsId
copilot -p "$id 创建一个小孩子学习英语的应用" --agent "铁面督工" --yolo
# 记住 $id，后续一直用它
```

### 5.3 自动循环

```powershell
.\.ops\loop.ps1 -Id "xk9m2f"
```

### 5.4 多任务并行

```powershell
# 终端 1
$id1 = New-OpsId  # "xk9m2f"
copilot -p "$id1 做英语学习应用" --agent "铁面督工" --yolo
.\.ops\loop.ps1 -Id $id1

# 终端 2（完全隔离）
$id2 = New-OpsId  # "p7wq4n"
copilot -p "$id2 修复超时bug" --agent "铁面督工" --yolo
.\.ops\loop.ps1 -Id $id2
```

---

## 六、文件格式（精简版）

### goal.md
```markdown
# 会话目标

> ID：{id}
> 创建日期：{YYYY-MM-DD}
> 原始指令：{人类第一句话}
> 复杂度：简单 / 复杂（N 轮）

## 目标
{解析后的目标描述}

## 验收标准
- [ ] {条件}

## 不做什么
- {排除}
```

### plan.md / order.md / delivery.md / challenge.md / verdict.md / closure.md

（格式同 v2.1，唯一区别：header 中用 `> ID：{id}` 替代 `> 事件：{event-name}`）

---

## 七、普通模式

当 prompt **不以** `[a-z0-9]{4,12}` 开头时，所有 agent 回退为普通模式：

- 铁面督工 → 资深技术负责人，正常回答问题
- 执行工匠 → 认真的开发者，正常写代码
- 毒舌参谋 → 刻薄但有用的审查员，正常做 review

这确保了：不带 ID 就不会触发工作流，不会误操作。

---

## 八、铁律详解

### 为什么需要铁律一（无交互执行）

本系统的设计前提是：人类输入 `{ID} 继续` 后离开。Agent 之间通过文件自动协作。

如果任何一个 Agent 在执行过程中需要人类输入（"确认删除？[y/n]"、"选择模板 1/2/3"），**整个流水线就死了。** 没有人在看。没有人会回答。

所以：**宁可不做，也不能等人。** 找不到非交互方案就在 delivery.md 中标注为"已知局限"并跳过。

### 为什么需要铁律二（路径验证）

真实案例：

```bash
# 意图：删除 .ops/xk9m2f/round-01/tmp/
# 实际：$round_dir 为空字符串
rm -rf "$round_dir/tmp/"
# 等价于：rm -rf "/tmp/"  或  rm -rf "./tmp/" 
# 如果连 tmp 也为空：rm -rf "./"  → 项目全没了
```

**一个变量为空 = 一个项目消失。** 这不是理论风险，是已经发生过的事故。

所以：删除操作的路径必须是"确定的"，不能是"可能的"。
