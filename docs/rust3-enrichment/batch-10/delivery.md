# 批次 10 交付报告
> 提交日期：2026-04-29

## ⚠️ 施工单异议
| 要求 | 问题 | 修正 | Spec 依据 |
|------|------|------|-----------|
| T-002~T-006 位置 `@Rust3/src-tauri/src/task_engine.rs:353` | 该文件是**死代码**（`lib.rs` 无 `mod task_engine`，实际使用 `tfp_engine::TaskEngine`） | 所有引擎改动实现在 `crates/tfp-engine/src/task_engine.rs` | `src-tauri/src/state.rs:6` import + `src-tauri/src/lib.rs:121` 调用 |
| T-005: "Research←[Insight]" | 与 Spec DAG 矛盾。Research 应直接依赖 Transcribed（Insight 本身依赖 Transcribed，强制 Insight→Research 会阻塞单独研究） | Research←[Transcribed]（与 Summarized/PodcastScript 同级） | AudioLab.md §AudioTaskDependencies 实际图 |
| T-005: "MindMap←[Summarized], PodcastScript←[Summarized]" | 不一致。PodcastScript 和 Insight 按 Spec DAG 均直接来自 Transcribed | MindMap←[Summarized]，其余←[Transcribed] | AudioLab.md §AudioTaskDependencies |

## 任务完成
- [x] T-001: AudioLab 命令桥接 → `services.rs:submit_task()` 双写 + `audiolab.rs` kick engine [Spec §流程3 ✅]
- [x] T-002: task_engine audio_stage + audio_auto_tags 分派 → `task_engine.rs:L680-L700` [Spec §流程3 ✅]
- [x] T-003: 研究两阶段处理 → `task_engine.rs:execute_research()` [Spec §流程4 ✅]
- [x] T-004: 播客 TTS 分派 → `task_engine.rs:L754-L832` [Spec §流程5 ✅]
- [x] T-005: DAG 依赖检查 + get_previous_stage_content 修正 → `audiolab_stage_dependencies()` [Spec §AudioTaskDependencies ✅]
- [x] T-006: 任务完成后同步 StudioTask → `sync_studio_task_status()` [Spec §流程4 ✅]
- [x] T-007: stage_runner endpoint_id + model 参数 → 签名扩展 + CompletionRequest 填充 [Spec §流程3 ✅]
- [x] T-008: auto_tags 提示词 + 解析 → `prompts.rs` 新增 6 函数 [Spec §GetCustomPrompt ✅]

## 编译
```
cargo check -p tfp-audiolab -p tfp-engine -p truefluent-pro-r3: 0 errors, 0 new warnings
```

## 测试
```
cargo test --workspace: 445 passed, 0 failed
新增 17 个测试:
  tfp-audiolab (10): parse_auto_tags_json, parse_auto_tags_json_embedded, parse_auto_tags_comma_fallback,
                     parse_auto_tags_empty, auto_tags_system_prompt_not_empty, auto_tags_user_prompt_contains_transcript,
                     parse_research_topics_json, parse_research_topics_empty,
                     research_prompt_phase1_format, research_prompt_phase2_format
  tfp-engine (7): dag_dependencies_correct, dag_unknown_stage_defaults_to_transcribed,
                  parse_stage_key_from_prompt, parse_stage_key_no_prefix,
                  parse_studio_task_id_from_prompt, parse_studio_task_id_none,
                  podcast_tts_requires_script_stage_key_check
```

## 文件清单
| 文件 | 操作 | 行数变化 |
|------|------|----------|
| `crates/tfp-audiolab/src/prompts.rs` | 修改 | +150 (auto_tags + research prompts + tests) |
| `crates/tfp-audiolab/src/stage_runner.rs` | 修改 | +8 (endpoint_id, model params, token return) |
| `crates/tfp-audiolab/src/services.rs` | 修改 | +40 (双写 audio_task_queue + extract_stage) |
| `crates/tfp-engine/Cargo.toml` | 修改 | +1 (tfp-audiolab dep) |
| `crates/tfp-engine/src/task_engine.rs` | 修改 | +480 (4 dispatch branches + DAG + helpers + tests) |
| `crates/tfp-storage/src/audiolab_repo/mod.rs` | 修改 | +32 (update_research_topic + get_research_topic) |
| `src-tauri/src/commands/audiolab.rs` | 修改 | +16 (kick engine 4处) |

## 已知局限
1. `src-tauri/src/task_engine.rs` 为死代码（未被编译），其中 DAG 映射仍是旧版。建议架构师后续清理。
2. `audio_research` 任务使用 `topic_id` 作为 `audio_item_id`/`session_id`，与实际 session_id 有歧义（保留自 batch-9 命令签名设计）。
3. Research 两阶段执行是串行的（phase1 → N 个 phase2），大量 topics 时可能超时。生产环境建议拆分为独立子任务。
