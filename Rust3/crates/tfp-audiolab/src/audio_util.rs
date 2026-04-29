use std::path::Path;

/// Estimate audio file duration (ms) from file size + extension.
pub fn estimate_audio_duration_ms(path: &Path, file_size_bytes: i64) -> i64 {
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();
    match ext.as_str() {
        "wav" => {
            let bytes_per_sec = 32000_i64; // 16kHz, 16-bit mono
            if file_size_bytes > 44 {
                ((file_size_bytes - 44) * 1000) / bytes_per_sec
            } else {
                0
            }
        }
        "mp3" => {
            let bytes_per_sec = 16000_i64;
            (file_size_bytes * 1000) / bytes_per_sec
        }
        _ => {
            let bytes_per_sec = 16000_i64;
            (file_size_bytes * 1000) / bytes_per_sec
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;

    #[test]
    fn test_estimate_wav() {
        assert_eq!(estimate_audio_duration_ms(Path::new("test.wav"), 32044), 1000);
        assert_eq!(estimate_audio_duration_ms(Path::new("test.wav"), 44), 0);
        assert_eq!(estimate_audio_duration_ms(Path::new("test.wav"), 20), 0);
    }

    #[test]
    fn test_estimate_mp3() {
        assert_eq!(estimate_audio_duration_ms(Path::new("test.mp3"), 16000), 1000);
    }

    #[test]
    fn test_estimate_unknown() {
        assert_eq!(estimate_audio_duration_ms(Path::new("test.flac"), 16000), 1000);
    }
}
