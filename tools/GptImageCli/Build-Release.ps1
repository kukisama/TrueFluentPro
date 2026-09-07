#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$NoOpen
)

$ErrorActionPreference = 'Stop'
try {
    $output = Join-Path $PSScriptRoot "bin/Release/net10.0/$Runtime/publish/gpt-image-cli"
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Publish.ps1') -Runtime $Runtime -Mode NativeAot -OutputDirectory $output
    if ($LASTEXITCODE -ne 0) { throw "Release 发布失败，退出码：$LASTEXITCODE" }
    Write-Host "Release 交付目录（无需另装 .NET）：$output"
    if (-not $NoOpen) { Start-Process explorer.exe -ArgumentList "/select,`"$output`"" }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}