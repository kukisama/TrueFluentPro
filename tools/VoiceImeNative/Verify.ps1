[CmdletBinding()]
param([string]$ArtifactDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
$pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source
$buildScript = Join-Path $PSScriptRoot 'Build.ps1'
$artifacts = [IO.Path]::GetFullPath($ArtifactDirectory)
$allowed = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not ($artifacts + [IO.Path]::DirectorySeparatorChar).StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) {
    throw '验证输出必须位于 VoiceImeNative\artifacts 下。'
}
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$report = [ordered]@{ GeneratedUtc = [DateTime]::UtcNow.ToString('o'); Builds = @{}; Tests = @{}; Artifacts = @() }
$exitCode = 1 # Cancellation can execute finally without entering catch: incomplete is never success.
try {
    foreach ($configuration in @('Debug', 'Release')) {
        $out = Join-Path $artifacts $configuration
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        $log = Join-Path $out 'build-final.log'
        $global:LASTEXITCODE = $null
        & $pwsh -NoProfile -File $buildScript -OutputDirectory $out -Configuration $configuration *> $log
        $code = $global:LASTEXITCODE
        $report.Builds[$configuration] = @{ ExitCode = $code; Log = $log }
        Write-Output "BUILD $configuration exit=$code"
        if ($null -eq $code -or $code -ne 0) { throw "编译失败，详见 $log" }
        foreach ($arch in @('win-x64', 'win-x86')) {
            $archOut = Join-Path $out $arch
            $dll = Join-Path $archOut 'TrueFluentVoiceIme.dll'
            $exe = Join-Path $archOut 'VoiceImeTests.exe'
            $log = Join-Path $out "test-final-$arch.log"
            $global:LASTEXITCODE = $null
            & $exe $dll *> $log
            $code = $global:LASTEXITCODE
            $report.Tests["$configuration-$arch"] = @{ ExitCode = $code; Log = $log }
            Write-Output "TEST $configuration $arch exit=$code"
            Get-Content -LiteralPath $log | Select-Object -Last 1
            if ($null -eq $code -or $code -ne 0) { throw "离线测试失败，详见 $log" }
            $log = Join-Path $out "registration-policy-$arch.log"
            & (Join-Path $archOut 'VoiceImeRegistrationTests.exe') *> $log
            $code = $global:LASTEXITCODE
            $report.Tests["$configuration-$arch-policy"] = @{ ExitCode = $code; Log = $log }
            Write-Output "POLICY $configuration $arch exit=$code"
            Get-Content -LiteralPath $log | Select-Object -Last 1
            if ($null -eq $code -or $code -ne 0) { throw "内存安装策略测试失败，详见 $log" }
            $fixture = Join-Path $out 'maintenance-fixture'
            New-Item -ItemType Directory -Force -Path $fixture | Out-Null
            $presentMarker = Join-Path $fixture 'maintenance.lock'
            $absentMarker = Join-Path $fixture ('absent-' + [Guid]::NewGuid().ToString('N') + '.lock')
            [IO.File]::WriteAllBytes($presentMarker, [byte[]]@())
            $log = Join-Path $out "lifecycle-$arch.log"
            & (Join-Path $archOut 'VoiceImeLifecycleTests.exe') $absentMarker $presentMarker *> $log
            $code = $global:LASTEXITCODE
            $report.Tests["$configuration-$arch-lifecycle"] = @{ ExitCode = $code; Log = $log }
            Write-Output "LIFECYCLE $configuration $arch exit=$code"
            Get-Content -LiteralPath $log | Select-Object -Last 1
            if ($null -eq $code -or $code -ne 0) { throw "离线维护门禁测试失败，详见 $log" }
            foreach ($name in @('TrueFluentVoiceIme.dll', 'VoiceImeRegister.exe', 'VoiceImeTests.exe')) {
                $path = Join-Path $archOut $name
                $file = Get-Item -LiteralPath $path
                $bytes = [IO.File]::ReadAllBytes($path)
                $pe = [BitConverter]::ToInt32($bytes, 0x3c)
                $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
                $report.Artifacts += @{ Path = $path; Length = $file.Length; LastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('o'); PEMachine = ('0x{0:X4}' -f $machine) }
            }
        }
    }
    $out = Join-Path $artifacts 'Release'
    $log = Join-Path $out 'incremental-final.log'
    & $pwsh -NoProfile -File $buildScript -OutputDirectory $out -Configuration Release *> $log
    $report.IncrementalReleaseExitCode = $global:LASTEXITCODE
    if ($report.IncrementalReleaseExitCode -ne 0) { throw '增量构建失败。' }
    Get-Content -LiteralPath $log
    $log = Join-Path $artifacts 'relative-output-rejection.log'
    & $pwsh -NoProfile -File $buildScript -OutputDirectory 'relative-output' -Configuration Release *> $log
    $report.ExpectedRelativePathRejection = @{ ExitCode = $global:LASTEXITCODE; Log = $log }
    if ($global:LASTEXITCODE -eq 0) { throw '错误接受了相对输出目录。' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $msvcRoot = Join-Path $vs 'VC\Tools\MSVC'
    $msvc = Get-ChildItem -LiteralPath $msvcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $dumpbin = Join-Path $msvc.FullName 'bin\Hostx64\x64\dumpbin.exe'
    $dependencyLog = Join-Path $artifacts 'dependencies.log'
    Set-Content -LiteralPath $dependencyLog -Value "dumpbin: $dumpbin" -Encoding UTF8
    foreach ($arch in @('win-x64', 'win-x86')) {
        foreach ($name in @('TrueFluentVoiceIme.dll', 'VoiceImeRegister.exe')) {
            $path = [IO.Path]::Combine($out, $arch, $name)
            Add-Content -LiteralPath $dependencyLog -Value "=== $path ==="
            & $dumpbin /dependents $path >> $dependencyLog
            if ($global:LASTEXITCODE -ne 0) { throw '导入表读取失败。' }
        }
    }
    $report.DependenciesLog = $dependencyLog
    $report.DynamicVcRuntimeImports = @(Select-String -LiteralPath $dependencyLog -Pattern '(?i)\b(VCRUNTIME|MSVCP|ucrtbased)[^\s]*\.dll\b' | ForEach-Object { $_.Line.Trim() })
    if ($report.DynamicVcRuntimeImports.Count) { throw '检测到非预期的动态 VC 运行库依赖。' }
    $exitCode = 0
} catch {
    $exitCode = 1
    $report.Failure = $_.Exception.Message
    Write-Output "VALIDATION FAILED: $($report.Failure)"
} finally {
    $report.ExitCode = $exitCode
    $report.Sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in '.cpp', '.h', '.def', '.ps1' } |
        ForEach-Object { @{ Name = $_.Name; LastWriteTimeUtc = $_.LastWriteTimeUtc.ToString('o') } })
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts 'validation.json') -Encoding UTF8
}
exit $exitCode