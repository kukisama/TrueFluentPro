//! Unicode 扩展字素簇可见字数计数（1:1 对齐 C# `VoiceImeUsageService.CountCharacters`）。
//!
//! 规则：一个可见字素簇只计 1 次，跳过空白、标点、控制符、格式符（如 ZWJ / 变体选择符）与组合音标。

use unicode_segmentation::UnicodeSegmentation;

pub fn count_visible_characters(text: &str) -> usize {
    let mut count = 0usize;
    for grapheme in text.graphemes(true) {
        if grapheme.chars().any(is_countable_char) {
            count += 1;
        }
    }
    count
}

fn is_countable_char(ch: char) -> bool {
    if ch.is_whitespace() || ch.is_control() || ch.is_ascii_punctuation() {
        return false;
    }
    // 排除常见中文与全角标点、零宽格式符及组合标记区间
    let u = ch as u32;
    if matches!(
        u,
        0x00AD
            | 0x0300..=0x036F // Combining Diacritical Marks
            | 0x0483..=0x0489
            | 0x1AB0..=0x1AFF
            | 0x1DC0..=0x1DFF
            | 0x2000..=0x206F // General Punctuation + Format (ZWSP, ZWNJ, ZWJ, LRM, RLM...)
            | 0x20D0..=0x20FF // Combining Diacritical Marks for Symbols
            | 0x2E00..=0x2E7F // Supplemental Punctuation
            | 0x3000..=0x303F // CJK Symbols and Punctuation (。、「」《》【】…)
            | 0xFE00..=0xFE0F // Variation Selectors
            | 0xFE20..=0xFE2F
            | 0xFE30..=0xFE4F // CJK Compatibility Forms
            | 0xFE50..=0xFE6F // Small Form Variants
            | 0xFEFF          // BOM / ZWNBSP
            | 0xFF01..=0xFF0F // Fullwidth ASCII punctuation (！＂＃…／)
            | 0xFF1A..=0xFF20 // (：；＜＝＞？＠)
            | 0xFF3B..=0xFF40 // (［＼］＾＿｀)
            | 0xFF5B..=0xFF65 // (｛｜｝～・)
            | 0xE0100..=0xE01EF // Variation Selectors Supplement
    ) {
        return false;
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn counts_cjk_latin_and_emoji_excluding_punctuation_and_spaces() {
        assert_eq!(count_visible_characters("你好，世界！ Hello, world!"), 14);
        // 组合家庭 Emoji 👨🏻‍💻 属于单个字素簇，只计 1 字
        assert_eq!(count_visible_characters("👨🏻‍💻"), 1);
        assert_eq!(count_visible_characters("   ，。！？\n\t "), 0);
    }
}
