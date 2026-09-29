#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][long[]]$JobId,
    [string]$QueueDirectory,
    [switch]$Wait,
    [ValidateRange(1, 86400)][int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'gpt-image.exe'
$watcher = $null
$results = @()
try {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '同目录缺少 gpt-image.exe。' }
    if (@($JobId | Where-Object { $_ -le 0 }).Count -gt 0) { throw '任务编号必须为正整数。' }
    $ids = @($JobId | Select-Object -Unique)
    $queue = if ($QueueDirectory) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($QueueDirectory) }
        else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GptImageCli' }
    if (-not (Test-Path -LiteralPath (Join-Path $queue 'queue.db') -PathType Leaf)) { throw '未找到该目录的队列数据库；请使用提交时的目录。' }
    if ($Wait) { $watcher = [IO.FileSystemWatcher]::new($queue, 'queue.db*') }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $timedOut = $false
    do {
        $results = @()
        foreach ($id in $ids) {
            $text = & $exe queue show $id --queue-dir $queue --json
            if ($LASTEXITCODE -ne 0) { throw "任务 $id 查询失败；请核对编号和队列目录。" }
            $reply = $text | ConvertFrom-Json -ErrorAction Stop
            if (-not $reply.ok -or $null -eq $reply.job) { throw "任务 $id 查询失败。" }
            $results += [pscustomobject]@{ jobId=$id; job=$reply.job; attempts=@($reply.attempts) }
        }
        $allCompleted = @($results | Where-Object { $_.job.state -in @('pending','running','retry_wait') }).Count -eq 0
        $allSucceeded = $allCompleted -and @($results | Where-Object {
            $_.job.state -ne 'succeeded' -or -not $_.job.result.ok -or $_.job.result.exit_code -ne 0 -or @($_.job.result.files).Count -eq 0
        }).Count -eq 0
        if (-not $Wait -or $allCompleted) { break }
        $remaining = ($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds
        if ($remaining -le 0) { $timedOut = $true; break }
        # Only wait for database changes; generation, concurrency and retries belong to the worker.
        $null = $watcher.WaitForChanged([IO.WatcherChangeTypes]::All, [int][Math]::Min(2000, [Math]::Ceiling($remaining)))
    } while ($true)
    [pscustomobject]@{
        kind='query'; ok=$true; allCompleted=$allCompleted; allSucceeded=$allSucceeded; timedOut=$timedOut
        queueDirectory=$queue; results=$results
        message=if ($timedOut) { '等待已超时，后台任务未被取消或重提。' } else { '查询完成；请按任务状态判断生成结果。' }
    } | ConvertTo-Json -Depth 20
    if ($Wait -and -not $allSucceeded) { exit 1 }
    exit 0
} catch {
    [pscustomobject]@{ kind='query'; ok=$false; allCompleted=$false; allSucceeded=$false; timedOut=$false; message=$_.Exception.Message; results=$results } |
        ConvertTo-Json -Depth 20
    exit 1
} finally {
    if ($null -ne $watcher) { $watcher.Dispose() }
}