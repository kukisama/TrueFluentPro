//! Dialog exporter — export chat messages to Markdown, plain text, or JSON.

use serde::Serialize;

/// A message prepared for export.
#[derive(Debug, Clone, Serialize)]
pub struct ExportableMessage {
    pub role: String,
    pub text: String,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub reasoning_text: String,
    pub timestamp: String,
    pub content_type: String,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub media_paths: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub prompt_tokens: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub completion_tokens: Option<u32>,
}

/// Export messages to Markdown format.
///
/// Each message is rendered as a heading with timestamp, optional reasoning
/// in a collapsible `<details>` block, the main text, media attachments, and
/// token usage.
pub fn export_to_markdown(messages: &[ExportableMessage]) -> String {
    let mut out = String::new();

    for msg in messages {
        out.push_str(&format!("### {} ({})\n\n", msg.role, msg.timestamp));

        if !msg.reasoning_text.is_empty() {
            out.push_str("<details><summary>Reasoning</summary>\n\n");
            out.push_str(&msg.reasoning_text);
            out.push_str("\n\n</details>\n\n");
        }

        out.push_str(&msg.text);
        out.push('\n');

        if !msg.media_paths.is_empty() {
            out.push('\n');
            for path in &msg.media_paths {
                out.push_str(&format!("- 📎 {}\n", path));
            }
        }

        if msg.prompt_tokens.is_some() || msg.completion_tokens.is_some() {
            out.push('\n');
            let pt = msg.prompt_tokens.unwrap_or(0);
            let ct = msg.completion_tokens.unwrap_or(0);
            out.push_str(&format!("_Tokens: {} prompt + {} completion = {}_\n", pt, ct, pt + ct));
        }

        out.push_str("\n---\n\n");
    }

    out
}

/// Export messages to plain text format.
pub fn export_to_plain_text(messages: &[ExportableMessage]) -> String {
    let mut out = String::new();
    for msg in messages {
        out.push_str(&format!("[{}] ({}) {}\n", msg.role, msg.timestamp, msg.text));
    }
    out
}

/// Export messages to JSON format using serde_json.
pub fn export_to_json(messages: &[ExportableMessage]) -> String {
    #[derive(Serialize)]
    struct JsonMessage {
        role: String,
        text: String,
        timestamp: String,
        content_type: String,
        #[serde(skip_serializing_if = "Option::is_none")]
        prompt_tokens: Option<u32>,
        #[serde(skip_serializing_if = "Option::is_none")]
        completion_tokens: Option<u32>,
    }

    let json_msgs: Vec<JsonMessage> = messages
        .iter()
        .map(|m| JsonMessage {
            role: m.role.clone(),
            text: m.text.clone(),
            timestamp: m.timestamp.clone(),
            content_type: m.content_type.clone(),
            prompt_tokens: m.prompt_tokens,
            completion_tokens: m.completion_tokens,
        })
        .collect();

    serde_json::to_string_pretty(&json_msgs).unwrap_or_else(|_| "[]".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_msg(role: &str, text: &str) -> ExportableMessage {
        ExportableMessage {
            role: role.to_string(),
            text: text.to_string(),
            reasoning_text: String::new(),
            timestamp: "2026-04-30 00:00:00".to_string(),
            content_type: "text".to_string(),
            media_paths: Vec::new(),
            prompt_tokens: None,
            completion_tokens: None,
        }
    }

    fn make_full_msg() -> ExportableMessage {
        ExportableMessage {
            role: "assistant".to_string(),
            text: "The answer is 42.".to_string(),
            reasoning_text: "Let me think step by step...".to_string(),
            timestamp: "2026-04-30 12:30:00".to_string(),
            content_type: "text".to_string(),
            media_paths: vec!["images/chart.png".to_string()],
            prompt_tokens: Some(150),
            completion_tokens: Some(50),
        }
    }

    // --- Markdown tests ---

    #[test]
    fn markdown_empty_list() {
        let result = export_to_markdown(&[]);
        assert!(result.is_empty());
    }

    #[test]
    fn markdown_simple_message() {
        let msgs = vec![make_msg("user", "Hello, how are you?")];
        let result = export_to_markdown(&msgs);
        assert!(result.contains("### user (2026-04-30 00:00:00)"));
        assert!(result.contains("Hello, how are you?"));
        assert!(result.contains("---"));
        assert!(!result.contains("<details>"));
    }

    #[test]
    fn markdown_with_reasoning_and_media() {
        let msgs = vec![make_full_msg()];
        let result = export_to_markdown(&msgs);
        assert!(result.contains("<details><summary>Reasoning</summary>"));
        assert!(result.contains("Let me think step by step..."));
        assert!(result.contains("</details>"));
        assert!(result.contains("📎 images/chart.png"));
        assert!(result.contains("_Tokens: 150 prompt + 50 completion = 200_"));
    }

    #[test]
    fn markdown_multi_message() {
        let msgs = vec![
            make_msg("user", "What is 6 * 7?"),
            make_full_msg(),
        ];
        let result = export_to_markdown(&msgs);
        assert_eq!(result.matches("---").count(), 2);
        assert!(result.contains("### user"));
        assert!(result.contains("### assistant"));
    }

    // --- Plain text tests ---

    #[test]
    fn plaintext_empty_list() {
        let result = export_to_plain_text(&[]);
        assert!(result.is_empty());
    }

    #[test]
    fn plaintext_multi_message() {
        let msgs = vec![
            make_msg("user", "Hello"),
            make_msg("assistant", "Hi there"),
        ];
        let result = export_to_plain_text(&msgs);
        assert!(result.contains("[user] (2026-04-30 00:00:00) Hello"));
        assert!(result.contains("[assistant] (2026-04-30 00:00:00) Hi there"));
    }

    // --- JSON tests ---

    #[test]
    fn json_empty_list() {
        let result = export_to_json(&[]);
        assert_eq!(result.trim(), "[]");
    }

    #[test]
    fn json_round_trip() {
        let msgs = vec![make_full_msg()];
        let result = export_to_json(&msgs);
        let parsed: serde_json::Value = serde_json::from_str(&result).unwrap();
        let arr = parsed.as_array().unwrap();
        assert_eq!(arr.len(), 1);
        assert_eq!(arr[0]["role"], "assistant");
        assert_eq!(arr[0]["text"], "The answer is 42.");
        assert_eq!(arr[0]["prompt_tokens"], 150);
        assert_eq!(arr[0]["completion_tokens"], 50);
    }

    #[test]
    fn json_multi_message() {
        let msgs = vec![
            make_msg("user", "Hello"),
            make_msg("assistant", "Hi there"),
        ];
        let result = export_to_json(&msgs);
        let parsed: serde_json::Value = serde_json::from_str(&result).unwrap();
        let arr = parsed.as_array().unwrap();
        assert_eq!(arr.len(), 2);
        assert_eq!(arr[0]["role"], "user");
        assert_eq!(arr[1]["role"], "assistant");
    }
}
