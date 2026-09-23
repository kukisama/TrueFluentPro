# GPT-Image CLI 能力与边界

以下区分已确认的能力结果、CLI 参数范围与服务限制；端点、模型部署及 API 版本不同，支持范围可能不同，不构成所有云端的通用保证。

本文件为按需参考，不是普通生图的必读或验收清单。AI 日常只调用 exe 并报告数量和路径，图片由用户检查；仅用户明确要求能力核验时才解码或查看图片。skill 的本地默认节点见 `SKILL.md` 的可删除明文配置区块，与下述 CLI 内置默认参数分开。

**默认值可全部省略：生图（images）、1024×640、中等质量（medium）、PNG、1 张，保存到当前工作目录。** 输出省略或指定目录时自动生成时间戳＋随机 ID 文件名；指定带扩展名文件路径时按给定名称保存，多张追加序号，无需调用方生成 ID 或预查路径。

## 图片能力

| 能力 | 事实结果与边界 |
| --- | --- |
| 生图 | 默认模式，可省略 `--mode images`；根据提示词生成图片，内容准确性不作普遍保证 |
| 单图 / 多图编辑 | `--mode edit` 支持本地参考图，重复 `--image` 按顺序传多图；服务可能限制格式、数量及大小 |
| 连续文件编辑 | 可将上次输出作为下一轮输入；每轮独立请求，不是会话续接 |
| 蒙版编辑 | PNG alpha=0 指示可编辑区，蒙版指导不等于区域外像素锁；用户提供与首图尺寸一致且含 alpha 的蒙版，默认不额外核验 |
| 透明 PNG | 可生成含真实透明及半透明像素的 PNG；用 `--background transparent --format png`，实际效果由用户检查；JPEG 不支持透明 |
| PNG / JPEG | 生图输出及编辑输入、输出已有可用结果；服务兼容性仍依端点 |
| WebP | CLI 接受相关格式参数及输入后缀，服务可能拒绝输入或输出，不保证可用 |
| quality | `low` / `medium` / `high` / `auto` 四档已有可用结果；默认 `medium`，不保证固定质量增益或成本比例 |
| n | 默认 1，本地整数范围 1..10，不保证服务全部接受；Responses 不保证返回数量 |
| JPEG 压缩 | `--output-compression` 参数被接受不等于压缩调节效果得到保证；本地范围 0..100，仅 JPEG/WebP |

## 尺寸

| 场景 | 规则与限制 |
| --- | --- |
| direct GPT Image 2（images/edit）显式尺寸 | 宽高均为 16 倍数；每边 ≤3840；长短边比例 ≤3:1；总像素 655360..8294400；>3686400 像素为 experimental |
| 本地校验范围 | 上述专用校验在逻辑图片模型为 `gpt-image-2` 且使用 images/edit 时启用；从主配置映射的部署别名仍保留逻辑模型校验，独立传入的未知别名除外；合法参数也不保证服务接受 |
| 推荐 direct edit 尺寸 | `1024x640`、`640x1024`、`1440x480`、`816x816`、`1536x864` 已确认可精确输出，不保证所有端点相同 |
| auto | 尺寸由服务选择，不承诺精确宽高或输出符合显式输入的倍数规则 |
| Responses | 显式选择该模式时须按端点指定尺寸，默认 `1024x640` 不保证适用；可能仅接受 `1024x1024`、`1536x1024`、`1024x1536` 与 `auto`，实际尺寸仍可能不同 |
| 实际输出 | 请求尺寸与响应元数据不代替图像解码；CLI 不缩放返回图片 |

## CLI 接口边界

| 接口 | 当前范围 |
| --- | --- |
| 主程序配置 | 默认只读同一用户的配置；支持友好名称/ID 选择、默认图片模型和部署名映射；列表不输出地址或密钥 |
| 独立连接 | 参数、环境变量优先，`--no-config` 禁用回退；不要求安装或启动主程序，不自动加载 `.env` |
| 配置适配范围 | 内建 OpenAI-compatible、Azure OpenAI、APIM 的 API key 认证；不自动登录 AAD、不解释自定义 profile，不自动重试路由 |
| edit | 使用 multipart 传本地文件；蒙版仅做有限文件校验，不解码确认 alpha 和尺寸 |
| Responses | 可使用 `--image` 将本地参考图作为 `input_image` data URL 随文本发送，`--image-action generate\|edit\|auto` 选择工具动作；带图默认 generate。不支持会话续接；需文字模型和网关具备 image_generation 工具能力 |
| 未暴露入口 | SSE、`/images/edits` JSON 请求体、`input_fidelity`、Batch、`file_id`；服务具备能力不等于 CLI 已接入 |
| JSON 报告 | `--json` 仅控制本地结果报告，不启用 JSON edit 或流式请求 |
| 行为保证 | 不保证人物保真、文字完全准确或安全拦截效果；参数接受不等于效果保证 |

## 2026-09-23 所选节点实测

- `公司大实例`：此前 `gpt-image-2.5-flare` 的纯文字 `/images/generations` 已返回 HTTP 200 并保存图片；`/images/edits` 曾返回 HTTP 404。两项为历史调用结果，不代表其他模型或节点。
- 本轮参考图 Responses `action=generate` 与 `action=edit` 各发送一次，指定文字模型 `gpt-4.1`、图片模型 `gpt-image-2.5-flare`：均为 HTTP 503 / `model_not_found`，无图片文件；无法仅凭此错误定位具体缺失的模型或路由。
- 本地 CLI 回环验证能正确发送参考图原始字节、顺序与动作，并解析保存模拟响应；这只能证明 CLI 请求形状，不证明上述节点支持 Responses 参考图流程。须先确认该节点可用的 Responses 文字模型及 image_generation 工具路由，再进行有授权的在线复测。

### 后续在线能力样张（同一节点）

| 测试 | 在线结果 | 文件验证 |
| --- | --- | --- |
| Flare：低质量、1024×640、透明 PNG、中文“启航”提示词 | HTTP 200，保存 [Flare 样张](../../artifacts/gpt-image-cli-live-check/capability-flare-transparent-text.png) | 实际 1024×640 RGBA；8 像素间隔抽样 10240 点：完全透明 9077、半透明 1163、不透明 0；四角 alpha=0 |
| Sunburst：相同提示词与参数 | HTTP 200，保存 [Sunburst 样张](../../artifacts/gpt-image-cli-live-check/capability-sunburst-transparent-text.png) | 实际 1024×640 RGBA；相同抽样：完全透明 8667、半透明 1573、不透明 0 |

两次请求各发一次、`n=1`，没有自动重试。它们确认本节点这两种模型的**纯文字生图及透明像素输出**，不证明中文文字准确、所有画面要求均满足或同一模型的改图能力；文字请以样张实际观感为准。样张位于本地 `artifacts/`，未验证其在其他机器上的可访问性。参考图流程仍须先核对该节点可用的 Responses 文字模型与工具路由，不能用本节成功结果替代带图验证。

### `gpt-5.6-sol` Responses 带图复测

- 同一节点、Flare 图片模型、既有 Flare 样张作为 `input_image`，`action=generate`，`1024x1024`、`quality=low`、PNG：请求一次后返回 HTTP 400；错误 `code=invalid_request_error`、`type=image_generation_user_error`、`param=tools`，无 request ID、无 usage、无输出文件。已确认本次未生成目标文件。
- 本次没有发 `action=edit` 请求，也没有对 generate 自动重试。400 的具体成因未确定，不能从 `param=tools` 反推是哪一个工具字段、文字模型还是网关不兼容；同样不代表全部 New API 节点都不支持带图。后续须取得节点/网关侧脱敏错误详情，再决定是否有依据修正请求并另行复测。
- 参考 [OpenAI 图片生成指南](https://developers.openai.com/api/docs/guides/image-generation) 与 [Responses 创建接口](https://developers.openai.com/api/reference/resources/responses/methods/create#responses-create-tools)：官方示例允许 data URL 参考图与 `image_generation` 工具、`action`；对 `image_generation_user_error` 不应无修改自动重试。官方协议允许某种请求形状不证明当前节点支持该模型和工具组合。

### New API 直连 edit 核对（区别于 APIM→AOAI）

- [New API 官方编辑接口](https://docs.newapi.ai/en/docs/api/ai-model/images/openai/post-v1-images-edits) 标注 `POST /v1/images/edits`、Bearer 认证、`multipart/form-data`，单图为文件字段 `image`，提示词字段为 `prompt`。该页的 PNG 方形、4 MB 及尺寸说明沿用旧式编辑接口，未列出 `gpt-image-2.5-flare`；不能据此推断 Flare 支持或不支持这些参数。[官方 README](https://github.com/QuantumNous/new-api#protocols-and-endpoints) 也列有 `/v1/images/edits`，但指出实际能力取决于渠道、上游模型及部署版本。
- 脱敏核对：`公司大实例` 在本机配置中是 OpenAI-compatible（类型 0、路由模式 0）、Bearer 模式；基地址路径为根路径，Flare 无部署别名。按 CLI 当前代码，本次请求目标为 `/v1/images/edits`，而不是 APIM 的 `/images/edits`；发送单图 `image`、`model`、`prompt` 等 multipart 字段。既有隔离回环只证明本地请求形状，不代表服务端支持。
- 使用既有 Flare 透明 PNG（444383 字节）与 `gpt-image-2.5-flare`，`1024x640`、`quality=low`，只执行一次在线直连 edit：HTTP 404，`code/type=bad_response_status_code`，没有 request ID、usage 或输出图片；未自动切换路由、模型或参数重试。不能从 404 独自判断是 New API 实例版本、模型渠道、上游编辑端点还是中间代理造成的；纯文字 generations 成功及此前 APIM→AOAI 的编辑成功均不能替代本节点 edit 的成功证据。下一步须由网关维护方提供对应请求的脱敏路由／上游响应日志及部署版本，再决定是否需改动 CLI。