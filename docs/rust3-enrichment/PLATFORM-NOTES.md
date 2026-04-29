# 平台知识库
> 随项目推进持续更新。每发现新的平台知识，立即追加。

## Tauri 2
- 所有 IPC 命令需要在 capabilities/*.json 中声明权限
- 无边框窗口需要前端实现 resize 边缘检测 + startResizeDragging()
- window.startDragging() 需要 core:window:allow-start-dragging 权限
- 事件监听需要 core:event:allow-listen 权限
- app_handle.emit() 需要 core:event:default 权限

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

## Rust3 已有 API Key 获取方式
- `src-tauri/tests/common.rs` 的 `load_rust2_endpoints()`
- 从 Rust2 的 SQLite database 读取已配置的 endpoint
- 路径: `{data_dir}/com.truefluent.pro/truefluent.db`
- 需要 Rust2 数据库存在（开发机上已有）

## i18n
- 所有 .tsx 文件的用户可见字符串必须走 useTranslation + t()
- 新增 view 时必须同步更新 zh-CN.json 和 en.json
- 现有 i18n 文件: src/lib/locales/zh-CN.json (850 行) + en.json (850 行)

## Avalonia → React 已知差异
- Cursor 类型名不同（SizeNWSE → css cursor: nwse-resize）
- ObservableCollection → Zustand store 或 React state
- ICommand → onClick handler
- PropertyChanged → useEffect / useSyncExternalStore
