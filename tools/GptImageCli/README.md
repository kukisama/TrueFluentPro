# GPT-Image CLI 使用指南

独立的图片生成与编辑命令行工具，供人和 AI 调用，不依赖主程序。服务能力与限制见 [CAPABILITIES.md](./CAPABILITIES.md)，不能将某个端点的支持范围推广到所有云端。

## 开始使用

### 1. 找到 exe，先做离线检查

以下为 **PowerShell** 示例。先在终端进入交付的 `gpt-image-cli` 文件夹，执行一次：

```powershell
$exe = (Resolve-Path -LiteralPath './gpt-image.exe').Path
& $exe --help
& $exe --list-endpoints --json
```

`$exe` 保存实际路径，之后可切换到需要保存图片的项目目录；不需要把 exe 加入 PATH。若从其他目录调用，也可将 `$exe` 设置为实际 exe 的完整路径；带引号的路径前必须使用 `&`。`--list-endpoints` 是离线读配置，**不是** `--list`，列表成功不表示认证或生图成功。

### 2. 用已配置的节点生成一张图

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

**不写参数即使用：生图（images）、1024×640、中等质量（medium）、PNG、1 张、当前工作目录。** 这些参数都可省略，只有需要改变时才指定。

输出可省略，也可只传目录，如 `--output "./图片"`，程序自动创建目录并生成“时间戳＋随机 ID＋序号”文件名，无需调用方生成 ID 或预查路径。指定 `--output "./图片/result.png"` 则使用该文件名，多张追加 `-01`、`-02`。已有目录或无扩展名路径按目录处理；新建的带点目录请以 `/` 或 `\` 结尾。实际文件路径从 `files` 获取。

多图编辑按顺序重复 `--image "<参考图路径>"`，在提示词中说明各图角色。连续修改将上次 `files` 中选定的结果作为下一次输入；输出仍可省略或复用同一目录，程序自动生成新文件名，这不是会话续接。

蒙版编辑追加 `--mask "<蒙版路径.png>"`。蒙版须带 alpha、与首图尺寸一致，alpha=0 为可编辑区域；蒙版不是区域外像素锁。CLI 不解码核验尺寸和 alpha，调用方须自行检查。

## 选择主程序终结点

- 查看节点：`./gpt-image.exe --list-endpoints --json`，仅列出 ID、友好名称、图片模型，不输出地址或密钥。
- **主程序有节点，CLI 列表却没有？** 查看 stderr 的「未列出节点」说明：会区分未启用、节点类型不适用、尚未添加模型、未配置有效图片能力模型。模型名称叫 `gpt-image-2` 并不等于已勾选图片生成能力；请在主程序「AI 终结点管理 → 该节点 → 模型列表」展开模型，勾选图片生成能力并保存。列表为空也会明确提示，不表示节点被删除。筛选提示不是网络或认证失败，列举仍可退出 0。
- 指定名称：`./gpt-image.exe --endpoint-name "<友好名称>" --prompt "<用户提示词>" --json`；重名时改用 `--endpoint-id "<ID>"`，两者互斥。
- 不指定节点时，优先主程序默认图片模型引用；无默认引用时可使用唯一启用的图片节点。默认引用失效或存在歧义时提示选择，不随意猜测。
- 指定名称/ID 时，配置地址和 Key 成对覆盖本次外层值；无选择器时按参数、环境变量、配置回退取值，外部密钥必须有明确 URL，不会发往自动选中的配置节点。无选择器的显式 URL 借用配置密钥须匹配完整 BaseUrl，不能只匹配域名。
- 图片模型自动映射到配置部署名；尺寸、质量、数量仍使用 CLI 默认值，不继承主程序的生成参数。
- `--config "<路径>"` 可指定另一份配置；只读取，不创建、迁移或写回。配置回退支持内建 OpenAI-compatible、Azure OpenAI、APIM 的 API key 认证；AAD 登录及自定义 profile 请使用 `--no-config` 独立提供连接。

## 常用参数

有值参数支持 `--name value` 或 `--name=value`；仅 `--image` 累积重复，其他同名参数最后一次生效。`--help`、`--json`、`--overwrite`、`--list-endpoints`、`--no-config` 是无值开关。别名与其余参数见 `--help`。

| 参数 | 默认值与合法范围 |
| --- | --- |
| `--mode` | 默认 `images`（生图）；改图指定 `edit`，兼容模式指定 `responses` |
| `--prompt` / `--prompt-file` | 无默认文本；依次优先使用提示词、文件、重定向 stdin，不能为空 |
| `--image` | 仅 edit，至少一个本地 PNG/JPG/JPEG/WebP 文件；服务接受范围可能更窄 |
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

## JSON 结果与文件安全

### 失败时怎么看

| 结果 | 含义与下一步 |
| --- | --- |
| HTTP 401 | exe 已启动且收到认证拒绝；查看认证头和密钥来源。指定名称/ID 时使用节点 Key，应核对该配置 Key 的有效性和认证方式；无需清环境，也不是靠改提示词或输出路径解决 |
| HTTP 403 | 可能涉及权限或网络访问策略，结合服务配置排查，不直接认定 key 无效 |
| HTTP 404 | 检查所选服务的路由及部署，不自动尝试多个地址 |
| 退出码 2、没有 HTTP 状态 | 参数、配置或输入检查失败，未发送请求；节点配置等 CLI 校验错误会在 JSON 的 `error` 与 stderr 中给出具体原因和操作建议 |

`POST [配置目标 URL 已隐藏]` 是隐私保护，不是 URL 缺失。HTTP 失败或超时后不要自动重试；确认原因和付费授权后再执行一次。错误输出只报告密钥来源，不打印密钥值或服务端原文。

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

生图/改图需退出成功、`ok=true`、`files` 非空且文件存在，再用图像解码器核验真实格式、尺寸和所需透明度，并检查内容。不能仅凭 HTTP 成功、扩展名或元数据宣布验收完成。

默认拒绝覆盖已有文件。images/edit 显式文件输出按 n 预检目标，冲突可在请求前退出 2；目录输出、Responses 或保存竞态的冲突可能在请求后退出 1，应检查部分 `files`。仅显式 `--overwrite` 才允许覆盖。

不自动重试、切路由、改模型或增加数量。超时或网络错误仍可能已计费，重试须重新获得用户授权。

## 本地构建与 CI/CD

在源码 `tools/GptImageCli` 目录用 PowerShell 7 执行，成功后在资源管理器中选中完整的 `gpt-image-cli` 文件夹，方便直接复制；不运行生图测试。

| 脚本 | 输出（相对于 GptImageCli 项目） |
| --- | --- |
| `./Build-Debug.ps1` | `bin/Debug/net10.0/gpt-image-cli/`，普通调试构建，运行需要 .NET 10，须保留目录中的 DLL 等文件 |
| `./Build-Release.ps1` | `bin/Release/net10.0/win-x64/publish/gpt-image-cli/`，Native AOT 独立 skill 包，含 exe、说明、SKILL.md 与发行清单 |

两者支持 `-NoOpen`；Release 默认 win-x64，可传 `-Runtime win-arm64`。构建需要 .NET 10 SDK，Release 另需 Visual Studio C++ 桌面开发工具链（ARM64 需对应组件）。请分发 Release 输出中的整个 `gpt-image-cli` 目录，而不是其上层构建目录或仅 SKILL.md。

交付文件全部收在 `gpt-image-cli/` 内：根目录放 `SKILL.md`、exe、两份说明和 Release 清单，不再嵌套 `skills/gpt-image-cli`，也不在 skill 的上一层放 CLI 文件。Debug 所需 DLL 等也在 skill 内；当前交付包不需要额外子目录。

`Publish.ps1` 为共用发布入口，默认也输出项目的 Release skill 目录；自动化可用 `-OutputDirectory` 指定最终 skill 目录（脚本不会再附加子目录）。GitHub Release 工作流为 x64、ARM64 分别发布主程序、Updater 和 CLI，再整体压缩上传；完整 skill 位于主包的 `skills/gpt-image-cli/`。本地常规构建不使用仓库根 `artifacts`；该目录仅用于专门打包、测试产物及 CI 暂存。

## 发行文件职责

- `gpt-image.exe`：独立 CLI；使用与操作系统和架构匹配的发行包。
- 随主程序交付时位于 `skills/gpt-image-cli/`；将整个 `gpt-image-cli` 复制到目标项目所用的 skill 根目录即可，例如 `.github/skills/` 或 `.claude/skills/`，具体发现规则以 AI 宿主为准。无需安装或启动主程序，也不依赖原仓库位置。
- `release-manifest.json`：发行信息；以其中 `mode` 判断运行模式。仅 `NativeAot` 表示原生 AOT；`Managed` 是自包含托管模式，**两者均无需另装 .NET**，不能混淆模式与版本。
- `README.md`：使用、参数和结果说明；`CAPABILITIES.md`：能力结果及适用边界。
- `SKILL.md`：交付包根目录中的 AI 调用规程，与 exe 及两份说明同级；源码模板位于 `skills/gpt-image-cli/SKILL.md`。让 AI 显式读取或按宿主机制注册；普通 `skills/` 目录不保证自动发现。
- 文档与 skill 是包内伴随文件，不嵌入 exe；保留目录结构以维持相对链接。
