---
name: gpt-image-cli
description: '使用独立 gpt-image.exe 生图、单图或多图编辑、蒙版编辑与连续文件修改。用于安全调用 GPT Image CLI、解析 JSON 结果并核验真实图像，不将端点能力推广到所有云端。'
---

# 独立 CLI 生图与改图

以本文件目录为基准，包内 exe 为 `../../gpt-image.exe`；先读取 [使用指南](../../README.md) 和 [能力边界](../../CAPABILITIES.md)。执行前解析为绝对路径，不依赖当前工作目录；缺文件时停止并说明，不猜路径或能力。

## 授权与安全

1. 开始任务或更换发行包后先运行 `--help`；它离线可用，无需密钥。只使用帮助中存在的参数。
2. 确认生图或改图、输入图顺序、目标、数量及付费授权，再发送请求。默认生图 `images` 可省略模式；改图指定 `--mode edit`。
3. 优先让 exe 内部只读主程序配置，不要求用户重复提供 key；AI 不直接打开配置 JSON 或读取密钥。需要选择时用 `--list-endpoints --json` 获取 `endpoints` 中的 `id/name/models`，再用 `--endpoint-name "<友好名称>"`（重名用 `--endpoint-id`）；已有有效默认节点时可省略选择器。
4. 密钥不进入提示词、日志、文档或 URL；远端用 HTTPS，认证按服务要求选择。缺配置只说明缺项；分享诊断前脱敏，不修改全局指令。
5. 独立连接由受信任启动器注入 `GPT_IMAGE_ENDPOINT`、`GPT_IMAGE_API_KEY`，可用 `--no-config` 禁用回退；CLI 不自动读 `.env`。不回显环境、不在命令行传密钥、不通过 AI 消息索取秘密。外部密钥须有明确 URL 或节点选择器，不绕过缺目标保护。

## 输入、输出与参数

- 默认保存到进程当前工作目录，执行前明确工作目录即可。`--output` 可省略或只给目录，程序自动创建目录并用时间戳＋随机 ID 命名；不要自行生成 GUID、时间戳或预查输出文件。用户明确给出文件名时照用，扩展名匹配格式；新建带点目录以分隔符结尾。默认不覆盖，明确授权后才加 `--overwrite`。
- 生图用 `--prompt` 或 `--prompt-file`；改图加 `--image`，多图按用户顺序重复并说明各图角色。检查输入存在且可解码；本地接受格式不等于服务支持。
- 蒙版用 `--mask`：带 alpha 的 PNG，与首图尺寸一致，alpha=0 为可编辑区；不是像素锁。CLI 不解码核验蒙版，调用方须检查尺寸及 alpha。
- 连续编辑将上次 `files` 中选定的结果作为下次 `--image`；输出可继续省略或复用同一目录，自动生成新文件名，不是会话续接。
- 默认值：`mode=images`、`size=1024x640`、`quality=medium`（中）、`format=png`、`n=1`、输出为当前目录自动命名。默认参数应省略，不主动改为 low；仅按用户需要覆盖，不承诺固定成本。
- 最短调用：生图 `gpt-image --prompt "<用户提示词>" --json`；改图 `gpt-image --mode edit --image "<输入图>" --prompt "<编辑要求>" --json`；指定目录仅追加 `--output "<目录>"`。
- quality 可选 `low/medium/high/auto`，format 可选 `png/jpeg/webp`；n 本地范围 1..10，不保证服务全部接受。尺寸规则、推荐值与格式边界以配套能力说明为准。
- 透明图显式 `--background transparent --format png` 并验 alpha；JPEG 压缩参数被接受不代表调节效果已保证。

## 执行与验收

1. 按授权执行请求，带 `--json`；分别收集 stdout 的单份 JSON 和 stderr，不混合解析。
2. 同时检查进程退出码、`ok`、`exit_code`、`http_status`、`files`，错误参考 `api_error`、`error`。HTTP 字段不是 `status`。
3. 退出码 0 为成功（含帮助），2 为参数/输入校验失败，1 为请求/解析/保存失败。生成需退出成功、`ok=true`、`files` 非空且存在；失败时也须核对部分已保存文件。
4. 用已有图像解码器核验真实格式、尺寸及所需 alpha，再查看内容是否满足要求；扩展名、HTTP 成功与 `response_metadata` 均不能代替验收。
5. 无解码或查看能力时明确列出未验证项，不虚构成功、画面或能力；帮助成功不代表产图。

## 停止边界

- CLI Responses 不支持本地参考图或会话，改图使用 edit；其尺寸限制依端点，实际尺寸可能与请求不同。
- 不编造 SSE、JSON edit、`input_fidelity`、Batch、`file_id` 参数；CLI 未暴露这些入口，`--json` 仅指本地结果报告。
- 不自动重试、换路由、改模型、增加数量或覆盖文件。超时/网络错误也可能已计费，先报告结果，重新授权后才重试。
- 区分 CLI 参数范围、端点支持与本次实际结果，不将条件性结论写成所有云端保证。