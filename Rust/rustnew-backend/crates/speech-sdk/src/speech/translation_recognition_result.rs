//! TranslationRecognitionResult and related event types.
//!
//! Ported from Go SDK: translation_recognition_result.go
//! (https://github.com/microsoft/cognitive-services-speech-sdk-go/blob/master/speech/translation_recognition_result.go)
//!
//! Key types:
//! - TranslationRecognitionResult: extends SpeechRecognitionResult with translations map
//! - TranslationSynthesisResult: voice output of translated text
//! - TranslationRecognitionEventArgs: event args for recognizing/recognized events
//! - TranslationRecognitionCanceledEventArgs: event args for canceled events
//! - TranslationSynthesisEventArgs: event args for synthesis events

use std::collections::HashMap;

use crate::common::{
    CancellationErrorCode, CancellationReason, PropertyId, ResultReason,
};
use crate::error::{convert_err, Result};
use crate::ffi::{
    recognizer_recognition_event_get_result, recognizer_event_handle_release,
    result_get_reason, result_get_reason_canceled, result_get_canceled_error_code,
    translation_text_result_get_translation_count, translation_text_result_get_translation,
    translation_synthesis_result_get_audio_data,
    SPXEVENTHANDLE, SPXRESULTHANDLE, SPXERR_BUFFER_TOO_SMALL,
};
use crate::speech::SpeechRecognitionResult;

use std::mem::MaybeUninit;

// ─── TranslationRecognitionResult ────────────────────────────────────

/// Result of a translation recognition operation.
///
/// Extends SpeechRecognitionResult with a map of translations keyed by language code.
///
/// Go equivalent: `TranslationRecognitionResult` struct
#[derive(Debug)]
pub struct TranslationRecognitionResult {
    /// The base recognition result (text, reason, timing, etc.)
    pub base: SpeechRecognitionResult,
    /// Map of target language code → translated text (e.g. "en" → "Hello")
    pub translations: HashMap<String, String>,
}

impl TranslationRecognitionResult {
    /// Create from a native result handle, extracting both base result and translation map.
    ///
    /// Go equivalent: `NewTranslationRecognitionResultFromHandle`
    ///
    /// # Safety
    /// `handle` must be a valid result handle from a translation recognizer.
    pub(crate) unsafe fn from_handle(handle: SPXRESULTHANDLE) -> Result<TranslationRecognitionResult> {
        // Parse the base recognition result (text, reason, duration, offset, properties)
        let base = SpeechRecognitionResult::from_handle(handle)?;

        // Get translation count
        let mut count: usize = 0;
        let ret = translation_text_result_get_translation_count(
            base.handle.inner(),
            &mut count,
        );
        convert_err(ret, "TranslationRecognitionResult: get_translation_count error")?;

        // Extract each translation pair
        let mut translations = HashMap::with_capacity(count);
        for i in 0..count {
            // First call: get required buffer sizes
            let mut lang_size: usize = 0;
            let mut text_size: usize = 0;
            let ret = translation_text_result_get_translation(
                base.handle.inner(),
                i,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                &mut lang_size,
                &mut text_size,
            );
            // This call returns the required sizes; ignore the specific error code
            if ret != 0 && ret != SPXERR_BUFFER_TOO_SMALL as usize {
                convert_err(ret, "TranslationRecognitionResult: get_translation size query error")?;
            }

            // Second call: get actual data
            let mut lang_buf = vec![0u8; lang_size];
            let mut text_buf = vec![0u8; text_size];
            let ret = translation_text_result_get_translation(
                base.handle.inner(),
                i,
                lang_buf.as_mut_ptr() as *mut std::os::raw::c_char,
                text_buf.as_mut_ptr() as *mut std::os::raw::c_char,
                &mut lang_size,
                &mut text_size,
            );
            convert_err(ret, "TranslationRecognitionResult: get_translation error")?;

            // Convert C strings to Rust strings (strip null terminator)
            let lang = String::from_utf8_lossy(&lang_buf[..lang_size.saturating_sub(1)]).into_owned();
            let text = String::from_utf8_lossy(&text_buf[..text_size.saturating_sub(1)]).into_owned();
            translations.insert(lang, text);
        }

        Ok(TranslationRecognitionResult { base, translations })
    }

    /// Get translation for a specific language.
    ///
    /// Go equivalent: `(TranslationRecognitionResult).GetTranslation`
    pub fn get_translation(&self, language: &str) -> Option<&str> {
        self.translations.get(language).map(|s| s.as_str())
    }

    /// Get all translations.
    ///
    /// Go equivalent: `(TranslationRecognitionResult).GetTranslations`
    pub fn get_translations(&self) -> &HashMap<String, String> {
        &self.translations
    }
}

// ─── TranslationSynthesisResult ──────────────────────────────────────

/// Voice output of translated text (audio data).
///
/// Go equivalent: `TranslationSynthesisResult` struct
#[derive(Debug)]
pub struct TranslationSynthesisResult {
    pub reason: ResultReason,
    pub audio_data: Vec<u8>,
}

impl TranslationSynthesisResult {
    /// Create from a native result handle.
    ///
    /// Go equivalent: `NewTranslationSynthesisResultFromHandle`
    ///
    /// # Safety
    /// `handle` must be a valid result handle.
    pub(crate) unsafe fn from_handle(handle: SPXRESULTHANDLE) -> Result<TranslationSynthesisResult> {
        // Get reason
        let mut c_reason: i32 = 0;
        let ret = result_get_reason(handle, &mut c_reason);
        convert_err(ret, "TranslationSynthesisResult: get_reason error")?;
        let reason = ResultReason::from(c_reason);

        // Get audio data size (first call with null buffer)
        let mut size: usize = 0;
        let mut ret = translation_synthesis_result_get_audio_data(handle, std::ptr::null_mut(), &mut size);

        if ret == SPXERR_BUFFER_TOO_SMALL as usize && size > 0 {
            let mut buffer = vec![0u8; size];
            ret = translation_synthesis_result_get_audio_data(
                handle,
                buffer.as_mut_ptr(),
                &mut size,
            );
            convert_err(ret, "TranslationSynthesisResult: get_audio_data error")?;
            Ok(TranslationSynthesisResult { reason, audio_data: buffer })
        } else if ret == 0 {
            // No audio data available (SPX_NOERROR with size 0)
            Ok(TranslationSynthesisResult { reason, audio_data: Vec::new() })
        } else {
            // Propagate actual errors (Go SDK: `if ret != C.SPX_NOERROR { return nil, err }`)
            convert_err(ret, "TranslationSynthesisResult: get_audio_data size query error")?;
            unreachable!()
        }
    }

    /// Get the synthesized audio data bytes.
    pub fn get_audio_data(&self) -> &[u8] {
        &self.audio_data
    }
}

// ─── Event Args types ────────────────────────────────────────────────

/// Event args for translation recognition events (recognizing / recognized).
///
/// Go equivalent: `TranslationRecognitionEventArgs`
#[derive(Debug)]
pub struct TranslationRecognitionEventArgs {
    pub result: TranslationRecognitionResult,
}

impl TranslationRecognitionEventArgs {
    /// Create from a native event handle.
    ///
    /// Go equivalent: `NewTranslationRecognitionEventArgsFromHandle`
    ///
    /// # Safety
    /// `handle` must be a valid event handle.
    pub(crate) unsafe fn from_handle(handle: SPXEVENTHANDLE) -> Result<TranslationRecognitionEventArgs> {
        // Extract result handle from event
        let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
        let ret = recognizer_recognition_event_get_result(handle, result_handle.as_mut_ptr());
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "TranslationRecognitionEventArgs: get_result error")?;
        }

        let result = match TranslationRecognitionResult::from_handle(result_handle.assume_init()) {
            Ok(r) => r,
            Err(e) => {
                recognizer_event_handle_release(handle);
                return Err(e);
            }
        };

        // Release event handle after extraction
        recognizer_event_handle_release(handle);

        Ok(TranslationRecognitionEventArgs { result })
    }
}

/// Event args for canceled translation events.
///
/// Go equivalent: `TranslationRecognitionCanceledEventArgs`
#[derive(Debug)]
pub struct TranslationRecognitionCanceledEventArgs {
    pub result: TranslationRecognitionResult,
    pub error_details: String,
    pub reason: CancellationReason,
    pub error_code: CancellationErrorCode,
}

impl TranslationRecognitionCanceledEventArgs {
    /// Create from a native event handle.
    ///
    /// Go equivalent: `NewTranslationRecognitionCanceledEventArgsFromHandle`
    ///
    /// # Safety
    /// `handle` must be a valid event handle.
    pub(crate) unsafe fn from_handle(handle: SPXEVENTHANDLE) -> Result<TranslationRecognitionCanceledEventArgs> {
        // First, extract the translation result
        let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
        let ret = recognizer_recognition_event_get_result(handle, result_handle.as_mut_ptr());
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "TranslationRecognitionCanceledEventArgs: get_result error")?;
        }

        let result = match TranslationRecognitionResult::from_handle(result_handle.assume_init()) {
            Ok(r) => r,
            Err(e) => {
                recognizer_event_handle_release(handle);
                return Err(e);
            }
        };

        // Get cancellation reason
        let mut c_reason: i32 = 0;
        let ret = result_get_reason_canceled(result.base.handle.inner(), &mut c_reason);
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "TranslationRecognitionCanceledEventArgs: get_reason_canceled error")?;
        }
        let reason = CancellationReason::from(c_reason);

        // Get cancellation error code
        let mut c_error_code: i32 = 0;
        let ret = result_get_canceled_error_code(result.base.handle.inner(), &mut c_error_code);
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "TranslationRecognitionCanceledEventArgs: get_error_code error")?;
        }
        let error_code = CancellationErrorCode::from(c_error_code);

        // Get error details from properties
        let error_details = result
            .base
            .properties
            .get_property(PropertyId::SpeechServiceResponseJsonErrorDetails, "")
            .unwrap_or_default();

        // Release event handle
        recognizer_event_handle_release(handle);

        Ok(TranslationRecognitionCanceledEventArgs {
            result,
            error_details,
            reason,
            error_code,
        })
    }
}

/// Event args for translation synthesis events (synthesized audio output).
///
/// Go equivalent: `TranslationSynthesisEventArgs`
#[derive(Debug)]
pub struct TranslationSynthesisEventArgs {
    pub result: TranslationSynthesisResult,
}

impl TranslationSynthesisEventArgs {
    /// Create from a native event handle.
    ///
    /// Go equivalent: `NewTranslationSynthesisEventArgsFromHandle`
    ///
    /// # Safety
    /// `handle` must be a valid event handle.
    pub(crate) unsafe fn from_handle(handle: SPXEVENTHANDLE) -> Result<TranslationSynthesisEventArgs> {
        let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
        let ret = recognizer_recognition_event_get_result(handle, result_handle.as_mut_ptr());
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "TranslationSynthesisEventArgs: get_result error")?;
        }

        let result = match TranslationSynthesisResult::from_handle(result_handle.assume_init()) {
            Ok(r) => r,
            Err(e) => {
                recognizer_event_handle_release(handle);
                return Err(e);
            }
        };

        // Release event handle after extraction (matching other event args types)
        recognizer_event_handle_release(handle);

        Ok(TranslationSynthesisEventArgs { result })
    }
}
