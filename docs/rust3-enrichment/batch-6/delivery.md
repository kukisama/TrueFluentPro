# batch-6 交付报告
> 日期：2026-04-29 | Phase 2 第 1 轮

## 主题：Speech SDK 实时翻译

### 施工单 8 项任务完成情况

| 任务 | 描述 | 文件 | 状态 |
|------|------|------|------|
| T-001 | AzureSpeechProvider 实时翻译 | `crates/tfp-providers/src/azure_speech.rs` (新建) | ✅ |
| T-002 | OpenAiRealtimeProvider WebSocket | `crates/tfp-providers/src/openai_realtime.rs` (新建) | ✅ |
| T-003 | 自动重连逻辑 | `crates/tfp-speech/src/reconnect.rs` (新建) | ✅ |
| T-004 | RealtimeEvent 扩展 | `crates/tfp-core/src/models/api.rs` (修改) | ✅ |
| T-005 | 字幕导出增强 | `crates/tfp-speech/src/subtitle.rs` (新建) | ✅ |
| T-006 | Provider 注册增强 | `crates/tfp-providers/src/registration.rs` (修改) | ✅ |
| T-007 | push_realtime_audio 命令 | `src-tauri/src/commands/translate.rs` (修改) | ✅ |
| T-008 | 模块声明 + 依赖更新 | 多文件 Cargo.toml + lib.rs | ✅ |

### 新增文件 (4)

| 文件 | 行数 | 说明 |
|------|------|------|
| `crates/tfp-providers/src/azure_speech.rs` | ~200 | Speech SDK 实时翻译 provider (stub mode, 7 tests) |
| `crates/tfp-providers/src/openai_realtime.rs` | ~280 | OpenAI Realtime WS provider (3 tests) |
| `crates/tfp-speech/src/reconnect.rs` | ~120 | 指数退避重连策略 (5 tests) |
| `crates/tfp-speech/src/subtitle.rs` | ~180 | SRT/VTT 毫秒精度字幕格式化 (6 tests) |

### 修改文件 (7)

| 文件 | 变更 |
|------|------|
| `crates/tfp-core/src/models/api.rs` | +4 RealtimeEvent 变体 (AudioLevel, ReconnectAttempt, ReconnectSuccess, Canceled) |
| `crates/tfp-core/src/models/mod.rs` | 测试更新: 新变体序列化验证 |
| `crates/tfp-providers/src/registration.rs` | AzureSpeech→register_realtime_speech, OpenAI→register_realtime_speech |
| `crates/tfp-providers/src/lib.rs` | +2 模块声明 + pub use |
| `crates/tfp-providers/src/test_helpers.rs` | +2 factory 别名 |
| `crates/tfp-providers/Cargo.toml` | +4 依赖 (tokio-tungstenite, base64, url, uuid) |
| `crates/tfp-speech/src/lib.rs` | +2 模块声明 (reconnect, subtitle) |
| `src-tauri/src/commands/translate.rs` | +push_realtime_audio 命令 |
| `src-tauri/src/lib.rs` | 注册 push_realtime_audio |

### 关键设计决策

1. **AzureSpeechProvider stub**: 无需 Speech SDK 即可编译。stub 模式返回 SessionStarted 事件，实际 FFI 集成由 platform-specific 构建激活
2. **OpenAI Realtime 双模式**: Azure (api-key) vs OpenAI (Bearer), session.update 配置 server_vad
3. **重连策略**: 指数退避 (base 1s, max 30s) + jitter (±25%), max_attempts=10
4. **字幕时间戳**: 毫秒精度 (SRT: HH:MM:SS,mmm / VTT: HH:MM:SS.mmm), 支持 session-relative fallback
5. **push_realtime_audio**: base64 编码 PCM 从前端推送到会话 handle

### 测试统计

- **新增测试**: 21 (azure_speech 7 + openai_realtime 3 + reconnect 5 + subtitle 6)
- **全域测试**: 398 (batch-5 为 377)
- **全部通过**: ✅

### 代码量

| 区域 | batch-5 | batch-6 | 增量 |
|------|---------|---------|------|
| Rust crates | 17,271 | 18,233 | +962 |
| src-tauri | 7,110 | 7,133 | +23 |
| 前端 TS/TSX | 9,195 | 9,195 | ±0 |
| **总计** | **33,576** | **34,561** | **+985** |
