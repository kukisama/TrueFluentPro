//! 发音评估（Pronunciation Assessment）。
//!
//! - `PronunciationAssessmentConfig`：配置参考文本、评分系统、粒度、错读检测等，
//!   通过 `apply_to_recognizer` 应用到识别器。
//! - `PronunciationAssessmentResult`：从识别结果解析准确度/流利度/完整度/总分/韵律分。

use crate::error::{convert_err, Result};
use crate::ffi::{
    create_pronunciation_assessment_config, create_pronunciation_assessment_config_from_json,
    pronunciation_assessment_config_apply_to_recognizer, pronunciation_assessment_config_release,
    pronunciation_assessment_config_to_json, PronunciationAssessmentGradingSystem_FivePoint,
    PronunciationAssessmentGradingSystem_HundredMark, PronunciationAssessmentGranularity_FullText,
    PronunciationAssessmentGranularity_Phoneme, PronunciationAssessmentGranularity_Word,
    SmartHandle, SPXPRONUNCIATIONASSESSMENTCONFIGHANDLE,
};
use crate::speech::{SpeechRecognitionResult, SpeechRecognizer};
use std::ffi::{CStr, CString};
use std::mem::MaybeUninit;

/// 评分系统。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GradingSystem {
    /// 五分制。
    FivePoint,
    /// 百分制（默认）。
    HundredMark,
}

impl GradingSystem {
    fn as_ffi(self) -> i32 {
        match self {
            GradingSystem::FivePoint => PronunciationAssessmentGradingSystem_FivePoint,
            GradingSystem::HundredMark => PronunciationAssessmentGradingSystem_HundredMark,
        }
    }
}

/// 评估粒度。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Granularity {
    /// 音素级。
    Phoneme,
    /// 单词级。
    Word,
    /// 全文级（默认）。
    FullText,
}

impl Granularity {
    fn as_ffi(self) -> i32 {
        match self {
            Granularity::Phoneme => PronunciationAssessmentGranularity_Phoneme,
            Granularity::Word => PronunciationAssessmentGranularity_Word,
            Granularity::FullText => PronunciationAssessmentGranularity_FullText,
        }
    }
}

/// 发音评估配置。
#[derive(Debug)]
pub struct PronunciationAssessmentConfig {
    handle: SmartHandle<SPXPRONUNCIATIONASSESSMENTCONFIGHANDLE>,
}

impl PronunciationAssessmentConfig {
    /// 以参考文本、评分系统、粒度、是否启用错读检测创建配置。
    pub fn new(
        reference_text: &str,
        grading_system: GradingSystem,
        granularity: Granularity,
        enable_miscue: bool,
    ) -> Result<PronunciationAssessmentConfig> {
        let c_ref = CString::new(reference_text)?;
        unsafe {
            let mut handle: MaybeUninit<SPXPRONUNCIATIONASSESSMENTCONFIGHANDLE> =
                MaybeUninit::uninit();
            let ret = create_pronunciation_assessment_config(
                handle.as_mut_ptr(),
                c_ref.as_ptr(),
                grading_system.as_ffi(),
                granularity.as_ffi(),
                enable_miscue,
            );
            convert_err(ret, "PronunciationAssessmentConfig::new error")?;
            Ok(PronunciationAssessmentConfig {
                handle: SmartHandle::create(
                    "PronunciationAssessmentConfig",
                    handle.assume_init(),
                    pronunciation_assessment_config_release,
                ),
            })
        }
    }

    /// 从 JSON 创建配置。
    pub fn from_json(json: &str) -> Result<PronunciationAssessmentConfig> {
        let c_json = CString::new(json)?;
        unsafe {
            let mut handle: MaybeUninit<SPXPRONUNCIATIONASSESSMENTCONFIGHANDLE> =
                MaybeUninit::uninit();
            let ret = create_pronunciation_assessment_config_from_json(
                handle.as_mut_ptr(),
                c_json.as_ptr(),
            );
            convert_err(ret, "PronunciationAssessmentConfig::from_json error")?;
            Ok(PronunciationAssessmentConfig {
                handle: SmartHandle::create(
                    "PronunciationAssessmentConfig",
                    handle.assume_init(),
                    pronunciation_assessment_config_release,
                ),
            })
        }
    }

    /// 序列化为 JSON。
    pub fn to_json(&self) -> Result<String> {
        unsafe {
            let ptr = pronunciation_assessment_config_to_json(self.handle.inner());
            if ptr.is_null() {
                return Ok(String::new());
            }
            Ok(CStr::from_ptr(ptr).to_string_lossy().into_owned())
        }
    }

    /// 把配置应用到识别器（需在识别前调用）。
    pub fn apply_to_recognizer(&self, recognizer: &SpeechRecognizer) -> Result<()> {
        unsafe {
            let ret = pronunciation_assessment_config_apply_to_recognizer(
                self.handle.inner(),
                recognizer.handle_inner(),
            );
            convert_err(ret, "PronunciationAssessmentConfig::apply_to_recognizer error")
        }
    }
}

/// 发音评估结果，从识别结果的 JSON 解析。
#[derive(Debug, Clone, Default)]
pub struct PronunciationAssessmentResult {
    /// 准确度分。
    pub accuracy_score: f64,
    /// 流利度分。
    pub fluency_score: f64,
    /// 完整度分。
    pub completeness_score: f64,
    /// 总分（发音质量）。
    pub pronunciation_score: f64,
    /// 韵律分（如服务返回）。
    pub prosody_score: Option<f64>,
}

impl PronunciationAssessmentResult {
    /// 从识别结果解析发音评估分数。
    ///
    /// 读取结果属性 `SpeechServiceResponse_JsonResult`，
    /// 取 `NBest[0].PronunciationAssessment` 下的各项分数。
    pub fn from_result(result: &SpeechRecognitionResult) -> Result<PronunciationAssessmentResult> {
        let json = result.properties.get_property(
            crate::common::PropertyId::SpeechServiceResponseJsonResult,
            "",
        )?;
        if json.is_empty() {
            return Ok(PronunciationAssessmentResult::default());
        }
        let root: serde_json::Value = serde_json::from_str(&json)
            .map_err(|e| crate::error::Error::new(format!("解析发音评估 JSON 失败：{e}"), 0))?;
        let pa = root
            .get("NBest")
            .and_then(|n| n.get(0))
            .and_then(|b| b.get("PronunciationAssessment"));

        let get = |key: &str| -> f64 {
            pa.and_then(|p| p.get(key))
                .and_then(|v| v.as_f64())
                .unwrap_or(0.0)
        };

        Ok(PronunciationAssessmentResult {
            accuracy_score: get("AccuracyScore"),
            fluency_score: get("FluencyScore"),
            completeness_score: get("CompletenessScore"),
            pronunciation_score: get("PronScore"),
            prosody_score: pa
                .and_then(|p| p.get("ProsodyScore"))
                .and_then(|v| v.as_f64()),
        })
    }
}
