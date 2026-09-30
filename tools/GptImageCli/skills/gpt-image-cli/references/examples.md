# 少用命令示例

沿用[主 skill](../SKILL.md)的绝对路径 `$exe`；`$outputDir` 为用户项目中的本轮输出目录。按用户选择调整连接与模型，仅执行需要的小节：
交付目录仅放图片；以下 `--json` 和查询脚本只返回 stdout，不写 JSON 文件。任务与诊断已在数据库，直接查询或在内存中 `ConvertFrom-Json`，不要增加 `Set-Content`、`Out-File`、重定向留档或批次任务清单；用 `--name` 定位历史任务。
```powershell
$connectionArgs = @('--endpoint-name', '公司大实例')
$imageModel = 'gpt-image-2.5-flare'
```

## 不同提示词批量提交

不同页面各自 submit；`--n` 不是提示词列表。任务名和固定文件名分别指定，图片同目录保存。

```powershell
$items = @(
    @{ Name = '01_封面'; Prompt = '极简蓝色几何封面，标题：项目概览' }
    @{ Name = '02_流程'; Prompt = '三步骤流程图：输入、处理、输出' }
)
foreach ($item in $items) {
    & $exe submit @connectionArgs --image-model $imageModel --prompt $item.Prompt --name $item.Name --output (Join-Path $outputDir "$($item.Name).png") --json
    if ($LASTEXITCODE -ne 0) { break }
}
```

## 三模型同提示词比较

```powershell
$prompt = '<本次相同提示词>'
foreach ($model in @('gpt-image-2.5-flare', 'gpt-image-2.5-sunburst', 'gpt-image-2')) {
    & $exe submit @connectionArgs --image-model $model --prompt $prompt --name $model --output (Join-Path $outputDir "$model.png") --json
    if ($LASTEXITCODE -ne 0) { break }
}
```

依次提交三个任务，调度并发由队列决定；固定任务名与 `<模型名>.png` 同目录输出。返回的 jobId 在当前会话使用，丢失时按任务名从数据库列表找回，不另写回执文件；失败即停止，未知结果先查原任务，不整批重跑。循环结束不代表所有任务已生成，文件名也只是计划路径。

## 蒙版编辑 / 多图编辑

以下两例择一；`$source`、`$mask`、`$reference` 均为实际输入路径。蒙版须与首图同尺寸、带 alpha 的 PNG，alpha=0 可编辑，不保证区域外像素锁定。

```powershell
& $exe submit @connectionArgs --image-model $imageModel --mode edit --image $source --mask $mask --prompt '仅修改蒙版区域为绿色船帆' --output $outputDir --json
```

```powershell
& $exe submit @connectionArgs --image-model $imageModel --mode edit --image $source --image $reference --prompt '保留图一主体，参考图二配色' --output $outputDir --json
```

## 查询与必要等待

默认单次查询，`$jobId` 使用本次真实返回编号：

```powershell
& $exe queue show $jobId --json
```

仅明确要求等待或后续编辑/替换依赖结果时使用：

```powershell
[long[]]$jobIds = 12,13,14 # 替换为已返回的真实编号
& (Join-Path (Split-Path -Parent $exe) 'Get-GptImageQueueResult.ps1') -JobId $jobIds -Wait -TimeoutSeconds 600
```

自定义队列须沿用提交时的目录：CLI 加 `--queue-dir`，脚本加 `-QueueDirectory`。超时只结束等待，不取消或重新提交任务；结果判据见主 skill。

## 高级：Responses 参考图生成

```powershell
& $exe submit @connectionArgs --mode responses --image $source --image-action generate --model '<端点实际支持的文字模型>' --image-model $imageModel --prompt '参考原图风格创作新图' --output $outputDir --json
```

此例是 data URL + image_generation 请求构造，需替换为真实文字模型；不保证远端支持，不等同于 multipart edit。