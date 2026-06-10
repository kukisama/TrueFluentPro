# build-desktop.ps1 — 打包工作程序「译见 Pro」桌面应用（Tauri，Release）
# 优先用 cargo tauri build 生成安装包/捆绑产物（target/release/bundle）。
# 若未安装 tauri-cli，则回退为仅编译 release exe。
#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$workspace = $PSScriptRoot
$desktopDir = Join-Path $workspace "crates\desktop"

# 检测 tauri-cli
$hasTauri = $false
& cargo tauri --version *> $null
if ($LASTEXITCODE -eq 0) { $hasTauri = $true }

if ($hasTauri) {
    Write-Host "==> 使用 cargo tauri build 打包 tfp-desktop (release)..." -ForegroundColor Cyan
    Push-Location $desktopDir
    try {
        cargo tauri build
        if ($LASTEXITCODE -ne 0) { throw "tauri build 失败，退出码 $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
    Write-Host "==> 完成。捆绑产物：$workspace\target\release\bundle" -ForegroundColor Green
}
else {
    Write-Warning "未检测到 tauri-cli，回退为仅编译 release exe（不生成安装包）。"
    Write-Warning "如需安装包请先执行：cargo install tauri-cli --version `"^2.0`" --locked"
    Write-Host "==> cargo build -p tfp-desktop --release..." -ForegroundColor Cyan
    Push-Location $workspace
    try {
        cargo build -p tfp-desktop --release
        if ($LASTEXITCODE -ne 0) { throw "tfp-desktop 构建失败，退出码 $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }
    Write-Host "==> 完成。可执行文件：$workspace\target\release\tfp-desktop.exe" -ForegroundColor Green
}
