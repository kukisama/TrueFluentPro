# 批次 10 施工单
> 日期：2026-04-29 | Phase 3 — 听析中心 + 批处理 | 进度 10/21 | 依赖：batch 9 ✅

## 目标
将 AudioLab 8 阶段任务从"仅入队不执行"推进到"端到端可执行"，实现 task_engine 对 audiolab 任务类型的完整分派。

## Spec 来源
| 文档 | 相关段落 |
|------|---------|
| .exchange/docs/Services/AudioTaskStageHandlerService.md | § 流程3-6 (AI 阶段通用 + 研究两阶段 + 播客 TTS + 自定义阶段) |
| .exchange/docs/Services/AudioTaskExecutor.md | § 流程3 (DAG 依赖检查), § 流程5 (AreDependenciesSatisfied) |
| .exchange/docs/Models/AudioLab.md | § AudioTaskDependencies (DAG 图), § AudioLifecycleStage (枚举) |

## Rust3 现状
- `task_engine.rs` 调度循环完善，支持并发控制 + 超时 + 重试 + billing
- `task_engine.rs` 的 `execute_task_real()` 只处理 4 种 task_type: "Transcription", "AiCompletion", "TTS", "ImageGeneration"
- AudioLab 命令 (`audiolab.rs`) 将任务提交到 `studio_tasks` 表（StudioTask），task_type 为 "audio_stage"/"audio_podcast_tts"/"audio_auto_tags"/"audio_research"
- **关键 GAP**: task_engine 从 `audio_task_queue` 表取任务，audiolab 命令写 `studio_tasks` 表 → 任务入队但永不执行
- `stage_runner.rs` 有 `run_stage()` 函数（调 AI completion），但未连接到 task_engine
- `get_previous_stage_content()` 中的 DAG 依赖关系与 Spec 不一致

## 运行时假设
- task_engine 已有 AI completion provider 可用（已在 batch-4 验证）
- TTS provider 可用（已在 batch-8 验证）
- 自测方法：`cargo test -p truefluent-pro-r3` + `cargo test -p tfp-audiolab` 全绿

## 任务清单

- [ ] T-001: AudioLab 命令桥接 — 提交到 audio_task_queue [Spec: AudioTaskExecutor.md §流程3]
  - 位置: @Rust3/src-tauri/src/commands/audiolab.rs
  - 契约: 修改 `audiolab_start_stage`, `audiolab_start_podcast_tts`, `audiolab_generate_auto_tags`, `audiolab_start_research` 四个命令，除写 StudioTask 外，同时写 AudioTaskRow 到 audio_task_queue
  - 逻辑:
    1. 保留现有 StudioTask 创建（前端读取状态用）
    2. 同时创建 AudioTaskRow { task_type 使用 audiolab 前缀, stage 对应实际阶段, audio_item_id 用 session_id, prompt_text 携带参数, status="Queued" }
    3. 调用 `state.task_engine.kick()` 唤醒调度循环
  - 测试: task_bridge_submits_to_queue (单元测试检查双写逻辑)

- [ ] T-002: task_engine 增加 audiolab 任务分派 [Spec: AudioTaskStageHandlerService.md §流程3]
  - 位置: @Rust3/src-tauri/src/task_engine.rs:353 (execute_task_real match 分支)
  - 契约: 在 match task.task_type.as_str() 中增加 "audio_stage" | "audio_auto_tags" 分支
  - 逻辑:
    1. `"audio_stage"`: 从 prompt_text 解析 stage_key → 调用 `tfp_audiolab::stage_runner::run_stage()` → 返回 (content, prompt_tokens, completion_tokens)
    2. `"audio_auto_tags"`: 用固定系统提示词让 AI 从转录文本提取 5-10 个标签 → 解析 JSON/逗号列表 → 批量插入 auto_tags → 返回标签列表
  - 测试: #[test] parse_stage_key_from_prompt

- [ ] T-003: task_engine 增加研究两阶段处理 [Spec: AudioTaskStageHandlerService.md §流程4]
  - 位置: @Rust3/src-tauri/src/task_engine.rs (新分支 "audio_research")
  - 契约: `async fn execute_research(app, db, task) -> Result<(String, u32, u32), ...>`
  - 逻辑:
    1. Phase 1: 用系统提示词让 AI 从转录内容规划 3-5 个研究课题（JSON 输出）
    2. 解析 JSON → 写入 research_topics
    3. Phase 2: 用课题列表 + 转录 + 固定报告模板提示词生成研究报告
    4. 合并两次 token 用量
    5. 更新 research_topic.report_markdown + status
  - 测试: #[test] research_prompt_phase1_format, #[test] research_prompt_phase2_format

- [ ] T-004: task_engine 增加播客 TTS 分派 [Spec: AudioTaskStageHandlerService.md §流程5]
  - 位置: @Rust3/src-tauri/src/task_engine.rs (新分支 "audio_podcast_tts")
  - 契约: 复用已有 "TTS" 逻辑但从 audiolab stage_output 读取 PodcastScript 内容
  - 逻辑:
    1. 从 audiolab_get_stage_outputs(session_id) 找到 stage_key="PodcastScript" && status="Ready"
    2. 如无则返回错误 "请先生成播客台本"
    3. 复用 detect_speakers() + TTS 合成逻辑
    4. 保存输出文件路径到 stage_output (stage_key="PodcastAudio")
  - 测试: #[test] podcast_tts_requires_script (验证无台本时报错)

- [ ] T-005: DAG 依赖检查 + 修正依赖图 [Spec: AudioLab.md §AudioTaskDependencies]
  - 位置: @Rust3/src-tauri/src/task_engine.rs (新函数 check_audiolab_dependencies)
  - 契约: `fn audiolab_stage_dependencies(stage: &str) -> &[&str]`
  - 逻辑:
    1. 定义静态 DAG: Summarized←[Transcribed], MindMap←[Summarized], Insight←[Transcribed], PodcastScript←[Transcribed], PodcastAudio←[PodcastScript], Research←[Transcribed], Translated←[Transcribed]
    2. 在 execute_task_real 的 "audio_stage" 分支开头调用检查
    3. 检查: 对每个前置阶段，确认 audio_lifecycle 中有 Completed 记录 或 audiolab_stage_outputs 中有 Ready 记录
    4. 不满足 → 返回 Err("前置阶段未完成: {stage}")
    5. 修正 get_previous_stage_content() 中旧的错误映射
  - 测试: #[test] dag_dependencies_correct (验证每个阶段的前置依赖)

- [ ] T-006: 任务完成后同步 StudioTask 状态 [Spec: AudioTaskExecutor.md §流程4]
  - 位置: @Rust3/src-tauri/src/task_engine.rs (任务完成/失败处理块)
  - 契约: 在 task 完成/失败时，如果 prompt_text 包含 studio_task_id，则同步更新 studio_tasks 记录
  - 逻辑:
    1. AudioLab 命令在 prompt_text 中附加 `studio_task_id={id}`
    2. task_engine 完成时解析此 ID → 调用 `db.studio_update_task_status(id, "completed"/"failed", error)`
    3. 同时 emit "audiolab-task-update" 事件通知前端刷新
  - 测试: #[test] parse_studio_task_id_from_prompt

- [ ] T-007: stage_runner 支持 endpoint_id + model 参数传递 [Spec: AudioTaskStageHandlerService.md §流程3 步骤3-4]
  - 位置: @Rust3/crates/tfp-audiolab/src/stage_runner.rs:21
  - 契约: `pub async fn run_stage(db, session_id, stage_key, custom_prompt, ai_provider, endpoint_id: &str, model: &str) -> Result<(String, u32, u32), String>`
  - 逻辑:
    1. 在 CompletionRequest 中填入 endpoint_id 和 model（当前为空字符串）
    2. 返回 (content, prompt_tokens, completion_tokens) 元组供 task_engine 使用
    3. 保留 DB 更新逻辑（stage_output 写入）
  - 测试: 修改已有 stage_runner 测试适配新签名

- [ ] T-008: 新增 auto_tags AI 提取提示词 [Spec: AudioLabStagePresetDefaults §GetCustomPrompt]
  - 位置: 新建内容在 @Rust3/crates/tfp-audiolab/src/prompts.rs 追加
  - 契约: `pub fn auto_tags_system_prompt() -> &'static str` + `pub fn auto_tags_user_prompt(transcript: &str) -> String`
  - 逻辑:
    1. System: "你是标签提取专家。从音频转录文本中提取 5-10 个关键标签..."
    2. User: "请从以下转录文本中提取标签，以 JSON 数组格式返回：[\"tag1\", \"tag2\", ...]\n\n{transcript}"
    3. 解析函数: `pub fn parse_auto_tags(ai_response: &str) -> Vec<String>` — 尝试 JSON 解析，fallback 逗号分割
  - 测试: #[test] parse_auto_tags_json, #[test] parse_auto_tags_comma_fallback

## 退出标准
- cargo check -p truefluent-pro-r3 -p tfp-audiolab 0 errors 0 warnings
- cargo test -p truefluent-pro-r3 全绿（含新增测试）
- cargo test -p tfp-audiolab 全绿（含新增测试）
- 新增测试 ≥ 10 个
- `audiolab_start_stage` → task_engine 可执行 → stage_output 更新为 Ready（调用链完整）
- 备注：本 batch 完成后 batch-11（批处理状态机）的 AudioLab 阶段串联成为前置条件
