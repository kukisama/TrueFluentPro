//! 关键词识别（KeywordRecognizer）与唤醒词模型（KeywordRecognitionModel）。
//!
//! - `KeywordRecognitionModel::from_file`：从 `.table` 模型文件加载唤醒词。
//! - `KeywordRecognizer`：在音频流中持续侦听唤醒词，命中后返回结果。
//!
//! 注意：需要本地唤醒词模型文件与（通常）麦克风输入，属手动测试场景。

use crate::audio::AudioConfig;
use crate::error::{convert_err, Result};
use crate::ffi::{
    keyword_recognition_model_create_from_file, keyword_recognition_model_handle_release,
    recognizer_async_handle_release, recognizer_create_keyword_recognizer_from_audio_config,
    recognizer_handle_release, recognizer_recognize_keyword_once_async,
    recognizer_recognize_keyword_once_async_wait_for, recognizer_stop_keyword_recognition,
    SmartHandle, SPXASYNCHANDLE, SPXKEYWORDHANDLE, SPXRECOHANDLE, SPXRESULTHANDLE,
};
use crate::speech::SpeechRecognitionResult;
use std::ffi::CString;
use std::mem::MaybeUninit;

/// 唤醒词模型。
#[derive(Debug)]
pub struct KeywordRecognitionModel {
    pub(crate) handle: SmartHandle<SPXKEYWORDHANDLE>,
}

impl KeywordRecognitionModel {
    /// 从 `.table` 文件加载唤醒词模型。
    pub fn from_file(file_path: &str) -> Result<KeywordRecognitionModel> {
        let c_path = CString::new(file_path)?;
        unsafe {
            let mut handle: MaybeUninit<SPXKEYWORDHANDLE> = MaybeUninit::uninit();
            let ret =
                keyword_recognition_model_create_from_file(c_path.as_ptr(), handle.as_mut_ptr());
            convert_err(ret, "KeywordRecognitionModel::from_file error")?;
            Ok(KeywordRecognitionModel {
                handle: SmartHandle::create(
                    "KeywordRecognitionModel",
                    handle.assume_init(),
                    keyword_recognition_model_handle_release,
                ),
            })
        }
    }
}

/// 关键词识别器。
#[derive(Debug)]
pub struct KeywordRecognizer {
    handle: SmartHandle<SPXRECOHANDLE>,
}

impl KeywordRecognizer {
    /// 以音频输入创建关键词识别器。
    pub fn from_audio_config(audio_config: &AudioConfig) -> Result<KeywordRecognizer> {
        unsafe {
            let mut handle: MaybeUninit<SPXRECOHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_create_keyword_recognizer_from_audio_config(
                handle.as_mut_ptr(),
                audio_config.handle.inner(),
            );
            convert_err(ret, "KeywordRecognizer::from_audio_config error")?;
            Ok(KeywordRecognizer {
                handle: SmartHandle::create(
                    "KeywordRecognizer",
                    handle.assume_init(),
                    recognizer_handle_release,
                ),
            })
        }
    }

    /// 侦听唤醒词一次，命中后返回结果（阻塞直到命中或流结束）。
    pub async fn recognize_once_async(
        &self,
        model: &KeywordRecognitionModel,
    ) -> Result<SpeechRecognitionResult> {
        unsafe {
            let mut h_async: MaybeUninit<SPXASYNCHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_recognize_keyword_once_async(
                self.handle.inner(),
                model.handle.inner(),
                h_async.as_mut_ptr(),
            );
            convert_err(ret, "KeywordRecognizer::recognize_once_async start error")?;
            let h_async = h_async.assume_init();

            let mut h_result: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_recognize_keyword_once_async_wait_for(
                h_async,
                u32::MAX,
                h_result.as_mut_ptr(),
            );
            recognizer_async_handle_release(h_async);
            convert_err(ret, "KeywordRecognizer::recognize_once_async wait error")?;
            SpeechRecognitionResult::from_handle(h_result.assume_init())
        }
    }

    /// 停止关键词识别。
    pub fn stop(&self) -> Result<()> {
        unsafe {
            let ret = recognizer_stop_keyword_recognition(self.handle.inner());
            convert_err(ret, "KeywordRecognizer::stop error")
        }
    }
}
