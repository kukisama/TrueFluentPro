//! 能力位标志。
//!
//! 对应 C# 的 `ModelCapability` / `SpeechCapability` [Flags] 枚举，
//! 这里用 Rust 惯用的 newtype + 常量 + 位运算实现，序列化为整数。

use serde::{Deserialize, Serialize};

macro_rules! capability_flags {
    ($name:ident { $($flag:ident = $val:expr),+ $(,)? }) => {
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
        #[serde(transparent)]
        pub struct $name(pub u32);

        impl $name {
            pub const NONE: Self = Self(0);
            $(pub const $flag: Self = Self($val);)+

            #[inline]
            pub fn contains(self, other: Self) -> bool {
                other.0 != 0 && (self.0 & other.0) == other.0
            }

            #[inline]
            pub fn is_empty(self) -> bool {
                self.0 == 0
            }

            #[inline]
            pub fn bits(self) -> u32 {
                self.0
            }
        }

        impl std::ops::BitOr for $name {
            type Output = Self;
            #[inline]
            fn bitor(self, rhs: Self) -> Self {
                Self(self.0 | rhs.0)
            }
        }

        impl std::ops::BitOrAssign for $name {
            #[inline]
            fn bitor_assign(&mut self, rhs: Self) {
                self.0 |= rhs.0;
            }
        }

        impl std::ops::BitAnd for $name {
            type Output = Self;
            #[inline]
            fn bitand(self, rhs: Self) -> Self {
                Self(self.0 & rhs.0)
            }
        }
    };
}

capability_flags!(ModelCapability {
    TEXT = 1,            // 文字对话
    IMAGE = 2,           // 图片生成
    VIDEO = 4,           // 视频生成
    SPEECH_TO_TEXT = 8,  // 语音转文字
    TEXT_TO_SPEECH = 16, // 文字转语音
});

capability_flags!(SpeechCapability {
    REALTIME_SPEECH_TO_TEXT = 1, // 实时语音转文字
    BATCH_SPEECH_TO_TEXT = 2,    // 批量/文件转写
    TEXT_TO_SPEECH = 4,          // 语音合成
});

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn combine_and_contains() {
        let caps = ModelCapability::TEXT | ModelCapability::IMAGE;
        assert!(caps.contains(ModelCapability::TEXT));
        assert!(caps.contains(ModelCapability::IMAGE));
        assert!(!caps.contains(ModelCapability::VIDEO));
        assert!(!caps.is_empty());
        assert_eq!(caps.bits(), 3);
    }

    #[test]
    fn serde_is_integer() {
        let caps = ModelCapability::TEXT | ModelCapability::SPEECH_TO_TEXT; // 1 | 8 = 9
        let json = serde_json::to_string(&caps).unwrap();
        assert_eq!(json, "9");
        let back: ModelCapability = serde_json::from_str(&json).unwrap();
        assert_eq!(back, caps);
    }
}
