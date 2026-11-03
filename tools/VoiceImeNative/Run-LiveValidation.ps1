[CmdletBinding()]
param([ValidateSet('Baseline')][string]$Mode = 'Baseline')
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'artifacts\Live'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$report = [ordered]@{ Mode = $Mode; StartedUtc = [DateTime]::UtcNow.ToString('o') }
$code = 1
try {
    & (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $out -Configuration Debug *> (Join-Path $out 'build-latest.log')
    $report.BuildExit = $global:LASTEXITCODE
    if ($report.BuildExit -ne 0) { throw '构建失败。' }
    $report.Tests = @(foreach ($arch in @('win-x64', 'win-x86')) {
        $dir = Join-Path $out $arch
        $log = Join-Path $out "$stamp-$arch-offline.log"
        & (Join-Path $dir 'VoiceImeTests.exe') (Join-Path $dir 'TrueFluentVoiceIme.dll') *> $log
        [ordered]@{ Architecture = $arch; Kind = 'COM'; ExitCode = $global:LASTEXITCODE; Log = $log }
        $log = Join-Path $out "$stamp-$arch-edit.log"
        & (Join-Path $dir 'VoiceImeEditTests.exe') *> $log
        [ordered]@{ Architecture = $arch; Kind = 'TextStore'; ExitCode = $global:LASTEXITCODE; Log = $log }
    })
    if (@($report.Tests | Where-Object { $_.ExitCode -ne 0 }).Count) { throw '离线测试失败。' }
    $arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-Registration.ps1'))
    $arguments += @('-BinaryDirectory', $out) # Only status/probe; never register/unregister.
    $pwsh = (Get-Command pwsh -CommandType Application).Source
    & $pwsh @arguments *> (Join-Path $out "$Mode-latest.log")
    $code = $global:LASTEXITCODE
    $report.ValidationExit = $code
} catch { $report.Failure = $_.Exception.Message }
finally {
    $report.ExitCode = $code
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out "$Mode-exit.json") -Encoding UTF8
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out "$stamp-$Mode-exit.json") -Encoding UTF8
}
exit $code