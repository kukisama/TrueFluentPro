//! Speech recognizer types: config, recognizer, results, and events.

mod speech_config;
mod speech_translation_config;
mod speech_recognition_result;
mod translation_recognition_result;
mod translation_recognizer;
mod speech_recognizer;
mod speech_synthesizer;
mod language_config;
mod phrase_list_grammar;
mod connection;
mod pronunciation_assessment;
mod session_event;
mod recognition_event;

pub use self::speech_config::SpeechConfig;
pub use self::speech_translation_config::SpeechTranslationConfig;
pub use self::speech_recognition_result::SpeechRecognitionResult;
pub use self::translation_recognition_result::{
    TranslationRecognitionResult,
    TranslationSynthesisResult,
    TranslationRecognitionEventArgs,
    TranslationRecognitionCanceledEventArgs,
    TranslationSynthesisEventArgs,
};
pub use self::translation_recognizer::TranslationRecognizer;
pub use self::speech_recognizer::{
    SpeechRecognizer,
    SpeechRecognitionEventArgs,
    SpeechRecognitionCanceledEventArgs,
};
pub use self::speech_synthesizer::{
    SpeechSynthesizer, SpeechSynthesisResult, VoiceInfo, SpeechSynthesisEventArgs,
    SpeechSynthesisWordBoundaryEventArgs, SpeechSynthesisVisemeEventArgs,
    SpeechSynthesisBookmarkEventArgs,
};
pub use self::language_config::{AutoDetectSourceLanguageConfig, SourceLanguageConfig};
pub use self::phrase_list_grammar::PhraseListGrammar;
pub use self::connection::Connection;
pub use self::pronunciation_assessment::{
    GradingSystem, Granularity, PronunciationAssessmentConfig, PronunciationAssessmentResult,
};
pub use self::session_event::SessionEvent;
pub use self::recognition_event::RecognitionEvent;
