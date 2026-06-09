//! 音频流封装：PushAudioInputStream + AudioStreamFormat。
//!
//! 用于「推流识别」场景：调用方把裸 PCM 字节分块写入流，识别器据此识别。
//! 典型用法：
//! ```ignore
//! let fmt = AudioStreamFormat::from_pcm(16000, 16, 1)?;
//! let stream = PushAudioInputStream::create(&fmt)?;
//! let audio = AudioConfig::from_stream(&stream)?;
//! // ... 创建识别器，另起线程 stream.write(chunk) / stream.close()
//! ```

use crate::error::{convert_err, Result};
use crate::ffi::{
    audio_stream_create_push_audio_input_stream, audio_stream_format_create_from_waveformat_pcm,
    audio_stream_format_release, audio_stream_release, push_audio_input_stream_close,
    push_audio_input_stream_write, SmartHandle, SPXAUDIOSTREAMFORMATHANDLE, SPXAUDIOSTREAMHANDLE,
};
use std::mem::MaybeUninit;

/// PCM 音频格式描述（用于 push/pull 流）。
#[derive(Debug)]
pub struct AudioStreamFormat {
    pub(crate) handle: SmartHandle<SPXAUDIOSTREAMFORMATHANDLE>,
}

impl AudioStreamFormat {
    /// 创建 PCM 格式。常用：16000 Hz / 16 bit / 单声道。
    pub fn from_pcm(samples_per_sec: u32, bits_per_sample: u8, channels: u8) -> Result<Self> {
        unsafe {
            let mut handle: MaybeUninit<SPXAUDIOSTREAMFORMATHANDLE> = MaybeUninit::uninit();
            let ret = audio_stream_format_create_from_waveformat_pcm(
                handle.as_mut_ptr(),
                samples_per_sec,
                bits_per_sample,
                channels,
            );
            convert_err(ret, "AudioStreamFormat::from_pcm error")?;
            Ok(AudioStreamFormat {
                handle: SmartHandle::create(
                    "AudioStreamFormat",
                    handle.assume_init(),
                    audio_stream_format_release,
                ),
            })
        }
    }
}

/// 推送式音频输入流：调用方主动写入 PCM 字节。
#[derive(Debug)]
pub struct PushAudioInputStream {
    pub(crate) handle: SmartHandle<SPXAUDIOSTREAMHANDLE>,
}

impl PushAudioInputStream {
    /// 以指定格式创建推流。
    pub fn create(format: &AudioStreamFormat) -> Result<Self> {
        unsafe {
            let mut handle: MaybeUninit<SPXAUDIOSTREAMHANDLE> = MaybeUninit::uninit();
            let ret = audio_stream_create_push_audio_input_stream(
                handle.as_mut_ptr(),
                format.handle.inner(),
            );
            convert_err(ret, "PushAudioInputStream::create error")?;
            Ok(PushAudioInputStream {
                handle: SmartHandle::create(
                    "PushAudioInputStream",
                    handle.assume_init(),
                    audio_stream_release,
                ),
            })
        }
    }

    /// 写入一块 PCM 字节。
    pub fn write(&self, data: &[u8]) -> Result<()> {
        unsafe {
            // C API 的 buffer 形参是 *mut u8，但仅作读取拷贝，不会改写调用方数据。
            let ret = push_audio_input_stream_write(
                self.handle.inner(),
                data.as_ptr() as *mut u8,
                data.len() as u32,
            );
            convert_err(ret, "PushAudioInputStream::write error")
        }
    }

    /// 关闭流，告知识别器没有更多数据。
    pub fn close(&self) -> Result<()> {
        unsafe {
            let ret = push_audio_input_stream_close(self.handle.inner());
            convert_err(ret, "PushAudioInputStream::close error")
        }
    }
}
