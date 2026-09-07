#Requires -Version 7.0
# 六个发布版离线场景；只用回环地址和合成密钥，不读取真实配置或发送云请求。
[CmdletBinding()]
param([string]$ExePath = (Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/publish/gpt-image-cli/gpt-image.exe'))

$ErrorActionPreference = 'Stop'
try {
    $exe = (Resolve-Path -LiteralPath $ExePath).Path
    $root = Join-Path $PSScriptRoot ('bin/auth-source-check/' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $configKey = 'synthetic-node-key'
    $externalKey = 'synthetic-unrelated-key'
    $png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7ioAAAAASUVORK5CYII='
    foreach ($scenario in @('name-apim-401', 'id-openai-explicit', 'environment', 'plaintext', 'missing-key', 'unknown-node')) {
        $selected = $scenario -notin @('environment', 'plaintext')
        $rejected = $scenario -in @('missing-key', 'unknown-node')
        $bearer = $scenario -eq 'id-openai-explicit'
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $process = [Diagnostics.Process]::new()
        $client = $null
        $started = $false
        try {
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $configPath = Join-Path $root 'config.json'
            @{
                Endpoints = @(@{ Id='offline'; Name='离线节点'; EndpointType=$(if ($bearer) { 0 } else { 2 }); AuthMode=0; ApiKeyHeaderMode=$(if ($bearer) { 2 } else { 1 })
                    ProfileId=$(if ($bearer) { 'builtin.openai.compatible' } else { 'builtin.microsoft.apim-gateway' })
                    BaseUrl="http://127.0.0.1:$port"; ApiKey=$(if ($scenario -eq 'missing-key') { '' } else { $configKey })
                    Models=@(@{ModelId='gpt-image-2'; Capabilities=2}) })
            } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding utf8
            $before = (Get-FileHash -LiteralPath $configPath).Hash
            $start = [Diagnostics.ProcessStartInfo]::new($exe)
            $start.UseShellExecute = $false
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.StandardOutputEncoding = [Text.Encoding]::UTF8
            $start.StandardErrorEncoding = [Text.Encoding]::UTF8
            $start.WorkingDirectory = $root
            foreach ($name in @('GPT_IMAGE_ENDPOINT','OPENAI_BASE_URL','AZURE_OPENAI_ENDPOINT',
                'GPT_IMAGE_API_KEY','OPENAI_API_KEY','AZURE_OPENAI_API_KEY',
                'GPT_IMAGE_MODEL','GPT_IMAGE_TEXT_MODEL','AZURE_OPENAI_API_VERSION')) {
                $null = $start.Environment.Remove($name)
            }
            # .NET NO_PROXY 不支持 *；明确绕过回环，其他目标仅允许走本地代理地址。
            foreach ($proxy in @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY')) {
                $start.Environment[$proxy] = 'http://127.0.0.1:9'
            }
            $start.Environment['NO_PROXY'] = '127.0.0.1,localhost'
            $start.Environment['OPENAI_API_KEY'] = $externalKey
            $start.Environment['OPENAI_BASE_URL'] = "http://127.0.0.1:$port/external"
            $cliArgs = @('--prompt','offline','--json')
            if ($selected) {
                $cliArgs += @('--config',$configPath)
                if ($bearer) { $cliArgs += @('--endpoint-id','offline') }
                else { $cliArgs += @('--endpoint-name',$(if ($scenario -eq 'unknown-node') { '不存在' } else { '离线节点' })) }
                if ($scenario -ne 'name-apim-401') {
                    $cliArgs += @('--endpoint',"http://127.0.0.1:$port/plaintext",'--api-key',$externalKey)
                }
            } else {
                $cliArgs += @('--no-config','--auth','api-key')
                if ($scenario -eq 'plaintext') {
                    $cliArgs += @('--endpoint',"http://127.0.0.1:$port/plaintext",'--api-key',$configKey)
                }
            }
            foreach ($arg in $cliArgs) {
                $start.ArgumentList.Add($arg)
            }
            $process.StartInfo = $start
            $started = $process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if ($rejected) {
                if (-not $process.WaitForExit(10000)) { throw '配置错误时CLI未退出' }
                $output = $stdout.GetAwaiter().GetResult()
                $errorText = $stderr.GetAwaiter().GetResult()
                $report = $output | ConvertFrom-Json
                if ($process.ExitCode -ne 2 -or $report.ok -or $null -ne $report.http_status -or $listener.Pending() -or
                    ($output + $errorText).Contains($configKey) -or ($output + $errorText).Contains($externalKey) -or
                    (Get-FileHash -LiteralPath $configPath).Hash -ne $before) { throw '配置失败未安全停止' }
                Write-Host "PASS: $scenario; exit=2; no HTTP; secrets hidden; config unchanged"
                continue
            }
            $accept = $listener.AcceptTcpClientAsync()
            if (-not $accept.Wait(10000)) {
                $detail = if ($process.HasExited -and $stderr.IsCompleted) {
                    "exit=$($process.ExitCode); " + $stderr.GetAwaiter().GetResult().Replace($configKey, '[已隐藏]').Replace($externalKey, '[已隐藏]')
                } else { '子进程尚未退出' }
                throw "CLI未连接离线服务器：$scenario; $detail"
            }
            $client = $accept.Result
            $stream = $client.GetStream()
            $stream.ReadTimeout = 10000
            $stream.WriteTimeout = 10000
            $header = [Collections.Generic.List[byte]]::new()
            do {
                $byte = $stream.ReadByte()
                if ($byte -lt 0 -or $header.Count -gt 32768) { throw '无效请求头' }
                $header.Add([byte]$byte)
                $text = [Text.Encoding]::ASCII.GetString($header.ToArray())
            } until ($text.EndsWith("`r`n`r`n"))
            $length = [int][regex]::Match($text, '(?im)^Content-Length:\s*(\d+)').Groups[1].Value
            $body = [byte[]]::new($length)
            $stream.ReadExactly($body)
            $expectedKey = if ($scenario -eq 'environment') { $externalKey } else { $configKey }
            $expectedHeader = if ($bearer) { "Authorization: Bearer $expectedKey`r`n" } else { "api-key: $expectedKey`r`n" }
            $unexpectedHeader = if ($bearer) { 'api-key:' } else { 'Authorization:' }
            $prefix = if ($selected) { '' } elseif ($scenario -eq 'environment') { '/external' } else { '/plaintext' }
            if (-not $text.Contains($expectedHeader) -or $text.Contains($unexpectedHeader) -or
                -not $text.StartsWith("POST $prefix/v1/images/generations HTTP/1.1`r`n")) {
                throw '认证头或密钥来源不符合预期'
            }
            $unauthorized = $scenario -eq 'name-apim-401'
            $status = if ($unauthorized) { '401 Unauthorized' } else { '200 OK' }
            $response = if ($unauthorized) { '{"error":{"code":"invalid_key"}}' } else { '{"data":[{"b64_json":"' + $png + '"}]}' }
            $bytes = [Text.Encoding]::UTF8.GetBytes($response)
            $headers = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: application/json`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n")
            $stream.Write($headers)
            $stream.Write($bytes)
            $client.Dispose()
            $client = $null
            if (-not $process.WaitForExit(10000)) { throw 'CLI未在离线响应后退出' }
            $output = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            $report = $output | ConvertFrom-Json
            if (($output + $errorText).Contains($configKey) -or ($output + $errorText).Contains($externalKey)) { throw '输出泄漏合成密钥' }
            if ($unauthorized) {
                if ($process.ExitCode -ne 1 -or $report.http_status -ne 401 -or $report.ok -or $report.files.Count -ne 0 -or
                    -not $report.error.Contains('密钥来源：节点配置') -or -not $errorText.Contains('密钥来源：节点配置') -or
                    $errorText.Contains('OPENAI_API_KEY')) {
                    throw '401节点来源或UTF-8中文提示验收失败'
                }
            } else {
                if ($process.ExitCode -ne 0 -or -not $report.ok -or $report.files.Count -ne 1 -or
                    [Convert]::ToBase64String([IO.File]::ReadAllBytes($report.files[0])) -ne $png -or $errorText.Contains('OPENAI_API_KEY')) { throw '连接来源或图片落盘场景失败' }
            }
            if ((Get-FileHash -LiteralPath $configPath).Hash -ne $before) { throw '配置被修改' }
                    if ($listener.Pending()) { throw 'CLI发出了额外请求' }
                    Write-Host "PASS: $scenario; HTTP=$($report.http_status); exit=$($process.ExitCode); secrets hidden; config unchanged"
        } finally {
            if ($client) { $client.Dispose() }
            $listener.Stop()
            if ($started -and -not $process.HasExited) { $process.Kill($true) }
            $process.Dispose()
        }
    }
    Write-Host '仅完成六个离线连接来源场景；不代表云端生图成功。'
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}