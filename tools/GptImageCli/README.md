# GPT-Image CLI

独立图片生成与编辑工具。日常提交、查询和队列管理见发行包 `SKILL.md`；实际契约与实测限制见 [CAPABILITIES.md](./CAPABILITIES.md)。

## 安装

- 复制完整 `gpt-image-cli/` 到宿主支持的 skill 目录，例如项目 `.github/skills/gpt-image-cli/`；不要只复制 exe 或 SKILL.md。
- 保留根目录的 exe、SKILL.md、两份说明、辅助脚本、队列模板、发行清单及 `references/`。源码 skill 资产位于 `skills/gpt-image-cli/`，发行时展开到包根。
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
| `--output` | 当前目录自动命名；可给目录或明确文件名，多张追加序号；新建带点目录以分隔符结尾 |
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
