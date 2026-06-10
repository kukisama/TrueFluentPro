# build-test-ui.ps1 — 打包测试程序 speech-test-ui（eframe/egui，Release）
# 构建时其 build.rs 会把 Speech 原生库复制到 target/release 与 exe 同目录。
#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$workspace = Join-Path $PSScriptRoot "rustnew-backend"

Write-Host "==> 构建 speech-test-ui (release)..." -ForegroundColor Cyan
Push-Location $workspace
try {
    cargo build -p speech-test-ui --release
    if ($LASTEXITCODE -ne 0) { throw "speech-test-ui 构建失败，退出码 $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Host "==> 完成。可执行文件：$workspace\target\release\speech-test-ui.exe" -ForegroundColor Green
