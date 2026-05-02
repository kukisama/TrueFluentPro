# .ops/ — 三角协作信箱（ID 路由）

每个子目录名 = 一个会话 ID。有 `closure.md` = 已完成。

## 核心概念

- **ID**：4-12 位随机字符串，是任务的唯一标识
- **路由**：prompt 以 ID 开头 → 工作流模式；不带 ID → 普通模式
- **隔离**：不同 ID 的事件完全独立，可并行

## 快速启动

```powershell
# 生成 ID
$id = -join ((48..57)+(97..122) | Get-Random -Count 6 | ForEach-Object {[char]$_})

# 创建事件（督工自动建目录+开工）
copilot -p "$id 你的需求描述" --agent "铁面督工" --yolo

# 自动循环
.\.ops\loop.ps1 -Id $id

# 或手动逐步
copilot -p "$id 继续" --agent "执行工匠" --yolo
copilot -p "$id 继续" --agent "铁面督工" --yolo
```

## 角色

| Agent | 触发 | 行为 |
|-------|------|------|
| 铁面督工 | `{id} 需求` 或 `{id} 继续` | 建事件 / 写 order / 审查 / closure |
| 执行工匠 | `{id} 继续` | 读 order → delivery |
| 毒舌参谋 | `{id} 继续`（门禁轮次） | 读 delivery → challenge |

## 详细文档

→ `docs/ops-playbook.md`
