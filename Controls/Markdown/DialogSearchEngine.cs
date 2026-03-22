using System;
using System.Collections.Generic;
using System.Linq;

namespace TrueFluentPro.Controls.Markdown;

/// <summary>
/// 对话搜索引擎 —— 在消息列表中进行全文搜索。
/// 独立于宿主应用，仅依赖 IDialogMessage 接口。
/// 支持关键词搜索、高亮匹配、上/下导航。
/// </summary>
public sealed class DialogSearchEngine
{
    private IReadOnlyList<IDialogMessage> _messages = Array.Empty<IDialogMessage>();
    private string _query = "";
    private List<SearchMatch> _matches = new();
    private int _currentIndex = -1;

    /// <summary>当前搜索查询</summary>
    public string Query => _query;

    /// <summary>匹配结果数量</summary>
    public int MatchCount => _matches.Count;

    /// <summary>当前高亮的匹配索引（0-based），-1 表示无匹配</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>当前匹配</summary>
    public SearchMatch? CurrentMatch => _currentIndex >= 0 && _currentIndex < _matches.Count
        ? _matches[_currentIndex]
        : null;

    /// <summary>所有匹配结果</summary>
    public IReadOnlyList<SearchMatch> Matches => _matches;

    /// <summary>
    /// 设置消息源。
    /// </summary>
    public void SetMessages(IReadOnlyList<IDialogMessage> messages)
    {
        _messages = messages;
        if (!string.IsNullOrEmpty(_query))
            ExecuteSearch();
    }

    /// <summary>
    /// 执行搜索。返回匹配数量。
    /// </summary>
    public int Search(string query)
    {
        _query = query?.Trim() ?? "";
        ExecuteSearch();
        return _matches.Count;
    }

    /// <summary>
    /// 跳到下一个匹配（循环）。
    /// </summary>
    public SearchMatch? NavigateNext()
    {
        if (_matches.Count == 0) return null;
        _currentIndex = (_currentIndex + 1) % _matches.Count;
        return _matches[_currentIndex];
    }

    /// <summary>
    /// 跳到上一个匹配（循环）。
    /// </summary>
    public SearchMatch? NavigatePrevious()
    {
        if (_matches.Count == 0) return null;
        _currentIndex = (_currentIndex - 1 + _matches.Count) % _matches.Count;
        return _matches[_currentIndex];
    }

    /// <summary>
    /// 清除搜索状态。
    /// </summary>
    public void Clear()
    {
        _query = "";
        _matches.Clear();
        _currentIndex = -1;
    }

    /// <summary>
    /// 检查指定消息是否包含匹配。
    /// </summary>
    public bool HasMatch(IDialogMessage message)
        => _matches.Any(m => ReferenceEquals(m.Message, message));

    /// <summary>
    /// 获取指定消息中的所有匹配位置。
    /// </summary>
    public IEnumerable<(int Start, int Length)> GetMatchPositions(IDialogMessage message)
        => _matches
            .Where(m => ReferenceEquals(m.Message, message))
            .Select(m => (m.StartIndex, m.Length));

    // ── 内部搜索实现 ─────────────────────────────────────

    private void ExecuteSearch()
    {
        _matches.Clear();
        _currentIndex = -1;

        if (string.IsNullOrEmpty(_query))
            return;

        for (int msgIdx = 0; msgIdx < _messages.Count; msgIdx++)
        {
            var msg = _messages[msgIdx];

            // 搜索主文本
            FindAllOccurrences(msg, msg.Text, msgIdx, SearchField.Text);

            // 搜索推理文本
            if (!string.IsNullOrEmpty(msg.ReasoningText))
                FindAllOccurrences(msg, msg.ReasoningText, msgIdx, SearchField.Reasoning);
        }

        if (_matches.Count > 0)
            _currentIndex = 0;
    }

    private void FindAllOccurrences(IDialogMessage msg, string text, int messageIndex, SearchField field)
    {
        if (string.IsNullOrEmpty(text))
            return;

        int pos = 0;
        while (pos < text.Length)
        {
            int idx = text.IndexOf(_query, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;

            _matches.Add(new SearchMatch(msg, messageIndex, idx, _query.Length, field));
            pos = idx + 1;
        }
    }

    /// <summary>搜索匹配结果</summary>
    public sealed record SearchMatch(
        IDialogMessage Message,
        int MessageIndex,
        int StartIndex,
        int Length,
        SearchField Field);

    /// <summary>匹配所在的字段</summary>
    public enum SearchField
    {
        Text,
        Reasoning,
    }
}
