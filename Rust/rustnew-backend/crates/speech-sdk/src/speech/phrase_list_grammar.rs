//! 短语列表语法（PhraseListGrammar）。
//!
//! 通过向识别器添加一组短语（如专有名词、人名、产品名），
//! 提升这些词汇的识别准确率。对应微软 Speech SDK 的 `PhraseListGrammar`。

use crate::error::{convert_err, Result};
use crate::ffi::{
    grammar_handle_release, grammar_phrase_create_from_text, grammar_phrase_handle_release,
    phrase_list_grammar_add_phrase, phrase_list_grammar_clear,
    phrase_list_grammar_from_recognizer_by_name, phrase_list_grammar_set_weight, SmartHandle,
    SPXGRAMMARHANDLE, SPXPHRASEHANDLE,
};
use crate::speech::SpeechRecognizer;
use std::ffi::CString;
use std::mem::MaybeUninit;

/// 短语列表语法，附着于某个识别器。
#[derive(Debug)]
pub struct PhraseListGrammar {
    handle: SmartHandle<SPXGRAMMARHANDLE>,
}

impl PhraseListGrammar {
    /// 从识别器创建短语列表语法（默认语法名）。
    pub fn from_recognizer(recognizer: &SpeechRecognizer) -> Result<PhraseListGrammar> {
        Self::from_recognizer_by_name(recognizer, "")
    }

    /// 从识别器创建指定名称的短语列表语法。
    pub fn from_recognizer_by_name(
        recognizer: &SpeechRecognizer,
        name: &str,
    ) -> Result<PhraseListGrammar> {
        let c_name = CString::new(name)?;
        unsafe {
            let mut handle: MaybeUninit<SPXGRAMMARHANDLE> = MaybeUninit::uninit();
            let ret = phrase_list_grammar_from_recognizer_by_name(
                handle.as_mut_ptr(),
                recognizer.handle_inner(),
                c_name.as_ptr(),
            );
            convert_err(ret, "PhraseListGrammar::from_recognizer error")?;
            Ok(PhraseListGrammar {
                handle: SmartHandle::create(
                    "PhraseListGrammar",
                    handle.assume_init(),
                    grammar_handle_release,
                ),
            })
        }
    }

    /// 添加一条短语。
    pub fn add_phrase(&self, text: &str) -> Result<()> {
        let c_text = CString::new(text)?;
        unsafe {
            let mut phrase: MaybeUninit<SPXPHRASEHANDLE> = MaybeUninit::uninit();
            let ret = grammar_phrase_create_from_text(phrase.as_mut_ptr(), c_text.as_ptr());
            convert_err(ret, "PhraseListGrammar::add_phrase create error")?;
            let phrase = phrase.assume_init();
            let ret = phrase_list_grammar_add_phrase(self.handle.inner(), phrase);
            // 无论添加成功与否都释放短语句柄。
            let _ = grammar_phrase_handle_release(phrase);
            convert_err(ret, "PhraseListGrammar::add_phrase error")
        }
    }

    /// 设置短语列表权重（0.0~2.0，默认 1.0）。
    pub fn set_weight(&self, weight: f64) -> Result<()> {
        unsafe {
            let ret = phrase_list_grammar_set_weight(self.handle.inner(), weight);
            convert_err(ret, "PhraseListGrammar::set_weight error")
        }
    }

    /// 清空所有短语。
    pub fn clear(&self) -> Result<()> {
        unsafe {
            let ret = phrase_list_grammar_clear(self.handle.inner());
            convert_err(ret, "PhraseListGrammar::clear error")
        }
    }
}
