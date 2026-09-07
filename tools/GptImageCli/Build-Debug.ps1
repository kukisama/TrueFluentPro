#Requires -Version 7.0
[CmdletBinding()]
param([switch]$NoOpen)

$ErrorActionPreference = 'Stop'
try {
    $output = Join-Path $PSScriptRoot 'bin/Debug/net10.0/gpt-image-cli'
    & dotnet build (Join-Path $PSScriptRoot 'GptImageCli.csproj') -c Debug -o $output
    if ($LASTEXITCODE -ne 0) { throw "Debug 构建失败，退出码：$LASTEXITCODE" }
    foreach ($relative in @('README.md', 'CAPABILITIES.md', 'skills/gpt-image-cli/SKILL.md')) {
        $destination = Join-Path $output ([IO.Path]::GetFileName($relative))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relative) -Destination $destination -Force
    }
    Write-Host "Debug 构建成功（运行需要 .NET 10）：$output"
    if (-not $NoOpen) { Start-Process explorer.exe -ArgumentList "/select,`"$output`"" }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}