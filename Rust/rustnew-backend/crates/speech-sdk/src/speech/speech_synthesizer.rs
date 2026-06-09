//! SpeechSynthesizer — 文本转语音（TTS）。
//!
//! 全功能封装，对齐微软 Speech SDK：
//! - 同步合成：`speak_text` / `speak_ssml`
//! - 异步合成：`speak_text_async` / `speak_ssml_async`
//! - 流式合成（边合成边出音频，配合 `synthesizing` 回调）：`start_speaking_text` / `start_speaking_ssml`
//! - 停止合成：`stop_speaking`
//! - 嗓音列表：`get_voices_list`
//! - 事件回调：synthesis_started / synthesizing / completed / canceled / word_boundary / viseme / bookmark

use crate::audio::AudioConfig;
use crate::common::ResultReason;
use crate::error::{convert_err, Error, Result};
use crate::ffi::{
    synth_result_get_audio_data, synth_result_get_audio_length_duration,
    synth_result_get_canceled_error_code, synth_result_get_reason,
    synth_result_get_reason_canceled, synthesis_voices_result_get_reason,
    synthesis_voices_result_get_voice_info, synthesis_voices_result_get_voice_num,
    synthesizer_async_handle_release, synthesizer_bookmark_event_get_values,
    synthesizer_bookmark_reached_set_callback, synthesizer_canceled_set_callback,
    synthesizer_completed_set_callback, synthesizer_create_speech_synthesizer_from_config,
    synthesizer_event_get_text, synthesizer_get_voices_list, synthesizer_handle_release,
    synthesizer_result_handle_release, synthesizer_speak_async_wait_for, synthesizer_speak_ssml,
    synthesizer_speak_ssml_async, synthesizer_speak_text, synthesizer_speak_text_async,
    synthesizer_start_speaking_ssml, synthesizer_start_speaking_text,
    synthesizer_started_set_callback, synthesizer_stop_speaking,
    synthesizer_synthesis_event_get_result, synthesizer_synthesizing_set_callback,
    synthesizer_viseme_event_get_animation, synthesizer_viseme_event_get_values,
    synthesizer_viseme_received_set_callback, synthesizer_word_boundary_event_get_values,
    synthesizer_word_boundary_set_callback, voice_info_get_local_name, voice_info_get_locale,
    voice_info_get_name, voice_info_get_short_name, voice_info_get_style_list,
    voice_info_handle_release, SmartHandle, SPXASYNCHANDLE, SPXEVENTHANDLE, SPXRESULTHANDLE,
    SPXSYNTHHANDLE,
};
use crate::speech::SpeechConfig;
use log::*;
use std::ffi::{CStr, CString};
use std::fmt;
use std::mem::MaybeUninit;
use std::os::raw::c_void;

/// 合成结果。
#[derive(Debug, Clone)]
pub struct SpeechSynthesisResult {
    pub reason: ResultReason,
    /// 合成音频总字节数。
    pub audio_length: u32,
    /// 合成音频时长（100ns 单位的原始值）。
    pub audio_duration_ticks: u64,
    /// 完整的合成音频数据（PCM/WAV，取决于输出格式）。
    pub audio_data: Vec<u8>,
}

/// 单个嗓音信息。
#[derive(Debug, Clone)]
pub struct VoiceInfo {
    pub name: String,
    pub locale: String,
    pub short_name: String,
    pub local_name: String,
    /// 该嗓音支持的风格（逗号分隔的原始字符串）。
    pub style_list: String,
}

/// 合成事件参数（synthesis_started / synthesizing / completed / canceled）。
#[derive(Debug)]
pub struct SpeechSynthesisEventArgs {
    pub result: SpeechSynthesisResult,
}

/// 词边界事件参数。
#[derive(Debug, Clone)]
pub struct SpeechSynthesisWordBoundaryEventArgs {
    pub audio_offset_ticks: u64,
    pub duration_ticks: u64,
    pub text_offset: u32,
    pub word_length: u32,
    /// 边界类型原始值（0=Word，1=Punctuation，2=Sentence）。
    pub boundary_type: u32,
}

/// 口型（viseme）事件参数。
#[derive(Debug, Clone)]
pub struct SpeechSynthesisVisemeEventArgs {
    pub audio_offset_ticks: u64,
    pub viseme_id: u32,
    /// SSML 动画（若有）。
    pub animation: String,
}

/// 书签事件参数。
#[derive(Debug, Clone)]
pub struct SpeechSynthesisBookmarkEventArgs {
    pub audio_offset_ticks: u64,
    pub text: String,
}

#[derive(Default)]
struct CallbackBag {
    started_cb: Option<Box<dyn Fn(SpeechSynthesisEventArgs) + Send>>,
    synthesizing_cb: Option<Box<dyn Fn(SpeechSynthesisEventArgs) + Send>>,
    completed_cb: Option<Box<dyn Fn(SpeechSynthesisEventArgs) + Send>>,
    canceled_cb: Option<Box<dyn Fn(SpeechSynthesisEventArgs) + Send>>,
    word_boundary_cb: Option<Box<dyn Fn(SpeechSynthesisWordBoundaryEventArgs) + Send>>,
    viseme_cb: Option<Box<dyn Fn(SpeechSynthesisVisemeEventArgs) + Send>>,
    bookmark_cb: Option<Box<dyn Fn(SpeechSynthesisBookmarkEventArgs) + Send>>,
}

/// 文本转语音合成器。
pub struct SpeechSynthesizer {
    handle: SmartHandle<SPXSYNTHHANDLE>,
    callback_bag: Box<CallbackBag>,
}

impl fmt::Debug for SpeechSynthesizer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("SpeechSynthesizer").field("handle", &self.handle).finish()
    }
}

impl SpeechSynthesizer {
    /// 内部：暴露原生合成器句柄，供 Connection 等同 crate 使用。
    pub(crate) fn handle_inner(&self) -> SPXSYNTHHANDLE {
        self.handle.inner()
    }

    /// 以配置与音频输出创建合成器。
    pub fn from_config(
        config: &SpeechConfig,
        audio_config: &AudioConfig,
    ) -> Result<SpeechSynthesizer> {
        unsafe {
            let mut handle: MaybeUninit<SPXSYNTHHANDLE> = MaybeUninit::uninit();
            let ret = synthesizer_create_speech_synthesizer_from_config(
                handle.as_mut_ptr(),
                config.handle.inner(),
                audio_config.handle.inner(),
            );
            convert_err(ret, "SpeechSynthesizer::from_config error")?;
            Ok(SpeechSynthesizer {
                handle: SmartHandle::create(
                    "SpeechSynthesizer",
                    handle.assume_init(),
                    synthesizer_handle_release,
                ),
                callback_bag: Box::new(CallbackBag::default()),
            })
        }
    }

    // ─── 同步合成 ────────────────────────────────────────────────────

    /// 同步合成一段纯文字，阻塞直到完成。
    pub fn speak_text(&self, text: &str) -> Result<SpeechSynthesisResult> {
        let c_text = CString::new(text)?;
        unsafe {
            let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            let ret = synthesizer_speak_text(
                self.handle.inner(),
                c_text.as_ptr(),
                text.len() as u32,
                result_handle.as_mut_ptr(),
            );
            convert_err(ret, "SpeechSynthesizer::speak_text error")?;
            Self::take_result(result_handle.assume_init())
        }
    }

    /// 同步合成一段 SSML，阻塞直到完成。
    pub fn speak_ssml(&self, ssml: &str) -> Result<SpeechSynthesisResult> {
        let c_ssml = CString::new(ssml)?;
        unsafe {
            let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            let ret = synthesizer_speak_ssml(
                self.handle.inner(),
                c_ssml.as_ptr(),
                ssml.len() as u32,
                result_handle.as_mut_ptr(),
            );
            convert_err(ret, "SpeechSynthesizer::speak_ssml error")?;
            Self::take_result(result_handle.assume_init())
        }
    }

    // ─── 异步合成 ────────────────────────────────────────────────────

    /// 异步合成纯文字（内部以 async + wait_for 实现，仍返回完整结果）。
    pub async fn speak_text_async(&self, text: &str) -> Result<SpeechSynthesisResult> {
        let c_text = CString::new(text)?;
        unsafe {
            let mut h_async: MaybeUninit<SPXASYNCHANDLE> = MaybeUninit::uninit();
            let ret = synthesizer_speak_text_async(
                self.handle.inner(),
                c_text.as_ptr(),
                text.len() as u32,
                h_async.as_mut_ptr(),
            );
            convert_err(ret, "SpeechSynthesizer::speak_text_async error")?;
            self.await_speak(h_async.assume_init())
        }
    }

    /// 异步合成 SSML。
    pub async fn speak_ssml_async(&self, ssml: &str) -> Result<SpeechSynthesisResult> {
        let c_ssml = CString::new(ssml)?;
        unsafe {
            let mut h_async: MaybeUninit<SPXASYNCHANDLE> = MaybeUninit::uninit();
            let ret = synthesizer_speak_ssml_async(
                self.handle.inner(),
                c_ssml.as_ptr(),
                ssml.len() as u32,
                h_async.as_mut_ptr(),
            );
            convert_err(ret, "SpeechSynthesizer::speak_ssml_async error")?;
            self.await_speak(h_async.assume_init())
        }
    }

    // ─── 流式合成（边合成边出音频，配合 synthesizing 回调）────────────

    /// 开始流式合成纯文字：尽快返回，音频块通过 `set_synthesizing_cb` 回调推送。
    pub fn start_speaking_text(&self, text: &str) -> Result<SpeechSynthesisResult> {
        let c_text = CString::new(text)?;
        unsafe {
            let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            let ret = synthesizer_start_speaking_text(
                self.handle.inner(),
                c_text.as_ptr(),
                text.len() as u32,
                result_handle.as_mut_ptr(),
            );
            convert_err(ret, "SpeechSynthesizer::start_speaking_text error")?;
            Self::take_result(result_handle.assume_init())
        }
    }

    /// 开始流式合成 SSML。
    pub fn start_speaking_ssml(&self, ssml: &str) -> Result<SpeechSynthesisResult> {
        let c_ssml = CString::new(ssml)?;
        unsafe {
            let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            let ret = synthesizer_start_speaking_ssml(
                self.handle.inner(),
                c_ssml.as_ptr(),
                ssml.len() as u32,
                result_handle.as_mut_ptr(),
            );
            convert_err(ret, "SpeechSynthesizer::start_speaking_ssml error")?;
            Self::take_result(result_handle.assume_init())
        }
    }

    /// 停止当前合成。
    pub fn stop_speaking(&self) -> Result<()> {
        unsafe {
            let ret = synthesizer_stop_speaking(self.handle.inner());
            convert_err(ret, "SpeechSynthesizer::stop_speaking error")
        }
    }

    // ─── 嗓音列表 ────────────────────────────────────────────────────

    /// 查询可用嗓音。`locale` 传空串表示全部，例如 "zh-CN"。
    pub fn get_voices_list(&self, locale: &str) -> Result<Vec<VoiceInfo>> {
        let c_locale = CString::new(locale)?;
        unsafe {
            let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            let ret = synthesizer_get_voices_list(
                self.handle.inner(),
                c_locale.as_ptr(),
                result_handle.as_mut_ptr(),
            );
            convert_err(ret, "SpeechSynthesizer::get_voices_list error")?;
            let result_handle = result_handle.assume_init();

            let parsed = Self::parse_voices(result_handle);
            synthesizer_result_handle_release(result_handle);
            parsed
        }
    }

    /// # Safety
    /// `handle` 必须是有效的嗓音结果句柄。
    unsafe fn parse_voices(handle: SPXRESULTHANDLE) -> Result<Vec<VoiceInfo>> {
        let mut reason_val: i32 = 0;
        let ret = synthesis_voices_result_get_reason(handle, &mut reason_val);
        convert_err(ret, "get_voices_list: get_reason error")?;
        if ResultReason::from(reason_val) == ResultReason::Canceled {
            return Err(Error::new("获取嗓音列表被取消".to_string(), 0));
        }

        let mut num: u32 = 0;
        let ret = synthesis_voices_result_get_voice_num(handle, &mut num);
        convert_err(ret, "get_voices_list: get_voice_num error")?;

        let mut out = Vec::with_capacity(num as usize);
        for i in 0..num {
            let mut hvoice: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            let ret = synthesis_voices_result_get_voice_info(handle, i, hvoice.as_mut_ptr());
            if ret != 0 {
                continue;
            }
            let hvoice = hvoice.assume_init();
            out.push(VoiceInfo {
                name: cstr_to_string(voice_info_get_name(hvoice)),
                locale: cstr_to_string(voice_info_get_locale(hvoice)),
                short_name: cstr_to_string(voice_info_get_short_name(hvoice)),
                local_name: cstr_to_string(voice_info_get_local_name(hvoice)),
                style_list: cstr_to_string(voice_info_get_style_list(hvoice)),
            });
            voice_info_handle_release(hvoice);
        }
        Ok(out)
    }

    // ─── 内部：结果解析 ──────────────────────────────────────────────

    /// 解析并释放结果句柄。
    unsafe fn take_result(handle: SPXRESULTHANDLE) -> Result<SpeechSynthesisResult> {
        let parsed = Self::parse_result(handle);
        synthesizer_result_handle_release(handle);
        parsed
    }

    /// 等待异步合成完成并取结果。
    unsafe fn await_speak(&self, h_async: SPXASYNCHANDLE) -> Result<SpeechSynthesisResult> {
        let mut result_handle: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
        let ret = synthesizer_speak_async_wait_for(h_async, u32::MAX, result_handle.as_mut_ptr());
        synthesizer_async_handle_release(h_async);
        convert_err(ret, "SpeechSynthesizer: speak_async_wait_for error")?;
        Self::take_result(result_handle.assume_init())
    }

    /// 从结果句柄解析原因与音频数据（不释放句柄）。
    ///
    /// # Safety
    /// `handle` 必须是有效的合成结果句柄。
    unsafe fn parse_result(handle: SPXRESULTHANDLE) -> Result<SpeechSynthesisResult> {
        // 原因
        let mut reason_val: i32 = 0;
        let ret = synth_result_get_reason(handle, &mut reason_val);
        convert_err(ret, "SpeechSynthesizer: get_reason error")?;
        let reason = ResultReason::from(reason_val);

        // 若被取消，返回带错误码的错误
        if reason == ResultReason::Canceled {
            let mut creason: i32 = 0;
            let _ = synth_result_get_reason_canceled(handle, &mut creason);
            let mut ecode: i32 = 0;
            let _ = synth_result_get_canceled_error_code(handle, &mut ecode);
            return Err(Error::new(
                format!("合成被取消 (cancel_reason={creason}, error_code={ecode})"),
                ecode as usize,
            ));
        }

        // 音频长度与时长
        let mut audio_length: u32 = 0;
        let mut audio_duration: u64 = 0;
        let _ =
            synth_result_get_audio_length_duration(handle, &mut audio_length, &mut audio_duration);

        // 读取音频数据
        let mut audio_data = vec![0u8; audio_length as usize];
        if audio_length > 0 {
            let mut filled: u32 = 0;
            let ret = synth_result_get_audio_data(
                handle,
                audio_data.as_mut_ptr(),
                audio_length,
                &mut filled,
            );
            convert_err(ret, "SpeechSynthesizer: get_audio_data error")?;
            audio_data.truncate(filled as usize);
        }

        Ok(SpeechSynthesisResult {
            reason,
            audio_length,
            audio_duration_ticks: audio_duration,
            audio_data,
        })
    }

    // ─── 事件回调注册 ────────────────────────────────────────────────

    pub fn set_synthesis_started_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechSynthesisEventArgs) + 'static + Send,
    {
        self.callback_bag.started_cb = Some(Box::new(f));
        unsafe {
            let ret = synthesizer_started_set_callback(
                self.handle.inner(),
                Some(Self::cb_started),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "set_synthesis_started_cb error")
        }
    }

    pub fn set_synthesizing_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechSynthesisEventArgs) + 'static + Send,
    {
        self.callback_bag.synthesizing_cb = Some(Box::new(f));
        unsafe {
            let ret = synthesizer_synthesizing_set_callback(
                self.handle.inner(),
                Some(Self::cb_synthesizing),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "set_synthesizing_cb error")
        }
    }

    pub fn set_synthesis_completed_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechSynthesisEventArgs) + 'static + Send,
    {
        self.callback_bag.completed_cb = Some(Box::new(f));
        unsafe {
            let ret = synthesizer_completed_set_callback(
                self.handle.inner(),
                Some(Self::cb_completed),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "set_synthesis_completed_cb error")
        }
    }

    pub fn set_synthesis_canceled_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechSynthesisEventArgs) + 'static + Send,
    {
        self.callback_bag.canceled_cb = Some(Box::new(f));
        unsafe {
            let ret = synthesizer_canceled_set_callback(
                self.handle.inner(),
                Some(Self::cb_canceled),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "set_synthesis_canceled_cb error")
        }
    }

    pub fn set_word_boundary_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechSynthesisWordBoundaryEventArgs) + 'static + Send,
    {
        self.callback_bag.word_boundary_cb = Some(Box::new(f));
        unsafe {
            let ret = synthesizer_word_boundary_set_callback(
                self.handle.inner(),
                Some(Self::cb_word_boundary),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "set_word_boundary_cb error")
        }
    }

    pub fn set_viseme_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechSynthesisVisemeEventArgs) + 'static + Send,
    {
        self.callback_bag.viseme_cb = Some(Box::new(f));
        unsafe {
            let ret = synthesizer_viseme_received_set_callback(
                self.handle.inner(),
                Some(Self::cb_viseme),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "set_viseme_cb error")
        }
    }

    pub fn set_bookmark_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(SpeechSynthesisBookmarkEventArgs) + 'static + Send,
    {
        self.callback_bag.bookmark_cb = Some(Box::new(f));
        unsafe {
            let ret = synthesizer_bookmark_reached_set_callback(
                self.handle.inner(),
                Some(Self::cb_bookmark),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "set_bookmark_cb error")
        }
    }

    // ─── 静态回调跳板 ────────────────────────────────────────────────

    /// 从合成事件取结果，封装为事件参数。
    unsafe fn synthesis_args_from_event(hevent: SPXEVENTHANDLE) -> Option<SpeechSynthesisEventArgs> {
        let mut hresult: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
        if synthesizer_synthesis_event_get_result(hevent, hresult.as_mut_ptr()) != 0 {
            return None;
        }
        let hresult = hresult.assume_init();
        let parsed = Self::parse_result(hresult);
        synthesizer_result_handle_release(hresult);
        parsed.ok().map(|result| SpeechSynthesisEventArgs { result })
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_started(
        _h: SPXSYNTHHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.started_cb {
            if let Some(args) = Self::synthesis_args_from_event(hevent) {
                cb(args);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_synthesizing(
        _h: SPXSYNTHHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.synthesizing_cb {
            if let Some(args) = Self::synthesis_args_from_event(hevent) {
                cb(args);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_completed(
        _h: SPXSYNTHHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.completed_cb {
            if let Some(args) = Self::synthesis_args_from_event(hevent) {
                cb(args);
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_canceled(
        _h: SPXSYNTHHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.canceled_cb {
            // 取消事件下 parse_result 会判为 Canceled 返回 Err，
            // 这里直接构造一个 Canceled 空结果，便于回调感知。
            let mut hresult: MaybeUninit<SPXRESULTHANDLE> = MaybeUninit::uninit();
            if synthesizer_synthesis_event_get_result(hevent, hresult.as_mut_ptr()) == 0 {
                let hresult = hresult.assume_init();
                let mut reason_val: i32 = 0;
                let _ = synth_result_get_reason(hresult, &mut reason_val);
                let result = SpeechSynthesisResult {
                    reason: ResultReason::from(reason_val),
                    audio_length: 0,
                    audio_duration_ticks: 0,
                    audio_data: Vec::new(),
                };
                synthesizer_result_handle_release(hresult);
                cb(SpeechSynthesisEventArgs { result });
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_word_boundary(
        _h: SPXSYNTHHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.word_boundary_cb {
            let mut audio_offset: u64 = 0;
            let mut duration: u64 = 0;
            let mut text_offset: u32 = 0;
            let mut word_length: u32 = 0;
            let mut boundary_type: i32 = 0;
            if synthesizer_word_boundary_event_get_values(
                hevent,
                &mut audio_offset,
                &mut duration,
                &mut text_offset,
                &mut word_length,
                &mut boundary_type,
            ) == 0
            {
                cb(SpeechSynthesisWordBoundaryEventArgs {
                    audio_offset_ticks: audio_offset,
                    duration_ticks: duration,
                    text_offset,
                    word_length,
                    boundary_type: boundary_type as u32,
                });
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_viseme(
        _h: SPXSYNTHHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.viseme_cb {
            let mut audio_offset: u64 = 0;
            let mut viseme_id: u32 = 0;
            if synthesizer_viseme_event_get_values(hevent, &mut audio_offset, &mut viseme_id) == 0 {
                let animation = cstr_to_string(synthesizer_viseme_event_get_animation(hevent));
                cb(SpeechSynthesisVisemeEventArgs {
                    audio_offset_ticks: audio_offset,
                    viseme_id,
                    animation,
                });
            }
        }
    }

    #[allow(non_snake_case)]
    unsafe extern "C" fn cb_bookmark(
        _h: SPXSYNTHHANDLE,
        hevent: SPXEVENTHANDLE,
        pvContext: *mut c_void,
    ) {
        let bag = &*(pvContext as *const CallbackBag);
        if let Some(cb) = &bag.bookmark_cb {
            let mut audio_offset: u64 = 0;
            if synthesizer_bookmark_event_get_values(hevent, &mut audio_offset) == 0 {
                let text = cstr_to_string(synthesizer_event_get_text(hevent));
                cb(SpeechSynthesisBookmarkEventArgs {
                    audio_offset_ticks: audio_offset,
                    text,
                });
            }
        }
    }
}

impl Drop for SpeechSynthesizer {
    fn drop(&mut self) {
        trace!("SpeechSynthesizer::drop — unregistering callbacks");
        unsafe {
            let h = self.handle.inner();
            let _ = synthesizer_started_set_callback(h, None, std::ptr::null_mut());
            let _ = synthesizer_synthesizing_set_callback(h, None, std::ptr::null_mut());
            let _ = synthesizer_completed_set_callback(h, None, std::ptr::null_mut());
            let _ = synthesizer_canceled_set_callback(h, None, std::ptr::null_mut());
            let _ = synthesizer_word_boundary_set_callback(h, None, std::ptr::null_mut());
            let _ = synthesizer_viseme_received_set_callback(h, None, std::ptr::null_mut());
            let _ = synthesizer_bookmark_reached_set_callback(h, None, std::ptr::null_mut());
        }
    }
}

/// 把 C 字符串指针转为 Rust String，空指针返回空串。
unsafe fn cstr_to_string(ptr: *const std::os::raw::c_char) -> String {
    if ptr.is_null() {
        String::new()
    } else {
        CStr::from_ptr(ptr).to_string_lossy().into_owned()
    }
}
