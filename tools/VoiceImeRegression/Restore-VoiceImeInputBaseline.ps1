[CmdletBinding()]
param([switch]$Restore)

# One incident only. Default is read-only; no language-list setter, product code,
# machine registration, elevation, cache cleanup, service removal or restart.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$source = Join-Path $PSScriptRoot 'artifacts\20261103-084602-system-registration-5e2110cc3554449f8e3970fa3f722bec'
$artifacts = Join-Path $PSScriptRoot ('artifacts\input-baseline-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$reportPath = Join-Path $artifacts 'report.json'
$addedTip = '0404:{531FDEBF-9B4C-4A43-A2AA-960E8FCDC732}{6024B45F-5C54-11D4-B921-0080C882687E}'
$pinyin = '0804:{E7EA138E-69F8-11D7-A6EA-00065B844310}{E7EA138F-69F8-11D7-A6EA-00065B844311}'
$ownTip = '0804:{5638E6E5-D18C-4FE8-B778-E59A5170B481}{A91272E1-9973-43F9-8EB4-7D5494EC92B1}'
$baseline = $null
$before = $null
$after = $null
$api = $null
$report = [ordered]@{
    StartedAt = [DateTimeOffset]::Now.ToString('o')
    RestoreRequested = [bool]$Restore
    ExitCode = 1
    Stage = 'Initialize'
    SourceDirectory = $source
    ReportPath = $reportPath
    Preflight = $null
    UninstallInvoked = $false
    UninstallResult = $null
    PreloadWriteAttempted = $false
    PreloadWriteConfirmed = $false
    Results = $null
    SpellingHistoricalKnown = $false
    SpellingWritten = $false
    Sources = @(
        'https://learn.microsoft.com/en-us/windows/win32/tsf/installlayoutortip',
        'https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-loadlibraryexw',
        'https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getprocaddress',
        'https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.registrykey.setvalue'
    )
}

function Save([string]$name, $value) {
    [IO.File]::WriteAllText((Join-Path $artifacts $name), (ConvertTo-Json -InputObject $value -Depth 16), $utf8)
}
function Same($left, $right) {
    return (ConvertTo-Json -InputObject $left -Depth 16 -Compress) -ceq (ConvertTo-Json -InputObject $right -Depth 16 -Compress)
}
function Except-Own($state) {
    $copy = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $state -Depth 16)
    foreach ($language in $copy.Languages) {
        $language.InputMethodTips = @($language.InputMethodTips | Where-Object { $_ -ine $ownTip })
    }
    return $copy
}
function Own-Tips($state) {
    return @(foreach ($language in $state.Languages) {
        [ordered]@{ LanguageTag = $language.LanguageTag; Tips = @($language.InputMethodTips | Where-Object { $_ -ieq $ownTip }) }
    })
}
function Same-ExceptOwn($left, $right) { return (Same (Except-Own $left) (Except-Own $right)) }
function Require([bool]$condition, [string]$stage) {
    $report.Stage = $stage
    if (-not $condition) { throw 'Fixed input baseline guard failed; no retry.' }
}
function Read-Values([Microsoft.Win32.RegistryKey]$base, [string]$path) {
    $key = $base.OpenSubKey($path, $false)
    try {
        $values = @(if ($null -ne $key) {
            foreach ($name in ($key.GetValueNames() | Sort-Object)) {
                [ordered]@{ Name = $name; Kind = $key.GetValueKind($name).ToString()
                    Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
            }
        })
        return [ordered]@{ Exists = $null -ne $key; Values = $values }
    }
    finally { if ($null -ne $key) { $key.Dispose() } }
}
function Read-Input {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
    try {
        $key = $base.OpenSubKey('Control Panel\International\User Profile', $false)
        try {
            $present = $null -ne $key -and $key.GetValueNames() -contains 'Default'
            $default = [ordered]@{ Exists = $present; Kind = $null; Value = $null }
            if ($present) {
                $default.Kind = $key.GetValueKind('Default').ToString()
                $default.Value = $key.GetValue('Default', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
        }
        finally { if ($null -ne $key) { $key.Dispose() } }
        # Project detached values immediately; never mutate the cmdlet's objects.
        $languages = @()
        $metadata = @()
        foreach ($language in (International\Get-WinUserLanguageList -ErrorAction Stop)) {
            $languages += [ordered]@{ LanguageTag = [string]$language.LanguageTag; InputMethodTips = @($language.InputMethodTips) }
            $metadata += [ordered]@{ LanguageTag = [string]$language.LanguageTag; Handwriting = [bool]$language.Handwriting }
        }
        $override = International\Get-WinDefaultInputMethodOverride -ErrorAction Stop
        return [ordered]@{
            Input = [ordered]@{ DefaultRegistry = $default
                DefaultCmdlet = $(if ($null -eq $override) { $null } else { [string]$override.InputMethodTip })
                Languages = $languages
                Preload = Read-Values $base 'Keyboard Layout\Preload'
                Substitutes = Read-Values $base 'Keyboard Layout\Substitutes' }
            ObservedMetadata = $metadata
        }
    }
    finally { $base.Dispose() }
}
function Other-Preload($inputState) {
    return [ordered]@{ Exists = $inputState.Preload.Exists; Values = @($inputState.Preload.Values | Where-Object { $_.Name -cne '2' }) }
}
function NonOwn-Tips($inputState) {
    return @(foreach ($language in $inputState.Languages) {
        [ordered]@{ LanguageTag = $language.LanguageTag; InputMethodTips = @($language.InputMethodTips | Where-Object { $_ -ine $ownTip }) }
    })
}
function Compare-Input($expected, $actual) {
    $cn = @($actual.Languages | Where-Object { $_.LanguageTag -ceq 'zh-Hans-CN' })
    $hk = @($actual.Languages | Where-Object { $_.LanguageTag -ceq 'zh-Hant-HK' })
    $second = @($actual.Preload.Values | Where-Object { $_.Name -ceq '2' })
    return [ordered]@{
        LanguageTags = Same @($expected.Languages.LanguageTag) @($actual.Languages.LanguageTag)
        NonOwnTipsAndOrder = Same (NonOwn-Tips $expected) (NonOwn-Tips $actual)
        AllTipsAndOrder = Same $expected.Languages $actual.Languages
        ZhHansCNTips = $cn.Count -eq 1 -and (Same @($pinyin) @($cn[0].InputMethodTips))
        ZhHantHKTips = $hk.Count -eq 1 -and (Same @() @($hk[0].InputMethodTips))
        DefaultRegistry = Same $expected.DefaultRegistry $actual.DefaultRegistry
        DefaultCmdlet = Same $expected.DefaultCmdlet $actual.DefaultCmdlet
        Preload = Same $expected.Preload $actual.Preload
        Preload2RegSz = $second.Count -eq 1 -and $second[0].Kind -ceq 'String' -and $second[0].Value -ceq '00000409'
        PreloadOtherValues = Same (Other-Preload $expected) (Other-Preload $actual)
        Substitutes = Same $expected.Substitutes $actual.Substitutes
        BaselineExact = Same $expected $actual
    }
}

try {
    Require ([Environment]::Is64BitProcess -and $PSVersionTable.PSEdition -ceq 'Desktop') 'Host.WindowsPowerShell64'
    Import-Module -Name (Join-Path $PSHOME 'Modules\International\International.psd1') -ErrorAction Stop
    $report.Stage = 'Source.Read'
    $baseline = (ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $source 'baseline.json')))).Input
    $historicalAfter = (ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $source 'after.json')))).Input
    $historicalFinal = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $source 'final-input.json')))
    $report['SourceHashes'] = @(foreach ($name in @('baseline.json', 'after.json', 'final-input.json')) {
        [ordered]@{ Name = $name; Sha256 = (Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash }
    })
    Require (Same $historicalAfter $historicalFinal) 'Source.AfterEqualsFinal'
    Require ((Same @('zh-Hans-CN', 'zh-Hant-HK') @($baseline.Languages.LanguageTag)) -and
        (Same @($pinyin) @($baseline.Languages[0].InputMethodTips)) -and
        (Same @() @($baseline.Languages[1].InputMethodTips))) 'Source.Languages'
    Require ((Same @('1', '2') @($baseline.Preload.Values.Name)) -and
        $baseline.Preload.Exists -and $baseline.Preload.Values[0].Kind -ceq 'String' -and
        $baseline.Preload.Values[0].Value -ceq '00000804' -and
        $baseline.Preload.Values[1].Kind -ceq 'String' -and
        $baseline.Preload.Values[1].Value -ceq '00000409') 'Source.Preload'
    # The entire historical diff must be precisely these two fields.
    $expectedAfter = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $baseline -Depth 16)
    $expectedAfter.Languages[1].InputMethodTips = @($addedTip)
    $expectedAfter.Preload.Values[1].Value = '00000404'
    Require (Same $expectedAfter $historicalAfter) 'Source.ExactAuthorizedDiff'
    Save 'authorized-diff.json' @(
        [ordered]@{ Field = 'Languages.zh-Hant-HK.InputMethodTips'; Before = @(); After = @($addedTip); Action = 'InstallLayoutOrTip'; Flags = 1 },
        [ordered]@{ Field = 'HKCU.Keyboard Layout.Preload.2'; Before = $baseline.Preload.Values[1]; After = $historicalAfter.Preload.Values[1]; Action = 'Existing REG_SZ only if still matches after' }
    )
    $before = Read-Input
    Save 'before.json' $before
    $report.Preflight = [ordered]@{
        HistoricalAfterEqualsFinal = Same $historicalAfter $historicalFinal
        CurrentMatchesAfter = Same-ExceptOwn $historicalAfter $before.Input
        DefaultRegistryUnchanged = Same $baseline.DefaultRegistry $before.Input.DefaultRegistry
        DefaultCmdletUnchanged = Same $baseline.DefaultCmdlet $before.Input.DefaultCmdlet
        LanguageTagsUnchanged = Same @($baseline.Languages.LanguageTag) @($before.Input.Languages.LanguageTag)
        MainlandTipsUnchanged = Same $baseline.Languages[0] $before.Input.Languages[0]
        HongKongMatchesAfter = Same $historicalAfter.Languages[1] $before.Input.Languages[1]
        PreloadMatchesAfter = Same $historicalAfter.Preload $before.Input.Preload
        SubstitutesUnchanged = Same $baseline.Substitutes $before.Input.Substitutes
    }
    Save 'report.json' $report
    Require $report.Preflight.CurrentMatchesAfter 'Preflight.CurrentMatchesAfterExceptOwn'
    if ($Restore) {
        $report.Stage = 'Api.Prepare'
        # Interop only, compiled inside this process; no native/C# project edits.
        # BOOL CALLBACK(LPCWSTR, DWORD); flag 1 only. Never default/clean-install.
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public sealed class VoiceImeBaselineInputApi : IDisposable {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", ExactSpelling=true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    delegate bool InstallLayoutOrTip([MarshalAs(UnmanagedType.LPWStr)] string psz, uint dwFlags);
    IntPtr module;
    InstallLayoutOrTip uninstall;
    public VoiceImeBaselineInputApi() {
        module = LoadLibraryExW(Path.Combine(Environment.SystemDirectory, "input.dll"), IntPtr.Zero, 0x00000800);
        if (module == IntPtr.Zero) throw new InvalidOperationException("SystemInputDllUnavailable");
        IntPtr address = GetProcAddress(module, "InstallLayoutOrTip");
        if (address == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("InstallLayoutOrTipUnavailable"); }
        uninstall = (InstallLayoutOrTip)Marshal.GetDelegateForFunctionPointer(address, typeof(InstallLayoutOrTip));
    }
    public bool UninstallQuickTip() {
        return uninstall("0404:{531FDEBF-9B4C-4A43-A2AA-960E8FCDC732}{6024B45F-5C54-11D4-B921-0080C882687E}", 1u);
    }
    public void Dispose() {
        if (module != IntPtr.Zero) { FreeLibrary(module); module = IntPtr.Zero; }
    }
}
'@
        $api = [VoiceImeBaselineInputApi]::new()
        $recheck = Read-Input
        Save 'before-api.json' $recheck
        Require ((Same $before.Input $recheck.Input) -and (Same $before.ObservedMetadata $recheck.ObservedMetadata)) 'Preflight.ImmediateRecheck'
        $report.Stage = 'Api.UninstallQuickTip'
        $report.UninstallInvoked = $true
        Save 'report.json' $report
        $report.UninstallResult = $api.UninstallQuickTip()
        $after = Read-Input
        Save 'after-api.json' $after
        Save 'report.json' $report
        Require $report.UninstallResult 'Api.UninstallReturnedTrue'
        # Success is not inferred from BOOL. Permit only a remaining Preload/2 diff.
        $expectedAfterApi = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $baseline -Depth 16)
        $expectedAfterApi.Preload.Values[1].Value = $historicalAfter.Preload.Values[1].Value
        Require (((Same-ExceptOwn $baseline $after.Input) -or (Same-ExceptOwn $expectedAfterApi $after.Input)) -and
            (Same (Own-Tips $before.Input) (Own-Tips $after.Input)) -and
            (Same $before.ObservedMetadata $after.ObservedMetadata)) 'Api.OnlyAuthorizedPreloadDiffRemains'
        if (-not (Same $baseline.Preload $after.Input.Preload)) {
            $recheck = Read-Input
            Save 'before-preload.json' $recheck
            Require ((Same $after.Input $recheck.Input) -and
                (Same $before.ObservedMetadata $recheck.ObservedMetadata)) 'Preload.ImmediateRecheck'
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
            $key = $null
            try {
                $key = $base.OpenSubKey('Keyboard Layout\Preload', $true)
                Require ($null -ne $key -and $key.GetValueNames() -contains '2' -and
                    $key.GetValueKind('2') -eq [Microsoft.Win32.RegistryValueKind]::String -and
                    $key.GetValue('2', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -ceq $historicalAfter.Preload.Values[1].Value) 'Preload.ExistingSecondValueMatchesAfter'
                $report.Stage = 'Preload.RestoreExistingRegSz'
                $report.PreloadWriteAttempted = $true
                Save 'report.json' $report
                $key.SetValue('2', [string]$baseline.Preload.Values[1].Value, [Microsoft.Win32.RegistryValueKind]::String)
                $key.Flush()
                $report.PreloadWriteConfirmed = $key.GetValueKind('2') -eq [Microsoft.Win32.RegistryValueKind]::String -and
                    $key.GetValue('2', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -ceq $baseline.Preload.Values[1].Value
            }
            finally { if ($null -ne $key) { $key.Dispose() }; $base.Dispose() }
            Require $report.PreloadWriteConfirmed 'Preload.WriteReadback'
        }
        $after = Read-Input
        Save 'after.json' $after
        $report.Results = Compare-Input $baseline $after.Input
        $report.Results['ObservedHandwritingUnchanged'] = Same $before.ObservedMetadata $after.ObservedMetadata
        $report.Results['NonOwnSettingsBaselineExact'] = Same-ExceptOwn $baseline $after.Input
        $report.Results['OwnTipPreserved'] = Same (Own-Tips $before.Input) (Own-Tips $after.Input)
        Require ($report.Results.NonOwnSettingsBaselineExact -and $report.Results.OwnTipPreserved -and
            $report.Results.ObservedHandwritingUnchanged) 'Final.BaselineExceptOwnExact'
        $report.Stage = 'RestoredAndVerified'
    }
    else { $report.Stage = 'ReadOnlyMatchedAfter' }
    $report.ExitCode = 0
}
catch {
    # Do not echo exception messages that might contain user data. Stage is fixed.
    $report['Failure'] = [ordered]@{ Stage = $report.Stage; Type = $_.Exception.GetType().FullName; ScriptLine = $_.InvocationInfo.ScriptLineNumber }
}
finally {
    if ($null -ne $api) { $api.Dispose() }
    if ($null -ne $before) {
        try {
            $after = Read-Input
            Save 'after.json' $after
            $report.Results = Compare-Input $baseline $after.Input
            $report.Results['ObservedHandwritingUnchanged'] = Same $before.ObservedMetadata $after.ObservedMetadata
            $report.Results['NonOwnSettingsBaselineExact'] = Same-ExceptOwn $baseline $after.Input
            $report.Results['OwnTipPreserved'] = Same (Own-Tips $before.Input) (Own-Tips $after.Input)
            if ($Restore -and (-not $report.Results.NonOwnSettingsBaselineExact -or -not $report.Results.OwnTipPreserved -or
                -not $report.Results.ObservedHandwritingUnchanged)) { $report.ExitCode = 1 }
        }
        catch { $report['FinalSnapshotSucceeded'] = $false; $report.ExitCode = 1 }
    }
    $report['CompletedAt'] = [DateTimeOffset]::Now.ToString('o')
    Save 'report.json' $report
    [IO.File]::WriteAllText((Join-Path $artifacts 'exit-code.txt'), [string]$report.ExitCode, $utf8)
    if ($null -ne $report.Preflight) { foreach ($name in $report.Preflight.Keys) { Write-Output ($name + '=' + $report.Preflight[$name]) } }
    if ($null -ne $report.UninstallResult) { Write-Output ('UninstallResult=' + $report.UninstallResult) }
    Write-Output ('PreloadWriteConfirmed=' + $report.PreloadWriteConfirmed)
    if ($null -ne $report.Results) { foreach ($name in $report.Results.Keys) { Write-Output ($name + '=' + $report.Results[$name]) } }
    Write-Output ('SpellingWritten=' + $report.SpellingWritten)
    Write-Output ('Stage=' + $report.Stage)
    Write-Output ('Report=' + $reportPath)
    Write-Output ('ExitCode=' + $report.ExitCode)
}
exit $report.ExitCode