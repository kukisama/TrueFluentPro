use tfp_core::AudioSegment;

/// Format audio segments as SRT subtitle.
pub fn format_srt(segments: &[AudioSegment]) -> String {
    let mut out = String::new();
    for (i, s) in segments.iter().enumerate() {
        out.push_str(&format!("{}\n", i + 1));
        out.push_str(&format!(
            "{} --> {}\n",
            ms_to_srt_time(s.start_ms),
            ms_to_srt_time(s.end_ms)
        ));
        out.push_str(&format!("{}\n\n", s.text));
    }
    out
}

/// Format audio segments as WebVTT subtitle.
pub fn format_vtt(segments: &[AudioSegment]) -> String {
    let mut out = String::from("WEBVTT\n\n");
    for s in segments {
        out.push_str(&format!(
            "{} --> {}\n",
            ms_to_vtt_time(s.start_ms),
            ms_to_vtt_time(s.end_ms)
        ));
        out.push_str(&format!("{}\n\n", s.text));
    }
    out
}

/// Format plain-text transcript (one line per segment: `[Speaker] text`).
pub fn format_txt(segments: &[AudioSegment]) -> String {
    segments
        .iter()
        .map(|s| format!("[{}] {}", s.speaker, s.text))
        .collect::<Vec<_>>()
        .join("\n")
}

/// Convert milliseconds to SRT time format `HH:MM:SS,mmm`.
pub fn ms_to_srt_time(ms: i64) -> String {
    let h = ms / 3_600_000;
    let m = (ms % 3_600_000) / 60_000;
    let s = (ms % 60_000) / 1_000;
    let millis = ms % 1_000;
    format!("{:02}:{:02}:{:02},{:03}", h, m, s, millis)
}

/// Convert milliseconds to WebVTT time format `HH:MM:SS.mmm`.
pub fn ms_to_vtt_time(ms: i64) -> String {
    let h = ms / 3_600_000;
    let m = (ms % 3_600_000) / 60_000;
    let s = (ms % 60_000) / 1_000;
    let millis = ms % 1_000;
    format!("{:02}:{:02}:{:02}.{:03}", h, m, s, millis)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_seg(seq: i64, start_ms: i64, end_ms: i64, text: &str) -> AudioSegment {
        AudioSegment {
            id: format!("s{seq}"),
            transcript_id: "t1".into(),
            sequence: seq,
            speaker: "Speaker 1".into(),
            speaker_index: 0,
            start_ms,
            end_ms,
            text: text.into(),
            confidence: None,
        }
    }

    #[test]
    fn test_ms_to_srt_time_zero() {
        assert_eq!(ms_to_srt_time(0), "00:00:00,000");
    }

    #[test]
    fn test_ms_to_srt_time_complex() {
        assert_eq!(ms_to_srt_time(3661234), "01:01:01,234");
        assert_eq!(ms_to_srt_time(999), "00:00:00,999");
        assert_eq!(ms_to_srt_time(60000), "00:01:00,000");
    }

    #[test]
    fn test_ms_to_vtt_time_zero() {
        assert_eq!(ms_to_vtt_time(0), "00:00:00.000");
    }

    #[test]
    fn test_ms_to_vtt_time_complex() {
        assert_eq!(ms_to_vtt_time(3661234), "01:01:01.234");
    }

    #[test]
    fn test_format_srt_basic() {
        let segs = vec![
            make_seg(1, 0, 1500, "Hello world"),
            make_seg(2, 2000, 4000, "Second line"),
        ];
        let out = format_srt(&segs);
        assert!(out.contains("1\n"));
        assert!(out.contains("00:00:00,000 --> 00:00:01,500\n"));
        assert!(out.contains("Hello world\n"));
        assert!(out.contains("2\n"));
        assert!(out.contains("00:00:02,000 --> 00:00:04,000\n"));
        assert!(out.contains("Second line\n"));
    }

    #[test]
    fn test_format_vtt_basic() {
        let segs = vec![
            make_seg(1, 0, 1500, "Hello world"),
            make_seg(2, 2000, 4000, "Second line"),
        ];
        let out = format_vtt(&segs);
        assert!(out.starts_with("WEBVTT\n\n"));
        assert!(out.contains("00:00:00.000 --> 00:00:01.500\n"));
        assert!(out.contains("Hello world\n"));
        assert!(out.contains("00:00:02.000 --> 00:00:04.000\n"));
        assert!(out.contains("Second line\n"));
    }

    #[test]
    fn test_format_srt_empty() {
        assert_eq!(format_srt(&[]), "");
    }

    #[test]
    fn test_format_txt_basic() {
        let segs = vec![make_seg(1, 0, 1000, "Hello")];
        assert_eq!(format_txt(&segs), "[Speaker 1] Hello");
    }
}
