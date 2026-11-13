//! UTF-16 代码单元边界计算与代理对安全切片。
//!
//! 对齐 C# `VoiceImeSharedHost.DeliverRecognized`：
//! 当一句长语音中途切换输入框目标时，已投递到旧输入框的前缀按 UTF-16 代码单元长度冻结为 `boundary`。
//! 新输入框只接收 `boundary` 之后的后缀；若新快照恰好在 `boundary` 处变成代理对中间（低代理项），
//! 向后跳过低代理项，绝不制造非法孤立代理项或回吐归属旧目标的半个字符。

/// 计算 UTF-8 字符串对应的 UTF-16 代码单元长度（与 Windows `wchar_t` / C# `string.Length` 一致）。
pub fn utf16_len(text: &str) -> usize {
    text.encode_utf16().count()
}

/// 从指定的 UTF-16 代码单元偏移 `utf16_boundary` 安全截取后缀字符串。
pub fn slice_from_utf16_boundary(text: &str, utf16_boundary: usize) -> String {
    if utf16_boundary == 0 {
        return text.to_string();
    }
    let units: Vec<u16> = text.encode_utf16().collect();
    let mut start = utf16_boundary.min(units.len());
    // 若 start 落在代理对中间（前一个是高代理 0xD800..=0xDBFF，当前是低代理 0xDC00..=0xDFFF），向后跳过
    if start > 0
        && start < units.len()
        && (0xD800..=0xDBFF).contains(&units[start - 1])
        && (0xDC00..=0xDFFF).contains(&units[start])
    {
        start += 1;
    }
    String::from_utf16_lossy(&units[start..])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ascii_and_cjk_slice_exact_boundary() {
        let s = "你好世界Hello";
        assert_eq!(utf16_len("你好"), 2);
        assert_eq!(slice_from_utf16_boundary(s, 2), "世界Hello");
        assert_eq!(slice_from_utf16_boundary(s, 99), "");
    }

    #[test]
    fn surrogate_pair_midpoint_skips_forward_safely() {
        // "😀" 在 UTF-16 中占 2 个代码单元 (0xD83D, 0xDE00)
        let s = "A😀B";
        assert_eq!(utf16_len(s), 4);
        // 边界 2 恰好落在 "😀" 的高代理与低代理之间，必须跳过整个 Emoji，只输出 "B"
        assert_eq!(slice_from_utf16_boundary(s, 2), "B");
        assert_eq!(slice_from_utf16_boundary(s, 1), "😀B");
        assert_eq!(slice_from_utf16_boundary(s, 3), "B");
    }
}
