# build-sdk.ps1 — 打包 speech-sdk（Release）
# speech-sdk 是 FFI 库 crate，构建时其 build.rs 会下载/解压 Azure Speech 原生库到 target/release。
#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$workspace = $PSScriptRoot

Write-Host "==> 构建 speech-sdk (release)..." -ForegroundColor Cyan
Push-Location $workspace
try {
    cargo build -p speech-sdk --release
    if ($LASTEXITCODE -ne 0) { throw "speech-sdk 构建失败，退出码 $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Host "==> 完成。产物目录：$workspace\target\release" -ForegroundColor Green
