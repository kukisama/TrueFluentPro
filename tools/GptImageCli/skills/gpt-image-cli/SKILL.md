---
name: gpt-image-cli
description: '使用独立 gpt-image.exe 生成图片、参考图改图、多图编辑、蒙版编辑及连续修改。用于 AI 安全调用 GPT Image CLI、读取 JSON 结果并核验真实图像；不用于宣称所有 REST 或云端能力均已接入。'
---

# 用独立 exe 生图与改图

本 skill 随发行包提供，不需要主程序或源码。以本文件所在目录为基准，发行根目录为 `../../`，Windows exe 为 `../../gpt-image.exe`。读取 [参数与报告说明](../../README.md) 和 [能力实测矩阵](../../CAPABILITIES.md)；后者缺失时明确告知缺少实测证据，不推断全部支持。将相对路径解析成绝对路径后再执行；不要把当前工作目录误当成 skill 所在目录。

当前默认发行是 Windows x64 Native AOT，exe 约 5.01 MiB，免安装 .NET。该包通过 9 个离线场景、287 条断言；历史 47 项云端请求不是对该 AOT 二进制的重新实测。版本指纹和验证边界以随包 README 为准。

## 1. 先确认接口与授权

1. 每次开始任务或更换发行包后重新执行 exe 的 `--help`，必要时执行 `--json --help`。两者均离线、无需密钥。以实际帮助和配套 README 为准，不编造参数。
2. 明确用户希望文生图还是改图，确认输入图顺序、目标和付费授权。**显式使用 `--mode images` 或 `--mode edit`，不要默认 Responses**，即使程序的兼容默认值仍是 responses。
3. 低成本起点为 `--size 1024x640 --quality low --format png --n 1`；只有用户明确需要时才提高成本，不能许诺固定价格。
4. 密钥由受信任启动器/密钥管理器通过 `GPT_IMAGE_API_KEY`（或兼容环境变量）注入，端点用 `GPT_IMAGE_ENDPOINT`。不在命令行传 key，不回显环境变量，不读取主程序真实配置；不把秘密放进日志、提示词、skill 或 URL。需要用户输入密钥时由用户直接在安全终端/密钥界面操作，不经过 AI 消息。
5. 公司 APIM 类端点明确 `--auth api-key`；不自动换路由或猜真实端点。没有环境配置时说明缺项即可，不搜集或打印密钥。

## 2. 准备唯一输出与输入

- 输出使用**唯一绝对文件路径**，扩展名匹配 `--format`。当前默认不覆盖：目录命名含时间戳+GUID，实际保存使用 `CreateNew`；images/edit 显式文件路径按 n 提前检查所有派生目标，冲突请求前退出 2。Responses 数量未知或保存竞态可能在请求后退出 1，须检查部分 `files`。`--overwrite` 是默认 false 的无值本地开关，只有用户明确授权才加，不能传 `=true/false`。
- 单图 edit 使用一次 `--image <绝对路径>`；多图重复该参数，顺序保持用户指定顺序，并在提示词写清每张图的角色。单图 multipart 字段为 `image`，多图为 `image[]`。
- 本地允许 PNG/JPEG/WebP 不等于端点都接受。检查输入文件存在且能解码，按能力矩阵选择格式，不因扩展名合法就承诺成功。
- 蒙版用 `--mask <绝对路径>`，必须是带 alpha 的 PNG，与首张参考图尺寸一致；**alpha=0 表示可编辑区域**。CLI 仅检查存在、PNG 扩展名和 8 字节 signature，**不解码验 alpha/尺寸**；由调用方用已有解码器核验，不能把本地校验成功当成蒙版合格。E04 区域内/外 RGB MAE 为 9.3792/3.3314，区域外并未锁定；严格保真须对比或另做用户授权的确定性合成。
- 连续 edit 没有隐藏历史：将上次 `files` 中选定的成功结果作为本轮 `--image`，每轮给新输出路径，不覆盖原图或上次结果。

## 3. 执行与判断结果

1. 获得授权后执行一次请求，带 `--json`；分开收集 stdout（单份 JSON）与 stderr（帮助/进度/错误），不把两者混合后当 JSON 解析。分享诊断前脱敏。
2. 读取进程退出码、`ok`、`exit_code`、`http_status` 和 `files`。当前 **没有 `status` 字段**，HTTP status 是 `http_status`；未收响应时可为 null。可参考 `request_id`、`elapsed_ms`、`usage`、`api_error`、`error`、`response_metadata`，usage 不等于实际账单。`api_error` 仅来自当前响应根 error 对象；code/type/param 必须为 1..64 字符、匹配 `\A[A-Za-z0-9_.\[\]-]+\z` 且不含当前 key，否则 null；message 固定 `服务端返回错误。`，不回显服务端原 message。
3. 退出码 0 为成功（含帮助），2 为参数/输入校验失败，1 为请求/解析/保存失败。未知参数退出 2 且不发请求；先重读帮助，不尝试另一个臆造参数。帮助成功的 `files=[]` 不代表生成成功。
4. 生图/改图必须同时满足退出成功、`ok=true`、`files` 非空且文件存在。用可用的图像解码器检查真实格式、宽高、alpha（透明像素数量/范围），再观察内容是否满足用户要求。CLI 只保存返回字节，不保证请求尺寸或扩展名等于真实产物，响应元数据也不能替代验证。
5. 无图像解码/查看能力时明确标注“文件已保存，真实尺寸/格式/alpha/视觉效果未验证”，不要虚称验收完成。失败报告也可能含部分已保存的 `files`，先核对再决定后续。

## 4. 已知边界与停止规则

- 当前 APIM 的 47 项历史报告含 40 个 HTTP 200、7 个 HTTP 400，**不是全部通过**。早期 request.body 是模板，以 cli_args 同名最后一次生效值为准；body_kind=effective 是后期参数记录，仍不是 wire capture。
- 优先 direct edit 已验尺寸：1024×640、640×1024、1440×480、816×816、1536×864。E12 auto→1586×992；E14 512×512→400。Images G07 auto→1254×1254 合法，不应套显式 grid 承诺。显式 Images/edit 尺寸规则：边长 16 倍数、各边≤3840、比例≤3、像素 655360..8294400；>3686400 属 experimental，规则不代表每个尺寸都已测试。
- Responses 改图仅参考真实矩阵：R04 1024×1024→1254×1254 **差异**，R05 1536×1024 精确，R06 1024×1536→1016×1548 **差异**，R07 1024×640→400。R02/R03 也有尺寸差异。R01 历史聊天链测已完成，用户当前只用 Responses 改图，不扩展 response/chat，不引入 CLI 聊天或会话参数。
- PNG/JPEG 已验，G10 WebP 输出 400；输入 WebP 未测。G11 alpha=0 / 0<alpha<255 为 441770/213590，发布版 G18 为 432870/222490；需透明时显式 `--background transparent --format png` 并逐图验 alpha。JPEG compression 0/50/100 接受，但三者 DQT 哈希相同、均值均为 1，不承诺压缩调节有效。
- **不传 `input_fidelity` / `--input-fidelity`**：E07 REST high 接受，但无保真行为提升证明，CLI 没有此开关。
- CLI **未支持 stream、JSON 请求体 edit、Responses 多轮/多图输入**，也没有 `previous_response_id` 会话续接。REST 测试另有成功结果不代表 exe 可调用；`--json` 是输出报告格式，绝不是 JSON edit 开关。
- E06 JSON edit、S01/S02 SSE（请求 2 partial，实际各 1 partial+final）均属 REST 已验而 CLI 不暴露；partial 数少于请求符合官方允许提前完成的语义。不得把全部文件保存时刻当网络到达时间。
- 未验 4K/最大尺寸、n=10、16 张/50MB 输入压力、WebP 输入、远程 URL/file_id、Batch 24h、安全拦截效果、人物保真；不自动启动付费 Batch，不为验证安装依赖。
- 最终离线回归 **355 checks 全部通过**。G18/G19 来自约 70.17 MiB 自包含 exe，SHA256 `2FF4EE10BDD7E9E7A6D4438E34A9CCE53FF9B22BC1731B4AEF746AB621A21093`。源码仓库可用 README 中的离线分析脚本复核，发行包不保证附带原始 artifacts。
- 不自动重试付费请求、不自动切路由/模型、不自动增加数量、不覆盖已有图片。超时/网络错误不证明未计费；先报告结果和已有文件，取得用户重新授权后才可重试。
- 不将端点特有成功推广到所有云端；报告中区分“CLI 支持”“用户提供的既有实测”“本次已验证”和“未验证”。本 skill 不携带真实端点或密钥，也不修改全局指令。