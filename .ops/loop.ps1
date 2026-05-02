# 三角协作自动循环脚本
# 铁面督工 ↔ 执行工匠 ↔ 毒舌参谋
#
# 用法：.\.ops\loop.ps1 [-MaxRounds 50] [-PauseSec 3]
# Ctrl+C 随时中断
#
# 工作原理：
# 1. 每轮先调督工 → 督工根据文件状态决定行为
# 2. 如果有未交付的施工单 → 调工匠
# 3. 如果工匠交付了且未审查 → 可选调参谋
# 4. 回到步骤 1
#
# 每个 agent 内部有自动启动协议，会根据 .ops/ 的文件状态判断是否轮到自己。

param(
    [int]$MaxRounds = 100,
    [int]$PauseSec = 3,
    [string]$Model = "claude-opus-4.6"
)

$logFile = Join-Path $PSScriptRoot "loop-$(Get-Date -Format 'yyyyMMdd_HHmmss').log"
Write-Host "日志: $logFile" -ForegroundColor Magenta
Write-Host "模型: $Model | 最大轮次: $MaxRounds | 间隔: ${PauseSec}s" -ForegroundColor Magenta
Write-Host "Ctrl+C 中断" -ForegroundColor DarkGray
Write-Host ""

function Log($msg, $color = "White") {
    $ts = Get-Date -Format 'HH:mm:ss'
    $line = "[$ts] $msg"
    Write-Host $line -ForegroundColor $color
    $line | Out-File -Append $logFile -Encoding utf8
}

function Get-ActiveEvent {
    $f = Join-Path $PSScriptRoot "active.txt"
    if (Test-Path $f) {
        $content = (Get-Content $f -Raw).Trim()
        if ($content) { return $content }
    }
    return $null
}

function Get-LatestRound($eventDir) {
    $rounds = Get-ChildItem "$eventDir\round-*" -Directory -ErrorAction SilentlyContinue |
              Sort-Object Name
    return $rounds | Select-Object -Last 1
}

for ($i = 1; $i -le $MaxRounds; $i++) {
    Log "═══════════════════════════════════" "Cyan"
    Log "  轮次 $i / $MaxRounds" "Cyan"
    Log "═══════════════════════════════════" "Cyan"

    $event = Get-ActiveEvent
    if (-not $event) {
        Log "⚠ 无活跃事件 (.ops/active.txt 为空)" "Yellow"
        Log "请创建事件后重新运行" "Yellow"
        break
    }

    $eventDir = Join-Path $PSScriptRoot "events\$event"

    # 检查事件是否已关闭
    if (Test-Path "$eventDir\closure.md") {
        Log "✅ 事件 '$event' 已关闭" "Green"
        break
    }

    # 1. 督工回合
    Log "→ 铁面督工" "Yellow"
    copilot -p "继续" --agent "铁面督工" --model $Model --reasoning-effort high --yolo
    Start-Sleep $PauseSec

    # 重新检查关闭状态（督工可能独自完成了简单任务）
    if (Test-Path "$eventDir\closure.md") {
        Log "✅ 事件 '$event' 已关闭（督工独自完成）" "Green"
        break
    }

    # 2. 检查是否需要工匠
    $latest = Get-LatestRound $eventDir
    if ($latest) {
        $hasOrder = Test-Path "$($latest.FullName)\order.md"
        $hasDelivery = Test-Path "$($latest.FullName)\delivery.md"
        $hasVerdict = Test-Path "$($latest.FullName)\verdict.md"

        if ($hasOrder -and -not $hasDelivery) {
            Log "→ 执行工匠" "Green"
            copilot -p "继续" --agent "执行工匠" --model $Model --reasoning-effort high --yolo
            Start-Sleep $PauseSec

            # 3. 检查是否需要参谋（工匠交付后）
            $hasDeliveryNow = Test-Path "$($latest.FullName)\delivery.md"
            $hasChallenge = Test-Path "$($latest.FullName)\challenge.md"

            if ($hasDeliveryNow -and -not $hasChallenge) {
                # 检查 plan.md 中是否标记了门禁
                $planFile = "$eventDir\plan.md"
                $roundName = $latest.Name
                $needChallenger = $false

                if (Test-Path $planFile) {
                    $planContent = Get-Content $planFile -Raw
                    # 如果 plan 中当前轮次行包含 🐍 标记
                    if ($planContent -match "$roundName.*🐍") {
                        $needChallenger = $true
                    }
                }

                if ($needChallenger) {
                    Log "→ 毒舌参谋（门禁触发）" "Red"
                    copilot -p "继续" --agent "毒舌参谋" --model $Model --reasoning-effort high --yolo
                    Start-Sleep $PauseSec
                } else {
                    Log "⊘ 参谋跳过（本轮无门禁标记）" "DarkGray"
                }
            }
        } elseif ($hasDelivery -and -not $hasVerdict) {
            Log "⊘ 等待督工审查（下轮处理）" "DarkGray"
        }
    }
}

Log "" "White"
Log "循环结束（共执行 $i 轮）" "Cyan"
