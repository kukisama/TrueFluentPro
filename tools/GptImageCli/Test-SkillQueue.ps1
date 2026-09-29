#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SkillDirectory)
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $SkillDirectory).Path
$exe = Join-Path $directory 'gpt-image.exe'
$query = Join-Path $directory 'Get-GptImageQueueResult.ps1'
$root = Join-Path $PSScriptRoot "../../artifacts/gpt-image-skill-tests/$([Guid]::NewGuid().ToString('N'))"
$root = [IO.Path]::GetFullPath($root)
$null = [IO.Directory]::CreateDirectory($root)
$config = Join-Path $root 'fake-connection.json'
$reference = Join-Path $root 'reference.png'
[IO.File]::WriteAllBytes($reference, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7ioAAAAASUVORK5CYII='))
@{ Endpoints=@(@{
    Id='offline'; Name='offline'; IsEnabled=$true; BaseUrl='http://127.0.0.1:1'; ApiKey='fake-skill-test-key';
    EndpointType=0; AuthMode=0; ApiKeyHeaderMode=0; ImageApiRouteMode=0;
    Models=@('gpt-image-2','gpt-image-2.5-flare','gpt-image-2.5-sunburst') | ForEach-Object { @{ ModelId=$_; Capabilities=2 } }
}) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $config -Encoding utf8
$queue = Join-Path $root 'queue'
$encodingBefore = [Console]::OutputEncoding
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "PASS: $Message"
}
function Invoke-QueueTestCli([string[]]$Arguments) {
    $text = & $exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw '隔离测试 CLI 操作失败。' }
    return ($text | ConvertFrom-Json)
}
try {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $null = Invoke-QueueTestCli @('queue','pause','--queue-dir',$queue,'--json')
    $ids = foreach ($model in @('gpt-image-2.5-flare','gpt-image-2.5-sunburst','gpt-image-2')) {
        $reply = Invoke-QueueTestCli @('submit','--queue-dir',$queue,'--config',$config,'--endpoint-name','offline',
            '--image-model',$model,'--mode','edit','--image',$reference,'--size','832x800',
            '--prompt','同一提示词：添加绿色边框。','--output',(Join-Path $root "$model.png"),'--json')
        Check ($reply.ok -and $reply.jobId -gt 0) 'direct submit returns an accepted job ID'
        $reply.jobId
    }
    Check (@($ids | Select-Object -Unique).Count -eq 3) 'model requests are independent jobs'
    $snapshot = Invoke-QueueTestCli @('queue','list','--queue-dir',$queue,'--json')
    Check ($snapshot.snapshot.paused -and $snapshot.snapshot.jobs.Count -eq 3 -and
        @($snapshot.snapshot.jobs | Where-Object { $_.state -ne 'pending' -or $_.attempts -ne 0 }).Count -eq 0) 'all queued jobs remain unexecuted; no HTTP is sent'
    $pending = (& $query -JobId $ids -QueueDirectory $queue) | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 0 -and $pending.ok -and -not $pending.allCompleted -and -not $pending.allSucceeded) 'read-only query does not report pending jobs as generated'
    $timed = (& $query -JobId $ids -QueueDirectory $queue -Wait -TimeoutSeconds 1) | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 1 -and $timed.ok -and $timed.timedOut -and -not $timed.allCompleted) 'bounded wait times out without resubmitting'
    $snapshot = Invoke-QueueTestCli @('queue','list','--queue-dir',$queue,'--json')
    Check ($snapshot.snapshot.jobs.Count -eq 3 -and @($snapshot.snapshot.jobs | Where-Object { $_.state -ne 'pending' -or $_.attempts -ne 0 }).Count -eq 0) 'query and wait neither start nor cancel paused jobs'
    $null = Invoke-QueueTestCli @('queue','clear','--yes','--queue-dir',$queue,'--json')
    $cancelled = (& $query -JobId $ids -QueueDirectory $queue -Wait -TimeoutSeconds 1) | ConvertFrom-Json
    Check ($LASTEXITCODE -eq 1 -and $cancelled.ok -and $cancelled.allCompleted -and -not $cancelled.allSucceeded -and -not $cancelled.timedOut) 'completed cancellations are not mistaken for saved images'

    Write-Host "Skill queue tests passed. Artifacts: $root"
} finally {
    if (Test-Path -LiteralPath (Join-Path $queue 'queue.db')) { $null = & $exe queue clear --yes --queue-dir $queue --json }
    [Console]::OutputEncoding = $encodingBefore
}