param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$production = Join-Path $repo 'bin/Debug/net10.0/TrueFluentPro.dll'
$solution = Join-Path $repo 'TrueFluentPro.sln'
$evidence = Join-Path $PSScriptRoot ('artifacts/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$before = @{ production = (Get-FileHash -LiteralPath $production -Algorithm SHA256).Hash; solution = (Get-FileHash -LiteralPath $solution -Algorithm SHA256).Hash }
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'baseline.json') -Encoding utf8
Push-Location $repo
$code = 1
try {
    & dotnet build (Join-Path $PSScriptRoot 'VoiceImeUiRegression.csproj') --nologo -v minimal 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'build.log')
    $buildCode = $LASTEXITCODE
    "BUILD_EXIT_CODE=$buildCode" | Add-Content -LiteralPath (Join-Path $evidence 'build.log')
    if ($buildCode -eq 0) {
        & dotnet (Join-Path $PSScriptRoot 'bin/Debug/net10.0/VoiceImeUiRegression.dll') $evidence $production 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'desktop.log')
        $code = $LASTEXITCODE
    } else { $code = $buildCode }
} finally {
    $after = @{ production = (Get-FileHash -LiteralPath $production -Algorithm SHA256).Hash; solution = (Get-FileHash -LiteralPath $solution -Algorithm SHA256).Hash }
    $unchanged = $before.production -eq $after.production -and $before.solution -eq $after.solution
    @{ before = $before; after = $after; unchanged = $unchanged; processExitCode = $code } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'integrity.json') -Encoding utf8
    if (-not $unchanged) { $code = 1 }
    "EXIT_CODE=$code`nEVIDENCE=$evidence`nPRODUCTION_AND_SOLUTION_UNCHANGED=$unchanged" | Tee-Object -FilePath (Join-Path $evidence 'exit.txt')
    Pop-Location
}
exit $code