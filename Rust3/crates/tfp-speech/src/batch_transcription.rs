//! Batch Transcription parser — Azure Speech Batch Transcription JSON → SubtitleCue list.
//!
//! Handles recognizedPhrases with ticks-based timing and ISO 8601 duration offsets.

use tfp_core::{BatchSubtitleSplitOptions, SubtitleCue};

/// Parse Azure Batch Transcription JSON (recognizedPhrases format).
pub fn parse_batch_transcription(
    json_str: &str,
    options: &BatchSubtitleSplitOptions,
) -> Vec<SubtitleCue> {
    let json: serde_json::Value = match serde_json::from_str(json_str) {
        Ok(v) => v,
        Err(_) => return vec![],
    };

    let phrases = match json["recognizedPhrases"].as_array() {
        Some(arr) => arr,
        None => return vec![],
    };

    let mut cues = Vec::new();

    for phrase in phrases {
        let speaker = phrase["speaker"]
            .as_u64()
            .map(|s| format!("Speaker {s}"));

        let channel = phrase["channel"].as_u64();
        let speaker_label = speaker.or_else(|| channel.map(|c| format!("Channel {c}")));

        let best = phrase["nBest"]
            .as_array()
            .and_then(|arr| arr.first());

        let best_obj = match best {
            Some(b) => b,
            None => continue,
        };

        let display_text = best_obj["display"].as_str().unwrap_or("");
        if display_text.trim().is_empty() {
            continue;
        }

        let offset_ms = parse_offset_ms(phrase);
        let duration_ms = parse_duration_ms(phrase);

        if let Some(words) = best_obj["words"].as_array() {
            if !words.is_empty() && options.enable_sentence_split {
                let mut sub_cues = split_words_to_cues(words, speaker_label.as_deref(), options);
                cues.append(&mut sub_cues);
                continue;
            }
        }

        cues.push(SubtitleCue {
            start_ms: offset_ms,
            end_ms: offset_ms + duration_ms,
            text: display_text.to_string(),
            speaker: speaker_label,
        });
    }

    cues.sort_by_key(|c| c.start_ms);
    cues
}

/// Split word array into cues, each word has "word", "offset" (ISO), "duration" (ISO) or ticks.
fn split_words_to_cues(
    words: &[serde_json::Value],
    speaker: Option<&str>,
    options: &BatchSubtitleSplitOptions,
) -> Vec<SubtitleCue> {
    let mut cues = Vec::new();
    let mut current_text = String::new();
    let mut segment_start_ms: i64 = 0;
    let mut segment_end_ms: i64;
    let mut started = false;
    let mut char_count: u32 = 0;

    for (i, word) in words.iter().enumerate() {
        let w_text = word["word"].as_str()
            .or_else(|| word["text"].as_str())
            .unwrap_or("");
        let w_offset = word_offset_ms(word);
        let w_duration = word_duration_ms(word);
        let w_end = w_offset + w_duration;

        if !started {
            segment_start_ms = w_offset;
            started = true;
        }

        if !current_text.is_empty() {
            current_text.push(' ');
        }
        current_text.push_str(w_text);
        segment_end_ms = w_end;
        char_count += w_text.chars().count() as u32;

        let should_split = {
            let exceeds_chars = char_count >= options.max_chars;
            let exceeds_duration = (segment_end_ms - segment_start_ms) as f64 / 1000.0 >= options.max_duration_seconds;
            let sentence_break = options.enable_sentence_split && w_text.ends_with(|c: char| matches!(c, '.' | '!' | '?' | '。' | '！' | '？'));
            let pause_split = if i + 1 < words.len() {
                let next_offset = word_offset_ms(&words[i + 1]);
                let gap = next_offset - w_end;
                gap >= options.pause_split_ms as i64
            } else {
                false
            };

            exceeds_chars || exceeds_duration || sentence_break || pause_split
        };

        if should_split || i == words.len() - 1 {
            if !current_text.trim().is_empty() {
                cues.push(SubtitleCue {
                    start_ms: segment_start_ms,
                    end_ms: segment_end_ms,
                    text: current_text.trim().to_string(),
                    speaker: speaker.map(|s| s.to_string()),
                });
            }
            current_text.clear();
            char_count = 0;
            started = false;
        }
    }

    cues
}

/// Parse phrase-level offset in ms from ticks or ISO duration.
fn parse_offset_ms(phrase: &serde_json::Value) -> i64 {
    // Try ticks first (1 tick = 100 nanoseconds)
    if let Some(ticks) = phrase["offsetInTicks"].as_i64() {
        return ticks / 10_000;
    }
    // Try ISO 8601 duration string
    if let Some(offset_str) = phrase["offset"].as_str() {
        return parse_iso_duration_ms(offset_str);
    }
    if let Some(ms) = phrase["offsetMilliseconds"].as_i64() {
        return ms;
    }
    0
}

/// Parse phrase-level duration in ms from ticks or ISO duration.
fn parse_duration_ms(phrase: &serde_json::Value) -> i64 {
    if let Some(ticks) = phrase["durationInTicks"].as_i64() {
        return ticks / 10_000;
    }
    if let Some(dur_str) = phrase["duration"].as_str() {
        return parse_iso_duration_ms(dur_str);
    }
    if let Some(ms) = phrase["durationMilliseconds"].as_i64() {
        return ms;
    }
    3000
}

/// Parse ISO 8601 duration like "PT1.23S" or "PT1M2.5S" or "PT1H2M3.456S" to ms.
pub fn parse_iso_duration_ms(s: &str) -> i64 {
    let s = s.trim();
    if !s.starts_with("PT") {
        return 0;
    }
    let body = &s[2..];
    let mut total_ms: f64 = 0.0;
    let mut num_str = String::new();

    for c in body.chars() {
        match c {
            'H' | 'h' => {
                if let Ok(h) = num_str.parse::<f64>() {
                    total_ms += h * 3_600_000.0;
                }
                num_str.clear();
            }
            'M' | 'm' => {
                if let Ok(m) = num_str.parse::<f64>() {
                    total_ms += m * 60_000.0;
                }
                num_str.clear();
            }
            'S' | 's' => {
                if let Ok(sec) = num_str.parse::<f64>() {
                    total_ms += sec * 1_000.0;
                }
                num_str.clear();
            }
            _ => {
                num_str.push(c);
            }
        }
    }

    total_ms.round() as i64
}

/// Get word offset in ms from ticks, ISO, or milliseconds fields.
fn word_offset_ms(word: &serde_json::Value) -> i64 {
    if let Some(ticks) = word["offsetInTicks"].as_i64() {
        return ticks / 10_000;
    }
    if let Some(s) = word["offset"].as_str() {
        return parse_iso_duration_ms(s);
    }
    if let Some(ms) = word["offsetMilliseconds"].as_i64() {
        return ms;
    }
    0
}

/// Get word duration in ms from ticks, ISO, or milliseconds fields.
fn word_duration_ms(word: &serde_json::Value) -> i64 {
    if let Some(ticks) = word["durationInTicks"].as_i64() {
        return ticks / 10_000;
    }
    if let Some(s) = word["duration"].as_str() {
        return parse_iso_duration_ms(s);
    }
    if let Some(ms) = word["durationMilliseconds"].as_i64() {
        return ms;
    }
    500
}

#[cfg(test)]
mod tests {
    use super::*;

    fn default_options() -> BatchSubtitleSplitOptions {
        BatchSubtitleSplitOptions::default()
    }

    #[test]
    fn test_parse_iso_duration() {
        assert_eq!(parse_iso_duration_ms("PT1.5S"), 1500);
        assert_eq!(parse_iso_duration_ms("PT1M2.5S"), 62500);
        assert_eq!(parse_iso_duration_ms("PT1H2M3.456S"), 3723456);
        assert_eq!(parse_iso_duration_ms("PT0S"), 0);
    }

    #[test]
    fn test_parse_empty_json() {
        let cues = parse_batch_transcription("{}", &default_options());
        assert!(cues.is_empty());
    }

    #[test]
    fn test_parse_ticks_format() {
        let json = r#"{
            "recognizedPhrases": [{
                "offsetInTicks": 50000000,
                "durationInTicks": 30000000,
                "speaker": 1,
                "nBest": [{
                    "display": "Hello world",
                    "confidence": 0.95
                }]
            }]
        }"#;
        let cues = parse_batch_transcription(json, &default_options());
        assert_eq!(cues.len(), 1);
        assert_eq!(cues[0].start_ms, 5000);
        assert_eq!(cues[0].end_ms, 8000);
        assert_eq!(cues[0].speaker, Some("Speaker 1".to_string()));
        assert_eq!(cues[0].text, "Hello world");
    }

    #[test]
    fn test_parse_iso_offset_format() {
        let json = r#"{
            "recognizedPhrases": [{
                "offset": "PT5S",
                "duration": "PT3S",
                "nBest": [{
                    "display": "Test phrase"
                }]
            }]
        }"#;
        let cues = parse_batch_transcription(json, &default_options());
        assert_eq!(cues.len(), 1);
        assert_eq!(cues[0].start_ms, 5000);
        assert_eq!(cues[0].end_ms, 8000);
    }

    #[test]
    fn test_parse_with_words() {
        let json = r#"{
            "recognizedPhrases": [{
                "offsetInTicks": 0,
                "durationInTicks": 50000000,
                "nBest": [{
                    "display": "Hello world foo bar",
                    "words": [
                        {"word": "Hello", "offsetInTicks": 0, "durationInTicks": 5000000},
                        {"word": "world.", "offsetInTicks": 5000000, "durationInTicks": 5000000},
                        {"word": "foo", "offsetInTicks": 20000000, "durationInTicks": 5000000},
                        {"word": "bar.", "offsetInTicks": 25000000, "durationInTicks": 5000000}
                    ]
                }]
            }]
        }"#;
        let cues = parse_batch_transcription(json, &default_options());
        // word "world." ends with '.', triggers sentence split
        assert!(cues.len() >= 2, "expected >= 2 cues, got {}", cues.len());
    }

    #[test]
    fn test_channel_fallback() {
        let json = r#"{
            "recognizedPhrases": [{
                "offsetInTicks": 0,
                "durationInTicks": 10000000,
                "channel": 2,
                "nBest": [{"display": "From channel 2"}]
            }]
        }"#;
        let cues = parse_batch_transcription(json, &default_options());
        assert_eq!(cues[0].speaker, Some("Channel 2".to_string()));
    }
}
