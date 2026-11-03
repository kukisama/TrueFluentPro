[CmdletBinding()]
param([switch]$Restore)

# One incident only: default read-only, one missing REG_SZ value on explicit restore.
# No product/TSF operation, language setter, default write, key creation or deletion.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$utf8 = [Text.UTF8Encoding]::new($false)
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$baselinePath = Join-Path $PSScriptRoot 'artifacts\20261103-084602-system-registration-5e2110cc3554449f8e3970fa3f722bec\baseline.json'
$artifacts = Join-Path $PSScriptRoot ('artifacts\preload-compensation-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($artifacts)
$ownTip = '0804:{5638E6E5-D18C-4FE8-B778-E59A5170B481}{A91272E1-9973-43F9-8EB4-7D5494EC92B1}'
$speedTip = '0404:{531FDEBF-9B4C-4A43-A2AA-960E8FCDC732}{6024B45F-5C54-11D4-B921-0080C882687E}'
$before = $null
$after = $null
$report = [ordered]@{
    StartedAt = [DateTimeOffset]::Now.ToString('o'); RestoreRequested = [bool]$Restore
    Stage = 'Initialize'; ExitCode = 1; WriteAttempted = $false; WriteCount = 0
    BaselinePath = $baselinePath; ReportPath = (Join-Path $artifacts 'report.json')
    Preflight = $null; Results = $null; ReadProcesses = @()
}
function Save([string]$name, $value) {
    [IO.File]::WriteAllText((Join-Path $artifacts $name), (ConvertTo-Json -InputObject $value -Depth 16), $utf8)
}
function Same($left, $right) {
    return (ConvertTo-Json -InputObject $left -Depth 16 -Compress) -ceq (ConvertTo-Json -InputObject $right -Depth 16 -Compress)
}
function Require([bool]$condition, [string]$stage) {
    $report.Stage = $stage
    if (-not $condition) { throw 'FixedPreloadGuardFailed' }
}
function NonOwn-Languages($state) {
    return @(foreach ($language in $state.Languages) {
        [ordered]@{ LanguageTag = $language.LanguageTag
            InputMethodTips = @($language.InputMethodTips | Where-Object { $_ -ine $ownTip }) }
    })
}
function Own-Tips($state) {
    return @(foreach ($language in $state.Languages) {
        [ordered]@{ LanguageTag = $language.LanguageTag
            InputMethodTips = @($language.InputMethodTips | Where-Object { $_ -ieq $ownTip }) }
    })
}
# Each observation has its own Windows PowerShell 5.1 process; never trust a writer's cache.
$readScript = @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version 2.0
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
function Values($base, [string]$path) {
    $key = $base.OpenSubKey($path, $false)
    try {
        $rows = @(if ($null -ne $key) {
            foreach ($name in ($key.GetValueNames() | Sort-Object)) {
                [ordered]@{ Name = $name; Kind = $key.GetValueKind($name).ToString()
                    Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
            }
        })
        return [ordered]@{ Exists = $null -ne $key; Values = $rows }
    } finally { if ($null -ne $key) { $key.Dispose() } }
}
try {
    if ($PSVersionTable.PSVersion.ToString() -notlike '5.1.*' -or -not [Environment]::Is64BitProcess) { throw 'ReadHost' }
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
    try {
        $root = $base.OpenSubKey('Control Panel\International\User Profile', $false)
        try {
            if ($null -eq $root -or $root.GetValueKind('Languages') -ne [Microsoft.Win32.RegistryValueKind]::MultiString) { throw 'RootLanguages' }
            $tags = @($root.GetValue('Languages'))
            $default = [ordered]@{ Exists = $root.GetValueNames() -contains 'Default'; Kind = $null; Value = $null }
            if ($default.Exists) {
                $default.Kind = $root.GetValueKind('Default').ToString()
                $default.Value = $root.GetValue('Default', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
        } finally { if ($null -ne $root) { $root.Dispose() } }
        $subkeys = @(foreach ($tag in $tags) {
            [ordered]@{ LanguageTag = $tag; Registry = Values $base ('Control Panel\International\User Profile\' + $tag) }
        })
        Import-Module -Name ([IO.Path]::Combine($PSHOME, 'Modules\International\International.psd1')) -ErrorAction Stop
        $items = International\Get-WinUserLanguageList -ErrorAction Stop
        $languages = @(foreach ($language in $items) {
            [ordered]@{ LanguageTag = [string]$language.LanguageTag; InputMethodTips = @($language.InputMethodTips) }
        })
        $metadata = @(foreach ($language in $items) {
            [ordered]@{ LanguageTag = [string]$language.LanguageTag; Handwriting = [bool]$language.Handwriting }
        })
        $override = International\Get-WinDefaultInputMethodOverride -ErrorAction Stop
        $state = [ordered]@{
            RootLanguages = $tags; LanguageSubkeys = $subkeys; ObservedMetadata = $metadata
            Input = [ordered]@{ DefaultRegistry = $default
                DefaultCmdlet = $(if ($null -eq $override) { $null } else { [string]$override.InputMethodTip })
                Languages = $languages; Preload = Values $base 'Keyboard Layout\Preload'
                Substitutes = Values $base 'Keyboard Layout\Substitutes' }
        }
        [Console]::WriteLine((ConvertTo-Json -InputObject $state -Depth 16 -Compress))
    } finally { $base.Dispose() }
    exit 0
} catch { [Console]::Error.WriteLine('FreshInputReadFailed'); exit 1 }
'@
function Read-Fresh([string]$phase) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [IO.Path]::Combine([Environment]::SystemDirectory, 'WindowsPowerShell\v1.0\powershell.exe')
    $start.Arguments = '-NoLogo -NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($readScript))
    $start.WorkingDirectory = $repo; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8; $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'ReadStartFailed' }
        $output = $process.StandardOutput.ReadToEndAsync(); $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(8000)) {
            $process.Kill(); [void]$process.WaitForExit(1000); throw 'ReadTimeoutNoRetry'
        }
        $report.ReadProcesses += [ordered]@{ Phase = $phase; ProcessId = $process.Id; ExitCode = $process.ExitCode }
        $errorText = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($errorText)) { throw 'ReadFailed' }
        $state = ConvertFrom-Json -InputObject $output.GetAwaiter().GetResult()
        Save ($phase + '.json') $state
        return $state
    } finally { $process.Dispose() }
}
function Preflight($state, $baseline) {
    $withoutSecond = [ordered]@{ Exists = $baseline.Preload.Exists
        Values = @($baseline.Preload.Values | Where-Object { $_.Name -cne '2' }) }
    $allTips = @($state.Input.Languages | ForEach-Object { $_.InputMethodTips })
    $registryNames = @($state.LanguageSubkeys | ForEach-Object { $_.Registry.Values } | ForEach-Object { $_.Name })
    return [ordered]@{
        RootLanguagesAndOrder = Same @('zh-Hans-CN', 'zh-Hant-HK') @($state.RootLanguages)
        LanguageTagsAndOrder = Same @($baseline.Languages.LanguageTag) @($state.Input.Languages.LanguageTag)
        NonOwnTipsAndOrder = Same (NonOwn-Languages $baseline) (NonOwn-Languages $state.Input)
        SpeedAbsent = $allTips -inotcontains $speedTip -and $registryNames -inotcontains $speedTip
        OwnTipPresent = @($allTips | Where-Object { $_ -ieq $ownTip }).Count -eq 1
        HongKongEmpty = @($state.Input.Languages | Where-Object { $_.LanguageTag -ceq 'zh-Hant-HK' -and $_.InputMethodTips.Count -eq 0 }).Count -eq 1
        Preload1ExactAnd2Missing = Same $withoutSecond $state.Input.Preload
        SubstitutesBaselineExact = Same $baseline.Substitutes $state.Input.Substitutes
        DefaultRegistryBaselineExact = Same $baseline.DefaultRegistry $state.Input.DefaultRegistry
        DefaultCmdletBaselineExact = Same $baseline.DefaultCmdlet $state.Input.DefaultCmdlet
    }
}
try {
    Require ([Environment]::Is64BitProcess) 'Host.Native64'
    $baselineText = [IO.File]::ReadAllText($baselinePath)
    $report['BaselineSha256'] = (Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash
    $baseline = (ConvertFrom-Json -InputObject $baselineText).Input
    $baselineSecond = @($baseline.Preload.Values | Where-Object { $_.Name -ceq '2' })
    Require ($baselineSecond.Count -eq 1 -and $baselineSecond[0].Kind -ceq 'String' -and
        $baselineSecond[0].Value -ceq '00000409' -and
        (Same @('zh-Hans-CN', 'zh-Hant-HK') @($baseline.Languages.LanguageTag)) -and
        (Same @('1', '2') @($baseline.Preload.Values.Name)) -and
        $baseline.Preload.Values[0].Kind -ceq 'String' -and $baseline.Preload.Values[0].Value -ceq '00000804') 'Baseline.ExactIncident'
    $before = Read-Fresh 'before'
    $report.Preflight = Preflight $before $baseline
    Save 'report.json' $report
    Require (-not ($report.Preflight.Values -contains $false)) 'Preflight.AllGuards'
    if ($Restore) {
        $recheck = Read-Fresh 'before-preload'
        Require ((Same $before $recheck) -and [IO.File]::ReadAllText($baselinePath) -ceq $baselineText) 'Preload.FreshImmediateRecheck'
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
        $key = $null
        try {
            $key = $base.OpenSubKey('Keyboard Layout\Preload', $true)
            Require ($null -ne $key -and (Same @('1') @($key.GetValueNames())) -and
                $key.GetValueKind('1') -eq [Microsoft.Win32.RegistryValueKind]::String -and
                $key.GetValue('1', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -ceq '00000804') 'Preload.StillOnlyOriginal1'
            $report.Stage = 'Preload.RestoreMissing2'; $report.WriteAttempted = $true
            Save 'report.json' $report
            $key.SetValue('2', [string]$baselineSecond[0].Value, [Microsoft.Win32.RegistryValueKind]::String)
            $report.WriteCount = 1
        } finally { if ($null -ne $key) { $key.Dispose() }; $base.Dispose() }
    }
    $after = Read-Fresh 'after'
    $report.Results = [ordered]@{
        RootLanguagesAndOrder = Same @($baseline.Languages.LanguageTag) @($after.RootLanguages)
        NonOwnTipsAndOrder = Same (NonOwn-Languages $baseline) (NonOwn-Languages $after.Input)
        PreloadBaselineExact = Same $baseline.Preload $after.Input.Preload
        SubstitutesBaselineExact = Same $baseline.Substitutes $after.Input.Substitutes
        DefaultRegistryBaselineExact = Same $baseline.DefaultRegistry $after.Input.DefaultRegistry
        DefaultCmdletBaselineExact = Same $baseline.DefaultCmdlet $after.Input.DefaultCmdlet
        OwnTipPreserved = Same (Own-Tips $before.Input) (Own-Tips $after.Input)
        LanguageRegistryMetadataUnchanged = Same $before.LanguageSubkeys $after.LanguageSubkeys
        HandwritingUnchanged = Same $before.ObservedMetadata $after.ObservedMetadata
    }
    if ($Restore) { Require (-not ($report.Results.Values -contains $false)) 'Final.ExactExceptOwn' }
    else { Require (Same $before $after) 'ReadOnly.Unchanged' }
    $report.Stage = $(if ($Restore) { 'RestoredAndVerified' } else { 'ReadOnlyReady' })
    $report.ExitCode = 0
} catch {
    $report['Failure'] = [ordered]@{ Stage = $report.Stage; Type = $_.Exception.GetType().FullName; Line = $_.InvocationInfo.ScriptLineNumber }
    # No additional write or automatic retry even if the independent postcheck fails.
    if ($null -ne $before -and $null -eq $after) {
        try { $after = Read-Fresh 'after' } catch { $report['FinalReadFailed'] = $true }
    }
} finally {
    $report['CompletedAt'] = [DateTimeOffset]::Now.ToString('o')
    Save 'report.json' $report
    [IO.File]::WriteAllText((Join-Path $artifacts 'exit-code.txt'), [string]$report.ExitCode, $utf8)
    foreach ($section in @('Preflight', 'Results')) {
        if ($null -ne $report[$section]) {
            foreach ($name in $report[$section].Keys) { Write-Output ($section + '.' + $name + '=' + $report[$section][$name]) }
        }
    }
    Write-Output ('WriteCount=' + $report.WriteCount + ';Stage=' + $report.Stage + ';ExitCode=' + $report.ExitCode)
    Write-Output ('Report=' + $report.ReportPath)
}
exit $report.ExitCode