# GPT-Image CLI

独立图片生成与编辑工具。日常提交、查询和队列管理见发行包 `SKILL.md`；实际契约与实测限制见 [CAPABILITIES.md](./CAPABILITIES.md)。

运行 `gpt-image --help` 查看程序版本；版本能力与变更见 [CHANGELOG.md](./CHANGELOG.md)。版本号仅在 `GptImageCli.csproj` 的 `Version` 定义，由 SDK 生成程序集/文件版本，程序读取版本元数据后传给帮助界面，无需同步修改帮助文案。

## 安装

- 复制完整 `gpt-image-cli/` 到宿主支持的 skill 目录，例如项目 `.github/skills/gpt-image-cli/`；不要只复制 exe 或 SKILL.md。
- 保留根目录的 exe、SKILL.md、README.md、CAPABILITIES.md、CHANGELOG.md、辅助脚本、队列模板、发行清单及 `references/`。源码 skill 资产位于 `skills/gpt-image-cli/`，发行时展开到包根。
- 发行清单 `runtime` 须匹配 Windows 架构；`mode=NativeAot` 与 `Managed` 均无需另装 .NET，Debug 包需要 .NET 10。
- 无需启动主程序或配置 PATH；图片输出目录与安装目录独立。宿主未发现 skill 时，可显式加载该目录的 SKILL.md。

仅安装核验时，在发行目录执行以下离线命令：

```powershell
$exe = (Resolve-Path -LiteralPath './gpt-image.exe').Path
& $exe --help
```

## 构建

源码 `tools/GptImageCli` 目录、PowerShell 7、.NET 10 SDK；Release AOT 另需 Visual Studio C++ 桌面工具链。

| 命令 | 输出目录（相对本项目） |
| --- | --- |
| `./Build-Debug.ps1 -NoOpen` | `bin/Debug/net10.0/gpt-image-cli/`，保留 DLL 等配套文件 |
| `./Build-Release.ps1 -NoOpen` | `bin/Release/net10.0/win-x64/publish/gpt-image-cli/`，Native AOT |
| `./Build-Release.ps1 -Runtime win-arm64 -NoOpen` | `bin/Release/net10.0/win-arm64/publish/gpt-image-cli/`，需 ARM64 工具链 |

省略 `-NoOpen` 会在资源管理器中选中交付目录。自动化发布可用 `Publish.ps1 -Runtime win-x64 -Mode NativeAot -OutputDirectory <最终目录>`；不自动追加子目录，也不运行付费生图测试。

## 常用参数

表中是 CLI 默认值，不是 skill 的模型选择策略。有值参数支持 `--参数 value` / `--参数=value`；仅 `--image` 累积重复，其他同名参数最后一次生效。

| 参数 | 默认值 / 用途 |
| --- | --- |
| `--mode` | `images`；可选 `edit`、`responses` |
| `--prompt` / `--prompt-file` | 提示词 / UTF-8 文件；均省略时读取重定向 stdin |
| `--image-model` | 图片模型或部署名；无配置覆盖时 `gpt-image-2` |
| `--model` | Responses 文字模型；无配置覆盖时 `gpt-4.1` |
| `--image` | 本地 PNG/JPEG/WebP，可重复，最多 16 张；edit 必填，responses 可选 |
| `--mask` | 仅 edit；与首图同尺寸、带 alpha 的 PNG，透明区域可编辑 |
| `--image-action` | 仅 responses：`generate` / `edit` / `auto`；带图默认 `generate` |
| `--size` | `1024x640`；`auto` 或宽x高，限制见能力文档 |
| `--quality` | `medium`；可选 `low` / `high` / `auto` |
| `--format` | `png`；可选 `jpeg`（别名 `jpg`）/ `webp` |
| `--n` | `1`；整数 1..10，同一提示词的结果数量，不是不同任务 |
| `--output` | 当前目录自动命名；可给目录或明确图片文件名，扩展名须匹配格式（JPEG 可用 .jpg/.jpeg）；多张追加序号；新建带点目录以分隔符结尾 |
| `--name` | 仅 submit 的任务名，最多 120 字符、无控制字符；不决定文件名 |
| `--overwrite` | 无值开关，默认不覆盖已有文件 |
| `--background` | 默认不发送；`auto` / `opaque` / `transparent`，透明不能配 JPEG |
| `--output-compression` | 默认不发送；0..100，仅 JPEG/WebP |
| `--moderation` | 默认不发送；`auto` / `low` |
| `--timeout-minutes` | `10`；整数 1..35791 |
| `--endpoint-name` / `--endpoint-id` | 选择已配置节点，二者互斥 |
| `--config` / `--no-config` | 指定只读连接配置 / 禁用配置回退；后者不能与配置或节点选择器同用 |
| `--auth` | `auto`；可选 `bearer` / `api-key` |
| `--json` | 无值开关；stdout 为 JSON，stderr 为进度或错误 |

独立连接可由受信任启动器注入 `GPT_IMAGE_ENDPOINT`、`GPT_IMAGE_API_KEY`；CLI 不自动读取 `.env`。指定节点时使用该节点成对的地址与 Key。

批量、三模型对比、蒙版与 Responses 示例在发行包 `references/examples.md`（源码 `skills/gpt-image-cli/references/examples.md`）；资源接入另见同目录 `project-resource-replacement.md`。

## 日志保存位置与查询

日常通过 `submit` 入队的生图和 edit 任务自动持久化；同一 Windows 用户跨项目共享，不保存在 skill 安装目录。默认目录为 `%LOCALAPPDATA%\GptImageCli`（例如 `C:\Users\a9y\AppData\Local\GptImageCli`）：

**成功输出图片，最终失败输出同目录、同名 `.txt`。** 例如 `xxx.jpg` / `xxx.png` 失败时写 `xxx.txt`；多张按原序号写 `xxx-01.txt` 等，已有本次成功图片不另写失败占位。TXT 是简洁 JSON：任务编号、状态、HTTP、错误码、上游错误原文（沿用脱敏）、已保存图片数、模型、模式和请求 ID，不加分析说明；没有可用上游信息时记录本次错误。数据库及每次尝试照常保存，TXT 不加入 `result.files`。等待重试时不写 TXT；本次执行返回的结果不明错误写 `State=unknown`，不自动重提。旧历史、强制中断恢复及无法解密参数的任务不补写。

TXT 默认不覆盖已有文件，显式 `--overwrite` 才允许覆盖；日志不可写时原任务记录仍在数据库，并新增 `failure_output_error` 事件。成功不会删除以前的 TXT 或其他用户文件。同步调用也输出失败 TXT，但 `Id=null`，仍不入队列库。除此以外不另存 JSON 回执、提示词或任务清单；`--json` 和查询 `.ps1` 的 JSON 只是 stdout 通信格式。

| 位置 | 保存内容 |
| --- | --- |
| `queue.db` | 加密参数快照、任务状态与名称、输出路径、每次尝试、脱敏诊断、重试决策、事件及执行器就绪信息；成功后仍保留失败尝试 |
| `queue-settings.json` | 队列配额与模型映射配置，不是日志 |
| `queue.db-wal` / `queue.db-shm`、`*.lock` | SQLite 运行文件及同步锁，不是交付文件 |
| `inputs/` | edit 参考图与 mask 的输入快照，不是生成图片或日志 |
| 提交时的 `--output`，省略时为提交工作目录 | 成功图片或最终失败同名 `.txt`；图片实际路径以 `job.result.files` 为准 |

优先从 CLI 查询，不必直接打开数据库。`$exe` 为本安装目录 `gpt-image.exe` 的绝对路径，任务编号来自提交回执：

```powershell
& $exe queue list --json
& $exe queue list --page 2 --json
& $exe queue show <任务编号> --json
```

- 列表每页最多 20 条，可用 `--queue ID` / `--state 状态` 筛选。`queue show` 的 `attempts` 是全部尝试记录，429 详情看 `attempts[].result.api_error.upstream_message`，实际重试依据看 `attempts[].retry`；不要只看最终 `job.result`，具体字段见下节。
- 使用 `--queue-dir <目录>` 时数据位于该目录，提交和查询必须指定相同目录。`queue config` 返回的 `settingsPath` 所在目录也是实际日志目录；该目录不能位于图片交付目录内，不要另建队列来找历史或绕过限额。
- 当前没有按天数或数量自动清理：任务及尝试历史持续保留，`cancel/clear` 仅取消未执行任务，不删除历史或图片；edit 输入快照在任务结束后也不自动清理，目录可能持续增长。
- 旧直接同步调用（没有 `submit`）不写队列历史，日常应使用 `submit`；不要为了留档另用 `Set-Content` / `Out-File` / 重定向保存 stdout，直接查数据库，不与 stderr 合并解析。源码 `artifacts/` 内的专项验收文件是测试时另存的证据，不是日常自动日志，也不放入图片交付目录。
- 新执行器把就绪信息写入 `worker_runtime` 表，不再生成 `worker-info.json`；只读兼容仍在运行的旧执行器，新执行器持有独占租约后清理本工具的旧就绪文件。不会清理图片目录中已有的用户文件或旧报告。
- 保存的是脱敏诊断，不是完整 HTTP 响应正文；排查和分享优先使用 CLI 查询结果，不分享整个队列数据库、输入快照或连接配置，凭据过滤边界见下节。

## 429 排错与队列隔离

在运行任务的那台机器、同一 Windows 用户下执行 `gpt-image queue show <编号> --json`（使用过 `--queue-dir` 时继续指定相同目录）。交互 console 的任务详情也显示诊断；`job.result` 仅是最新结果，排错应查看 `attempts`，最终成功不会覆盖此前失败尝试。

- `attempts[].configuration`：派发时生效的队列配额与模型映射；不是查询时的当前配置。在途任务仍按派发时策略处理重试，下次尝试才使用重载后的配置。
- `attempts[].dispatch`：派发队列、当时在途数、最近一分钟尝试数及最小派发间距。
- `attempts[].result`：该次尝试独立的 HTTP 状态、脱敏服务端错误、请求 ID、白名单响应头和阶段耗时；不保存原始响应正文。
- `attempts[].retry`：是否重试及原因、已用额外重试数、配置等待与服务端等待、最终采用的等待来源、冷却范围及截止时间。`cooldownUntil` / `nextAttemptAt` 是 UTC Unix 毫秒；前者含同队列其他请求延长的冷却，后者只表示本任务可重试的最早时间，仍受队列限额约束。`delaySource=configuration` 表示配置等待较长或服务端未给出有效等待，`server` 表示采用更长的服务端等待，`configuration_and_server` 表示两者相同。额度耗尽与重试耗尽分别记录为 `quota_not_retryable`、`retry_exhausted`。

`server_delay_too_long` / `safety_cap` 表示服务端等待超过 365 天：沿用原有安全策略，不自动重试该任务，并对该队列保留最多 365 天的保护性冷却，需人工核查服务商返回值；不是正常的 61 秒重试。

默认 `image2`、`flare`、`sunburst` 的并发、滚动 RPM、派发间距和 429 冷却均独立；image2 满载或冷却不会暂停另外两个队列，反之亦然。同一配置条目 `models` 中的模型共用该队列；全局暂停则有意影响全部队列。客户端隔离不能改变服务商按账号、节点或部署共享的服务端配额，不应因为 429 盲目增加并发。

升级后才会产生新增诊断；旧历史的这些字段为 `null`，不能补回旧响应头或旧配置。服务商没有返回可用请求 ID 或限额头时，不会伪造；错误 `message` 是按已知错误标识和关键词归类的安全摘要，不是原文或服务商确认的根因，未知内容仍省略，应结合服务商记录定位。阶段耗时是客户端观测值，等待响应头包含连接、上传、网络及服务端处理，不能单独解释为服务端计算耗时。

`api_error.upstream_message` 另行保留上游 `error.message` 的脱敏详情，包括限额、已用量、建议等待等原有文字和数值。屏蔽本次密钥/提示词的已知编码、Bearer、sk-、常见凭据标签和 32 位十六进制凭据，以及 URL/图片数据；控制字符转为空格。非字符串、超过 8192 字符或无法安全处理时为 `null`，不保存完整响应正文。客户端不知道上游私有密钥，模式过滤不保证识别任意未知格式；分享诊断仍需检查。历史记录不能恢复已省略的原文。

`result.response_headers` 只收录经过校验的请求关联 ID、重试和限额白名单头；`request_id_source` 标明来源，`request_id_status` 区分缺失或被过滤。`request_settings` 保留实际/逻辑模型、模式、尺寸、质量、张数及超时，不含提示词、节点 URL 或密钥。`phase_elapsed_ms` 分为 `headers_wait`、`body_receive`、`parse`、`download`、`decode`、`write`；未执行的阶段为 `null`，多张图片的对应阶段累计计时，失败阶段也保留已经耗费的时间。

先对照 `api_error.code/type`、`upstream_message`、分类摘要和限额响应头：RPM 指向请求频率（`requestsPerMinute`），并发限制指向 `maxConcurrency`，TPM 则需检查令牌吞吐及服务商配额，余额/计费不足不是提高并发能解决的。限额类型不明时，携带请求 ID 与尝试时间向服务商核查，不把关键词推断当成定论。
