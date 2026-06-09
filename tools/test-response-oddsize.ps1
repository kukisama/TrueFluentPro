param(
    [string]$ImageDirectory = 'C:\Users\a9y\AppData\Roaming\TrueFluentPro\Sessions\library\image\2026\05',
    [string]$ImagePath,
    [string]$EndpointName = '公司大实例',
    [string]$BaseUrlMatch = 'apim240804z.azure-api.net/ai02/openai',
    [string]$TextModel = 'gpt-5.4',
    [string]$ImageModel = 'gpt-image-2',
    [string]$ApiVersion = '2025-03-01-preview',
    [string]$Prompt = '让这个狗子跳起来，保持主体身份与室内场景一致',
    [ValidateSet('low','medium','high','auto')]
    [string]$Quality = 'low',
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Get-ImageSize {
    param([string]$Path)
    Add-Type -AssemblyName System.Drawing
    $img = [System.Drawing.Image]::FromFile($Path)
    try {
        [pscustomobject]@{
            Width = $img.Width
            Height = $img.Height
            Size = ('{0}x{1}' -f $img.Width, $img.Height)
        }
    }
    finally {
        $img.Dispose()
    }
}

function Select-OddSizeImage {
    param([string]$Directory)

    if (-not (Test-Path $Directory)) {
        throw "图片目录不存在: $Directory"
    }

    $standardSizes = @('1024x1024', '1024x1536', '1536x1024')
    $candidates = New-Object System.Collections.Generic.List[object]

    Get-ChildItem -Path $Directory -File | Where-Object { $_.Extension -in '.png', '.jpg', '.jpeg', '.webp' } | ForEach-Object {
        try {
            $size = Get-ImageSize -Path $_.FullName
            if ($standardSizes -notcontains $size.Size) {
                $candidates.Add([pscustomobject]@{
                    FullName = $_.FullName
                    Name = $_.Name
                    Width = $size.Width
                    Height = $size.Height
                    Size = $size.Size
                    Area = $size.Width * $size.Height
                    LastWriteTime = $_.LastWriteTime
                }) | Out-Null
            }
        }
        catch {
            Write-Warning "读取图片尺寸失败，已跳过: $($_.FullName) :: $($_.Exception.Message)"
        }
    }

    if ($candidates.Count -eq 0) {
        throw "目录中未找到异形尺寸图片: $Directory"
    }

    $ordered = $candidates | Sort-Object 
        @{ Expression = 'Area'; Descending = $true },
        @{ Expression = 'LastWriteTime'; Descending = $true }
    Write-Host '找到的异形尺寸候选（前 10 个）:' -ForegroundColor Yellow
    $ordered | Select-Object -First 10 Name, Size, Area, LastWriteTime | Format-Table | Out-String | Write-Host
    return $ordered[0]
}

function Get-EndpointConfig {
    $configPath = Join-Path $env:APPDATA 'TrueFluentPro\config.json'
    if (-not (Test-Path $configPath)) {
        throw "配置文件不存在: $configPath"
    }

    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    if (-not $config.Endpoints) {
        throw '配置中不存在 Endpoints 节点'
    }

    $endpoint = $config.Endpoints | Where-Object {
        $_.Name -eq $EndpointName -or ($_.BaseUrl -like "*$BaseUrlMatch*")
    } | Select-Object -First 1

    if (-not $endpoint) {
        throw "未找到目标终结点：Name=$EndpointName / BaseUrlMatch=$BaseUrlMatch"
    }

    if ([string]::IsNullOrWhiteSpace($endpoint.BaseUrl)) {
        throw "终结点 BaseUrl 为空: $($endpoint.Name)"
    }

    if ([string]::IsNullOrWhiteSpace($endpoint.ApiKey)) {
        throw "终结点 ApiKey 为空: $($endpoint.Name)"
    }

    return $endpoint
}

function New-HttpClient {
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    return $client
}

function Upload-File {
    param(
        [System.Net.Http.HttpClient]$Http,
        [string]$BaseUrl,
        [string]$ApiKey,
        [string]$FilePath
    )

    $uploadUrl = $BaseUrl.TrimEnd('/') + '/v1/files'
    $bytes = [System.IO.File]::ReadAllBytes($FilePath)
    $stream = New-Object System.IO.MemoryStream(,$bytes)
    $fileContent = New-Object System.Net.Http.StreamContent($stream)
    $ext = [System.IO.Path]::GetExtension($FilePath).ToLowerInvariant()
    $mime = switch ($ext) {
        '.jpg' { 'image/jpeg' }
        '.jpeg' { 'image/jpeg' }
        '.webp' { 'image/webp' }
        default { 'image/png' }
    }
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($mime)

    $form = New-Object System.Net.Http.MultipartFormDataContent
    $form.Add($fileContent, 'file', [System.IO.Path]::GetFileName($FilePath))
    $form.Add((New-Object System.Net.Http.StringContent('assistants')), 'purpose')

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $uploadUrl)
    $request.Headers.Add('api-key', $ApiKey)
    $request.Content = $form

    try {
        $response = $Http.Send($request)
        $body = $response.Content.ReadAsStringAsync().Result
        return [pscustomobject]@{
            Url = $uploadUrl
            Response = $response
            Body = $body
        }
    }
    finally {
        $request.Dispose()
        $form.Dispose()
        $fileContent.Dispose()
        $stream.Dispose()
    }
}

function Invoke-ResponsesEdit {
    param(
        [System.Net.Http.HttpClient]$Http,
        [string]$BaseUrl,
        [string]$ApiKey,
        [string]$TextModel,
        [string]$ImageModel,
        [string]$ApiVersion,
        [string]$Prompt,
        [string]$FileId,
        [string]$Size,
        [string]$Quality
    )

    $url = $BaseUrl.TrimEnd('/') + "/responses?api-version=$ApiVersion"
    $payload = [ordered]@{
        model = $TextModel
        input = @(
            @{
                role = 'user'
                content = @(
                    @{ type = 'input_text'; text = $Prompt }
                    @{ type = 'input_image'; file_id = $FileId }
                )
            }
        )
        tools = @(
            @{
                type = 'image_generation'
                size = $Size
                quality = $Quality
                output_format = 'png'
            }
        )
    } | ConvertTo-Json -Depth 10

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $url)
    $request.Headers.Add('api-key', $ApiKey)
    $request.Headers.Add('x-ms-oai-image-generation-deployment', $ImageModel)
    $request.Content = New-Object System.Net.Http.StringContent($payload, [System.Text.Encoding]::UTF8, 'application/json')

    try {
        $response = $Http.Send($request)
        $body = $response.Content.ReadAsStringAsync().Result
        return [pscustomobject]@{
            Url = $url
            RequestJson = $payload
            Response = $response
            Body = $body
        }
    }
    finally {
        $request.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $env:TEMP ('tfp-response-oddsize-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Step '读取终结点配置'
$endpoint = Get-EndpointConfig
Write-Host ('Endpoint=' + $endpoint.Name)
Write-Host ('BaseUrl=' + $endpoint.BaseUrl)
Write-Host ('ApiKeyLength=' + $endpoint.ApiKey.Length)

if ([string]::IsNullOrWhiteSpace($ImagePath)) {
    Write-Step '自动选择异形尺寸图片'
    $picked = Select-OddSizeImage -Directory $ImageDirectory
    $ImagePath = $picked.FullName
    $imageSize = $picked.Size
}
else {
    if (-not (Test-Path $ImagePath)) {
        throw "指定图片不存在: $ImagePath"
    }
    $pickedSize = Get-ImageSize -Path $ImagePath
    $imageSize = $pickedSize.Size
}

Write-Host ('Image=' + $ImagePath)
Write-Host ('ImageSize=' + $imageSize)

$http = New-HttpClient
try {
    Write-Step '上传参考图到 Files API'
    $upload = Upload-File -Http $http -BaseUrl $endpoint.BaseUrl -ApiKey $endpoint.ApiKey -FilePath $ImagePath
    $upload.Response.Headers | Out-Null
    Write-Host ('UploadHttp=' + [int]$upload.Response.StatusCode + ' ' + $upload.Response.ReasonPhrase)
    $upload.Body | Out-File -FilePath (Join-Path $OutputDir 'upload-response.json') -Encoding utf8
    if (-not $upload.Response.IsSuccessStatusCode) {
        throw "上传失败，详见: $(Join-Path $OutputDir 'upload-response.json')"
    }

    $fileId = ($upload.Body | ConvertFrom-Json).id
    if ([string]::IsNullOrWhiteSpace($fileId)) {
        throw '上传成功但未返回 file_id'
    }
    Write-Host ('FileId=' + $fileId)

    Write-Step '按原始异形尺寸调用 Responses + image_generation'
    $resp = Invoke-ResponsesEdit -Http $http -BaseUrl $endpoint.BaseUrl -ApiKey $endpoint.ApiKey -TextModel $TextModel -ImageModel $ImageModel -ApiVersion $ApiVersion -Prompt $Prompt -FileId $fileId -Size $imageSize -Quality $Quality
    $resp.RequestJson | Out-File -FilePath (Join-Path $OutputDir 'responses-request.json') -Encoding utf8
    $resp.Body | Out-File -FilePath (Join-Path $OutputDir 'responses-response.json') -Encoding utf8
    Write-Host ('ResponsesHttp=' + [int]$resp.Response.StatusCode + ' ' + $resp.Response.ReasonPhrase)

    if (-not $resp.Response.IsSuccessStatusCode) {
        Write-Host '接口返回非成功状态，原始响应已落盘：' -ForegroundColor Yellow
        Write-Host (Join-Path $OutputDir 'responses-response.json')
        exit 2
    }

    $json = $resp.Body | ConvertFrom-Json
    $imageBytes = $null
    foreach ($item in $json.output) {
        if ($item.type -eq 'image_generation_call' -and $item.result) {
            $imageBytes = [Convert]::FromBase64String($item.result)
            break
        }
    }

    if ($null -eq $imageBytes) {
        Write-Host '成功返回，但未解析到图片结果。原始响应已落盘。' -ForegroundColor Yellow
        Write-Host (Join-Path $OutputDir 'responses-response.json')
        exit 3
    }

    $outFile = Join-Path $OutputDir 'responses-result.png'
    [System.IO.File]::WriteAllBytes($outFile, $imageBytes)
    $outSize = Get-ImageSize -Path $outFile

    Write-Step '测试完成'
    Write-Host ('ResultFile=' + $outFile)
    Write-Host ('ResultSize=' + $outSize.Size)
    Write-Host ('OutputDir=' + $OutputDir)
}
finally {
    $http.Dispose()
}
