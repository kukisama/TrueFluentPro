use tfp_core::LanguageInfo;

/// Built-in language list for the translation UI.
pub fn built_in_languages() -> Vec<LanguageInfo> {
    [
        ("zh-Hans", "Chinese (Simplified)", "中文（简体）"),
        ("zh-Hant", "Chinese (Traditional)", "中文（繁體）"),
        ("en", "English", "English"),
        ("ja", "Japanese", "日本語"),
        ("ko", "Korean", "한국어"),
        ("fr", "French", "Français"),
        ("de", "German", "Deutsch"),
        ("es", "Spanish", "Español"),
        ("ru", "Russian", "Русский"),
        ("pt", "Portuguese", "Português"),
        ("it", "Italian", "Italiano"),
        ("ar", "Arabic", "العربية"),
        ("hi", "Hindi", "हिन्दी"),
        ("th", "Thai", "ภาษาไทย"),
        ("vi", "Vietnamese", "Tiếng Việt"),
    ]
    .into_iter()
    .map(|(code, name, native)| LanguageInfo {
        code: code.into(),
        name: name.into(),
        native_name: native.into(),
    })
    .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_built_in_languages() {
        let langs = built_in_languages();
        assert_eq!(langs.len(), 15);
        assert_eq!(langs[0].code, "zh-Hans");
        assert_eq!(langs[0].name, "Chinese (Simplified)");
        assert_eq!(langs[0].native_name, "中文（简体）");
        assert_eq!(langs.last().unwrap().code, "vi");
    }
}
