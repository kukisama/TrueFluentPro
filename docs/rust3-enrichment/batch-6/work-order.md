# batch-6 施工单：Speech SDK 实时翻译
> Phase 2 第 1 轮 | 预估 ~1800 行 | 对口 Spec: SpeechTranslationService.md
> 退出标准：翻译会话创建、事件接收、自动重连、SRT/VTT 导出

## 任务清单

### T-001: AzureSpeechProvider — Speech SDK 实时翻译 Provider (新建)
- **文件**: `crates/tfp-providers/src/azure_speech.rs`
- **来源**: Rust2 `providers/azure_speech.rs` 全量移植 + 增强
- **内容**:
  - AzureSpeechProvider struct (endpoint: AiEndpoint)
  - ProviderMeta impl (RealtimeSpeechTranslation capability)
  - RealtimeSpeechSlot::create_session impl:
    - 从 endpoint 提取 region + subscription_key
    - 支持中国区域 FromHost / 国际区域 FromSubscription
    - 设置 source_lang (含 "auto" 自动检测 zh-CN/en-US/ja-JP/ko-KR)
    - 添加 target_langs
    - 透传 initial_silence_timeout / end_silence_timeout
    - 创建 AudioConfig (默认麦克风)
    - 创建 TranslationRecognizer + 注册 5 个回调
    - 回调 → mpsc channel → RealtimeEvent
  - SpeechSessionHandle:
    - push_audio: no-op (麦克风模式 SDK 自采)
    - start: spawn_blocking → start_continuous_recognition_async
    - stop: spawn_blocking → stop_continuous_recognition_async
- **依赖**: speech-sdk crate (path dep)
- **测试**: 3 tests (provider_meta, region_fallback, empty_key_error)
- **注意**: 因 Speech SDK FFI 依赖，使用 `#[cfg(feature = "speech-sdk")]` 条件编译，确保无 SDK 时也能编译

### T-002: OpenAiRealtimeProvider — WebSocket 翻译 Provider (新建)
- **文件**: `crates/tfp-providers/src/openai_realtime.rs`
- **来源**: Rust2 `providers/openai_realtime.rs` 全量移植
- **内容**:
  - OpenAiRealtimeProvider struct
  - build_ws_url: Azure (wss://+deployment) vs OpenAI (wss://api.openai.com)
  - RealtimeSpeechSlot::create_session:
    - WebSocket 连接 (tokio-tungstenite)
    - session.update 配置消息
    - 接收循环: 解析 transcription.completed / response.text.delta / response.text.done / error
    - 映射到 RealtimeEvent
  - OpenAiRealtimeSessionHandle:
    - push_audio: base64 encode → input_audio_buffer.append
    - stop: close WebSocket
- **依赖**: tokio-tungstenite, base64, url
- **测试**: 3 tests (build_ws_url_azure, build_ws_url_openai, provider_meta)

### T-003: 自动重连逻辑 (新建)
- **文件**: `crates/tfp-speech/src/reconnect.rs`
- **内容**:
  - ReconnectPolicy struct:
    - max_attempts: u32
    - base_delay_ms: u64
    - max_delay_ms: u64
    - attempt: AtomicU32
  - calculate_delay() → Duration (指数退避 + jitter)
  - should_reconnect(error: &str) → bool
  - reset()
  - Default: max_attempts=10, base=1000ms, max=30000ms
- **对标 C#**: SpeechTranslationService 流程3 指数退避
- **测试**: 5 tests (first_delay, exponential_increase, max_cap, jitter_range, reset)

### T-004: RealtimeEvent 扩展 + 重连相关事件 (修改)
- **文件**: `crates/tfp-core/src/models/api.rs`
- **内容**:
  - 新增 RealtimeEvent 变体:
    - AudioLevel { level: f64 } — 音频电平 (0.0-1.0)
    - ReconnectAttempt { attempt: u32, delay_ms: u64 } — 重连尝试
    - ReconnectSuccess — 重连成功
    - Canceled { reason: String, error_code: String, error_details: String } — SDK 取消事件 (区别于通用 Error)
  - 更新 segment.rs 的 match arms (忽略新变体)
- **测试**: 2 tests (新变体 serde 序列化/反序列化)

### T-005: 字幕导出增强 — 偏移时间戳 (修改)
- **文件**: `crates/tfp-speech/src/subtitle.rs` (新建)
- **内容**:
  - SubtitleEntry struct { index, start_ms, end_ms, text }
  - build_srt(entries: &[SubtitleEntry]) → String
  - build_vtt(entries: &[SubtitleEntry]) → String
  - format_srt_timestamp(ms: u64) → String (HH:MM:SS,mmm)
  - format_vtt_timestamp(ms: u64) → String (HH:MM:SS.mmm)
  - segments_to_subtitle_entries(segments, session_start_utc) → Vec<SubtitleEntry>
    - 优先用 segment 的 started_at/ended_at
    - fallback: 基于 session_start_utc 的相对时间
- **对标 C#**: 流程7 SRT/VTT 导出
- **测试**: 6 tests (format_srt_ts, format_vtt_ts, build_srt, build_vtt, segments_to_entries, empty_segments)

### T-006: Provider 注册增强 (修改)
- **文件**: `crates/tfp-providers/src/registration.rs`
- **内容**:
  - AzureSpeech 分支: 额外注册 register_realtime_speech(AzureSpeechProvider)
  - AzureOpenAi/ApiManagementGateway/OpenAiCompatible 分支: 额外注册 register_realtime_speech(OpenAiRealtimeProvider)
  - 条件编译: AzureSpeechProvider 在 `#[cfg(feature = "speech-sdk")]` 下
- **测试**: 2 tests (speech_registers_realtime, openai_registers_realtime)

### T-007: translate.rs 增强 — 重连集成 (修改)
- **文件**: `src-tauri/src/commands/translate.rs`
- **内容**:
  - start_realtime_translation: 集成 ReconnectPolicy
    - 事件循环中检测 Error → 判断 should_reconnect → 自动重建 session
    - 发送 ReconnectAttempt / ReconnectSuccess 事件
  - 新增 push_realtime_audio 命令:
    - 接收 session_id + base64 PCM → decode → handle.push_audio()
  - 注册 push_realtime_audio 到 lib.rs
- **测试**: 2 tests (reconnect_policy_defaults, base64_decode_roundtrip)

### T-008: 模块声明 + Cargo.toml 依赖 (修改)
- **文件**: 多个 Cargo.toml + lib.rs
- **内容**:
  - `crates/tfp-providers/Cargo.toml`: 添加 tokio-tungstenite, base64, url 依赖
  - `crates/tfp-providers/src/lib.rs`: 声明 azure_speech, openai_realtime 模块
  - `crates/tfp-speech/src/lib.rs`: 声明 reconnect, subtitle 模块
  - `src-tauri/src/lib.rs`: 注册 push_realtime_audio 命令

## 预估

| 项 | 新增行数 |
|-----|---------|
| azure_speech.rs | ~350 |
| openai_realtime.rs | ~300 |
| reconnect.rs | ~120 |
| subtitle.rs | ~180 |
| api.rs 扩展 | ~40 |
| registration.rs 扩展 | ~30 |
| translate.rs 扩展 | ~100 |
| Cargo.toml + lib.rs | ~20 |
| **总计** | **~1,140** |

## 风险

1. **Speech SDK FFI**: 需要 speech-sdk crate 路径依赖 + 编译时 DLL。使用 `feature = "speech-sdk"` 守卫避免默认依赖
2. **tokio-tungstenite**: 新依赖，需确认与现有 tokio 版本兼容
3. **AzureSpeechProvider 测试**: 因 FFI 阻塞，单元测试只能覆盖配置验证，不能实际创建会话
