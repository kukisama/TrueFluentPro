# 三角协作自动循环
# 用法：.\.ops\loop.ps1 -Id "xk9m2f"
# Ctrl+C 中断

param(
    [Parameter(Mandatory=$true)]
    [string]$Id,

    [int]$MaxRounds = 100,
    [int]$PauseSec = 3,
    [string]$Model = "claude-opus-4.6"
)

$opsRoot = Split-Path $PSScriptRoot -Parent | Join-Path -ChildPath ".ops"
$eventDir = Join-Path (Split-Path $PSScriptRoot -Parent) ".ops\$Id"

# 如果脚本在 .ops/ 内运行，修正路径
if (-not (Test-Path $eventDir)) {
    $eventDir = Join-Path $PSScriptRoot $Id
}

$logFile = Join-Path $PSScriptRoot "loop-${Id}-$(Get-Date -Format 'yyyyMMdd_HHmm').log"

Write-Host "═══════════════════════════════" -ForegroundColor Cyan
Write-Host "  ID: $Id" -ForegroundColor Cyan
Write-Host "  日志: $logFile" -ForegroundColor DarkGray
Write-Host "  Ctrl+C 中断" -ForegroundColor DarkGray
Write-Host "═══════════════════════════════" -ForegroundColor Cyan

function Log($msg, $color = "White") {
    $ts = Get-Date -Format 'HH:mm:ss'
    $line = "[$ts] $msg"
    Write-Host $line -ForegroundColor $color
    $line | Out-File -Append $logFile -Encoding utf8
}

function Get-LatestRound {
    $rounds = Get-ChildItem "$eventDir\round-*" -Directory -ErrorAction SilentlyContinue |
              Sort-Object Name
    return $rounds | Select-Object -Last 1
}

function Test-NeedsChallenger($roundName) {
    $planFile = "$eventDir\plan.md"
    if (Test-Path $planFile) {
        $content = Get-Content $planFile -Raw
        if ($content -match "$roundName.*🐍") { return $true }
    }
    return $false
}

# 检查事件是否存在
if (-not (Test-Path "$eventDir\goal.md")) {
    Log "⚠ .ops/$Id/ 不存在或无 goal.md" "Yellow"
    Log "请先创建事件：copilot -p `"$Id 你的需求`" --agent `"铁面督工`" --yolo" "Yellow"
    exit 1
}

for ($i = 1; $i -le $MaxRounds; $i++) {
    Log "═══ 第 $i 轮 ═══" "Cyan"

    # 检查完成
    if (Test-Path "$eventDir\closure.md") {
        Log "✅ 任务已完成" "Green"
        break
    }

    # 1. 督工
    Log "→ 铁面督工" "Yellow"
    copilot -p "$Id 继续" --agent "铁面督工" --model $Model --reasoning-effort high --yolo
    Start-Sleep $PauseSec

    if (Test-Path "$eventDir\closure.md") {
        Log "✅ 任务已完成（督工独立完成）" "Green"
        break
    }

    # 2. 工匠
    $round = Get-LatestRound
    if ($round) {
        $hasOrder = Test-Path "$($round.FullName)\order.md"
        $hasDelivery = Test-Path "$($round.FullName)\delivery.md"

        if ($hasOrder -and -not $hasDelivery) {
            Log "→ 执行工匠" "Green"
            copilot -p "$Id 继续" --agent "执行工匠" --model $Model --reasoning-effort high --yolo
            Start-Sleep $PauseSec

            # 3. 参谋（门禁）
            $hasDeliveryNow = Test-Path "$($round.FullName)\delivery.md"
            $hasChallenge = Test-Path "$($round.FullName)\challenge.md"
            $hasVerdict = Test-Path "$($round.FullName)\verdict.md"

            if ($hasDeliveryNow -and -not $hasChallenge -and -not $hasVerdict) {
                if (Test-NeedsChallenger $round.Name) {
                    Log "→ 毒舌参谋（🐍）" "Red"
                    copilot -p "$Id 继续" --agent "毒舌参谋" --model $Model --reasoning-effort high --yolo
                    Start-Sleep $PauseSec
                }
            }
        }
    }
}

Log "循环结束" "Cyan"
