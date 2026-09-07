#Requires -Version 7.0
<# 离线证据分析；不读取配置、环境密钥，不请求网络，不修改历史证据。
JPEG 依据：https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/main/src/jdmarker.c
已核对 get_dqt/first_marker/next_marker/skip_variable：大端长度含长度字段；DQT 为 Pq/Tq + 64 个值。
仅分析首个 SOS 前的基线 JPEG DQT；完整可解码性另外由系统解码器验证，不自制 JPEG 解码器。
#>
[CmdletBinding()]
param([string]$EvidenceRoot = (Join-Path $PSScriptRoot '../../artifacts/gpt-image-capabilities/20260907'))
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$approved = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/gpt-image-capabilities/20260907'))
$root = [IO.Path]::GetFullPath($EvidenceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($root -ne $approved) { throw '仅允许分析本工作区 artifacts/gpt-image-capabilities/20260907，拒绝其他根目录。' }
function Resolve-EvidencePath([string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $Relative))
    if (!$path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '证据路径越界。' }
    if ([IO.Path]::GetExtension($path).ToLowerInvariant() -notin @('.json','.png','.jpg','.jpeg')) { throw '只允许 JSON/PNG/JPEG。' }
    for ($p = $path; $p -and $p -ne $workspace; $p = [IO.Path]::GetDirectoryName($p)) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '拒绝重解析点。' }
    }
    return $path
}
$sourceHashes = [ordered]@{}
function Read-EvidenceJson([string]$Relative) {
    $path = Resolve-EvidencePath $Relative
    $sourceHashes[$Relative.Replace('\','/')] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
}
function Get-JpegDqt([byte[]]$Bytes) {
    if ($Bytes.Length -lt 4 -or $Bytes[0] -ne 255 -or $Bytes[1] -ne 216) { throw '缺少 JPEG SOI。' }
    $tables = [Collections.Generic.List[object]]::new(); $payload = [Collections.Generic.List[byte]]::new()
    $pos = 2; $sof = $null; $sos = $false
    while ($pos -lt $Bytes.Length) {
        if ($Bytes[$pos++] -ne 255) { throw 'JPEG header marker 非法。' }
        while ($pos -lt $Bytes.Length -and $Bytes[$pos] -eq 255) { $pos++ }
        if ($pos -ge $Bytes.Length) { throw 'JPEG marker 截断。' }
        $marker = [int]$Bytes[$pos++]
        if ($marker -eq 0 -or $marker -eq 216) { throw 'JPEG header 含异常 marker。' }
        if ($marker -eq 217) { break }
        if ($marker -eq 1 -or $marker -in 208..215) { continue }
        if ($pos + 2 -gt $Bytes.Length) { throw 'JPEG 长度截断。' }
        $length = [int]$Bytes[$pos] * 256 + $Bytes[$pos + 1]; $end = $pos + $length
        if ($length -lt 2 -or $end -gt $Bytes.Length) { throw 'JPEG 段长度越界。' }
        $pos += 2
        if ($marker -in @(192,193,194,195,197,198,199,201,202,203,205,206,207)) { $sof = $marker }
        if ($marker -eq 218) { $sos = $true; break }
        if ($marker -eq 219) {
            while ($pos -lt $end) {
                $start = $pos; $info = [int]$Bytes[$pos++]; $precision = $info -shr 4; $tableId = $info -band 15
                if ($precision -notin @(0,1) -or $tableId -gt 3) { throw 'DQT 精度或表号无效。' }
                $width = $precision + 1
                if ($pos + 64 * $width -gt $end) { throw 'DQT 表截断。' }
                $values = for ($i = 0; $i -lt 64; $i++) {
                    if ($width -eq 1) { [int]$Bytes[$pos++] }
                    else { [int]$Bytes[$pos] * 256 + $Bytes[$pos+1]; $pos += 2 }
                }
                if (@($values | Where-Object { $_ -eq 0 }).Count) { throw 'DQT 不允许零量化值。' }
                [byte[]]$raw = $Bytes[$start..($pos-1)]; $payload.AddRange($raw)
                $tables.Add([ordered]@{id=$tableId;precision_bits=8*$width;mean=($values | Measure-Object -Average).Average;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw));zigzag_values=@($values)})
            }
        }
        $pos = $end
    }
    if (!$sos -or $sof -ne 192 -or !$tables.Count) { throw '本分析仅支持含 DQT 的基线 JPEG；未认定其他编码已完整测量。' }
    return [ordered]@{scope='baseline header before first SOS';hash_encoding='DQT selector byte + raw zigzag values, concatenated in file order';sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload.ToArray()));tables=@($tables.ToArray())}
}
Add-Type -AssemblyName System.Drawing.Common
$pictureCache = @{}
function Measure-Picture([string]$Relative) {
    $Relative = $Relative.Replace('\','/')
    if ($pictureCache.ContainsKey($Relative)) { return $pictureCache[$Relative] }
    $path = Resolve-EvidencePath $Relative; $bytes = [IO.File]::ReadAllBytes($path)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)); $sourceHashes[$Relative] = $hash
    $bitmap = [Drawing.Bitmap]::new($path)
    try {
        $format = if ($bitmap.RawFormat.Guid -eq [Drawing.Imaging.ImageFormat]::Png.Guid) { 'png' } elseif ($bitmap.RawFormat.Guid -eq [Drawing.Imaging.ImageFormat]::Jpeg.Guid) { 'jpeg' } else { throw '解码格式不在 PNG/JPEG 范围。' }
        $signature = [Convert]::ToHexString($bytes[0..([Math]::Min(7,$bytes.Length-1))])
        if (($format -eq 'png' -and $signature -ne '89504E470D0A1A0A') -or ($format -eq 'jpeg' -and !$signature.StartsWith('FFD8'))) { throw '签名与解码格式不符。' }
        # 逐行读取确保实际解码，不仅仅相信头部尺寸；像素拷贝仅存内存。
        $rect = [Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height)
        $bits = $bitmap.LockBits($rect,[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $pixels = [byte[]]::new($bitmap.Width*$bitmap.Height*4)
            for ($y=0; $y -lt $bitmap.Height; $y++) { [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($bits.Scan0,$y*$bits.Stride),$pixels,$y*$bitmap.Width*4,$bitmap.Width*4) }
        } finally { $bitmap.UnlockBits($bits) }
        $m = [ordered]@{path=$Relative;bytes=$bytes.Length;sha256=$hash;format=$format;width=$bitmap.Width;height=$bitmap.Height;decoded=$true;dqt=$(if($format -eq 'jpeg'){Get-JpegDqt $bytes}else{$null})}
        $pictureCache[$Relative] = $m
        if ($Relative -match '^(G01|E01|E02|E04|G11|G18)/') {
            $a0=0L; $aMid=0L; $green=0L; $orange=0L; $yellow=0L
            for ($i=0; $i -lt $pixels.Length; $i+=4) {
                $b=[int]$pixels[$i]; $g=[int]$pixels[$i+1]; $r=[int]$pixels[$i+2]; $a=[int]$pixels[$i+3]
                if ($a -eq 0) { $a0++ } elseif ($a -lt 255) { $aMid++ }
                if ($a -gt 128 -and $g -gt $r*1.3 -and $g -gt $b*1.15) { $green++ }
                if ($a -gt 128 -and $r -gt 180 -and $g -gt 50 -and $g -lt 190 -and $b -lt 100) { $orange++ }
                $index=$i/4; $x=$index % $bitmap.Width; $y=[Math]::Floor($index/$bitmap.Width)
                if ($x -gt $bitmap.Width*0.65 -and $y -lt $bitmap.Height*0.4 -and $r -gt 190 -and $g -gt 150 -and $b -lt 110) { $yellow++ }
            }
            $m.pixels=[ordered]@{alpha_zero=$a0;alpha_partial=$aMid;alpha_opaque=$bitmap.Width*$bitmap.Height-$a0-$aMid;green=$green;orange=$orange;yellow_upper_right=$yellow}
        }
        if ($Relative -in @('G01/output.png','E04/output.png','E04/mask.png')) { $script:maskBuffers[$Relative]=$pixels }
        return $m
    } finally { $bitmap.Dispose() }
}
$maskBuffers = @{}
$ids = @((1..19 | ForEach-Object { 'G{0:00}' -f $_ }); (1..14 | ForEach-Object { 'E{0:00}' -f $_ }); (1..3 | ForEach-Object { 'S{0:00}' -f $_ }); (1..4 | ForEach-Object { 'N{0:00}' -f $_ }); (1..7 | ForEach-Object { 'R{0:00}' -f $_ }))
$cases = foreach ($id in $ids) {
    $q=Read-EvidenceJson "$id/request.json"; $r=Read-EvidenceJson "$id/result.json"; $p=Read-EvidenceJson "$id/response.json"
    if ($q.id -ne $id -or $r.id -ne $id) { throw '证据 ID 不匹配。' }
    $body=$q.body | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable
    $semantics=if($q.body_kind){$q.body_kind}else{'historical REST field record; CLI body is template'}
    if ($q.transport -eq 'CLI') {
        if (!$q.cli_args) { throw "$id 缺少 cli_args，不能以模板代替有效参数。" }
        $options=@{}; $imageArgs=[Collections.Generic.List[string]]::new()
        for ($i=0; $i -lt $q.cli_args.Count; $i++) {
            $parts=$q.cli_args[$i] -split '=',2; $key=$parts[0].ToLowerInvariant()
            if ($key -in @('--json','--help','--overwrite')) { $options[$key]=$true; continue }
            if ($parts.Count -eq 2) { $value=$parts[1] } else { $i++; if($i -ge $q.cli_args.Count){throw 'CLI 参数缺值。'}; $value=$q.cli_args[$i] }
            if ($key -eq '--image') { $imageArgs.Add($value) } else { $options[$key]=$value }
        }
        $body=[ordered]@{model='gpt-image-2';prompt=$options['--prompt'];size='1024x1024';quality='medium';output_format='png';n=1}
        $map=[ordered]@{'--image-model'='model';'--size'='size';'--quality'='quality';'--output-format'='output_format';'--format'='output_format';'--count'='n';'--n'='n';'--background'='background';'--output-compression'='output_compression';'--moderation'='moderation';'--user'='user'}
        foreach ($key in $map.Keys) { if($options.ContainsKey($key)){$body[$map[$key]]=$options[$key]} }
        foreach ($key in @('n','output_compression')) { if($body.Contains($key)){$body[$key]=[int]$body[$key]} }
        if ($body.output_format -eq 'jpg') { $body.output_format='jpeg' }
        $semantics='reconstructed from cli_args; repeated same option last wins; canonical alias precedence; NOT wire capture'
        if ($imageArgs.Count -ne @($q.inputs).Count) { throw "$id 输入参数和证据数量不符。" }
    }
    if ($id -eq 'E06' -and !$body.images) {
        $body.images=@(@{image_url='<local source as data URL>'})
        $semantics='historical body + images reconstructed from Test-Capabilities.ps1 JSON edit branch and recorded inputs; NOT wire capture'
    }
    $inputs=@(foreach($im in $q.inputs){$m=Measure-Picture $im.path;if($im.sha256 -ne $m.sha256){throw '输入证据哈希变化。'};$m})
    $mask=if($q.mask){Measure-Picture $q.mask.path}else{$null}
    if($q.mask -and $q.mask.sha256 -ne $mask.sha256){throw '蒙版证据哈希变化。'}
    $images=@(foreach($im in $r.images){$m=Measure-Picture $im.path;if($im.sha256 -ne $m.sha256 -or $im.width -ne $m.width -or $im.height -ne $m.height){throw '图像与历史测量不符。'};$m})
    $events=@(); $usage=$null; $toolUsage=$null; $responseId=$null; $requestId=$null
    if($body.stream){$events=@($p);$usage=@($events | Where-Object { $_.type -match '\.completed$' })[0].usage}
    else{$usage=$p.usage;$toolUsage=$p.tool_usage;$responseId=$p.id;$requestId=$p.request_id}
    if(!$requestId -and $r.headers){$requestId=$r.headers['X-Request-ID'];if(!$requestId){$requestId=$r.headers['apim-request-id']}}
    $requestedSize=if($id.StartsWith('R')){if($body.tools){$body.tools[0].size}else{$null}}else{$body.size}
    $exact=if($images.Count -and $requestedSize -and $requestedSize -ne 'auto'){@($images | Where-Object { "$($_.width)x$($_.height)" -ne $requestedSize }).Count -eq 0}else{$null}
    $partials=@($events | Where-Object { $_.type -match '\.partial_image$' }); $finals=@($events | Where-Object { $_.type -match '\.completed$' })
    $finalCount=if($body.stream){$finals.Count}else{$images.Count}
    $assessment=if($r.http_status -ne 200){'服务端拒绝'}elseif($exact -eq $false){'差异：显式尺寸不符'}elseif($id -eq 'R01'){'历史文字链证据，不扩展'}elseif($requestedSize -eq 'auto'){'自动尺寸返回，不承诺 grid'}else{'已返回并解码；语义效果另判'}
    [ordered]@{id=$id;title=$q.title;transport=$q.transport;route=$q.route;semantics=$semantics;effective_body=$body;cli_args=$q.cli_args;request_id=$requestId;response_id=$responseId;headers=$r.headers;http_status=$r.http_status;elapsed_ms=$r.elapsed_ms;cli_elapsed_ms=$(if($q.transport -eq 'CLI'){$p.elapsed_ms}else{$null});usage=$usage;tool_usage=$toolUsage;inputs=$inputs;mask=$mask;images=$images;final_count=$finalCount;partial_count=$partials.Count;first_partial_ms=$(if($partials.Count){$partials[0].received_ms}else{$null});final_ms=$(if($finals.Count){$finals[-1].received_ms}else{$null});requested_size=$requestedSize;exact_size_match=$exact;assessment=$assessment;error=$(if($body.stream){$null}else{$p.error});executable_sha256=$q.executable_sha256;original_request=$q;original_response=$p;original_result=$r}
}
if (@($cases).Count -ne 47) { throw '必须覆盖全部 47 项。' }
$source=$pictureCache['G01/output.png']; $edit=$pictureCache['E04/output.png']; $mask=$pictureCache['E04/mask.png']
if ($source.width -ne $edit.width -or $source.height -ne $edit.height -or $source.width -ne $mask.width -or $source.height -ne $mask.height) { throw '蒙版对照尺寸不一致，禁止缩放后冒充像素对照。' }
$before=$maskBuffers['G01/output.png']; $after=$maskBuffers['E04/output.png']; $alpha=$maskBuffers['E04/mask.png']
$counts=@(0L,0L); $sums=@(0L,0L); $changed=@(0L,0L); $changed8=@(0L,0L)
for ($i=0; $i -lt $before.Length; $i+=4) {
    $region=if($alpha[$i+3] -eq 0){0}else{1}; $counts[$region]++
    $db=[Math]::Abs([int]$before[$i]-[int]$after[$i]); $dg=[Math]::Abs([int]$before[$i+1]-[int]$after[$i+1]); $dr=[Math]::Abs([int]$before[$i+2]-[int]$after[$i+2])
    $sums[$region]+=$db+$dg+$dr; $max=[Math]::Max($db,[Math]::Max($dg,$dr))
    if($max -gt 0){$changed[$region]++};if($max -gt 8){$changed8[$region]++}
}
$maskMetrics=@(for($i=0;$i -lt 2;$i++){
    if(!$counts[$i]){throw '缺少蒙版内或外像素。'}
    [ordered]@{region=$(if($i -eq 0){'alpha=0 editable'}else{'alpha>0 outside'});pixels=$counts[$i];rgb_mae=$sums[$i]/(3.0*$counts[$i]);changed_pixels=$changed[$i];changed_ratio=$changed[$i]/[double]$counts[$i];changed_gt8_pixels=$changed8[$i];changed_gt8_ratio=$changed8[$i]/[double]$counts[$i]}
})
# 写报告前再次核对所有已读取历史文件，衍生 JSON 不纳入历史清单。
foreach($relative in $sourceHashes.Keys){if((Get-FileHash -LiteralPath (Resolve-EvidencePath $relative) -Algorithm SHA256).Hash -ne $sourceHashes[$relative]){throw '分析期间历史证据发生变化。'}}
$output=Resolve-EvidencePath 'analysis-derived.json'
$report=[ordered]@{schema_version=1;case_count=47;network_requests=0;source_files_unchanged=$true;source_hashes=$sourceHashes;jpeg_source='https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/main/src/jdmarker.c';pixel_method='System.Drawing BGRA; green A>128,G>1.3R,G>1.15B; orange A>128,R>180,50<G<190,B<100; yellow x>0.65W,y<0.4H,R>190,G>150,B<110; MAE unweighted RGB bytes 0..255, no registration or resizing';mask_comparison=$maskMetrics;cases=@($cases)}
[IO.File]::WriteAllText($output,($report | ConvertTo-Json -Depth 70),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{case_count=47;decoded_images=$pictureCache.Count;source_files=$sourceHashes.Count;source_files_unchanged=$true;output=$output;mask=$maskMetrics} | ConvertTo-Json -Depth 8