# GPT-Image CLI 独立发行包

供人和 AI 调用的生图/改图工具。图片模型/部署名默认 `gpt-image-2`，不依赖主程序、真实配置或第三方 NuGet 包。**调用时明确写 `--mode images` 或 `--mode edit`**；程序为兼容旧用法仍默认 `responses`，不要依赖此默认值。

端点实测与能力边界见 [CAPABILITIES.md](./CAPABILITIES.md)：2026-09-07 的 47 项既有实测逐项报告，包含拒绝和尺寸差异，**不是“全部通过”**。早期 `request.body` 是模板，CLI 有效参数以 `cli_args` 同名最后一次为准；这些记录不是 wire capture。不能把 REST 测试成功等同于 CLI 已支持，更不能承诺所有云端能力完全兼容。

## 独立构建与发布

在仓库根目录执行，需要 .NET 10 SDK；默认 Native AOT 发布在 Windows 上还需要 Visual Studio 2022 或更新版本的“使用 C++ 的桌面开发”工具链。不需要启动或构建主程序，不读取主程序配置或 `.env`：

```powershell
dotnet build ./tools/GptImageCli/GptImageCli.csproj -c Release
& ./tools/GptImageCli/Publish.ps1 -Runtime win-x64
```

默认产物目录为仓库 `artifacts/gpt-image-cli-aot/win-x64/`，包含 `gpt-image.exe`、本 README、存在时的 `CAPABILITIES.md` 和 `skills/gpt-image-cli/SKILL.md`。Native AOT 编译成独立原生 exe，不要求目标机器安装 .NET，也不使用旧单文件包的运行时库自解压机制。文档/skill 是伴随文件，不嵌入 exe。首次发布可能需要联网下载编译工具包，但不会调用生图 API 或读取密钥。

2026-09-07 Native AOT 产物：**5,250,560 字节（5.01 MiB）**，较原 73,574,097 字节缩小 **92.9%**；每次发布的实际大小和 SHA256 写入随包 `release-manifest.json`，重新链接不保证哈希相同。实际 exe 的 9 个离线场景、287 条断言通过，包括 images/edit/responses、本地 HTTP、JSON、文件保存及错误/防覆盖；本轮未重跑云请求，不将历史实测指纹套给 AOT 包。

旧 Managed 自包含包保留在 `artifacts/gpt-image-cli-standalone/win-x64/`；G18/G19 对应其 SHA256 `2FF4EE10BDD7E9E7A6D4438E34A9CCE53FF9B22BC1731B4AEF746AB621A21093`。需要重新生成 Managed 包时给发布脚本添加 `-Mode Managed`，该模式仍包含完整运行时、体积较大，并需运行时自解压。能力报告的图片/JSON 相对链接指向仓库 artifacts，不随单独 exe 自动分发。

`-Mode` 默认 `NativeAot`，可选 `Managed`；`-Runtime` 默认 `win-x64`。其他 RID 必须满足对应模式的编译工具链与平台支持要求，并在目标平台另行验证，本轮仅验证 Windows x64。脚本不删除已有目录、不终止进程，发布/复制失败明确退出 1。已有同名发布文件会更新；若目标 exe 被占用，先自行关闭该程序再发布。缺失的可选文档不会清除旧副本，分发前应核对其版本。

离线验收（帮助不要求端点、密钥或提示词）：

```powershell
& ./artifacts/gpt-image-cli-aot/win-x64/gpt-image.exe --help
& ./artifacts/gpt-image-cli-aot/win-x64/gpt-image.exe --json --help
& ./tools/GptImageCli/Test-Published.ps1
```

`Test-Published.ps1` 仅运行回环假服务器，不读取真实配置或访问云端，证据输出到仓库 `artifacts/gpt-image-cli-published-tests/`。AOT 适配使用具名 DTO + JSON 源生成元数据，保留请求格式与报告 schema，禁用反射序列化回退。依据：[Native AOT 官方说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)、[JSON 源生成与禁用反射](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)。

配套 [skill](./skills/gpt-image-cli/SKILL.md) 面向使用 exe 的 AI。请让 AI 显式读取发行包内该文件，或按宿主支持的机制注册；普通 `skills/` 目录不保证被所有 AI 宿主自动发现。不要修改全局指令；不要只复制 SKILL.md 后仍假定其相对链接有效。

## 安全配置与调用

通过受信任的启动器/密钥管理器给子进程注入环境变量，**密钥不放命令行、日志、提示词、skill 或提交文件**。默认不会读取任何配置文件；不要为帮助或构建加载真实环境。不要把密钥放入端点 URL 查询字符串，CLI 会记录请求 URL；诊断输出分享前仍须脱敏。

| 配置 | 优先级从左到右 |
| --- | --- |
| 端点 | `--endpoint` → `GPT_IMAGE_ENDPOINT` → `OPENAI_BASE_URL` → `AZURE_OPENAI_ENDPOINT`，首个非空值 |
| 密钥 | `--api-key`（仅兼容，不推荐）→ `GPT_IMAGE_API_KEY` → `OPENAI_API_KEY` → `AZURE_OPENAI_API_KEY`，首个非空值 |
| 文字模型 | `--model` → `GPT_IMAGE_TEXT_MODEL` → `gpt-4.1`；环境变量未设置时使用默认值 |
| 图片模型 | `--image-model` → `GPT_IMAGE_MODEL` → `gpt-image-2`；环境变量未设置时使用默认值 |
| API 版本 | `--api-version` → `AZURE_OPENAI_API_VERSION`，否则不追加 |

端点可以是基址或当前模式的完整 API 地址；不会自动探测多个路由。`auto` 认证对含 `openai.azure.com` 的地址选 `api-key`，其余选 Bearer；公司 APIM 类端点应显式使用 `--auth api-key`。

以下命令会产生付费请求，**仅在用户授权且已安全注入环境变量后执行**。低成本起点：`1024x640`、`low`、`n=1`、PNG。在发行包目录中：

```powershell
$output = Join-Path ([IO.Path]::GetFullPath('.')) ('result-' + [guid]::NewGuid().ToString('N') + '.png')
if (Test-Path -LiteralPath $output) { throw '输出已存在，请换用新路径。' }
& ./gpt-image.exe --mode images --prompt '极简帆船插画' --size 1024x640 --quality low --format png --n 1 --output $output --json
```

改图使用 `--mode edit --image <输入图绝对路径>`，多图按角色依次重复 `--image`，并在提示词中写明第 1/2 张的用途。连续修改时将上次报告中的结果文件作为新的输入，每轮换用新的输出全路径；不是 Responses 会话续接。

## 全部参数与默认值

有值参数支持 `--name value` / `--name=value`；仅 `--image` 可累积重复，其他同名参数最后一次生效（不同别名按下表优先级）。开关 `--json` / `--help` / `--overwrite` 无值，不能写 `--overwrite=true` 或 `--overwrite false`。未知参数或缺值退出 2，发送请求前失败；不要编造参数。

| 参数（含别名） | 含义、默认与约束 |
| --- | --- |
| `--help` | 离线帮助；无参数也显示帮助。单独 `-h` 或 `help` 等价 |
| `--json` | stdout 单份 JSON，帮助/进度/错误走 stderr |
| `--endpoint` | 必需（可由环境变量替代）；绝对 HTTP/HTTPS URL，无用户名、密码、片段；实际远端应使用 HTTPS |
| `--api-key` | 必需（可由环境变量替代）；不要在命令行传真实密钥 |
| `--prompt` | 提示词；优先于 `--prompt-file`，再回退到重定向 stdin；不能为空 |
| `--prompt-file` | 从文件读取提示词；不自动读取配置 |
| `--mode` | `responses`（别名 `response`，默认）、`images`（`image` / `generations`）、`edit`（`edits`） |
| `--image` | 仅 edit 且必填，可重复；现有 PNG/JPG/JPEG/WebP 文件；云端接受范围可能更窄 |
| `--mask` | 仅 edit；CLI 仅校验存在、`.png` 扩展名和 8 字节 PNG signature，不解码验证。调用方须验 alpha 和与首张输入的尺寸一致性；alpha=0 为编辑区域，不保证区域外像素锁定 |
| `--auth` | `auto`（默认）、`bearer`、`api-key`（别名 `apikey`） |
| `--model` | 仅 Responses 文字模型，默认 `gpt-4.1`；images/edit 不需要 |
| `--image-model` | 图片模型/部署名，默认 `gpt-image-2`；Responses 使用部署请求头 |
| `--api-version` | 需要时追加查询参数；完整地址已有 `api-version` 时保留原值 |
| `--size` | 默认 `1024x1024`，可用 `auto`；建议显式 `1024x640` |
| `--quality` | `low` / `medium`（默认）/ `high` / `auto` |
| `--format` / `--output-format` | `png`（默认）/ `jpeg`（`jpg`）/ `webp`；前者优先 |
| `--n` / `--count` | 整数 1..10，默认 1；前者优先。Responses 仅以指令请求多结果，不保证数量 |
| `--output` / `--out` | 输出目录或文件名，默认当前目录；前者优先；默认不覆盖 |
| `--overwrite` | 无值、本地开关，默认 false；只有用户明确授权覆盖时使用，不发送到 API |
| `--timeout-minutes` | 整数 1..35791，默认 10；超时不代表云端未计费 |
| `--background` | 可选 `auto` / `opaque` / `transparent`；透明不能配 JPEG |
| `--output-compression` | 可选整数 0..100，仅 JPEG/WebP |
| `--moderation` | 可选 `auto` / `low` |
| `--user` | 可选字符串，仅 images/edit，Responses 会拒绝；不要填秘密或不必要的个人信息 |

`mask/background/output-compression/moderation/user` 仅显式提供时发送。图片模型名恰为 `gpt-image-2` 且使用 images/edit 时，显式尺寸边长须为 16 倍数、各边 ≤3840、比例 ≤3:1、总像素 655360..8294400；其他部署别名不等于已经通过这些本地校验。请求尺寸不代表产物真实尺寸，CLI 不缩放产物。

超过 3,686,400 像素按官方 experimental 范围处理；没有做 4K/最大尺寸压力测试。`auto` 的实测产物（G07 1254×1254、E12 1586×992）不受显式输入 16 倍数约束的精确输出承诺。

edit 使用 `/images/edits` + multipart：单图字段 `image`，多图字段 `image[]`，按输入顺序发送；蒙版针对首图。CLI 只做有限输入校验，不解码确认所有图片/蒙版尺寸和 alpha。

## JSON 报告与退出码

同时检查进程退出码与报告，不要只看 HTTP 200。当前没有名为 `status` 的 JSON 字段：**HTTP status 对应 `http_status`**。

| 字段 | 含义 |
| --- | --- |
| `ok` | 布尔值，等于 `exit_code == 0`；帮助成功不代表生成过图片 |
| `exit_code` | 0 成功（含帮助）；2 参数/输入校验失败；1 请求、解析、保存等运行失败 |
| `files` | 已成功保存的文件绝对路径数组；帮助为空；失败时可能包含部分已保存文件 |
| `mode` | 解析后的 `images` / `edit` / `responses`；帮助或解析失败为 null |
| `http_status` | HTTP 状态码；没有收到响应时为 null |
| `request_id` | 从响应头提取的请求 ID，可能为 null |
| `elapsed_ms` | 本次调用耗时，毫秒 |
| `usage` | 服务返回的数值型 token 使用量白名单及明细，可能为 null；不是费用账单 |
| `api_error` | 仅当前响应根 `error` 对象；否则 null。对象字段 `code/type/param/message`，详见下述安全规则 |
| `error` | 成功为 null；失败为错误摘要，细节检查已脱敏 stderr |
| `response_metadata` | 响应中 `size` / `quality` / `output_format` 白名单元数据对象数组，可能为空；不是图像解码验证结果 |

使用 `--json --help` 时 stdout 仍只有报告，帮助正文在 stderr，`ok=true`、`files=[]`、`mode/http_status=null`。生成成功后应检查 `files` 存在且非空，用图像解码器核验真实格式、尺寸及透明图 alpha；不要相信扩展名或元数据就宣布成功。

`api_error.code/type/param` 仅保留长度 1..64、匹配 `\A[A-Za-z0-9_.\[\]-]+\z` 且不含当前 API key 的字符串；不安全、非字符串或缺失值返回 null，不截断或清洗后放行。`message` 固定为 `服务端返回错误。`，不回显服务端 message。它与顶层 `error` 摘要是不同字段；旧版历史报告可能没有 `api_error`。usage 仅保留 `input_tokens/output_tokens/total_tokens/input_tokens_details/output_tokens_details/text_tokens/image_tokens/cached_tokens/reasoning_tokens` 数值及递归对象白名单。

默认保存用 `FileMode.CreateNew` 防止覆盖，包括预检查之后发生的竞态。images/edit 的显式文件路径会提前按请求 n 检查所有目标（多结果为 `名称-01` 等），冲突时**请求前退出 2**。目录名含 UTC 秒时间戳和 GUID；Responses 数量未知、目录模式及预检查后的冲突在实际保存时保护，可能已发请求，退出 1 且保留已成功保存的 `files`。显式 `--overwrite` 才使用 `FileMode.Create` 覆盖并截断文件。仍建议唯一绝对输出路径，扩展名匹配 `--format`，不要为失败自动覆盖或重试。

## 能力边界与验证

- 当前公司 APIM 的 PNG/JPEG 成功，G10 WebP 输出 HTTP 400；输入 WebP 未测。G11 的 alpha=0 为 441770、0<alpha<255 为 213590；最新发行版 G18 分别为 432870 / 222490，真实透明度已解码验证，但不能保证所有图片透明。
- direct edit 已精确返回 1024×640、640×1024、1440×480、816×816、1536×864；E14 512×512 HTTP 400。优先选已验 direct edit，而非宣称“任意尺寸”。
- Responses 仅保留改图边界：R04 请求 1024×1024 实际 1254×1254，R05 1536×1024 精确，R06 请求 1024×1536 实际 1016×1548，R07 1024×640 HTTP 400。R02/R03 同样尺寸差异；R01 聊天链测属于已完成历史，不继续扩展，也不引入 CLI 聊天。
- JPEG compression 0/50/100 均被接受且可解码，但三者 DQT 表哈希相同、均值均为 1，未证明压缩调节有效；不同内容的文件字节大小不是压缩效果证据。蒙版是编辑指导，E04 区域外 RGB MAE=3.3314，非像素锁定。
- 不传 `input_fidelity` / `--input-fidelity`：E07 REST 显式 high 被接受，但没有保真行为提升的证明，CLI 未提供该参数。
- CLI 尚未支持流式输出、JSON 请求体 edit、Responses 多轮会话/多图输入（包括 `previous_response_id`）；REST 测试另有结果不改变此边界。`--json` 仅指本地报告格式，不是 JSON edit。
- 不自动重试、切路由、改模型或增加数量；超时、网络错误也可能已计费，重试前先让用户决定。云端实测需用户授权；本轮 47 项已获授权，最终分析/构建不重放请求。
- 最终离线回归 **355 checks 全部通过**，入口：`dotnet run --project ./tools/GptImageCli.Tests/GptImageCli.Tests.csproj`；完整解决方案构建成功，仅有仓库既有依赖漏洞警告。
- 仓库内离线复核：`& ./tools/GptImageCli/Analyze-Capabilities.ps1`。Windows PowerShell 7 + 现有 System.Drawing；无须安装依赖。只读 47 项 JSON/PNG/JPEG，重建参数、解码、DQT/蒙版定量分析并核对历史哈希，仅写 `artifacts/gpt-image-capabilities/20260907/analysis-derived.json`。`-EvidenceRoot` 仅接受该工作区的此证据根目录，拒绝越界与重解析点，不读配置/密钥、不访问网络。
- 未测最大 n=10、16 张输入、50MB 输入、远程 URL/file_id、Batch 24h、安全拦截效果和人物保真；不自动启动付费 Batch。API 文档范围不等于当前 APIM 全量支持，更不推广所有 Azure 区域/API 版本。
