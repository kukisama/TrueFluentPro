# GPT Image CLI 中文能力逐项验收报告

日期：2026-09-07。范围：当前公司 APIM（脱敏别名“公司大实例”）下的 **47 项真实请求**：G01..G19、E01..E14、S01..S03、N01..N04、R01..R07。本轮完成真实请求、离线证据分析、CLI 回归及独立发布；未修改密钥/主配置，未启动付费 Batch。最后的分析与构建阶段未重发云请求。

## 1. 结论与证据语义（先读）

- **47 项均有证据，不等于 47 项全部通过。** 40 项 HTTP 200、7 项 HTTP 400；HTTP 200 中 R02/R03/R04/R06 显式尺寸不符，必须标“差异”。R01 是历史文字链证据而非生图成功。
- 39 个有图的请求，共 41 张 final、2 张 partial；连同 E03 第二参考图、E04 蒙版、E05 JPEG 输入，共独立解码 46 个文件。141 个 request/response/result JSON 加 46 个图片文件共 187 个原始证据，分析前后 SHA256 未变。
- 原始目录：[20260907](../../artifacts/gpt-image-capabilities/20260907/)。机器可读衍生报告：[analysis-derived.json](../../artifacts/gpt-image-capabilities/20260907/analysis-derived.json)，含 47 项完整原始脱敏对象、有效参数、usage 明细、图片哈希、DQT 表与测量方法。
- **早期 `request.body` 是模板！** CLI 的有效参数以 `cli_args` 为准：同名最后一次生效，`--image` 累积；不同别名按解析器优先级（format 优于 output-format、n 优于 count 等）。例如 G05 实际 640×1024、G08/09 实际 JPEG、G14 实际 n=2，不能依据早期 body 的默认值宣判差异。
- 后期 `body_kind="effective parameters (file bytes omitted)"` 表示记录有效参数；仍不是传输字节捕获。**所有 request 记录均不是 wire capture**，省略认证秘密、文件字节和 data URL 数据；CLI 的 `response.json` 是 CLI 报告，不是完整服务端原始响应。REST 的 response 是脱敏后的响应/事件。
- E06 早期 body 未记入 `images`；结合已读取的 `Test-Capabilities.ps1` JSON edit 分支和 G01 输入证据，将 `images:[{image_url:"<local source as data URL>"}]` 标作**重建字段**，不冒充原记录或抓包。本次不改历史 JSON。
- 不沿用历史 `result.status/requested_size/exact_size_match` 作最终判定；脚本重建请求后，按实际解码宽高重新判断。`auto` 不参与显式尺寸精确匹配，G07 的 1254×1254 合法，不能套用显式 16 倍数 grid 承诺。
- 耗时为历史 `result.elapsed_ms`：包含驱动、CLI 启动（CLI 项）、传输、保存与测量，不是纯模型计算或网络延迟。CLI 自身耗时另存 `cli_elapsed_ms`。SSE 另用事件 `received_ms`，见第 4 节。
- 本轮已打开图片查看：G17“春日出发”准确；E02 绿船+太阳；E03 橙船+参考图八射线太阳；E04 太阳在右上透明区域；G11 透明绿船。像素/格式另行量化；这些视觉描述不等于 OCR 或自动视觉评测。

## 2. 后续 AI 已验可用清单与停止边界

| 分类 | 可采用的结论 | 不得推断 |
| --- | --- | --- |
| CLI 生图 | PNG low/medium/high/auto；1024×640、640×1024、1440×480；auto；n=2；中文标题样本 | 所有尺寸/质量内容均稳定、所有文字准确、最大 n 已验 |
| CLI direct edit（优先） | PNG/JPEG 输入；单图、双图、以上次结果继续改；PNG 蒙版；n=2；1024×640、640×1024、1440×480、816×816、1536×864 精确 | 区域外像素锁定、任意尺寸、任意输入格式/数量 |
| 透明 PNG | G11 REST、G18 最新发布 CLI 均检出真实 alpha=0 和中间 alpha | 透明效果普遍保证或 JPEG 支持透明 |
| JPEG | compression 0/50/100 被接受、可解码；DQT 三组相同 | 字节数更小就说明参数有效；已证明可调压缩效果 |
| REST 已验但 CLI 不暴露 | E06 JSON edit；E07 input_fidelity=high 参数接受；S01/S02/S03 SSE；Responses 参考图/历史续接 | `--json` 是 JSON edit；存在 `--stream`、`--input-fidelity`、`--previous-response-id` |
| Responses 改图 | R05 1536×1024 精确；R04/R06 返回图但尺寸差异；R07 自定义尺寸拒绝 | HTTP 200 等于尺寸符合，或可把 direct edit 尺寸规则照搬到 Responses |
| 差异/拒绝 | R02/3/4/6 尺寸差异；G10 WebP 输出、E14/N01/N02/N03/N04/R07 HTTP 400 | 全部通过、任意尺寸、任何云端均拒绝相同参数 |
| 仅接受，行为未证 | G15 moderation auto+user、G16 moderation low、E07 high | 安全拦截有效性、用户标识行为、保真提升 |
| 未测试 | 下述压力、格式、会话与安全边界 | 自动补跑、自动开启付费 Batch |

### 尺寸与输入范围

- GPT Image 2 的显式 Images/direct edit 尺寸规则：宽高分别为 **16 倍数**，各边最大 **3840**，长短边比例最多 **3:1**，总像素 **655360..8294400**；**>3686400 pixels 为 experimental**。CLI 仅在图片模型名恰为 `gpt-image-2` 且非 Responses 时做这一校验。部署别名并不代表已经应用同样本地校验。
- 本组采用小图，不做 4K/最大边长/最大总像素压力；n 只测 1/2，**最大 n=10 未验**；输入只测 1/2 张，**最大 16 张和 50MB 文件压力未验**。这里列的是文档/接口范围，不是压力测试成功声明。
- **输入 WebP、远程 URL、file_id、Batch 24h、安全拦截效果、人物保真未验**。N04 测的是输出 `response_format=url` 参数拒绝，不是远程 URL 输入测试。所有提示词为合规插画，无法评估安全拦截强度。
- CLI 当前 edit 仅 multipart。CLI 本地接受 WebP 后缀不等于此端点接受 WebP 输入。仅 `.png` 扩展名+8 字节 PNG signature 的 mask 校验不等于解码验证；调用方必须验证 mask 的 alpha 和与首张输入的尺寸一致。
- R01→R02→R03 聊天/生图/改图链是**用户最新范围要求前已做的历史**，必须保留证据但不扩展。用户当前只用 Responses 改图，不引入 CLI 聊天、不新增 Responses 会话功能。
- Azure 多轮指南与当前 APIM 实测存在适用范围差别时，**仅说当前 APIM 实测成功，不能推广所有 Azure、区域或 API 版本**。Responses 请求使用文字模型 gpt-5.4、图片部署请求头 gpt-image-2；响应 tools 元数据出现 gpt-image-1 时也不据此反推内部路由，端点内部模型映射未独立证明。

## 3. JPEG compression 0/50/100 定量对照

实现前已只读获取官方 [libjpeg-turbo jdmarker.c](https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/main/src/jdmarker.c)，核对 `get_dqt`、`first_marker`、`next_marker`、`skip_variable`：SOI 为 FF D8；marker 可有 FF 填充；有长度的段使用大端两字节长度且包含长度字段自身；DQT 首字节高 4 位为精度、低 4 位为表号，后接 64 个 zigzag 顺序量化值（8/16 bit）。本脚本只提取基线 JPEG 首个 SOS 前的 DQT；其他编码会拒绝，不伪装成通用 JPEG 解码器。图片可解码性另由现有 Windows System.Drawing 独立验证。

| 样本 | 有效 output_compression | 实际格式/尺寸 | 字节数（仅记录） | 表 0 均值 | 表 1 均值 | DQT 对照 |
| --- | ---: | --- | ---: | ---: | ---: | --- |
| G19 | 0 | JPEG 1024×640 | 90983 | 1.000000 | 1.000000 | 与 50/100 相同 |
| G09 | 50 | JPEG 1024×640 | 129411 | 1.000000 | 1.000000 | 与 0/100 相同 |
| G08 | 100 | JPEG 1024×640 | 131821 | 1.000000 | 1.000000 | 与 0/50 相同 |

三者均为基线 JPEG、两表均 8-bit、每表 64 个值均为 1。哈希定义：每表包含 Pq/Tq selector 字节与该表原始 zigzag 值；组合哈希按文件出现次序连接所有 DQT 表内容，不含 marker/length。

- 表 0 SHA256：`931F38AF772CA6A076AD98C475DD6CD41BB81457D70189F1844B8FAB837706AF`
- 表 1 SHA256：`DC7156746A46CBE6EDFACEB4CCFB9B27FC7250D2608A991848CFEC6F62F39932`
- 组合 DQT SHA256：`0ACEB3569C50251A20964B8C090DCE4848D59F2696D172E7CE8DB2407FC9BC29`

**结论：参数被接受，未观察到量化表随 compression 变化。** 三次生成内容不同，字节大小不能证明压缩效果；不据此断言服务端一定忽略参数，也未比较所有编码器内部策略、重编码质量或相同源图的受控压缩。因此状态为“接受但调节效果未证明”，不是“0/50/100 压缩有效”。三张图和原始请求链接见逐项详情。

## 4. 蒙版、颜色、透明度与流式量化

### E04 相对 G01（不缩放、不配准、同坐标比较）

首图和 mask 都是 1024×640；mask 的透明矩形为 x=700..959、y=40..239，共 52000 像素。解码 BGRA 后以 mask alpha=0 划可编辑区，其余 alpha>0 为区外。本样本 mask 无中间 alpha。RGB MAE 为各区所有 R/G/B 的绝对差之和除以 3×像素数，范围 0..255，非线性字节空间、非感知距离。改变像素指任一通道绝对差 >0；附加 >8 阈值用于区分微小变化，不代表视觉语义分割。

| 区域 | 像素数 | RGB MAE | 改变像素（>0） | 比例 | 任一通道差>8 | 比例 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 透明可编辑区 | 52000 | 9.379237 | 52000 | 100.000000% | 5059 | 9.728846% |
| 区域外 | 603360 | 3.331442 | 603354 | 99.999006% | 3569 | 0.591521% |

已有看图结论“太阳位于右上透明区”与量化不冲突：主要较大改动集中在透明区，但区域外近乎所有像素都有小差值。**mask 和提示词都是编辑指导，不是像素锁定**；不能把“看起来保持”写成“区域外完全不变”。

### 颜色阈值计数

green：A>128、G>1.3R、G>1.15B；orange：A>128、R>180、50<G<190、B<100；yellow upper right：x>0.65W、y<0.4H、R>190、G>150、B<110（沿用历史阈值，不另加 alpha 门槛）。阈值只是可复核代理指标，不等于识别船或太阳。

| 图 | green | orange | yellow upper right | 可支持的有限解释 |
| --- | ---: | ---: | ---: | --- |
| G01 | 0 | 38990 | 0 | 基准橙船 |
| E01 | 39376 | 0 | 0 | 橙色减少、绿色增加，支持改绿成功 |
| E02 | 39342 | 1099 | 7592 | 相对 E01 右上黄增加，已有看图为绿船+太阳；橙色可能来自太阳，不能误判船改回橙色 |

### 真实 PNG alpha

| 样本 | alpha=0 | 0<alpha<255 | alpha=255 | 总像素 |
| --- | ---: | ---: | ---: | ---: |
| G11（REST） | 441770 | 213590 | 0 | 655360 |
| G18（最新发布 CLI） | 432870 | 222490 | 0 | 655360 |

两图都有真正完全透明与半透明像素；**441770 是 G11 alpha=0，不是 alpha<255 总数**。两图 alpha<255 总数各 655360，不能把透明像素统计写成只凭请求参数推断，也不保证后续每张图有同样透明度。

### SSE 到达时间与数量

| 样本 | 请求 partial_images | 实际 partial + final | 首 partial received_ms | final received_ms | 整体 elapsed_ms |
| --- | ---: | --- | ---: | ---: | ---: |
| S01 生图 | 2 | 1 + 1 | 16583 | 20168 | 20491 |
| S02 改图 | 2 | 1 + 1 | 8345 | 13865 | 14152 |
| S03 生图 | 0 | 0 + 1 | 无 | 12870 | 13143 |

官方说明允许最终图提前完成时少于请求的 partial 数量，因此 S01/02 不是“少返回 1 张 final”或协议失败。事件 received_ms 在驱动读取并解析 data 事件、保存该图片前记录，是**客户端观测到达时间**，不是 wire timestamp；前序图片解码/保存可影响后续读取。整体 elapsed_ms 更不能当成首包或 final 网络到达时间。CLI 不暴露 SSE。

## 5. 47 项主矩阵

`U=输入/输出/总 tokens`，是响应 usage，不是费用。S 行取 completed 事件 usage。R 行列顶层文字/编排 usage，图像工具 usage 在详情另列，不混加。`—` 表示无该字段/没有图片，不等于 0 tokens 或免费。格式为系统实际解码结果；数量分 final/partial，不把预览计作 n 个最终结果。主矩阵与同编号详情共同构成逐项验收记录。

| 项 | 能力 | 传输 | 请求尺寸→实际尺寸 | 格式·final/partial | HTTP | ms | U | 验收结论 |
| --- | --- | --- | --- | --- | ---: | ---: | --- | --- |
| G01 | PNG low 最小横图 | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 14098 | 34/107/141 | 已验基准 |
| G02 | medium | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 27212 | 34/956/990 | 接受；元数据 medium |
| G03 | high | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 72012 | 34/3824/3858 | 接受；元数据 high |
| G04 | quality auto | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 14847 | 34/107/141 | 自动选择元数据 low |
| G05 | 最小竖图 | CLI | 640×1024→640×1024 | PNG·1/0 | 200 | 12025 | 34/107/141 | 精确；不是模板横图 |
| G06 | 3:1 | CLI | 1440×480→1440×480 | PNG·1/0 | 200 | 11347 | 34/54/88 | 精确 |
| G07 | size auto | CLI | auto→1254×1254 | PNG·1/0 | 200 | 16286 | 34/229/263 | 合法自动尺寸 |
| G08 | JPEG compression 100 | CLI | 1024×640→1024×640 | JPEG·1/0 | 200 | 13662 | 34/107/141 | 接受；DQT 无对照差异 |
| G09 | JPEG compression 50 | CLI | 1024×640→1024×640 | JPEG·1/0 | 200 | 14493 | 34/107/141 | 接受；DQT 无对照差异 |
| G10 | WebP 输出 | REST | 1024×640→— | —·0/0 | 400 | 1060 | — | 拒绝 output_format |
| G11 | transparent | REST | 1024×640→1024×640 | PNG·1/0 | 200 | 24979 | 22/107/129 | 真实透明 alpha |
| G12 | opaque | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 11936 | 34/107/141 | 参数接受 |
| G13 | background auto | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 13685 | 34/107/141 | 参数接受 |
| G14 | n=2 | CLI | 1024×640→两张均相同 | PNG·2/0 | 200 | 13394 | 68/213/281 | 数量精确 |
| G15 | moderation auto+user | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 13103 | 34/107/141 | 接受；安全效果未验 |
| G16 | moderation low | REST | 1024×640→1024×640 | PNG·1/0 | 200 | 14294 | 34/107/141 | 接受；安全效果未验 |
| G17 | 中文排版 | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 14762 | 36/107/143 | 已有看图：春日出发准确 |
| G18 | 发布版透明 PNG | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 17750 | 22/107/129 | 真实透明 alpha |
| G19 | JPEG compression 0 | CLI | 1024×640→1024×640 | JPEG·1/0 | 200 | 14349 | 34/107/141 | 接受；DQT 无对照差异 |
| E01 | PNG 单图改绿 | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 11756 | 670/107/777 | 颜色计数支持成功 |
| E02 | 连续 edit 加太阳 | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 12103 | 670/107/777 | 绿船+太阳 |
| E03 | 双参考图 | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 13877 | 1321/107/1428 | 橙船+八射线太阳 |
| E04 | PNG alpha mask | CLI | 1024×640→1024×640 | PNG·1/0 | 200 | 10732 | 673/107/780 | 局部指导有效；非像素锁 |
| E05 | JPEG 输入/输出 | CLI | 1024×640→1024×640 | JPEG·1/0 | 200 | 12088 | 661/107/768 | 已验组合 |
| E06 | JSON data URL edit | REST | 1024×640→1024×640 | PNG·1/0 | 200 | 15943 | 660/107/767 | REST 可用；CLI 不暴露 |
| E07 | input_fidelity high | REST | 1024×640→1024×640 | PNG·1/0 | 200 | 18173 | 660/107/767 | 接受；行为提升未证 |
| E08 | edit 自定义竖图 | CLI | 640×1024→640×1024 | PNG·1/0 | 200 | 14978 | 673/107/780 | 精确 |
| E09 | edit 3:1 宽图 | CLI | 1440×480→1440×480 | PNG·1/0 | 200 | 14999 | 673/54/727 | 精确 |
| E10 | edit 小方图 | CLI | 816×816→816×816 | PNG·1/0 | 200 | 14047 | 673/171/844 | 精确 |
| E11 | edit 16:9 | CLI | 1536×864→1536×864 | PNG·1/0 | 200 | 13485 | 672/120/792 | 精确 |
| E12 | edit auto | CLI | auto→1586×992 | PNG·1/0 | 200 | 13875 | 660/143/803 | 自动尺寸，非显式承诺 |
| E13 | edit n=2 | CLI | 1024×640→两张均相同 | PNG·2/0 | 200 | 14165 | 1320/213/1533 | 数量精确 |
| E14 | edit 小于像素下限 | REST | 512×512→— | —·0/0 | 400 | 595 | — | 服务端拒绝 |
| S01 | 生成 SSE partial=2 | REST | 1024×640→全部相同 | PNG·1/1 | 200 | 20491 | 34/174/208 | 合规提前完成；CLI 不暴露 |
| S02 | 编辑 SSE partial=2 | REST | 1024×640→全部相同 | PNG·1/1 | 200 | 14152 | 660/174/834 | 合规提前完成；CLI 不暴露 |
| S03 | 生成 SSE partial=0 | REST | 1024×640→1024×640 | PNG·1/0 | 200 | 13143 | 34/107/141 | 仅 final；CLI 不暴露 |
| N01 | 小于最低像素 | REST | 512×512→— | —·0/0 | 400 | 432 | — | 负向验收：按限制拒绝 |
| N02 | 非 16 倍数 | REST | 1025×640→— | —·0/0 | 400 | 413 | — | 负向验收：按限制拒绝 |
| N03 | 超过 3:1 | REST | 2048×512→— | —·0/0 | 400 | 1082 | — | 负向验收：按限制拒绝 |
| N04 | response_format=url | REST | 1024×640→— | —·0/0 | 400 | 455 | — | 参数未知；非远程输入测试 |
| R01 | 历史纯文字前置轮 | REST | 无尺寸/无图 | —·0/0 | 200 | 1863 | 36/5/41 | READY；保留历史、不扩展 |
| R02 | 历史聊天后生成图 | REST | 1024×1024→1254×1254 | PNG·1/0 | 200 | 28692 | 2393/48/2441 | **差异**：显式尺寸不符 |
| R03 | 历史上一轮改图 | REST | 1024×1024→1254×1254 | PNG·1/0 | 200 | 24082 | 2480/57/2537 | **差异**：显式尺寸不符 |
| R04 | data URL 参考图 edit | REST | 1024×1024→1254×1254 | PNG·1/0 | 200 | 24125 | 3129/67/3196 | **差异**：显式尺寸不符 |
| R05 | Responses 标准横图 edit | REST | 1536×1024→1536×1024 | PNG·1/0 | 200 | 23143 | 3047/62/3109 | 精确 |
| R06 | Responses 标准竖图 edit | REST | 1024×1536→1016×1548 | PNG·1/0 | 200 | 22307 | 3090/67/3157 | **差异**：显式尺寸不符 |
| R07 | Responses 自定义 edit | REST | 1024×640→— | —·0/0 | 400 | 840 | — | tools.size 拒绝 |

G02/03 相对 G01 输出 tokens 为 956/3824 对 107、耗时也更高，但每档只有一次不同生成，不能据此承诺延迟、成本或视觉质量的固定比例。G04 auto 本次响应元数据为 low，不代表始终选择 low。

## 6. 逐项复现参数和证据链接

### 共用约定（与每项详情组合使用）

下面是**规范化后的有效参数说明**，不是自动执行命令；本次不重放。确需复现时须另获付费授权，并用新的输出绝对路径，不覆盖原始证据；随机生成不能保证字节或视觉完全相同。端点/密钥由受信任启动器注入，不在本报告保存。

- **P0**（原文提示词）：`Minimal flat illustration: one orange sailboat with two triangular sails on a blue wave, cream background, no text, centered with wide margins.`
- **PT**：`An isolated green sailboat sticker, transparent background, no shadow, no text.`
- **PE**：`Change the orange sailboat to emerald green; keep everything else.`
- **PR**：`Change the orange boat in the input image to green. Preserve the background and composition.`
- **CLI-C**：`--mode images --auth api-key --image-model gpt-image-2 --prompt <本项原文> --size 1024x640 --quality low --format png --n 1 --output <新的绝对路径.png> --json`。edit 项将 mode 改 `edit`，加本项输入和 mask。详情增量覆盖默认；JPEG 同步改输出扩展名。这里显式写 n=1 便于复现，历史原始 argv（含重复参数与旧路径）以每项 request 的 cli_args 为准。
- **BODY-B**：`{model:"gpt-image-2",prompt:<本项原文>,size:"1024x640",quality:"low",output_format:"png",n:1}`。详情给出覆盖/追加字段。Images 生图路由 `/v1/images/generations`，Content-Type JSON；direct edit 路由 `/v1/images/edits`，除 E06 外为 multipart 字段+文件。单图 `image`，多图有序 `image[]`，mask 对首图。
- **BODY-R**：`{model:"gpt-5.4",input:<本项输入>,store:true,tools:[{type:"image_generation",size:"1024x1024",quality:"low",output_format:"png",action:<本项 action>}],tool_choice:{type:"image_generation"}}`。路由 `/responses?api-version=2025-04-01-preview`，JSON，认证 `api-key`，另有 `x-ms-oai-image-generation-deployment:gpt-image-2`；只记录历史实测，不新增 CLI 入口。
- R04..R07 的 input 形状：`[{role:"user",content:[{type:"input_text",text:PR},{type:"input_image",image_url:<G01 PNG data URL>}]}]`。将所链原始 G01 文件字节编码为 `data:image/png;base64,...`；不是远程 URL。E06 的 images 同理。
- 所有 CLI 项由 cli_args 重建 BODY-B；REST 项用记录字段+明确说明的文件映射。有效体完整展开在衍生 JSON 的 `cases[].effective_body`。输入文件路径/哈希在每项 request.inputs 与衍生 inputs；所有图片 SHA256 可复核。
- 详情中的 ID 是请求头 ID/CLI request_id；R 系列另列 response.id（不同概念）。REST 的 APIM ID 等其他头见 result.headers。详情 usage 采用主矩阵 U；所有原始 input/output token 明细在 response/衍生 JSON，不以缺失值作零。

### G01 · PNG low 基准
- Prompt：P0；CLI-C，无增量；BODY-B；无输入图。
- 请求 ID：`2aef18da-efc0-4475-a18a-3838b1599c93`。HTTP 200，14098 ms，U=34/107/141；PNG 1024×640，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G01/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G01/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G01/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G01/output.png)

### G02 · medium 质量
- Prompt：P0；CLI-C 覆盖 `--quality medium`；body quality=medium。响应元数据 medium，未做盲评质量排序。
- 请求 ID：`bfc367b1-e26b-4ec6-a8cb-3bd538ebae3a`。HTTP 200，27212 ms，U=34/956/990；PNG 1024×640，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G02/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G02/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G02/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G02/output.png)

### G03 · high 质量
- Prompt：P0；CLI-C 覆盖 `--quality high`；body quality=high。接受且元数据 high，不承诺固定质量增益。
- 请求 ID：`c137eb33-bd82-44a0-a925-0756bd8c7631`。HTTP 200，72012 ms，U=34/3824/3858；PNG 1024×640，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G03/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G03/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G03/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G03/output.png)

### G04 · quality auto
- Prompt：P0；CLI-C 覆盖 `--quality auto`；body quality=auto；响应选择 low。
- 请求 ID：`565cc2ff-5919-493e-8471-7ed5b77df0af`。HTTP 200，14847 ms，U=34/107/141；PNG 1024×640，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G04/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G04/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G04/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G04/output.png)

### G05 · 最小竖图
- Prompt：P0；CLI-C 覆盖 `--size 640x1024`；body size=640x1024。早期模板里的 1024x640 不生效。
- 请求 ID：`2ef80de0-dae8-48c0-9ce7-90069ab27c9c`。HTTP 200，12025 ms，U=34/107/141；PNG 640×1024，1 final，精确。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G05/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G05/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G05/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G05/output.png)

### G06 · 3:1 宽图
- Prompt：P0；CLI-C 覆盖 `--size 1440x480`；body size=1440x480。
- 请求 ID：`6cc85cbe-5015-4781-af1b-16b5bbfdb461`。HTTP 200，11347 ms，U=34/54/88；PNG 1440×480，1 final，精确。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G06/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G06/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G06/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G06/output.png)

### G07 · auto 尺寸
- Prompt：P0；CLI-C 覆盖 `--size auto`；body size=auto。1254 非 16 倍数不是显式尺寸违约。
- 请求 ID：`f799ba00-5b8a-47c0-9bfd-e2841d3bb400`。HTTP 200，16286 ms，U=34/229/263；PNG 1254×1254，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G07/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G07/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G07/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G07/output.png)

### G08 · JPEG compression 100
- Prompt：P0；CLI-C 覆盖 `--format jpeg --output-compression 100`；body output_format=jpeg、output_compression=100；不是模板 PNG。
- 请求 ID：`d729b3a6-dd60-4116-97bf-ba791f50444b`。HTTP 200，13662 ms，U=34/107/141；JPEG 1024×640，1 final。量化表见第 3 节。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G08/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G08/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G08/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G08/output.jpeg)

### G09 · JPEG compression 50
- Prompt：P0；CLI-C 覆盖 `--format jpeg --output-compression 50`；body output_format=jpeg、output_compression=50。
- 请求 ID：`8f3fdfc5-b842-41d2-86d3-894cdd6efeb5`。HTTP 200，14493 ms，U=34/107/141；JPEG 1024×640，1 final。DQT 与 G08/G19 一致。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G09/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G09/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G09/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G09/output.jpeg)

### G10 · WebP 输出拒绝
- Prompt：P0；REST generations，BODY-B 覆盖 output_format=webp；没有使用 CLI。
- 请求 ID：`24d14048-b33f-490d-82b0-a5fe8d0ed361`。HTTP 400，1060 ms，usage 缺失，0 图片。code=invalid_value、type=invalid_request_error、param=output_format，服务仅接受 png/jpeg。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G10/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G10/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G10/result.json)；无图片链接（未产图）。

### G11 · REST 透明背景
- Prompt：PT；REST generations，BODY-B 追加 background=transparent。
- 请求 ID：`d4e504f1-0542-40f4-951f-1c76000eece0`。HTTP 200，24979 ms，U=22/107/129；PNG 1024×640，1 final。真实 alpha 与既有透明绿船视觉结论见第 4 节。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G11/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G11/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G11/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G11/output-1.png)

### G12 · opaque 背景
- Prompt：P0；CLI-C 追加 `--background opaque`；body background=opaque。
- 请求 ID：`cef0cdaa-4a95-46c3-b352-6e5596b104d9`。HTTP 200，11936 ms，U=34/107/141；PNG 1024×640，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G12/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G12/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G12/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G12/output.png)

### G13 · auto 背景
- Prompt：P0；CLI-C 追加 `--background auto`；body background=auto；不是 size auto。
- 请求 ID：`ebd5e81e-cb91-497f-a354-c6ef662e7c3b`。HTTP 200，13685 ms，U=34/107/141；PNG 1024×640，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G13/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G13/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G13/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G13/output.png)

### G14 · n=2
- Prompt：P0；CLI-C 覆盖 `--n 2`；body n=2（早期模板 n=1 不适用）。不是异步 Batch API。
- 请求 ID：`1657eeac-98d0-4ec0-ae03-626886a50897`。HTTP 200，13394 ms，U=68/213/281；2 final，均 PNG 1024×640。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G14/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G14/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G14/result.json) · [图 1](../../artifacts/gpt-image-capabilities/20260907/G14/output-01.png) · [图 2](../../artifacts/gpt-image-capabilities/20260907/G14/output-02.png)

### G15 · moderation auto 与 user
- Prompt：P0；CLI-C 追加 `--moderation auto --user gpt-image-cli-capability-test`；body 同名字段。合规提示词只证明接受。
- 请求 ID：`a34f332d-d32f-41a0-a293-0d7b3b8a36d6`。HTTP 200，13103 ms，U=34/107/141；PNG 1024×640，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G15/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G15/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G15/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G15/output.png)

### G16 · moderation low
- Prompt：P0；REST generations，BODY-B 追加 moderation=low；不以合规输出证明拦截效果。
- 请求 ID：`891ac546-86b3-4e96-86e0-ef06cb072d61`。HTTP 200，14294 ms，U=34/107/141；PNG 1024×640，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G16/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G16/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G16/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G16/output-1.png)

### G17 · 中文标题与布局
- Prompt：`A clean cream poster. Exact large Chinese headline: 春日出发. Below it a small orange sailboat, blue wave, no other words.`；CLI-C 无其他增量，BODY-B 使用此 prompt。
- 请求 ID：`fd0b6fa8-1e0a-4809-ab67-6b4b2b08c38c`。HTTP 200，14762 ms，U=36/107/143；PNG 1024×640，1 final。已有看图确认“春日出发”准确，非全面中文 OCR 测试。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G17/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G17/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G17/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G17/output.png)

### G18 · 最新发布 CLI 透明 PNG
- Prompt：PT；CLI-C 追加 `--background transparent`；body background=transparent。body_kind=effective，exe 指纹见第 7 节。
- 请求 ID：`8fae95b3-b184-4754-891c-5b296337bea1`。HTTP 200，17750 ms（CLI 自身 17429），U=22/107/129；PNG 1024×640，1 final，真实透明 alpha。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G18/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G18/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G18/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G18/output.png)

### G19 · 最新发布 CLI JPEG compression 0
- Prompt：P0；CLI-C 覆盖 `--format jpeg --output-compression 0`；body output_format=jpeg、output_compression=0。body_kind=effective。
- 请求 ID：`467eab1d-6393-4a80-9efe-e022c7b7223b`。HTTP 200，14349 ms（CLI 自身 14039），U=34/107/141；JPEG 1024×640，1 final；DQT 对照无差异。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/G19/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/G19/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/G19/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/G19/output.jpeg)

### E01 · PNG 单图改绿
- Prompt：`Change only all orange parts of the sailboat to emerald green. Preserve its shape, cream background and blue wave.`；CLI-C mode=edit，加 `--image <G01/output.png>`；BODY-B + multipart image=G01。
- 请求 ID：`3fbdcae9-9d84-473b-a3e6-a033b25d63c3`。HTTP 200，11756 ms，U=670/107/777；PNG 1024×640，1 final；green/orange 计数见第 4 节。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E01/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E01/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E01/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E01/output.png)

### E02 · 基于上次输出继续改
- Prompt：`Keep the green sailboat, cream background and blue wave. Add a small yellow sun in the upper right corner.`；CLI-C mode=edit，加 `--image <E01/output.png>`；multipart image=E01。无会话状态。
- 请求 ID：`4b8b2e17-dc02-4ef2-af73-37180331edd8`。HTTP 200，12103 ms，U=670/107/777；PNG 1024×640，1 final；已有看图为绿船+太阳，右上黄色 0→7592。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E02/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E02/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E02/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/E01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E02/output.png)

### E03 · 双参考图
- Prompt：`Keep the sailboat from the first reference. Add the yellow circular sun with eight orange rays from the second reference in the upper right. Cream background and blue wave.`；CLI-C mode=edit，按序 `--image <G01/output.png> --image <E03/sun-reference.png>`；multipart 两个 image[]。
- 请求 ID：`9933f149-df31-4bc9-8889-bf48ac200b6a`。HTTP 200，13877 ms，U=1321/107/1428；PNG 1024×640，1 final。已有看图为橙船+八射线太阳，第一参考是 G01，不是 E01 绿船。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E03/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E03/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E03/result.json) · [输入 1](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [输入 2](../../artifacts/gpt-image-capabilities/20260907/E03/sun-reference.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E03/output.png)

### E04 · PNG alpha 蒙版
- Prompt：`Add a small yellow sun only in the transparent upper-right region of the mask. Keep the orange sailboat and blue wave unchanged.`；CLI-C mode=edit，`--image <G01/output.png> --mask <E04/mask.png>`；multipart image+mask。
- 请求 ID：`9e74421a-dd18-4445-a010-2948852730b6`。HTTP 200，10732 ms，U=673/107/780；PNG 1024×640，1 final。太阳区域视觉符合；区外不是像素锁定，定量见第 4 节。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E04/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E04/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E04/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [蒙版](../../artifacts/gpt-image-capabilities/20260907/E04/mask.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E04/output.png)

### E05 · JPEG 输入和输出
- Prompt：`Change the sailboat to emerald green; keep the background and wave.`；CLI-C mode=edit，`--image <E05/input.jpg> --format jpeg --output-compression 50`；BODY-B 覆盖 jpeg/50，multipart image 为已保存的 G01 转 JPEG 输入。
- 请求 ID：`1bfae81d-89c5-4ca2-adb4-e76708fddd6e`。HTTP 200，12088 ms，U=661/107/768；JPEG 1024×640，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E05/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E05/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E05/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/E05/input.jpg) · [图片](../../artifacts/gpt-image-capabilities/20260907/E05/output.jpeg)

### E06 · JSON data URL edit
- Prompt：PE；REST edits，JSON BODY-B + **重建** `images:[{image_url:<G01 PNG data URL>}]`；不是 multipart，也不是 CLI --json 功能。原始 request.inputs 记录 G01，但早期 body 未保存 images。
- 请求 ID：`ddaa3f01-4ee7-4a67-bcbb-6a5da797b235`。HTTP 200，15943 ms，U=660/107/767；PNG 1024×640，1 final。REST 路径可用，CLI 不暴露。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E06/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E06/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E06/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E06/output-1.png)

### E07 · input_fidelity high
- Prompt：PE；REST edits，multipart BODY-B + input_fidelity=high、image=G01；非 CLI。
- 请求 ID：`69dfeb44-0100-4aec-9db7-6ebf17f3d4ba`。HTTP 200，18173 ms，U=660/107/767；PNG 1024×640，1 final。**只证明参数被接受，无独立保真行为提升证明**，不增加 CLI 开关。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E07/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E07/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E07/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E07/output-1.png)

### E08 · direct edit 640×1024
- Prompt：`Change the orange sailboat to emerald green. Fit the composition to the requested portrait canvas, preserving the cream background and blue wave.`；CLI-C mode=edit，`--image <G01/output.png> --size 640x1024`；body size=640x1024。
- 请求 ID：`5c7ef84b-a44b-43fd-a761-2ec2d48aa2e2`。HTTP 200，14978 ms，U=673/107/780；PNG **640×1024 精确**，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E08/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E08/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E08/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E08/output.png)

### E09 · direct edit 1440×480
- Prompt：`Change the orange sailboat to emerald green. Fit the composition to the requested wide canvas, preserving the cream background and blue wave.`；CLI-C mode=edit，`--image <G01/output.png> --size 1440x480`；body size=1440x480。
- 请求 ID：`203b46ad-6e4c-4e0b-897a-84a2c34fbdd7`。HTTP 200，14999 ms，U=673/54/727；PNG **1440×480 精确**，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E09/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E09/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E09/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E09/output.png)

### E10 · direct edit 816×816
- Prompt：`Change the orange sailboat to emerald green. Fit the composition to the requested square canvas, preserving the cream background and blue wave.`；CLI-C mode=edit，`--image <G01/output.png> --size 816x816`；body size=816x816。
- 请求 ID：`f94f15d3-f170-4e23-ac05-3a6136c13b7f`。HTTP 200，14047 ms，U=673/171/844；PNG **816×816 精确**，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E10/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E10/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E10/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E10/output.png)

### E11 · direct edit 1536×864
- Prompt：`Change the orange sailboat to emerald green. Fit the composition to the requested canvas, preserving the cream background and blue wave.`；CLI-C mode=edit，`--image <G01/output.png> --size 1536x864`；body size=1536x864。
- 请求 ID：`4ecc0a4d-f7a2-4612-8a38-e3cee9d8dad5`。HTTP 200，13485 ms，U=672/120/792；PNG **1536×864 精确**，1 final。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E11/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E11/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E11/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E11/output.png)

### E12 · direct edit auto
- Prompt：`Change the orange sailboat to emerald green. Preserve everything else.`；CLI-C mode=edit，`--image <G01/output.png> --size auto`；body size=auto。
- 请求 ID：`e39231ca-e3a5-42ff-8121-0fb00b2408b0`。HTTP 200，13875 ms，U=660/143/803；PNG **1586×992**，1 final；自动输出，非显式 grid 承诺。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E12/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E12/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E12/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/E12/output.png)

### E13 · direct edit n=2
- Prompt：`Change the orange sailboat to emerald green. Preserve everything else.`；CLI-C mode=edit，`--image <G01/output.png> --n 2`；body n=2。
- 请求 ID：`ae7f83a2-a478-441e-b0ff-2e9cd5203118`。HTTP 200，14165 ms，U=1320/213/1533；2 final，均 PNG 1024×640。不是最大数量测试。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E13/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E13/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E13/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图 1](../../artifacts/gpt-image-capabilities/20260907/E13/output-01.png) · [图 2](../../artifacts/gpt-image-capabilities/20260907/E13/output-02.png)

### E14 · direct edit 512×512 拒绝
- Prompt：`Change the orange sailboat to emerald green.`；REST edits，multipart BODY-B 覆盖 size=512x512，image=G01；不通过 CLI 本地前置校验发送。
- 请求 ID：`eb720e9d-ebab-4db5-aa57-a63d99780f29`。HTTP 400，595 ms，usage 缺失，0 图片。code=invalid_value、type=image_generation_user_error、param=size；低于当前最低像素预算。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/E14/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/E14/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/E14/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png)；无输出图片。

### S01 · 生图 SSE partial_images=2
- Prompt：P0；REST generations，JSON BODY-B + stream=true、partial_images=2；CLI 不暴露。
- 请求 ID：`4aa8a135-7323-4a92-b456-dc3d2f985d0b`。HTTP 200，20491 ms，U=34/174/208；PNG 1024×640，1 partial+1 final。首 partial=16583 ms，final=20168 ms。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/S01/request.json) · [response 事件](../../artifacts/gpt-image-capabilities/20260907/S01/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/S01/result.json) · [partial](../../artifacts/gpt-image-capabilities/20260907/S01/event-1.png) · [final](../../artifacts/gpt-image-capabilities/20260907/S01/event-2.png)

### S02 · 改图 SSE partial_images=2
- Prompt：PE；REST edits，multipart BODY-B + stream=true、partial_images=2、image=G01；CLI 不暴露。
- 请求 ID：`10f1a567-b2a7-46e6-a0eb-a2ca5a673d04`。HTTP 200，14152 ms，U=660/174/834；PNG 1024×640，1 partial+1 final。首 partial=8345 ms，final=13865 ms。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/S02/request.json) · [response 事件](../../artifacts/gpt-image-capabilities/20260907/S02/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/S02/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [partial](../../artifacts/gpt-image-capabilities/20260907/S02/event-1.png) · [final](../../artifacts/gpt-image-capabilities/20260907/S02/event-2.png)

### S03 · 生图 SSE partial_images=0
- Prompt：P0；REST generations，JSON BODY-B + stream=true、partial_images=0；CLI 不暴露。
- 请求 ID：`bf56905d-8fb5-4cfb-9011-c38b1e839f66`。HTTP 200，13143 ms，U=34/107/141；PNG 1024×640，0 partial+1 final。首 partial 不适用，final=12870 ms。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/S03/request.json) · [response 事件](../../artifacts/gpt-image-capabilities/20260907/S03/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/S03/result.json) · [final](../../artifacts/gpt-image-capabilities/20260907/S03/event-1.png)

### N01 · 小于最低像素
- Prompt：P0；REST generations，BODY-B 覆盖 size=512x512。
- 请求 ID：`ae3eb810-4c25-4ea4-a9d8-812b3457c5a9`。HTTP 400，432 ms，usage 缺失，0 图片。code=invalid_value、type=image_generation_user_error、param=size；低于最低像素预算。负向测试符合限制，不是生成成功。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/N01/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/N01/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/N01/result.json)；无图片。

### N02 · 非 16 倍数
- Prompt：P0；REST generations，BODY-B 覆盖 size=1025x640。
- 请求 ID：`d0465c2c-6ad6-4cdb-92d3-79985fdce48c`。HTTP 400，413 ms，usage 缺失，0 图片。code=invalid_value、type=image_generation_user_error、param=size；宽高必须被 16 整除。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/N02/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/N02/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/N02/result.json)；无图片。

### N03 · 超过 3:1
- Prompt：P0；REST generations，BODY-B 覆盖 size=2048x512。
- 请求 ID：`dbd618bc-9db7-417f-8c56-0cdae63328f6`。HTTP 400，1082 ms，usage 缺失，0 图片。code=invalid_value、type=image_generation_user_error、param=size；4:1 超过 3:1。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/N03/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/N03/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/N03/result.json)；无图片。

### N04 · response_format=url
- Prompt：P0；REST generations，BODY-B 追加 response_format=url；不是 output_format，也不是 URL 输入。
- 请求 ID：`f744f83c-4f35-4852-9927-b66460d7ad9c`。HTTP 400，455 ms，usage 缺失，0 图片。code=unknown_parameter、type=invalid_request_error、param=response_format。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/N04/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/N04/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/N04/result.json)；无图片。

### R01 · 历史纯文字前置轮（不扩展）
- 历史 input：`We will create a minimal illustration of an orange sailboat on a blue wave and cream background. Reply READY only; do not generate any image yet.`；BODY-R **移除 tools/tool_choice**，仅 model=gpt-5.4、input、store=true；没有图片尺寸和输入图。返回 `READY`。
- 请求 ID：`484fc8e8-22c0-441b-a450-18ebd8cc4ad8`；response.id=`resp_0460f8c0b3f952ea006a9e398cbff88196b9ae29ef6bc1a630`。HTTP 200，1863 ms；顶层 U=36/5/41，image_gen U=0/0/0；0 图片。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/R01/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/R01/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/R01/result.json)；无图片。这不是 CLI 聊天支持声明。

### R02 · 历史聊天后生成图（尺寸差异）
- input：P0；BODY-R action=generate，previous_response_id=R01 的 response.id；size=1024x1024；无新输入图。
- 请求 ID：`ac5990bf-ec08-4d9e-9983-210d81210b26`；response.id=`resp_0460f8c0b3f952ea006a9e398e99a08196ba9daf7757523014`。HTTP 200，28692 ms；顶层 U=2393/48/2441，image_gen U=35/1024/1059。
- 1 final PNG，**请求 1024×1024，实际 1254×1254：差异**。历史续接成功不等于尺寸通过。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/R02/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/R02/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/R02/result.json) · [图片](../../artifacts/gpt-image-capabilities/20260907/R02/output-1.png)

### R03 · 历史上一轮改图（尺寸差异）
- input：`Change the boat to green, preserving the background and composition.`；BODY-R action=edit，previous_response_id=R02 的 response.id；size=1024x1024。依赖服务端历史，不是 CLI 重传输入图。
- 请求 ID：`ca55632b-9f9a-480c-92cd-95133ddfe906`；response.id=`resp_0460f8c0b3f952ea006a9e39ab51a88196a2d798422f46258c`。HTTP 200，24082 ms；顶层 U=2480/57/2537，image_gen U=162/1024/1186。
- 1 final PNG，**请求 1024×1024，实际 1254×1254：差异**。续接证据保留，不新增会话功能。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/R03/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/R03/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/R03/result.json) · [上一轮图](../../artifacts/gpt-image-capabilities/20260907/R02/output-1.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/R03/output-1.png)

### R04 · data URL 参考图 edit（尺寸差异）
- Prompt：PR；BODY-R action=edit、size=1024x1024；input 使用本节共用 content 形状，输入 G01 data URL；无 previous_response_id。
- 请求 ID：`4407ee70-df16-465e-a971-d098cfafd8d6`；response.id=`resp_0fe30735ec5480bd006a9e39c45a0881979d28f0f85cfefbc3`。HTTP 200，24125 ms；顶层 U=3129/67/3196，image_gen U=695/576/1271。
- 1 final PNG，**请求 1024×1024，实际 1254×1254：差异**。响应输出元数据的 1024x1024 不能替代解码尺寸。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/R04/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/R04/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/R04/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/R04/output-1.png)

### R05 · Responses 标准横图 edit
- Prompt：PR；BODY-R action=edit、size=1536x1024；input 为 G01 data URL + PR；无 previous_response_id。
- 请求 ID：`863349af-5368-4ea7-ab18-02d57bb40b87`；response.id=`resp_003062a522a436f8006a9e3beffb4481949fb10220e5d34a3b`。HTTP 200，23143 ms；顶层 U=3047/62/3109，image_gen U=690/384/1074。
- 1 final PNG，**1536×1024 精确**。这是本组 Responses 已验精确尺寸，不由此推广所有尺寸。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/R05/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/R05/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/R05/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/R05/output-1.png)

### R06 · Responses 标准竖图 edit（尺寸差异）
- Prompt：PR；BODY-R action=edit、size=1024x1536；input 为 G01 data URL + PR；无 previous_response_id。
- 请求 ID：`ecd5ca62-ba65-443e-8905-280a79217057`；response.id=`resp_0f56b27af156481a006a9e3c07714c819687f220c0de37d196`。HTTP 200，22307 ms；顶层 U=3090/67/3157，image_gen U=695/672/1367。
- 1 final PNG，**请求 1024×1536，实际 1016×1548：差异**，不把大致纵横比例相近算作精确。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/R06/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/R06/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/R06/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png) · [图片](../../artifacts/gpt-image-capabilities/20260907/R06/output-1.png)

### R07 · Responses 自定义尺寸拒绝
- Prompt：PR；BODY-R action=edit、size=1024x640；input 为 G01 data URL + PR；无 previous_response_id。
- 请求 ID：`c6765cf8-7bda-4d68-9e0b-1f52d6bd7d2f`。HTTP 400，840 ms；无 response.id、usage 缺失、0 图片。code=invalid_value、type=image_generation_user_error、param=tools；错误列出的支持值为 1024x1024、1024x1536、1536x1024、auto。
- **错误列举支持值不保证实际尺寸一致**：R04/R06 已证明仍需解码复核。不能把 direct edit 自定义尺寸能力迁移到此路由。
- 证据：[request](../../artifacts/gpt-image-capabilities/20260907/R07/request.json) · [response](../../artifacts/gpt-image-capabilities/20260907/R07/response.json) · [result](../../artifacts/gpt-image-capabilities/20260907/R07/result.json) · [输入](../../artifacts/gpt-image-capabilities/20260907/G01/output.png)；无输出图片。

## 7. CLI 版本、报告 schema 与离线验证

- **后续轻量重打包（Native AOT）**：`artifacts/gpt-image-cli-aot/win-x64/gpt-image.exe` 为 **5,250,560 字节（5.01 MiB）**，相对下述旧包缩小92.9%，无需安装.NET。首轮AOT测试SHA256为 `BC92F4D0C553F43BC7C58F97C578D4C5039473AA46C79B3EADFD7C7EB1434546`，对应证据 `artifacts/gpt-image-cli-published-tests/8af65fea-32a9-402a-b08e-3f32fa5bdb08/`；后续重新链接的实际指纹以随包 `release-manifest.json` 为准。JSON改为源生成，协议/参数不变；实际原生exe通过9个离线场景、287条断言。重打包未请求云端：本报告G18/G19等云端实测仍属于下述旧版本，文中“最新发布CLI”均指当时的Managed版本，不指AOT。
- 已有 win-x64 self-contained 单文件发布产物为 **73,574,097 字节（约 70.17 MiB）**。本次只读核对 exe SHA256：`2FF4EE10BDD7E9E7A6D4438E34A9CCE53FF9B22BC1731B4AEF746AB621A21093`，与 G18/G19 request.executable_sha256 一致；两项确由最新已发布 exe 跑出。不要把此指纹套给早期无指纹记录。
- 最终重新执行离线回归：**355 checks 全部通过**，产物目录 `artifacts/gpt-image-cli-tests/3515657a92ac478c9735c569a2963077`。完整解决方案构建成功（5.4 秒），仅有既有 AngleSharp NU1902 / SQLitePCLRaw NU1903 依赖漏洞警告；CLI 诊断无错误，git diff --check 通过。
- 已只读核对 `CommandLineParser.cs`、`CliApplication.cs`、`CliReport.cs`、`ImageResultWriter.cs`、`ParameterValidation.cs`。`--overwrite` 是无值本地开关、默认 false；默认 `CreateNew`，显式开关才 `Create`。images/edit 显式输出可按 n 提前检测冲突并**请求前 exit 2**；目录名带 GUID；竞态或未知 Responses 数量在保存时保护，可能 HTTP 200 后 exit 1，保留部分成功 files。不能笼统说所有冲突都不会产生云请求。
- JSON schema：`ok,exit_code,files,mode,http_status,request_id,elapsed_ms,usage,api_error,error,response_metadata`；没有 `status`。`api_error` 仅取当前根 error 对象，code/type/param 为长度 1..64、匹配 `\A[A-Za-z0-9_.\[\]-]+\z` 且不含当前 API key 的字符串，否则 null；message 固定 `服务端返回错误。`，不回显不受信任的服务端 message。旧记录缺少 api_error 不是错误。
- `--mask` 当前只做文件存在、`.png` 扩展名和 8 字节 PNG signature 检查，**CLI 没有解码验 alpha/尺寸**。本报告的 mask 验证来自离线分析脚本，不是 CLI 内建功能。
- 分析入口：[Analyze-Capabilities.ps1](./Analyze-Capabilities.ps1)。仓库根运行 `& ./tools/GptImageCli/Analyze-Capabilities.ps1`；可显式传 `-EvidenceRoot`，但只允许此工作区的 `artifacts/gpt-image-capabilities/20260907`。脚本拒绝越界、重解析点和非 JSON/PNG/JPEG 证据；不读取环境密钥、主配置或 .env，不访问网络、不安装依赖。
- 使用现有 Windows PowerShell 7 + System.Drawing 解码，纯 PowerShell DQT 头部解析；PowerShell AST 语法检查 0 errors，首次离线执行成功。只写 `analysis-derived.json`，保留所有历史原件；全部 187 个源文件前后哈希一致。脚本不会启动 Test-Capabilities.ps1 或 Publish.ps1。
- 最终交叉校验通过：第 5 节 47 行主矩阵、第 6 节 47 个详情；HTTP、耗时、usage、请求/响应 ID、提示词、解码尺寸和输出链接均与证据一致，209 个本地链接存在。4 种异常 JPEG 头部、非法根目录拒绝测试通过。临时文档校验器前两次误计入其他章节的 E04 标题及 JPEG/颜色表，查官方多行正则说明后按章节限定，第三次验证通过；没有据此修改历史证据或掩盖失败。
- 最终发行包由 `Publish.ps1` 同步本报告、README 和 skill。单独分发 exe/文档不自动带上 artifacts，图片相对链接须结合仓库证据目录使用；报告正文已保留逐项结果和请求 ID。

## 8. 官方来源及其用途

以下官方页面已在本轮调查中读取，用于接口范围/术语说明；当前端点验收结论以逐项证据为准，不把官方可用范围等同于本 APIM 已验。

1. [OpenAI Image generation guide](https://developers.openai.com/api/docs/guides/image-generation)：生图/改图、透明背景、流式 partial 可以少于请求数量、蒙版指导不是严格像素锁。
2. [OpenAI Images API reference](https://developers.openai.com/api/reference/resources/images)：Images 字段、输入形态、尺寸/数量约束和输出参数；E06 的 JSON 形态不代表 CLI 接入。
3. [GPT Image 2 model](https://developers.openai.com/api/docs/models/gpt-image-2)：模型适用范围和显式尺寸/experimental 边界；不替代当前 APIM 实测。
4. [Azure image generation](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/dall-e)：Azure 图片接口及部署上下文；不同区域/API 版本不能由一个 APIM 推广。
5. [Azure Responses](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses)：Responses 与多轮上下文。即使指南多轮范围与 R01..R03 有冲突，也只记录“当前 APIM 实测成功”，不声称所有 Azure 都支持，不扩展用户当前范围。
6. [libjpeg-turbo 官方 jdmarker.c](https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/main/src/jdmarker.c)：本次在编写 parser **之前**实际只读获取，依据 marker/length/DQT 格式编写最小解析；未安装依赖、未调用云模型。
7. [Microsoft 正则表达式选项](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-options)：本次校验器排错时读取；Multiline 的 `^` 匹配每行开头，不自动限定文档章节，因此按第 5/6 节限定表格/详情核验范围，避免重复计数。

### 本次变更记录（限定文件内，不写其他日志文件）
- 新增本报告与离线分析脚本；修正早期模板/有效参数语义，完整覆盖 47 项，附全部原始证据和产物链接。
- 增加 JPEG DQT、E04 蒙版内外差值、颜色/alpha/SSE 定量结论；README/skill 同步真实尺寸、355 既有回归口径、防覆盖和 api_error 安全 schema。
- 保留拒绝、差异、未测与 CLI 不暴露边界；未新增云请求、CLI 功能或付费 Batch，未修改历史原始证据。