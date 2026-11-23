$ErrorActionPreference = 'Stop'

$target = Join-Path $PSScriptRoot 'target'
$id = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$work = Join-Path $target "release-package-$id"
$dist = Join-Path $PSScriptRoot "dist\$id"

Push-Location $PSScriptRoot
try {
    $oldEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $metadata = cargo metadata --locked --no-deps --format-version 1
        if ($LASTEXITCODE -ne 0) { throw '无法读取 Cargo 工作区元数据。' }
    } finally {
        [Console]::OutputEncoding = $oldEncoding
    }
    $version = ($metadata | ConvertFrom-Json).packages.Where({ $_.name -eq 'voice-ime-tauri' })[0].version
    $name = "TrueFluentVoiceIme-$version-win-x64"
    $package = Join-Path $work $name
    $native = Join-Path $work 'native-build'

    cargo build --locked --release --bins -p voice-ime-tauri -p voice-ime-daemon --features tauri/custom-protocol --target x86_64-pc-windows-msvc --target-dir $target
    if ($LASTEXITCODE -ne 0) { throw 'Rust Release 构建失败。' }
    & "$PSScriptRoot\native\Build.ps1" -OutputDirectory $native -Configuration Release -Architectures @('x64', 'x86') -LinkMode Static -Version $version

    New-Item -ItemType Directory -Path $package, $dist | Out-Null
    $rust = Join-Path $target 'x86_64-pc-windows-msvc\release'
    foreach ($file in 'TrueFluentVoiceImeTools.exe', 'TrueFluentVoiceImeHost.exe') {
        Copy-Item -LiteralPath (Join-Path $rust $file) -Destination $package
    }
    foreach ($arch in 'win-x64', 'win-x86') {
        $archDir = Join-Path $package "voice-ime\$arch"
        New-Item -ItemType Directory -Path $archDir | Out-Null
        foreach ($file in 'VoiceImeStandalone.dll', 'VoiceImeRegister.exe') {
            Copy-Item -LiteralPath (Join-Path $native "$arch\$file") -Destination $archDir
        }
    }
    Copy-Item -LiteralPath "$PSScriptRoot\README.md" -Destination $package
    Copy-Item -LiteralPath "$PSScriptRoot\..\LICENSE" -Destination $package
    $source = Join-Path $package 'relink-source\native'
    New-Item -ItemType Directory -Path $source | Out-Null
    Get-ChildItem -LiteralPath "$PSScriptRoot\native" -Exclude 'artifacts', '.gitignore' |
        Copy-Item -Destination $source -Recurse

    $zip = Join-Path $dist "$name.zip"
    Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal
    $hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $name.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
    Write-Output "PACKAGE=$zip"
    Write-Output "NATIVE_TESTS=$native"
} finally {
    Pop-Location
}