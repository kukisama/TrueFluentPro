using System;
using System.Collections.Generic;
using System.Text;

namespace TrueFluentPro.Controls.Markdown;

/// <summary>
/// 对话导出器 —— 将对话消息列表导出为多种格式。
/// 独立于宿主应用，仅依赖 IDialogMessage 接口。
/// 支持导出为 Markdown、纯文本、JSON 格式。
/// </summary>
public static class DialogExporter
{
    /// <summary>
    /// 将对话导出为 Markdown 格式。
    /// 每条消息以 ### 角色 标题分隔，包含时间戳和内容。
    /// </summary>
    public static string ExportToMarkdown(IEnumerable<IDialogMessage> messages, string? sessionName = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(sessionName))
        {
            sb.AppendLine($"# {sessionName}");
            sb.AppendLine();
        }

        sb.AppendLine($"> 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            var roleName = msg.IsUser ? "用户" : "AI 助手";
            var roleEmoji = msg.IsUser ? "👤" : "🤖";

            sb.AppendLine($"### {roleEmoji} {roleName}");
            sb.AppendLine($"*{msg.Timestamp:yyyy-MM-dd HH:mm:ss}*");
            sb.AppendLine();

            // 推理过程（如有）
            if (!string.IsNullOrEmpty(msg.ReasoningText))
            {
                sb.AppendLine("<details>");
                sb.AppendLine("<summary>💭 思考过程</summary>");
                sb.AppendLine();
                sb.AppendLine(msg.ReasoningText);
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }

            // 主内容
            sb.AppendLine(msg.Text);
            sb.AppendLine();

            // 媒体文件
            if (msg.MediaPaths.Count > 0)
            {
                sb.AppendLine("**附件：**");
                foreach (var path in msg.MediaPaths)
                    sb.AppendLine($"- `{path}`");
                sb.AppendLine();
            }

            // Token 用量
            if (msg.PromptTokens.HasValue || msg.CompletionTokens.HasValue)
            {
                var prompt = msg.PromptTokens?.ToString() ?? "?";
                var completion = msg.CompletionTokens?.ToString() ?? "?";
                sb.AppendLine($"⚡ 输入 {prompt} / 输出 {completion} tokens");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 将对话导出为纯文本格式。
    /// </summary>
    public static string ExportToPlainText(IEnumerable<IDialogMessage> messages, string? sessionName = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(sessionName))
        {
            sb.AppendLine(sessionName);
            sb.AppendLine(new string('=', sessionName.Length));
            sb.AppendLine();
        }

        foreach (var msg in messages)
        {
            var roleName = msg.IsUser ? "用户" : "AI";
            sb.AppendLine($"[{roleName}] ({msg.Timestamp:HH:mm:ss})");
            sb.AppendLine(msg.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 将对话导出为 JSON 格式（简化版，适合备份和导入）。
    /// </summary>
    public static string ExportToJson(IEnumerable<IDialogMessage> messages, string? sessionName = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");

        if (!string.IsNullOrEmpty(sessionName))
            sb.AppendLine($"  \"session\": \"{EscapeJson(sessionName)}\",");

        sb.AppendLine($"  \"exportedAt\": \"{DateTime.Now:O}\",");
        sb.AppendLine("  \"messages\": [");

        bool first = true;
        foreach (var msg in messages)
        {
            if (!first) sb.AppendLine(",");
            first = false;

            sb.AppendLine("    {");
            sb.AppendLine($"      \"role\": \"{EscapeJson(msg.Role)}\",");
            sb.AppendLine($"      \"text\": \"{EscapeJson(msg.Text)}\",");
            sb.AppendLine($"      \"timestamp\": \"{msg.Timestamp:O}\",");
            sb.AppendLine($"      \"contentType\": \"{EscapeJson(msg.ContentType)}\"");

            if (msg.PromptTokens.HasValue)
                sb.AppendLine($"      ,\"promptTokens\": {msg.PromptTokens.Value}");
            if (msg.CompletionTokens.HasValue)
                sb.AppendLine($"      ,\"completionTokens\": {msg.CompletionTokens.Value}");

            sb.Append("    }");
        }

        sb.AppendLine();
        sb.AppendLine("  ]");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
