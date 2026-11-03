#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [ValidateSet('x64', 'x86')][string[]]$Architecture = @('x64'),
    [string]$ExpectedDll,
    [switch]$BuildOnly,
    [switch]$SwitchOnly
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
    Scope = 'Windows真实激活PF client TIP；共享x64 owner独立STA的唯一主条/两hint、Idle/hover、不抢前台、空Context保留同HWND并拒绝F8、返回正常Doc恢复资格；FORPROCESS选择已启用其他IME，观察owner实际隐藏，再Voice恢复Idle同HWND。只按实际窗口矩形安全裁剪，遮挡或外部背景时skip，不冒充目视通过。BuildOnly仅编译测试，不读登记、不激活、不运行GUI。'
    Constraints = '只编译ActivationTests.cpp及其同源x64只读owner查询模式；PF client模块与HostExe父目录voice-ime/win-x64 owner模块分别验摘要，x86不截断远程HMODULE。窗口/编辑只在自有host与TSF FORPROCESS；现有光标下自有无caption client中一次2 INPUT，无光标移动/其他应用点击/浮窗拖动；finally还原原profile、关联、光标和前台、关闭自有窗口。生产Native worker可以新建idle owner，T前不读config/云/音频；I状态帧不是录音。owner正常独立常驻，绝不kill TrueFluentPro --shared、不注入Close远程指针/取消/gate。无production修改、注册/启用写入、默认/session改变、UAC、真实配置、云或音频调用。'
    Source = Join-Path $PSScriptRoot 'ActivationTests.cpp'; SourceSha256 = $null
    ScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    BuildOnly = [bool]$BuildOnly; FunctionalTestsExecuted = $false; FunctionalAssessment = '未实测，待主agent统一生产部署后运行真实激活/GUI验收。'
    SwitchOnly = [bool]$SwitchOnly
    ExpectedDll = $ExpectedDll; Owner = $null; Preflight = @(); Builds = @(); Runs = @(); ExitCode = 1
    Sources = @(
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-activateprofile',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-getforeground',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-activate',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-createdocumentmgr',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-associatefocus',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-setfocus',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-getfocus',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfdocumentmgr-gettop',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfthreadmgr-isthreadfocus',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfkeystrokemgr-testkeydown',
        'https://learn.microsoft.com/en-us/windows/win32/api/textstor/ns-textstor-ts_status',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itftextinputprocessorex-activateex',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow',
        'https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfrompoint',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcursorpos',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clienttoscreen',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-screentoclient',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipcursor',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcapture',
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
        'https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getmodulefilenameexw',
        'https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofilemgr-enumprofiles',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/nf-msctf-itfinputprocessorprofiles-getlanguageprofiledescription',
        'https://learn.microsoft.com/en-us/windows/win32/api/msctf/ns-msctf-tf_inputprocessorprofile',
        'https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-timer',
        'https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-bitblt',
        'https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo',
        'https://devblogs.microsoft.com/oldnewthing/20161118-00/?p=94745'
    )
}
if ($SwitchOnly) {
    $summary.Scope = 'SwitchOnly：Windows真实FORPROCESS激活client，鉴别共享owner及Idle主浮窗；优先选已启用搜狗，验证真实ActiveProfile、前台服务非Voice、同owner HWND隐藏，再Voice重选同HWND可见/F8资格恢复。跳过hover、截图、NoDocument、Readonly，不发F8Down、不录音。BuildOnly仅编译测试。'
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
    for ($node = $file; $node -and $node.FullName.StartsWith($root, [StringComparison]::OrdinalIgnoreCase);) {
        if (($node.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '自有安装路径包含重解析点，停止加载。' }
        $node = if ($node -is [IO.FileInfo]) { $node.Directory } else { $node.Parent }
    }
    return [pscustomobject]@{ Architecture = $Arch; RegisteredDll = $effective; Package = $package; RegisteredDllSha256 = (Get-FileHash -LiteralPath $effective -Algorithm SHA256).Hash }
}
function Read-RegisteredOwner {
    # Host registration metadata only, never config.json/owner.lock/credentials or a process launch.
    $hostExe = Read-RegistrationValue CurrentUser Registry64 'Software\TrueFluentPro\VoiceIme' 'HostExe'
    $ownerTag = Read-RegistrationValue CurrentUser Registry64 'Software\TrueFluentPro\VoiceIme' 'VoiceImeOwner'
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $sub = $base.OpenSubKey('Software\TrueFluentPro\VoiceImeUI', $false)
        if ($sub) {
            $standalone = $sub.GetValue('StandaloneHostExe')
            if ($standalone -is [string] -and -not [string]::IsNullOrWhiteSpace($standalone) -and (Test-Path -LiteralPath $standalone -PathType Leaf)) {
                $hostExe = $standalone
            }
            $sub.Dispose()
        }
    } finally { $base.Dispose() }
    if ($ownerTag -ne 'TrueFluentPro.VoiceIme.v1' -or $hostExe -notmatch '^[A-Za-z]:\\' -or
        $hostExe -ne [IO.Path]::GetFullPath($hostExe) -or ([IO.Path]::GetFileName($hostExe) -notin @('TrueFluentPro.exe', 'TrueFluentVoiceImeHost.exe'))) {
        throw '共享owner登记必须是当前自有TrueFluentPro.exe或TrueFluentVoiceImeHost.exe的本地绝对路径。'
    }
    $root = [IO.Path]::GetDirectoryName($hostExe)
    $module = Join-Path $root 'voice-ime\win-x64\TrueFluentVoiceIme.dll'
    foreach ($path in @($hostExe, $module)) {
        $file = Get-Item -LiteralPath $path
        if ($file.PSIsContainer) { throw '共享owner EXE/DLL必须是普通文件。' }
        for ($node = $file; $node;) {
            if (($node.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '共享owner路径包含重解析点，停止加载。' }
            $node = if ($node -is [IO.FileInfo]) { $node.Directory } else { $node.Parent }
        }
    }
    $installed = Read-RegisteredDll x64 # Always x64, even with a single x86 PF client ExpectedDll.
    $hash = (Get-FileHash -LiteralPath $module -Algorithm SHA256).Hash
    if ($hash -ne $installed.RegisteredDllSha256) { throw 'Host运行目录x64 owner DLL与当前已安装PF x64 DLL摘要不一致，不能验收旧owner UI。' }
    return [pscustomobject]@{
        HostExe = $hostExe; HostExeSha256 = (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash
        ExpectedOwnerModule = $module; OwnerDllSha256 = $hash
        InstalledX64Dll = $installed.RegisteredDll; InstalledX64DllSha256 = $installed.RegisteredDllSha256
        IdentityBasis = '只读HKCU HostExe元数据；HostExe父目录实际RootRuntime x64 DLL与fresh PF installed x64摘要一致，非perServiceDLL路径'
    }
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
    if (-not $BuildOnly) { foreach ($arch in $Architecture) {
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
        $summary.Owner = Read-RegisteredOwner
        Write-Output "OWNER EXE=$($summary.Owner.HostExe) DLL=$($summary.Owner.ExpectedOwnerModule) SHA256=$($summary.Owner.OwnerDllSha256)"
    }
    $summary.SourceSha256 = (Get-FileHash -LiteralPath $summary.Source -Algorithm SHA256).Hash
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw '需要现有 Visual Studio C++ x64/x86 工具；本脚本不会安装。' }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
    $buildArchitectures = @(@('x64') + $Architecture | Select-Object -Unique)
    foreach ($arch in $buildArchitectures) {
        $dir = Join-Path $ArtifactDirectory "win-$arch"
        New-Item -ItemType Directory -Path $dir | Out-Null
        $exe = Join-Path $dir 'VoiceImeActivationTests.exe'
        $command = 'cl /nologo /std:c++17 /EHsc /W4 /utf-8 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0602 /DPSAPI_VERSION=1 /MT /O2 "' + $summary.Source +
            '" /Fe:"' + $exe + '" /Fo:"' + (Join-Path $dir 'ActivationTests.obj') +
            '" /link /STACK:4194304 /INCREMENTAL:NO /MANIFEST:EMBED /MANIFESTUAC:"level=''asInvoker'' uiAccess=''false''" ole32.lib oleaut32.lib uuid.lib advapi32.lib user32.lib gdi32.lib gdiplus.lib dwmapi.lib psapi.lib'
        $batch = Join-Path $dir 'compile.cmd'
        @('@echo off', '@chcp 65001 >nul', ('call "' + $vcvars + '" ' + $arch + ' >nul'), 'if errorlevel 1 exit /b %errorlevel%', $command, 'exit /b %errorlevel%') |
            Set-Content -LiteralPath $batch -Encoding utf8
        $build = Invoke-Child $env:ComSpec @('/d', '/s', '/c', $batch) $dir (Join-Path $dir 'build') 120000
        $build | Add-Member NoteProperty Architecture $arch
        $build | Add-Member NoteProperty Role $(if ($arch -in $Architecture) { 'ActivationClientAndReadOnlyObserver' } else { 'ReadOnlyX64OwnerObserverOnly' })
        $build | Add-Member NoteProperty ExecutableSha256 $(if (Test-Path -LiteralPath $exe -PathType Leaf) { (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash } else { $null })
        $summary.Builds += $build
        Write-Output "BUILD win-$arch exit=$($build.ExitCode) timeout=$($build.TimedOut)"
        if ($build.ExitCode -ne 0 -or $build.TimedOut) { throw "win-$arch 编译失败；停止，不重试。" }
    }
    if (-not $BuildOnly) { foreach ($arch in $Architecture) {
        $dir = Join-Path $ArtifactDirectory "win-$arch"; $exe = Join-Path $dir 'VoiceImeActivationTests.exe'
        $preflight = $summary.Preflight | Where-Object Architecture -eq $arch
        $before = Read-RegisteredDll $arch
        $ownerBefore = Read-RegisteredOwner
        if ($before.RegisteredDll -ne $preflight.RegisteredDll -or $before.RegisteredDllSha256 -ne $preflight.RegisteredDllSha256 -or
            $ownerBefore.HostExe -ne $summary.Owner.HostExe -or $ownerBefore.HostExeSha256 -ne $summary.Owner.HostExeSha256 -or
            $ownerBefore.ExpectedOwnerModule -ne $summary.Owner.ExpectedOwnerModule -or $ownerBefore.OwnerDllSha256 -ne $summary.Owner.OwnerDllSha256 -or
            (Get-FileHash -LiteralPath $summary.Source -Algorithm SHA256).Hash -ne $summary.SourceSha256) {
            throw '编译期间登记 DLL 或测试源码已改变，停止，不运行旧测试产物。'
        }
        $reportPath = Join-Path $dir 'activation.json'
        $queryExe = Join-Path $ArtifactDirectory 'win-x64\VoiceImeActivationTests.exe'
        $summary.FunctionalTestsExecuted = $true
        $activationArguments = @($reportPath, $preflight.RegisteredDll, $summary.Owner.HostExe,
            $summary.Owner.ExpectedOwnerModule, $queryExe)
        if ($SwitchOnly) { $activationArguments += '--switch-only' } # Last only; x86 still gets the same-source x64 query EXE at argv[5].
        $run = Invoke-Child $exe $activationArguments $dir (Join-Path $dir 'activation') 30000
        $native = if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json } else { $null }
        $loadedHash = $null
        # Do not hash arbitrary report paths: first require the exact preflight-owned registration.
        if ($native -and $native.LoadedModule -eq $preflight.RegisteredDll) { $loadedHash = (Get-FileHash -LiteralPath $native.LoadedModule -Algorithm SHA256).Hash }
        $after = Read-RegisteredDll $arch
        $ownerAfter = Read-RegisteredOwner
        $ownerHash = $null
        if ($native -and $native.ActualOwnerModule -eq $summary.Owner.ExpectedOwnerModule) {
            $ownerHash = (Get-FileHash -LiteralPath $summary.Owner.ExpectedOwnerModule -Algorithm SHA256).Hash
        }
        $identity = $native -and $native.RegisteredDll -eq $preflight.RegisteredDll -and
            $loadedHash -eq $preflight.ExpectedDllSha256 -and $loadedHash -eq $preflight.RegisteredDllSha256 -and
            $after.RegisteredDll -eq $preflight.RegisteredDll -and $after.RegisteredDllSha256 -eq $loadedHash
        $ownerIdentity = $native -and $native.OwnerPid -gt 0 -and $native.OwnerPid -ne $run.Pid -and $native.OwnerThread -gt 0 -and
            $native.OwnerProcessCreated -gt 0 -and $native.OwnerClassHmodule -gt 0 -and $native.OwnerQueryError -eq 0 -and
            $native.RegisteredOwnerExe -eq $summary.Owner.HostExe -and $native.ActualOwnerExe -eq $summary.Owner.HostExe -and
            $native.ExpectedOwnerModule -eq $summary.Owner.ExpectedOwnerModule -and
            $native.ToolbarClassModule -eq $summary.Owner.ExpectedOwnerModule -and
            ($SwitchOnly -or $native.NoDocumentToolbarClassModule -eq $summary.Owner.ExpectedOwnerModule) -and
            $ownerHash -eq $summary.Owner.InstalledX64DllSha256 -and $ownerHash -eq $summary.Owner.OwnerDllSha256 -and
            $ownerAfter.HostExe -eq $summary.Owner.HostExe -and $ownerAfter.HostExeSha256 -eq $summary.Owner.HostExeSha256 -and
            $ownerAfter.ExpectedOwnerModule -eq $summary.Owner.ExpectedOwnerModule -and $ownerAfter.OwnerDllSha256 -eq $ownerHash -and
            $ownerAfter.InstalledX64Dll -eq $summary.Owner.InstalledX64Dll -and
            $native.OwnerMainHwnd -gt 0 -and $native.OwnerTipHwnd -gt 0 -and $native.OwnerNoticeHwnd -gt 0 -and
            $native.InitialMainHwnd -eq $native.OwnerMainHwnd -and ($SwitchOnly -or $native.NoDocumentMainHwnd -eq $native.OwnerMainHwnd) -and
            $native.VoiceReselectedMainHwnd -eq $native.OwnerMainHwnd -and $native.OwnerTerminationAttempted -eq $false
        $required = if ($SwitchOnly) {
            @('SwitchOnly', 'SystemForegroundServiceIsVoice', 'SelectedVoice0804', 'SystemLoadedRegisteredDll', 'RegistrationMatchesPreflight',
                'OwnerRegistrationMatchesPreflight', 'RealFocusBeforeActivation', 'RealFocusAfterActivation', 'OwnWindowObtainedForeground',
                'MouseSequenceInserted', 'OwnWindowReceivedNormalClick', 'OwnWindowTopmostRemoved', 'Production500msTimerDispatched',
                'SharedOwnerShownByNativeSelection', 'SharedOwnerMainAndTwoHintsExist', 'SharedOwnerIndependentProcessAndUiThread',
                'InitialOwnerStable', 'InitialVisibility', 'InitialIdle', 'EditorFocusedAtIdleUi',
                'RealEditLockSelectionRoundTrip', 'A_Passthrough', 'F8_SelectedEditable',
                'EditorFocusedBeforeOtherIme', 'BeforeOtherImeOwnerStable', 'BeforeOtherImeVisibility', 'BeforeOtherImeIdle',
                'OtherImeEnabledEnumerationComplete', 'OtherImeEnabledNonVoiceAvailable', 'OtherImeActuallySelected', 'OtherImeForegroundNotVoice',
                'EditorFocusedDuringOtherIme', 'OtherImeOwnerActuallyHidden', 'OtherImeOwnerStable', 'OtherImeVisibility', 'OtherImeIdle',
                'VoiceReselectIdleSameHwnd', 'VoiceReselectedActuallySelected', 'VoiceReselectedOwnerStable', 'VoiceReselectedVisibility', 'VoiceReselectedIdle', 'F8_AfterOtherImeRestore',
                'NoTextInjected', 'TextUnchangedAtCleanup', 'OriginalProcessProfileRestored', 'FocusAssociationRestored', 'ContextPopped', 'ThreadDeactivated',
                'OriginalForegroundAndFocusRestored', 'OriginalCursorRestored', 'OwnWindowDestroyed', 'OwnerRetainedAfterClientCleanup', 'CleanupCompleted')
        } else {
            @('ProcessUsesPMV2', 'OwnWindowTopmostAtHitTest', 'OwnClientExposedAfterPump', 'OwnClientReadyForMouseActivation', 'ClickInsideVirtualDesktop', 'ClickNotConfinedElsewhere',
            'NoCapturedMouseOrActiveMenu', 'NoHeldMouseButtonsOrModifiers', 'ClickTargetVerifiedOwnClient', 'MouseClickReverifiedOwnRootAndPid', 'MouseSequenceInserted', 'OwnWindowReceivedNormalClick', 'OwnWindowTopmostRemoved',
            'SystemLoadedRegisteredDll', 'Production500msTimerDispatched', 'OwnerRegistrationMatchesPreflight', 'SharedOwnerShownByNativeSelection',
            'SharedOwnerMainAndTwoHintsExist', 'SharedOwnerIndependentProcessAndUiThread', 'InitialOwnerStable', 'InitialVisibility', 'InitialIdle',
            'ToolbarNoActivateLayeredToolWindow', 'ToolbarSurface104x44Dip', 'HoverTooltipVisibleWithStartText', 'HoverOwnerStable', 'HoverVisibility', 'EditorFocusedDuringHover',
            'EditorFocusedAfterScreenshot', 'HoverClearedOwnerStable', 'HoverClearedVisibility', 'HoverClearedIdle', 'A_Passthrough', 'F8_SelectedEditable',
            'EditorFocusedBeforeNoDocument', 'NoDocumentBaselineOwnerStable', 'NoDocumentBaselineVisibility', 'NoDocumentBaselineIdle',
            'NoDocumentBaselineThreadFocus', 'NoDocumentEmptyDocumentCreated', 'NoDocumentPreviousWindowAssociationIsOriginalDocument',
            'NoDocumentBeforeTimerContextNull', 'NoDocumentBeforeTimerEmptyDocumentFocused', 'NoDocumentBeforeTimerThreadFocusUnchanged', 'NoDocumentBeforeTimerOwnForegroundAndFocus', 'NoDocumentBeforeTimerSelectedVoice',
            'NoDocumentAfterTimerContextNull', 'NoDocumentAfterTimerEmptyDocumentFocused', 'NoDocumentAfterTimerThreadFocusUnchanged', 'NoDocumentAfterTimerOwnForegroundAndFocus', 'NoDocumentAfterTimerSelectedVoice',
            'NoDocumentProduction500msTimerDispatched', 'NoDocumentClientModuleIsRegisteredDll', 'NoDocumentOwnerStable', 'NoDocumentVisibility', 'NoDocumentIdle', 'NoDocumentToolbarSurface104x44Dip',
            'F8_SelectedNoDocumentNotEaten', 'NoDocumentAfterF8ContextNull', 'NoDocumentAfterF8EmptyDocumentFocused', 'NoDocumentAfterF8ThreadFocusUnchanged', 'NoDocumentAfterF8OwnForegroundAndFocus', 'NoDocumentAfterF8SelectedVoice',
            'NoDocumentF8KeyDownNotDispatched', 'NoDocumentNoAudioRequested', 'NoDocumentAfterKeyOwnerStable', 'NoDocumentAfterKeyVisibility', 'NoDocumentAfterKeyIdle',
            'EditorFocusedAfterNoDocumentRestore', 'F8_AfterNoDocumentRestore', 'NoDocumentRestoredOwnerStable', 'NoDocumentRestoredVisibility', 'NoDocumentRestoredIdle',
            'NoDocumentWindowAssociationRestored', 'NoDocumentDocumentFocusRestored',
            'F8_SelectedReadOnlyNotEaten', 'OtherImeEnabledEnumerationComplete', 'OtherImeEnabledNonVoiceAvailable', 'OtherImeActuallySelected',
            'EditorFocusedDuringOtherIme', 'OtherImeOwnerActuallyHidden', 'OtherImeOwnerStable', 'OtherImeVisibility', 'OtherImeIdle',
            'VoiceReselectIdleSameHwnd', 'VoiceReselectedActuallySelected', 'VoiceReselectedOwnerStable', 'VoiceReselectedVisibility', 'VoiceReselectedIdle', 'F8_AfterOtherImeRestore',
            'OriginalProcessProfileRestored', 'OriginalForegroundAndFocusRestored', 'OriginalCursorRestored', 'OwnWindowDestroyed', 'OwnerRetainedAfterClientCleanup')
        }
        $missing = @($required | Where-Object { -not $native -or $native.$_ -ne $true })
        $switchEvidence = $null
        if ($SwitchOnly) {
            $switchEvidence = $native -and $native.SwitchOnly -eq $true -and
                $native.OtherImeActivateFlags -eq $native.ActivateFlags -and $native.VoiceReselectFlags -eq $native.ActivateFlags -and
                $native.OtherImeActiveProfileHr -eq '0x00000000' -and
                $native.OtherImeForegroundHr -in @('0x00000000', '0x00000001') -and
                $native.OtherImeForegroundClsid -and $native.OtherImeForegroundClsid -ne $native.ForegroundClsid -and
                $native.BeforeOtherImeMainHwnd -eq $native.OwnerMainHwnd -and $native.OtherImeMainHwnd -eq $native.OwnerMainHwnd -and
                $native.VoiceReselectedMainHwnd -eq $native.OwnerMainHwnd -and $native.F8_AfterOtherImeRestoreEaten -eq $true -and
                $native.F8KeyDownDispatched -eq $false -and $native.SyntheticPartialFinalInjected -eq $false -and
                $native.ServiceIdleFrames -ge 1 -and $native.ServiceNonIdleFrames -eq 0 -and $native.TextStoreWrites -eq 0 -and
                $native.NoDocumentSkipped -eq $true -and $native.ReadOnlySkipped -eq $true -and
                -not [string]::IsNullOrWhiteSpace($native.OtherImeName) -and
                $native.OtherImeIsSogou -eq [bool]($native.OtherImeName -match '搜狗|Sogou')
        }
        $noDocumentEvidence = $null
        if (-not $SwitchOnly) {
        # NoDocument is a compatibility prefix for no textContext, never document=null.
        $noDocumentStages = @('NoDocumentBeforeTimer', 'NoDocumentAfterTimer', 'NoDocumentAfterF8')
        $invalidNoDocumentContexts = @($noDocumentStages | Where-Object {
            -not $native -or $native."$($_)GetFocusHr" -ne '0x00000000' -or $native."$($_)Document" -le 0 -or
            $native."$($_)EmptyDocumentFocused" -ne $true -or
            $native."$($_)GetTopHr" -notin @('0x00000000', '0x00000001') -or
            $native."$($_)Context" -ne 0 -or $native."$($_)ContextNull" -ne $true -or
            $native."$($_)IsThreadFocusHr" -ne '0x00000000' -or $native."$($_)ThreadFocus" -ne $true -or
            $native."$($_)ThreadFocus" -ne $native.NoDocumentBaselineThreadFocus
        })
        $noDocumentEvidence = $native -and $native.NoDocumentEmptyDocument -gt 0 -and
            $native.NoDocumentPreviousWindowDocument -gt 0 -and $native.NoDocumentPreviousWindowAssociationIsOriginalDocument -eq $true -and
            $native.NoDocumentBaselineThreadFocus -eq $true -and $invalidNoDocumentContexts.Count -eq 0 -and
            $native.NoDocumentCreateEmptyDocumentHr -eq '0x00000000' -and $native.NoDocumentAssociateEmptyDocumentHr -eq '0x00000000' -and
            $native.NoDocumentSetFocusHr -eq '0x00000000' -and
            $native.NoDocumentWindowAssociationRestored -eq $true -and $native.NoDocumentDocumentFocusRestored -eq $true -and
            $native.F8_SelectedNoDocumentNotEatenHr -in @('0x00000000', '0x00000001') -and
            $native.F8_SelectedNoDocumentNotEatenEaten -eq $false -and $native.F8KeyDownDispatched -eq $false -and
            $native.NoDocumentNonIdleFrames -eq 0 -and $native.NoDocumentTextStoreWrites -eq 0 -and
            $native.TextStoreWrites -eq 0 -and $native.SyntheticPartialFinalInjected -eq $false -and
            $native.ServiceIdleFrames -ge 1 -and $native.ServiceNonIdleFrames -eq 0 -and $native.F8_AfterNoDocumentRestoreEaten -eq $true -and
            $native.NoDocumentService500msTimerUpdates -ge 1 -and $native.NoDocumentServiceTimerElapsedMs -ge 500
        }
        $mouseIdentity = $native -and $native.MouseMoveInserted -eq $false -and
            $native.MouseMoveSendInputRequested -eq 0 -and $native.MouseMoveSendInputSent -eq 0 -and
            $native.MouseSendInputRequested -eq 2 -and $native.MouseSendInputSent -eq 2
        $screenshot = Join-Path $dir 'OWNMAIN.png'
        $tipScreenshot = Join-Path $dir 'OWNTIP.png'
        $screenshotValid = $native -and $(
            if ($SwitchOnly) {
                $native.CaptureStatus -eq 'Skipped' -and $native.CaptureSkipReason -like 'SwitchOnly*'
            } elseif ($native.CaptureStatus -eq 'Captured') {
                $native.ScreenshotAreaFullyExposed -eq $true -and $native.ScreenshotsSaved -eq $true -and $native.ObservedBody96x36Dip -eq $true -and
                $native.OwnMainScreenshot -eq $screenshot -and (Test-Path -LiteralPath $screenshot -PathType Leaf) -and
                $native.OwnTipScreenshot -eq $tipScreenshot -and (Test-Path -LiteralPath $tipScreenshot -PathType Leaf)
            } else { $native.CaptureStatus -eq 'Skipped' -and -not [string]::IsNullOrWhiteSpace($native.CaptureSkipReason) }
        )
        $idlePixelsValid = $null
        if (-not $SwitchOnly) { $idlePixelsValid = $native -and $(
            if ($native.NoDocumentCaptureStatus -eq 'Captured') {
                $native.NoDocumentScreenshotAreaFullyExposed -eq $true -and $native.NoDocumentObservedBody96x36Dip -eq $true -and $native.NoDocumentObservedIdleBrandMic -eq $true
            } else { $native.NoDocumentCaptureStatus -eq 'Skipped' -and -not [string]::IsNullOrWhiteSpace($native.NoDocumentCaptureSkipReason) }
        ) }
        $summary.Runs += [pscustomobject]@{
            Architecture = $arch; Process = $run; ReportPath = $reportPath; Result = $native; LoadedModuleSha256 = $loadedHash
            ExpectedDllSha256 = $preflight.ExpectedDllSha256; RegisteredDllUnchanged = ($after.RegisteredDllSha256 -eq $preflight.RegisteredDllSha256)
            LoadedModuleIdentityVerified = [bool]$identity; MissingAssertions = $missing
            ActualOwnerModuleSha256 = $ownerHash; InstalledX64DllSha256 = $summary.Owner.InstalledX64DllSha256
            SharedOwnerIdentityVerified = [bool]$ownerIdentity; OwnerAfter = $ownerAfter
            SwitchOnly = [bool]$SwitchOnly; SwitchEvidenceVerified = $switchEvidence
            OtherImeName = $native.OtherImeName; OtherImeIsSogou = $native.OtherImeIsSogou; OtherImeCoverage = $native.OtherImeCoverage
            NoDocumentEvidenceVerified = $noDocumentEvidence
            ClickOnlyMouseInputVerified = [bool]$mouseIdentity
            ScreenshotPath = $(if ($native.CaptureStatus -eq 'Captured' -and $screenshotValid) { $screenshot } else { $null })
            TipScreenshotPath = $(if ($native.CaptureStatus -eq 'Captured' -and $screenshotValid) { $tipScreenshot } else { $null })
            CaptureStatus = $native.CaptureStatus; CaptureSkipReason = $native.CaptureSkipReason
            NoDocumentCaptureStatus = $native.NoDocumentCaptureStatus; NoDocumentCaptureSkipReason = $native.NoDocumentCaptureSkipReason
            VisualReview = $(if ($SwitchOnly) { 'SwitchOnly 按范围不截图，不要求强制目视UI；用户已提供截图。仅以真实Profile/前台服务/owner HWND动作及身份/清理验收。' } else { '未目视验收；Captured仅说明真实窗口裁剪已保存；Skipped说明曝光不安全，未采集外部文本。主agent后续审核安全图片或另行真实GUI验收。' })
            ExecutableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        }
        Write-Output "RUN win-$arch pid=$($run.Pid) exit=$($run.ExitCode) exited=$($run.HasExited) report=$reportPath"
        if ($SwitchOnly) {
            Write-Output "SWITCH-ONLY ownerPid=$($native.OwnerPid) ownerIdentity=$ownerIdentity clientIdentity=$identity idle=$($native.InitialIdle) evidence=$switchEvidence otherIme=$($native.OtherImeName) sogou=$($native.OtherImeIsSogou) hidden=$($native.OtherImeOwnerActuallyHidden) foregroundNotVoice=$($native.OtherImeForegroundNotVoice) restoredF8=$($native.F8_AfterOtherImeRestoreEaten) capture=$($native.CaptureStatus)"
            Write-Output $native.OtherImeCoverage
        } else {
        Write-Output "UI ownerPid=$($native.OwnerPid) ownerIdentity=$ownerIdentity timer=$($native.Production500msTimerDispatched) idle=$($native.InitialIdle) hover=$($native.HoverTooltipVisibleWithStartText) focus=$($native.EditorFocusedDuringHover) clientIdentity=$identity capture=$($native.CaptureStatus)"
        Write-Output "NO-TEXT-CONTEXT evidence=$noDocumentEvidence emptyDoc=$($native.NoDocumentEmptyDocument) context=$($native.NoDocumentAfterF8Context) sameHwnd=$($native.NoDocumentOwnerStable) capture=$($native.NoDocumentCaptureStatus) F8=$($native.F8_SelectedNoDocumentNotEatenEaten) restoredF8=$($native.F8_AfterNoDocumentRestoreEaten)"
        }
        if ($run.ExitCode -ne 0 -or $run.TimedOut -or -not $native -or -not $native.Passed -or -not $native.CleanupCompleted -or
            -not $identity -or -not $ownerIdentity -or -not $mouseIdentity -or $missing.Count -or
            ($SwitchOnly -and -not $switchEvidence) -or (-not $SwitchOnly -and (-not $noDocumentEvidence -or -not $idlePixelsValid)) -or
            -not $screenshotValid -or $native.Pid -ne $run.Pid -or
            $native.Bitness -ne $(if ($arch -eq 'x64') { 64 } else { 32 })) {
            Write-Output ('UNMET=' + ($missing -join ', '))
            throw "win-$arch 验收失败；先核对报告和官方机制，后续架构不再运行。"
        }
    }
        $summary.FunctionalAssessment = if ($SwitchOnly) {
            'SwitchOnly 真实激活/共享owner Idle/其他IME前台服务让出及同HWND隐藏恢复/清理断言通过；搜狗是否实测以每架构 OtherImeName/OtherImeIsSogou 为准。未进行hover、截图、NoDocument、Readonly或录音。'
        } else { '真实激活/共享owner/空Context/其他IME隐藏恢复的程序断言通过；截图与目视状态单独列出，不代表真实录音通过。' }
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