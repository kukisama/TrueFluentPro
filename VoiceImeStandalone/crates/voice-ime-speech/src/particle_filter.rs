//! 中文与多语种句末语气词轻量过滤（对齐现有 `live.rs` 与 `SpeechTranslationService`）。

pub fn filter_modal_particles(input: &str) -> String {
    const PARTICLES: &[char] = &[
        '呢', '吧', '啊', '呀', '啦', '嘛', '哦', '喔', '噢', '嗯', '咯', '唉', '呃', '额',
    ];
    let mut chars: Vec<char> = input.trim_end().chars().collect();
    while chars.len() > 2 {
        match chars.last().copied() {
            Some(last) if PARTICLES.contains(&last) => {
                chars.pop();
            }
            Some(last) if matches!(last, '。' | '！' | '？' | '，' | '、' | '.' | '!' | '?') => {
                if chars.len() >= 2 && PARTICLES.contains(&chars[chars.len() - 2]) {
                    chars.pop();
                    chars.pop();
                    chars.push(last);
                    break;
                } else {
                    break;
                }
            }
            _ => break,
        }
    }
    chars.into_iter().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strips_trailing_particle_preserving_sentence_punctuation() {
        assert_eq!(filter_modal_particles("今天天气真好啊。"), "今天天气真好。");
        assert_eq!(filter_modal_particles("我们开始开会吧"), "我们开始开会");
        assert_eq!(filter_modal_particles("好的"), "好的");
    }
}
