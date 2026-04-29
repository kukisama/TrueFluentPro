//! Fast Transcription parser — Azure Speech Fast Transcription API response → SubtitleCue list.
//!
//! Aligned with C# FastTranscriptionParser: phrase-level + word-level splitting + cross-phrase merging.

use tfp_core::{BatchSubtitleSplitOptions, SubtitleCue};

/// Parse Azure Speech Fast Transcription JSON response into subtitle cues.
pub fn parse_fast_transcription(
    json_str: &str,
    options: &BatchSubtitleSplitOptions,
) -> Vec<SubtitleCue> {
    let json: serde_json::Value = match serde_json::from_str(json_str) {
        Ok(v) => v,
        Err(_) => return vec![],
    };

    let phrases = match json["phrases"].as_array() {
        Some(arr) => arr,
        None => return vec![],
    };

    let mut cues = Vec::new();

    for phrase in phrases {
        let text = phrase["text"].as_str().unwrap_or("").to_string();
        if text.trim().is_empty() {
            continue;
        }

        let speaker = phrase["speaker"]
            .as_u64()
            .map(|s| format!("Speaker {s}"));

        let offset_ms = phrase["offsetMilliseconds"].as_i64().unwrap_or(0);
        let duration_ms = phrase["durationMilliseconds"].as_i64().unwrap_or(3000);

        if let Some(words) = phrase["words"].as_array() {
            if !words.is_empty() && options.enable_sentence_split {
                let mut sub_cues = split_phrase_to_cues(
                    &text,
                    words,
                    speaker.as_deref(),
                    options,
                );
                cues.append(&mut sub_cues);
                continue;
            }
        }

        cues.push(SubtitleCue {
            start_ms: offset_ms,
            end_ms: offset_ms + duration_ms,
            text,
            speaker,
        });
    }

    cues.sort_by_key(|c| c.start_ms);
    merge_adjacent_cues(&mut cues, options);
    cues
}

/// Split a phrase into subtitle cues using word-level timing.
fn split_phrase_to_cues(
    _display_text: &str,
    words: &[serde_json::Value],
    speaker: Option<&str>,
    options: &BatchSubtitleSplitOptions,
) -> Vec<SubtitleCue> {
    let mut cues = Vec::new();
    let mut current_text = String::new();
    let mut segment_start: Option<i64> = None;
    let mut segment_end: i64;
    let mut char_count: u32 = 0;
    let mut segment_start_ms: i64 = 0;

    for (i, word) in words.iter().enumerate() {
        let w_text = word["text"].as_str().unwrap_or("");
        let w_offset = word["offsetMilliseconds"].as_i64().unwrap_or(0);
        let w_duration = word["durationMilliseconds"].as_i64().unwrap_or(0);
        let w_end = w_offset + w_duration;

        if segment_start.is_none() {
            segment_start = Some(w_offset);
            segment_start_ms = w_offset;
        }

        if !current_text.is_empty() && !w_text.starts_with(|c: char| c.is_ascii_punctuation() || is_cjk_punctuation(c)) {
            current_text.push(' ');
        }
        current_text.push_str(w_text);
        segment_end = w_end;
        char_count += w_text.chars().count() as u32;

        let should_split = {
            let is_sentence_end = options.enable_sentence_split && is_sentence_break_punctuation(w_text);
            let is_comma_split = options.split_on_comma && w_text.ends_with(',');
            let exceeds_chars = char_count >= options.max_chars;
            let exceeds_duration = (segment_end - segment_start_ms) as f64 / 1000.0 >= options.max_duration_seconds;

            // Check pause before next word
            let pause_split = if i + 1 < words.len() {
                let next_offset = words[i + 1]["offsetMilliseconds"].as_i64().unwrap_or(0);
                let gap = next_offset - w_end;
                gap >= options.pause_split_ms as i64
            } else {
                false
            };

            is_sentence_end || is_comma_split || exceeds_chars || exceeds_duration || pause_split
        };

        if should_split || i == words.len() - 1 {
            if !current_text.trim().is_empty() {
                cues.push(SubtitleCue {
                    start_ms: segment_start_ms,
                    end_ms: segment_end,
                    text: normalize_subtitle_text(&current_text),
                    speaker: speaker.map(|s| s.to_string()),
                });
            }
            current_text.clear();
            char_count = 0;
            segment_start = None;
        }
    }

    cues
}

/// Merge adjacent cues that are close together and from the same speaker.
fn merge_adjacent_cues(cues: &mut Vec<SubtitleCue>, options: &BatchSubtitleSplitOptions) {
    if cues.len() < 2 {
        return;
    }

    let min_merge_chars = 15;
    let mut i = 0;
    while i + 1 < cues.len() {
        let same_speaker = cues[i].speaker == cues[i + 1].speaker;
        let gap = cues[i + 1].start_ms - cues[i].end_ms;
        let short_gap = gap < options.pause_split_ms as i64;

        let merged_chars = cues[i].text.chars().count() + cues[i + 1].text.chars().count();
        let merged_duration = (cues[i + 1].end_ms - cues[i].start_ms) as f64 / 1000.0;
        let within_limits = merged_chars <= options.max_chars as usize
            && merged_duration <= options.max_duration_seconds;

        let prev_is_sentence_end = options.enable_sentence_split
            && is_sentence_break_text(&cues[i].text)
            && cues[i].text.chars().count() >= min_merge_chars;

        if same_speaker && short_gap && within_limits && !prev_is_sentence_end {
            let merged_text = format!("{} {}", cues[i].text, cues[i + 1].text);
            cues[i].end_ms = cues[i + 1].end_ms;
            cues[i].text = normalize_subtitle_text(&merged_text);
            cues.remove(i + 1);
        } else {
            i += 1;
        }
    }
}

fn is_sentence_break_punctuation(text: &str) -> bool {
    text.ends_with(|c: char| matches!(c, '。' | '！' | '？' | '!' | '?' | '；' | ';'))
}

fn is_sentence_break_text(text: &str) -> bool {
    is_sentence_break_punctuation(text)
}

fn is_cjk_punctuation(c: char) -> bool {
    matches!(c, '\u{ff0c}' | '\u{3002}' | '\u{ff01}' | '\u{ff1f}' | '\u{ff1b}' | '\u{ff1a}' | '\u{3001}' | '\u{201c}' | '\u{201d}' | '\u{2018}' | '\u{2019}')
}

fn normalize_subtitle_text(text: &str) -> String {
    let mut result = String::with_capacity(text.len());
    let mut prev_space = false;
    for c in text.chars() {
        if c.is_whitespace() {
            if !prev_space {
                result.push(' ');
            }
            prev_space = true;
        } else {
            result.push(c);
            prev_space = false;
        }
    }
    result.trim().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn default_options() -> BatchSubtitleSplitOptions {
        BatchSubtitleSplitOptions::default()
    }

    #[test]
    fn test_parse_empty_json() {
        let cues = parse_fast_transcription("{}", &default_options());
        assert!(cues.is_empty());
    }

    #[test]
    fn test_parse_single_phrase_no_words() {
        let json = r#"{
            "phrases": [{
                "text": "Hello world",
                "offsetMilliseconds": 1000,
                "durationMilliseconds": 2000
            }]
        }"#;
        let cues = parse_fast_transcription(json, &default_options());
        assert_eq!(cues.len(), 1);
        assert_eq!(cues[0].text, "Hello world");
        assert_eq!(cues[0].start_ms, 1000);
        assert_eq!(cues[0].end_ms, 3000);
    }

    #[test]
    fn test_parse_with_speaker() {
        let json = r#"{
            "phrases": [{
                "text": "Hello",
                "offsetMilliseconds": 0,
                "durationMilliseconds": 1000,
                "speaker": 1
            }]
        }"#;
        let cues = parse_fast_transcription(json, &default_options());
        assert_eq!(cues[0].speaker, Some("Speaker 1".to_string()));
    }

    #[test]
    fn test_parse_with_words_splits() {
        let json = r#"{
            "phrases": [{
                "text": "This is a sentence. And another one.",
                "offsetMilliseconds": 0,
                "durationMilliseconds": 5000,
                "words": [
                    {"text": "This", "offsetMilliseconds": 0, "durationMilliseconds": 300},
                    {"text": "is", "offsetMilliseconds": 300, "durationMilliseconds": 200},
                    {"text": "a", "offsetMilliseconds": 500, "durationMilliseconds": 100},
                    {"text": "sentence.", "offsetMilliseconds": 600, "durationMilliseconds": 500},
                    {"text": "And", "offsetMilliseconds": 2000, "durationMilliseconds": 300},
                    {"text": "another", "offsetMilliseconds": 2300, "durationMilliseconds": 400},
                    {"text": "one.", "offsetMilliseconds": 2700, "durationMilliseconds": 300}
                ]
            }]
        }"#;
        let cues = parse_fast_transcription(json, &default_options());
        // Should split at sentence boundary "sentence."
        assert!(cues.len() >= 2, "expected >= 2 cues, got {}", cues.len());
    }

    #[test]
    fn test_merge_adjacent_cues() {
        let mut cues = vec![
            SubtitleCue { start_ms: 0, end_ms: 1000, text: "Hi".into(), speaker: None },
            SubtitleCue { start_ms: 1100, end_ms: 2000, text: "there".into(), speaker: None },
        ];
        let opts = default_options();
        merge_adjacent_cues(&mut cues, &opts);
        // Gap is 100ms < pause_split_ms 600ms, same speaker, within limits
        assert_eq!(cues.len(), 1);
        assert_eq!(cues[0].text, "Hi there");
        assert_eq!(cues[0].end_ms, 2000);
    }
}
