$ErrorActionPreference = 'Stop'

$target = Join-Path $PSScriptRoot 'target'
$native = Join-Path $target 'release-native'
$dist = Join-Path $PSScriptRoot 'dist'
$triple = 'x86_64-pc-windows-msvc'
$apps = [ordered]@{
    'voice-ime-tauri' = 'TrueFluentVoiceImeTools'
    'voice-ime-daemon' = 'TrueFluentVoiceImeHost'
}

Push-Location $PSScriptRoot
try {
    $oldEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $metadata = cargo metadata --locked --format-version 1 --filter-platform $triple
        if ($LASTEXITCODE -ne 0) { throw '无法读取 Cargo 工作区元数据。' }
    } finally {
        [Console]::OutputEncoding = $oldEncoding
    }
    $packages = (($metadata -join "`n") | ConvertFrom-Json).packages
    $version = ($packages | Where-Object name -eq 'voice-ime-tauri').version
    $sqliteInclude = Join-Path (Split-Path ($packages | Where-Object name -eq 'libsqlite3-sys').manifest_path) 'sqlite3'
    & "$PSScriptRoot\native\Build.ps1" -OutputDirectory $native -Configuration Release -Architectures @('x64', 'x86') -LinkMode Static -Version $version -SqliteSource (Join-Path $sqliteInclude 'sqlite3.c')

    # libsqlite3-sys 的官方链接覆盖：保留 bundled bindings，改链接本次生成的导入库。
    $sqlite = Join-Path $native 'sqlite-x64'
    $overrides = @{
        LIBSQLITE3_SYS_USE_PKG_CONFIG = '1'
        SQLITE3_NO_PKG_CONFIG = '1'
        SQLITE3_LIB_DIR = $sqlite
        SQLITE3_INCLUDE_DIR = $sqliteInclude
        SQLITE3_STATIC = '0'
    }
    $previous = @{}
    try {
        foreach ($key in $overrides.Keys) {
            $previous[$key] = [Environment]::GetEnvironmentVariable($key)
            Set-Item -LiteralPath "Env:$key" -Value $overrides[$key]
        }
        foreach ($app in $apps.GetEnumerator()) {
            $features = 'voice-ime-win32/private-sqlite'
            if ($app.Key -eq 'voice-ime-tauri') { $features += ',tauri/custom-protocol' }
            cargo rustc --locked --release -p $app.Key --bin $app.Value --features $features --target $triple --target-dir $target -- -C link-arg=/DEPENDENTLOADFLAG:0xA00
            if ($LASTEXITCODE -ne 0) { throw "Rust Release 构建失败：$($app.Value)" }
        }
    } finally {
        foreach ($key in $previous.Keys) {
            if ($null -eq $previous[$key]) { Remove-Item -LiteralPath "Env:$key" -ErrorAction SilentlyContinue }
            else { Set-Item -LiteralPath "Env:$key" -Value $previous[$key] }
        }
    }

    # 不覆盖仍在运行的文件；也不结束已安装版或旧目录里的进程。
    $running = Get-Process -Name @($apps.Values) -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and (Split-Path $_.Path) -eq $dist
    }
    if ($running) { throw '请关闭 dist 中正在运行的输入法工具或后台，再重新构建。' }
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $rust = Join-Path $target "$triple\release"
    Copy-Item -LiteralPath (Join-Path $sqlite 'TrueFluentVoiceImeSqlite.dll') -Destination $dist -Force
    foreach ($exe in $apps.Values) {
        Copy-Item -LiteralPath (Join-Path $rust "$exe.exe") -Destination $dist -Force
    }
    foreach ($arch in 'win-x64', 'win-x86') {
        $archDir = Join-Path $dist "voice-ime\$arch"
        New-Item -ItemType Directory -Force -Path $archDir | Out-Null
        foreach ($file in 'VoiceImeStandalone.dll', 'VoiceImeRegister.exe') {
            Copy-Item -LiteralPath (Join-Path $native "$arch\$file") -Destination $archDir -Force
        }
    }
    Copy-Item -LiteralPath "$PSScriptRoot\README.md", "$PSScriptRoot\..\LICENSE" -Destination $dist -Force
    $source = Join-Path $dist 'relink-source\native'
    New-Item -ItemType Directory -Force -Path $source | Out-Null
    Get-ChildItem -LiteralPath "$PSScriptRoot\native" -Exclude 'artifacts', '.gitignore', 'Brand.ico' |
        Copy-Item -Destination $source -Recurse -Force

    Write-Output "DIRECTORY=$dist"
    Write-Output "NATIVE_TESTS=$native"
} finally {
    Pop-Location
}