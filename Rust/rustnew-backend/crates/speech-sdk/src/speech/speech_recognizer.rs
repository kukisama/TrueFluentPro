//! SpeechRecognizer — 纯语音转文字（不翻译）。
//!
//! 支持单次识别（`recognize_once_async`，适合 WAV 文件，可自动化测试）
//! 与连续识别（`start/stop_continuous_recognition_async` + 回调）。

use crate::audio::AudioConfig;
use crate::common::{CancellationErrorCode, CancellationReason, PropertyCollection, PropertyId};
use crate::error::{convert_err, Result};
use crate::ffi::{
    recognizer_async_handle_release,
    recognizer_canceled_set_callback,
    recognizer_create_speech_recognizer_from_config,
    recognizer_create_speech_recognizer_from_auto_detect_source_lang_config,
    recognizer_create_speech_recognizer_from_source_lang_config,
    recognizer_event_handle_release,
    recognizer_get_property_bag,
    recognizer_handle_release,
    recognizer_recognition_event_get_result,
    recognizer_recognize_once,
    recognizer_recognized_set_callback,
    recognizer_recognizing_set_callback,
    recognizer_session_started_set_callback,
    recognizer_session_stopped_set_callback,
    recognizer_start_continuous_recognition_async,
    recognizer_start_continuous_recognition_async_wait_for,
    recognizer_stop_continuous_recognition_async,
    recognizer_stop_continuous_recognition_async_wait_for,
    result_get_canceled_error_code, result_get_reason_canceled,
    SmartHandle, SPXEVENTHANDLE, SPXPROPERTYBAGHANDLE, SPXRECOHANDLE, SPXRESULTHANDLE,
};
use crate::speech::{SpeechConfig, SpeechRecognitionResult};
use crate::speech::{AutoDetectSourceLanguageConfig, SourceLanguageConfig};
use log::*;
use std::fmt;
use std::mem::MaybeUninit;
use std::os::raw::c_void;

/// 识别事件参数（recognizing / recognized）。
#[derive(Debug)]
pub struct SpeechRecognitionEventArgs {
    pub result: SpeechRecognitionResult,
}

impl SpeechRecognitionEventArgs {
    /// # Safety
    /// `handle` 必须是有效的识别事件句柄。
    pub(crate) unsafe fn from_handle(handle: SPXEVENTHANDLE) -> Result<SpeechRecognitionEventArgs> {
        let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
        let ret = recognizer_recognition_event_get_result(handle, result_handle.as_mut_ptr());
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "SpeechRecognitionEventArgs: get_result error")?;
        }
        let result = match SpeechRecognitionResult::from_handle(result_handle.assume_init()) {
            Ok(r) => r,
            Err(e) => {
                recognizer_event_handle_release(handle);
                return Err(e);
            }
        };
        recognizer_event_handle_release(handle);
        Ok(SpeechRecognitionEventArgs { result })
    }
}

/// 取消事件参数。
#[derive(Debug)]
pub struct SpeechRecognitionCanceledEventArgs {
    pub result: SpeechRecognitionResult,
    pub reason: CancellationReason,
    pub error_code: CancellationErrorCode,
    pub error_details: String,
}

impl SpeechRecognitionCanceledEventArgs {
    /// # Safety
    /// `handle` 必须是有效的识别事件句柄。
    pub(crate) unsafe fn from_handle(
        handle: SPXEVENTHANDLE,
    ) -> Result<SpeechRecognitionCanceledEventArgs> {
        let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
        let ret = recognizer_recognition_event_get_result(handle, result_handle.as_mut_ptr());
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "SpeechRecognitionCanceledEventArgs: get_result error")?;
        }
        let result_handle = result_handle.assume_init();

        let mut creason: i32 = 0;
        let _ = result_get_reason_canceled(result_handle, &mut creason);
        let reason = CancellationReason::from(creason);

        let mut ecode: i32 = 0;
        let _ = result_get_canceled_error_code(result_handle, &mut ecode);
        let error_code = CancellationErrorCode::from(ecode);

        let result = match SpeechRecognitionResult::from_handle(result_handle) {
            Ok(r) => r,
            Err(e) => {
                recognizer_event_handle_release(handle);
                return Err(e);
            }
        };

        let error_details = result
            .properties
            .get_property(PropertyId::SpeechServiceResponseJsonErrorDetails, "")
            .unwrap_or_default();

        recognizer_event_handle_release(handle);

        Ok(SpeechRecognitionCanceledEventArgs {
            result,
            reason,
            error_code,
            error_details,
        })
    }
}

struct CallbackBag {
    recognizing_cb: Option<Box<dyn Fn(SpeechRecognitionEventArgs) + Send>>,
    recognized_cb: Option<Box<dyn Fn(SpeechRecognitionEventArgs) + Send>>,
    canceled_cb: Option<Box<dyn Fn(SpeechRecognitionCanceledEventArgs) + Send>>,
    session_started_cb: Option<Box<dyn Fn() + Send>>,
    session_stopped_cb: Option<Box<dyn Fn() + Send>>,
}

/// 纯语音识别器（STT）。
pub struct SpeechRecognizer {
    handle: SmartHandle<SPXRECOHANDLE>,
    #[allow(dead_code)]
    properties: PropertyCollection,
    callback_bag: Box<CallbackBag>,
}

impl fmt::Debug for SpeechRecognizer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("SpeechRecognizer").field("handle", &self.handle).finish()
    }
}

impl SpeechRecognizer {
    /// 内部：暴露原生识别器句柄，供短语列表语法等同模块使用。
    pub(crate) fn handle_inner(&self) -> SPXRECOHANDLE {
        self.handle.inner()
    }

    /// 以配置与音频输入创建识别器。
    pub fn from_config(
        config: &SpeechConfig,
        audio_config: &AudioConfig,
    ) -> Result<SpeechRecognizer> {
        unsafe {
            let mut handle: MaybeUninit<SPXRECOHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_create_speech_recognizer_from_config(
                handle.as_mut_ptr(),
                config.handle.inner(),
                audio_config.handle.inner(),
            );
            convert_err(ret, "SpeechRecognizer::from_config error")?;
            let handle = handle.assume_init();

            let mut prop_bag_handle: MaybeUninit<SPXPROPERTYBAGHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_get_property_bag(handle, prop_bag_handle.as_mut_ptr());
            convert_err(ret, "SpeechRecognizer::from_config get_property_bag error")?;
            let properties = PropertyCollection::from_handle(prop_bag_handle.assume_init());

            Ok(SpeechRecognizer {
                handle: SmartHandle::create("SpeechRecognizer", handle, recognizer_handle_release),
                properties,
                callback_bag: Box::new(CallbackBag {
                    recognizing_cb: None,
                    recognized_cb: None,
                    canceled_cb: None,
                    session_started_cb: None,
                    session_stopped_cb: None,
                }),
            })
        }
    }

    /// 以源语言配置（显式单一语言/自定义端点）创建识别器。
    pub fn from_source_language_config(
        config: &SpeechConfig,
        source_language_config: &SourceLanguageConfig,
        audio_config: &AudioConfig,
    ) -> Result<SpeechRecognizer> {
        unsafe {
            let mut handle: MaybeUninit<SPXRECOHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_create_speech_recognizer_from_source_lang_config(
                handle.as_mut_ptr(),
                config.handle.inner(),
                source_language_config.handle.inner(),
                audio_config.handle.inner(),
            );
            convert_err(ret, "SpeechRecognizer::from_source_language_config error")?;
            Self::finish_construct(handle.assume_init())
        }
    }

    /// 以自动语言检测配置创建识别器。
    pub fn from_auto_detect_source_language_config(
        config: &SpeechConfig,
        auto_detect_config: &AutoDetectSourceLanguageConfig,
        audio_config: &AudioConfig,
    ) -> Result<SpeechRecognizer> {
        unsafe {
            let mut handle: MaybeUninit<SPXRECOHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_create_speech_recognizer_from_auto_detect_source_lang_config(
                handle.as_mut_ptr(),
                config.handle.inner(),
                auto_detect_config.handle.inner(),
                audio_config.handle.inner(),
            );
            convert_err(ret, "SpeechRecognizer::from_auto_detect_source_language_config error")?;
            Self::finish_construct(handle.assume_init())
        }
    }

    /// 句柄创建后的公共收尾：建属性集与回调袋。
    unsafe fn finish_construct(handle: SPXRECOHANDLE) -> Result<SpeechRecognizer> {
        let mut prop_bag_handle: MaybeUninit<SPXPROPERTYBAGHANDLE> = MaybeUninit::uninit();
        let ret = recognizer_get_property_bag(handle, prop_bag_handle.as_mut_ptr());
        convert_err(ret, "SpeechRecognizer get_property_bag error")?;
        let properties = PropertyCollection::from_handle(prop_bag_handle.assume_init());

        Ok(SpeechRecognizer {
            handle: SmartHandle::create("SpeechRecognizer", handle, recognizer_handle_release),
            properties,
            callback_bag: Box::new(CallbackBag {
                recognizing_cb: None,
                recognized_cb: None,
                canceled_cb: None,
                session_started_cb: None,
                session_stopped_cb: None,
            }),
        })
    }

    /// 单次识别（适合 WAV 文件，自动化测试）。
    pub async fn recognize_once_async(&self) -> Result<SpeechRecognitionResult> {
        unsafe {
            let mut handle_result: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_recognize_once(self.handle.inner(), handle_result.as_mut_ptr());
            convert_err(ret, "SpeechRecognizer::recognize_once_async error")?;
            SpeechRecognitionResult::from_handle(handle_result.assume_init())
        }
    }

    /// 开始连续识别。
    pub async fn start_continuous_recognition_async(&mut self) -> Result<()> {
        unsafe {
            let mut handle_async: MaybeUninit<crate::ffi::SPXASYNCHANDLE> = MaybeUninit::uninit();
            let mut ret = recognizer_start_continuous_recognition_async(
                self.handle.inner(),
                handle_async.as_mut_ptr(),
            );
            convert_err(ret, "SpeechRecognizer: start_continuous error")?;
            let h_async = handle_async.assume_init();
            ret = recognizer_start_continuous_recognition_async_wait_for(h_async, u32::MAX);
            recognizer_async_handle_release(h_async);
            convert_err(ret, "SpeechRecognizer: start_continuous wait_for error")?;
        }
        Ok(())
    }

    /// 停止连续识别。
    pub async fn stop_continuous_recognition_async(&mut self) -> Result<()> {
        unsafe {
            let mut handle_async: MaybeUninit<crate::ffi::SPXASYNCHANDLE> = MaybeUninit::uninit();
            let mut ret = recognizer_stop_continuous_recognition_async(
                self.handle.inner(),
                handle_async.as_mut_ptr(),
            );
            convert_err(ret, "SpeechRecognizer: stop_continuous error")?;
            let h_async = handle_async.assume_init();
            ret = recognizer_stop_continuous_recognition_async_wait_for(h_async, u32::MAX);
            recognizer_async_handle_release(h_async);
            convert_err(ret, "SpeechRecognizer: stop_continuous wait_for error")?;
        }
        Ok(())
    }

    pub fn set_recognizing_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechRecognitionEventArgs) + 'static + Send,
    {
        self.callback_bag.recognizing_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_recognizing_set_callback(
                self.handle.inner(),
                Some(Self::cb_recognizing),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "SpeechRecognizer::set_recognizing_cb error")
        }
    }

    pub fn set_recognized_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechRecognitionEventArgs) + 'static + Send,
    {
        self.callback_bag.recognized_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_recognized_set_callback(
                self.handle.inner(),
                Some(Self::cb_recognized),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "SpeechRecognizer::set_recognized_cb error")
        }
    }

    pub fn set_canceled_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechRecognitionCanceledEventArgs) + 'static + Send,
    {
        self.callback_bag.canceled_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_canceled_set_callback(
                self.handle.inner(),
                Some(Self::cb_canceled),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "SpeechRecognizer::set_canceled_cb error")
        }
    }

    pub fn set_session_started_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn() + 'static + Send,
    {
        self.callback_bag.session_started_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_session_started_set_callback(
                self.handle.inner(),
                Some(Self::cb_session_started),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "SpeechRecognizer::set_session_started_cb error")
        }
    }

    pub fn set_session_stopped_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn() + 'static + Send,
    {
        self.callback_bag.session_stopped_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_session_stopped_set_callback(
                self.handle.inner(),
                Some(Self::cb_session_stopped),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "SpeechRecognizer::set_session_stopped_cb error")
        }
    }

    // ─── 静态回调跳板 ────────────────────────────────────────────────

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_recognizing(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.recognizing_cb {
            if let Ok(args) = SpeechRecognitionEventArgs::from_handle(hevent) {
                cb(args);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_recognized(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.recognized_cb {
            if let Ok(args) = SpeechRecognitionEventArgs::from_handle(hevent) {
                cb(args);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_canceled(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.canceled_cb {
            if let Ok(args) = SpeechRecognitionCanceledEventArgs::from_handle(hevent) {
                cb(args);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_session_started(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        // 不需要事件内容，直接释放并回调
        recognizer_event_handle_release(hevent);
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.session_started_cb {
            cb();
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_session_stopped(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        recognizer_event_handle_release(hevent);
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.session_stopped_cb {
            cb();
        }
    }
}

impl Drop for SpeechRecognizer {
    fn drop(&mut self) {
        trace!("SpeechRecognizer::drop — unregistering callbacks");
        unsafe {
            let _ = recognizer_recognizing_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_recognized_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_canceled_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_session_started_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_session_stopped_set_callback(self.handle.inner(), None, std::ptr::null_mut());
        }
    }
}
