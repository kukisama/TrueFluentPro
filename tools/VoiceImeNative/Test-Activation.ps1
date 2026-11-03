#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [ValidateSet('x64', 'x86')][string[]]$Architecture = @('x64'),
    [string]$ExpectedDll
)
$ErrorActionPreference = 'Stop'
$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
if (-not $ArtifactDirectory) {
    $ArtifactDirectory = Join-Path $artifactRoot ('activation-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
if (-not $ArtifactDirectory.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '验收产物必须保存在 tools\VoiceImeNative\artifacts 内。'
}
if (Test-Path -LiteralPath $ArtifactDirectory) { throw '验收目录必须是新目录，不覆盖已有证据。' }
New-Item -ItemType Directory -Path $ArtifactDirectory | Out-Null
$summary = [ordered]@{
    StartedAt = [DateTimeOffset]::Now.ToString('o'); ArtifactDirectory = $ArtifactDirectory
    Scope = '真实系统 TIP 激活、生产 ServiceUI/500ms 定时器/Idle/hover 与按键资格；一张 OWNMAIN 实际截图；不录音，不派发 F8，不注入合成 P/F。'
    Constraints = '只编译 ActivationTests.cpp；FORPROCESS 激活/恢复；先正常 Show 再短暂 TOPMOST；每次 SendInput 前核对自有外露区域根 HWND/PID 和鼠标状态，无焦点仅允许已验证置顶自有区域的一次普通点击，点击后验真实前台；finally 还原鼠标/前台焦点/自有profile并关闭，原前台已恢复则不重复请求。无 AttachThreadInput/系统登记卸载/部署/默认或session修改/UAC/用户配置/云端/麦克风/用户应用启动或结束。'
    Source = Join-Path $PSScriptRoot 'ActivationTests.cpp'; SourceSha256 = $null
    ScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    ExpectedDll = $ExpectedDll; Preflight = @(); Builds = @(); Runs = @(); ExitCode = 1
    Sources = @(
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-activateprofile',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-getforeground',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-activate',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-associatefocus',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-testkeydown',
        'https://learn.microsoft.com/en-us/windows/win32/api/textstor/ns-textstor-ts_status',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itftextinputprocessorex-activateex',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow',
        'https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcursorpos',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-msgwaitformultipleobjectsex',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow',
        'https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow',
        'https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Windows.cs',
        'https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setprocessdpiawarenesscontext',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclasslongptrw',
        'https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-timer',
        'https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-bitblt',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo',
        'https://devblogs.microsoft.com/oldnewthing/20161118-00/?p=94745'
    )
}
function Read-RegistrationValue([Microsoft.Win32.RegistryHive]$Hive, [Microsoft.Win32.RegistryView]$View, [string]$Path, [string]$Name) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, $View)
    $key = $null
    try {
        $key = $base.OpenSubKey($Path, $false)
        if (-not $key -or $key.GetValueKind($Name) -ne [Microsoft.Win32.RegistryValueKind]::String) {
            throw "登记值不存在或不是 REG_SZ：$Hive/$View/$Path。"
        }
        return [string]$key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    } finally { if ($key) { $key.Dispose() }; $base.Dispose() }
}
function Read-RegisteredDll([string]$Arch) {
    $view = if ($Arch -eq 'x64') { [Microsoft.Win32.RegistryView]::Registry64 } else { [Microsoft.Win32.RegistryView]::Registry32 }
    $relative = 'CLSID\{5638E6E5-D18C-4FE8-B778-E59A5170B481}\InprocServer32'
    $effective = Read-RegistrationValue ClassesRoot $view $relative ''
    $machine = Read-RegistrationValue LocalMachine $view ('SOFTWARE\Classes\' + $relative) ''
    if (-not [string]::Equals($effective, $machine, [StringComparison]::OrdinalIgnoreCase)) { throw '有效 COM 路径与机器登记不同，停止以免加载旧用户覆盖 DLL。' }
    $programFiles = Read-RegistrationValue LocalMachine Registry64 'SOFTWARE\Microsoft\Windows\CurrentVersion' 'ProgramFilesDir'
    $root = [IO.Path]::GetFullPath((Join-Path $programFiles 'TrueFluentPro\VoiceIme'))
    $pattern = '^' + [regex]::Escape($root + '\') + '(?<Package>[0-9a-f]{64})\\win-' + $Arch + '\\TrueFluentVoiceIme\.dll$'
    if ($effective -notmatch $pattern -or -not [string]::Equals($effective, [IO.Path]::GetFullPath($effective), [StringComparison]::OrdinalIgnoreCase)) {
        throw "登记路径必须是固定 Program Files\TrueFluentPro\VoiceIme\SHA256\win-$Arch\TrueFluentVoiceIme.dll。"
    }
    $package = $Matches.Package
    $file = Get-Item -LiteralPath $effective
    if ($file.PSIsContainer) { throw '登记 DLL 不是普通文件。' }
    for ($node = $file; $node -and $node.FullName.StartsWith($root, [StringComparison]::OrdinalIgnoreCase); $node = $node.Parent ?? $node.Directory) {
        if (($node.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '自有安装路径包含重解析点，停止加载。' }
    }
    return [pscustomobject]@{ Architecture = $Arch; RegisteredDll = $effective; Package = $package; RegisteredDllSha256 = (Get-FileHash -LiteralPath $effective -Algorithm SHA256).Hash }
}
# Child-only environment, compiler intermediates and logs stay in the repository.
function Invoke-Child([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [string]$LogBase, [int]$DeadlineMs) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal # No STARTF_USESHOWWINDOW override; keep console suppressed.
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8; $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $info.Environment['TEMP'] = $WorkingDirectory; $info.Environment['TMP'] = $WorkingDirectory
    if ($File -eq $env:ComSpec) {
        # cmd.exe uses its own quoting rules, not the C runtime ArgumentList escaping.
        $info.Arguments = '/d /s /c ""' + $Arguments[-1] + '""'
    } else {
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
    $timedOut = $false; $started = $false
    try {
        if (-not $process.Start()) { throw '无法启动验收子进程。' }
        $started = $true
        $childId = $process.Id
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($DeadlineMs)) {
            $timedOut = $true
            # Only this owned process. Ask its window to close/unwind before last-resort termination.
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
        }
        [IO.File]::WriteAllText($LogBase + '.stdout.log', $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText($LogBase + '.stderr.log', $stderr.GetAwaiter().GetResult())
        return [pscustomobject]@{ File = $File; Arguments = $Arguments; Pid = $childId; ExitCode = $process.ExitCode; TimedOut = $timedOut; HasExited = $process.HasExited; LogBase = $LogBase; CreateNoWindow = $info.CreateNoWindow; WindowStyle = $info.WindowStyle.ToString() }
    } finally {
        if ($started -and -not $process.HasExited) { $null = $process.CloseMainWindow(); if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() } }
        $process.Dispose()
    }
}
try {
    if ($ExpectedDll -and $Architecture.Count -ne 1) { throw 'ExpectedDll 对应单一架构；请显式选择 x64 或 x86。' }
    $Architecture = @($Architecture | Select-Object -Unique | Sort-Object { if ($_ -eq 'x64') { 0 } else { 1 } })
    foreach ($arch in $Architecture) {
        $registered = Read-RegisteredDll $arch
        $expectedHash = if ($ExpectedDll) { (Get-FileHash -LiteralPath $ExpectedDll -Algorithm SHA256).Hash } else { $registered.RegisteredDllSha256 }
        $summary.Preflight += [pscustomobject]@{
            Architecture = $arch; RegisteredDll = $registered.RegisteredDll; Package = $registered.Package
            RegisteredDllSha256 = $registered.RegisteredDllSha256; ExpectedDllSha256 = $expectedHash
            IdentityBasis = $(if ($ExpectedDll) { 'ExpectedDll digest' } else { 'fresh read-only machine/effective registration digest' })
        }
        Write-Output "REGISTERED win-$arch DLL=$($registered.RegisteredDll) SHA256=$($registered.RegisteredDllSha256)"
        if ($registered.RegisteredDllSha256 -ne $expectedHash) { throw '已登记 DLL 与 ExpectedDll 摘要不同，不激活、不允许旧产物通过。' }
    }
    $summary.SourceSha256 = (Get-FileHash -LiteralPath $summary.Source -Algorithm SHA256).Hash
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw '需要现有 Visual Studio C++ x64/x86 工具；本脚本不会安装。' }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
    foreach ($arch in $Architecture) {
        $dir = Join-Path $ArtifactDirectory "win-$arch"
        New-Item -ItemType Directory -Path $dir | Out-Null
        $exe = Join-Path $dir 'VoiceImeActivationTests.exe'
        $command = 'cl /nologo /std:c++17 /EHsc /W4 /utf-8 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0602 /MT /O2 "' + $summary.Source +
            '" /Fe:"' + $exe + '" /Fo:"' + (Join-Path $dir 'ActivationTests.obj') +
            '" /link /INCREMENTAL:NO /MANIFEST:EMBED /MANIFESTUAC:"level=''asInvoker'' uiAccess=''false''" ole32.lib oleaut32.lib uuid.lib advapi32.lib user32.lib gdi32.lib gdiplus.lib dwmapi.lib'
        $batch = Join-Path $dir 'compile.cmd'
        @('@echo off', '@chcp 65001 >nul', ('call "' + $vcvars + '" ' + $arch + ' >nul'), 'if errorlevel 1 exit /b %errorlevel%', $command, 'exit /b %errorlevel%') |
            Set-Content -LiteralPath $batch -Encoding utf8
        $build = Invoke-Child $env:ComSpec @('/d', '/s', '/c', $batch) $dir (Join-Path $dir 'build') 120000
        $summary.Builds += $build
        Write-Output "BUILD win-$arch exit=$($build.ExitCode) timeout=$($build.TimedOut)"
        if ($build.ExitCode -ne 0 -or $build.TimedOut) { throw "win-$arch 编译失败；停止，不重试。" }
    }
    foreach ($arch in $Architecture) {
        $dir = Join-Path $ArtifactDirectory "win-$arch"; $exe = Join-Path $dir 'VoiceImeActivationTests.exe'
        $preflight = $summary.Preflight | Where-Object Architecture -eq $arch
        $before = Read-RegisteredDll $arch
        if ($before.RegisteredDll -ne $preflight.RegisteredDll -or $before.RegisteredDllSha256 -ne $preflight.RegisteredDllSha256 -or
            (Get-FileHash -LiteralPath $summary.Source -Algorithm SHA256).Hash -ne $summary.SourceSha256) {
            throw '编译期间登记 DLL 或测试源码已改变，停止，不运行旧测试产物。'
        }
        $reportPath = Join-Path $dir 'activation.json'
        $run = Invoke-Child $exe @($reportPath, $preflight.RegisteredDll) $dir (Join-Path $dir 'activation') 30000
        $native = if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json } else { $null }
        $loadedHash = $null
        # Do not hash arbitrary report paths: first require the exact preflight-owned registration.
        if ($native -and $native.LoadedModule -eq $preflight.RegisteredDll) { $loadedHash = (Get-FileHash -LiteralPath $native.LoadedModule -Algorithm SHA256).Hash }
        $after = Read-RegisteredDll $arch
        $identity = $native -and $native.RegisteredDll -eq $preflight.RegisteredDll -and $native.ToolbarClassModule -eq $native.LoadedModule -and
            $loadedHash -eq $preflight.ExpectedDllSha256 -and $loadedHash -eq $preflight.RegisteredDllSha256 -and
            $after.RegisteredDll -eq $preflight.RegisteredDll -and $after.RegisteredDllSha256 -eq $loadedHash
        $required = @('ProcessUsesPMV2', 'OwnWindowTopmostAtHitTest', 'OwnClientReadyForMouseActivation', 'ClickTargetVerifiedOwnClient', 'MouseClickReverifiedOwnRootAndPid', 'OwnWindowReceivedNormalClick', 'OwnWindowTopmostRemoved',
            'SystemLoadedRegisteredDll', 'Production500msTimerDispatched', 'OwnMainAndTwoHintsExist', 'OwnTooltipAndNoticeIdentified',
            'ToolbarClassModuleIsRegisteredDll', 'ToolbarNoActivateLayeredToolWindow', 'ProductionRetainsHostPMV2', 'ToolbarSurface104x44Dip',
            'IdleMainVisibleHintsHidden', 'HoverTooltipVisibleWithStartText', 'HoverMainVisibleNoticeHidden', 'EditorFocusedDuringHover',
            'CaptureOnlyWithinOwnEditor', 'OwnScreenshotAreaFullyExposed', 'OwnMainScreenshotSaved', 'ObservedBody96x36Dip',
            'EditorFocusedAfterScreenshot', 'HoverClearedMainStillVisible', 'A_Passthrough', 'F8_SelectedEditable',
            'F8_SelectedReadOnlyNotEaten', 'OriginalProcessProfileRestored', 'OriginalForegroundAndFocusRestored', 'OriginalCursorRestored', 'OwnWindowDestroyed')
        $missing = @($required | Where-Object { -not $native -or $native.$_ -ne $true })
        $screenshot = Join-Path $dir 'OWNMAIN.png'
        $screenshotValid = $native -and $native.OwnMainScreenshot -eq $screenshot -and (Test-Path -LiteralPath $screenshot -PathType Leaf)
        $summary.Runs += [pscustomobject]@{
            Architecture = $arch; Process = $run; ReportPath = $reportPath; Result = $native; LoadedModuleSha256 = $loadedHash
            ExpectedDllSha256 = $preflight.ExpectedDllSha256; RegisteredDllUnchanged = ($after.RegisteredDllSha256 -eq $preflight.RegisteredDllSha256)
            LoadedModuleIdentityVerified = [bool]$identity; MissingAssertions = $missing
            ScreenshotPath = $(if ($screenshotValid) { $screenshot } else { $null })
            VisualReview = '请目视这一张实际 OWNMAIN.png 的品牌、麦克风和中文 tooltip 字体；程序断言不冒充目视验收。'
            ExecutableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        }
        Write-Output "RUN win-$arch pid=$($run.Pid) exit=$($run.ExitCode) exited=$($run.HasExited) report=$reportPath"
        Write-Output "UI timer=$($native.Production500msTimerDispatched) idle=$($native.IdleMainVisibleHintsHidden) hover=$($native.HoverTooltipVisibleWithStartText) focus=$($native.EditorFocusedDuringHover) identity=$identity screenshot=$screenshot"
        if ($run.ExitCode -ne 0 -or $run.TimedOut -or -not $native -or -not $native.Passed -or -not $native.CleanupCompleted -or
            -not $identity -or $missing.Count -or -not $screenshotValid -or $native.Pid -ne $run.Pid -or
            $native.Bitness -ne $(if ($arch -eq 'x64') { 64 } else { 32 })) {
            Write-Output ('UNMET=' + ($missing -join ', '))
            throw "win-$arch 验收失败；先核对报告和官方机制，后续架构不再运行。"
        }
    }
    $summary.ExitCode = 0
} catch {
    $summary.Error = $_.Exception.Message
    Write-Output ("STOP: " + $_.Exception.Message)
} finally {
    $summary.CompletedAt = [DateTimeOffset]::Now.ToString('o')
    $summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'result.json') -Encoding utf8
    Write-Output ('REPORT=' + (Join-Path $ArtifactDirectory 'result.json'))
}
exit $summary.ExitCode