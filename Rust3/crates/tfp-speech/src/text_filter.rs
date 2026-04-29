/// Filter modal particles / filler words from recognized text (aligned with C# ModalParticleFillers).
pub fn filter_modal_particles(text: &str) -> String {
    static FILLERS: &[&str] = &[
        "\u{554a}", "\u{5440}", "\u{5427}", "\u{5566}", "\u{561b}", "\u{5462}",
        "\u{54e6}", "\u{5450}", "\u{54c8}", "\u{5475}", "\u{55ef}", "\u{5509}",
        "\u{54ce}", "\u{90a3}\u{4e2a}", "\u{8fd9}\u{4e2a}", "\u{5c31}\u{662f}",
        "\u{7136}\u{540e}", "\u{5c31}\u{662f}\u{8bf4}", "\u{600e}\u{4e48}\u{8bf4}",
        "\u{4f60}\u{77e5}\u{9053}", "\u{5bf9}\u{5427}", "\u{662f}\u{5427}",
        "\u{5443}", "\u{989d}", "\u{55ef}\u{55ef}", "\u{554a}\u{554a}",
        "\u{54e6}\u{54e6}",
    ];
    let mut result = text.to_string();
    for filler in FILLERS {
        // Remove filler at start of sentence (followed by comma or entire match)
        let start_pattern = format!("{}\u{ff0c}", filler);
        if result.starts_with(&start_pattern) {
            result = result[start_pattern.len()..].to_string();
        } else if result.starts_with(filler) && result.len() == filler.len() {
            result.clear();
        }
        // Remove filler at end of sentence (preceded by comma or trailing)
        let end_pattern = format!("\u{ff0c}{}", filler);
        if result.ends_with(&end_pattern) {
            let new_len = result.len() - end_pattern.len();
            result.truncate(new_len);
        } else if result.ends_with(filler) {
            let new_len = result.len() - filler.len();
            result.truncate(new_len);
        }
    }
    result.trim().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_filter_modal_particles_removes_fillers() {
        assert_eq!(filter_modal_particles("啊，很好"), "很好");
        assert_eq!(filter_modal_particles("好的吧"), "好的");
        assert_eq!(filter_modal_particles("正常文本"), "正常文本");
        assert_eq!(filter_modal_particles("我觉得然后"), "我觉得");
    }

    #[test]
    fn test_filter_modal_particles_empty_after_filter() {
        assert_eq!(filter_modal_particles("啊"), "");
        assert_eq!(filter_modal_particles("吧"), "");
    }
}
