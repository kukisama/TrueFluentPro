# GPT-Image CLI 能力与边界

本文区分源码能力和已记录的实测结果；本机节点的成功不代表所有端点、尺寸或视觉效果都有保证。具体用法见 [README.md](./README.md)，普通 AI 调用按发行包中的 `SKILL.md` 执行。

## 请求能力（源码已实现）

依据 `RequestFactory.cs`、`CommandLineParser.cs`、`ParameterValidation.cs`：

| 模式 | 请求构造 | 本地约束与远端边界 |
| --- | --- | --- |
| `images`（默认） | POST `images/generations`，JSON 文本生图；模型、提示词、尺寸、质量、格式，`n>1` 时发送 `n` | 不接受参考图；服务是否接受参数与数量需单独确认 |
| `edit` | POST `images/edits`，multipart；单图字段 `image`、多图字段 `image[]`，可带 PNG `mask` | 至少 1 张参考图，最多 16 张；支持本地 PNG/JPG/JPEG/WebP，不是 JSON edit |
| `responses` | POST `responses`，文字模型配合 `image_generation` 工具；本地参考图以内联 `input_image` data URL 发送 | **支持带图请求构造**，不是“Responses 不支持图片”；不经过 `/files`，仍取决于网关、文字模型及图片工具/模型的支持 |

- Responses 的 `--model` 选择文字模型，`--image-model` 选择图片模型/部署；请求带 `x-ms-oai-image-generation-deployment` 头。带参考图时工具包含图片模型与 `action`，默认 `generate`，可选 `edit` / `auto`；`edit` 必须有参考图。`n>1` 通过文字指令要求多个变体，不保证实际数量。
- 默认 `images`、`1024x640`、`medium`、PNG、1 张、10 分钟超时；无配置覆盖时图片模型为 `gpt-image-2`、Responses 文字模型为 `gpt-4.1`。可设置质量、格式、背景、压缩及审核参数；参数可发送不等于服务必接受或效果保证。
- 本地 `n` 范围 1..10、参考图最多 16 张。非 Responses 的逻辑模型 `gpt-image-2` 校验尺寸为 `auto` 或 16 倍数宽高，单边 ≤3840、长宽比 ≤3、总像素 655360..8294400；这不是其他模型或端点的统一规格。
- 蒙版只用于 edit：检查 PNG 文件头，不解码核验 alpha 或尺寸。用户须提供与首图尺寸一致、alpha=0 表示可编辑区的蒙版；不承诺区域外像素锁定。透明背景不允许 JPEG；实际透明、尺寸、压缩及画面由用户验收。
- Bearer / `api-key` 认证和配置节点路由已实现；显式节点名称/ID 用该节点的地址与 Key 覆盖本次外层连接。AI 不读取配置 Key。CLI 不自动探测或切换路由，不自动读取 `.env`。
- 当前 CLI 未暴露 SSE、JSON edit、`input_fidelity`、Batch、`file_id` 或会话续接入口；连续编辑是把已保存文件作为新的输入。此边界仅指当前 CLI，不代表上游平台不支持。

## 执行与持久队列

依据 `Program.cs`、`QueueApplication.cs`、`QueueModels.cs`、`QueueConsole.cs`、`QueueStore.cs`、`QueueWorker.cs` 和 `queue-settings.default.json`：

- 同 exe 的 `submit [生图参数] [--name 名称] --json` 持久入队并尝试启动后台。camelCase `QueueReply` 的 `ok/message/jobId/workerRunning` 表示提交情况，**不是生成结果**；后台启动失败也可能已经分配编号，不可据此重复提交。
- `queue` 默认交互管理；已实现 `list`、`show`、`pause`、`resume`、`cancel`、`clear --yes`、`config`、`start`、`worker`，筛选/分页参数见 README。菜单 6 提供详情与尝试记录，菜单 8 编辑数值配额；没有任务重试、日志导出或登录自启命令。
- 默认同 Windows 用户全项目共享 `%LOCALAPPDATA%\GptImageCli`；`--queue-dir` 可隔离到另一本机目录，提交与管理必须一致。配置首次由内置默认生成，已有配置保留；可手改 `queue-settings.json`，模型映射可扩展，菜单 8 不改模型或启用状态。
- 默认三组：`image2` → `gpt-image-2` 为 **9 RPM / 4 并发**；`flare` → `gpt-image-2.5-flare`、`sunburst` → `gpt-image-2.5-sunburst` 各 **2 RPM / 1 并发**。各组独立，按模型归组，不按节点分别计额；未映射模型拒绝入队。
- 近 60 秒滚动限额配合间隔派发，按派发尝试/请求数计量，**不是 Token 或图片张数**；`n>1` 仍算一次请求，重试另计。按图片额度收费/限流的服务需用户自行对应配置，不能把本地限速当成服务配额保证。
- 默认可重试 HTTP 429 等待至少 **61 秒**，额外重试 **2 次**（最多总 **3 次**）；更长 `Retry-After` 优先，并冷却该组。`insufficient_quota`、`billing_hard_limit_reached` 不重试；其他 HTTP 错误、超时、网络及保存错误不自动重试。
- worker 在无未完成任务时退出；暂停/禁用仍有任务则等候，关闭管理不停止后台。关机后须再次触发恢复：下次 `submit` / `queue start`（继续操作也尝试启动），无登录启动功能。恢复的原 `running` 转 `unknown`，需人工确认，不自动重画。
- `cancel` / `clear` 仅取消 `pending` / `retry_wait`，保留历史与输出，不撤回在途请求。菜单 3 同时解除全局/各组暂停，无 ID 的 `queue resume` 仅解除全局；菜单 4 清除范围不随列表筛选缩小。
- 旧的不带 `submit` 同步接口继续保留原 snake_case JSON，且不自动重试；队列与同步调用不能混用成功判据。

## 数据与结果边界

依据 `QueuePaths.cs`、`QueueSubmission.cs`、`QueueWorkerHost.cs`、`CliReport.cs`、`ImageResponseParser.cs`、`ImageResultWriter.cs` 及项目依赖：

- 队列面向 Windows 10/11，使用系统 `winsqlite3`，通过 `Microsoft.Data.Sqlite.Core` / `SQLitePCLRaw.provider.winsqlite3` 访问，不附带额外 native SQLite 库。后台独立进程可能被宿主权限限制；报告启动失败时先保留任务编号。
- DPAPI `CurrentUser` 加密完整任务参数与解析后的连接快照。worker 用提交时的快照，密钥轮换不会更新旧任务，须取消可取消的旧任务再重新提交。此保护不是全库/全目录加密，也不是可跨用户搬迁的凭据格式。
- 参考图及蒙版复制到队列 `inputs/`；副本未由上述 DPAPI 加密。输出在提交时固定为绝对路径。`queue-settings.json` 无密钥；任务名称、结果路径、事件与尝试记录不属于加密载荷，不要在名称中放秘密。
- `queue.db` 内含 `events` / `attempts`；`queue show` / 菜单 6 返回任务结果与尝试记录，不提供 events 导出。外层 camelCase，`job.result` 是原 snake_case `CliReport`（如 `exit_code`、`http_status`、`files`）。
- 响应解析支持 Images 的 `data[].b64_json/url` 及 Responses 的 `image_generation_call.result` 图片数据；保存支持 Base64 和下载 URL，返回已保存文件绝对路径，默认拒绝覆盖，失败可能保留部分输出。
- `submit` 入队成功、`queue show` 查询成功、HTTP 成功、图片保存成功和视觉效果验收是不同结论。只有生成结果报告保存成功才报告文件；超时/中断可能仍在服务端执行或已计费，不据此自动重画。

## 发布与验证状态

默认队列模板既内置于 exe，也复制到输出目录；运行时修改用户的 `queue-settings.json`。未改动用户安装目录，当前交付产物位于项目的 Release skill 目录。

2026-09-29 核验：

- Native AOT 发布成功；实际 exe 的回环服务观察到 image2 四个同时未完成的请求，另一模型队列可独立发送；429 重试、耗尽、暂停取消、菜单修改并发和后台退出均通过。
- 默认策略 61 秒等待、2 次额外重试用可控时间验证；产物级测试将等待改为 1 秒以缩短测试，不将其冒充真实等待 61 秒的在线结果。
- 当前配置节点真实队列请求：image2 生成参考图 HTTP 200、实际 PNG `1024x640`；image2、image2.5 flare、image2.5 sunburst 各一次 `edit` 均 HTTP 200，实际 PNG **`832x800`**，都未重试。三个 edit 的请求时间存在重叠。该结果支持这组 16 像素倍数尺寸，不代表任意 16 倍数尺寸均符合服务的面积、比例或上限要求。
- 真实结果记录：仓库 `artifacts/gpt-image-queue/live-20260929-edit/results.json`。核验实际 PNG 头尺寸，不作画面内容或区域保持的视觉验收承诺。
- 后续输出预留、后台就绪握手及 AOT 裁剪通过本地回归，未为这些收尾改动增加付费请求。
- 使用 Windows 系统 SQLite，不携带额外 SQLite 原生 DLL；AOT 保留异常文本/堆栈，关闭未使用的 HTTP/3、框架遥测及地区格式依赖，中文参数和配置、实际终端菜单回归通过。