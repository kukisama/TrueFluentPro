[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9]+(?:-[a-z0-9]+)+$')]
    [string]$Runtime = 'win-x64',
    [ValidateSet('NativeAot', 'Managed')]
    [string]$Mode = 'NativeAot'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $project = Join-Path $PSScriptRoot 'GptImageCli.csproj'
    $folder = if ($Mode -eq 'NativeAot') { 'gpt-image-cli-aot' } else { 'gpt-image-cli-standalone' }
    $output = Join-Path $repoRoot "artifacts/$folder/$Runtime"
    foreach ($required in @('README.md', 'skills/gpt-image-cli/SKILL.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $required) -PathType Leaf)) {
            throw "缺少发布所需文件：$required"
        }
    }

    # 只发布独立 CLI；不读取配置/密钥，不删除目录或终止进程。
    $publishOptions = if ($Mode -eq 'NativeAot') {
        @('-p:PublishAot=true', '-p:PublishSingleFile=false', '-p:OptimizationPreference=Size')
    } else {
        @('-p:PublishAot=false', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
    }
    & dotnet publish $project -c Release -r $Runtime --self-contained true `
        @publishOptions `
        -p:DebugType=None -p:DebugSymbols=false -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码：$LASTEXITCODE"
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output -Force
    $capabilities = Join-Path $PSScriptRoot 'CAPABILITIES.md'
    if (Test-Path -LiteralPath $capabilities -PathType Leaf) {
        Copy-Item -LiteralPath $capabilities -Destination $output -Force
    }
    else {
        Write-Warning 'CAPABILITIES.md 尚未提供，已跳过复制；旧副本不会删除，分发前请核对。'
    }
    $skillsOutput = Join-Path $output 'skills'
    New-Item -ItemType Directory -Path $skillsOutput -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'skills/gpt-image-cli') `
        -Destination $skillsOutput -Recurse -Force
    $executable = Join-Path $output $(if ($Runtime.StartsWith('win-')) { 'gpt-image.exe' } else { 'gpt-image' })
    $manifest = [ordered]@{
        mode = $Mode
        runtime = $Runtime
        executable = [IO.Path]::GetFileName($executable)
        bytes = (Get-Item -LiteralPath $executable).Length
        sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Encoding utf8
    Write-Host "独立发布成功：$output"
    exit 0
}
catch {
    [Console]::Error.WriteLine("独立发布失败：$($_.Exception.Message)")
    exit 1
}