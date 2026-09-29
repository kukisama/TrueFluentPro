---
name: gpt-image-cli
description: '用户要求画图、生图、edit 改图、查询或维护队列、修改配额、模型对比、替换项目图片资源时使用。用同目录 gpt-image.exe 异步 submit，默认 flare；调度、并发与 429 重试由队列处理。'
---

# gpt-image-cli 命令速查

## 定位与默认
- 用宿主提供的本 SKILL.md 绝对路径定位同目录 exe；始终以 `& $exe` 调用，避免 PATH、同名函数或 alias 干扰。路径未知或 exe 缺失则说明缺项，不另找版本。
```powershell
$exe = Join-Path (Split-Path -Parent '<本 SKILL.md 的实际绝对路径>') 'gpt-image.exe'
```
- 默认每次显式传 `--endpoint-name "公司大实例"`；用户指定节点或连接方式时替换默认。`--endpoint-name` 与 `--endpoint-id` 互斥，连接交给 exe 解析。
- 每次显式传 `--image-model`：默认 flare=`gpt-image-2.5-flare`；新需求明确要更精细时 sunburst=`gpt-image-2.5-sunburst`；明确 image2=`gpt-image-2` 优先。连续编辑保持已选模型，用户要求更换才改。
- 默认 `mode=images / size=1024x640 / quality=medium / format=png / n=1`，默认参数省略；不随模型改变质量或失败自动回退。
- 画图请求即授权提交；改配额、暂停、取消或清除须用户授权，不读 Key；普通调用不读 help、枚举 endpoints 或做配置预检。

## 最短提交
在当前用户项目目录执行，替换提示词和图片路径；`submit` / `queue` 必须是首个参数。
```powershell
& $exe submit --endpoint-name "公司大实例" --image-model gpt-image-2.5-flare --prompt "绘制橙色小帆船插画" --json
& $exe submit --endpoint-name "公司大实例" --image-model gpt-image-2.5-flare --mode edit --image "<原图绝对路径>" --prompt "船帆改为绿色，保留构图" --json
```
- 输出默认当前项目目录，勿放 skill 目录；`--output` 可为目录或文件，省略/目录输出自动命名。`--name` 仅任务名，不设文件名；文件扩展名匹配格式，覆盖须授权并加 `--overwrite`。
- 不同页面、不同提示词各自独立 `submit`；`--n` 是同一提示词的多张结果，不代表多个提示词，拼图仍 `n=1`。
- 多图编辑重复 `--image` 并说明图序角色；连续编辑从上次成功任务的 `job.result.files` 取真实路径，不用计划路径或猜文件名。
- 需近方形编辑时可推荐 `--size 832x800`：当前节点三个模型的 edit 已实测；不推断所有 16 倍数均可用，其余限制见末尾能力边界。
- 生成是异步任务：提交退出码 0、`ok=true` 且有 `jobId` 即报告“已入队，编号 …”并返回，不默认轮询等出图。失败报告 `message`；即使失败仍有编号，也保留编号，不当成未入队重提。

## 查询与结果
```powershell
& $exe queue list --json
& $exe queue show <任务编号> --json
```
- 默认只查询一次；外层 JSON 是 camelCase（如 `jobId`），`job.result` 是 snake_case（如 `exit_code`），stdout 与 stderr 不合并解析。
- 只有 `job.state="succeeded"`、`job.result.ok=true`、`job.result.exit_code=0` 且 `job.result.files` 非空，才报告已保存数量及路径；外层 `ok` 仅说明查询成功。
- `unknown` 表示结果不明，`failed` 表示最终失败；均不自动重提，超时也不另开生成请求。用户要求等待、继续编辑或资源替换时查询原任务，等待方法见扩展示例。
- 默认不解码、展示或视觉评价结果；仅用户要求验图或资源替换需要时检查。已入队不等于已保存，已保存不等于已验图或已替换资源。

## 队列命令
以下 `[id]` 是可选队列 ID（`image2/flare/sunburst`），`<任务编号>` 来自回执；执行时替换占位符。
| 命令 | 用途 |
| --- | --- |
| `& $exe queue` | 打开交互 console |
| `& $exe queue pause [id]` | 暂停派发；省略 ID 为全局 |
| `& $exe queue resume [id]` | 解除指定队列/全局暂停；无 ID 不解除各队列单独暂停 |
| `& $exe queue cancel <任务编号>` | 取消指定未执行任务 |
| `& $exe queue clear --yes [--queue id]` | 取消全部或指定队列剩余未执行任务 |
| `& $exe queue start` | 启动后台并恢复任务 |
| `& $exe queue config` | 显示运行时配置内容和 `settingsPath` |
- `cancel/clear` 只取消 `pending/retry_wait`，保留历史与图片，不能撤回在途请求；`pause` 也不停止在途请求。
- 无未完成任务后后台退出，无常驻服务；关机后下次 `queue start` 或 `submit` 恢复，中断的在途任务转 `unknown`，需核对。

## 修改配额与模型映射
先运行 `& $exe queue config` 找实际运行时 `queue-settings.json`，默认位于 `%LOCALAPPDATA%\GptImageCli`（同一 Windows 用户跨项目共享）。直接用编辑器修改该文件；或运行 `& $exe queue` 进入 console **菜单 8**，修改 RPM、并发、重试数和延时等数值；`models/enabled` 需直接编辑 JSON。
以下是完整默认配置示例，不是要求覆盖现有配置：
```json
{
	"version": 1,
	"queues": [
		{"id":"image2","models":["gpt-image-2"],"requestsPerMinute":9,"maxConcurrency":4,"retryCount":2,"retryDelaySeconds":61,"enabled":true},
		{"id":"flare","models":["gpt-image-2.5-flare"],"requestsPerMinute":2,"maxConcurrency":1,"retryCount":2,"retryDelaySeconds":61,"enabled":true},
		{"id":"sunburst","models":["gpt-image-2.5-sunburst"],"requestsPerMinute":2,"maxConcurrency":1,"retryCount":2,"retryDelaySeconds":61,"enabled":true}
	]
}
```
- 每个队列配额独立：`requestsPerMinute` 是请求/分钟，非 Token 或图片张数，`n>1` 仍一次请求；`maxConcurrency` 是同时在途请求上限，`enabled` 控制是否启用。
- `models` 可映射新模型；新增模型在 `queues` 追加独立条目，设置唯一 `id`、模型名及全部配额字段。模型名不跨队列重复，未映射不能入队；已配置值不自动改。
- 并发、限速及可重试 HTTP 429 由队列程序处理，AI 不自行实现；默认 `retryCount=2` 为额外 2 次（最多 3 次尝试），`retryDelaySeconds=61` 为等待秒数，服务要求更久则等更久，配额不足不重试。
- 保存合法配置后后台自动重载。只有用户要求隔离才用 `--queue-dir <本机目录>`，提交与管理保持一致，不通过另建队列目录绕过限额。

## 罕用扩展（按需读取）
- [扩展示例](./references/examples.md)：批量/三模型对比直接 `foreach` 分别提交、mask、Responses、可选等待脚本 `Get-GptImageQueueResult.ps1`。
- [项目资源替换](./references/project-resource-replacement.md)：应用成功结果。
- [平台图标入口](./references/platform-icon-entrypoints.md)：定位资源引用。
- [能力边界](./CAPABILITIES.md)：尺寸、参数及端点限制。