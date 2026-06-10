//! 本地录音机：把交错 16-bit PCM 写成 WAV（无损）或 MP3（有损）文件。
//!
//! 设计目标（避免巨型类、便于跨平台/替换实现）：
//! - 用 [`SampleEncoder`] trait 抽象「编码 + 落盘」，调用方只面向 trait；
//! - [`WavEncoder`] / [`Mp3Encoder`] 是两个独立实现，互不依赖；
//! - 平台采集层（WASAPI / 未来 ScreenCaptureKit 等）只需把 PCM 喂给 trait，
//!   不关心容器格式。
//!
//! 对齐 C# `Services/Audio/HighQualityRecorder.cs`（WASAPI 原格式抓 → LAME MP3），
//! 但把「编码落盘」与「平台采集」解耦：本模块只负责编码落盘，平台无关。

use std::fs::File;
use std::io::{BufWriter, Seek, SeekFrom, Write};
use std::path::Path;

use mp3lame_encoder::{Bitrate, Builder, DualPcm, FlushNoGap, MonoPcm, Quality};
use thiserror::Error;

use crate::audio::{AudioFormat, RecorderSettings, RecordingContainer};

/// 录音错误。
#[derive(Debug, Error)]
pub enum RecorderError {
    #[error("文件 IO 失败：{0}")]
    Io(#[from] std::io::Error),
    #[error("不支持的录音格式：{0}")]
    UnsupportedFormat(String),
    #[error("MP3 编码器初始化失败：{0}")]
    Mp3Init(String),
    #[error("MP3 编码失败：{0}")]
    Mp3Encode(String),
}

/// 把交错 16-bit 小端 PCM 编码并落盘的抽象。
///
/// 调用约定：多次 [`write_pcm`](SampleEncoder::write_pcm) 追加数据，
/// 最后调用一次 [`finalize`](SampleEncoder::finalize) 收尾（补 WAV 头 / flush MP3 尾）。
pub trait SampleEncoder: Send {
    /// 追加一段交错的 16-bit 小端 PCM。
    fn write_pcm(&mut self, pcm16le: &[u8]) -> Result<(), RecorderError>;
    /// 收尾并刷新到磁盘。
    fn finalize(&mut self) -> Result<(), RecorderError>;
}

/// 按录音设置在 `path` 上打开一个编码器。
pub fn open_recorder(
    path: &Path,
    settings: &RecorderSettings,
) -> Result<Box<dyn SampleEncoder>, RecorderError> {
    match settings.container {
        RecordingContainer::Wav => Ok(Box::new(WavEncoder::create(path, settings.format)?)),
        RecordingContainer::Mp3 => Ok(Box::new(Mp3Encoder::create(
            path,
            settings.format,
            settings.mp3_bitrate_kbps,
        )?)),
    }
}

// ───────────────────────── WAV ─────────────────────────

/// WAV（PCM）编码器：写 44 字节头 + 原始 PCM，收尾时回填长度字段。
pub struct WavEncoder {
    writer: BufWriter<File>,
    data_bytes: u64,
    finalized: bool,
}

impl WavEncoder {
    /// 创建文件并写入占位头。
    pub fn create(path: &Path, format: AudioFormat) -> Result<Self, RecorderError> {
        if format.bits_per_sample != 16 {
            return Err(RecorderError::UnsupportedFormat(format!(
                "WAV 当前仅支持 16-bit，收到 {}-bit",
                format.bits_per_sample
            )));
        }
        let file = File::create(path)?;
        let mut writer = BufWriter::new(file);
        Self::write_header(&mut writer, format, 0)?;
        Ok(Self {
            writer,
            data_bytes: 0,
            finalized: false,
        })
    }

    fn write_header(
        writer: &mut BufWriter<File>,
        format: AudioFormat,
        data_len: u32,
    ) -> Result<(), RecorderError> {
        let channels = format.channels;
        let sample_rate = format.sample_rate;
        let bits = format.bits_per_sample;
        let block_align = channels * (bits / 8);
        let byte_rate = sample_rate * block_align as u32;

        writer.write_all(b"RIFF")?;
        writer.write_all(&(36 + data_len).to_le_bytes())?;
        writer.write_all(b"WAVE")?;
        writer.write_all(b"fmt ")?;
        writer.write_all(&16u32.to_le_bytes())?; // fmt chunk size
        writer.write_all(&1u16.to_le_bytes())?; // PCM
        writer.write_all(&channels.to_le_bytes())?;
        writer.write_all(&sample_rate.to_le_bytes())?;
        writer.write_all(&byte_rate.to_le_bytes())?;
        writer.write_all(&block_align.to_le_bytes())?;
        writer.write_all(&bits.to_le_bytes())?;
        writer.write_all(b"data")?;
        writer.write_all(&data_len.to_le_bytes())?;
        Ok(())
    }
}

impl SampleEncoder for WavEncoder {
    fn write_pcm(&mut self, pcm16le: &[u8]) -> Result<(), RecorderError> {
        self.writer.write_all(pcm16le)?;
        self.data_bytes += pcm16le.len() as u64;
        Ok(())
    }

    fn finalize(&mut self) -> Result<(), RecorderError> {
        if self.finalized {
            return Ok(());
        }
        self.finalized = true;
        let data_len = self.data_bytes.min(u32::MAX as u64) as u32;
        // 回填 RIFF chunk 大小（offset 4）与 data chunk 大小（offset 40）。
        self.writer.seek(SeekFrom::Start(4))?;
        self.writer.write_all(&(36 + data_len).to_le_bytes())?;
        self.writer.seek(SeekFrom::Start(40))?;
        self.writer.write_all(&data_len.to_le_bytes())?;
        self.writer.flush()?;
        Ok(())
    }
}

impl Drop for WavEncoder {
    fn drop(&mut self) {
        let _ = self.finalize();
    }
}

// ───────────────────────── MP3 ─────────────────────────

/// MP3 编码器（LAME，CBR）。支持单/双声道。
pub struct Mp3Encoder {
    encoder: mp3lame_encoder::Encoder,
    writer: BufWriter<File>,
    channels: u16,
    finalized: bool,
}

impl Mp3Encoder {
    /// 创建文件并初始化 LAME 编码器。
    pub fn create(
        path: &Path,
        format: AudioFormat,
        bitrate_kbps: u32,
    ) -> Result<Self, RecorderError> {
        if format.bits_per_sample != 16 {
            return Err(RecorderError::UnsupportedFormat(format!(
                "MP3 当前仅支持 16-bit，收到 {}-bit",
                format.bits_per_sample
            )));
        }
        if format.channels != 1 && format.channels != 2 {
            return Err(RecorderError::UnsupportedFormat(format!(
                "MP3 仅支持 1 或 2 声道，收到 {}",
                format.channels
            )));
        }

        let mut builder = Builder::new().ok_or_else(|| {
            RecorderError::Mp3Init("无法创建 LAME builder".to_string())
        })?;
        builder
            .set_num_channels(format.channels as u8)
            .map_err(|e| RecorderError::Mp3Init(format!("设置声道失败：{e:?}")))?;
        builder
            .set_sample_rate(format.sample_rate)
            .map_err(|e| RecorderError::Mp3Init(format!("设置采样率失败：{e:?}")))?;
        builder
            .set_brate(map_bitrate(bitrate_kbps))
            .map_err(|e| RecorderError::Mp3Init(format!("设置码率失败：{e:?}")))?;
        builder
            .set_quality(Quality::Good)
            .map_err(|e| RecorderError::Mp3Init(format!("设置质量失败：{e:?}")))?;
        let encoder = builder
            .build()
            .map_err(|e| RecorderError::Mp3Init(format!("LAME 初始化失败：{e:?}")))?;

        let file = File::create(path)?;
        Ok(Self {
            encoder,
            writer: BufWriter::new(file),
            channels: format.channels,
            finalized: false,
        })
    }

    fn flush_buffer(&mut self, encoded: &[u8]) -> Result<(), RecorderError> {
        self.writer.write_all(encoded)?;
        Ok(())
    }
}

impl SampleEncoder for Mp3Encoder {
    fn write_pcm(&mut self, pcm16le: &[u8]) -> Result<(), RecorderError> {
        if pcm16le.len() < 2 {
            return Ok(());
        }
        // 字节 → i16 样本（交错）。
        let sample_count = pcm16le.len() / 2;
        let mut samples = Vec::with_capacity(sample_count);
        for c in pcm16le.chunks_exact(2) {
            samples.push(i16::from_le_bytes([c[0], c[1]]));
        }

        let mut out = Vec::new();
        let encoded_size = if self.channels == 2 {
            let frames = samples.len() / 2;
            let mut left = Vec::with_capacity(frames);
            let mut right = Vec::with_capacity(frames);
            for f in samples.chunks_exact(2) {
                left.push(f[0]);
                right.push(f[1]);
            }
            out.reserve(mp3lame_encoder::max_required_buffer_size(left.len()));
            let input = DualPcm {
                left: &left,
                right: &right,
            };
            self.encoder
                .encode(input, out.spare_capacity_mut())
                .map_err(|e| RecorderError::Mp3Encode(format!("{e:?}")))?
        } else {
            out.reserve(mp3lame_encoder::max_required_buffer_size(samples.len()));
            let input = MonoPcm(&samples);
            self.encoder
                .encode(input, out.spare_capacity_mut())
                .map_err(|e| RecorderError::Mp3Encode(format!("{e:?}")))?
        };
        unsafe {
            out.set_len(encoded_size);
        }
        self.flush_buffer(&out)?;
        Ok(())
    }

    fn finalize(&mut self) -> Result<(), RecorderError> {
        if self.finalized {
            return Ok(());
        }
        self.finalized = true;
        let mut out = Vec::new();
        out.reserve(mp3lame_encoder::max_required_buffer_size(0).max(8192));
        let encoded_size = self
            .encoder
            .flush::<FlushNoGap>(out.spare_capacity_mut())
            .map_err(|e| RecorderError::Mp3Encode(format!("flush 失败：{e:?}")))?;
        unsafe {
            out.set_len(encoded_size);
        }
        self.writer.write_all(&out)?;
        self.writer.flush()?;
        Ok(())
    }
}

impl Drop for Mp3Encoder {
    fn drop(&mut self) {
        let _ = self.finalize();
    }
}

/// 把 kbps 数值映射到最接近的 LAME CBR 档位。
fn map_bitrate(kbps: u32) -> Bitrate {
    const TABLE: &[(u32, Bitrate)] = &[
        (8, Bitrate::Kbps8),
        (16, Bitrate::Kbps16),
        (24, Bitrate::Kbps24),
        (32, Bitrate::Kbps32),
        (40, Bitrate::Kbps40),
        (48, Bitrate::Kbps48),
        (64, Bitrate::Kbps64),
        (80, Bitrate::Kbps80),
        (96, Bitrate::Kbps96),
        (112, Bitrate::Kbps112),
        (128, Bitrate::Kbps128),
        (160, Bitrate::Kbps160),
        (192, Bitrate::Kbps192),
        (224, Bitrate::Kbps224),
        (256, Bitrate::Kbps256),
        (320, Bitrate::Kbps320),
    ];
    let mut best = TABLE[0];
    let mut best_diff = u32::MAX;
    for &(rate, br) in TABLE {
        let diff = rate.abs_diff(kbps);
        if diff < best_diff {
            best_diff = diff;
            best = (rate, br);
        }
    }
    best.1
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::audio::AudioFormat;

    fn tmp_path(name: &str) -> std::path::PathBuf {
        let mut p = std::env::temp_dir();
        p.push(format!("tfp_rec_test_{name}"));
        p
    }

    #[test]
    fn wav_header_and_data_sizes_are_patched() {
        let path = tmp_path("a.wav");
        let fmt = AudioFormat {
            sample_rate: 16_000,
            bits_per_sample: 16,
            channels: 1,
        };
        {
            let mut enc = WavEncoder::create(&path, fmt).unwrap();
            // 写 100 个样本 = 200 字节
            let pcm = vec![1u8; 200];
            enc.write_pcm(&pcm).unwrap();
            enc.finalize().unwrap();
        }
        let bytes = std::fs::read(&path).unwrap();
        assert_eq!(&bytes[0..4], b"RIFF");
        assert_eq!(&bytes[8..12], b"WAVE");
        // data 大小（offset 40）应为 200
        let data_len = u32::from_le_bytes([bytes[40], bytes[41], bytes[42], bytes[43]]);
        assert_eq!(data_len, 200);
        // RIFF 大小（offset 4）应为 36 + 200
        let riff_len = u32::from_le_bytes([bytes[4], bytes[5], bytes[6], bytes[7]]);
        assert_eq!(riff_len, 236);
        assert_eq!(bytes.len(), 44 + 200);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn mp3_produces_nonempty_output() {
        let path = tmp_path("b.mp3");
        let fmt = AudioFormat {
            sample_rate: 16_000,
            bits_per_sample: 16,
            channels: 1,
        };
        {
            let mut enc = Mp3Encoder::create(&path, fmt, 128).unwrap();
            // 1 秒静音（16000 样本 * 2 字节）
            let pcm = vec![0u8; 16_000 * 2];
            enc.write_pcm(&pcm).unwrap();
            enc.finalize().unwrap();
        }
        let meta = std::fs::metadata(&path).unwrap();
        assert!(meta.len() > 0, "MP3 文件不应为空");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn map_bitrate_picks_nearest() {
        assert!(matches!(map_bitrate(256), Bitrate::Kbps256));
        assert!(matches!(map_bitrate(130), Bitrate::Kbps128));
        assert!(matches!(map_bitrate(1000), Bitrate::Kbps320));
    }
}
