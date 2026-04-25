//! # speech-sdk
//!
//! Rust FFI bindings for Microsoft Cognitive Services Speech SDK,
//! with Translation Recognition support.
//!
//! This crate provides safe Rust wrappers around the Speech SDK C API,
//! focusing on the `TranslationRecognizer` that is missing from the
//! community `cognitive-services-speech-sdk-rs` crate.
//!
//! ## Architecture
//!
//! The FFI layer follows the pattern established by
//! [jabber-tools/cognitive-services-speech-sdk-rs](https://github.com/jabber-tools/cognitive-services-speech-sdk-rs),
//! while the Translation-specific types are ported from the official
//! [Go Speech SDK](https://github.com/microsoft/cognitive-services-speech-sdk-go)
//! PR #144 by Microsoft.
//!
//! ## Usage
//!
//! ```no_run
//! use speech_sdk::speech::{SpeechTranslationConfig, TranslationRecognizer};
//! use speech_sdk::audio::AudioConfig;
//!
//! // Create config
//! let mut config = SpeechTranslationConfig::from_subscription("key", "region").unwrap();
//! config.set_speech_recognition_language("zh-CN").unwrap();
//! config.add_target_language("en").unwrap();
//! config.add_target_language("ja").unwrap();
//!
//! // Create audio input from microphone
//! let audio = AudioConfig::from_default_microphone_input().unwrap();
//!
//! // Create recognizer and register callbacks
//! let mut recognizer = TranslationRecognizer::from_config(&config, &audio).unwrap();
//!
//! recognizer.set_recognized_cb(|event| {
//!     println!("Source: {}", event.result.base.text);
//!     for (lang, text) in event.result.get_translations() {
//!         println!("  → {}: {}", lang, text);
//!     }
//! }).unwrap();
//! ```

pub mod ffi;
pub mod error;
pub mod common;
pub mod audio;
pub mod speech;
