[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('Native', 'Toolbar')][string]$Target = 'Native'
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($OutputDirectory) -or
    $OutputDirectory -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)') { throw 'OutputDirectory 必须是绝对路径。' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw '未找到 Visual Studio Installer / vswhere。' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw '需要已安装的 Visual Studio C++ x64/x86 工具；本脚本不会安装。' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$versionsJsonPath = Join-Path $repoRoot 'Assets\versions.json'
$releaseNotesPath = Join-Path $repoRoot 'Assets\RELEASE_NOTES.md'
$productVersionStr = '2.2.3'
$productVersionCsv = '2,2,3,0'
if (Test-Path -LiteralPath $versionsJsonPath) {
    try {
        $verObj = Get-Content -LiteralPath $versionsJsonPath -Raw | ConvertFrom-Json -DateKind String
        $candidate = if ($verObj.voiceImeVersion) { [string]$verObj.voiceImeVersion } elseif ($verObj.appVersion) { [string]$verObj.appVersion } else { '' }
        if ($candidate -match '^v?(\d+)\.(\d+)\.(\d+)$') {
            $productVersionStr = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
            $productVersionCsv = "$($Matches[1]),$($Matches[2]),$($Matches[3]),0"
        }
    } catch { }
} elseif (Test-Path -LiteralPath $releaseNotesPath) {
    $firstLine = Get-Content -LiteralPath $releaseNotesPath -TotalCount 1
    if ($firstLine -match 'v(\d+)\.(\d+)\.(\d+)') {
        $productVersionStr = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
        $productVersionCsv = "$($Matches[1]),$($Matches[2]),$($Matches[3]),0"
    }
}
$rcVersionFlags = "/DVOICE_IME_VERSION_CSV=$productVersionCsv /DVOICE_IME_VERSION_STR=\`"$productVersionStr\`""
$buildIdentity = "$Configuration|$productVersionStr"
$runtime = if ($Configuration -eq 'Debug') { '/MTd /Od /Zi' } else { '/MT /O2 /DNDEBUG' }
$toolbarFlags = "/nologo /std:c++17 /EHsc /W4 /utf-8 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0602 $runtime"
$brandIcon = Join-Path $PSScriptRoot 'Brand.ico'
$brandInputs = @('BrandIconGenerator.cpp', 'VoiceBrand.h') | ForEach-Object { Get-Item -LiteralPath (Join-Path $PSScriptRoot $_) }
$brandNewest = ($brandInputs | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
if (-not (Test-Path -LiteralPath $brandIcon) -or (Get-Item -LiteralPath $brandIcon).LastWriteTimeUtc -lt $brandNewest) {
    $brandObj = Join-Path $OutputDirectory "brand-obj-$Configuration"
    New-Item -ItemType Directory -Force -Path $brandObj | Out-Null
    $maker = Join-Path $brandObj 'BrandIconGenerator.exe'
    $batch = Join-Path $brandObj 'make-icon.cmd'
    $lines = @('@echo off', '@chcp 65001 >nul', "call `"$vcvars`" x64 >nul", 'if errorlevel 1 exit /b %errorlevel%',
        "cl $toolbarFlags `"$(Join-Path $PSScriptRoot 'BrandIconGenerator.cpp')`" /Fe:`"$maker`" /Fo:`"$brandObj\\`" /Fd:`"$brandObj\maker.pdb`" /link /INCREMENTAL:NO gdiplus.lib user32.lib gdi32.lib",
        'if errorlevel 1 exit /b %errorlevel%', "`"$maker`" `"$brandIcon`"", 'if errorlevel 1 exit /b %errorlevel%')
    Set-Content -LiteralPath $batch -Value $lines -Encoding utf8
    & $env:ComSpec /d /s /c "`"$batch`""
    if ($LASTEXITCODE -ne 0) { throw "品牌图标生成失败，退出码 $LASTEXITCODE" }
}
# This early branch never reads or links Registration, libIME2, Common or C#.
if ($Target -eq 'Toolbar') {
    $toolbarInputs = @('VoiceToolbar.cpp', 'VoiceToolbar.h', 'VoiceBrand.h', 'ToolbarTests.cpp', 'Brand.rc', 'Brand.ico', 'Build.ps1') |
        ForEach-Object { Get-Item -LiteralPath (Join-Path $PSScriptRoot $_) }
    $toolbarNewest = ($toolbarInputs | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    foreach ($arch in @('x64', 'x86')) {
        $out = Join-Path $OutputDirectory "win-$arch"
        $obj = Join-Path $out "obj-Toolbar-$Configuration"
        New-Item -ItemType Directory -Force -Path $obj | Out-Null
        $test = Join-Path $out 'VoiceToolbarTests.exe'
        $stamp = Join-Path $obj 'built.toolbar.ok'
        if ((Test-Path -LiteralPath $stamp) -and (Test-Path -LiteralPath $test) -and
            (Get-Item -LiteralPath $stamp).LastWriteTimeUtc -ge $toolbarNewest -and
            (Get-Content -LiteralPath $stamp -Raw).Trim() -eq $buildIdentity) {
            Write-Output "UP-TO-DATE Toolbar win-$arch $Configuration"; continue
        }
        $res = Join-Path $obj 'Brand.res'
        $commands = @("call `"$vcvars`" $arch >nul",
            "rc /nologo /c65001 $rcVersionFlags /I `"$PSScriptRoot`" /fo `"$res`" `"$(Join-Path $PSScriptRoot 'Brand.rc')`"",
            "cl $toolbarFlags /DVOICE_TOOLBAR_TEST `"$(Join-Path $PSScriptRoot 'ToolbarTests.cpp')`" `"$(Join-Path $PSScriptRoot 'VoiceToolbar.cpp')`" /Fe:`"$test`" /Fo:`"$obj\\`" /Fd:`"$obj\toolbar.pdb`" /link `"$res`" /INCREMENTAL:NO /DYNAMICBASE /NXCOMPAT /MANIFEST:EMBED /MANIFESTUAC:`"level='asInvoker' uiAccess='false'`" gdiplus.lib dwmapi.lib user32.lib gdi32.lib shell32.lib")
        $batch = Join-Path $obj 'build-toolbar.cmd'
        $lines = @('@echo off', '@chcp 65001 >nul')
        foreach ($command in $commands) { $lines += $command; $lines += 'if errorlevel 1 exit /b %errorlevel%' }
        Set-Content -LiteralPath $batch -Value $lines -Encoding utf8
        & $env:ComSpec /d /s /c "`"$batch`""
        if ($LASTEXITCODE -ne 0) { throw "Toolbar win-$arch 编译失败，退出码 $LASTEXITCODE" }
        Set-Content -LiteralPath $stamp -Value $buildIdentity -Encoding ASCII
        Write-Output "BUILT Toolbar win-$arch $Configuration"
    }
    return
}
$libImeRoot = Join-Path $PSScriptRoot 'ThirdParty\libIME2'
$libImeSrc = Join-Path $libImeRoot 'src'
if (-not (Test-Path -LiteralPath (Join-Path $libImeSrc 'TextService.cpp'))) { throw '缺少固定 vendored libIME2 源码。' }
$inputs = @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in '.cpp', '.h', '.hpp', '.def', '.ps1', '.rc', '.ico' }) +
    @(Get-ChildItem -LiteralPath $libImeSrc -File | Where-Object { $_.Extension -in '.cpp', '.h' })
$newest = ($inputs | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
foreach ($arch in @('x64', 'x86')) {
    $out = Join-Path $OutputDirectory "win-$arch"
    $obj = Join-Path $out "obj-$Configuration"
    New-Item -ItemType Directory -Force -Path $obj | Out-Null
    $stamp = Join-Path $obj 'built.ok'
    $dll = Join-Path $out 'TrueFluentVoiceIme.dll'
    $exe = Join-Path $out 'VoiceImeRegister.exe'
    $test = Join-Path $out 'VoiceImeTests.exe'
    $editTest = Join-Path $out 'VoiceImeEditTests.exe'
    $registrationTest = Join-Path $out 'VoiceImeRegistrationTests.exe'
    $lifecycleTest = Join-Path $out 'VoiceImeLifecycleTests.exe'
    $build = -not ((Test-Path -LiteralPath $stamp) -and (Test-Path -LiteralPath $dll) -and
        (Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $test) -and (Test-Path -LiteralPath $editTest) -and
        (Test-Path -LiteralPath $registrationTest) -and (Test-Path -LiteralPath $lifecycleTest))
    if (-not $build) {
        $built = (Get-Item -LiteralPath $stamp).LastWriteTimeUtc
        $build = $built -lt $newest -or (Get-Content -LiteralPath $stamp -Raw).Trim() -ne $buildIdentity -or
            (Get-Item -LiteralPath $dll).LastWriteTimeUtc -gt $built -or (Get-Item -LiteralPath $exe).LastWriteTimeUtc -gt $built
    }
    if (-not $build) { Write-Output "UP-TO-DATE win-$arch $Configuration"; continue }
    $runtime = if ($Configuration -eq 'Debug') { '/MTd /Od /Zi' } else { '/MT /O2 /DNDEBUG' }
    $flags = "/nologo /std:c++17 /EHsc /W4 /utf-8 /DUNICODE /D_UNICODE /DVOICE_IME_RUNTIME_ONLY /D_WIN32_WINNT=0x0602 $runtime"
    $def = if ($arch -eq 'x86') { 'Exports.x86.def' } else { 'Exports.def' }
    $sources = 'VoiceIme.cpp', 'VoiceToolbar.cpp', 'ToolbarBridge.cpp', 'PipeClient.cpp', 'Registration.cpp' | ForEach-Object { '"' + (Join-Path $PSScriptRoot $_) + '"' }
    $brandResource = Join-Path $obj 'Brand.res'
    $toolbarSource = '"' + (Join-Path $PSScriptRoot 'VoiceToolbar.cpp') + '"'
    $bridgeSource = '"' + (Join-Path $PSScriptRoot 'ToolbarBridge.cpp') + '"'
    # Compile upstream core and its Win32 UI support, with the same CRT as the adapter.
    # No CMake/gtest installation; the upstream registration implementation is excluded.
    $libImeObj = Join-Path $obj 'libIME2'
    New-Item -ItemType Directory -Force -Path $libImeObj | Out-Null
    $libImeLibrary = Join-Path $libImeObj 'libIME2.lib'
    $libImeSources = 'ImeModule', 'libIME', 'TextService', 'KeyEvent', 'EditSession',
        'DisplayAttributeInfo', 'DisplayAttributeInfoEnum', 'DisplayAttributeProvider',
        'LangBarButton', 'Utils', 'DrawUtils', 'Window', 'ImeWindow', 'MessageWindow', 'CandidateWindow' |
        ForEach-Object { '"' + (Join-Path $libImeSrc "$_.cpp") + '"' }
    $libs = 'ole32.lib oleaut32.lib uuid.lib advapi32.lib user32.lib gdi32.lib gdiplus.lib'
    $uiLibs = 'dwmapi.lib shell32.lib'
    $imeLibs = '"' + $libImeLibrary + '" shlwapi.lib'
    # Embed, not merely generate a sidecar: x86 must not silently virtualize failed HKLM writes.
    $exeLink = '/INCREMENTAL:NO /MANIFEST:EMBED /MANIFESTUAC:"level=''asInvoker'' uiAccess=''false''"'
    # vcvarsall chooses the installed Windows SDK, INCLUDE and LIB. Environment changes stay inside cmd.exe.
    $commands = @(
        "call `"$vcvars`" $arch >nul",
        "rc /nologo /c65001 $rcVersionFlags /I `"$PSScriptRoot`" /fo `"$brandResource`" `"$(Join-Path $PSScriptRoot 'Brand.rc')`"",
        "cl $flags /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS /c $($libImeSources -join ' ') /Fo:`"$libImeObj\\`" /Fd:`"$libImeObj\libIME2.pdb`"",
        "lib /nologo /OUT:`"$libImeLibrary`" `"$libImeObj\*.obj`"",
        "cl $flags /LD $($sources -join ' ') /Fe:`"$dll`" /Fo:`"$obj\\`" /Fd:`"$obj\dll.pdb`" /link `"$brandResource`" /DEF:`"$(Join-Path $PSScriptRoot $def)`" /INCREMENTAL:NO /DYNAMICBASE /NXCOMPAT $libs $uiLibs $imeLibs",
        "cl $flags `"$(Join-Path $PSScriptRoot 'RegisterMain.cpp')`" `"$(Join-Path $PSScriptRoot 'Registration.cpp')`" /Fe:`"$exe`" /Fo:`"$obj\\`" /Fd:`"$obj\helper.pdb`" /link `"$brandResource`" $exeLink $libs",
        "cl $flags `"$(Join-Path $PSScriptRoot 'Tests.cpp')`" /Fe:`"$test`" /Fo:`"$obj\\`" /Fd:`"$obj\tests.pdb`" /link $exeLink $libs",
        "cl $flags `"$(Join-Path $PSScriptRoot 'EditTests.cpp')`" $toolbarSource $bridgeSource `"$(Join-Path $PSScriptRoot 'Registration.cpp')`" /Fe:`"$editTest`" /Fo:`"$obj\\`" /Fd:`"$obj\edits.pdb`" /link `"$brandResource`" $exeLink $libs $uiLibs $imeLibs",
        "cl $flags `"$(Join-Path $PSScriptRoot 'RegistrationTests.cpp')`" /Fe:`"$registrationTest`" /Fo:`"$obj\\`" /Fd:`"$obj\registration-tests.pdb`" /link $exeLink $libs",
        "cl $flags `"$(Join-Path $PSScriptRoot 'LifecycleTests.cpp')`" $toolbarSource $bridgeSource `"$(Join-Path $PSScriptRoot 'Registration.cpp')`" /Fe:`"$lifecycleTest`" /Fo:`"$obj\\`" /Fd:`"$obj\lifecycle-tests.pdb`" /link `"$brandResource`" $exeLink $libs $uiLibs $imeLibs"
    )
    Push-Location $obj
    try {
        # Keep each compiler invocation below cmd.exe's combined command-line limit.
        $batch = Join-Path $obj 'build.cmd'
        $batchLines = @('@echo off', '@chcp 65001 >nul')
        foreach ($command in $commands) { $batchLines += $command; $batchLines += 'if errorlevel 1 exit /b %errorlevel%' }
        Set-Content -LiteralPath $batch -Value $batchLines -Encoding utf8
        & $env:ComSpec /d /s /c "`"$batch`""
        if ($LASTEXITCODE -ne 0) { throw "win-$arch 编译失败，退出码 $LASTEXITCODE" }
        Set-Content -LiteralPath $stamp -Value $buildIdentity -Encoding ASCII
    } finally { Pop-Location }
    Write-Output "BUILT win-$arch $Configuration"
}