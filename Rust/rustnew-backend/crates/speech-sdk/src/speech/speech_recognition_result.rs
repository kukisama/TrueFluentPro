//! SpeechRecognitionResult — base result type for recognition operations.

use crate::common::{PropertyCollection, ResultReason};
use crate::error::{convert_err, Result};
use crate::ffi::{
    result_get_result_id, result_get_reason, result_get_text,
    result_get_duration, result_get_offset, result_get_property_bag,
    recognizer_result_handle_release,
    SmartHandle, SPXRESULTHANDLE, SPXPROPERTYBAGHANDLE,
};
use std::ffi::CStr;
use std::mem::MaybeUninit;
use std::time::Duration;

/// Base recognition result — contains text, reason, timing, and properties.
#[derive(Debug)]
pub struct SpeechRecognitionResult {
    pub(crate) handle: SmartHandle<SPXRESULTHANDLE>,
    pub result_id: String,
    pub reason: ResultReason,
    pub text: String,
    pub duration: Duration,
    pub offset: Duration,
    pub properties: PropertyCollection,
}

impl SpeechRecognitionResult {
    /// Create from a native result handle.
    ///
    /// Corresponds to Go SDK's `NewSpeechRecognitionResultFromHandle`.
    ///
    /// # Safety
    /// `handle` must be a valid result handle.
    pub(crate) unsafe fn from_handle(handle: SPXRESULTHANDLE) -> Result<SpeechRecognitionResult> {
        let mut buffer = vec![0u8; 1024];

        // ResultID
        let ret = result_get_result_id(
            handle,
            buffer.as_mut_ptr() as *mut std::os::raw::c_char,
            buffer.len() as u32,
        );
        convert_err(ret, "SpeechRecognitionResult: result_get_result_id error")?;
        let result_id = CStr::from_ptr(buffer.as_ptr() as *const std::os::raw::c_char)
            .to_string_lossy()
            .into_owned();

        // Reason
        let mut c_reason: i32 = 0;
        let ret = result_get_reason(handle, &mut c_reason);
        convert_err(ret, "SpeechRecognitionResult: result_get_reason error")?;
        let reason = ResultReason::from(c_reason);

        // Text
        let ret = result_get_text(
            handle,
            buffer.as_mut_ptr() as *mut std::os::raw::c_char,
            buffer.len() as u32,
        );
        convert_err(ret, "SpeechRecognitionResult: result_get_text error")?;
        let text = CStr::from_ptr(buffer.as_ptr() as *const std::os::raw::c_char)
            .to_string_lossy()
            .into_owned();

        // Duration (100-nanosecond ticks)
        let mut c_duration: u64 = 0;
        let ret = result_get_duration(handle, &mut c_duration);
        convert_err(ret, "SpeechRecognitionResult: result_get_duration error")?;
        let duration = Duration::from_nanos(c_duration * 100);

        // Offset (100-nanosecond ticks)
        let mut c_offset: u64 = 0;
        let ret = result_get_offset(handle, &mut c_offset);
        convert_err(ret, "SpeechRecognitionResult: result_get_offset error")?;
        let offset = Duration::from_nanos(c_offset * 100);

        // Properties
        let mut prop_bag_handle: MaybeUninit<SPXPROPERTYBAGHANDLE> = MaybeUninit::uninit();
        let ret = result_get_property_bag(handle, prop_bag_handle.as_mut_ptr());
        convert_err(ret, "SpeechRecognitionResult: result_get_property_bag error")?;
        let properties = PropertyCollection::from_handle(prop_bag_handle.assume_init());

        Ok(SpeechRecognitionResult {
            handle: SmartHandle::create("SpeechRecognitionResult", handle, recognizer_result_handle_release),
            result_id,
            reason,
            text,
            duration,
            offset,
            properties,
        })
    }

    /// 自动语言检测结果（如 "zh-CN"）；未启用自动检测时返回空串。
    pub fn detected_language(&self) -> Result<String> {
        self.properties.get_property(
            crate::common::PropertyId::SpeechServiceConnectionAutoDetectSourceLanguageResult,
            "",
        )
    }

    /// 会话转写中的说话人标识（如 "Guest-1"）；非转写结果时返回空串。
    pub fn speaker_id(&self) -> Result<String> {
        unsafe {
            let mut buffer = vec![0u8; 256];
            let ret = crate::ffi::conversation_transcription_result_get_speaker_id(
                self.handle.inner(),
                buffer.as_mut_ptr() as *mut std::os::raw::c_char,
                buffer.len() as u32,
            );
            convert_err(ret, "SpeechRecognitionResult: get_speaker_id error")?;
            Ok(CStr::from_ptr(buffer.as_ptr() as *const std::os::raw::c_char)
                .to_string_lossy()
                .into_owned())
        }
    }
}