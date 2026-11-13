[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string[]]$Architectures = @('x64', 'x86', 'arm64x'),
    [ValidateSet('Static', 'DynamicLibIme')][string]$LinkMode = 'Static',
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { throw 'OutputDirectory 必须是绝对路径。' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw '未找到 Visual Studio Installer / vswhere。' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw '需要已安装的 Visual Studio C++ x64/x86 工具链。' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "版本号格式无效: $Version" }
$versionCsv = "$($Matches[1]),$($Matches[2]),$($Matches[3]),0"
$rcVersionFlags = "/DVOICE_IME_VERSION_CSV=$versionCsv /DVOICE_IME_VERSION_STR=\`"$Version\`""

$runtime = if ($Configuration -eq 'Debug') { '/MTd /Od /Zi' } else { '/MT /O2 /DNDEBUG' }
$commonFlags = "/nologo /std:c++17 /EHsc /W4 /utf-8 /DUNICODE /D_UNICODE /DVOICE_IME_RUNTIME_ONLY /D_WIN32_WINNT=0x0602 $runtime"

# 1. 生成高清多尺寸品牌图标 Brand.ico
$brandIcon = Join-Path $PSScriptRoot 'Brand.ico'
if (-not (Test-Path -LiteralPath $brandIcon)) {
    $brandObj = Join-Path $OutputDirectory "brand-obj-$Configuration"
    New-Item -ItemType Directory -Force -Path $brandObj | Out-Null
    $maker = Join-Path $brandObj 'BrandIconGenerator.exe'
    $batch = Join-Path $brandObj 'make-icon.cmd'
    $lines = @(
        '@echo off', '@chcp 65001 >nul',
        "call `"$vcvars`" x64 >nul", 'if errorlevel 1 exit /b %errorlevel%',
        "cl $commonFlags `"$(Join-Path $PSScriptRoot 'BrandIconGenerator.cpp')`" /Fe:`"$maker`" /Fo:`"$brandObj\\`" /link /INCREMENTAL:NO gdiplus.lib user32.lib gdi32.lib",
        'if errorlevel 1 exit /b %errorlevel%',
        "`"$maker`" `"$brandIcon`""
    )
    Set-Content -LiteralPath $batch -Value $lines -Encoding utf8
    & $env:ComSpec /d /s /c "`"$batch`""
    if ($LASTEXITCODE -ne 0) { throw "Brand.ico 生成失败 ($LASTEXITCODE)" }
}

$libImeSrc = Join-Path $PSScriptRoot 'ThirdParty\libIME2\src'
$libImeUnits = 'ImeModule', 'libIME', 'TextService', 'KeyEvent', 'EditSession',
    'DisplayAttributeInfo', 'DisplayAttributeInfoEnum', 'DisplayAttributeProvider',
    'LangBarButton', 'Utils', 'DrawUtils', 'Window', 'ImeWindow', 'MessageWindow', 'CandidateWindow'
$libImeSources = $libImeUnits | ForEach-Object { '"' + (Join-Path $libImeSrc "$_.cpp") + '"' }
$coreUnits = 'VoiceIme', 'VoiceToolbar', 'ToolbarBridge', 'PipeClient', 'Registration'
$coreSources = $coreUnits | ForEach-Object { '"' + (Join-Path $PSScriptRoot "$_.cpp") + '"' }

$sysLibs = 'ole32.lib oleaut32.lib uuid.lib advapi32.lib user32.lib gdi32.lib gdiplus.lib dwmapi.lib shell32.lib shlwapi.lib'
$exeLink = '/INCREMENTAL:NO /MANIFEST:EMBED /MANIFESTUAC:"level=''asInvoker'' uiAccess=''false''"'

foreach ($arch in $Architectures) {
    $out = Join-Path $OutputDirectory "win-$arch"
    $obj = Join-Path $out "obj-$Configuration"
    New-Item -ItemType Directory -Force -Path $obj | Out-Null

    if ($arch -eq 'arm64x') {
        # 检查是否安装了 ARM64 / ARM64EC 编译组件
        $hasArm64 = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64 -property installationPath
        $hasArm64EC = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64EC -property installationPath
        if (-not $hasArm64 -or -not $hasArm64EC) {
            Write-Warning "当前机器未安装 VS ARM64/ARM64EC 工具链，跳过 win-arm64x 构建。"
            continue
        }
        $objArm64 = Join-Path $obj 'arm64'
        $objArm64Ec = Join-Path $obj 'arm64ec'
        New-Item -ItemType Directory -Force -Path $objArm64, $objArm64Ec | Out-Null
        $res = Join-Path $obj 'Brand.res'
        $dll = Join-Path $out 'VoiceImeStandalone.dll'
        $exe = Join-Path $out 'VoiceImeRegister.exe'
        $def = Join-Path $PSScriptRoot 'Exports.arm64x.def'

        $commands = @(
            "call `"$vcvars`" x64_arm64 >nul",
            "rc /nologo /c65001 $rcVersionFlags /I `"$PSScriptRoot`" /fo `"$res`" `"$(Join-Path $PSScriptRoot 'Brand.rc')`"",
            "cl $commonFlags /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS /c $($libImeSources -join ' ') $($coreSources -join ' ') /Fo:`"$objArm64\\`"",
            "cl $commonFlags `"$(Join-Path $PSScriptRoot 'RegisterMain.cpp')`" `"$(Join-Path $PSScriptRoot 'Registration.cpp')`" /Fe:`"$exe`" /Fo:`"$objArm64\\`" /link `"$res`" $exeLink $sysLibs",
            "call `"$vcvars`" x64_arm64ec >nul",
            "cl /arm64EC $commonFlags /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS /c $($libImeSources -join ' ') $($coreSources -join ' ') /Fo:`"$objArm64Ec\\`"",
            "link /nologo /DLL /MACHINE:ARM64X /DEF:`"$def`" /DEFARM64NATIVE:`"$def`" /OUT:`"$dll`" `"$res`" `"$objArm64\*.obj`" `"$objArm64Ec\*.obj`" $sysLibs"
        )
        $batch = Join-Path $obj 'build-arm64x.cmd'
        $lines = @('@echo off', '@chcp 65001 >nul')
        foreach ($cmd in $commands) { $lines += $cmd; $lines += 'if errorlevel 1 exit /b %errorlevel%' }
        Set-Content -LiteralPath $batch -Value $lines -Encoding utf8
        & $env:ComSpec /d /s /c "`"$batch`""
        if ($LASTEXITCODE -ne 0) { throw "win-arm64x 编译失败 ($LASTEXITCODE)" }
        Write-Output "BUILT win-arm64x $Configuration"
        continue
    }

    $vcArch = if ($arch -eq 'arm64') { 'x64_arm64' } else { $arch }
    $defFile = if ($arch -eq 'x86') { 'Exports.x86.def' } else { 'Exports.def' }
    $libImeObj = Join-Path $obj 'libIME2'
    New-Item -ItemType Directory -Force -Path $libImeObj | Out-Null
    $libImeLib = Join-Path $libImeObj 'libIME2.lib'
    $res = Join-Path $obj 'Brand.res'
    $dll = Join-Path $out 'VoiceImeStandalone.dll'
    $exe = Join-Path $out 'VoiceImeRegister.exe'
    $testExe = Join-Path $out 'VoiceImeTests.exe'
    $regTestExe = Join-Path $out 'VoiceImeRegistrationTests.exe'

    $libBuildStep = if ($LinkMode -eq 'DynamicLibIme') {
        $libImeDll = Join-Path $out 'libIME2.dll'
        "cl $commonFlags /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS /DlibIME_EXPORTS /LD $($libImeSources -join ' ') /Fe:`"$libImeDll`" /Fo:`"$libImeObj\\`" /link /IMPLIB:`"$libImeLib`" $sysLibs"
    } else {
        @(
            "cl $commonFlags /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS /c $($libImeSources -join ' ') /Fo:`"$libImeObj\\`"",
            "lib /nologo /OUT:`"$libImeLib`" `"$libImeObj\*.obj`""
        )
    }

    $commands = @(
        "call `"$vcvars`" $vcArch >nul",
        "rc /nologo /c65001 $rcVersionFlags /I `"$PSScriptRoot`" /fo `"$res`" `"$(Join-Path $PSScriptRoot 'Brand.rc')`""
    ) + $libBuildStep + @(
        "cl $commonFlags /LD $($coreSources -join ' ') /Fe:`"$dll`" /Fo:`"$obj\\`" /link `"$res`" /DEF:`"$(Join-Path $PSScriptRoot $defFile)`" /INCREMENTAL:NO /DYNAMICBASE /NXCOMPAT `"$libImeLib`" $sysLibs",
        "cl $commonFlags `"$(Join-Path $PSScriptRoot 'RegisterMain.cpp')`" `"$(Join-Path $PSScriptRoot 'Registration.cpp')`" /Fe:`"$exe`" /Fo:`"$obj\\`" /link `"$res`" $exeLink $sysLibs",
        "cl $commonFlags `"$(Join-Path $PSScriptRoot 'Tests.cpp')`" /Fe:`"$testExe`" /Fo:`"$obj\\`" /link $exeLink $sysLibs",
        "cl $commonFlags `"$(Join-Path $PSScriptRoot 'RegistrationTests.cpp')`" /Fe:`"$regTestExe`" /Fo:`"$obj\\`" /link $exeLink $sysLibs"
    )

    $batch = Join-Path $obj 'build.cmd'
    $lines = @('@echo off', '@chcp 65001 >nul')
    foreach ($cmd in $commands) { $lines += $cmd; $lines += 'if errorlevel 1 exit /b %errorlevel%' }
    Set-Content -LiteralPath $batch -Value $lines -Encoding utf8
    & $env:ComSpec /d /s /c "`"$batch`""
    if ($LASTEXITCODE -ne 0) { throw "win-$arch 编译失败 ($LASTEXITCODE)" }
    Write-Output "BUILT win-$arch $Configuration ($LinkMode)"
}
