//! Stage prompt templates for AudioLab stage generation.
//!
//! Each stage has a system prompt and a user prompt template.
//! The transcript text is injected via `{transcript}` placeholder.

/// Get the system prompt for a given stage key.
pub fn stage_system_prompt(stage_key: &str) -> &'static str {
    match stage_key {
        "summary" => SUMMARY_SYSTEM,
        "mindmap" => MINDMAP_SYSTEM,
        "insight" => INSIGHT_SYSTEM,
        "research" => RESEARCH_SYSTEM,
        "podcast" => PODCAST_SYSTEM,
        "translation" => TRANSLATION_SYSTEM,
        _ => CUSTOM_SYSTEM,
    }
}

/// Get the user prompt template for a given stage key.
/// Contains `{transcript}` placeholder to be replaced with actual content.
pub fn stage_user_template(stage_key: &str) -> &'static str {
    match stage_key {
        "summary" => SUMMARY_USER,
        "mindmap" => MINDMAP_USER,
        "insight" => INSIGHT_USER,
        "research" => RESEARCH_USER,
        "podcast" => PODCAST_USER,
        "translation" => TRANSLATION_USER,
        _ => CUSTOM_USER,
    }
}

/// Build the full user prompt by replacing `{transcript}` placeholder.
pub fn build_stage_prompt(stage_key: &str, transcript: &str, custom_prompt: Option<&str>) -> String {
    if let Some(custom) = custom_prompt {
        return custom.replace("{transcript}", transcript);
    }
    let template = stage_user_template(stage_key);
    template.replace("{transcript}", transcript)
}

const SUMMARY_SYSTEM: &str = "\
You are an expert content summarizer. Produce a structured Markdown summary \
with sections: Key Points, Main Topics, Action Items (if any), and a brief conclusion. \
Use bullet points and keep it concise but comprehensive.";

const SUMMARY_USER: &str = "\
Please summarize the following transcript:\n\n---\n{transcript}\n---\n\n\
Produce a Markdown summary with: Key Points, Main Topics, Action Items, Conclusion.";

const MINDMAP_SYSTEM: &str = "\
You are an expert at creating hierarchical mind maps from content. \
Output a Markdown structure using nested bullet points (- / indent) that represents \
the topic hierarchy. Use exactly this format:\n\
- Root Topic\n  - Subtopic A\n    - Detail 1\n    - Detail 2\n  - Subtopic B";

const MINDMAP_USER: &str = "\
Create a hierarchical mind map from the following transcript. \
Use nested Markdown bullet points:\n\n---\n{transcript}\n---";

const INSIGHT_SYSTEM: &str = "\
You are an AI analyst specializing in discovering non-obvious insights from content. \
Find patterns, contradictions, implications, and connections that aren't immediately apparent. \
Present findings in Markdown with clear headings.";

const INSIGHT_USER: &str = "\
Analyze the following transcript and provide deep insights:\n\n---\n{transcript}\n---\n\n\
Focus on: Hidden patterns, Contradictions, Implications, Cross-domain connections.";

const RESEARCH_SYSTEM: &str = "\
You are a research assistant. Provide detailed analysis and background information \
related to the topics discussed. Include relevant context, definitions, and connections \
to broader knowledge. Output in structured Markdown.";

const RESEARCH_USER: &str = "\
Based on this transcript, provide research context and background:\n\n---\n{transcript}\n---";

const PODCAST_SYSTEM: &str = "\
You are a podcast script writer. Convert the transcript into an engaging podcast script \
with speaker labels, natural transitions, and conversational tone. \
Format: Speaker: dialogue (one speaker per line).";

const PODCAST_USER: &str = "\
Convert this transcript into a podcast script format:\n\n---\n{transcript}\n---";

const TRANSLATION_SYSTEM: &str = "\
You are a professional translator. Translate the transcript to the target language \
while preserving meaning, tone, and speaker attribution. Output in Markdown.";

const TRANSLATION_USER: &str = "\
Translate the following transcript:\n\n---\n{transcript}\n---";

const CUSTOM_SYSTEM: &str = "\
You are a helpful AI assistant processing audio transcript content. \
Follow the user instructions precisely.";

const CUSTOM_USER: &str = "\
Process the following transcript:\n\n---\n{transcript}\n---";

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_stage_system_prompts_not_empty() {
        for key in &["summary", "mindmap", "insight", "research", "podcast", "translation", "custom"] {
            let prompt = stage_system_prompt(key);
            assert!(!prompt.is_empty(), "Empty system prompt for {key}");
        }
    }

    #[test]
    fn test_stage_user_templates_contain_placeholder() {
        for key in &["summary", "mindmap", "insight", "research", "podcast", "translation", "custom"] {
            let template = stage_user_template(key);
            assert!(template.contains("{transcript}"), "No placeholder in {key}");
        }
    }

    #[test]
    fn test_build_stage_prompt() {
        let result = build_stage_prompt("summary", "Hello world", None);
        assert!(result.contains("Hello world"));
        assert!(!result.contains("{transcript}"));
    }

    #[test]
    fn test_build_stage_prompt_custom() {
        let custom = "Translate this: {transcript}";
        let result = build_stage_prompt("summary", "Test data", Some(custom));
        assert_eq!(result, "Translate this: Test data");
    }
}
