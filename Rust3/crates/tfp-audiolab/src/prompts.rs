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

// ── Auto-tags prompt functions ──

/// System prompt for auto-tag extraction.
pub fn auto_tags_system_prompt() -> &'static str {
    "你是标签提取专家。从音频转录文本中提取 5-10 个关键标签，\
     标签应涵盖主题、人物、事件、概念等核心信息。\
     以 JSON 数组格式返回，如 [\"tag1\", \"tag2\", ...]。\
     只返回 JSON 数组，不要附加其他说明。"
}

/// User prompt for auto-tag extraction.
pub fn auto_tags_user_prompt(transcript: &str) -> String {
    format!(
        "请从以下转录文本中提取标签，以 JSON 数组格式返回：[\"tag1\", \"tag2\", ...]\n\n{transcript}"
    )
}

/// Parse AI response into a list of tags.
/// Tries JSON array first, falls back to comma-separated parsing.
pub fn parse_auto_tags(ai_response: &str) -> Vec<String> {
    let trimmed = ai_response.trim();

    // Try JSON array parse
    if let Ok(tags) = serde_json::from_str::<Vec<String>>(trimmed) {
        return tags.into_iter().filter(|t| !t.is_empty()).collect();
    }

    // Try to extract JSON array from within text (e.g., "Here are the tags: [...]")
    if let Some(start) = trimmed.find('[') {
        if let Some(end) = trimmed.rfind(']') {
            if start < end {
                let slice = &trimmed[start..=end];
                if let Ok(tags) = serde_json::from_str::<Vec<String>>(slice) {
                    return tags.into_iter().filter(|t| !t.is_empty()).collect();
                }
            }
        }
    }

    // Fallback: comma-separated parsing
    trimmed
        .split(',')
        .map(|s| s.trim().trim_matches('"').trim().to_string())
        .filter(|s| !s.is_empty())
        .collect()
}

// ── Research prompt functions ──

/// System prompt for research topic planning (phase 1).
pub fn research_phase1_system_prompt() -> &'static str {
    "你是一个深度研究规划助手。根据音频转录内容，规划 3-5 个值得深入研究的课题。\
     返回 JSON 数组格式：[{\"title\": \"课题标题\", \"description\": \"简要描述\"}]。\
     只返回 JSON 数组，不要附加说明。"
}

/// User prompt for research topic planning.
pub fn research_phase1_user_prompt(transcript: &str) -> String {
    format!(
        "请根据以下转录内容，规划 3-5 个值得深入研究的课题：\n\n{transcript}"
    )
}

/// System prompt for research report generation (phase 2).
pub fn research_phase2_system_prompt() -> &'static str {
    "你是一个深度研究报告撰写专家。根据提供的研究课题和转录内容，\
     撰写详细的研究报告。使用 Markdown 格式，包含：\
     ## 研究背景\n## 核心发现\n## 分析与讨论\n## 结论与建议"
}

/// User prompt for research report generation.
pub fn research_phase2_user_prompt(topic_title: &str, topic_desc: &str, transcript: &str) -> String {
    format!(
        "研究课题：{topic_title}\n课题描述：{topic_desc}\n\n\
         原始转录内容：\n{transcript}\n\n\
         请撰写详细的研究报告。"
    )
}

/// Parse research topics JSON from AI response.
pub fn parse_research_topics(ai_response: &str) -> Vec<(String, String)> {
    let trimmed = ai_response.trim();

    #[derive(serde::Deserialize)]
    struct TopicItem {
        title: String,
        description: String,
    }

    // Try direct parse
    if let Ok(topics) = serde_json::from_str::<Vec<TopicItem>>(trimmed) {
        return topics.into_iter().map(|t| (t.title, t.description)).collect();
    }

    // Try to extract JSON array from within text
    if let Some(start) = trimmed.find('[') {
        if let Some(end) = trimmed.rfind(']') {
            if start < end {
                let slice = &trimmed[start..=end];
                if let Ok(topics) = serde_json::from_str::<Vec<TopicItem>>(slice) {
                    return topics.into_iter().map(|t| (t.title, t.description)).collect();
                }
            }
        }
    }

    Vec::new()
}

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

    // ── T-008 tests ──

    #[test]
    fn test_parse_auto_tags_json() {
        let input = r#"["AI", "机器学习", "深度学习", "NLP"]"#;
        let tags = parse_auto_tags(input);
        assert_eq!(tags, vec!["AI", "机器学习", "深度学习", "NLP"]);
    }

    #[test]
    fn test_parse_auto_tags_json_embedded() {
        let input = "以下是提取的标签：\n[\"标签1\", \"标签2\", \"标签3\"]";
        let tags = parse_auto_tags(input);
        assert_eq!(tags, vec!["标签1", "标签2", "标签3"]);
    }

    #[test]
    fn test_parse_auto_tags_comma_fallback() {
        let input = "AI, 机器学习, 深度学习, NLP";
        let tags = parse_auto_tags(input);
        assert_eq!(tags, vec!["AI", "机器学习", "深度学习", "NLP"]);
    }

    #[test]
    fn test_parse_auto_tags_empty() {
        let tags = parse_auto_tags("");
        assert!(tags.is_empty());
    }

    #[test]
    fn test_auto_tags_system_prompt_not_empty() {
        assert!(!auto_tags_system_prompt().is_empty());
    }

    #[test]
    fn test_auto_tags_user_prompt_contains_transcript() {
        let prompt = auto_tags_user_prompt("test content");
        assert!(prompt.contains("test content"));
    }

    // ── Research prompt tests ──

    #[test]
    fn test_research_prompt_phase1_format() {
        let prompt = research_phase1_user_prompt("这是一段关于AI的讨论");
        assert!(prompt.contains("AI"));
        assert!(prompt.contains("规划"));
    }

    #[test]
    fn test_research_prompt_phase2_format() {
        let prompt = research_phase2_user_prompt("AI伦理", "关于AI伦理问题的探讨", "转录内容...");
        assert!(prompt.contains("AI伦理"));
        assert!(prompt.contains("转录内容"));
    }

    #[test]
    fn test_parse_research_topics_json() {
        let input = r#"[{"title": "课题A", "description": "描述A"}, {"title": "课题B", "description": "描述B"}]"#;
        let topics = parse_research_topics(input);
        assert_eq!(topics.len(), 2);
        assert_eq!(topics[0].0, "课题A");
        assert_eq!(topics[1].1, "描述B");
    }

    #[test]
    fn test_parse_research_topics_empty() {
        let topics = parse_research_topics("invalid json");
        assert!(topics.is_empty());
    }
}
