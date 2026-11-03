$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $repo
New-Item -ItemType Directory -Force artifacts | Out-Null
Start-Transcript -Path (Join-Path $repo 'artifacts/voice-ime-registration-release.log') -Force
try {
    Get-Process TrueFluentPro, Updater -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction Continue
    dotnet build TrueFluentPro.csproj -c Release -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
    foreach ($arch in @('win-x64', 'win-x86')) {
        $dir = Join-Path $repo "bin/Release/net10.0/voice-ime/$arch"
        & (Join-Path $dir 'VoiceImeRegistrationTests.exe')
        if ($LASTEXITCODE -ne 0) { throw "Policy test failed: $arch" }
        & (Join-Path $dir 'VoiceImeTests.exe') (Join-Path $dir 'TrueFluentVoiceIme.dll')
        if ($LASTEXITCODE -ne 0) { throw "COM test failed: $arch" }
    }
    dotnet build tools/VoiceImeRegression/VoiceImeRegression.csproj -c Release -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Regression build failed' }
    & ./tools/VoiceImeRegression/bin/Release/net10.0/VoiceImeRegression.exe
    if ($LASTEXITCODE -ne 0) { throw 'Regression failed' }
    & ./tools/VoiceImeRegression/bin/Release/net10.0/VoiceImeRegression.exe --lifecycle
    if ($LASTEXITCODE -ne 0) { throw 'Lifecycle regression failed' }
    Get-Item bin/Release/net10.0/TrueFluentPro.exe,
        bin/Release/net10.0/TrueFluentPro.dll,
        bin/Release/net10.0/voice-ime/win-x64/VoiceImeRegister.exe,
        bin/Release/net10.0/voice-ime/win-x86/VoiceImeRegister.exe |
        Select-Object Name, LastWriteTime, Length | Format-Table -AutoSize
    Write-Output ('__VERIFIED_RELEASE__ ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
}
finally { Stop-Transcript }