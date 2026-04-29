# batch-7 施工单：Realtime WebSocket + STT
> Phase 2 第 2 轮 | 预估 ~1900 行 | 对口 Spec: AiAudioTranscriptionService.md, FastTranscriptionParser.md, BatchTranscriptionParser.md
> 退出标准：WebSocket 翻译 + 音频转录

## 任务清单

### T-001: OpenAI Whisper STT Provider (新建)
- **文件**: `crates/tfp-providers/src/openai_stt.rs`
- **来源**: AiAudioTranscriptionService.md
- **内容**:
  - OpenAiSttProvider struct
  - 支持 Whisper/GPT-4o-transcribe
  - 多 URL 候选 (audio_url_candidates)
  - multipart/form-data 上传
  - response_format: json vs verbose_json
  - 解析 segments/words/text
- **测试**: 4 tests

### T-002: FastTranscriptionParser (新建)
- **文件**: `crates/tfp-speech/src/fast_transcription.rs`
- **来源**: FastTranscriptionParser.md
- **内容**:
  - parse_fast_transcription(json) → Vec<SubtitleCue>
  - 跨 phrase 合并 (merge_adjacent_cues)
  - 词级断句 (split_phrase_to_cues)
- **测试**: 5 tests

### T-003: BatchTranscriptionParser (新建)
- **文件**: `crates/tfp-speech/src/batch_transcription.rs`
- **来源**: BatchTranscriptionParser.md
- **内容**:
  - parse_batch_transcription(json) → Vec<SubtitleCue>
  - 词级断句 + 标点切分
  - 多格式时间解析 (ticks, ISO duration, ms)
- **测试**: 5 tests

### T-004: SubtitleCue 模型 + 切分选项 (新建/修改)
- **文件**: `crates/tfp-core/src/models/api.rs` + `crates/tfp-core/src/models/settings.rs`
- **内容**:
  - SubtitleCue { index, start_ms, end_ms, text, speaker }
  - BatchSubtitleSplitOptions { enable_sentence_split, split_on_comma, max_chars, max_duration_seconds, pause_split_ms }
- **测试**: 2 tests

### T-005: Provider 注册 (修改)
- **文件**: `crates/tfp-providers/src/registration.rs`
- **内容**:
  - OpenAI 类端点: 额外注册 register_stt(OpenAiSttProvider)
- **测试**: 1 test

### T-006: STT Tauri 命令增强 (修改)
- **文件**: `src-tauri/src/commands/audio.rs`
- **内容**:
  - transcribe_audio 命令: 使用 SpeechToTextSlot provider
  - 支持 endpoint_id 选择特定 provider
- **测试**: 1 test

### T-007: 模块声明 + lib.rs 更新
- **文件**: 多个 lib.rs
