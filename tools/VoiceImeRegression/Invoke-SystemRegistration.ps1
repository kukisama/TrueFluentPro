[CmdletBinding()]
param(
    [switch]$InstallSystem,
    [switch]$UninstallSystem,
    [switch]$CleanupPartial,
    [switch]$BuildRelease
)

# 显式管理员验收。默认仅查询；不启动应用入口、麦克风或识别后台。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($InstallSystem -and $UninstallSystem) { throw 'InstallSystem and UninstallSystem are separate lifecycle operations.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$release = Join-Path $repo 'bin/Release/net10.0'
$manifestPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TrueFluentPro/VoiceIme/install-manifest.json'
$installRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'TrueFluentPro/VoiceIme'
$clsid = '{5638E6E5-D18C-4FE8-B778-E59A5170B481}'
$profileGuid = '{A91272E1-9973-43F9-8EB4-7D5494EC92B1}'
$owner = 'TrueFluentPro.VoiceIme.v1'
$architectures = @('win-x64', 'win-x86')
$artifacts = Join-Path $PSScriptRoot ('artifacts/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-system-registration-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$log = [IO.StreamWriter]::new((Join-Path $artifacts 'run.log'), $false, $utf8)
$log.AutoFlush = $true
$commands = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    StartedAt = [DateTimeOffset]::Now.ToString('o')
    Artifacts = $artifacts
    InstallRequested = [bool]$InstallSystem
    UninstallRequested = [bool]$UninstallSystem
    CleanupPartialRequested = [bool]$CleanupPartial
    RegisterCalls = 0
    UnregisterCalls = 0
    ExitCode = 1
    Stage = 'Initialize'
    Constraints = 'Fixed component only; production lifecycle may reclaim exact owned package files and add/remove its TIP in an existing user language using system APIs. No app configuration, credentials, microphone, cloud, UAC, default changes, new languages or manual registry writes. Removal uses actual owned DLL paths.'
    Sources = @('https://learn.microsoft.com/en-us/dotnet/api/system.appcontext.basedirectory?view=net-10.0', 'https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/AppContext.cs')
}
function Note([string]$text) {
    $line = [DateTimeOffset]::Now.ToString('o') + ' ' + $text
    $log.WriteLine($line)
    Write-Host $line
}
function Save([string]$name, $value) {
    [IO.File]::WriteAllText((Join-Path $artifacts $name), (ConvertTo-Json -InputObject $value -Depth 24), $utf8)
}
function Hash([string]$path) {
    if (-not [IO.File]::Exists($path)) { return $null }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Get-FileFact([string]$path) {
    $exists = [IO.File]::Exists($path)
    [ordered]@{ Path = $path; Exists = $exists; Sha256 = Hash $path
        Length = $(if ($exists) { ([IO.FileInfo]::new($path)).Length } else { $null })
        LastWriteTimeUtc = $(if ($exists) { ([IO.FileInfo]::new($path)).LastWriteTimeUtc.ToString('o') } else { $null }) }
}
function Invoke-Recorded([string]$name, [string]$executable, [string[]]$arguments, [int]$timeoutSeconds = 30) {
    $record = [ordered]@{ Name = $name; Executable = $executable; Arguments = $arguments
        WorkingDirectory = $repo; StartedAt = [DateTimeOffset]::Now.ToString('o'); ExitCode = $null }
    $commands.Add($record)
    Save 'commands.json' $commands.ToArray()
    Note ('COMMAND ' + $name + ': "' + $executable + '" ' + (($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $executable
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    foreach ($arg in $arguments) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Child process did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
            $record['TimedOut'] = $true
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) { throw 'Child termination is uncertain; no retry.' }
        }
        $record.ExitCode = $process.ExitCode
        $record['Stdout'] = $stdout.GetAwaiter().GetResult()
        $record['Stderr'] = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $artifacts ($name + '.stdout.log')), $record.Stdout, $utf8)
        [IO.File]::WriteAllText((Join-Path $artifacts ($name + '.stderr.log')), $record.Stderr, $utf8)
        Note ('EXIT ' + $name + '=' + $record.ExitCode)
        return [pscustomobject]$record
    }
    finally {
        $record['CompletedAt'] = [DateTimeOffset]::Now.ToString('o')
        Save 'commands.json' $commands.ToArray()
        $process.Dispose()
    }
}
function Read-Values([Microsoft.Win32.RegistryKey]$base, [string]$path) {
    $key = $base.OpenSubKey($path, $false)
    try {
        $rows = @()
        if ($key) {
            foreach ($name in ($key.GetValueNames() | Sort-Object)) {
                $rows += [ordered]@{ Name = $name; Kind = $key.GetValueKind($name).ToString()
                    Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
            }
        }
        return [ordered]@{ Exists = $null -ne $key; Values = $rows }
    }
    finally { if ($key) { $key.Dispose() } }
}
function Read-InputBaseline {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
    try {
        $key = $base.OpenSubKey('Control Panel\International\User Profile', $false)
        try {
            $present = $null -ne $key -and $key.GetValueNames() -contains 'Default'
            $default = [ordered]@{ Exists = $present; Kind = $null; Value = $null }
            if ($present) { $default.Kind = $key.GetValueKind('Default').ToString(); $default.Value = $key.GetValue('Default') }
        }
        finally { if ($key) { $key.Dispose() } }
        $languageList = Get-WinUserLanguageList -ErrorAction Stop
        $languages = @(foreach ($language in $languageList) {
            [ordered]@{ LanguageTag = $language.LanguageTag; InputMethodTips = @($language.InputMethodTips) }
        })
        $override = Get-WinDefaultInputMethodOverride -ErrorAction Stop
        return [ordered]@{ DefaultRegistry = $default
            DefaultCmdlet = $(if ($null -eq $override) { $null } else { [string]$override.InputMethodTip })
            Languages = $languages
            Preload = Read-Values $base 'Keyboard Layout\Preload'
            Substitutes = Read-Values $base 'Keyboard Layout\Substitutes' }
    }
    finally { $base.Dispose() }
}
function Read-Footprint {
    $rows = @()
    foreach ($view in @('Registry64', 'Registry32')) {
        foreach ($hive in @('CurrentUser', 'LocalMachine')) {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                $com = $base.OpenSubKey(('Software\Classes\CLSID\' + $clsid), $false)
                $tip = $base.OpenSubKey(('Software\Microsoft\CTF\TIP\' + $clsid), $false)
                $server = if ($com) { $com.OpenSubKey('InprocServer32', $false) } else { $null }
                $lang = if ($tip) { $tip.OpenSubKey(('LanguageProfile\0x00000804\' + $profileGuid), $false) } else { $null }
                $config = if ($hive -eq 'CurrentUser') { $base.OpenSubKey('Software\TrueFluentPro\VoiceIme', $false) } else { $null }
                try {
                    $configOwner = if ($config) { $config.GetValue('VoiceImeOwner') } else { $null }
                    $rows += [ordered]@{ Hive = $hive; View = $view; ComExists = $null -ne $com
                        ComOwner = $(if ($com) { $com.GetValue('VoiceImeOwner') } else { $null })
                        DllPath = $(if ($server) { $server.GetValue('') } else { $null })
                        ThreadingModel = $(if ($server) { $server.GetValue('ThreadingModel') } else { $null })
                        TipExists = $null -ne $tip; ProfileKeyExists = $null -ne $lang
                        TipOwner = $(if ($tip) { $tip.GetValue('VoiceImeOwner') } else { $null })
                        HostRecordExists = $null -ne $config; HostOwner = $configOwner
                        HostExe = $(if ($configOwner -ceq $owner) { $config.GetValue('HostExe') } else { $null })
                        VoiceImeInstall = $(if ($configOwner -ceq $owner) { $config.GetValue('VoiceImeInstall') } else { $null }) }
                }
                finally { foreach ($key in @($lang, $server, $com, $tip, $config)) { if ($key) { $key.Dispose() } } }
            }
            finally { $base.Dispose() }
        }
    }
    return $rows
}
function Read-Manifest {
    if (-not [IO.File]::Exists($manifestPath)) { return [ordered]@{ Path = $manifestPath; Exists = $false; Version = $null; Installations = @() } }
    if (([IO.FileInfo]::new($manifestPath)).Length -gt 65536) { throw 'Component manifest exceeds 64 KiB; stopped.' }
    $raw = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    return [ordered]@{ Path = $manifestPath; Exists = $true; Version = $raw.Version
        Installations = @($raw.Installations | ForEach-Object {
            [ordered]@{ Architecture = $_.Architecture; DllPath = $_.DllPath; HelperPath = $_.HelperPath }
        }) }
}
function Get-PackageFacts {
    $paths = @((Join-Path $release 'TrueFluentPro.dll'), (Join-Path $release 'TrueFluentPro.exe'))
    foreach ($arch in $architectures) {
        $paths += Join-Path $release "voice-ime/$arch/TrueFluentVoiceIme.dll"
        $paths += Join-Path $release "voice-ime/$arch/VoiceImeRegister.exe"
    }
    return @($paths | ForEach-Object { Get-FileFact $_ })
}
function Get-QueryEntries($manifest, $fallback) {
    $entries = @()
    foreach ($entry in $fallback) {
        $saved = @($manifest.Installations | Where-Object Architecture -eq $entry.Architecture)
        if ($saved.Count -eq 0) { $entries += $entry; continue }
        if ($saved.Count -ne 1) { throw 'Ambiguous component manifest; no helper executed.' }
        $actual = $saved[0]
        $relative = [IO.Path]::GetRelativePath($installRoot, [IO.Path]::GetFullPath($actual.DllPath))
        if ($relative -notmatch ('^[a-fA-F0-9]{64}\\' + [regex]::Escape($entry.Architecture) + '\\TrueFluentVoiceIme\.dll$')) {
            throw 'Manifest DLL is outside the fixed Program Files component layout; no helper executed.'
        }
        $helperRelative = [IO.Path]::GetRelativePath([IO.Path]::GetDirectoryName($actual.DllPath), [IO.Path]::GetFullPath($actual.HelperPath))
        if ($helperRelative -notmatch '^[a-fA-F0-9]{64}\\VoiceImeRegister\.exe$') { throw 'Manifest helper layout is not the fixed component cache.' }
        $hash = Hash $actual.HelperPath
        if ($null -eq $hash) { throw 'Manifest helper is missing; left untouched.' }
        $packageRoot = ($relative -split '\\')[0]
        $cacheIdentity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($packageRoot + ':' + $hash))).ToLowerInvariant()
        if (($helperRelative -split '\\')[0] -ine $cacheIdentity) { throw 'Manifest helper cache identity is inconsistent; no helper executed.' }
        $entries += $actual
    }
    return $entries
}
function Read-Native([string]$phase, $entries) {
    $rows = @()
    foreach ($entry in $entries) {
        $arch = $entry.Architecture
        $s = Invoke-Recorded "$phase-$arch-status" $entry.HelperPath @('--status', $entry.DllPath)
        $p = Invoke-Recorded "$phase-$arch-probe" $entry.HelperPath @('--probe')
        $status = $null
        try { $status = $s.Stdout.Trim() | ConvertFrom-Json -AsHashtable } catch { }
        $match = [regex]::Match($p.Stdout, 'probe enumerated=([01]) enabled=([01]) keyboard=([01]);')
        $rows += [ordered]@{ Architecture = $arch; DllPath = $entry.DllPath; HelperPath = $entry.HelperPath
            DllSha256 = Hash $entry.DllPath; HelperSha256 = Hash $entry.HelperPath
            StatusExitCode = $s.ExitCode; Status = $status; StatusOutput = $s.Stdout.Trim()
            ProbeExitCode = $p.ExitCode; ProbeOutput = $p.Stdout.Trim()
            Enumerated = $match.Success -and $match.Groups[1].Value -eq '1'
            Enabled = $match.Success -and $match.Groups[2].Value -eq '1'
            Keyboard = $match.Success -and $match.Groups[3].Value -eq '1' }
    }
    return $rows
}
function Get-ServiceResult([string]$name, $task) {
    $result = $task.WaitAsync([TimeSpan]::FromSeconds(180)).GetAwaiter().GetResult()
    $safe = [ordered]@{ Succeeded = [bool]$result.Succeeded; Message = [string]$result.Message }
    Save ($name + '.json') $safe
    Note ($name + ': Succeeded=' + $safe.Succeeded + '; ' + $safe.Message)
    return $safe
}
function Same($left, $right) {
    return (ConvertTo-Json -InputObject $left -Depth 16 -Compress) -ceq (ConvertTo-Json -InputObject $right -Depth 16 -Compress)
}
function Input-ExceptComponent($inputState) {
    $copy = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $inputState -Depth 16 -Compress) -AsHashtable
    $ownTip = '0804:' + $clsid + $profileGuid
    foreach ($language in $copy.Languages) {
        $language.InputMethodTips = @($language.InputMethodTips | Where-Object { $_ -ine $ownTip })
    }
    return $copy
}

$originalBase = [AppContext]::GetData('APP_CONTEXT_BASE_DIRECTORY')
$originalTrace = [Environment]::GetEnvironmentVariable('TRUEFLUENT_VOICE_IME_TRACE')
$before = $null
$expected = @()
try {
    Set-Location -LiteralPath $repo
    Note ('ARTIFACTS=' + $artifacts)
    if ($InstallSystem -or $UninstallSystem -or $CleanupPartial) {
        $tracePath = Join-Path $artifacts 'native-stages.log'
        [IO.File]::WriteAllText($tracePath, '', $utf8)
        [Environment]::SetEnvironmentVariable('TRUEFLUENT_VOICE_IME_TRACE', $tracePath)
        $report['NativeTracePath'] = $tracePath
    }
    $report.Stage = 'Elevation'
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class VoiceImeInstallTokenEvidence {
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int size, out int returned);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static bool IsElevated() {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var token)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            if (!GetTokenInformation(token, 20, out int value, 4, out int returned)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (returned != 4) throw new InvalidOperationException("Unexpected TOKEN_ELEVATION size.");
            return value != 0;
        } finally { CloseHandle(token); }
    }
}
'@
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $principalAdmin = [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
    $report['Environment'] = [ordered]@{ Elevated = [VoiceImeInstallTokenEvidence]::IsElevated(); Administrator = $principalAdmin
        PowerShell = $PSVersionTable.PSVersion.ToString(); Dotnet = [Environment]::Version.ToString()
        ProcessPath = [Environment]::ProcessPath; Is64Bit = [Environment]::Is64BitProcess; OriginalBaseDirectory = [AppContext]::BaseDirectory }
    Save 'environment.json' $report.Environment
    if (-not $report.Environment.Elevated -or -not $principalAdmin) { throw 'Elevated=False; stopped without calling registration.' }
    if (-not [Environment]::Is64BitProcess -or [Environment]::Version.Major -lt 10) { throw 'Requires existing 64-bit PowerShell running .NET 10; no elevation or host installation attempted.' }

    $report.Stage = 'Baseline'
    $before = [ordered]@{ Input = Read-InputBaseline; Footprint = @(Read-Footprint); Manifest = Read-Manifest; Package = @(Get-PackageFacts) }
    Save 'baseline.json' $before
    if ($BuildRelease) {
        $report.Stage = 'BuildRelease'
        $build = Invoke-Recorded 'release-build' (Get-Command dotnet -CommandType Application).Source @('build', 'TrueFluentPro.csproj', '-c', 'Release', '-v', 'quiet', '--nologo') 300
        if ($build.ExitCode -ne 0 -or $build.PSObject.Properties['TimedOut']) { throw 'Release build failed/timed out; installation not attempted.' }
    }
    $package = @(Get-PackageFacts)
    Save 'preinstall-package.json' $package
    if (@($package | Where-Object { -not $_.Exists }).Count) { throw 'Release package incomplete; no system writes attempted.' }
    $identityLines = @()
    foreach ($arch in $architectures) {
        $dll = Join-Path $release "voice-ime/$arch/TrueFluentVoiceIme.dll"
        $helper = Join-Path $release "voice-ime/$arch/VoiceImeRegister.exe"
        $identityLines += $arch + ':' + (Hash $dll) + ':' + (Hash $helper)
    }
    $packageIdentity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes(($identityLines -join "`n")))).ToLowerInvariant()
    $report['PackageIdentity'] = $packageIdentity
    foreach ($arch in $architectures) {
        $helperHash = Hash (Join-Path $release "voice-ime/$arch/VoiceImeRegister.exe")
        $cacheIdentity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($packageIdentity + ':' + $helperHash))).ToLowerInvariant()
        $expected += [ordered]@{ Architecture = $arch; DllPath = Join-Path $installRoot "$packageIdentity/$arch/TrueFluentVoiceIme.dll"
            HelperPath = Join-Path $installRoot "$packageIdentity/$arch/$cacheIdentity/VoiceImeRegister.exe" }
    }
    Save 'expected-installation.json' $expected
    $report.Stage = 'LoadReleaseService'
    # AppContext 的公开进程局部数据槽；不注入生产字段、不运行 TrueFluentPro.Main。
    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $release + [IO.Path]::DirectorySeparatorChar)
    if (-not [string]::Equals([IO.Path]::GetFullPath([AppContext]::BaseDirectory).TrimEnd('\'), $release.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Test process BaseDirectory does not match Release.' }
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $release 'TrueFluentPro.dll'))
    if ($assembly.Location -ine (Join-Path $release 'TrueFluentPro.dll')) { throw 'Loaded assembly is not the selected Release DLL.' }
    $service = [Activator]::CreateInstance($assembly.GetType('TrueFluentPro.Services.VoiceImeRegistrationService', $true))
    $report['ServiceAssembly'] = $assembly.Location
    $report['ServiceBaseDirectory'] = [AppContext]::BaseDirectory
    $report['RefreshBefore'] = Get-ServiceResult 'service-refresh-before' ($service.RefreshStatusAsync())
    $packagedEntries = @($architectures | ForEach-Object { [ordered]@{ Architecture = $_
        DllPath = Join-Path $release "voice-ime/$_/TrueFluentVoiceIme.dll"; HelperPath = Join-Path $release "voice-ime/$_/VoiceImeRegister.exe" } })
    $beforeEntries = @(Get-QueryEntries $before.Manifest $packagedEntries)
    if ($CleanupPartial) {
        $beforeEntries = @($beforeEntries | ForEach-Object { [ordered]@{ Architecture = $_.Architecture; DllPath = $_.DllPath
            HelperPath = Join-Path $release ('voice-ime/' + $_.Architecture + '/VoiceImeRegister.exe') } })
    }
    $report['NativeBefore'] = @(Read-Native 'before' $beforeEntries)
    Save 'native-before.json' $report.NativeBefore
    $preinstall = [ordered]@{ Input = Read-InputBaseline; Footprint = @(Read-Footprint); Manifest = Read-Manifest }
    Save 'preinstall.json' $preinstall
    if (-not (Same $before.Input $preinstall.Input) -or -not (Same $before.Footprint $preinstall.Footprint) -or -not (Same $before.Manifest $preinstall.Manifest)) { throw 'Baseline changed during read-only preparation; no registration attempted.' }
    if (-not $report.RefreshBefore.Succeeded) { throw 'C# refresh state is unknown; no registration attempted.' }
    $differentExistingPath = $false
    foreach ($row in $preinstall.Footprint) {
        if ($row.Hive -eq 'CurrentUser' -and ($row.ComExists -or ($row.TipExists -and
            @($report.NativeBefore | Where-Object { $_.Status.state -eq 'legacy-user' }).Count))) {
            throw 'Legacy HKCU component footprint present; left untouched.'
        }
        if ($row.ComExists -and $row.ComOwner -cne $owner) { throw 'Existing component owner is unknown; no uninstall or overwrite.' }
        if ($row.ComExists -and $row.DllPath -ine ($expected | Where-Object Architecture -eq $(if ($row.View -eq 'Registry64') { 'win-x64' } else { 'win-x86' })).DllPath) { $differentExistingPath = $true }
        if ($row.HostRecordExists -and ($row.HostOwner -cne $owner -or $row.HostExe -ine (Join-Path $release 'TrueFluentPro.exe'))) { throw 'Existing host owner/path does not match selected Release; left untouched.' }
    }
    foreach ($row in $report.NativeBefore) {
        if ($row.StatusExitCode -notin @(0, 1) -or $null -eq $row.Status -or $row.Status.state -notin @('unregistered', 'partial', 'registered')) { throw 'Existing native state is different-path/legacy/unknown; no registration attempted.' }
    }
    # Explicit InstallSystem uses the production upgrade/repair lifecycle; it still
    # validates the exact owner, SID, receipt and old paths before replacing a package.
    if (-not $UninstallSystem -and -not $InstallSystem -and $differentExistingPath -and @($report.NativeBefore | Where-Object { $_.Status.state -eq 'registered' -or $_.Enumerated }).Count) {
        throw 'Existing successful/enumerable installation uses another version; read-only stop, no uninstall or overwrite.'
    }
    if (-not $UninstallSystem -and -not $InstallSystem -and $differentExistingPath -and @($report.NativeBefore | Where-Object { $_.Status.state -ne 'partial' }).Count) {
        throw 'Different-path state is not a confirmed partial installation; left untouched.'
    }
    if ($CleanupPartial -or $UninstallSystem) {
        $ownedMachineAnchor = @($preinstall.Footprint | Where-Object {
            $_.Hive -eq 'LocalMachine' -and $_.ComExists -and $_.ComOwner -ceq $owner
        }).Count -gt 0
        # 旧回滚可能只留下 Enable=0。仅允许已有精确清单进入生产维修入口；
        # 服务仍核对 SID/receipt，原生助手仍强制校验孤儿树、维护门禁及受保护 DLL。
        $receiptRecovery = $preinstall.Manifest.Exists -and $preinstall.Manifest.Installations.Count -eq $architectures.Count -and
            @($preinstall.Footprint | Where-Object ComExists).Count -eq 0 -and
            @($beforeEntries | Where-Object { -not [IO.File]::Exists($_.DllPath) }).Count -eq 0
        if ($CleanupPartial -and (@($report.NativeBefore | Where-Object { $_.Status.state -ne 'partial' -or $_.Enumerated }).Count -or
            (-not $ownedMachineAnchor -and -not $receiptRecovery))) {
            throw 'CleanupPartial requires confirmed non-enumerable partial state and an owned COM anchor or verified recovery receipt.'
        }
        $report.Stage = 'UnregisterAsync'
        $report.UnregisterCalls = 1
        Save 'progress.json' $report
        $report['UnregisterResult'] = Get-ServiceResult 'service-unregister' ($service.UnregisterAsync())
        $clean = [ordered]@{ Input = Read-InputBaseline; Footprint = @(Read-Footprint); Manifest = Read-Manifest }
        Save 'after-cleanup.json' $clean
        $report['NativeClean'] = @(Read-Native 'clean' $packagedEntries)
        Save 'native-clean.json' $report.NativeClean
        $report['CleanConfirmed'] = $report.UnregisterResult.Succeeded -and $clean.Manifest.Installations.Count -eq 0 -and
            @($clean.Footprint | Where-Object { $_.ComExists -or $_.TipExists -or $_.HostRecordExists }).Count -eq 0 -and
            @($report.NativeClean | Where-Object { $_.StatusExitCode -ne 1 -or $null -eq $_.Status -or $_.Status.state -ne 'unregistered' -or $_.Enumerated -or $_.Enabled -or $_.Keyboard }).Count -eq 0
        if (-not $report.CleanConfirmed) { throw 'Production uninstall did not confirm clean state; ownership/files retained, installation not attempted.' }
        $report['UninstallPreservedOtherInputSettings'] = Same (Input-ExceptComponent $before.Input) (Input-ExceptComponent $clean.Input)
        if (-not $report.UninstallPreservedOtherInputSettings) { throw 'Other input settings changed during cleanup; no installation attempted.' }
        Save 'progress.json' $report
    }
    if (-not $InstallSystem) {
        $report.Stage = if ($UninstallSystem) { 'UninstalledConfirmed' } else { 'ReadOnlyComplete' }
        $report.ExitCode = 0
    }
    else {
        $report.Stage = 'RegisterAsync'
        $report.RegisterCalls = 1
        Save 'progress.json' $report
        # 生产安装入口；每次驱动最多一次，失败保留本轮证据，不在脚本内循环安装。
        $report['RegisterResult'] = Get-ServiceResult 'service-register' ($service.RegisterAsync())
        $report.Stage = 'Postcheck'
        $report['ManifestAfter'] = Read-Manifest
        Save 'manifest-after.json' $report.ManifestAfter
        $queryFallback = @()
        foreach ($entry in $expected) {
            # 清单为空时才使用本次预期 DLL 路径和当前包的只读助手。
            $queryFallback += [ordered]@{ Architecture = $entry.Architecture; DllPath = $entry.DllPath
                HelperPath = Join-Path $release ('voice-ime/' + $entry.Architecture + '/VoiceImeRegister.exe') }
        }
        $queryEntries = @(Get-QueryEntries $report.ManifestAfter $queryFallback)
        $report['NativeAfter'] = @(Read-Native 'after' $queryEntries)
        Save 'native-after.json' $report.NativeAfter
        $report['RefreshAfter'] = Get-ServiceResult 'service-refresh-after' ($service.RefreshStatusAsync())
        $after = [ordered]@{ Input = Read-InputBaseline; Footprint = @(Read-Footprint); Manifest = Read-Manifest }
        Save 'after.json' $after
        $report['DefaultRegistryUnchanged'] = Same $before.Input.DefaultRegistry $after.Input.DefaultRegistry
        $report['DefaultCmdletUnchanged'] = Same $before.Input.DefaultCmdlet $after.Input.DefaultCmdlet
        $report['PreloadUnchanged'] = Same $before.Input.Preload $after.Input.Preload
        $report['SubstitutesUnchanged'] = Same $before.Input.Substitutes $after.Input.Substitutes
        $beforeOther = @($before.Input.Languages | ForEach-Object { [ordered]@{ LanguageTag = $_.LanguageTag; InputMethodTips = @($_.InputMethodTips | Where-Object { $_ -notlike "*$clsid*$profileGuid*" }) } })
        $afterOther = @($after.Input.Languages | ForEach-Object { [ordered]@{ LanguageTag = $_.LanguageTag; InputMethodTips = @($_.InputMethodTips | Where-Object { $_ -notlike "*$clsid*$profileGuid*" }) } })
        $report['OtherLanguagesAndTipsUnchanged'] = Same $beforeOther $afterOther
        $report['Installed'] = @($report.NativeAfter | Where-Object { $_.StatusExitCode -eq 0 -and $null -ne $_.Status -and $_.Status.registered -eq $true -and $_.Status.state -eq 'registered' -and $_.ProbeExitCode -eq 0 -and $_.Enumerated -and $_.Enabled -and $_.Keyboard }).Count -eq 2
        $report['ManifestMatchesExpected'] = $report.ManifestAfter.Installations.Count -eq 2 -and @($expected | Where-Object {
            $e = $_
            @($report.ManifestAfter.Installations | Where-Object { $_.Architecture -eq $e.Architecture -and $_.DllPath -ieq $e.DllPath -and $_.HelperPath -ieq $e.HelperPath }).Count -ne 1
        }).Count -eq 0
        $report['InstalledHashesMatch'] = @($report.NativeAfter | Where-Object {
            $_.DllSha256 -cne (Hash (Join-Path $release ('voice-ime/' + $_.Architecture + '/TrueFluentVoiceIme.dll'))) -or
            $_.HelperSha256 -cne (Hash (Join-Path $release ('voice-ime/' + $_.Architecture + '/VoiceImeRegister.exe')))
        }).Count -eq 0
        $knownRegistered = $report.RefreshAfter.Succeeded -and $report.RefreshAfter.Message.Contains('双架构原生安装状态完整')
        if ($report.RegisterResult.Succeeded -and $report.Installed -and $report.ManifestMatchesExpected -and $report.InstalledHashesMatch -and $knownRegistered -and
            $report.DefaultRegistryUnchanged -and $report.DefaultCmdletUnchanged -and $report.PreloadUnchanged -and $report.SubstitutesUnchanged -and $report.OtherLanguagesAndTipsUnchanged) {
            $report.ExitCode = 0
            $report.Stage = 'InstalledAndRetained'
        }
        else { $report.Stage = 'FailedOrPostcheckMismatch-NoRetry' }
    }
}
catch {
    # 只运行固定登记服务/测试代码；不读取配置。完整服务 Result 单独保存。
    $report['Failure'] = [ordered]@{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message; ScriptLine = $_.InvocationInfo.ScriptLineNumber }
    Note ('FAILED at ' + $report.Stage + ': ' + $report.Failure.Message)
}
finally {
    try {
        $finalFootprint = @(Read-Footprint)
        $finalManifest = Read-Manifest
        Save 'final-footprint.json' $finalFootprint
        Save 'final-manifest.json' $finalManifest
        if ($null -ne $before) {
            $finalInput = Read-InputBaseline
            Save 'final-input.json' $finalInput
            $report['FinalDefaultRegistryUnchanged'] = Same $before.Input.DefaultRegistry $finalInput.DefaultRegistry
            $report['FinalDefaultCmdletUnchanged'] = Same $before.Input.DefaultCmdlet $finalInput.DefaultCmdlet
            $report['FinalInputUnchanged'] = Same $before.Input $finalInput
            $report['FinalFootprintUnchanged'] = Same $before.Footprint $finalFootprint
            $report['FinalManifestUnchanged'] = Same $before.Manifest $finalManifest
        }
    }
    catch { $report['FinalSnapshotError'] = $_.Exception.GetType().FullName; $report.ExitCode = 1 }
    [AppContext]::SetData('APP_CONTEXT_BASE_DIRECTORY', $originalBase)
    if ($null -eq $originalTrace) { Remove-Item -LiteralPath Env:TRUEFLUENT_VOICE_IME_TRACE -ErrorAction SilentlyContinue }
    else { [Environment]::SetEnvironmentVariable('TRUEFLUENT_VOICE_IME_TRACE', $originalTrace) }
    $report['CompletedAt'] = [DateTimeOffset]::Now.ToString('o')
    Save 'result.json' $report
    Save 'commands.json' $commands.ToArray()
    [IO.File]::WriteAllText((Join-Path $artifacts 'exit-code.txt'), [string]$report.ExitCode, $utf8)
    Note ('FINAL_STAGE=' + $report.Stage + '; EXIT=' + $report.ExitCode + '; REGISTER_CALLS=' + $report.RegisterCalls)
    $log.Dispose()
}
exit $report.ExitCode