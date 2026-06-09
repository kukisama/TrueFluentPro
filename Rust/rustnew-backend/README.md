# rustnew-backend — Speech SDK FFI (Translation)

> Azure Cognitive Services Speech SDK 的 Rust FFI 绑定，重点补齐 **TranslationRecognizer** 功能。

## 背景

社区的 [cognitive-services-speech-sdk-rs](https://github.com/jabber-tools/cognitive-services-speech-sdk-rs) (v1.3.0) 只实现了 STT + TTS，没有 Translation。
微软官方的 [Go SDK](https://github.com/microsoft/cognitive-services-speech-sdk-go) 在 [PR #144](https://github.com/microsoft/cognitive-services-speech-sdk-go/pull/144) 中补齐了 TranslationRecognizer。

本项目照搬 Go SDK 的处理逻辑，结合 Rust SDK 的 FFI 模式（SmartHandle + CallbackBag），实现完整的 Translation FFI 绑定。

## 架构

```
speech-sdk crate
├── ffi/          — bindgen 生成的 C API 绑定 + SmartHandle RAII
├── error.rs      — 错误类型 + convert_err
├── common/       — PropertyCollection, ResultReason, PropertyId 等枚举
├── audio/        — AudioConfig (麦克风 / WAV 文件 / PushStream)
└── speech/
    ├── speech_config.rs                — SpeechConfig (基础配置)
    ├── speech_translation_config.rs    — SpeechTranslationConfig ← Go SDK 移植
    ├── speech_recognition_result.rs    — SpeechRecognitionResult (基础结果)
    ├── translation_recognition_result.rs — TranslationRecognitionResult ← Go SDK 移植
    ├── translation_recognizer.rs       — TranslationRecognizer ← Go SDK 移植
    ├── session_event.rs                — SessionEvent
    └── recognition_event.rs            — RecognitionEvent
```

## Go SDK → Rust 的映射关系

| Go SDK 文件 | 本项目 Rust 文件 | 说明 |
|---|---|---|
| `speech_translation_config.go` | `speech/speech_translation_config.rs` | 翻译配置（目标语言、语音输出） |
| `translation_recognizer.go` | `speech/translation_recognizer.rs` | 翻译识别器（创建、回调、连续识别） |
| `translation_recognition_result.go` | `speech/translation_recognition_result.rs` | 翻译结果 + 合成结果 + 事件类型 |
| `translation_callback_helpers.go` | 内联于 `translation_recognizer.rs` | Rust 用 CallbackBag 模式替代 Go 的全局 map |
| `cfunctions.go` (translation 部分) | `ffi/bindings.rs` (extern "C" 声明) | C 回调代理函数 |

## 关键 C API 函数

Translation 特有的 C API（来自 `speechapi_c_speech_translation_config.h` + `speechapi_c_translation_result.h`）：

```c
// 翻译配置
speech_translation_config_from_subscription()
speech_translation_config_from_authorization_token()
speech_translation_config_from_endpoint()
speech_translation_config_from_host()
speech_translation_config_add_target_language()
speech_translation_config_remove_target_language()
speech_translation_config_set_custom_model_category_id()

// 翻译识别器创建
recognizer_create_translation_recognizer_from_config()
recognizer_create_translation_recognizer_from_auto_detect_source_lang_config()

// 翻译结果
translation_text_result_get_translation_count()
translation_text_result_get_translation()
translation_synthesis_result_get_audio_data()

// 翻译合成回调
translator_synthesizing_audio_set_callback()
```

## 回调机制对比

| 维度 | Go SDK | Rust (本项目) |
|------|--------|-------------|
| 回调存储 | `sync.Mutex` 保护的全局 `map[SPXHANDLE]handler` | `Box<CallbackBag>` 堆分配，通过 `*mut c_void` 传入 C |
| 线程安全 | 由 Mutex 保证 | 由 `Box` 内存布局 + `Send` trait bound 保证 |
| 生命周期 | 手动 `Close()` 清理 map | `Drop` trait 自动 unregister + 释放 |
| CGo 桥接 | 需要 C 代理函数 + `//export` | 直接 `unsafe extern "C" fn` |

## 构建

```bash
cd rustnew-backend

# 完整构建（会下载 ~100MB Speech SDK NuGet 包 + 运行 bindgen）
cargo build

# 跳过 bindgen（使用预置的 stub bindings 进行开发）
MS_COG_SVC_SPEECH_SKIP_BINDGEN=1 cargo check
```

### 前提条件

- Rust stable toolchain
- Windows: 需要在 PATH 中有 `curl.exe`（Windows 10+ 自带）
- Linux: 需要 `curl` + `tar`
- macOS: 需要 `curl` + `unzip`
- bindgen 需要 `libclang`（[安装指南](https://rust-lang.github.io/rust-bindgen/requirements.html)）

## 使用示例

```rust
use speech_sdk::audio::AudioConfig;
use speech_sdk::speech::{SpeechTranslationConfig, TranslationRecognizer};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    // 1. 创建翻译配置
    let mut config = SpeechTranslationConfig::from_subscription(
        "your-subscription-key",
        "eastasia",
    )?;
    config.set_speech_recognition_language("zh-CN")?;
    config.add_target_language("en")?;
    config.add_target_language("ja")?;

    // 2. 创建音频输入
    let audio = AudioConfig::from_default_microphone_input()?;

    // 3. 创建翻译识别器
    let mut recognizer = TranslationRecognizer::from_config(&config, &audio)?;

    // 4. 注册回调
    recognizer.set_recognizing_cb(|event| {
        println!("[识别中] {}", event.result.base.text);
        for (lang, text) in event.result.get_translations() {
            println!("  → {}: {}", lang, text);
        }
    })?;

    recognizer.set_recognized_cb(|event| {
        println!("[已识别] {}", event.result.base.text);
        for (lang, text) in event.result.get_translations() {
            println!("  ✓ {}: {}", lang, text);
        }
    })?;

    recognizer.set_canceled_cb(|event| {
        eprintln!("[取消] {:?}: {}", event.reason, event.error_details);
    })?;

    // 5. 开始连续识别
    recognizer.start_continuous_recognition_async().await?;

    println!("按 Enter 停止...");
    let mut input = String::new();
    std::io::stdin().read_line(&mut input)?;

    // 6. 停止识别
    recognizer.stop_continuous_recognition_async().await?;

    Ok(())
}
```

## 与主项目的关系

本 crate 是 **迁移计划 P2 阶段**（Speech SDK FFI 补齐）的产出物。
后续会整合进 Tauri 2 应用的 Rust 核心层，作为实时语音翻译的基础。

参见：[平台转换合计-计划.md](../docs/平台转换合计-计划.md)

---

## Go → Rust 移植审查报告

### 已修复的问题

| 编号 | 严重程度 | 文件 | 问题描述 | 修复方式 |
|------|----------|------|----------|----------|
| **B1** | 🔴 高 | `translation_recognizer.rs` | `start/stop_continuous_recognition_async` 对同一 `MaybeUninit` 调用两次 `assume_init()`。虽然 `usize: Copy` 所以不是 UB，但与 Go SDK 模式不一致：Go 在 wait_for 后立即释放 async handle，我们却把它存进 struct 直到 Drop（资源泄漏） | 改为局部变量 + wait_for 后立即 `recognizer_async_handle_release()`，移除 struct 中不必要的 `handle_async_*` 字段 |
| **B2** | 🔴 高 | `translation_recognition_result.rs` | `TranslationSynthesisEventArgs::from_handle` 没有调用 `recognizer_event_handle_release(handle)` — 每次合成事件回调都会泄漏一个 event handle | 修复：成功/失败路径都确保释放 event handle |
| **B3** | 🟡 中 | `translation_recognition_result.rs` | `TranslationSynthesisResult::from_handle` 对首次 `translation_synthesis_result_get_audio_data` 调用结果静默吞掉错误（返回空 Vec），Go SDK 会在条件块后检查 `if ret != SPX_NOERROR` | 修复：增加了三路分支（BUFFER_TOO_SMALL / SPX_NOERROR / 其他错误） |
| **B4** | 🟡 中 | 所有 `*EventArgs::from_handle` + `SessionEvent` + `RecognitionEvent` | 如果 `from_handle` 中间操作失败（`convert_err` 返回 Err），event handle 永远不会被释放（泄漏） | 修复：每个提前返回路径都先调用 `recognizer_event_handle_release(handle)` |

### 已验证正确的部分

1. **C API 函数签名** — `translation_text_result_get_translation(result, index, langBuf, textBuf, &langSize, &textSize)` 的 stub 签名与 Go SDK 的 CGo 调用一致。最后两个参数是 `*mut usize`（指针），不是值类型。两遍调用模式（查大小→填数据）正确。

2. **回调机制** — Go 使用 `sync.Mutex` 保护的全局 `map[SPXHANDLE]handler`，Rust 使用 `Box<TranslationCallbackBag>` 通过 `*mut c_void` 传入 C。这是比 Go 更好的设计（无全局状态），且与现有 jabber-tools SDK 一致。

3. **SpeechTranslationConfig 构造函数** — 全部 8 个构造函数与 Go SDK 一一对应，参数传递顺序正确：
   - `from_subscription` / `from_auth_token` / `from_endpoint_with_subscription` / `from_endpoint` / `from_host_with_subscription` / `from_host`

4. **翻译结果解析** — `TranslationRecognitionResult::from_handle` 的两遍调用模式（获取 lang/text 大小 → 分配缓冲区 → 填充数据）与 Go SDK 逻辑一致。`saturating_sub(1)` 处理 null terminator 也正确。

5. **Drop 实现** — 8 个回调全部注销（传 None），SmartHandle 自动释放 recognizer handle。对应 Go SDK 的 `Close()` 方法。

6. **ResultReason 枚举值** — TranslatingSpeech=6, TranslatedSpeech=7 等值与 C SDK 头文件的实际值一致（通过 Go SDK 交叉验证）。

7. **CancellationErrorCode** — 9 个错误码与 Go SDK `common/cancellation_error_code.go` 一致。

### 已知限制（Not Ported Yet）

| 编号 | 说明 | Go SDK 对应 |
|------|------|-------------|
| **L1** | 缺少 `from_auto_detect_source_lang_config` 构造函数 | `NewTranslationRecognizerFromAutoDetectSourceLangConfig()` |
| **L2** | `SpeechRecognitionResult` 使用固定 1024 字节缓冲区读取 Text/ResultID，超长文本会被截断 | Go 使用 CGo string（无长度限制） |
| **L3** | `PropertyCollection.get_property` 同样使用 1024 字节缓冲区 | Go 无此限制 |
| **L4** | `TranslationRecognitionEventArgs` 不包含 offset/session 基础事件数据（Go 版本嵌入了 `RecognitionEventArgs`），但 offset 可通过 `event.result.base.offset` 获取 | `TranslationRecognitionEventArgs.RecognitionEventArgs` |
| **L5** | `recognize_once_async` 标记为 `async fn` 但实际在当前线程同步调用 C API（`recognizer_recognize_once` 是阻塞调用），应改用 `tokio::task::spawn_blocking` 或类似机制 | Go 使用 goroutine + channel 实现真正的异步 |
| **L6** | FFI 函数全部为 stub 声明，实际编译链接需要下载 Speech SDK NuGet 包（build.rs 负责） | — |

### 与 Go SDK 的设计差异（有意为之）

| 维度 | Go SDK | Rust 本项目 | 说明 |
|------|--------|-------------|------|
| Event handle 生命周期 | 存储在 EventArgs struct，通过 `Close()` 释放 | `from_handle()` 中提取数据后立即释放 | Rust 模式更安全，匹配 jabber-tools SDK |
| Callback 上下文 | 传 `nil`（用 recognizer handle 查全局 map） | 传 `&CallbackBag` 指针 | 无全局状态，更符合 Rust 习惯 |
| Async handle 生命周期 | start→wait→release（即用即丢） | 同上（已修复） | 最初是存 struct 直到 Drop（已修复为与 Go 一致） |
| Config 继承 | `SpeechTranslationConfig` 嵌入 `SpeechConfig` | 独立 struct，手动复制方法 | Rust 无继承，采用组合/重复 |

## 文件统计

| 文件 | 行数 | 说明 |
|------|------|------|
| `speech_translation_config.rs` | ~230 | 翻译配置 → 8 个构造函数 + 语言管理 |
| `translation_recognizer.rs` | ~350 | 翻译识别器 → 回调 + 连续识别 + Drop |
| `translation_recognition_result.rs` | ~260 | 翻译结果 + 5 种事件类型 |
| `speech_recognition_result.rs` | ~90 | 基础识别结果 |
| `speech_config.rs` | ~120 | 基础配置 |
| `ffi/bindings.rs` | ~280 | FFI 声明（含 Translation 特有函数） |
| 其余基础设施 | ~200 | error, common, audio, events |
| **合计** | **~1530** | |

与计划文档中估计的 "800-1200 行 Rust FFI" 基本吻合（考虑到增加了完整的基础设施层）。

## License

MIT
