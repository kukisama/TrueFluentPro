//! Enum types used throughout the SDK, mapped from C API constants.

use crate::ffi;

/// Recognition result reason.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum ResultReason {
    NoMatch = ffi::ResultReason_NoMatch,
    Canceled = ffi::ResultReason_Canceled,
    RecognizingSpeech = ffi::ResultReason_RecognizingSpeech,
    RecognizedSpeech = ffi::ResultReason_RecognizedSpeech,
    TranslatingSpeech = ffi::ResultReason_TranslatingSpeech,
    TranslatedSpeech = ffi::ResultReason_TranslatedSpeech,
    SynthesizingAudio = ffi::ResultReason_SynthesizingAudio,
    SynthesizingAudioCompleted = ffi::ResultReason_SynthesizingAudioComplete,
}

impl From<ffi::Result_Reason> for ResultReason {
    fn from(value: ffi::Result_Reason) -> Self {
        match value {
            ffi::ResultReason_NoMatch => ResultReason::NoMatch,
            ffi::ResultReason_Canceled => ResultReason::Canceled,
            ffi::ResultReason_RecognizingSpeech => ResultReason::RecognizingSpeech,
            ffi::ResultReason_RecognizedSpeech => ResultReason::RecognizedSpeech,
            ffi::ResultReason_TranslatingSpeech => ResultReason::TranslatingSpeech,
            ffi::ResultReason_TranslatedSpeech => ResultReason::TranslatedSpeech,
            ffi::ResultReason_SynthesizingAudio => ResultReason::SynthesizingAudio,
            ffi::ResultReason_SynthesizingAudioComplete => ResultReason::SynthesizingAudioCompleted,
            _ => ResultReason::NoMatch,
        }
    }
}

/// Cancellation reason for a recognition operation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum CancellationReason {
    Error = ffi::CancellationReason_Error,
    EndOfStream = ffi::CancellationReason_EndOfStream,
    CancelledByUser = ffi::CancellationReason_UserCancelled,
}

impl From<ffi::Result_CancellationReason> for CancellationReason {
    fn from(value: ffi::Result_CancellationReason) -> Self {
        match value {
            ffi::CancellationReason_Error => CancellationReason::Error,
            ffi::CancellationReason_EndOfStream => CancellationReason::EndOfStream,
            ffi::CancellationReason_UserCancelled => CancellationReason::CancelledByUser,
            _ => CancellationReason::Error,
        }
    }
}

/// Error code for a canceled recognition operation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum CancellationErrorCode {
    NoError = ffi::CancellationErrorCode_NoError,
    AuthenticationFailure = ffi::CancellationErrorCode_AuthenticationFailure,
    BadRequest = ffi::CancellationErrorCode_BadRequest,
    TooManyRequests = ffi::CancellationErrorCode_TooManyRequests,
    Forbidden = ffi::CancellationErrorCode_Forbidden,
    ConnectionFailure = ffi::CancellationErrorCode_ConnectionFailure,
    ServiceTimeout = ffi::CancellationErrorCode_ServiceTimeout,
    ServiceError = ffi::CancellationErrorCode_ServiceError,
    RuntimeError = ffi::CancellationErrorCode_RuntimeError,
}

impl From<ffi::Result_CancellationErrorCode> for CancellationErrorCode {
    fn from(value: ffi::Result_CancellationErrorCode) -> Self {
        match value {
            ffi::CancellationErrorCode_NoError => CancellationErrorCode::NoError,
            ffi::CancellationErrorCode_AuthenticationFailure => CancellationErrorCode::AuthenticationFailure,
            ffi::CancellationErrorCode_BadRequest => CancellationErrorCode::BadRequest,
            ffi::CancellationErrorCode_TooManyRequests => CancellationErrorCode::TooManyRequests,
            ffi::CancellationErrorCode_Forbidden => CancellationErrorCode::Forbidden,
            ffi::CancellationErrorCode_ConnectionFailure => CancellationErrorCode::ConnectionFailure,
            ffi::CancellationErrorCode_ServiceTimeout => CancellationErrorCode::ServiceTimeout,
            ffi::CancellationErrorCode_ServiceError => CancellationErrorCode::ServiceError,
            ffi::CancellationErrorCode_RuntimeError => CancellationErrorCode::RuntimeError,
            _ => CancellationErrorCode::RuntimeError,
        }
    }
}

/// Well-known property IDs for SpeechConfig and Recognizer properties.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum PropertyId {
    SpeechServiceConnectionKey = ffi::SpeechServiceConnection_Key,
    SpeechServiceConnectionEndpoint = ffi::SpeechServiceConnection_Endpoint,
    SpeechServiceConnectionRegion = ffi::SpeechServiceConnection_Region,
    SpeechServiceAuthorizationToken = ffi::SpeechServiceAuthorization_Token,
    SpeechServiceConnectionEndpointId = ffi::SpeechServiceConnection_EndpointId,
    SpeechServiceConnectionTranslationToLanguages = ffi::SpeechServiceConnection_TranslationToLanguages,
    SpeechServiceConnectionTranslationVoice = ffi::SpeechServiceConnection_TranslationVoice,
    SpeechServiceConnectionRecoLanguage = ffi::SpeechServiceConnection_RecoLanguage,
    SpeechServiceResponseJsonErrorDetails = ffi::SpeechServiceResponse_JsonErrorDetails,
    SpeechServiceResponseJsonResult = ffi::SpeechServiceResponse_JsonResult,
    SpeechServiceConnectionSynthLanguage = ffi::SpeechServiceConnection_SynthLanguage,
    SpeechServiceConnectionSynthVoice = ffi::SpeechServiceConnection_SynthVoice,
    SpeechServiceConnectionAutoDetectSourceLanguages = ffi::SpeechServiceConnection_AutoDetectSourceLanguages,
    SpeechServiceConnectionAutoDetectSourceLanguageResult = ffi::SpeechServiceConnection_AutoDetectSourceLanguageResult,
}
