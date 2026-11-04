[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TargetDir
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -eq 'true' -or -not (Test-Path -LiteralPath 'HKCU:\Software\TrueFluentPro\VoiceIme')) { exit 0 }
$TargetDir = [IO.Path]::GetFullPath($TargetDir)
if (-not (Test-Path -LiteralPath (Join-Path $TargetDir 'TrueFluentPro.dll'))) { exit 0 }

$hostDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TrueFluentPro\VoiceIme\host'

# Stop any running standalone VoiceIme host from $hostDir so files can be updated cleanly
foreach ($p in (Get-Process -Name 'TrueFluentPro', 'TrueFluentVoiceImeHost' -ErrorAction SilentlyContinue)) {
    try {
        if ($p.Path -and $p.Path.StartsWith($hostDir + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $p.Kill()
            $null = $p.WaitForExit(1500)
        }
    } catch { }
}

if ((Test-Path -LiteralPath $hostDir) -and (Test-Path -LiteralPath (Join-Path $hostDir 'hostfxr.dll'))) {
    Remove-Item -LiteralPath $hostDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $hostDir | Out-Null

foreach ($file in (Get-ChildItem -LiteralPath $TargetDir -File)) {
    if ($file.Extension -in @('.dll', '.exe', '.json', '.pdb', '.md')) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $hostDir $file.Name) -Force -ErrorAction SilentlyContinue
    }
}

$objDll = [IO.Path]::GetFullPath((Join-Path $TargetDir '..\..\..\obj\Release\net10.0\TrueFluentPro.dll'))
if ((Test-Path -LiteralPath $objDll) -and ((Get-Item -LiteralPath $objDll).LastWriteTimeUtc -gt (Get-Item -LiteralPath (Join-Path $TargetDir 'TrueFluentPro.dll')).LastWriteTimeUtc)) {
    Copy-Item -LiteralPath $objDll -Destination (Join-Path $hostDir 'TrueFluentPro.dll') -Force -ErrorAction SilentlyContinue
}

foreach ($sub in @('runtimes', 'Assets', 'voice-ime')) {
    $subSource = Join-Path $TargetDir $sub
    if (Test-Path -LiteralPath $subSource) {
        Copy-Item -LiteralPath $subSource -Destination $hostDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$standaloneExe = Join-Path $hostDir 'TrueFluentPro.exe'
$mainExe = Join-Path $TargetDir 'TrueFluentPro.exe'
New-Item -Path 'HKCU:\Software\TrueFluentPro\VoiceImeUI' -Force | Out-Null
Set-ItemProperty -LiteralPath 'HKCU:\Software\TrueFluentPro\VoiceImeUI' -Name 'MainAppExe' -Value $mainExe
Set-ItemProperty -LiteralPath 'HKCU:\Software\TrueFluentPro\VoiceImeUI' -Name 'StandaloneHostExe' -Value $standaloneExe
# HostExe / VoiceImeInstall 属于安装事务，必须与 install-manifest.json 保持一致。
# 原生 ReadHost(true) / TrustedServer 已支持 VoiceImeUI\StandaloneHostExe；
# 热同步只更新独立宿主入口，不能绕过生命周期改写安装身份。

# If the IME is installed, pre-start the standalone host in background so it is immediately active without logoff
if ((Test-Path -LiteralPath 'HKCU:\Software\TrueFluentPro\VoiceIme') -and (Test-Path -LiteralPath $standaloneExe)) {
    Start-Process -FilePath $standaloneExe -ArgumentList @('--voice-ime-host', '--shared') -WorkingDirectory $hostDir -WindowStyle Hidden
}
