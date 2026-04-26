//! AudioConfig wraps the C audio_config handle for configuring audio input.

use crate::error::{convert_err, Result};
use crate::ffi::{
    audio_config_create_audio_input_from_default_microphone,
    audio_config_create_audio_input_from_wav_file_name,
    audio_config_create_audio_input_from_stream,
    audio_config_release,
    SmartHandle, SPXAUDIOCONFIGHANDLE,
};
use std::ffi::CString;
use std::mem::MaybeUninit;

/// Audio input configuration for speech recognizers.
#[derive(Debug)]
pub struct AudioConfig {
    pub(crate) handle: SmartHandle<SPXAUDIOCONFIGHANDLE>,
}

impl AudioConfig {
    /// Create an AudioConfig from the default system microphone.
    pub fn from_default_microphone_input() -> Result<AudioConfig> {
        unsafe {
            let mut handle: MaybeUninit<SPXAUDIOCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = audio_config_create_audio_input_from_default_microphone(handle.as_mut_ptr());
            convert_err(ret, "AudioConfig::from_default_microphone_input error")?;
            Ok(AudioConfig {
                handle: SmartHandle::create("AudioConfig", handle.assume_init(), audio_config_release),
            })
        }
    }

    /// Create an AudioConfig from a WAV file path.
    pub fn from_wav_file_input(file_name: &str) -> Result<AudioConfig> {
        unsafe {
            let c_file = CString::new(file_name)?;
            let mut handle: MaybeUninit<SPXAUDIOCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = audio_config_create_audio_input_from_wav_file_name(
                handle.as_mut_ptr(),
                c_file.as_ptr(),
            );
            convert_err(ret, "AudioConfig::from_wav_file_input error")?;
            Ok(AudioConfig {
                handle: SmartHandle::create("AudioConfig", handle.assume_init(), audio_config_release),
            })
        }
    }

    /// Create an AudioConfig from a push audio input stream handle.
    ///
    /// # Safety
    /// `stream_handle` must be a valid push stream handle.
    pub unsafe fn from_push_stream(stream_handle: usize) -> Result<AudioConfig> {
        unsafe {
            let mut handle: MaybeUninit<SPXAUDIOCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = audio_config_create_audio_input_from_stream(
                handle.as_mut_ptr(),
                stream_handle,
            );
            convert_err(ret, "AudioConfig::from_push_stream error")?;
            Ok(AudioConfig {
                handle: SmartHandle::create("AudioConfig", handle.assume_init(), audio_config_release),
            })
        }
    }
}
