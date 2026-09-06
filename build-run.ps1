<#
.SYNOPSIS
  构建、打包并运行 TrueFluentPro 桌面程序。
.DESCRIPTION
    脚本只处理根目录的 C# 项目，并提供两个互斥开关：
    - 默认：生成 Release 发布包后启动发布目录中的程序。
    - Dev：执行 Debug 构建后启动 Debug 程序。
    - Run：使用 dotnet run 前台运行，保留控制台日志。
.EXAMPLE
  .\build-run.ps1
  默认执行 C# Release FDD 打包，生成 zip，并启动发布版程序。
.EXAMPLE
    .\build-run.ps1 -Dev
  构建并启动 C# Debug 版本。
.EXAMPLE
    .\build-run.ps1 -Run
  使用 dotnet run 前台执行 C# 程序。
#>
#Requires -Version 5.1
param(
        [switch]$Dev,
        [switch]$Run
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot = $PSScriptRoot
$csharpProject = Join-Path $repoRoot 'TrueFluentPro.csproj'
$csharpPublishScript = Join-Path $repoRoot 'Docs\publish-fdd.ps1'

function Write-Step([string]$Message) {
    Write-Host "`n========== $Message ==========" -ForegroundColor Cyan
}

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "未找到命令 '$Name'，请先安装并加入 PATH。"
    }
}

function Invoke-NativeCommand(
    [string]$FilePath,
    [string[]]$Arguments,
    [string]$FailureMessage
) {
    Write-Host "Run: $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage，退出码：$LASTEXITCODE"
    }
}

function Stop-RepositoryProcesses([string[]]$Names) {
    $repoPrefix = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $processes = Get-Process -Name $Names -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        $processPath = $null
        try { $processPath = $process.Path } catch { }
        if ([string]::IsNullOrWhiteSpace($processPath)) {
            Write-Warning "无法确认进程路径，未停止：$($process.Name) ($($process.Id))"
            continue
        }

        $fullProcessPath = [System.IO.Path]::GetFullPath($processPath)
        if (-not $fullProcessPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "保留仓库外进程：$fullProcessPath" -ForegroundColor DarkYellow
            continue
        }

        Write-Host "停止当前仓库程序：$($process.Name) ($($process.Id))" -ForegroundColor Yellow
        if ($process.CloseMainWindow()) {
            $process.WaitForExit(2000) | Out-Null
        }
        if (-not $process.HasExited) {
            $process | Stop-Process -Force
            $process.WaitForExit(5000) | Out-Null
        }
    }
}

function Start-App([string]$ExecutablePath) {
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "未找到可执行文件：$ExecutablePath"
    }

    $workingDirectory = Split-Path -Parent $ExecutablePath
    $process = Start-Process -FilePath $ExecutablePath -WorkingDirectory $workingDirectory -PassThru
    Write-Host "已启动：$ExecutablePath" -ForegroundColor Green
    Write-Host "进程 ID：$($process.Id)"
}

function Invoke-CSharp([string]$SelectedMode) {
    Assert-Command 'dotnet'
    if (-not (Test-Path -LiteralPath $csharpProject -PathType Leaf)) {
        throw "找不到 C# 项目：$csharpProject"
    }

    Stop-RepositoryProcesses @('TrueFluentPro', 'Updater')

    switch ($SelectedMode) {
        'release' {
            if (-not (Test-Path -LiteralPath $csharpPublishScript -PathType Leaf)) {
                throw "找不到发布脚本：$csharpPublishScript"
            }

            Write-Step 'C# Release 打包（win-x64 FDD）'
            & $csharpPublishScript -Rid 'win-x64' -Configuration 'Release' -NoOpen
            if ($LASTEXITCODE -ne 0) {
                throw "C# Release 打包失败，退出码：$LASTEXITCODE"
            }

            $releaseExe = Join-Path $repoRoot 'artifacts\publish\TrueFluentPro-win-x64-fdd\TrueFluentPro.exe'
            Start-App $releaseExe
        }
        'dev' {
            Write-Step 'C# Debug 构建'
            Invoke-NativeCommand 'dotnet' @('build', $csharpProject, '-c', 'Debug') 'C# Debug 构建失败'

            $debugExe = Join-Path $repoRoot 'bin\Debug\net10.0\TrueFluentPro.exe'
            Start-App $debugExe
        }
        'run' {
            Write-Step 'C# 前台运行（Debug）'
            Push-Location $repoRoot
            try {
                Invoke-NativeCommand 'dotnet' @('run', '--project', $csharpProject, '-c', 'Debug') 'C# 运行失败'
            }
            finally {
                Pop-Location
            }
        }
    }
}

if ($Dev -and $Run) {
    throw "参数 -Dev 与 -Run 不能同时使用。"
}

$mode = if ($Dev) { 'dev' } elseif ($Run) { 'run' } else { 'release' }
Write-Host "目标：C#；模式：$mode" -ForegroundColor White

Invoke-CSharp $mode
