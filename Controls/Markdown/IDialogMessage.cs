using System;
using System.Collections.Generic;

namespace TrueFluentPro.Controls.Markdown;

/// <summary>
/// 对话消息接口 —— 解耦对话模块与宿主应用的核心抽象。
/// 实现此接口后，整个 Markdown 对话模块可独立打包为 DLL。
/// </summary>
public interface IDialogMessage
{
    /// <summary>消息角色：user / assistant / system</summary>
    string Role { get; }

    /// <summary>消息文本内容</summary>
    string Text { get; }

    /// <summary>推理/思考过程文本</summary>
    string ReasoningText { get; }

    /// <summary>消息时间戳</summary>
    DateTime Timestamp { get; }

    /// <summary>内容类型：text / image / video</summary>
    string ContentType { get; }

    /// <summary>关联的媒体文件路径列表</summary>
    IReadOnlyList<string> MediaPaths { get; }

    /// <summary>Token 用量：输入 Token 数</summary>
    int? PromptTokens { get; }

    /// <summary>Token 用量：输出 Token 数</summary>
    int? CompletionTokens { get; }

    /// <summary>是否为用户消息</summary>
    bool IsUser => Role == "user";

    /// <summary>是否为助手消息</summary>
    bool IsAssistant => Role != "user";
}
