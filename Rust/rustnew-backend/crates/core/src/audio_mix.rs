//! PCM16 混音与增益（纯算法，平台无关，可单测）。
//!
//! 移植自 C# `Services/Audio/Pcm16AudioMixer.cs`：
//! - `mix_mono`：两路 16-bit 小端 PCM 样本相加，带防削顶（饱和到 i16 范围）；
//! - `apply_gain_in_place`：对一路 PCM 施加线性增益。
//!
//! 设计上保持「纯字节处理」，不依赖任何采集/编码实现，便于在不同平台、
//! 不同采集后端之间复用，也便于单元测试。

/// 读取 `buf` 中第 `byte_index` 字节起的一个 16-bit 小端样本。
/// 越界时返回 0（视为静音），与 C# 行为一致。
#[inline]
fn read_sample(buf: &[u8], byte_index: usize) -> i32 {
    if byte_index + 1 < buf.len() {
        i16::from_le_bytes([buf[byte_index], buf[byte_index + 1]]) as i32
    } else {
        0
    }
}

/// 将两路 16-bit 小端单声道 PCM 相加下混为一路（防削顶）。
///
/// 输出长度为两者较长者（偶数对齐）；较短一路缺失部分按静音处理。
/// 对齐 C# `Pcm16AudioMixer.MixMono`。
pub fn mix_mono(first: &[u8], second: &[u8]) -> Vec<u8> {
    let mut length = first.len().max(second.len());
    if length == 0 {
        return Vec::new();
    }
    if length & 1 != 0 {
        length -= 1;
    }

    let mut result = vec![0u8; length];
    let mut i = 0;
    while i + 1 < length {
        let a = read_sample(first, i);
        let b = read_sample(second, i);
        let mixed = (a + b).clamp(i16::MIN as i32, i16::MAX as i32) as i16;
        let bytes = mixed.to_le_bytes();
        result[i] = bytes[0];
        result[i + 1] = bytes[1];
        i += 2;
    }
    result
}

/// 对一路 16-bit 小端 PCM 原地施加线性增益。
///
/// - `gain == 1.0`（近似）时不做处理；
/// - `gain <= 0`（近似）时整段清零；
/// - 其余情况逐样本乘以增益并防削顶。
///
/// 对齐 C# `Pcm16AudioMixer.ApplyGainInPlace`。
pub fn apply_gain_in_place(pcm16: &mut [u8], gain: f32) {
    if pcm16.len() < 2 || (gain - 1.0).abs() < 0.0001 {
        return;
    }
    if gain <= 0.0001 {
        for b in pcm16.iter_mut() {
            *b = 0;
        }
        return;
    }

    let mut i = 0;
    while i + 1 < pcm16.len() {
        let sample = i16::from_le_bytes([pcm16[i], pcm16[i + 1]]) as f32;
        let scaled = (sample * gain).clamp(i16::MIN as f32, i16::MAX as f32) as i16;
        let bytes = scaled.to_le_bytes();
        pcm16[i] = bytes[0];
        pcm16[i + 1] = bytes[1];
        i += 2;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pcm(samples: &[i16]) -> Vec<u8> {
        let mut v = Vec::with_capacity(samples.len() * 2);
        for s in samples {
            v.extend_from_slice(&s.to_le_bytes());
        }
        v
    }

    fn samples(bytes: &[u8]) -> Vec<i16> {
        bytes
            .chunks_exact(2)
            .map(|c| i16::from_le_bytes([c[0], c[1]]))
            .collect()
    }

    #[test]
    fn mix_mono_adds_samples() {
        let a = pcm(&[100, -200, 300]);
        let b = pcm(&[50, 200, -100]);
        let out = mix_mono(&a, &b);
        assert_eq!(samples(&out), vec![150, 0, 200]);
    }

    #[test]
    fn mix_mono_saturates_on_overflow() {
        let a = pcm(&[30000]);
        let b = pcm(&[10000]);
        let out = mix_mono(&a, &b);
        assert_eq!(samples(&out), vec![i16::MAX]); // 40000 -> 削顶到 32767
    }

    #[test]
    fn mix_mono_handles_uneven_lengths() {
        let a = pcm(&[1000, 2000]);
        let b = pcm(&[500]);
        let out = mix_mono(&a, &b);
        // 第二路缺失样本按静音
        assert_eq!(samples(&out), vec![1500, 2000]);
    }

    #[test]
    fn apply_gain_doubles_samples() {
        let mut p = pcm(&[100, -200]);
        apply_gain_in_place(&mut p, 2.0);
        assert_eq!(samples(&p), vec![200, -400]);
    }

    #[test]
    fn apply_gain_unity_is_noop() {
        let mut p = pcm(&[123, -456]);
        apply_gain_in_place(&mut p, 1.0);
        assert_eq!(samples(&p), vec![123, -456]);
    }

    #[test]
    fn apply_gain_zero_silences() {
        let mut p = pcm(&[123, -456]);
        apply_gain_in_place(&mut p, 0.0);
        assert_eq!(samples(&p), vec![0, 0]);
    }

    #[test]
    fn apply_gain_saturates() {
        let mut p = pcm(&[20000]);
        apply_gain_in_place(&mut p, 4.0);
        assert_eq!(samples(&p), vec![i16::MAX]);
    }
}
