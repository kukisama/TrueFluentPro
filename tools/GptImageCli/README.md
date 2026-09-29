# GPT-Image CLI 使用指南

独立的图片生成与编辑命令行工具，供人和 AI 调用，不依赖主程序。服务能力与限制见 [CAPABILITIES.md](./CAPABILITIES.md)，不能将某个端点的支持范围推广到所有云端。

## 开始使用

### AI 日常调用：只执行，不扩展任务

普通画图只需已加载的 `SKILL.md`：适度整理提示词，以同目录 exe 的绝对路径执行一次 `submit ... --json`，再报告“已入队，任务编号：…”，不声称已生成。用户明确需要同步等待时，仍可使用不带 `submit` 的旧接口。本指南是按需参考，不是每次画图的前置阅读任务。

- 连接优先级为用户本次明确选择 → `SKILL.md` 的“本地明文默认配置”区块 → exe 自身解析。本地节点仅在该区块配置，不写入 exe、不修改环境变量；发布前可删除源码模板及安装副本中的该区块。
- 不先读其他文件、查网页、运行帮助或列节点，不读取主程序配置及密钥。以下安装检查与故障说明供用户主动要求时使用，不自动执行。
- 缺目标、缺密钥、节点无效、配置歧义或提交失败，报告后停止，不自动排障、切换节点或重复提交；若已返回任务编号，保留编号供查询。队列内的 429 重试由 worker 按配置执行，不由 AI 另发请求。
- `submit` 成功只表示入队；同步调用报告保存成功后才可说“已生成 N 张图片：路径”。不额外确认文件存在、不解码、不打开或再次展示图片；图片内容、尺寸和透明效果由用户人肉检查。

## 持久队列（推荐）

同一个 `gpt-image.exe` 提供提交、交互管理和后台执行器，无需另装服务。以下 `$exe` 表示同目录 exe 的绝对路径；在用户项目目录执行，节点按用户选择或 skill 默认配置填写：

```powershell
& $exe submit --endpoint-name '公司大实例' --prompt '一只小胖狗' --name '小胖狗插画' --output './图片' --json
& $exe queue
```

`submit` 接受现有生图/改图参数，额外支持可选 `--name`（最多 120 字符、不能含控制字符，仅为任务名称，不是输出文件名）。`--json` 的队列回复是 **camelCase `QueueReply`**：`ok`、`message`、`jobId`、`workerRunning`，另有按命令使用的可空字段。`ok=true`、退出码 0 仅表示提交操作成功，**不表示图片已生成**；后台启动失败时也可能已有 `jobId`，不要重复提交。

### 已实现的管理入口

以下参数均传给同一个 exe；`submit` / `queue` 必须是首个参数，连接参数及 `--queue-dir` 放在其后。任务编号是数据库 ID，不是列表行号。

| 命令 | 用途 |
| --- | --- |
| `queue` | 默认打开交互管理；不接受 `--json` |
| `queue list [--queue ID] [--state 状态] [--page N] [--json]` | 概览与任务分页，每页最多 20 条 |
| `queue show 编号 [--json]` | 任务、结果与尝试记录；此命令即使不加 `--json` 也输出 JSON |
| `queue pause [队列ID]` / `queue resume [队列ID]` | 省略 ID 表示全局暂停/继续；暂停只阻止新派发，不中止在途请求 |
| `queue cancel 编号` | 仅取消 `pending` / `retry_wait` 任务 |
| `queue clear --yes [--queue ID]` | 确认取消剩余排队/重试任务；不删除执行中任务、历史或输出 |
| `queue config` | 显示配置路径与内容，不是设置参数的子命令 |
| `queue start` | 启动后台处理未完成任务，仍遵守暂停及禁用状态 |
| `queue worker` | 前台诊断执行器；Ctrl+C 停止派发并等待在途任务结束 |

状态名：`pending`、`running`、`retry_wait`、`succeeded`、`failed`、`unknown`、`cancelled`。菜单 **6** 查看任务详情与尝试记录，菜单 **8** 编辑 RPM、并发、额外重试数、等待秒数。菜单 **3**“全部继续”同时解除全局和各队列暂停；命令行无 ID 的 `queue resume` 只解除全局暂停。菜单 **4** 清除全部队列的已确认剩余任务，**不受当前列表筛选限制**。

`submit` 与 `queue` 均支持 `--queue-dir <本机目录>` 隔离配置、数据库、输入快照及 worker；之后管理时须传同一目录。默认是 `%LOCALAPPDATA%\GptImageCli`，**同一 Windows 用户的所有项目共享**，不是每个项目各自限速；不支持网络共享目录。

### 配额与重试配置

首次使用队列时，若用户目录没有 `queue-settings.json`，程序从内置 `queue-settings.default.json` 创建；已有配置不被默认值覆盖。可手改该 JSON，或用菜单 8 修改数值；`models`、`enabled`、新增队列需手改。配置只含调度策略，**不放密钥**；它与连接配置 `--config` 是两回事。

| 队列 ID | 默认模型映射 | 每分钟请求数 / 并发上限 | 额外重试数 / 等待秒数 |
| --- | --- | --- | --- |
| `image2` | `gpt-image-2` | 9 / 4 | 2 / 61 |
| `flare` | `gpt-image-2.5-flare` | 2 / 1 | 2 / 61 |
| `sunburst` | `gpt-image-2.5-sunburst` | 2 / 1 | 2 / 61 |

- 字段为 `version=1`、`queues`，每组包含 `id`、`models`、`requestsPerMinute`、`maxConcurrency`、`retryCount`、`retryDelaySeconds`、`enabled`。模型映射可扩展，模型名不能跨组重复；未映射模型拒绝入队。有逻辑模型名时优先按其映射，否则按图片模型/部署名匹配。
- 每组独立限速、并发和冷却；同组不同节点仍共享该组额度。采用近 60 秒滚动计数并间隔派发，**按派发尝试/请求数，不按 Token 或图片数**；`n>1` 仍计一次请求，重试另计。服务若按图片张数限额，须自行对应调整 `n` 和本地配额，CLI 不自动换算。
- 默认仅对可重试的 **HTTP 429** 等待至少 61 秒、最多额外重试 2 次（总尝试最多 3 次）；服务端 `Retry-After` 要求更久则等更久，并冷却该组。`insufficient_quota` / `billing_hard_limit_reached` 不重试；认证错误、5xx、超时等也不自动重试。不保证重试未计费。
- 运行中的 worker 约每秒重载配置；非法配置、不可读配置或移除活动队列时保留上一份有效配置并记事件。不要移除仍有未完成任务的队列 ID。

### 持久化、安全与恢复

- 队列支持 Windows 10/11，SQLite 使用系统 **`winsqlite3`**（托管访问层为 `Microsoft.Data.Sqlite.Core` 与对应 provider），不随包携带额外 native SQLite 库。
- `queue.db` 保存任务、`events` 事件和 `attempts` 尝试记录。菜单 6 / `queue show` 查看任务结果与尝试记录；当前没有事件列表或日志导出命令，不要编造 `queue export`。
- 完整任务参数与提交时解析的连接快照用 **DPAPI `CurrentUser`** 加密保存，worker 不重新读取最新连接配置。密钥轮换不自动更新旧任务；应取消可取消的旧任务，再重新提交。任务名称、结果/路径和日志不是全库加密内容，名称中不要放秘密。
- 参考图及蒙版复制到队列目录的 `inputs/`，输出在提交时固定为绝对路径；不要删除这些输入快照。DPAPI 保护的是任务参数，不是参考图副本。
- 无 `pending` / `running` / `retry_wait` 任务后 worker 自动退出；若暂停或禁用后仍有任务，则继续等候。关闭管理界面不停止 worker。关机后没有登录自启功能；下次 `submit` 或 `queue start` 启动恢复，`queue resume` / 菜单继续操作也会尝试启动后台。
- 恢复时原 `running` 转为 **`unknown`**，需人工核对输出与服务端记录，不自动重画，也没有“确认后重试”子命令。超时/网络中断可能已经计费。`cancel` / `clear` 只改排队或等待重试任务的状态，保留历史和输出，不是清库。
- 入队事务会预留预计输出文件：活动任务不能共享同一目标，即使传 `--overwrite` 也不允许并发占用。取消待办或任务正常结束后释放；`unknown` 保留预留，请核对后使用新文件名。Responses 实际多图数量由服务决定，超出预期数量的文件仍依赖保存时的防覆盖检查。
- 后台须完成数据库初始化和恢复后才确认就绪；正在运行的不同二进制版本不混用同一队列，请等旧执行器结束后再使用新版。某些 SDK/IDE 宿主的 Windows Job 禁止后台脱离时，会明确返回启动失败及已入队编号；从独立 PowerShell 运行 `queue start`，不要重新提交任务。

### 开发验证

离线队列回归：`dotnet run --project tools/GptImageCli.Tests -- --queue-only`。
实际发布产物回归：先构建测试项目，再**直接运行** `tools/GptImageCli.Tests/bin/Debug/net10.0/GptImageCli.Tests.exe --queue-published <发布exe绝对路径>`，不要用会限制子进程脱离的 `dotnet run` 宿主。测试使用回环地址、假密钥和独立目录，验证实际四并发、429、终端菜单及退出行为。

`Test-QueueLive.ps1` 仅在显式 `-AllowPaidRequests` 后运行：指定 exe、节点名称及尚不存在的输出目录，提交一张 image2 参考图，再分别用 image2、flare、sunburst 执行 `edit`，记录实际 PNG 尺寸。会产生费用，不属于默认构建或离线测试。

2026-09-29 当前节点实测：三模型 `edit` 均一次 HTTP 200、实际 PNG `832x800`，三个请求执行时间存在重叠。证据范围及结果位置见 `CAPABILITIES.md`；不是所有端点或任意 16 倍数尺寸的通用保证。

### 队列结果与旧接口区别

`queue show` 的外层字段是 camelCase（如 `job.state`、`attempts[].httpStatus`），但 `job.result` 保留原 `CliReport` 的 **snake_case**（如 `exit_code`、`http_status`、`files`）。查看实际结果时，依据 `job.state` 与 `job.result` 判断，不能把查询回复的 `ok=true` 当成任务成功；`succeeded` 且结果 `ok=true`、`exit_code=0`、`files` 非空才报告已保存的数量与路径。失败也可能有部分输出。

`submit` 退出码：0 表示提交成功，2 表示捕获到操作错误且本次没有新提交的编号，1 表示已入队但后续启动等操作失败；错误详情在 `message`，不是同步报告的 `error`。查询成功的退出码不是任务的生成退出码。

## 安装与同步兼容用法

### 在项目中使用 skill

将完整发行目录放到 `.github/skills/gpt-image-cli/`，保留同级 `SKILL.md`、`gpt-image.exe`、`README.md`、`CAPABILITIES.md`、`queue-settings.default.json`、`release-manifest.json`。在支持项目 skills 的 AI 宿主中，可输入 `/gpt-image-cli`，或说「使用 gpt-image-cli skill 生成／修改图片」。若当前会话尚未发现新 skill，显式让 AI 读取该目录的 `SKILL.md`。

AI 根据已加载的 `SKILL.md` 定位同级 exe，无需 PATH 或原源码目录。平台以发行清单的 `runtime` 为准：`win-x64` Release 用于 Windows x64，不能在 macOS/Linux 直接运行；Native AOT 无需安装 .NET。图片保存位置与 skill 安装位置独立。安装验证只执行 `--help`，不会自动发送付费请求。

### 1. 安装检查（仅在需要时）

以下为 **PowerShell** 示例。先在终端进入交付的 `gpt-image-cli` 文件夹，执行一次：

```powershell
$exe = (Resolve-Path -LiteralPath './gpt-image.exe').Path
& $exe --help
& $exe --list-endpoints --json
```

`$exe` 保存实际路径，之后可切换到需要保存图片的项目目录；不需要把 exe 加入 PATH。若从其他目录调用，也可将 `$exe` 设置为实际 exe 的完整路径；带引号的路径前必须使用 `&`。`--list-endpoints` 是离线读配置，**不是** `--list`，列表成功不表示认证或生图成功。

### 2. 用已配置的节点同步生成一张图（兼容模式）

**下文从本节到“JSON 结果与文件安全”的不带 `submit` 示例描述旧同步接口**，其 snake_case JSON 与不自动重试行为保留。生图参数及连接规则也供 `submit` 复用，但队列回复与重试规则以上文为准。

将下面的“节点友好名称”替换为列表中实际名称，提示词可直接替换为自己的需求；会发送一次可能计费的请求：

```powershell
& $exe --endpoint-name '节点友好名称' --prompt '一只小胖狗' --json
```

有效默认节点可省略 `--endpoint-name`。默认生成一张 1024×640、中等质量 PNG，文件保存在**终端当前工作目录**（不是 exe 所在目录），程序自动命名；要保存到指定目录，追加 `--output './图片'`。结果中 `ok=true`、`files` 非空才表示已保存图片；`--json` 不会打开图片或改变网络请求格式。

**指定名称或 ID 就使用该节点的地址和 Key。** 本次解析会用配置的 `BaseUrl`、`ApiKey` 覆盖外层环境变量及明文 `--endpoint`、`--api-key`，无需手动清环境。不会修改配置文件、父进程或用户/系统环境；下一次不指定节点时仍可使用原外层值。节点不存在、配置不可读或节点缺 Key 时直接报错，不回退到外部连接。

### 3. 三种连接方式

先完成上面的 `$exe` 定位，以下方式按需选一种，不要逐条执行生图：

| 方式 | PowerShell 调用 | 地址与 Key 来源 |
| --- | --- | --- |
| 外层注入 | `& $exe --no-config --prompt '一只小胖狗' --json` | 启动器已安全设置的 `GPT_IMAGE_ENDPOINT`、`GPT_IMAGE_API_KEY`（也支持 OpenAI/Azure 兼容变量） |
| 节点友好名称（推荐） | `& $exe --endpoint-name '节点友好名称' --prompt '一只小胖狗' --json` | 所选配置节点，覆盖本次外层地址与 Key；重名改用 `--endpoint-id` |
| 明文传递（不建议） | `& $exe --no-config --endpoint 'https://服务地址' --api-key '<密钥占位符>' --prompt '一只小胖狗' --json` | 命令行参数；占位符不是有效密钥，真实值可能暴露在命令历史或进程列表 |

混用时以**节点名称/ID 对应配置的地址和 Key**为准；未指定节点时仍是“明文参数 → 环境变量 → 配置回退”。`--config` 仅选择配置文件，不等于选择节点。模型、尺寸等生成选项仍可单独指定，不受这条地址/Key 优先规则影响。

APIM 与 OpenAI 兼容节点都可使用 Key 认证，但 HTTP 头可能是 `api-key` 或 `Authorization: Bearer`。配置模式默认沿用节点认证头设置；独立模式按服务要求传 `--auth api-key` 或 `--auth bearer`，显式 `--auth` 仍可覆盖认证头。它不改变 Key 的来源。

### 独立连接与其他说明

- 在发行包目录执行 `./gpt-image.exe --help` 查看全部参数；帮助离线可用，无需端点或密钥。
- 同一 Windows 用户已配置 TrueFluentPro 时，CLI 默认只读 `%APPDATA%\TrueFluentPro\config.json`，复用终结点、密钥和图片模型，无需启动主程序或重复传 key。
- 独立使用时，由受信任的启动器或密钥管理器安全注入 `GPT_IMAGE_ENDPOINT` 与 `GPT_IMAGE_API_KEY`；可加 `--no-config` 禁用配置回退。CLI 不自动读取 `.env`。
- 不建议将密钥放入命令行；不得写入提示词、文档、日志或 URL，不要回显环境变量。远端连接使用 HTTPS，分享诊断前脱敏。
- 端点可为基址或当前模式的完整 API 地址；按服务要求选择认证，不自动探测路由。兼容环境变量与其他配置选项见 `--help`。
- 请求可能计费，须先获用户授权；将下列占位符替换为用户提供的内容与路径。

已有受信任启动器设置好 `GPT_IMAGE_ENDPOINT`、`GPT_IMAGE_API_KEY` 时：`& $exe --no-config --prompt '一只小胖狗' --json`。需要 `api-key` 认证的服务再加 `--auth api-key`；标准 Bearer 服务加 `--auth bearer`。不在命令行复制粘贴密钥。

生图：`./gpt-image.exe --prompt "<用户提示词>" --json`

单图编辑：`./gpt-image.exe --mode edit --image "<输入图路径>" --prompt "<用户编辑要求>" --json`

参考图生成新图：`./gpt-image.exe --mode responses --model "<支持 Responses 的文字模型>" --image "<输入图路径>" --image-action generate --prompt "参考原图风格，创作新图" --json`

参考图编辑：将上述命令的 `--image-action generate` 改为 `--image-action edit`，并在提示词中写明需要保留和修改的部分。两者通过 Responses `image_generation` 工具处理，不等同于 multipart `/images/edits`；需网关同时支持所选文字模型、工具和图片模型。`--image` 会以内联 data URL 发送，图片内容可能增加请求体大小；不经过 `/files`。网关是否真正出图以本次 JSON 结果为准，不要收到 503 后自动重试。

**不写参数即使用：生图（images）、1024×640、中等质量（medium）、PNG、1 张、当前工作目录。** 这些参数都可省略，只有需要改变时才指定。

输出可省略，也可只传目录，如 `--output "./图片"`，程序自动创建目录并生成“时间戳＋随机 ID＋序号”文件名，无需调用方生成 ID 或预查路径。指定 `--output "./图片/result.png"` 则使用该文件名，多张追加 `-01`、`-02`。已有目录或无扩展名路径按目录处理；新建的带点目录请以 `/` 或 `\` 结尾。实际文件路径从 `files` 获取。

多图编辑按顺序重复 `--image "<参考图路径>"`，在提示词中说明各图角色。连续修改将上次 `files` 中选定的结果作为下一次输入；输出仍可省略或复用同一目录，程序自动生成新文件名，这不是会话续接。

蒙版编辑追加 `--mask "<蒙版路径.png>"`。用户须提供带 alpha、与首图尺寸一致的 PNG，alpha=0 为可编辑区域；蒙版不是区域外像素锁。CLI 不解码核验尺寸和 alpha，日常调用不额外验图；用户明确要求验证时才检查。

## 选择主程序终结点

- 查看节点：`./gpt-image.exe --list-endpoints --json`，仅列出 ID、友好名称、图片模型，不输出地址或密钥。
- **主程序有节点，CLI 列表却没有？** 查看 stderr 的「未列出节点」说明：会区分未启用、节点类型不适用、尚未添加模型、未配置有效图片能力模型。模型名称叫 `gpt-image-2` 并不等于已勾选图片生成能力；请在主程序「AI 终结点管理 → 该节点 → 模型列表」展开模型，勾选图片生成能力并保存。列表为空也会明确提示，不表示节点被删除。筛选提示不是网络或认证失败，列举仍可退出 0。
- 指定名称：`./gpt-image.exe --endpoint-name "<友好名称>" --prompt "<用户提示词>" --json`；重名时改用 `--endpoint-id "<ID>"`，两者互斥。
- 不指定节点时，优先主程序默认图片模型引用；无默认引用时可使用唯一启用的图片节点。默认引用失效或存在歧义时提示选择，不随意猜测。
- 指定名称/ID 时，配置地址和 Key 成对覆盖本次外层值；无选择器时按参数、环境变量、配置回退取值，外部密钥必须有明确 URL，不会发往自动选中的配置节点。无选择器的显式 URL 借用配置密钥须匹配完整 BaseUrl，不能只匹配域名。
- 图片模型自动映射到配置部署名；尺寸、质量、数量仍使用 CLI 默认值，不继承主程序的生成参数。
- `--config "<路径>"` 可指定另一份配置；只读取，不创建、迁移或写回。配置回退支持内建 OpenAI-compatible、Azure OpenAI、APIM 的 API key 认证；AAD 登录及自定义 profile 请使用 `--no-config` 独立提供连接。

## 常用参数

有值参数支持 `--参数 value` 或 `--参数=value`；仅 `--image` 累积重复，其他同名参数最后一次生效。`--help`、`--json`、`--overwrite`、`--list-endpoints`、`--no-config` 是无值开关。任务名称 `--name` 仅用于 `submit`；别名与其余生图参数见 `--help`。

| 参数 | 默认值与合法范围 |
| --- | --- |
| `--mode` | 默认 `images`（生图）；改图指定 `edit`，兼容模式指定 `responses` |
| `--prompt` / `--prompt-file` | 无默认文本；依次优先使用提示词、文件、重定向 stdin，不能为空 |
| `--image` | edit 必填，responses 可选；本地 PNG/JPG/JPEG/WebP 文件可重复传入，最多 16 张；images 模式拒绝参考图 |
| `--image-action` | 仅 responses 可用：`generate` / `edit` / `auto`；带参考图默认 `generate`，`edit` 必须提供参考图 |
| `--mask` | 仅 edit，可选 PNG 蒙版 |
| `--auth` | 默认 `auto`；可选 `auto` / `bearer` / `api-key`；自动选择不代替服务认证要求 |
| `--image-model` | 图片模型或部署名；未配置时默认 `gpt-image-2` |
| `--model` | 仅 Responses 文字模型；未配置时默认 `gpt-4.1` |
| `--size` | 默认 `1024x640`；可用 `auto` 或合法 `宽x高`；显式使用 Responses 时按端点要求指定尺寸 |
| `--quality` | 默认 `medium`；可选 `low` / `medium` / `high` / `auto` |
| `--format` | 默认 `png`；可选 `png` / `jpeg`（别名 `jpg`）/ `webp`，不保证服务全部接受 |
| `--n` | 默认 1；本地范围为整数 1..10，不保证服务全部接受；Responses 不保证结果数量 |
| `--output` | 默认当前工作目录并自动命名；可指定目录或带扩展名的文件名，默认不覆盖 |
| `--overwrite` | 默认关闭；仅获明确覆盖授权后使用，无值本地开关 |
| `--timeout-minutes` | 默认 10；整数 1..35791；超时不代表未计费 |
| `--background` | 默认不发送；可选 `auto` / `opaque` / `transparent`；透明不能配 JPEG |
| `--output-compression` | 默认不发送；整数 0..100，仅 JPEG/WebP；接受参数不保证调节效果 |
| `--moderation` | 默认不发送；可选 `auto` / `low`，接受参数不代表安全拦截效果保证 |

## JSON 结果与文件安全（旧同步接口）

### 失败时怎么看

| 结果 | 含义与下一步 |
| --- | --- |
| HTTP 401 | exe 已启动且收到认证拒绝；查看认证头和密钥来源。指定名称/ID 时使用节点 Key，应核对该配置 Key 的有效性和认证方式；无需清环境，也不是靠改提示词或输出路径解决 |
| HTTP 403 | 可能涉及权限或网络访问策略，结合服务配置排查，不直接认定 key 无效 |
| HTTP 404 | 检查所选服务的路由及部署，不自动尝试多个地址 |
| 退出码 2、没有 HTTP 状态 | 参数、配置或输入检查失败，未发送请求；节点配置等 CLI 校验错误会在 JSON 的 `error` 与 stderr 中给出具体原因和操作建议 |

`POST [配置目标 URL 已隐藏]` 是隐私保护，不是 URL 缺失。同步调用在 HTTP 失败或超时后不自动重试；确认原因和付费授权后再执行一次。错误输出只报告密钥来源，不打印密钥值或服务端原文。

stdout 和 stderr 均使用 UTF-8。`--json` 让 stdout 输出单份 JSON，帮助、进度和错误走 stderr；它不是 JSON edit 请求开关。解析 JSON 时不要用 `2>&1` 把 stderr 混进 stdout；JSON 中的 `\uXXXX` 中文转义会在解析后还原。

| 关键字段 | 含义 |
| --- | --- |
| `ok` / `exit_code` | 是否成功及退出码，需与进程退出码一起检查 |
| `files` | 已保存文件的绝对路径数组；失败时也可能含部分成功结果 |
| `endpoints` | 列表操作的节点数组，元素为 `id`、`name`、`models`；列表成功不代表产图 |
| `mode` / `http_status` | 解析后的模式及 HTTP 状态码；未收到响应时后者为 null；没有 `status` 字段 |
| `api_error` / `error` | 服务错误的受限摘要与运行错误摘要，可能为 null；诊断分享前仍需脱敏 |
| `response_metadata` | 服务返回的尺寸、质量、格式元数据，不代替图像解码 |

退出码：**0** 成功（含帮助）；**2** 参数或输入校验失败；**1** 请求、解析、保存等运行失败。帮助成功且 `files=[]` 不代表产图。

日常调用只依据本次终端结果：进程及报告退出码为 0、`ok=true`、`files` 非空时，报告实际生成数量和路径，不再执行文件检查或图像工具。这里的成功指 CLI 报告保存成功，不表示画面、尺寸或透明度已经验收；图片交由用户检查。只有用户另行明确要求验图时才解码或查看，不把 HTTP 成功、扩展名或元数据当作视觉验收。

默认拒绝覆盖已有文件。images/edit 显式文件输出按 n 预检目标，冲突可在请求前退出 2；目录输出、Responses 或保存竞态的冲突可能在请求后退出 1，应检查部分 `files`。仅显式 `--overwrite` 才允许覆盖。

旧同步接口不自动重试；队列仅按配置对可重试的 429 自动重试。两者均不自动切路由、改模型或增加数量。超时或网络错误仍可能已计费，人工重新提交/调用须重新获得用户授权。

## 本地构建与 CI/CD

在源码 `tools/GptImageCli` 目录用 PowerShell 7 执行，成功后在资源管理器中选中完整的 `gpt-image-cli` 文件夹，方便直接复制；不运行生图测试。

| 脚本 | 输出（相对于 GptImageCli 项目） |
| --- | --- |
| `./Build-Debug.ps1` | `bin/Debug/net10.0/gpt-image-cli/`，普通调试构建，运行需要 .NET 10，须保留目录中的 DLL 等文件 |
| `./Build-Release.ps1` | `bin/Release/net10.0/win-x64/publish/gpt-image-cli/`，Native AOT 独立 skill 包，含 exe、说明、SKILL.md 与发行清单 |

两者支持 `-NoOpen`；Release 默认 win-x64，可传 `-Runtime win-arm64`。构建需要 .NET 10 SDK，Release 另需 Visual Studio C++ 桌面开发工具链（ARM64 需对应组件）。请分发 Release 输出中的整个 `gpt-image-cli` 目录，而不是其上层构建目录或仅 SKILL.md。

Release AOT 使用体积优先优化、JSON 源生成和固定地区无关格式，裁掉未用 HTTP/3 与框架遥测；保留中文内容、异常文本和堆栈。SQLite 使用 Windows 系统库，不为队列引入 GUI、ORM 或额外原生数据库文件。修改这些裁剪设置后须重新验证实际发布产物。

交付文件全部收在 `gpt-image-cli/` 内：根目录放 `SKILL.md`、exe、两份说明、`queue-settings.default.json` 和 Release 清单，不再嵌套 `skills/gpt-image-cli`，也不在 skill 的上一层放 CLI 文件。Debug 所需 DLL 等也在 skill 内；运行时队列数据位于用户队列目录，不是发行目录。

`Publish.ps1` 为共用发布入口，默认也输出项目的 Release skill 目录；自动化可用 `-OutputDirectory` 指定最终 skill 目录（脚本不会再附加子目录）。GitHub Release 工作流为 x64、ARM64 分别发布主程序、Updater 和 CLI，再整体压缩上传；完整 skill 位于主包的 `skills/gpt-image-cli/`。本地常规构建不使用仓库根 `artifacts`；该目录仅用于专门打包、测试产物及 CI 暂存。

## 发行文件职责

- `gpt-image.exe`：独立 CLI；使用与操作系统和架构匹配的发行包。
- 随主程序交付时位于 `skills/gpt-image-cli/`；将整个 `gpt-image-cli` 复制到目标项目所用的 skill 根目录即可，例如 `.github/skills/` 或 `.claude/skills/`，具体发现规则以 AI 宿主为准。无需安装或启动主程序，也不依赖原仓库位置。
- `release-manifest.json`：发行信息；以其中 `mode` 判断运行模式。仅 `NativeAot` 表示原生 AOT；`Managed` 是自包含托管模式，**两者均无需另装 .NET**，不能混淆模式与版本。
- `README.md`：使用、参数和结果说明；`CAPABILITIES.md`：源码已实现能力及适用边界，不是在线测试通过报告。
- `queue-settings.default.json`：默认队列模板，同时内置于 exe；运行时编辑用户队列目录中的 `queue-settings.json`，不把密钥写入模板。
- `SKILL.md`：交付包根目录中的 AI 调用规程，与 exe 及两份说明同级；源码模板位于 `skills/gpt-image-cli/SKILL.md`。让 AI 显式读取或按宿主机制注册；普通 `skills/` 目录不保证自动发现。
- 文档与 skill 是包内伴随文件，不嵌入 exe；保留目录结构以维持相对链接。
