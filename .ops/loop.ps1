# 三角协作自动循环脚本
# 铁面督工 ↔ 执行工匠 ↔ 毒舌参谋
#
# 用法：.\.ops\loop.ps1 [-MaxRounds 50] [-PauseSec 3]
# 前提：先手动对督工说第一句话创建事件，再运行本脚本
# Ctrl+C 随时中断
#
# 工作原理：
#   agent 内部有自动启动协议，会扫描 .ops/ 找活跃事件（无 closure.md 的目录）
#   本脚本只负责按顺序调用 agent，agent 自己判断是否轮到自己

param(
    [int]$MaxRounds = 100,
    [int]$PauseSec = 3,
    [string]$Model = "claude-opus-4.6"
)

$opsRoot = $PSScriptRoot
$logFile = Join-Path $opsRoot "loop-$(Get-Date -Format 'yyyyMMdd_HHmmss').log"

Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  三角协作自动循环" -ForegroundColor Cyan
Write-Host "  日志: $logFile" -ForegroundColor DarkGray
Write-Host "  模型: $Model | 最大轮次: $MaxRounds" -ForegroundColor DarkGray
Write-Host "  Ctrl+C 中断" -ForegroundColor DarkGray
Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

function Log($msg, $color = "White") {
    $ts = Get-Date -Format 'HH:mm:ss'
    $line = "[$ts] $msg"
    Write-Host $line -ForegroundColor $color
    $line | Out-File -Append $logFile -Encoding utf8
}

function Get-ActiveEvent {
    # 扫描 .ops/ 下的子目录，排除有 closure.md 的
    $dirs = Get-ChildItem $opsRoot -Directory -ErrorAction SilentlyContinue
    $active = @()
    foreach ($d in $dirs) {
        if (-not (Test-Path "$($d.FullName)\closure.md")) {
            # 确认它有 goal.md（是合法事件）
            if (Test-Path "$($d.FullName)\goal.md") {
                $active += $d
            }
        }
    }
    if ($active.Count -eq 0) { return $null }
    if ($active.Count -eq 1) { return $active[0] }
    # 多个活跃事件：取最近修改的
    return $active | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Get-LatestRound($eventDir) {
    $rounds = Get-ChildItem "$($eventDir.FullName)\round-*" -Directory -ErrorAction SilentlyContinue |
              Sort-Object Name
    return $rounds | Select-Object -Last 1
}

function Test-NeedsChallenger($eventDir, $roundName) {
    $planFile = "$($eventDir.FullName)\plan.md"
    if (Test-Path $planFile) {
        $content = Get-Content $planFile -Raw
        if ($content -match "$roundName.*🐍") { return $true }
    }
    return $false
}

for ($i = 1; $i -le $MaxRounds; $i++) {
    Log "═══ 第 $i 轮 ═══" "Cyan"

    $event = Get-ActiveEvent
    if (-not $event) {
        Log "⚠ 无活跃事件（.ops/ 下没有未关闭的事件目录）" "Yellow"
        Log "请先对督工说第一句话创建事件" "Yellow"
        break
    }
    Log "活跃事件: $($event.Name)" "White"

    # 1. 督工回合
    Log "→ 铁面督工" "Yellow"
    copilot -p "继续" --agent "铁面督工" --model $Model --reasoning-effort high --yolo
    Start-Sleep $PauseSec

    # 检查是否已关闭（督工可能独自完成了简单任务）
    if (Test-Path "$($event.FullName)\closure.md") {
        Log "✅ 事件 '$($event.Name)' 已关闭" "Green"
        break
    }

    # 2. 检查是否需要工匠
    $round = Get-LatestRound $event
    if ($round) {
        $hasOrder = Test-Path "$($round.FullName)\order.md"
        $hasDelivery = Test-Path "$($round.FullName)\delivery.md"
        $hasChallenge = Test-Path "$($round.FullName)\challenge.md"
        $hasVerdict = Test-Path "$($round.FullName)\verdict.md"

        if ($hasOrder -and -not $hasDelivery) {
            # 工匠回合
            Log "→ 执行工匠" "Green"
            copilot -p "继续" --agent "执行工匠" --model $Model --reasoning-effort high --yolo
            Start-Sleep $PauseSec

            # 3. 检查是否需要参谋
            $hasDeliveryNow = Test-Path "$($round.FullName)\delivery.md"
            if ($hasDeliveryNow -and -not $hasChallenge -and -not $hasVerdict) {
                if (Test-NeedsChallenger $event $round.Name) {
                    Log "→ 毒舌参谋（🐍 门禁）" "Red"
                    copilot -p "继续" --agent "毒舌参谋" --model $Model --reasoning-effort high --yolo
                    Start-Sleep $PauseSec
                } else {
                    Log "  参谋跳过（本轮无门禁）" "DarkGray"
                }
            }
        }
    }
}

Log "" "White"
Log "循环结束（$i 轮）" "Cyan"
