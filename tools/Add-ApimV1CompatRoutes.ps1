[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()]
    [string]$SubscriptionId,

    [Parameter()]
    [string]$ResourceGroupName,

    [Parameter(Mandatory = $true)]
    [string]$ApimServiceName,

    [Parameter(Mandatory = $true)]
    [string]$ApiId,

    [string]$ManagementApiVersion = "2022-08-01",

    [string]$RoutePrefix = "/v1",

    [switch]$IncludeVideoContentVideoRoute,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ResolvedSubscriptionId = ''
$script:ResolvedResourceGroupName = ''

function Write-Step {
    param([string]$Message)
    Write-Host "[APIM-V1] $Message" -ForegroundColor Cyan
}

function Write-WarnStep {
    param([string]$Message)
    Write-Warning "[APIM-V1] $Message"
}

function Get-EncodedSegment {
    param([string]$Value)
    return [Uri]::EscapeDataString($Value)
}

function Get-ManagementPath {
    param([string]$Suffix)

    $sub = Get-EncodedSegment $script:ResolvedSubscriptionId
    $rg = Get-EncodedSegment $script:ResolvedResourceGroupName
    $service = Get-EncodedSegment $ApimServiceName
    $api = Get-EncodedSegment $ApiId

    return "/subscriptions/$sub/resourceGroups/$rg/providers/Microsoft.ApiManagement/service/$service/apis/$api$Suffix?api-version=$ManagementApiVersion"
}

function Get-AuthMode {
    if (Get-Command Invoke-AzRestMethod -ErrorAction SilentlyContinue) {
        return 'AzRest'
    }

    if (Get-Command az -ErrorAction SilentlyContinue) {
        return 'AzCli'
    }

    throw "未检测到 Invoke-AzRestMethod 或 az CLI。请先安装 Az.Accounts / Azure CLI，并完成登录。"
}

$script:AuthMode = Get-AuthMode

function Get-CurrentSubscriptionId {
    if ($script:AuthMode -eq 'AzRest') {
        if (Get-Command Get-AzContext -ErrorAction SilentlyContinue) {
            $context = Get-AzContext
            if ($context -and $context.Subscription -and -not [string]::IsNullOrWhiteSpace($context.Subscription.Id)) {
                return [string]$context.Subscription.Id
            }
        }

        throw "未检测到当前 Az 上下文订阅。请先执行 Connect-AzAccount，或显式传入 -SubscriptionId。"
    }

    $raw = & az account show --query id -o tsv 2>&1
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($raw | Out-String).Trim())) {
        throw "未检测到当前 Azure CLI 订阅。请先执行 az login，或显式传入 -SubscriptionId。"
    }

    return ($raw | Out-String).Trim()
}

function Get-ResourceGroupNameFromResourceId {
    param([string]$ResourceId)

    if ([string]::IsNullOrWhiteSpace($ResourceId)) {
        return ''
    }

    $match = [regex]::Match($ResourceId, '/resourceGroups/([^/]+)/', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        return ''
    }

    return $match.Groups[1].Value
}

function Resolve-ResourceGroupName {
    if (-not [string]::IsNullOrWhiteSpace($ResourceGroupName)) {
        return $ResourceGroupName.Trim()
    }

    $sub = Get-EncodedSegment $script:ResolvedSubscriptionId
    $servicesPath = "/subscriptions/$sub/providers/Microsoft.ApiManagement/service?api-version=$ManagementApiVersion"
    $servicesResponse = Invoke-ManagementRequest -Method GET -Path $servicesPath
    $services = @($servicesResponse.value)

    $matched = @($services | Where-Object {
        $_.name -and $_.name.Equals($ApimServiceName, [System.StringComparison]::OrdinalIgnoreCase)
    })

    if ($matched.Count -eq 0) {
        throw "在订阅 $($script:ResolvedSubscriptionId) 下未找到名为 '$ApimServiceName' 的 APIM 服务。请确认名称正确，或显式传入 -ResourceGroupName。"
    }

    if ($matched.Count -gt 1) {
        throw "在订阅 $($script:ResolvedSubscriptionId) 下找到了多个名为 '$ApimServiceName' 的 APIM 服务，请显式传入 -ResourceGroupName。"
    }

    $resolved = Get-ResourceGroupNameFromResourceId -ResourceId $matched[0].id
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        throw "已找到 APIM 服务 '$ApimServiceName'，但无法从资源 ID 解析资源组。请显式传入 -ResourceGroupName。"
    }

    return $resolved
}

function Invoke-ManagementRequest {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('GET', 'PUT', 'PATCH', 'DELETE')]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [object]$Body,

        [switch]$AllowNotFound,

        [switch]$RawContent
    )

    $url = "https://management.azure.com$Path"
    $jsonBody = if ($null -ne $Body) { $Body | ConvertTo-Json -Depth 100 -Compress } else { $null }

    try {
        if ($script:AuthMode -eq 'AzRest') {
            if (-not [string]::IsNullOrWhiteSpace($script:ResolvedSubscriptionId) -and (Get-Command Select-AzSubscription -ErrorAction SilentlyContinue)) {
                Select-AzSubscription -SubscriptionId $script:ResolvedSubscriptionId | Out-Null
            }

            $response = if ($null -ne $jsonBody) {
                Invoke-AzRestMethod -Method $Method -Path $Path -Payload $jsonBody
            }
            else {
                Invoke-AzRestMethod -Method $Method -Path $Path
            }

            if ($RawContent) {
                return $response.Content
            }

            if ([string]::IsNullOrWhiteSpace($response.Content)) {
                return $null
            }

            return $response.Content | ConvertFrom-Json -Depth 100
        }

        $azArgs = @('rest', '--only-show-errors', '--method', $Method, '--url', $url)
        if ($null -ne $jsonBody) {
            $azArgs += @('--headers', 'Content-Type=application/json', '--body', $jsonBody)
        }

        $raw = & az @azArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw ($raw | Out-String)
        }

        if ($RawContent) {
            return ($raw | Out-String)
        }

        $text = ($raw | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) {
            return $null
        }

        return $text | ConvertFrom-Json -Depth 100
    }
    catch {
        $message = $_.Exception.Message
        $isNotFound = $message -match '404' -or $message -match 'NotFound' -or $message -match 'not found'
        if ($AllowNotFound -and $isNotFound) {
            return $null
        }

        throw
    }
}

function Get-OperationLookupKey {
    param(
        [string]$Method,
        [string]$UrlTemplate
    )

    return ('{0} {1}' -f $Method.Trim().ToUpperInvariant(), $UrlTemplate.Trim())
}

function New-TemplateParameter {
    param(
        [string]$Name,
        [string]$Type = 'string',
        [bool]$Required = $true,
        [string]$Description = ''
    )

    return @{
        name = $Name
        type = $Type
        required = $Required
        description = $Description
    }
}

function New-RewritePolicyXml {
    param([string]$BackendTemplate)

    return @"
<policies>
  <inbound>
    <base />
    <rewrite-uri template="$BackendTemplate" copy-unmatched-params="true" />
  </inbound>
  <backend>
    <base />
  </backend>
  <outbound>
    <base />
  </outbound>
  <on-error>
    <base />
  </on-error>
</policies>
"@
}

$routeSpecs = @(
    @{
        OperationId = 'v1-chat-completions-post'
        Method = 'POST'
        TargetUrlTemplate = "$RoutePrefix/chat/completions"
        SourceUrlTemplate = '/chat/completions'
        DisplayName = 'Chat Completions (v1)'
    },
    @{
        OperationId = 'v1-responses-post'
        Method = 'POST'
        TargetUrlTemplate = "$RoutePrefix/responses"
        SourceUrlTemplate = '/responses'
        DisplayName = 'Responses (v1)'
    },
    @{
        OperationId = 'v1-images-generations-post'
        Method = 'POST'
        TargetUrlTemplate = "$RoutePrefix/images/generations"
        SourceUrlTemplate = '/images/generations'
        DisplayName = 'Image Generations (v1)'
    },
    @{
        OperationId = 'v1-images-edits-post'
        Method = 'POST'
        TargetUrlTemplate = "$RoutePrefix/images/edits"
        SourceUrlTemplate = '/images/edits'
        DisplayName = 'Image Edits (v1)'
    },
    @{
        OperationId = 'v1-videos-post'
        Method = 'POST'
        TargetUrlTemplate = "$RoutePrefix/videos"
        SourceUrlTemplate = '/videos'
        DisplayName = 'Videos Create (v1)'
    },
    @{
        OperationId = 'v1-videos-get'
        Method = 'GET'
        TargetUrlTemplate = "$RoutePrefix/videos/{videoId}"
        SourceUrlTemplate = '/videos/{videoId}'
        DisplayName = 'Videos Status (v1)'
        TemplateParameters = @(
            (New-TemplateParameter -Name 'videoId' -Description '视频任务 ID')
        )
    },
    @{
        OperationId = 'v1-videos-content-get'
        Method = 'GET'
        TargetUrlTemplate = "$RoutePrefix/videos/{videoId}/content"
        SourceUrlTemplate = '/videos/{videoId}/content'
        DisplayName = 'Videos Content (v1)'
        TemplateParameters = @(
            (New-TemplateParameter -Name 'videoId' -Description '视频任务 ID')
        )
    }
)

if ($IncludeVideoContentVideoRoute) {
    $routeSpecs += @{
        OperationId = 'v1-videos-content-video-get'
        Method = 'GET'
        TargetUrlTemplate = "$RoutePrefix/videos/{videoId}/content/video"
        SourceUrlTemplate = '/videos/{videoId}/content/video'
        DisplayName = 'Videos Content Video (v1)'
        TemplateParameters = @(
            (New-TemplateParameter -Name 'videoId' -Description '视频任务 ID')
        )
    }
}

if ([string]::IsNullOrWhiteSpace($SubscriptionId)) {
    $script:ResolvedSubscriptionId = Get-CurrentSubscriptionId
}
else {
    $script:ResolvedSubscriptionId = $SubscriptionId.Trim()
}

$script:ResolvedResourceGroupName = Resolve-ResourceGroupName

Write-Step "认证模式：$script:AuthMode"
Write-Step "目标订阅：$($script:ResolvedSubscriptionId)"
Write-Step "目标资源组：$($script:ResolvedResourceGroupName)"
Write-Step "读取现有 operation 列表：$ApimServiceName / $ApiId"
$operationsPath = Get-ManagementPath -Suffix '/operations'
$operationsResponse = Invoke-ManagementRequest -Method GET -Path $operationsPath
$operations = @($operationsResponse.value)

$sourceMap = @{}
foreach ($operation in $operations) {
    $key = Get-OperationLookupKey -Method $operation.properties.method -UrlTemplate $operation.properties.urlTemplate
    $sourceMap[$key] = $operation
}

$summary = New-Object System.Collections.Generic.List[object]

foreach ($route in $routeSpecs) {
    $targetKey = Get-OperationLookupKey -Method $route.Method -UrlTemplate $route.TargetUrlTemplate
    $sourceKey = Get-OperationLookupKey -Method $route.Method -UrlTemplate $route.SourceUrlTemplate

    $sourceOperation = $null
    if ($sourceMap.ContainsKey($sourceKey)) {
        $sourceOperation = $sourceMap[$sourceKey]
    }

    $targetExists = $sourceMap.ContainsKey($targetKey)
    $mode = if ($targetExists) { 'update' } else { 'create' }
    $actionText = "$mode operation $($route.Method) $($route.TargetUrlTemplate)"

    if (-not $sourceOperation) {
        Write-WarnStep "未找到源 operation：$sourceKey。将使用最小定义 + rewrite fallback 创建目标路由。"
    }

    $operationProperties = @{
        displayName = if ($sourceOperation) {
            if ([string]::IsNullOrWhiteSpace($sourceOperation.properties.displayName)) { $route.DisplayName } else { $sourceOperation.properties.displayName }
        }
        else {
            $route.DisplayName
        }
        method = $route.Method
        urlTemplate = $route.TargetUrlTemplate
    }

    if ($sourceOperation) {
        foreach ($propertyName in @('description', 'templateParameters', 'request', 'responses')) {
            $value = $sourceOperation.properties.$propertyName
            if ($null -ne $value) {
                $operationProperties[$propertyName] = $value
            }
        }
    }

    if (-not $operationProperties.ContainsKey('templateParameters') -and $route.ContainsKey('TemplateParameters')) {
        $operationProperties['templateParameters'] = $route.TemplateParameters
    }

    $operationBody = @{ properties = $operationProperties }
    $operationPath = Get-ManagementPath -Suffix ("/operations/{0}" -f (Get-EncodedSegment $route.OperationId))

    if ($DryRun) {
        Write-Step "[DryRun] 将$actionText"
    }
    elseif ($PSCmdlet.ShouldProcess("$ApimServiceName/$ApiId", $actionText)) {
        Invoke-ManagementRequest -Method PUT -Path $operationPath -Body $operationBody | Out-Null
        Write-Step "已$mode：$($route.Method) $($route.TargetUrlTemplate)"
    }

    $policyXml = $null
    if ($sourceOperation) {
        $sourcePolicyPath = Get-ManagementPath -Suffix ("/operations/{0}/policies/policy" -f (Get-EncodedSegment $sourceOperation.name))
        $sourcePolicyResponse = Invoke-ManagementRequest -Method GET -Path $sourcePolicyPath -AllowNotFound
        if ($sourcePolicyResponse -and $sourcePolicyResponse.properties -and -not [string]::IsNullOrWhiteSpace($sourcePolicyResponse.properties.value)) {
            $policyXml = [string]$sourcePolicyResponse.properties.value
        }
    }

    if ([string]::IsNullOrWhiteSpace($policyXml)) {
        $policyXml = New-RewritePolicyXml -BackendTemplate $route.SourceUrlTemplate
    }

    $policyBody = @{
        properties = @{
            format = 'rawxml'
            value = $policyXml
        }
    }

    $targetPolicyPath = Get-ManagementPath -Suffix ("/operations/{0}/policies/policy" -f (Get-EncodedSegment $route.OperationId))
    if ($DryRun) {
        Write-Step "[DryRun] 将设置 policy：$($route.OperationId)"
    }
    elseif ($PSCmdlet.ShouldProcess("$ApimServiceName/$ApiId/$($route.OperationId)", 'set operation policy')) {
        Invoke-ManagementRequest -Method PUT -Path $targetPolicyPath -Body $policyBody | Out-Null
        Write-Step "已设置 policy：$($route.OperationId)"
    }

    $summary.Add([pscustomobject]@{
        Method = $route.Method
        Target = $route.TargetUrlTemplate
        Source = $route.SourceUrlTemplate
        SourceFound = [bool]$sourceOperation
        Mode = $mode
        PolicyMode = if ($sourceOperation -and -not [string]::IsNullOrWhiteSpace($policyXml) -and $policyXml -notmatch '<rewrite-uri template="' + [regex]::Escape($route.SourceUrlTemplate) + '"') { 'copy-source-policy' } else { 'rewrite-fallback-or-source' }
    }) | Out-Null
}

Write-Host ''
Write-Host '=== APIM v1 路由补齐结果 ===' -ForegroundColor Green
$summary | Format-Table -AutoSize

Write-Host ''
Write-Host '说明：' -ForegroundColor Yellow
Write-Host '1. 本脚本优先复制源 operation 的 policy；若源 policy 不存在，则自动回退为 rewrite-uri 到非 /v1 路径。'
Write-Host '2. 默认补齐：chat/completions、responses、images/generations、images/edits、videos、videos/{videoId}、videos/{videoId}/content。'
Write-Host '3. 若你的下载链路还需要 /v1/videos/{videoId}/content/video，可追加 -IncludeVideoContentVideoRoute。'
Write-Host '4. 如果你只想预览将创建什么，可先加 -DryRun。'
