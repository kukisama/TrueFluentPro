# GPT-Image CLI 使用指南

独立的图片生成与编辑命令行工具，供人和 AI 调用，不依赖主程序。服务能力与限制见 [CAPABILITIES.md](./CAPABILITIES.md)，不能将某个端点的支持范围推广到所有云端。

## 开始使用

- 在发行包目录执行 `./gpt-image.exe --help` 查看全部参数；帮助离线可用，无需端点或密钥。
- 同一 Windows 用户已配置 TrueFluentPro 时，CLI 默认只读 `%APPDATA%\TrueFluentPro\config.json`，复用终结点、密钥和图片模型，无需启动主程序或重复传 key。
- 独立使用时，由受信任的启动器或密钥管理器安全注入 `GPT_IMAGE_ENDPOINT` 与 `GPT_IMAGE_API_KEY`；可加 `--no-config` 禁用配置回退。CLI 不自动读取 `.env`。
- 密钥不得放入命令行、提示词、文档、日志或 URL；不要回显环境变量。远端连接使用 HTTPS，分享诊断前脱敏。
- 端点可为基址或当前模式的完整 API 地址；按服务要求选择认证，不自动探测路由。兼容环境变量与其他配置选项见 `--help`。
- 请求可能计费，须先获用户授权；将下列占位符替换为用户提供的内容与路径。

生图：`./gpt-image.exe --prompt "<用户提示词>" --json`

单图编辑：`./gpt-image.exe --mode edit --image "<输入图路径>" --prompt "<用户编辑要求>" --json`

**不写参数即使用：生图（images）、1024×640、中等质量（medium）、PNG、1 张、当前工作目录。** 这些参数都可省略，只有需要改变时才指定。

输出可省略，也可只传目录，如 `--output "./图片"`，程序自动创建目录并生成“时间戳＋随机 ID＋序号”文件名，无需调用方生成 ID 或预查路径。指定 `--output "./图片/result.png"` 则使用该文件名，多张追加 `-01`、`-02`。已有目录或无扩展名路径按目录处理；新建的带点目录请以 `/` 或 `\` 结尾。实际文件路径从 `files` 获取。

多图编辑按顺序重复 `--image "<参考图路径>"`，在提示词中说明各图角色。连续修改将上次 `files` 中选定的结果作为下一次输入；输出仍可省略或复用同一目录，程序自动生成新文件名，这不是会话续接。

蒙版编辑追加 `--mask "<蒙版路径.png>"`。蒙版须带 alpha、与首图尺寸一致，alpha=0 为可编辑区域；蒙版不是区域外像素锁。CLI 不解码核验尺寸和 alpha，调用方须自行检查。

## 选择主程序终结点

- 查看节点：`./gpt-image.exe --list-endpoints --json`，仅列出 ID、友好名称、图片模型，不输出地址或密钥。
- 指定名称：`./gpt-image.exe --endpoint-name "<友好名称>" --prompt "<用户提示词>" --json`；重名时改用 `--endpoint-id "<ID>"`，两者互斥。
- 不指定节点时，优先主程序默认图片模型引用；无默认引用时可使用唯一启用的图片节点。默认引用失效或存在歧义时提示选择，不随意猜测。
- 连接值优先级为参数、环境变量、对应节点配置。外部密钥必须有明确 URL 或节点选择器；不会把无目标的环境密钥发送到自动选中的节点。显式 URL 借用配置密钥须匹配完整 BaseUrl，不能只匹配域名。
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

`--json` 让 stdout 输出单份 JSON，帮助、进度和错误走 stderr；它不是 JSON edit 请求开关。

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

在源码 `tools/GptImageCli` 目录用 PowerShell 7 执行，成功后自动打开产物目录；不运行生图测试。

| 脚本 | 输出（相对于 GptImageCli 项目） |
| --- | --- |
| `./Build-Debug.ps1` | `bin/Debug/net10.0/`，普通调试构建，运行需要 .NET 10 |
| `./Build-Release.ps1` | `bin/Release/net10.0/win-x64/publish/`，Native AOT 独立交付，含 exe、说明、skill 与发行清单 |

两者支持 `-NoOpen`；Release 默认 win-x64，可传 `-Runtime win-arm64`。构建需要 .NET 10 SDK，Release 另需 Visual Studio C++ 桌面开发工具链（ARM64 需对应组件）。请分发整个 Release 的 `publish` 目录，而不是其上层构建目录。

`Publish.ps1` 为共用发布入口，默认也输出项目的 Release 目录；自动化可用 `-OutputDirectory` 指定目标。GitHub Release 工作流为 x64、ARM64 分别发布主程序、Updater 和 CLI，再整体压缩上传；CLI 位于主包的 `GptImageCli/`。本地常规构建不使用仓库根 `artifacts`；该目录仅用于专门打包、测试产物及 CI 暂存。

## 发行文件职责

- `gpt-image.exe`：独立 CLI；使用与操作系统和架构匹配的发行包。
- 随主程序交付时位于 `GptImageCli/` 子目录；也可单独分发此目录，不要求安装主程序。
- `release-manifest.json`：发行信息；以其中 `mode` 判断运行模式。仅 `NativeAot` 表示原生 AOT；`Managed` 是自包含托管模式，**两者均无需另装 .NET**，不能混淆模式与版本。
- `README.md`：使用、参数和结果说明；`CAPABILITIES.md`：能力结果及适用边界。
- [skills/gpt-image-cli/SKILL.md](./skills/gpt-image-cli/SKILL.md)：AI 调用规程。让 AI 显式读取或按宿主机制注册；普通 `skills/` 目录不保证自动发现。
- 文档与 skill 是包内伴随文件，不嵌入 exe；保留目录结构以维持相对链接。
