//! TranslationRecognizer — real-time speech translation with callbacks.
//!
//! Ported from Go SDK: translation_recognizer.go + translation_callback_helpers.go
//! (https://github.com/microsoft/cognitive-services-speech-sdk-go/blob/master/speech/translation_recognizer.go)
//!
//! The callback mechanism follows the existing Rust SDK's pattern (CallbackBag),
//! where closures are boxed and stored in a heap-allocated struct, then a raw pointer
//! is passed as the C callback context. This avoids the Go SDK's global map approach
//! and is more idiomatic Rust.
//!
//! Key difference from Go: Rust uses `Box<CallbackBag>` pinned on the heap (same as
//! the existing SpeechRecognizer in jabber-tools/cognitive-services-speech-sdk-rs),
//! instead of Go's package-level sync.Mutex-protected maps.

use crate::audio::AudioConfig;
use crate::common::{PropertyCollection, PropertyId};
use crate::error::{convert_err, Result};
use crate::ffi::{
    recognizer_async_handle_release,
    recognizer_canceled_set_callback,
    recognizer_create_translation_recognizer_from_config,
    recognizer_get_property_bag,
    recognizer_handle_release,
    recognizer_recognize_once,
    recognizer_recognized_set_callback,
    recognizer_recognizing_set_callback,
    recognizer_session_started_set_callback,
    recognizer_session_stopped_set_callback,
    recognizer_speech_end_detected_set_callback,
    recognizer_speech_start_detected_set_callback,
    recognizer_start_continuous_recognition_async,
    recognizer_start_continuous_recognition_async_wait_for,
    recognizer_stop_continuous_recognition_async,
    recognizer_stop_continuous_recognition_async_wait_for,
    translator_synthesizing_audio_set_callback,
    SmartHandle, SPXASYNCHANDLE, SPXEVENTHANDLE, SPXPROPERTYBAGHANDLE,
    SPXRECOHANDLE, SPXRESULTHANDLE,
};
use crate::speech::{
    RecognitionEvent, SessionEvent, SpeechTranslationConfig,
    TranslationRecognitionCanceledEventArgs, TranslationRecognitionEventArgs,
    TranslationRecognitionResult, TranslationSynthesisEventArgs,
};
use log::*;
use std::fmt;
use std::mem::MaybeUninit;
use std::os::raw::c_void;

/// Internal struct holding all callback closures for translation events.
///
/// Boxed and pinned on the heap so the pointer passed to C remains stable even
/// if the TranslationRecognizer is moved.
struct TranslationCallbackBag {
    session_started_cb: Option<Box<dyn Fn(SessionEvent) + Send>>,
    session_stopped_cb: Option<Box<dyn Fn(SessionEvent) + Send>>,
    speech_start_detected_cb: Option<Box<dyn Fn(RecognitionEvent) + Send>>,
    speech_end_detected_cb: Option<Box<dyn Fn(RecognitionEvent) + Send>>,
    recognizing_cb: Option<Box<dyn Fn(TranslationRecognitionEventArgs) + Send>>,
    recognized_cb: Option<Box<dyn Fn(TranslationRecognitionEventArgs) + Send>>,
    canceled_cb: Option<Box<dyn Fn(TranslationRecognitionCanceledEventArgs) + Send>>,
    synthesizing_cb: Option<Box<dyn Fn(TranslationSynthesisEventArgs) + Send>>,
}

/// Translation recognizer for real-time speech-to-text translation.
///
/// Supports:
/// - Single-shot recognition (`recognize_once_async`)
/// - Continuous recognition (`start_continuous_recognition_async` / `stop_continuous_recognition_async`)
/// - Callbacks for recognizing, recognized, canceled, session, and synthesis events
///
/// Go equivalent: `TranslationRecognizer` struct
pub struct TranslationRecognizer {
    pub(crate) handle: SmartHandle<SPXRECOHANDLE>,
    properties: PropertyCollection,
    callback_bag: Box<TranslationCallbackBag>,
}

impl fmt::Debug for TranslationRecognizer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("TranslationRecognizer")
            .field("handle", &self.handle)
            .field("properties", &self.properties)
            .finish()
    }
}

impl TranslationRecognizer {
    /// Internal: create from a raw handle.
    ///
    /// # Safety
    /// `handle` must be a valid recognizer handle.
    unsafe fn from_handle(handle: SPXRECOHANDLE) -> Result<TranslationRecognizer> {
        let mut prop_bag_handle: MaybeUninit<SPXPROPERTYBAGHANDLE> = MaybeUninit::uninit();
        let ret = recognizer_get_property_bag(handle, prop_bag_handle.as_mut_ptr());
        convert_err(ret, "TranslationRecognizer::from_handle error")?;

        let properties = PropertyCollection::from_handle(prop_bag_handle.assume_init());

        Ok(TranslationRecognizer {
            handle: SmartHandle::create("TranslationRecognizer", handle, recognizer_handle_release),
            properties,
            callback_bag: Box::new(TranslationCallbackBag {
                session_started_cb: None,
                session_stopped_cb: None,
                speech_start_detected_cb: None,
                speech_end_detected_cb: None,
                recognizing_cb: None,
                recognized_cb: None,
                canceled_cb: None,
                synthesizing_cb: None,
            }),
        })
    }

    // ─── Constructors ────────────────────────────────────────────────

    /// Create a TranslationRecognizer from a SpeechTranslationConfig and AudioConfig.
    ///
    /// Go equivalent: `NewTranslationRecognizerFromConfig`
    pub fn from_config(
        config: &SpeechTranslationConfig,
        audio_config: &AudioConfig,
    ) -> Result<TranslationRecognizer> {
        unsafe {
            let mut handle: MaybeUninit<SPXRECOHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_create_translation_recognizer_from_config(
                handle.as_mut_ptr(),
                config.handle.inner(),
                audio_config.handle.inner(),
            );
            convert_err(ret, "TranslationRecognizer::from_config error")?;
            TranslationRecognizer::from_handle(handle.assume_init())
        }
    }

    // ─── Single-shot recognition ─────────────────────────────────────

    /// Recognize a single utterance and return the translation result.
    ///
    /// Go equivalent: `(TranslationRecognizer).RecognizeOnceAsync`
    pub async fn recognize_once_async(&self) -> Result<TranslationRecognitionResult> {
        unsafe {
            let mut handle_result: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            let ret = recognizer_recognize_once(self.handle.inner(), handle_result.as_mut_ptr());
            convert_err(ret, "TranslationRecognizer::recognize_once_async error")?;
            TranslationRecognitionResult::from_handle(handle_result.assume_init())
        }
    }

    // ─── Continuous recognition ──────────────────────────────────────

    /// Start continuous translation recognition.
    ///
    /// Go equivalent: `(TranslationRecognizer).StartContinuousRecognitionAsync`
    ///
    /// Follows Go SDK pattern: create async handle → wait → release immediately.
    pub async fn start_continuous_recognition_async(&mut self) -> Result<()> {
        unsafe {
            let mut handle_async: MaybeUninit<SPXASYNCHANDLE> = MaybeUninit::uninit();
            let mut ret = recognizer_start_continuous_recognition_async(
                self.handle.inner(),
                handle_async.as_mut_ptr(),
            );
            convert_err(ret, "TranslationRecognizer: start_continuous_recognition_async error")?;

            let h_async = handle_async.assume_init();
            ret = recognizer_start_continuous_recognition_async_wait_for(h_async, u32::MAX);
            // Release async handle after wait, matching Go SDK's releaseAsyncHandleIfValid pattern
            recognizer_async_handle_release(h_async);
            convert_err(ret, "TranslationRecognizer: start_continuous wait_for error")?;
        }
        Ok(())
    }

    /// Stop continuous translation recognition.
    ///
    /// Go equivalent: `(TranslationRecognizer).StopContinuousRecognitionAsync`
    ///
    /// Follows Go SDK pattern: create async handle → wait → release immediately.
    pub async fn stop_continuous_recognition_async(&mut self) -> Result<()> {
        unsafe {
            let mut handle_async: MaybeUninit<SPXASYNCHANDLE> = MaybeUninit::uninit();
            let mut ret = recognizer_stop_continuous_recognition_async(
                self.handle.inner(),
                handle_async.as_mut_ptr(),
            );
            convert_err(ret, "TranslationRecognizer: stop_continuous_recognition_async error")?;

            let h_async = handle_async.assume_init();
            ret = recognizer_stop_continuous_recognition_async_wait_for(h_async, u32::MAX);
            // Release async handle after wait, matching Go SDK's releaseAsyncHandleIfValid pattern
            recognizer_async_handle_release(h_async);
            convert_err(ret, "TranslationRecognizer: stop_continuous wait_for error")?;
        }
        Ok(())
    }

    // ─── Callback setters ────────────────────────────────────────────
    //
    // Pattern: store the closure in callback_bag, then register the
    // static `extern "C"` trampoline with the C API, passing a pointer
    // to callback_bag as the context.

    /// Register a callback for session started events.
    pub fn set_session_started_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SessionEvent) + 'static + Send,
    {
        self.callback_bag.session_started_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_session_started_set_callback(
                self.handle.inner(),
                Some(Self::cb_session_started),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "TranslationRecognizer::set_session_started_cb error")
        }
    }

    /// Register a callback for session stopped events.
    pub fn set_session_stopped_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SessionEvent) + 'static + Send,
    {
        self.callback_bag.session_stopped_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_session_stopped_set_callback(
                self.handle.inner(),
                Some(Self::cb_session_stopped),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "TranslationRecognizer::set_session_stopped_cb error")
        }
    }

    /// Register a callback for speech start detected events.
    pub fn set_speech_start_detected_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(RecognitionEvent) + 'static + Send,
    {
        self.callback_bag.speech_start_detected_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_speech_start_detected_set_callback(
                self.handle.inner(),
                Some(Self::cb_speech_start_detected),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "TranslationRecognizer::set_speech_start_detected_cb error")
        }
    }

    /// Register a callback for speech end detected events.
    pub fn set_speech_end_detected_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(RecognitionEvent) + 'static + Send,
    {
        self.callback_bag.speech_end_detected_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_speech_end_detected_set_callback(
                self.handle.inner(),
                Some(Self::cb_speech_end_detected),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "TranslationRecognizer::set_speech_end_detected_cb error")
        }
    }

    /// Register a callback for intermediate translation results (recognizing).
    ///
    /// Go equivalent: `(TranslationRecognizer).Recognizing`
    pub fn set_recognizing_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(TranslationRecognitionEventArgs) + 'static + Send,
    {
        self.callback_bag.recognizing_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_recognizing_set_callback(
                self.handle.inner(),
                Some(Self::cb_recognizing),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "TranslationRecognizer::set_recognizing_cb error")
        }
    }

    /// Register a callback for final translation results (recognized).
    ///
    /// Go equivalent: `(TranslationRecognizer).Recognized`
    pub fn set_recognized_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(TranslationRecognitionEventArgs) + 'static + Send,
    {
        self.callback_bag.recognized_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_recognized_set_callback(
                self.handle.inner(),
                Some(Self::cb_recognized),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "TranslationRecognizer::set_recognized_cb error")
        }
    }

    /// Register a callback for canceled translation events.
    ///
    /// Go equivalent: `(TranslationRecognizer).Canceled`
    pub fn set_canceled_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(TranslationRecognitionCanceledEventArgs) + 'static + Send,
    {
        self.callback_bag.canceled_cb = Some(Box::new(f));
        unsafe {
            let ret = recognizer_canceled_set_callback(
                self.handle.inner(),
                Some(Self::cb_canceled),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "TranslationRecognizer::set_canceled_cb error")
        }
    }

    /// Register a callback for translation synthesis events (audio output of translated text).
    ///
    /// Go equivalent: `(TranslationRecognizer).Synthesizing`
    pub fn set_synthesizing_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(TranslationSynthesisEventArgs) + 'static + Send,
    {
        self.callback_bag.synthesizing_cb = Some(Box::new(f));
        unsafe {
            let ret = translator_synthesizing_audio_set_callback(
                self.handle.inner(),
                Some(Self::cb_synthesizing),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "TranslationRecognizer::set_synthesizing_cb error")
        }
    }

    // ─── Properties ──────────────────────────────────────────────────

    pub fn get_endpoint_id(&self) -> Result<String> {
        self.properties.get_property(PropertyId::SpeechServiceConnectionEndpointId, "")
    }

    pub fn set_auth_token(&mut self, token: &str) -> Result<()> {
        self.properties.set_property(PropertyId::SpeechServiceAuthorizationToken, token)
    }

    pub fn get_auth_token(&self) -> Result<String> {
        self.properties.get_property(PropertyId::SpeechServiceAuthorizationToken, "")
    }

    // ─── Static callback trampolines (extern "C") ───────────────────
    //
    // These are the actual functions passed to the C SDK. They receive
    // a raw pointer to the CallbackBag and dispatch to the stored closures.

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_session_started(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        trace!("TranslationRecognizer::cb_session_started");
        let bag = &*(pvContext as *const TranslationCallbackBag);
        if let Some(cb) = &bag.session_started_cb {
            if let Ok(event) = SessionEvent::from_handle(hevent) {
                cb(event);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_session_stopped(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        trace!("TranslationRecognizer::cb_session_stopped");
        let bag = &*(pvContext as *const TranslationCallbackBag);
        if let Some(cb) = &bag.session_stopped_cb {
            if let Ok(event) = SessionEvent::from_handle(hevent) {
                cb(event);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_speech_start_detected(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        trace!("TranslationRecognizer::cb_speech_start_detected");
        let bag = &*(pvContext as *const TranslationCallbackBag);
        if let Some(cb) = &bag.speech_start_detected_cb {
            if let Ok(event) = RecognitionEvent::from_handle(hevent) {
                cb(event);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_speech_end_detected(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        trace!("TranslationRecognizer::cb_speech_end_detected");
        let bag = &*(pvContext as *const TranslationCallbackBag);
        if let Some(cb) = &bag.speech_end_detected_cb {
            if let Ok(event) = RecognitionEvent::from_handle(hevent) {
                cb(event);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_recognizing(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        trace!("TranslationRecognizer::cb_recognizing");
        let bag = &*(pvContext as *const TranslationCallbackBag);
        if let Some(cb) = &bag.recognizing_cb {
            if let Ok(event) = TranslationRecognitionEventArgs::from_handle(hevent) {
                cb(event);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_recognized(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        trace!("TranslationRecognizer::cb_recognized");
        let bag = &*(pvContext as *const TranslationCallbackBag);
        if let Some(cb) = &bag.recognized_cb {
            if let Ok(event) = TranslationRecognitionEventArgs::from_handle(hevent) {
                cb(event);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_canceled(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        trace!("TranslationRecognizer::cb_canceled");
        let bag = &*(pvContext as *const TranslationCallbackBag);
        if let Some(cb) = &bag.canceled_cb {
            if let Ok(event) = TranslationRecognitionCanceledEventArgs::from_handle(hevent) {
                cb(event);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_synthesizing(
        _hreco: SPXRECOHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        trace!("TranslationRecognizer::cb_synthesizing");
        let bag = &*(pvContext as *const TranslationCallbackBag);
        if let Some(cb) = &bag.synthesizing_cb {
            if let Ok(event) = TranslationSynthesisEventArgs::from_handle(hevent) {
                cb(event);
            }
        }
    }
}

/// On Drop, unregister all callbacks and release async handles,
/// mirroring Go SDK's `(TranslationRecognizer).Close()`.
impl Drop for TranslationRecognizer {
    fn drop(&mut self) {
        trace!("TranslationRecognizer::drop — unregistering callbacks");
        unsafe {
            // Unregister all callbacks by setting them to None in C
            let _ = recognizer_session_started_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_session_stopped_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_speech_start_detected_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_speech_end_detected_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_recognizing_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_recognized_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = recognizer_canceled_set_callback(self.handle.inner(), None, std::ptr::null_mut());
            let _ = translator_synthesizing_audio_set_callback(self.handle.inner(), None, std::ptr::null_mut());
        }
        // SmartHandle drops will release the recognizer and async handles
    }
}
