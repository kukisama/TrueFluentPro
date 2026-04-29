# 平台知识库
> 随项目推进持续更新。每发现新的平台知识，立即追加。

## Tauri 2
- 所有 IPC 命令需要在 capabilities/*.json 中声明权限
- 无边框窗口需要前端实现 resize 边缘检测 + startResizeDragging()
- window.startDragging() 需要 core:window:allow-start-dragging 权限
- 事件监听需要 core:event:allow-listen 权限
- app_handle.emit() 需要 core:event:default 权限
- `generate_handler!` 中所有已定义的 `#[tauri::command]` 函数必须注册，否则产生 `dead_code` 警告
- Tauri 事件命名约定: kebab-case（如 `realtime-event`, `studio-task-update`, `batch-package-update`）
- Tauri 2 事件 payload 必须是 `Serialize + Clone`

## Azure OpenAI
- Responses API 路径: /openai/deployments/{dep}/responses
- Chat Completions 路径: /openai/deployments/{dep}/chat/completions
- 图片最小尺寸: 1024x1024（256x256 已不支持）
- max_tokens 已弃用，使用 max_completion_tokens 或 max_output_tokens
- APIM 网关: /v1/files 仅支持 upload（purpose=assistants），不支持 list/get/delete
- /images/edits 的 image 参数只接受 multipart binary，不接受 file_id
- file_id 方式仅 Responses API 支持（input_image type）
- x-ms-oai-image-generation-deployment header 用于指定图片模型部署名
- Responses API 的 model 参数是文本模型（gpt-4o），不是图片模型

## Azure Speech SDK
- 实时翻译使用 WebSocket 协议: wss://{region}.stt.speech.microsoft.com/speech/universal/v2
- SDK 版本: Microsoft.CognitiveServices.Speech 1.45.0（NuGet）
- DLL 依赖: Microsoft.CognitiveServices.Speech.core.dll + 5 个附属 DLL
- 认证: Ocp-Apim-Subscription-Key header 或 Authorization: Bearer {aad_token}
- 翻译配置: SpeechTranslationConfig → 设置源语言 + 添加目标语言
- 事件类型: SessionStarted → Recognizing → Recognized → Translated → SessionStopped
- 音频输入支持: Push stream (PCM 16kHz 16bit mono) 或 microphone 直接输入

## OpenAI Realtime
- WebSocket 端点: wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview
- Azure 端点: wss://{resource}.openai.azure.com/openai/realtime?api-version=2024-10-01-preview&deployment={dep}
- Session 配置: 通过 session.update 事件设置语言、turn_detection、tools
- 音频格式: pcm16 (16kHz, 16-bit, mono, little-endian)
- 认证: OpenAI 用 Authorization: Bearer, Azure 用 api-key header

## AudioLab — 听析中心
- 8 阶段执行顺序:
  1. Transcribed — 转录（STT / Whisper）
  2. Summarized — 摘要
  3. MindMap — 思维导图
  4. Insight — 洞察
  5. Research — 调研
  6. PodcastScript — 播客脚本
  7. PodcastAudio — 播客音频（TTS）
  8. Translated — 翻译
- 每阶段有 Pending/Running/Completed/Failed/Stale 状态
- 阶段间有 DAG 依赖：Research 依赖 Transcribed + Summarized

## Batch Processing — 批处理
- Package 状态机: Draft → Queued → Running → Partial → Completed / Failed / Removed
- 子任务类型: speech_subtitle（语音字幕）、review_sheet（审校表）
- 子任务状态: pending → running → responding → completed / failed / paused
- Blob 存储: 通过 Azure Blob SDK 上传音频文件，获取 SAS URL
- Batch API: 适配 Azure OpenAI Batch API（上传 JSONL → 创建 batch → 轮询状态）

## Studio / Center — 创作工坊 / 媒体中心
- 消息分页: 使用 `before_sequence` cursor 向前加载，每次 20 条
- 分支(fork): 从指定 message_id 分裂新 session，复制该消息及之前所有消息
- Center 工作区: 按 round 组织资产，每个 round 对应一次生成调用
- 资产导出: 支持单个/批量导出到指定目录 + 可选 metadata.json
- 视频轮询: 创建后每 N 秒轮询状态，直到 completed/failed（poll_interval_ms 可配）

## 已知限制
- APIM /v1/files 仅支持 POST（上传），GET/DELETE 返回 404
- 图片编辑 /images/edits 仅接受 multipart binary file data，JSON file_id 方式全部失败
- Azure Speech SDK 的 recognize_once_async 是阻塞的，必须用 spawn_blocking
- PropertyCollection.get_property 使用 1024 字节缓冲区，超长属性值会截断
- 视频生成可能因 APIM 超时（30s）失败，需要使用异步轮询模式

## Rust3 已有 API Key 获取方式
- `src-tauri/tests/common.rs` 的 `load_rust2_endpoints()`
- 从 Rust2 的 SQLite database 读取已配置的 endpoint
- 路径: `{data_dir}/com.truefluent.pro/truefluent.db`
- 需要 Rust2 数据库存在（开发机上已有）

## i18n
- 所有 .tsx 文件的用户可见字符串必须走 useTranslation + t()
- 新增 view 时必须同步更新 zh-CN.json 和 en.json
- 现有 i18n 文件: src/lib/locales/zh-CN.json (850+ 行) + en.json (850+ 行)

## Avalonia → React 已知差异
- Cursor 类型名不同（SizeNWSE → css cursor: nwse-resize）
- ObservableCollection → Zustand store 或 React state
- ICommand → onClick handler
- PropertyChanged → useEffect / useSyncExternalStore
