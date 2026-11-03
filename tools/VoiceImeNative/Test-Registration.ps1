[CmdletBinding()]
param(
    [string]$BinaryDirectory = (Join-Path $PSScriptRoot 'artifacts\Release'),
    [switch]$VerifyNonAdminRefusal,
    [switch]$ExerciseRegistration,
    [switch]$OfficialApi
)
$ErrorActionPreference = 'Stop'
if ($ExerciseRegistration -or $OfficialApi) { throw '真实登记 exercise 已移除；本脚本仅只读。管理员安装由用户单独执行。' }
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($VerifyNonAdminRefusal -and $elevated) { throw '拒绝验证只能在非管理员进程中运行。' }
$out = Join-Path $PSScriptRoot 'artifacts\readonly-registration.json'
$report = [ordered]@{ GeneratedUtc = [DateTime]::UtcNow.ToString('o'); Elevated = $elevated; Commands = @() }
function Invoke-ReadOnlyHelper([string]$exe, [string[]]$arguments) {
    $global:LASTEXITCODE = $null
    $text = @(& $exe @arguments 2>&1 | ForEach-Object { [string]$_ })
    $code = $global:LASTEXITCODE
    $report.Commands += [ordered]@{ Exe = $exe; Arguments = $arguments; ExitCode = $code; Output = $text }
    if ($null -eq $code) { throw '未获得原生命令退出码。' }
    return [pscustomobject]@{ Text = $text; Code = $code }
}
foreach ($arch in @('win-x64', 'win-x86')) {
    $dir = Join-Path $BinaryDirectory $arch
    $exe = Join-Path $dir 'VoiceImeRegister.exe'
    $dll = Join-Path $dir 'TrueFluentVoiceIme.dll'
    $status = Invoke-ReadOnlyHelper $exe @('--status', $dll)
    if ($status.Code -notin @(0, 1) -or $status.Text.Count -ne 1) { throw '状态查询失败或不是单行 JSON。' }
    $json = $status.Text[0] | ConvertFrom-Json
    if ($json.registered -isnot [bool] -or $json.requiresAdmin -isnot [bool] -or
        $json.state -notin @('registered', 'unregistered', 'partial', 'different-path', 'legacy-user') -or
        $json.registered -ne ($json.state -eq 'registered') -or ($status.Code -eq 0) -ne $json.registered) { throw '状态 JSON 违反正式契约。' }
    $probe = Invoke-ReadOnlyHelper $exe @('--probe')
    if ($probe.Code -notin @(0, 1)) { throw '只读枚举失败。' }
    Write-Output "$arch status=$($status.Code) probe=$($probe.Code) state=$($json.state)"
    if ($VerifyNonAdminRefusal) {
        if ($json.state -ne 'unregistered' -or $probe.Code -ne 1) { throw '非管理员拒绝验证要求无任何既有登记且枚举为空。' }
        # Both arguments are local fixture binaries; helper exits at the elevation gate before loading either.
        $denied = Invoke-ReadOnlyHelper $exe @('--register', $dll, (Join-Path $dir 'VoiceImeTests.exe'))
        if ($denied.Code -ne 10 -or ($denied.Text -join '') -ne 'failed stage=RequiresAdmin HRESULT=0x800702E4') { throw '非管理员登记未按零写入契约拒绝。' }
        Write-Output "$arch non-admin-refusal=$($denied.Code)"
        $after = Invoke-ReadOnlyHelper $exe @('--status', $dll)
        if ($after.Code -ne $status.Code -or ($after.Text -join '') -ne ($status.Text -join '')) { throw '拒绝前后状态不一致。' }
    }
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $out -Encoding UTF8
Write-Output "REPORT=$out"
exit 0