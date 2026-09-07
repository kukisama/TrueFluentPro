#Requires -Version 7.0
<#
只验证传入 exe 的离线行为；不构建、不发布、不读取真实配置、不验证云端兼容性。
旧场景强制 --no-config；新场景仅用临时 APPDATA/--config 沙盒和合成密钥。
证据永久保留在 artifacts/gpt-image-cli-published-tests/<GUID>。
#>
[CmdletBinding()]
param(
    [string]$ExePath = (Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/publish/gpt-image-cli/gpt-image.exe'),
    [ValidateRange(5, 120)][int]$TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$latin1 = [Text.Encoding]::GetEncoding(28591)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$ExePath = [IO.Path]::GetFullPath($ExePath, $PSScriptRoot)
$evidence = Join-Path $root ('artifacts/gpt-image-cli-published-tests/' + [guid]::NewGuid().ToString('D'))
$null = [IO.Directory]::CreateDirectory($evidence)
$checks = [Collections.Generic.List[object]]::new()
$cases = [Collections.Generic.List[string]]::new()
$fingerprint = $null
$failure = $null
$key = 'offline-only-' + [guid]::NewGuid().ToString('N')
$externalKey = 'offline-external-' + [guid]::NewGuid().ToString('N')
$prompt = '把帆船改为绿色，保留天空与倒影。'

function Await-Task($Task, [string]$Operation, [int]$Milliseconds = ($TimeoutSeconds * 1000)) {
    if (-not $Task.Wait($Milliseconds)) { throw "$Operation 超时（${Milliseconds}ms）" }
    $Task.GetAwaiter().GetResult()
}
function Write-Bytes([string]$Path, [byte[]]$Bytes) {
    $null = Await-Task ([IO.File]::WriteAllBytesAsync($Path, $Bytes)) "写证据 $Path"
}
function Read-Bytes([string]$Path) {
    return ,([byte[]](Await-Task ([IO.File]::ReadAllBytesAsync($Path)) "读取 $Path"))
}
function Write-Json([string]$Path, $Value) {
    Write-Bytes $Path ($utf8.GetBytes(($Value | ConvertTo-Json -Depth 40)))
}
function Check([bool]$Condition, [string]$Name) {
    $checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw "断言失败：$Name" }
}
function Same-Bytes([byte[]]$Actual, [byte[]]$Expected) {
    return [Convert]::ToBase64String($Actual) -ceq [Convert]::ToBase64String($Expected)
}
function Get-Fingerprint {
    $bytes = Read-Bytes $ExePath
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
    return [ordered]@{ path = $ExePath; bytes = $bytes.LongLength; sha256 = $hash }
}

# 每次网络读取共享同一个总截止时间；循环只消费已收到的协议字节，不 sleep/轮询。
function Read-Exact($Stream, [int]$Count, $Clock) {
    if ($Count -lt 0 -or $Count -gt 2MB) { throw 'HTTP 内容长度越界' }
    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $remaining = [int]($TimeoutSeconds * 1000 - $Clock.ElapsedMilliseconds)
        if ($remaining -le 0) { throw 'HTTP 读取超过总截止时间' }
        $n = Await-Task ($Stream.ReadAsync($buffer, $offset, $Count - $offset)) 'HTTP read' $remaining
        if ($n -eq 0) { throw 'HTTP 意外 EOF' }
        $offset += $n
    }
    return ,$buffer
}
function Read-HttpLine($Stream, $Clock) {
    $bytes = [Collections.Generic.List[byte]]::new()
    do {
        $one = Read-Exact $Stream 1 $Clock
        $bytes.Add($one[0])
        if ($bytes.Count -gt 32768) { throw 'HTTP 行过长' }
    } until ($bytes.Count -ge 2 -and $bytes[$bytes.Count - 2] -eq 13 -and $bytes[$bytes.Count - 1] -eq 10)
    return $latin1.GetString($bytes.ToArray(), 0, $bytes.Count - 2)
}
function Read-Request($Stream, [string]$Directory) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $line = Read-HttpLine $Stream $clock
    $rawHeaders = $line + "`r`n"
    $headers = @{}
    while (($header = Read-HttpLine $Stream $clock) -ne '') {
        $rawHeaders += $header + "`r`n"
        if ($rawHeaders.Length -gt 32768) { throw 'HTTP headers 过长' }
        $pair = $header.Split(':', 2)
        if ($pair.Count -ne 2) { throw 'HTTP header 无效' }
        $headers[$pair[0].Trim()] = $pair[1].Trim()
    }
    Write-Bytes (Join-Path $Directory 'request.headers.txt') ($latin1.GetBytes($rawHeaders + "`r`n"))
    if ($headers.ContainsKey('Transfer-Encoding')) {
        if ($headers['Transfer-Encoding'] -ne 'chunked') { throw '不支持的 HTTP framing' }
        $bodyStream = [IO.MemoryStream]::new()
        try {
            do {
                $chunkSize = [Convert]::ToInt32((Read-HttpLine $Stream $clock).Split(';')[0], 16)
                if ($chunkSize -lt 0 -or $bodyStream.Length + $chunkSize -gt 2MB) { throw 'HTTP body 过大' }
                if ($chunkSize -gt 0) {
                    $chunk = Read-Exact $Stream $chunkSize $clock
                    $bodyStream.Write($chunk, 0, $chunk.Length)
                    if ((Read-HttpLine $Stream $clock) -ne '') { throw 'chunk 缺少 CRLF' }
                }
            } until ($chunkSize -eq 0)
            $trailerLength = 0
            while (($trailer = Read-HttpLine $Stream $clock) -ne '') {
                $trailerLength += $trailer.Length + 2
                if ($trailerLength -gt 32768) { throw 'HTTP trailers 过长' }
            }
            $body = $bodyStream.ToArray()
        } finally { $bodyStream.Dispose() }
    } else {
        if (-not $headers.ContainsKey('Content-Length')) { throw 'HTTP 缺少 body framing' }
        $body = Read-Exact $Stream ([int]$headers['Content-Length']) $clock
    }
    Write-Bytes (Join-Path $Directory 'request.body.bin') $body
    return @{ line = $line; headers = $headers; body = $body }
}

function Invoke-Case([string]$Name, [string[]]$CliArgs, [int]$Status = 0, $Response = $null, [hashtable]$ConfigCase = $null) {
    $dir = Join-Path $evidence $Name
    $null = [IO.Directory]::CreateDirectory($dir)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse('127.0.0.1'), 0)
    $process = [Diagnostics.Process]::new()
    $client = $null; $stream = $null; $outTask = $null; $errTask = $null
    $started = $false; $request = $null; $connections = 0
    $configPath = $null; $configHash = $null; $configStamp = $null
    try {
        $listener.Start()
        $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        $endpoint = "http://127.0.0.1:$port"
        $psi = [Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $ExePath
        $psi.WorkingDirectory = $dir
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = $utf8
        $psi.StandardErrorEncoding = $utf8
        # 仅枚举变量名并删除，不读取/记录继承的真实配置值，不改变父进程环境。
        $removed = @($psi.Environment.Keys | Where-Object { $_ -match '^(GPT_IMAGE|OPENAI|AZURE_OPENAI)' })
        foreach ($nameToRemove in $removed) { $null = $psi.Environment.Remove($nameToRemove) }
        Check (@($psi.Environment.Keys | Where-Object { $_ -match '^(GPT_IMAGE|OPENAI|AZURE_OPENAI)' }).Count -eq 0) "$Name 环境清理"
        foreach ($proxy in @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY')) {
            foreach ($envName in @($psi.Environment.Keys | Where-Object { $_ -ieq $proxy })) {
                $null = $psi.Environment.Remove($envName)
            }
        }
        $psi.Environment['NO_PROXY'] = '*'
        $psi.Environment['DOTNET_EnableDiagnostics'] = '0'
        $sandbox = Join-Path $dir 'isolated-appdata'
        $null = [IO.Directory]::CreateDirectory($sandbox)
        $psi.Environment['APPDATA'] = $sandbox
        if ($null -eq $ConfigCase) {
            $psi.Environment['GPT_IMAGE_API_KEY'] = $key
            $psi.Environment['GPT_IMAGE_ENDPOINT'] = $endpoint
            $psi.ArgumentList.Add('--no-config')
        } else {
            $configDirectory = Join-Path $sandbox 'TrueFluentPro'
            $null = [IO.Directory]::CreateDirectory($configDirectory)
            $configPath = Join-Path $configDirectory 'config.json'
            $type = if ($ConfigCase.ContainsKey('Type')) { [int]$ConfigCase.Type } else { 0 }
            $header = if ($ConfigCase.ContainsKey('Header')) { [int]$ConfigCase.Header } else { 0 }
            $node = @{
                Id = 'offline-node'; Name = '离线友好节点'; IsEnabled = $true; EndpointType = $type
                ProfileId = @('builtin.openai.compatible', 'builtin.microsoft.azure-openai', 'builtin.microsoft.apim-gateway')[$type]
                BaseUrl = $endpoint; ApiKey = $key; AuthMode = $(if ($ConfigCase.ContainsKey('Aad')) { 1 } else { 0 })
                ApiKeyHeaderMode = $header; ImageApiRouteMode = 0; ApiVersion = 'offline-config-version'
                Models = @(@{ ModelId = 'gpt-image-2'; DeploymentName = 'offline-config-deploy'; Capabilities = 2 },
                    @{ ModelId = 'gpt-image-1'; DeploymentName = 'offline-default-deploy'; Capabilities = 2 })
            }
            if ($ConfigCase.ContainsKey('BaseSuffix')) { $node.BaseUrl += $ConfigCase.BaseSuffix }
            if ($ConfigCase.ContainsKey('Route')) { $node.ImageApiRouteMode = [int]$ConfigCase.Route }
            if ($ConfigCase.ContainsKey('SecretUrl')) { $node.BaseUrl += '/Proxy/' + $key; $node.ApiVersion = $key }
            $fixture = @{ Endpoints = @($node); MediaGenConfig = @{ ImageSize = '16x16'; ImageQuality = 'high'; ImageCount = 9; ImageFormat = 'jpeg' } }
            if ($ConfigCase.ContainsKey('Default')) {
                $second = $node.Clone(); $second.Id = 'second-node'; $second.Name = '第二节点'
                $fixture.Endpoints += $second
                $fixture.MediaGenConfig.ImageModelRef = @{ EndpointId = 'offline-node'; ModelId = 'gpt-image-1' }
            }
            if ($ConfigCase.ContainsKey('Malformed')) { Write-Bytes $configPath ($utf8.GetBytes('{"ApiKey":"' + $key + '",BROKEN')) }
            else { Write-Json $configPath $fixture }
            $configHash = [Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData((Read-Bytes $configPath)))
            $configStamp = [IO.File]::GetLastWriteTimeUtc($configPath)
            if (-not $ConfigCase.ContainsKey('Implicit')) { $psi.ArgumentList.Add('--config'); $psi.ArgumentList.Add($configPath) }
            if ($ConfigCase.ContainsKey('Mismatch')) { $psi.ArgumentList.Add('--endpoint'); $psi.ArgumentList.Add($endpoint + '/other') }
            Check (-not $psi.Environment.ContainsKey('GPT_IMAGE_API_KEY') -and -not $psi.Environment.ContainsKey('GPT_IMAGE_ENDPOINT')) "$Name 无连接环境注入"
            if ($ConfigCase.ContainsKey('EnvKey')) { $psi.Environment[$ConfigCase.EnvKey] = $externalKey }
            if ($ConfigCase.ContainsKey('ExplicitKey')) { $psi.ArgumentList.Add('--api-key'); $psi.ArgumentList.Add($externalKey) }
        }
        foreach ($arg in $CliArgs) { $psi.ArgumentList.Add($arg) }
        if ($null -eq $ConfigCase -and (($Status -ne 0 -and $Name -notlike 'default-*') -or $Name -like 'conflict-*')) {
            $psi.ArgumentList.Add('--endpoint'); $psi.ArgumentList.Add($endpoint)
        }
        if ($Name -like 'default-*') {
            Check ($psi.ArgumentList.Count -eq $CliArgs.Count + 1 -and $psi.ArgumentList[0] -eq '--no-config') "$Name 仅注入 --no-config，使用隔离的假环境连接"
        }
        Write-Json (Join-Path $dir 'invocation.json') @{
            exe = $fingerprint; arguments = @($psi.ArgumentList); endpoint = $endpoint
            configuration = 'Inherited GPT_IMAGE*/OPENAI*/AZURE_OPENAI* removed; isolated APPDATA; legacy --no-config or synthetic config only; offline-only key; proxy bypass'
            timeout_seconds = $TimeoutSeconds
        }
        $process.StartInfo = $psi
        $started = $process.Start()
        if (-not $started) { throw 'exe 启动失败' }
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        if ($Status -ne 0) {
            $client = Await-Task ($listener.AcceptTcpClientAsync()) 'HTTP accept'
            $connections++
            Check ([Net.IPAddress]::IsLoopback(([Net.IPEndPoint]$client.Client.RemoteEndPoint).Address)) "$Name 回环连接"
            $stream = $client.GetStream()
            $stream.ReadTimeout = $TimeoutSeconds * 1000
            $stream.WriteTimeout = $TimeoutSeconds * 1000
            $request = Read-Request $stream $dir
            $responseBytes = $utf8.GetBytes(($Response | ConvertTo-Json -Depth 30 -Compress))
            Write-Bytes (Join-Path $dir 'response.body.json') $responseBytes
            $reason = if ($Status -eq 200) { 'OK' } else { 'Bad Request' }
            $headerBytes = $latin1.GetBytes("HTTP/1.1 $Status $reason`r`nContent-Type: application/json; charset=utf-8`r`nContent-Length: $($responseBytes.Length)`r`nx-request-id: offline-$Name`r`nConnection: close`r`n`r`n")
            Write-Bytes (Join-Path $dir 'response.headers.txt') $headerBytes
            $null = Await-Task ($stream.WriteAsync($headerBytes, 0, $headerBytes.Length)) 'HTTP write headers'
            $null = Await-Task ($stream.WriteAsync($responseBytes, 0, $responseBytes.Length)) 'HTTP write body'
            $stream.Dispose(); $stream = $null
            $client.Dispose(); $client = $null
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw "$Name exe 退出超时" }
        $stdout = Await-Task $outTask 'stdout read'
        $stderr = Await-Task $errTask 'stderr read'
        $pending = $listener.Pending()
        Write-Json (Join-Path $dir 'transport.json') @{
            accepted_connections = $connections; pending_after_exit = $pending; exit_code = $process.ExitCode
        }
        Check (-not $pending) "$Name 无额外连接（退出后 Pending=false）"
        Check (-not ($stdout + $stderr).Contains($key)) "$Name stdout/stderr 不泄漏假密钥"
        Check (-not ($stdout + $stderr).Contains($externalKey)) "$Name stdout/stderr 不泄漏外部假密钥"
        if ($null -ne $configPath) {
            Check (-not ($stdout + $stderr).Contains($endpoint)) "$Name 配置连接日志隐藏完整 URL"
            Check ($configHash -ceq [Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData((Read-Bytes $configPath))) -and
                $configStamp -eq [IO.File]::GetLastWriteTimeUtc($configPath)) "$Name 配置内容和写入时间未修改"
            Check (@([IO.Directory]::GetFiles([IO.Path]::GetDirectoryName($configPath))).Count -eq 1) "$Name 不创建默认配置或备份"
        }
        if ($CliArgs -contains '--list-endpoints') {
            Check (-not ($stdout + $stderr).Contains($endpoint) -and -not ($stdout + $stderr).Contains('offline-config-deploy') -and
                -not ($stdout + $stderr).Contains('offline-default-deploy')) "$Name 列表不泄漏 URL 或部署名"
        }
        if ($CliArgs -notcontains '--json') {
            return @{ stdout = $stdout; stderr = $stderr; request = $request; exit = $process.ExitCode; connections = $connections }
        }
        # JsonDocument 拒绝多份 JSON / 尾随日志；ConvertFrom-Json 用于后续字段断言。
        $document = [Text.Json.JsonDocument]::Parse([string]$stdout)
        $document.Dispose()
        $report = ConvertFrom-Json -InputObject $stdout -AsHashtable
        foreach ($field in @('ok', 'exit_code', 'files', 'mode', 'http_status', 'request_id', 'elapsed_ms', 'usage', 'api_error', 'error', 'response_metadata')) {
            Check ($report.ContainsKey($field)) "$Name JSON 字段 $field"
        }
        Check ($report['exit_code'] -eq $process.ExitCode -and $report['ok'] -is [bool] -and $report['ok'] -eq ($process.ExitCode -eq 0)) "$Name JSON/进程退出码一致"
        Check ($report['elapsed_ms'] -is [ValueType] -and $report['elapsed_ms'] -ge 0) "$Name 耗时为非负数"
        return @{ report = $report; request = $request; stderr = $stderr; exit = $process.ExitCode; connections = $connections }
    } finally {
        # 仅终止本次 Start 创建的子进程树；不触碰仓库其他进程。失败也保留输出。
        try {
            if ($started -and -not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { throw '本工具子进程清理超时' }
            }
        } finally {
            if ($null -ne $stream) { $stream.Dispose() }
            if ($null -ne $client) { $client.Dispose() }
            $listener.Stop()
            try {
                if ($null -ne $outTask) { Write-Bytes (Join-Path $dir 'stdout.json') ($utf8.GetBytes([string](Await-Task $outTask '保存 stdout' 5000))) }
                if ($null -ne $errTask) { Write-Bytes (Join-Path $dir 'stderr.txt') ($utf8.GetBytes([string](Await-Task $errTask '保存 stderr' 5000))) }
            } finally { $process.Dispose() }
        }
    }
}

function New-Args([string]$Mode, [string]$Output, [string]$Auth = 'bearer') {
    return @('--json', '--mode', $Mode, '--prompt', $prompt, '--image-model', 'gpt-image-2',
        '--model', 'offline-text', '--size', '1024x640', '--quality', 'low', '--format', 'png',
        '--n', '1', '--auth', $Auth, '--timeout-minutes', '1', '--output', $Output)
}
function Check-Request($Run, [string]$Mode, [string]$Auth, [string]$Quality = 'low') {
    $path = @{ images = '/v1/images/generations'; edit = '/v1/images/edits'; responses = '/v1/responses' }[$Mode]
    Check ($Run.request.line -ceq "POST $path HTTP/1.1") "$Mode 出站路径与方法"
    $h = $Run.request.headers
    Check ($h['Host'] -match '^127\.0\.0\.1:\d+$' -and $h['Accept'] -eq 'application/json') "$Mode Host/Accept"
    if ($Auth -eq 'api-key') {
        Check ($h['api-key'] -ceq $key -and -not $h.ContainsKey('Authorization')) "$Mode api-key 认证"
    } else {
        Check ($h['Authorization'] -ceq "Bearer $key" -and -not $h.ContainsKey('api-key')) "$Mode Bearer 认证"
    }
    if ($Mode -eq 'edit') { return }
    Check ($h['Content-Type'] -match '^application/json') "$Mode JSON Content-Type"
    $body = ConvertFrom-Json -InputObject ($utf8.GetString($Run.request.body)) -AsHashtable
    if ($Mode -eq 'responses') {
        Check ($body['model'] -ceq 'offline-text' -and $h['x-ms-oai-image-generation-deployment'] -ceq 'gpt-image-2') 'responses 模型与部署头'
        Check ($body['input'].Count -eq 1 -and $body['input'][0]['role'] -ceq 'user' -and $body['input'][0]['content'][0]['type'] -ceq 'input_text' -and $body['input'][0]['content'][0]['text'] -ceq $prompt) 'responses 中文 input'
        Check ($body['tools'].Count -eq 1 -and $body['tools'][0]['type'] -ceq 'image_generation' -and $body['tool_choice']['type'] -ceq 'image_generation') 'responses tool/tool_choice'
        $fields = $body['tools'][0]
    } else {
        Check ($body['model'] -ceq 'gpt-image-2' -and $body['prompt'] -ceq $prompt) 'images 模型与中文 prompt'
        $fields = $body
    }
    foreach ($pair in @{ size = '1024x640'; quality = $Quality; output_format = 'png' }.GetEnumerator()) {
        Check ($fields[$pair.Key] -ceq $pair.Value) "$Mode 请求字段 $($pair.Key)"
    }
}
function Check-Multipart($Request, $ExpectedFiles) {
    $ct = $Request.headers['Content-Type']
    Check ($ct -match '^multipart/form-data;\s*boundary=(?:"([^"]+)"|([^;\s]+))$') 'edit multipart boundary'
    $boundary = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
    # Latin-1 一一映射字节，不能用 UTF-8 解码二进制图片后再比较。
    $wire = $latin1.GetString($Request.body)
    $segments = $wire.Split(@('--' + $boundary), [StringSplitOptions]::None)
    Check ($segments[0] -eq '' -and $segments[-1] -eq "--`r`n") 'edit multipart 起止边界'
    $parts = [Collections.Generic.List[object]]::new()
    foreach ($segment in $segments[1..($segments.Count - 2)]) {
        $split = $segment.IndexOf("`r`n`r`n")
        Check ($segment.StartsWith("`r`n") -and $segment.EndsWith("`r`n") -and $split -gt 2) 'edit part framing'
        $header = $segment.Substring(2, $split - 2)
        Check ($header -match '(?im)^Content-Disposition: form-data;.*?\bname=(?:"([^"]+)"|([^;\r\n]+))') 'edit part disposition'
        $name = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
        $data = $latin1.GetBytes($segment.Substring($split + 4, $segment.Length - $split - 6))
        $parts.Add(@{ name = $name; header = $header; data = $data })
    }
    foreach ($pair in @{ model = 'gpt-image-2'; prompt = $prompt; size = '1024x640'; quality = 'low'; output_format = 'png'; n = '1' }.GetEnumerator()) {
        $found = @($parts | Where-Object { $_.name -ceq $pair.Key })
        Check ($found.Count -eq 1 -and $utf8.GetString($found[0].data) -ceq $pair.Value) "edit multipart 字段 $($pair.Key)"
    }
    $images = @($parts | Where-Object { $_.name -ceq 'image[]' })
    $masks = @($parts | Where-Object { $_.name -ceq 'mask' })
    Check ($parts.Count -eq 9 -and $images.Count -eq 2 -and $masks.Count -eq 1) 'edit 双图与 mask 数量'
    $files = @($images[0], $images[1], $masks[0])
    for ($i = 0; $i -lt 3; $i++) {
        Check ($files[$i].header -match '(?im)^Content-Type: image/png\r?$' -and $files[$i].header -match '\bfilename=') "edit 文件 $i MIME/filename"
        Check (Same-Bytes $files[$i].data $ExpectedFiles[$i]) "edit 文件 $i 原始字节与顺序"
    }
}

try {
    $fingerprint = Get-Fingerprint
    Write-Json (Join-Path $evidence 'exe-fingerprint.json') $fingerprint
    $png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7ioAAAAASUVORK5CYII=')
    # 第二个文件在 PNG IEND 后带离线标记，以便发现重复首图/顺序错误；不宣称解码或云端有效性。
    $secondPng = [byte[]]($png + $utf8.GetBytes('offline-second-image'))
    $source1 = Join-Path $evidence '参考图 one.png'; Write-Bytes $source1 $png
    $source2 = Join-Path $evidence '参考图 two.png'; Write-Bytes $source2 $secondPng
    $mask = Join-Path $evidence '蒙版 mask.png'; Write-Bytes $mask $png
    $help = Invoke-Case 'help' @('--json', '--help')
    Check ($help.exit -eq 0 -and $help.connections -eq 0 -and $help.report['files'].Count -eq 0 -and $null -eq $help.report['mode'] -and $null -eq $help.report['http_status'] -and $help.stderr.Contains('--mode')) 'help 离线成功且帮助在 stderr'
    $cases.Add('help')
    $unknown = Invoke-Case 'unknown' @('--json', '--unknown')
    Check ($unknown.exit -eq 2 -and $unknown.connections -eq 0 -and $unknown.report['files'].Count -eq 0 -and $null -eq $unknown.report['http_status']) 'unknown exit 2 无 HTTP'
    $cases.Add('unknown')
    foreach ($name in @('default-cwd', 'default-cwd-json', 'default-directory')) {
        $cliArgs = @('--prompt', $prompt)
        if ($name -ne 'default-cwd') { $cliArgs += '--json' }
        $directory = Join-Path $evidence $name
        if ($name -eq 'default-directory') {
            $cliArgs += @('--output', '中文 输出/新建目录.v2/')
            $directory = Join-Path $directory '中文 输出/新建目录.v2'
        }
        $run = Invoke-Case $name $cliArgs 200 @{ data = @(@{ b64_json = [Convert]::ToBase64String($png) }) }
        Check-Request $run 'images' 'bearer' 'medium'
        $body = ConvertFrom-Json -InputObject ($utf8.GetString($run.request.body)) -AsHashtable
        Check ($body.Count -eq 5 -and -not $body.ContainsKey('n') -and -not $body.ContainsKey('tools')) "$name 默认 n=1，无 Responses/额外字段"
        Check ($run.exit -eq 0 -and $run.connections -eq 1 -and [IO.Directory]::Exists($directory)) "$name 最少参数成功且输出目录自动创建"
        $files = @([IO.Directory]::GetFiles($directory, '*.png'))
        Check ($files.Count -eq 1 -and [IO.Path]::GetFileName($files[0]) -cmatch '^gpt-image-\d{8}-\d{6}-[0-9a-f]{32}-01\.png$') "$name 指定工作目录内生成时间戳加 GUID 文件名"
        Check (Same-Bytes (Read-Bytes $files[0]) $png) "$name 输出原始 PNG 字节"
        if ($name -eq 'default-cwd') {
            Check ($run.stdout.StartsWith('POST ') -and $run.stdout.Contains($files[0]) -and $run.stderr -eq '') "$name 仅 prompt 的非 JSON 输出"
        } else {
            Check ($run.report['mode'] -ceq 'images' -and $run.report['files'].Count -eq 1 -and $run.report['files'][0] -ceq $files[0]) "$name JSON 默认模式与自动路径"
        }
        $cases.Add($name)
    }
    $usage = @{ input_tokens = 12; output_tokens = 8; total_tokens = 20; input_tokens_details = @{ text_tokens = 5; image_tokens = 7; cached_tokens = 0 }; output_tokens_details = @{ image_tokens = 8; reasoning_tokens = 0 }; ignored = 'discard-me' }
    foreach ($mode in @('images', 'edit', 'responses')) {
        $target = Join-Path $evidence "$mode-result.png"
        $auth = if ($mode -eq 'edit') { 'api-key' } else { 'bearer' }
        $cliArgs = New-Args $mode $target $auth
        if ($mode -eq 'edit') { $cliArgs += @('--image', $source1, '--image', $source2, '--mask', $mask) }
        $item = @{ size = '1024x640'; quality = 'low'; output_format = 'png' }
        if ($mode -eq 'responses') {
            $item['type'] = 'image_generation_call'; $item['result'] = [Convert]::ToBase64String($png)
            $response = @{ output = @($item); usage = $usage }
        } else {
            $item['b64_json'] = [Convert]::ToBase64String($png)
            $response = @{ data = @($item); usage = $usage }
        }
        $run = Invoke-Case $mode $cliArgs 200 $response
        Check-Request $run $mode $auth
        if ($mode -eq 'edit') { Check-Multipart $run.request @($png, $secondPng, $png) }
        $r = $run.report
        Check ($run.exit -eq 0 -and $run.connections -eq 1 -and $r['http_status'] -eq 200 -and $r['mode'] -ceq $mode -and $r['request_id'] -ceq "offline-$mode") "$mode 成功状态/mode/requestid"
        Check ($r['files'].Count -eq 1 -and $r['files'][0] -ceq $target -and $null -eq $r['error'] -and $null -eq $r['api_error']) "$mode 文件报告与无错误"
        Check (Same-Bytes (Read-Bytes $target) $png) "$mode 输出 PNG bytes 完全一致"
        foreach ($field in @('input_tokens', 'output_tokens', 'total_tokens')) {
            Check ($r['usage'][$field] -is [ValueType] -and $r['usage'][$field] -eq $usage[$field]) "$mode usage 数字 $field"
        }
        foreach ($detail in @('input_tokens_details', 'output_tokens_details')) {
            foreach ($pair in $usage[$detail].GetEnumerator()) {
                Check ($r['usage'][$detail][$pair.Key] -is [ValueType] -and $r['usage'][$detail][$pair.Key] -eq $pair.Value) "$mode usage 嵌套数字 $detail/$($pair.Key)"
            }
        }
        Check (-not $r['usage'].ContainsKey('ignored')) "$mode usage 白名单过滤"
        Check ($r['response_metadata'].Count -eq 1) "$mode 元数据数量"
        foreach ($pair in @{ size = '1024x640'; quality = 'low'; output_format = 'png' }.GetEnumerator()) {
            Check ($r['response_metadata'][0][$pair.Key] -ceq $pair.Value) "$mode 响应元数据 $($pair.Key)"
        }
        $cases.Add($mode)
    }
    foreach ($kind in @('safe', 'unsafe')) {
        $name = "http400-$kind"
        $target = Join-Path $evidence "$name.png"
        $api = if ($kind -eq 'safe') {
            @{ code = 'Raw-New_Code.42'; type = 'invalid_request_error'; param = 'tools[0].output_format'; message = "不要回显 $key" }
        } else { @{ code = "prefix_$key"; type = "bad`nvalue"; param = 42; message = $key } }
        $run = Invoke-Case $name (New-Args 'images' $target) 400 @{ error = $api }
        Check-Request $run 'images' 'bearer'
        $r = $run.report
        Check ($run.exit -eq 1 -and $r['http_status'] -eq 400 -and $r['mode'] -ceq 'images' -and $r['request_id'] -ceq "offline-$name" -and $run.connections -eq 1) "$name 状态与 requestid"
        Check ($r['files'].Count -eq 0 -and -not [IO.File]::Exists($target) -and $null -ne $r['error'] -and $run.stderr.Contains('HTTP 400')) "$name 无输出图片且友好错误"
        Check ($r['api_error']['message'] -ceq '服务端返回错误。') "$name 固定安全 message"
        foreach ($field in @('code', 'type', 'param')) {
            $expected = if ($kind -eq 'safe') { $api[$field] } else { $null }
            Check ($r['api_error'][$field] -ceq $expected) "$name 安全字段 $field"
        }
        $cases.Add($name)
    }
    foreach ($mode in @('images', 'edit')) {
        $target = Join-Path $evidence "conflict-$mode.png"
        $existing = if ($mode -eq 'images') { Join-Path $evidence "conflict-$mode-02.png" } else { $target }
        $sentinel = $utf8.GetBytes('offline-only: do not overwrite existing output')
        Write-Bytes $existing $sentinel
        $cliArgs = New-Args $mode $target
        if ($mode -eq 'images') { $cliArgs += @('--n', '2') } else { $cliArgs += @('--image', $source1) }
        $run = Invoke-Case "conflict-$mode" $cliArgs
        Check ($run.exit -eq 2 -and $run.connections -eq 0 -and $null -eq $run.report['http_status'] -and $run.report['files'].Count -eq 0 -and -not $run.stderr.Contains('POST ')) "$mode 冲突 exit 2 无 HTTP"
        Check (Same-Bytes (Read-Bytes $existing) $sentinel) "$mode 冲突文件未被覆盖"
        if ($mode -eq 'images') { Check (-not [IO.File]::Exists((Join-Path $evidence 'conflict-images-01.png'))) 'images 第二目标冲突时不写首目标' }
        $cases.Add("conflict-$mode")
    }
    foreach ($scenario in @(
        @{ Name = 'config-name'; Options = @{}; Args = @('--endpoint-name', '离线友好节点') },
        @{ Name = 'config-mismatch'; Options = @{ Mismatch = $true }; Args = @('--endpoint-id', 'offline-node') },
        @{ Name = 'config-auto-name'; Options = @{ Implicit = $true }; Args = @('--endpoint-name', '离线友好节点') },
        @{ Name = 'config-default'; Options = @{ Default = $true }; Args = @() },
        @{ Name = 'config-apim-auto'; Options = @{ Type = 2 }; Args = @('--endpoint-name', '离线友好节点') },
        @{ Name = 'config-apim-apikey'; Options = @{ Type = 2; Header = 1 }; Args = @('--endpoint-id', 'offline-node') },
        @{ Name = 'config-azure'; Options = @{ Type = 1 }; Args = @('--endpoint-id', 'offline-node') }
    )) {
        $name = $scenario.Name
        $run = Invoke-Case $name (@('--json', '--prompt', $prompt) + $scenario.Args) 200 @{ data = @(@{ b64_json = [Convert]::ToBase64String($png) }) } $scenario.Options
        Check ($run.exit -eq 0 -and $run.connections -eq 1 -and $run.report['files'].Count -eq 1) "$name 按沙盒节点连接成功"
        $expectedPath = if ($name -eq 'config-azure') { '/openai/v1/images/generations' } else { '/v1/images/generations' }
        Check ($run.request.line -ceq "POST $expectedPath HTTP/1.1") "$name profile 首选路由不附加无关版本"
        $headers = $run.request.headers
        if ($name -in @('config-apim-apikey', 'config-azure')) {
            Check ($headers['api-key'] -ceq $key -and -not $headers.ContainsKey('Authorization')) "$name api-key 认证"
        } else { Check ($headers['Authorization'] -ceq "Bearer $key" -and -not $headers.ContainsKey('api-key')) "$name Auto Bearer 认证" }
        $body = ConvertFrom-Json -InputObject ($utf8.GetString($run.request.body)) -AsHashtable
        $expectedModel = if ($name -eq 'config-default') { 'offline-default-deploy' } else { 'offline-config-deploy' }
        Check ($body['model'] -ceq $expectedModel -and $body['prompt'] -ceq $prompt) "$name 逻辑模型映射部署（含有效默认引用）"
        Check ($body['size'] -ceq '1024x640' -and $body['quality'] -ceq 'medium' -and $body['output_format'] -ceq 'png' -and
            -not $body.ContainsKey('n')) "$name 不导入主配置生成参数"
        Check (Same-Bytes (Read-Bytes $run.report['files'][0]) $png) "$name 响应字节保存正确"
        $cases.Add($name)
    }
    foreach ($scenario in @(
        @{ Name = 'config-list'; Options = @{}; Args = @('--list-endpoints'); Exit = 0 },
        @{ Name = 'config-invalid'; Options = @{ Malformed = $true }; Args = @('--prompt', $prompt); Exit = 2 },
        @{ Name = 'config-aad'; Options = @{ Aad = $true }; Args = @('--prompt', $prompt); Exit = 2 },
        @{ Name = 'config-help'; Options = @{ Malformed = $true; Implicit = $true }; Args = @('--help'); Exit = 0 }
    )) {
        $run = Invoke-Case $scenario.Name (@('--json') + $scenario.Args) 0 $null $scenario.Options
        Check ($run.exit -eq $scenario.Exit -and $run.connections -eq 0 -and $run.report['files'].Count -eq 0) "$($scenario.Name) 离线预期退出码且零 HTTP"
        if ($scenario.Name -eq 'config-list') {
            $entries = $run.report['endpoints']
            Check ($entries.Count -eq 1 -and $entries[0]['id'] -ceq 'offline-node' -and $entries[0]['name'] -ceq '离线友好节点' -and
                ($entries[0]['models'] -join ',') -ceq 'gpt-image-2,gpt-image-1' -and $entries[0].Count -eq 3) 'config-list 仅公开 id/name/逻辑 models'
        }
        $cases.Add($scenario.Name)
    }
    # 外部 key 没有明确目标时必须在读取默认配置/发送 POST 前拒绝。
    foreach ($source in @('GPT_IMAGE_API_KEY', 'OPENAI_API_KEY', 'AZURE_OPENAI_API_KEY', 'explicit')) {
        foreach ($useDefault in @($false, $true)) {
            foreach ($mode in @('images', 'edit', 'responses')) {
                $name = "key-no-target-$source-$useDefault-$mode"
                $options = @{ Implicit = $true }
                if ($useDefault) { $options.Default = $true }
                if ($source -eq 'explicit') { $options.ExplicitKey = $true } else { $options.EnvKey = $source }
                $cliArgs = @('--json', '--prompt', $prompt, '--mode', $mode)
                if ($mode -eq 'edit') { $cliArgs += @('--image', $source1) }
                $run = Invoke-Case $name $cliArgs 0 $null $options
                # StandardErrorEncoding 不会强制子进程编码；中文原文由 C# 测试验证。
                # 此处校验同一提示的 ASCII 选择器，避免 Windows 代码页导致假阴性。
                Check ($run.exit -eq 2 -and $run.connections -eq 0 -and $null -eq $run.report['http_status'] -and
                    -not $run.stderr.Contains('POST ') -and $run.stderr.Contains('--endpoint-name / --endpoint-id')) "$name exit2 零POST 提示明确目标选择器"
                $cases.Add($name)
            }
        }
        foreach ($selector in @('--endpoint-id', '--endpoint-name')) {
            $name = "key-selected-$source-$($selector.TrimStart('-'))"
            $options = @{}
            if ($source -eq 'explicit') { $options.ExplicitKey = $true } else { $options.EnvKey = $source }
            $target = if ($selector -eq '--endpoint-id') { 'offline-node' } else { '离线友好节点' }
            $run = Invoke-Case $name @('--json', '--prompt', $prompt, $selector, $target) 200 @{ data = @(@{ b64_json = [Convert]::ToBase64String($png) }) } $options
            Check ($run.exit -eq 0 -and $run.connections -eq 1 -and $run.request.line -ceq 'POST /v1/images/generations HTTP/1.1') "$name 使用所选节点连接"
            Check ($run.request.headers['Authorization'] -ceq "Bearer $key" -and -not $run.request.headers.ContainsKey('api-key') -and
                -not ($run.request.headers.Values -join ' ').Contains($externalKey)) "$name 只发送节点配置密钥，忽略外部密钥"
            $cases.Add($name)
        }
    }
    foreach ($type in @(0, 1, 2)) {
        $routes = if ($type -eq 2) { @(0, 2) } else { @(0) }
        foreach ($route in $routes) {
            foreach ($mode in @('images', 'edit', 'responses')) {
                foreach ($tail in @('', '/v1', '/openai', '/openai/v1')) {
                    foreach ($slash in @('', '/')) {
                        $name = "path-$type-$route-$mode-$($tail.Replace('/', '_'))-$($slash.Length)"
                        $options = @{ Type = $type; Route = $route; BaseSuffix = '/Proxy/v1/Tenant' + $tail + $slash }
                        $cliArgs = @('--json', '--prompt', $prompt, '--mode', $mode)
                        if ($mode -eq 'edit') { $cliArgs += @('--image', $source1) }
                        $run = Invoke-Case $name $cliArgs 200 @{ data = @(@{ b64_json = [Convert]::ToBase64String($png) }) } $options
                        $action = @{ images = 'images/generations'; edit = 'images/edits'; responses = 'responses' }[$mode]
                        $raw = $type -eq 2 -and ($mode -eq 'responses' -or $route -eq 2)
                        $versioned = if ($tail -eq '') { if ($type -eq 1) { '/openai/v1' } else { '/v1' } }
                            elseif ($tail -eq '/openai') { '/openai/v1' } else { $tail }
                        $expected = '/Proxy/v1/Tenant' + $(if ($raw) { $tail } else { $versioned }) + '/' + $action
                        if ($type -eq 2 -and ($mode -eq 'responses' -or ($route -eq 2 -and $mode -eq 'images'))) { $expected += '?api-version=offline-config-version' }
                        Check ($run.exit -eq 0 -and $run.connections -eq 1 -and $run.request.line -ceq "POST $expected HTTP/1.1") "$name 精确路径，版本不重复，保留代理前缀和raw路由"
                        if ($type -eq 1) {
                            Check ($run.request.headers['api-key'] -ceq $key -and -not $run.request.headers.ContainsKey('Authorization')) "$name 配置api-key隔离"
                        } else { Check ($run.request.headers['Authorization'] -ceq "Bearer $key" -and -not $run.request.headers.ContainsKey('api-key')) "$name 配置Bearer隔离" }
                        $cases.Add($name)
                    }
                }
            }
        }
    }
    foreach ($jsonMode in @($false, $true)) {
        $name = "config-secret-url-$jsonMode"
        $cliArgs = @('--prompt', $prompt, '--mode', 'responses')
        if ($jsonMode) { $cliArgs += '--json' }
        $run = Invoke-Case $name $cliArgs 200 @{ data = @(@{ b64_json = [Convert]::ToBase64String($png) }) } @{ Type = 2; SecretUrl = $true }
        Check ($run.exit -eq 0 -and $run.connections -eq 1 -and $run.request.line -ceq "POST /Proxy/$key/responses?api-version=$key HTTP/1.1") "$name 假密钥在路径和版本中也不输出日志"
        $cases.Add($name)
    }
    $after = Get-Fingerprint
    Check ($after.sha256 -ceq $fingerprint.sha256 -and $after.bytes -eq $fingerprint.bytes) '测试前后实际 exe 指纹一致'
} catch {
    $failure = $_.Exception.Message
    Write-Bytes (Join-Path $evidence 'failure.txt') ($utf8.GetBytes(($_ | Out-String)))
} finally {
    $summary = [ordered]@{
        all_pass = ($null -eq $failure -and $cases.Count -eq 153)
        passed_tests = $cases.Count; expected_tests = 153
        passed_checks = @($checks | Where-Object { $_.passed }).Count
        cases = @($cases.ToArray()); checks = @($checks.ToArray()); failure = $failure
        evidence_directory = $evidence; exe = $fingerprint
        scope = '仅传入 exe + 127.0.0.1 离线假服务器行为验证；不代表云端真实性、图像尺寸或蒙版语义验证；是否 NativeAOT 以传入产物为准'
    }
    Write-Json (Join-Path $evidence 'summary.json') $summary
    Write-Host "测试通过：$($cases.Count)/153；断言通过：$($summary.passed_checks)"
    Write-Host "证据目录：$evidence"
    if ($null -ne $fingerprint) { Write-Host "实际 exe：$ExePath`n字节数：$($fingerprint.bytes)`nSHA256：$($fingerprint.sha256)" }
}
if (-not $summary.all_pass) { throw "离线验证失败：$failure；证据：$evidence" }