//! AudioConfig wraps the C audio_config handle for configuring audio input.

use crate::error::{convert_err, Result};
use crate::ffi::{
    audio_config_create_audio_input_from_a_microphone,
    audio_config_create_audio_input_from_default_microphone,
    audio_config_create_audio_input_from_wav_file_name,
    audio_config_create_audio_input_from_stream,
    audio_config_create_audio_output_from_default_speaker,
    audio_config_create_audio_output_from_wav_file_name,
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

    /// Create an AudioConfig from a specific microphone by its platform device name.
    ///
    /// ⚠️ 据官方文档（how-to-select-audio-input-devices），`FromMicrophoneInput`
    /// **接受的是设备「友好名」，而不是设备 ID**：
    /// - Windows：友好名，如 `Microphone (Realtek(R) Audio)`（**不是** `{0.0.1...}` 这种端点 ID）；
    /// - Linux：ALSA 设备名，如 `hw:1,0`；
    /// - macOS：CoreAudio 设备 UID，如 `BuiltInMicrophoneDevice`。
    ///
    /// 传空串等价于默认麦克风。
    pub fn from_microphone_input(device_name: &str) -> Result<AudioConfig> {
        if device_name.trim().is_empty() {
            return Self::from_default_microphone_input();
        }
        unsafe {
            let c_name = CString::new(device_name)?;
            let mut handle: MaybeUninit<SPXAUDIOCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = audio_config_create_audio_input_from_a_microphone(
                handle.as_mut_ptr(),
                c_name.as_ptr(),
            );
            convert_err(ret, "AudioConfig::from_microphone_input error")?;
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
                stream_handle as crate::ffi::SPXAUDIOSTREAMHANDLE,
            );
            convert_err(ret, "AudioConfig::from_push_stream error")?;
            Ok(AudioConfig {
                handle: SmartHandle::create("AudioConfig", handle.assume_init(), audio_config_release),
            })
        }
    }

    /// Create an AudioConfig from a safe `PushAudioInputStream` wrapper.
    ///
    /// 注意：返回的 AudioConfig 在识别期间依赖该流存活，调用方需同时持有 `stream`。
    pub fn from_stream(stream: &crate::audio::PushAudioInputStream) -> Result<AudioConfig> {
        unsafe {
            let mut handle: MaybeUninit<SPXAUDIOCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = audio_config_create_audio_input_from_stream(
                handle.as_mut_ptr(),
                stream.handle.inner(),
            );
            convert_err(ret, "AudioConfig::from_stream error")?;
            Ok(AudioConfig {
                handle: SmartHandle::create("AudioConfig", handle.assume_init(), audio_config_release),
            })
        }
    }

    /// Create an AudioConfig that outputs synthesized audio to the default speaker.
    pub fn from_default_speaker_output() -> Result<AudioConfig> {
        unsafe {
            let mut handle: MaybeUninit<SPXAUDIOCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = audio_config_create_audio_output_from_default_speaker(handle.as_mut_ptr());
            convert_err(ret, "AudioConfig::from_default_speaker_output error")?;
            Ok(AudioConfig {
                handle: SmartHandle::create("AudioConfig", handle.assume_init(), audio_config_release),
            })
        }
    }

    /// Create an AudioConfig that writes synthesized audio to a WAV file.
    pub fn from_wav_file_output(file_name: &str) -> Result<AudioConfig> {
        unsafe {
            let c_file = CString::new(file_name)?;
            let mut handle: MaybeUninit<SPXAUDIOCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = audio_config_create_audio_output_from_wav_file_name(
                handle.as_mut_ptr(),
                c_file.as_ptr(),
            );
            convert_err(ret, "AudioConfig::from_wav_file_output error")?;
            Ok(AudioConfig {
                handle: SmartHandle::create("AudioConfig", handle.assume_init(), audio_config_release),
            })
        }
    }
}
