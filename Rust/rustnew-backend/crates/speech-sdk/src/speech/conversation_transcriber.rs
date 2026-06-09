//! ConversationTranscriber — 多说话人会话转写。
//!
//! 在连续音频（多通道麦克风阵列或 WAV 文件）中区分不同说话人并转写文本，
//! 每条 `transcribed` 结果带有 `speaker_id`（如 "Guest-1"）。
//!
//! 复用底层 recognizer 的连续识别与事件回调机制。

use crate::audio::AudioConfig;
use crate::common::{PropertyCollection, PropertyId};
use crate::error::{convert_err, Result};
use crate::ffi::{
    recognizer_async_handle_release, recognizer_canceled_set_callback,
    recognizer_create_conversation_transcriber_from_config, recognizer_event_handle_release,
    recognizer_get_property_bag, recognizer_handle_release, recognizer_recognition_event_get_result,
    recognizer_recognized_set_callback, recognizer_recognizing_set_callback,
    recognizer_session_started_set_callback, recognizer_session_stopped_set_callback,
    recognizer_start_continuous_recognition_async,
    recognizer_start_continuous_recognition_async_wait_for,
    recognizer_stop_continuous_recognition_async,
    recognizer_stop_continuous_recognition_async_wait_for, SmartHandle, SPXASYNCHANDLE,
    SPXEVENTHANDLE, SPXPROPERTYBAGHANDLE, SPXRECOHANDLE, SPXRESULTHANDLE,
};
use crate::speech::{SpeechConfig, SpeechRecognitionResult};
use log::*;
use std::fmt;
use std::mem::MaybeUninit;
use std::os::raw::c_void;

/// 会话转写事件参数（transcribing / transcribed）。
#[derive(Debug)]
pub struct ConversationTranscriptionEventArgs {
    pub result: SpeechRecognitionResult,
    /// 说话人标识（如 "Guest-1"）。
    pub speaker_id: String,
}

impl ConversationTranscriptionEventArgs {
    /// # Safety
    /// `handle` 必须是有效的转写事件句柄。
    pub(crate) unsafe fn from_handle(
        handle: SPXEVENTHANDLE,
    ) -> Result<ConversationTranscriptionEventArgs> {
        let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
        let ret = recognizer_recognition_event_get_result(handle, result_handle.as_mut_ptr());
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "ConversationTranscriptionEventArgs: get_result error")?;
        }
        let result = match SpeechRecognitionResult::from_handle(result_handle.assume_init()) {
            Ok(r) => r,
            Err(e) => {
                recognizer_event_handle_release(handle);
                return Err(e);
            }
        };
        recognizer_event_handle_release(handle);
        let speaker_id = result.speaker_id().unwrap_or_default();
        Ok(ConversationTranscriptionEventArgs { result, speaker_id })
    }
}

struct CallbackBag {
    transcribing_cb: Option<Box<dyn Fn(ConversationTranscriptionEventArgs) + Send>>,
    transcribed_cb: Option<Box<dyn Fn(ConversationTranscriptionEventArgs) + Send>>,
    canceled_cb: Option<Box<dyn Fn(String) + Send>>,
    session_started_cb: Option<Box<dyn Fn() + Send>>,
    session_stopped_cb: Option<Box<dyn Fn() + Send>>,
}

/// 会话转写器。
pub struct ConversationTranscriber {
    handle: SmartHandle<SPXRECOHANDLE>,
    #[allow(dead_code)]
    properties: PropertyCollection,
    callback_bag: Box<CallbackBag>,
}

impl fmt::Debug for ConversationTranscriber {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ConversationTranscriber")
            .field("handle", &self.handle)
            .finish()
    }
}

impl ConversationTranscriber {
    /// 以配置与音频输入创建会话转写器。
    pub fn from_config(
        config: &SpeechConfig,
        audio_config: &AudioConfig,
    ) -> Result<ConversationTranscriber> {
        unsafe {
            let mut handle: MaybeUninit<SPXRECOHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_create_conversation_transcriber_from_config(
                handle.as_mut_ptr(),
                config.handle.inner(),
                audio_config.handle.inner(),
            );
            convert_err(ret, "ConversationTranscriber::from_config error")?;
            let handle = handle.assume_init();

            let mut prop_bag_handle: MaybeUninit<SPXPROPERTYBAGHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_get_property_bag(handle, prop_bag_handle.as_mut_ptr());
            convert_err(ret, "ConversationTranscriber::from_config get_property_bag error")?;
            let properties = PropertyCollection::from_handle(prop_bag_handle.assume_init());

            Ok(ConversationTranscriber {
                handle: SmartHandle::create(
                    "ConversationTranscriber",
                    handle,
                    recognizer_handle_release,
                ),
                properties,
                callback_bag: Box::new(CallbackBag {
                    transcribing_cb: None,
                    transcribed_cb: None,
                    canceled_cb: None,
                    session_started_cb: None,
                    session_stopped_cb: None,
                }),
            })
        }
    }

    /// 开始会话转写。
    pub async fn start_transcribing_async(&mut self) -> Result<()> {
        unsafe {
            let mut handle_async: MaybeUninit<SPXASYNCHANDLE> = MaybeUninit::uninit();
            let mut ret = recognizer_start_continuous_recognition_async(
                self.handle.inner(),
                handle_async.as_mut_ptr(),
            );
            convert_err(ret, "ConversationTranscriber: start error")?;
            let h_async = handle_async.assume_init();
            ret = recognizer_start_continuous_recognition_async_wait_for(h_async, u32::MAX);
            recognizer_async_handle_release(h_async);
            convert_err(ret, "ConversationTranscriber: start wait_for error")?;
        }
        Ok(())
    }

    /// 停止会话转写。
    pub async fn stop_transcribing_async(&mut self) -> Result<()> {
        unsafe {
            let mut handle_async: MaybeUninit<SPXASYNCHANDLE> = MaybeUninit::uninit();
            let mut ret = recognizer_stop_continuous_recognition_async(
                self.handle.inner(),
                handle_async.as_mut_ptr(),
            );
            convert_err(ret, "ConversationTranscriber: stop error")?;
            let h_async = handle_async.assume_init();
            ret = recognizer_stop_continuous_recognition_async_wait_for(h_async, u32::MAX);
            recognizer_async_handle_release(h_async);
            convert_err(ret, "ConversationTranscriber: stop wait_for error")?;
        }
        Ok(())
    }

    pub fn set_transcribing_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(ConversationTranscriptionEventArgs) + 'static + Send,
    {
        self.callback_bag.transcribing_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_recognizing_set_callback(
                self.handle.inner(),
                Some(Self::cb_transcribing),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "ConversationTranscriber::set_transcribing_cb error")
        }
    }

    pub fn set_transcribed_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(ConversationTranscriptionEventArgs) + 'static + Send,
    {
        self.callback_bag.transcribed_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_recognized_set_callback(
                self.handle.inner(),
                Some(Self::cb_transcribed),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "ConversationTranscriber::set_transcribed_cb error")
        }
    }

    pub fn set_canceled_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(String) + 'static + Send,
    {
        self.callback_bag.canceled_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_canceled_set_callback(
                self.handle.inner(),
                Some(Self::cb_canceled),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "ConversationTranscriber::set_canceled_cb error")
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
            convert_err(ret, "ConversationTranscriber::set_session_started_cb error")
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
            convert_err(ret, "ConversationTranscriber::set_session_stopped_cb error")
        }
    }

    // ─── 静态回调跳板 ────────────────────────────────────────────────

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_transcribing(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.transcribing_cb {
            if let Ok(args) = ConversationTranscriptionEventArgs::from_handle(hevent) {
                cb(args);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_transcribed(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.transcribed_cb {
            if let Ok(args) = ConversationTranscriptionEventArgs::from_handle(hevent) {
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
        let details = ConversationTranscriptionEventArgs::from_handle(hevent)
            .ok()
            .map(|a| {
                a.result
                    .properties
                    .get_property(PropertyId::SpeechServiceResponseJsonErrorDetails, "")
                    .unwrap_or_default()
            })
            .unwrap_or_default();
        if let Some(cb) = &bag.canceled_cb {
            cb(details);
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_session_started(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
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

impl Drop for ConversationTranscriber {
    fn drop(&mut self) {
        trace!("ConversationTranscriber::drop — unregistering callbacks");
        unsafe {
            let _ = recognizer_recognizing_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_recognized_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_canceled_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_session_started_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_session_stopped_set_callback(self.handle.inner(), None, std::ptr::null_mut());
        }
    }
}
