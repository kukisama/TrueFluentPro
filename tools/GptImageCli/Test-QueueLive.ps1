#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$EndpointName,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$AllowPaidRequests
)

$ErrorActionPreference = 'Stop'
if (-not $AllowPaidRequests) { throw '此测试会提交 1 次生图和 3 次 edit，可能计费；须显式传入 -AllowPaidRequests。' }
$exe = (Resolve-Path -LiteralPath $Executable).Path
$root = [IO.Path]::GetFullPath($OutputDirectory)
$queue = Join-Path $root 'queue'
$results = [Collections.Generic.List[object]]::new()
$watcher = $null
$created = $false

function Invoke-Cli([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { throw 'CLI 操作超时；请检查现有任务，不要重复提交。' }
        $text = $stdout.GetAwaiter().GetResult()
        $diagnostic = $stderr.GetAwaiter().GetResult()
        $reply = $text | ConvertFrom-Json
        if ($process.ExitCode -ne 0 -or -not $reply.ok) {
            throw "CLI 操作失败（已有任务编号 $($reply.jobId)）：$($reply.message) $diagnostic"
        }
        return $reply
    } finally { $process.Dispose() }
}

function Wait-JobResult([long]$Id) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(12)
    $last = ''
    do {
        $reply = Invoke-Cli @('queue', 'show', "$Id", '--queue-dir', $queue, '--json')
        $job = $reply.job
        $state = "$($job.state)/$($job.attempts)"
        if ($state -ne $last) { Write-Host "任务 $Id：$state"; $last = $state }
        if ($job.state -notin @('pending', 'running', 'retry_wait')) { return $reply }
        # Wait for actual database changes, with a bounded recheck if an event happened between query and watch.
        $null = $watcher.WaitForChanged([IO.WatcherChangeTypes]::All, 2000)
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "等待任务 $Id 超时；任务保留在 $queue，未重新提交或终止后台。"
}

function Record-Result($Reply, [string]$Model, [string]$Mode, [int]$Width, [int]$Height) {
    $job = $Reply.job
    if ($job.state -ne 'succeeded' -or -not $job.result.ok -or $job.result.files.Count -ne 1) {
        $results.Add([ordered]@{ model=$Model; mode=$Mode; jobId=$job.id; state=$job.state; error=$job.error; attempts=$Reply.attempts })
        return $false
    }
    $path = $job.result.files[0]
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 24 -or [Convert]::ToHexString([byte[]]$bytes[0..7]) -ne '89504E470D0A1A0A') { throw "任务 $($job.id) 未保存可识别的 PNG。" }
    $actualWidth = [uint32]$bytes[16]*16777216 + [uint32]$bytes[17]*65536 + [uint32]$bytes[18]*256 + [uint32]$bytes[19]
    $actualHeight = [uint32]$bytes[20]*16777216 + [uint32]$bytes[21]*65536 + [uint32]$bytes[22]*256 + [uint32]$bytes[23]
    $entry = [ordered]@{
        model=$Model; mode=$Mode; jobId=$job.id; state=$job.state; httpStatus=$job.httpStatus
        requestedSize="${Width}x${Height}"; actualSize="${actualWidth}x${actualHeight}"
        sizeMatches=($actualWidth -eq $Width -and $actualHeight -eq $Height)
        files=@($path); bytes=$bytes.Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        attempts=$Reply.attempts
    }
    $results.Add($entry)
    Write-Host "$Model $Mode：HTTP $($job.httpStatus)，实际尺寸 $($entry.actualSize)，$path"
    return [bool]$entry.sizeMatches
}

try {
    if (Test-Path -LiteralPath $root) { throw '请指定尚不存在的独立测试目录，避免覆盖或混入旧任务。' }
    $null = New-Item -ItemType Directory -Path $root
    $created = $true
    $null = Invoke-Cli @('queue', 'config', '--queue-dir', $queue, '--json')
    $watcher = [IO.FileSystemWatcher]::new($queue, 'queue.db*')
    $source = Join-Path $root 'source-image2.png'
    $submitted = Invoke-Cli @('submit', '--queue-dir', $queue, '--endpoint-name', $EndpointName,
        '--image-model', 'gpt-image-2', '--mode', 'images', '--size', '1024x640', '--quality', 'low',
        '--prompt', 'A very simple flat icon: one solid blue circle centered on a plain white background. No text, no shadows, no extra objects.',
        '--name', '实测-参考图', '--output', $source, '--json')
    Write-Host "参考图已入队：$($submitted.jobId)，提交进程已退出。"
    $sourceReply = Wait-JobResult $submitted.jobId
    if (-not (Record-Result $sourceReply 'gpt-image-2' 'images' 1024 640)) { throw '参考图失败或尺寸不符；停止提交后续付费任务。' }

    $jobs = [Collections.Generic.List[object]]::new()
    foreach ($model in @('gpt-image-2', 'gpt-image-2.5-flare', 'gpt-image-2.5-sunburst')) {
        $submitted = Invoke-Cli @('submit', '--queue-dir', $queue, '--endpoint-name', $EndpointName,
            '--image-model', $model, '--mode', 'edit', '--image', $source, '--size', '832x800', '--quality', 'low',
            '--prompt', 'Edit the provided image: change the blue circle to solid green. Keep one centered circle and a plain white background, no text or extra objects. Output canvas exactly 832 by 800 pixels.',
            '--name', "实测-edit-$model", '--output', (Join-Path $root "$model-edit.png"), '--json')
        $jobs.Add([PSCustomObject]@{ Model=$model; Id=$submitted.jobId })
        Write-Host "$model edit 已入队：$($submitted.jobId)，提交进程已退出。"
    }
    $ok = $true
    foreach ($job in $jobs) {
        $reply = Wait-JobResult $job.Id
        if (-not (Record-Result $reply $job.Model 'edit' 832 800)) { $ok = $false }
    }
    if (-not $ok) { throw '有 edit 任务失败或尺寸不符；已保留结果，不自动重新提交。' }
    Write-Host "真实队列测试完成：$root"
} finally {
    if ($null -ne $watcher) { $watcher.Dispose() }
    if ($created) {
        $results.ToArray() | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8
    }
}