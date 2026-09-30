# GPT-Image CLI 能力与边界

参数速查见 [README.md](./README.md)。以下区分本地实现契约与真实端点实测，不以请求构造能力推断远端兼容性。

## 请求契约

依据 `CommandLineParser.cs`、`ParameterValidation.cs`、`RequestFactory.cs`。

| 模式 | 本地请求构造 | 边界 |
| --- | --- | --- |
| `images` | POST `images/generations`，JSON | 不接受参考图 |
| `edit` | POST `images/edits`，multipart；单图 `image`、多图 `image[]`，可带 `mask` | 1..16 张本地 PNG/JPEG/WebP |
| `responses` | POST `responses`，文字模型 + `image_generation`；参考图为 `input_image` data URL | 不经 `/files`；依赖网关、文字模型和图片工具/模型支持 |

- Responses 的 `--model` 是文字模型，`--image-model` 是图片模型/部署；请求带 `x-ms-oai-image-generation-deployment`。带图时工具含图片模型及 `action`，默认 `generate`；`edit` 必须有图。
- 本地 `n=1..10`；Responses 仅用文字要求多变体，不保证数量。可发送的尺寸、质量、格式、背景、压缩和审核参数不等于远端接受或效果保证。
- 非 Responses 的逻辑模型 `gpt-image-2`：尺寸为 `auto` 或宽高均为 16 倍数，边长 ≤3840、比例 ≤3、像素数 655360..8294400；其他模型仍以端点限制为准。
- edit 蒙版只检查 PNG 文件头，不解码检查尺寸/alpha；输入须与首图同尺寸，alpha=0 为可编辑区，不是区域外像素锁。透明背景不能配 JPEG。
- 当前 CLI 未暴露 SSE、JSON edit、`input_fidelity`、Batch、`file_id` 或会话续接；连续编辑以已保存图片作新输入。这不代表上游平台没有相应能力。

## 队列、结果与文件契约

依据 `QueueApplication.cs`、`QueueModels.cs`、`QueueSubmission.cs`、`QueueStore.cs`、`ImageResultWriter.cs`。

- `submit` / `queue` 必须是首个参数。`QueueReply` 为 camelCase；提交字段 `ok/message/jobId/workerRunning` 不包含生成结果。后台启动失败仍可能已有 `jobId`。
- `submit` 退出码：0 为提交成功；2 为捕获到错误且未取得新编号；1 为已入队后发生错误。查询退出码表示查询操作，不是生成结果。
- `queue show` 的 `job.result` 保留 snake_case：保存成功要求 `job.state=succeeded`、`result.ok=true`、`result.exit_code=0`、`result.files` 非空；失败也可能有部分文件。保存成功不等于视觉验收。
- `--json` 只控制本地报告；stdout 为 JSON，stderr 为进度/错误，不能合并后解析。旧同步接口不受队列调度，也不自动重试。
- 队列按模型映射共享请求限额，不按节点分额；未映射模型拒绝入队。`n>1` 仍计一次请求，重试另计；仅按配置重试可重试 HTTP 429，配额不足错误不重试。
- 队列使用 Windows 10/11 系统 `winsqlite3`。参数及连接快照用 DPAPI `CurrentUser` 加密，旧任务不随密钥轮换更新；不是全库加密，参考图/蒙版副本及结果路径不在保护范围。
- 输出在提交时固定为绝对路径，默认拒绝覆盖；活动任务不能预留相同目标，即使有 `--overwrite`。`unknown` 保留预留；Responses 超出预计数量的输出依赖保存时防覆盖。
- 图片文件后缀必须匹配 `--format`：PNG 用 .png，JPEG 用 .jpg/.jpeg，WebP 用 .webp；overwrite 和 Responses 也不绕过此校验。图片交付目录不能包含队列运行目录，不生成 JSON 回执、提示词文件或日志旁车。
- 参数快照、任务/尝试诊断及执行器就绪信息均在 SQLite；`worker_runtime` 为兼容旧版数据库的新增表，新执行器不旁写 worker-info.json，旧执行器握手仅兼容读取。配置 JSON、SQLite 运行文件与锁保留在队列目录，安装清单保留在安装目录。
- 中断的 `running` 恢复为 `unknown`，不自动重画；超时/中断可能已计费。取消/清除只作用于 `pending/retry_wait`，保留历史和文件，不撤回在途请求；没有任务重试、事件导出或登录自启命令。

## 已记录实测（2026-09-29）

- 当前节点：image2 生成参考图为 HTTP 200、PNG `1024x640`；image2、flare、sunburst 的 `edit` 各一次 HTTP 200，实际 PNG 均为 **`832x800`**，无重试，三个编辑请求时间有重叠。
- 证据：仓库 `artifacts/gpt-image-queue/live-20260929-edit/results.json`；核验了 PNG 头尺寸，未据此验证画面或蒙版保持效果。
- **不是任意 16 倍数尺寸均可用**，也不是跨端点保证；例如 `512x512` 不满足上述 image2 本地最小像素数。
- 发布 exe 回环测试记录：image2 四并发及跨组独立派发、429、取消和后台退出通过；默认 61 秒等待用可控时间验证，产物测试缩为 1 秒，不是在线实等 61 秒。