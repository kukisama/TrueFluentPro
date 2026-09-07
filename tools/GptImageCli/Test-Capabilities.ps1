#Requires -Version 7.0
[CmdletBinding()]
param(
    [string[]]$CaseId = @(),
    [switch]$Run,
    [string]$EndpointName = '公司大实例',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../../artifacts/gpt-image-capabilities/20260907'),
    [string]$ExePath = (Join-Path $PSScriptRoot 'bin/Debug/net10.0/gpt-image.exe')
)
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$prompt = 'Minimal flat illustration: one orange sailboat with two triangular sails on a blue wave, cream background, no text, centered with wide margins.'
$cases = @(
    @{id='G01';title='PNG low 最小横图';args=@()},
    @{id='G02';title='medium 质量';args=@('--quality','medium')},
    @{id='G03';title='high 质量';args=@('--quality','high')},
    @{id='G04';title='auto 质量';args=@('--quality','auto')},
    @{id='G05';title='最小竖图';args=@('--size','640x1024')},
    @{id='G06';title='3:1 宽图';args=@('--size','1440x480')},
    @{id='G07';title='auto 尺寸';args=@('--size','auto')},
    @{id='G08';title='JPEG compression 100';args=@('--format','jpeg','--output-compression','100');format='jpeg'},
    @{id='G09';title='JPEG compression 50';args=@('--format','jpeg','--output-compression','50');format='jpeg'},
    @{id='G10';title='WebP 输出';raw=@{output_format='webp'}},
    @{id='G11';title='透明背景预览';raw=@{background='transparent'};prompt='An isolated green sailboat sticker, transparent background, no shadow, no text.'},
    @{id='G12';title='opaque 背景';args=@('--background','opaque')},
    @{id='G13';title='auto 背景';args=@('--background','auto')},
    @{id='G14';title='n=2 批量输出';args=@('--n','2');n=2},
    @{id='G15';title='moderation auto 与 user';args=@('--moderation','auto','--user','gpt-image-cli-capability-test')},
    @{id='G16';title='moderation low 合规提示词';raw=@{moderation='low'}},
    @{id='G17';title='中文文字与布局';args=@();prompt='A clean cream poster. Exact large Chinese headline: 春日出发. Below it a small orange sailboat, blue wave, no other words.'},
    @{id='G18';title='发布版 CLI 透明 PNG';args=@('--background','transparent');prompt='An isolated green sailboat sticker, transparent background, no shadow, no text.'},
    @{id='G19';title='JPEG compression 0';args=@('--format','jpeg','--output-compression','0');format='jpeg'},
    @{id='E01';title='单 PNG 参考图';edit=$true;args=@();prompt='Change only all orange parts of the sailboat to emerald green. Preserve its shape, cream background and blue wave.'},
    @{id='E02';title='以上次输出继续编辑';edit=$true;source='E01';args=@();prompt='Keep the green sailboat, cream background and blue wave. Add a small yellow sun in the upper right corner.'},
    @{id='E03';title='双参考图合成';edit=$true;multi=$true;args=@();prompt='Keep the sailboat from the first reference. Add the yellow circular sun with eight orange rays from the second reference in the upper right. Cream background and blue wave.'},
    @{id='E04';title='PNG alpha 蒙版局部编辑';edit=$true;mask=$true;args=@();prompt='Add a small yellow sun only in the transparent upper-right region of the mask. Keep the orange sailboat and blue wave unchanged.'},
    @{id='E05';title='JPEG 输入与 JPEG 输出';edit=$true;jpeg=$true;args=@('--format','jpeg','--output-compression','50');format='jpeg';prompt='Change the sailboat to emerald green; keep the background and wave.'},
    @{id='E06';title='JSON data URL 改图';edit=$true;jsonEdit=$true;raw=@{};prompt='Change the orange sailboat to emerald green; keep everything else.'},
    @{id='E07';title='显式 input_fidelity high 对照';edit=$true;raw=@{input_fidelity='high'};prompt='Change the orange sailboat to emerald green; keep everything else.'},
    @{id='E08';title='edit 自定义竖图';edit=$true;args=@('--size','640x1024');prompt='Change the orange sailboat to emerald green. Fit the composition to the requested portrait canvas, preserving the cream background and blue wave.'},
    @{id='E09';title='edit 3:1 宽图';edit=$true;args=@('--size','1440x480');prompt='Change the orange sailboat to emerald green. Fit the composition to the requested wide canvas, preserving the cream background and blue wave.'},
    @{id='E10';title='edit 自定义小方图';edit=$true;args=@('--size','816x816');prompt='Change the orange sailboat to emerald green. Fit the composition to the requested square canvas, preserving the cream background and blue wave.'},
    @{id='E11';title='edit 16:9 自定义尺寸';edit=$true;args=@('--size','1536x864');prompt='Change the orange sailboat to emerald green. Fit the composition to the requested canvas, preserving the cream background and blue wave.'},
    @{id='E12';title='edit auto 尺寸';edit=$true;args=@('--size','auto');prompt='Change the orange sailboat to emerald green. Preserve everything else.'},
    @{id='E13';title='edit n=2';edit=$true;args=@('--n','2');n=2;prompt='Change the orange sailboat to emerald green. Preserve everything else.'},
    @{id='E14';title='edit 低于像素下限';edit=$true;raw=@{size='512x512'};prompt='Change the orange sailboat to emerald green.'},
    @{id='S01';title='生图 SSE partial_images=2';raw=@{stream=$true;partial_images=2};reserve=3},
    @{id='S02';title='改图 SSE partial_images=2';edit=$true;raw=@{stream=$true;partial_images=2};reserve=3;prompt='Change the orange sailboat to emerald green; keep everything else.'},
    @{id='S03';title='生图 SSE partial_images=0';raw=@{stream=$true;partial_images=0}},
    @{id='N01';title='服务端拒绝小于最低像素';raw=@{size='512x512'};negative=$true},
    @{id='N02';title='服务端拒绝非16倍数';raw=@{size='1025x640'};negative=$true},
    @{id='N03';title='服务端拒绝超过3:1';raw=@{size='2048x512'};negative=$true},
    @{id='N04';title='response_format URL 兼容性';raw=@{response_format='url'}},
    @{id='R01';title='Responses 先纯文字聊天';response='chat';raw=@{};reserve=1},
    @{id='R02';title='Responses 聊天后生成图';response='generate';previous='R01';raw=@{}},
    @{id='R03';title='Responses 基于上一轮改图';response='edit';previous='R02';raw=@{};prompt='Change the boat to green, preserving the background and composition.'},
    @{id='R04';title='Responses data URL 参考图 edit';response='input';raw=@{};prompt='Change the orange boat in the input image to green. Preserve the background and composition.'},
    @{id='R05';title='Responses edit 标准横图';response='input';size='1536x1024';raw=@{};prompt='Change the orange boat in the input image to green. Preserve the background and composition.'},
    @{id='R06';title='Responses edit 标准竖图';response='input';size='1024x1536';raw=@{};prompt='Change the orange boat in the input image to green. Preserve the background and composition.'},
    @{id='R07';title='Responses edit 自定义尺寸';response='input';size='1024x640';raw=@{};prompt='Change the orange boat in the input image to green. Preserve the background and composition.'}
)
if (!$Run) { $cases | ForEach-Object { [pscustomobject]@{Id=$_.id;Title=$_.title;Transport=$(if ($_.ContainsKey('raw')) {'REST probe'} else {'CLI'})} }; return }
if (!$CaseId.Count) { throw '必须明确指定 -CaseId；默认不会运行付费测试。' }
foreach ($id in $CaseId) { if ($id -notin $cases.id) { throw "未知测试：$id" } }
if (!(Test-Path $ExePath -PathType Leaf)) { throw '请先独立构建 CLI。' }
$configPath = Join-Path $env:APPDATA 'TrueFluentPro/config.json'
$configHash = (Get-FileHash $configPath -Algorithm SHA256).Hash
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$endpoint = @($config.Endpoints | Where-Object Name -eq $EndpointName)
if ($endpoint.Count -ne 1 -or $endpoint[0].AuthMode -ne 0 -or $endpoint[0].ApiKeyHeaderMode -ne 1) { throw '测试驱动要求唯一匹配的 api-key 配置。' }
$baseUrl = $endpoint[0].BaseUrl.TrimEnd('/')
$key = $endpoint[0].ApiKey
if ([string]::IsNullOrWhiteSpace($key)) { throw '配置密钥为空。' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
Add-Type -AssemblyName System.Drawing.Common
if (!('CapabilityPixels' -as [type])) {
    Add-Type -ReferencedAssemblies ([System.Drawing.Bitmap].Assembly.Location),([System.Drawing.Color].Assembly.Location) -TypeDefinition @'
using System.Drawing;
public static class CapabilityPixels {
 public static long[] Count(Bitmap b) {
  long transparent=0, translucent=0, green=0, orange=0, yellowUpperRight=0;
  for(int y=0;y<b.Height;y++) for(int x=0;x<b.Width;x++) {
   var p=b.GetPixel(x,y);
   if(p.A==0) transparent++; else if(p.A<255) translucent++;
   if(p.A>128 && p.G>p.R*1.3 && p.G>p.B*1.15) green++;
   if(p.A>128 && p.R>180 && p.G>50 && p.G<190 && p.B<100) orange++;
   if(x>b.Width*0.65 && y<b.Height*0.4 && p.R>190 && p.G>150 && p.B<110) yellowUpperRight++;
  }
  return new[]{transparent,translucent,green,orange,yellowUpperRight};
 }
}
'@
}
function Measure-Picture([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $signature = [Convert]::ToHexString($bytes[0..([Math]::Min(11,$bytes.Length-1))])
    $m = [ordered]@{path=[IO.Path]::GetRelativePath($OutputRoot,$Path);bytes=$bytes.Length;sha256=(Get-FileHash $Path -Algorithm SHA256).Hash;signature=$signature}
    try {
        $img = [System.Drawing.Bitmap]::new($Path)
        try {
            $p = [CapabilityPixels]::Count($img)
            $m.width=$img.Width; $m.height=$img.Height; $m.pixel_format=$img.PixelFormat.ToString()
            $m.format=$img.RawFormat.Guid.ToString(); $m.transparent_pixels=$p[0]; $m.translucent_pixels=$p[1]
            $m.green_pixels=$p[2]; $m.orange_pixels=$p[3]; $m.yellow_upper_right_pixels=$p[4]
        } finally { $img.Dispose() }
    } catch { $m.decode_error='System.Drawing 无法解码该格式；保留文件签名与哈希，不认定图像验证通过。' }
    return $m
}
function Write-Json($Value,[string]$Path) { [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 40),[Text.UTF8Encoding]::new($false)) }
function Save-Encoded([string]$Data,[string]$Path) { [IO.File]::WriteAllBytes($Path,[Convert]::FromBase64String($Data)); return Measure-Picture $Path }
function Redact-Object($Value) {
    if ($Value -is [System.Collections.IDictionary]) {
        $safe=[ordered]@{}
        foreach($k in $Value.Keys) {
            if ($k -in @('b64_json','result','partial_image_b64') -and $Value[$k] -is [string]) { $safe[$k]='<image omitted>' }
            else { $safe[$k]=Redact-Object $Value[$k] }
        }; return $safe
    }
    if ($Value -is [array]) { return ,@($Value | ForEach-Object { Redact-Object $_ }) }
    if ($Value -is [string]) { return $Value.Replace($key,'<redacted>').Replace($baseUrl,'<endpoint>') }
    return $Value
}
function Get-Source([string]$Id) {
    $r=Get-Content (Join-Path $OutputRoot "$Id/result.json") -Raw | ConvertFrom-Json
    if (!$r.images.Count) { throw "前置测试 $Id 没有图片。" }
    return Join-Path $OutputRoot $r.images[0].path
}
$ledgerPath=Join-Path $OutputRoot 'rate-ledger.json'
$client=[Net.Http.HttpClient]::new(); $client.Timeout=[TimeSpan]::FromMinutes(5)
try {
 foreach ($case in $cases | Where-Object { $_.id -in $CaseId }) {
    $id=$case.id; $dir=Join-Path $OutputRoot $id
    if (Test-Path (Join-Path $dir 'request.json')) { throw "$id 已有请求记录，拒绝自动重发。使用新 OutputRoot 才能重新测试。" }
    $ledger=@(); if (Test-Path $ledgerPath) { $ledger=@(Get-Content $ledgerPath -Raw | ConvertFrom-Json) }
    $recent=@($ledger | Where-Object { [datetimeoffset]$_.at -gt [datetimeoffset]::UtcNow.AddSeconds(-61) })
    $reserve=1; if($case.n){$reserve=$case.n}; if($case.reserve){$reserve=$case.reserve}
    $used=($recent | Measure-Object -Property count -Sum).Sum
    if ($used+$reserve -gt 9) { Write-Host "RATE_GUARD: 本批停止于 $id，61 秒窗口预算不足；未发送请求。"; break }
    $source=$null
    if ($case.edit -or $case.response -eq 'input') { $source=Get-Source $(if($case.source){$case.source}else{'G01'}) }
    $previous=$null
    if ($case.previous) {
        $pr=Get-Content (Join-Path $OutputRoot "$($case.previous)/response.json") -Raw | ConvertFrom-Json
        if (!$pr.id) { Write-Host "BLOCKED $id : 前置响应没有 id"; continue }; $previous=$pr.id
    }
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    $referencePaths=@(); if ($source) {$referencePaths+= $source}
    if ($case.jpeg) {
        $b=[System.Drawing.Bitmap]::new($source); try {$source=Join-Path $dir 'input.jpg';$b.Save($source,[System.Drawing.Imaging.ImageFormat]::Jpeg)} finally {$b.Dispose()}; $referencePaths=@($source)
    }
    if ($case.multi) {
        $path=Join-Path $dir 'sun-reference.png'; $b=[System.Drawing.Bitmap]::new(1024,640);$g=[System.Drawing.Graphics]::FromImage($b)
        try {$g.Clear([System.Drawing.Color]::White);$g.FillEllipse([System.Drawing.Brushes]::Gold,392,200,240,240);for($i=0;$i -lt 8;$i++){$a=$i*[Math]::PI/4;$pen=[System.Drawing.Pen]::new([System.Drawing.Color]::Orange,18);try{$g.DrawLine($pen,[single](512+145*[Math]::Cos($a)),[single](320+145*[Math]::Sin($a)),[single](512+210*[Math]::Cos($a)),[single](320+210*[Math]::Sin($a)))}finally{$pen.Dispose()}};$b.Save($path,[System.Drawing.Imaging.ImageFormat]::Png)}finally{$g.Dispose();$b.Dispose()};$referencePaths+=$path
    }
    $maskPath=$null
    if ($case.mask) {
        $maskPath=Join-Path $dir 'mask.png';$b=[System.Drawing.Bitmap]::new(1024,640,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb);$g=[System.Drawing.Graphics]::FromImage($b)
        try {$g.Clear([System.Drawing.Color]::White);$g.CompositingMode=[System.Drawing.Drawing2D.CompositingMode]::SourceCopy;$g.FillRectangle([System.Drawing.Brushes]::Transparent,700,40,260,200);$b.Save($maskPath,[System.Drawing.Imaging.ImageFormat]::Png)} finally {$g.Dispose();$b.Dispose()}
    }
    $p=$prompt; if($case.prompt){$p=$case.prompt}
    $body=[ordered]@{model='gpt-image-2';prompt=$p;size='1024x640';quality='low';output_format='png';n=1}
    if($case.raw){foreach($k in $case.raw.Keys){$body[$k]=$case.raw[$k]}}
    $route='/v1/images/generations'; if($case.edit){$route='/v1/images/edits'}
    if($case.response) {
        $route='/responses?api-version=2025-04-01-preview'
        $tool=@{type='image_generation';size='1024x1024';quality='low';output_format='png';action='generate'}
        if($case.size){$tool.size=$case.size}
        $body=[ordered]@{model='gpt-5.4';input=$p;store=$true;tools=@($tool);tool_choice=@{type='image_generation'}}
        if($previous){$body.previous_response_id=$previous}
        if($case.response -eq 'chat') {$body.input='We will create a minimal illustration of an orange sailboat on a blue wave and cream background. Reply READY only; do not generate any image yet.';$body.Remove('tools');$body.Remove('tool_choice')}
        if($case.response -in @('edit','input')){$tool.action='edit'}
        if($case.response -eq 'input'){$body.input=@(@{role='user';content=@(@{type='input_text';text=$p},@{type='input_image';image_url='<local source as data URL>'})})}
    }
    $transport=if($case.ContainsKey('raw')){'REST probe'}else{'CLI'}
    if($transport -eq 'CLI') {
        $map=@{'--quality'='quality';'--size'='size';'--format'='output_format';'--n'='n';'--background'='background';'--output-compression'='output_compression';'--moderation'='moderation';'--user'='user'}
        for($i=0;$i -lt $case.args.Count;$i+=2) {
            $field=$map[$case.args[$i]]
            if($field){$body[$field]=$case.args[$i+1];if($field -in @('n','output_compression')){$body[$field]=[int]$body[$field]}}
        }
    }
    if($case.jsonEdit){$body.images=@(@{image_url='<local source as data URL>'})}
    $requestRecord=[ordered]@{id=$id;title=$case.title;transport=$transport;endpoint_name=$EndpointName;route=$route;auth='api-key';body=$body;body_kind='effective parameters (file bytes omitted)';extra_cli_args=$case.args;inputs=@($referencePaths|ForEach-Object{Measure-Picture $_});mask=$(if($maskPath){Measure-Picture $maskPath}else{$null});started_at=[datetimeoffset]::UtcNow.ToString('o');reserved_images=$reserve;executable_sha256=(Get-FileHash $ExePath -Algorithm SHA256).Hash}
    Write-Json $requestRecord (Join-Path $dir 'request.json')
    $ledger+=@{at=[datetimeoffset]::UtcNow.ToString('o');count=$reserve;id=$id};Write-Json $ledger $ledgerPath
    $sw=[Diagnostics.Stopwatch]::StartNew();$images=[Collections.Generic.List[object]]::new();$events=[Collections.Generic.List[object]]::new()
    $record=[ordered]@{id=$id;title=$case.title;transport=$transport;status='not_completed';http_status=$null;elapsed_ms=0;images=@();events=@();error=$null;config_unchanged=$false}
    try {
      if($transport -eq 'CLI') {
        $format=if($case.format){$case.format}else{'png'}
        $cliArgs=@('--mode',$(if($case.edit){'edit'}else{'images'}),'--auth','api-key','--image-model','gpt-image-2','--prompt',$p,'--size','1024x640','--quality','low','--format',$format,'--output',(Join-Path $dir "output.$format"),'--json')+$case.args
        foreach($path in $referencePaths){$cliArgs+=@('--image',$path)};if($maskPath){$cliArgs+=@('--mask',$maskPath)}
        $requestRecord.cli_args=$cliArgs; Write-Json $requestRecord (Join-Path $dir 'request.json')
        $psi=[Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($ExePath));$psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
        foreach($arg in $cliArgs){$psi.ArgumentList.Add([string]$arg)}
        $psi.Environment['GPT_IMAGE_ENDPOINT']=$baseUrl;$psi.Environment['GPT_IMAGE_API_KEY']=$key;$psi.Environment['AZURE_OPENAI_API_VERSION']=''
        $process=[Diagnostics.Process]::Start($psi)
        try {$outTask=$process.StandardOutput.ReadToEndAsync();$errTask=$process.StandardError.ReadToEndAsync();$process.WaitForExit();$stdout=$outTask.GetAwaiter().GetResult();$stderr=$errTask.GetAwaiter().GetResult();$exitCode=$process.ExitCode} finally {$process.Dispose()}
        $result=$stdout|ConvertFrom-Json -AsHashtable
        Write-Json (Redact-Object $result) (Join-Path $dir 'response.json')
        [IO.File]::WriteAllText((Join-Path $dir 'stderr.txt'),(Redact-Object $stderr))
        $record.http_status=$result.http_status;$record.exit_code=$exitCode;$record.error=$result.error
        foreach($path in $result.files){$images.Add((Measure-Picture $path))}
        $record.status=if($exitCode -eq 0 -and $images.Count -gt 0){'images_returned'}else{'rejected_or_failed'}
      } else {
        $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,($baseUrl+$route));$request.Headers.Add('api-key',$key);$request.Headers.ExpectContinue=$false
        if($case.response){$request.Headers.Add('x-ms-oai-image-generation-deployment','gpt-image-2')}
        if($case.edit -and !$case.jsonEdit) {
            $form=[Net.Http.MultipartFormDataContent]::new()
            foreach($k in $body.Keys){$v=[string]$body[$k];if($body[$k] -is [bool]){$v=$v.ToLowerInvariant()};$form.Add([Net.Http.StringContent]::new($v),$k)}
            $fileContent=[Net.Http.ByteArrayContent]::new([IO.File]::ReadAllBytes($source));$fileContent.Headers.ContentType=[Net.Http.Headers.MediaTypeHeaderValue]::new('image/png');$form.Add($fileContent,'image',[IO.Path]::GetFileName($source));$request.Content=$form
        } else {
            if($case.jsonEdit){$body.images=@(@{image_url='data:image/png;base64,'+[Convert]::ToBase64String([IO.File]::ReadAllBytes($source))})}
            if($case.response -eq 'input'){$body.input[0].content[1].image_url='data:image/png;base64,'+[Convert]::ToBase64String([IO.File]::ReadAllBytes($source))}
            $request.Content=[Net.Http.StringContent]::new(($body|ConvertTo-Json -Depth 30 -Compress),[Text.Encoding]::UTF8,'application/json')
        }
        try {
            $response=$client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            try {
                $record.http_status=[int]$response.StatusCode;$record.content_type=[string]$response.Content.Headers.ContentType
                $headers=[ordered]@{};foreach($h in $response.Headers){if($h.Key -match '(?i)request-id|retry-after'){$headers[$h.Key]=$h.Value -join ','}}; $record.headers=$headers
                if($record.content_type -like 'text/event-stream*' -and $response.IsSuccessStatusCode) {
                    $reader=[IO.StreamReader]::new($response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    try {
                        while(!$reader.EndOfStream) {
                            $line=$reader.ReadLine()
                            if(!$line.StartsWith('data:')){continue}
                            $data=$line.Substring(5).Trim()
                            if($data -eq '[DONE]'){continue}
                            $event=$data|ConvertFrom-Json -AsHashtable
                            $safe=Redact-Object $event
                            $safe.received_ms=$sw.ElapsedMilliseconds
                            $events.Add($safe)
                            if($event.b64_json) {
                                $ext=if($event.output_format){$event.output_format}else{'png'}
                                $images.Add((Save-Encoded $event.b64_json (Join-Path $dir "event-$($events.Count).$ext")))
                            }
                        }
                    } finally {$reader.Dispose()}
                    Write-Json @($events) (Join-Path $dir 'response.json');$record.status=if(@($events|Where-Object{ $_.type -match '\.completed$' }).Count){'stream_completed'}else{'stream_incomplete'}
                } else {
                    $text=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    try {$result=$text|ConvertFrom-Json -AsHashtable} catch {$result=@{unparsed=(Redact-Object $text)}}
                    Write-Json (Redact-Object $result) (Join-Path $dir 'response.json')
                    if(!$response.IsSuccessStatusCode){$record.status='rejected';$record.error=Redact-Object $result}
                    else {
                        foreach($item in $result.data){if($item.b64_json){$ext=if($result.output_format){$result.output_format}else{$body.output_format};$images.Add((Save-Encoded $item.b64_json (Join-Path $dir "output-$($images.Count+1).$ext")))}}
                        foreach($item in $result.output){if($item.type -eq 'image_generation_call' -and $item.result){$images.Add((Save-Encoded $item.result (Join-Path $dir "output-$($images.Count+1).png")))}}
                        $record.status=if($images.Count){'images_returned'}elseif($case.response -eq 'chat'){'text_returned'}else{'success_without_image'}
                    }
                }
            } finally {$response.Dispose()}
        } finally {$request.Dispose()}
      }
    } catch {$record.status='test_error';$record.error=Redact-Object $_.Exception.Message}
    finally {
        $record.elapsed_ms=$sw.ElapsedMilliseconds;$record.images=@($images.ToArray());$record.events=@($events.ToArray());$record.config_unchanged=((Get-FileHash $configPath -Algorithm SHA256).Hash -eq $configHash)
        $expectedSize=if($case.response){$tool.size}else{$body.size}
        $record.requested_size=$expectedSize
        if($images.Count -gt 0) {
            $record.exact_size_match=($expectedSize -eq 'auto') -or @($images | Where-Object { "$($_.width)x$($_.height)" -ne $expectedSize }).Count -eq 0
            if(!$record.exact_size_match){$record.status='response_mismatch'}
        }
        Write-Json $record (Join-Path $dir 'result.json')
    }
    Write-Host "$id $($record.status) HTTP=$($record.http_status) ms=$($record.elapsed_ms) images=$($images.Count)"
    foreach($im in $images){Write-Host "  $($im.path) $($im.width)x$($im.height) bytes=$($im.bytes) alpha0=$($im.transparent_pixels)"}
    if($record.status -eq 'test_error'){throw "测试驱动在 $id 失败；先检查 result.json，禁止自动重发。"}
 }
} finally {$client.Dispose();$key=$null;$config=$null;$endpoint=$null}