#Requires -Version 7.0
# 只测试本地列表与请求前拒绝；不读取真实配置，不修改代理或环境，不访问图片接口。
[CmdletBinding()]
param([string]$ExePath = (Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/publish/gpt-image-cli/gpt-image.exe'))
$ErrorActionPreference = 'Stop'
try {
    $exe = (Resolve-Path -LiteralPath $ExePath).Path
    $root = Join-Path $PSScriptRoot ('bin/endpoint-diagnostics/' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $root
    $path = Join-Path $root 'config.json'
    $secret = 'synthetic-diagnostic-secret'
    $nodes = foreach ($id in @('valid','unmarked','disabled','empty','speech','private','duplicate1','duplicate2')) {
        @{ Id=$id; Name=$(if ($id -eq 'private') { $secret } elseif ($id.StartsWith('duplicate')) { '同名节点' } else { $id })
            IsEnabled=($id -ne 'disabled'); EndpointType=$(if ($id -eq 'speech') { 3 } else { 2 })
            BaseUrl='not-a-network-url'; ApiKey=$secret
            Models=@(if ($id -ne 'empty') { @{ ModelId='gpt-image-2'; Capabilities=$(if ($id -in @('unmarked','private')) { 0 } else { 2 }) } })
        }
    }
    @{ Endpoints=@($nodes) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    $before = (Get-FileHash -LiteralPath $path).Hash
    $cases = @(
        @{ Name='list-json'; Args=@('--list-endpoints','--json'); Exit=0 },
        @{ Name='list-text'; Args=@('--list-endpoints'); Exit=0 },
        @{ Name='unmarked'; Text='节点存在，但没有配置有效的图片能力模型'; Exit=2 },
        @{ Name='disabled'; Text='节点未启用'; Exit=2 },
        @{ Name='empty'; Text='节点尚未配置模型'; Exit=2 },
        @{ Name='speech'; Text='节点类型不适用'; Exit=2 },
        @{ Name='missing'; Text='未找到指定节点'; Exit=2 },
        @{ Name='同名节点'; Text='多个同名节点'; Exit=2 }
    )
    foreach ($case in $cases) {
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.StandardOutputEncoding = [Text.Encoding]::UTF8
        $start.StandardErrorEncoding = [Text.Encoding]::UTF8
        $cliArgs = @('--config',$path)
        if ($case.Args) { $cliArgs += $case.Args }
        else { $cliArgs += @('--endpoint-name',$case.Name,'--prompt','offline','--json') }
        foreach ($arg in $cliArgs) { $start.ArgumentList.Add($arg) }
        $process = [Diagnostics.Process]::new()
        $started = $false
        try {
            $process.StartInfo = $start
            $started = $process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(10000)) { throw '本地诊断未及时退出' }
            $output = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -ne $case.Exit -or ($output + $errorText).Contains($secret) -or
                ($output + $errorText).Contains('not-a-network-url') -or $errorText.Contains('POST ')) { throw "退出码或隐私检查失败：$($case.Name)" }
            if ($case.Name -like 'list-*') {
                if (-not $errorText.Contains('未列出节点「unmarked」') -or -not $errorText.Contains('勾选图片生成能力') -or
                    -not $errorText.Contains('未列出节点「[已隐藏]」')) { throw '缺少列表原因或脱敏提示' }
            }
            if ($case.Name -ne 'list-text') {
                $doc = [Text.Json.JsonDocument]::Parse([string]$output)
                $doc.Dispose()
                $report = $output | ConvertFrom-Json
                if ($null -ne $report.http_status -or $report.files.Count -ne 0) { throw '本地诊断不应产生HTTP或文件' }
                if ($case.Exit -eq 0) {
                    if (-not $report.ok -or $report.endpoints.Count -ne 3) { throw '列表筛选行为变化' }
                } elseif ($report.ok -or -not $report.error.Contains($case.Text) -or -not $errorText.Contains($case.Text) -or
                    -not $report.error.Contains('未发送请求')) { throw "JSON与stderr原因不明确：$($case.Name)" }
            } elseif (-not $output.Contains('valid') -or $output.Contains('unmarked')) { throw '文本列表被诊断污染' }
            Write-Host "PASS: $($case.Name)"
        } finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true) }
            $process.Dispose()
        }
    }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $before) { throw '测试配置被CLI修改' }
    Write-Host 'PASS: 8个本地提示场景；配置未变，未修改代理，未发送生图请求。'
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}