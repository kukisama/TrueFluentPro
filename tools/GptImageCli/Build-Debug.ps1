#Requires -Version 7.0
[CmdletBinding()]
param([switch]$NoOpen)

$ErrorActionPreference = 'Stop'
try {
    $output = Join-Path $PSScriptRoot 'bin/Debug/net10.0/gpt-image-cli'
    & dotnet build (Join-Path $PSScriptRoot 'GptImageCli.csproj') -c Debug -o $output
    if ($LASTEXITCODE -ne 0) { throw "Debug 构建失败，退出码：$LASTEXITCODE" }
    foreach ($relative in @('README.md', 'CAPABILITIES.md')) {
        $destination = Join-Path $output ([IO.Path]::GetFileName($relative))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relative) -Destination $destination -Force
    }
    $obsolete = Join-Path $output 'Invoke-GptImageModelComparison.ps1'
    if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete }
    $skillRoot = Join-Path $PSScriptRoot 'skills/gpt-image-cli'
    foreach ($asset in Get-ChildItem -LiteralPath $skillRoot -Recurse -File) {
        $destination = Join-Path $output ([IO.Path]::GetRelativePath($skillRoot, $asset.FullName))
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        Copy-Item -LiteralPath $asset.FullName -Destination $destination -Force
    }
    Write-Host "Debug 构建成功（运行需要 .NET 10）：$output"
    if (-not $NoOpen) { Start-Process explorer.exe -ArgumentList "/select,`"$output`"" }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}