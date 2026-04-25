//! SpeechTranslationConfig — translation-specific configuration.
//!
//! Ported from Go SDK: speech_translation_config.go
//! (https://github.com/microsoft/cognitive-services-speech-sdk-go/blob/master/speech/speech_translation_config.go)
//!
//! SpeechTranslationConfig extends SpeechConfig with target language management
//! and voice output settings for speech translation scenarios.

use crate::common::{PropertyCollection, PropertyId};
use crate::error::{convert_err, Result};
use crate::ffi::{
    speech_config_get_property_bag, speech_config_release,
    speech_translation_config_from_subscription,
    speech_translation_config_from_authorization_token,
    speech_translation_config_from_endpoint,
    speech_translation_config_from_host,
    speech_translation_config_add_target_language,
    speech_translation_config_remove_target_language,
    speech_translation_config_set_custom_model_category_id,
    SmartHandle, SPXSPEECHCONFIGHANDLE, SPXPROPERTYBAGHANDLE,
};
use std::ffi::CString;
use std::mem::MaybeUninit;

/// Configuration for speech translation, extending SpeechConfig
/// with target languages and voice output settings.
///
/// Corresponds to Go SDK's `SpeechTranslationConfig` struct.
#[derive(Debug)]
pub struct SpeechTranslationConfig {
    pub(crate) handle: SmartHandle<SPXSPEECHCONFIGHANDLE>,
    pub(crate) properties: PropertyCollection,
}

impl SpeechTranslationConfig {
    /// Internal: create from a raw handle, extracting the property bag.
    ///
    /// # Safety
    /// `handle` must be a valid speech translation config handle.
    unsafe fn from_handle(handle: SPXSPEECHCONFIGHANDLE) -> Result<SpeechTranslationConfig> {
        let mut prop_bag_handle: MaybeUninit<SPXPROPERTYBAGHANDLE> = MaybeUninit::uninit();
        let ret = speech_config_get_property_bag(handle, prop_bag_handle.as_mut_ptr());
        convert_err(ret, "SpeechTranslationConfig::from_handle error")?;

        let mut properties = PropertyCollection::from_handle(prop_bag_handle.assume_init());
        properties.set_property_by_string("SPEECHSDK-SPEECH-CONFIG-SYSTEM-LANGUAGE", "Rust")?;

        Ok(SpeechTranslationConfig {
            handle: SmartHandle::create("SpeechTranslationConfig", handle, speech_config_release),
            properties,
        })
    }

    // ─── Constructors (ported from Go SDK) ───────────────────────────

    /// Create from subscription key and region.
    ///
    /// Go equivalent: `NewSpeechTranslationConfigFromSubscription`
    pub fn from_subscription(subscription: &str, region: &str) -> Result<SpeechTranslationConfig> {
        let c_sub = CString::new(subscription)?;
        let c_region = CString::new(region)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_translation_config_from_subscription(
                handle.as_mut_ptr(),
                c_sub.as_ptr(),
                c_region.as_ptr(),
            );
            convert_err(ret, "SpeechTranslationConfig::from_subscription error")?;
            SpeechTranslationConfig::from_handle(handle.assume_init())
        }
    }

    /// Create from authorization token and region.
    ///
    /// Go equivalent: `NewSpeechTranslationConfigFromAuthorizationToken`
    pub fn from_auth_token(auth_token: &str, region: &str) -> Result<SpeechTranslationConfig> {
        let c_token = CString::new(auth_token)?;
        let c_region = CString::new(region)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_translation_config_from_authorization_token(
                handle.as_mut_ptr(),
                c_token.as_ptr(),
                c_region.as_ptr(),
            );
            convert_err(ret, "SpeechTranslationConfig::from_auth_token error")?;
            SpeechTranslationConfig::from_handle(handle.assume_init())
        }
    }

    /// Create from endpoint URI with subscription key.
    ///
    /// Go equivalent: `NewSpeechTranslationConfigFromEndpointWithSubscription`
    pub fn from_endpoint_with_subscription(
        endpoint: &str,
        subscription: &str,
    ) -> Result<SpeechTranslationConfig> {
        let c_endpoint = CString::new(endpoint)?;
        let c_sub = CString::new(subscription)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_translation_config_from_endpoint(
                handle.as_mut_ptr(),
                c_endpoint.as_ptr(),
                c_sub.as_ptr(),
            );
            convert_err(ret, "SpeechTranslationConfig::from_endpoint_with_subscription error")?;
            SpeechTranslationConfig::from_handle(handle.assume_init())
        }
    }

    /// Create from endpoint URI without subscription (set auth token after).
    ///
    /// Go equivalent: `NewSpeechTranslationConfigFromEndpoint`
    pub fn from_endpoint(endpoint: &str) -> Result<SpeechTranslationConfig> {
        let c_endpoint = CString::new(endpoint)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_translation_config_from_endpoint(
                handle.as_mut_ptr(),
                c_endpoint.as_ptr(),
                std::ptr::null(),
            );
            convert_err(ret, "SpeechTranslationConfig::from_endpoint error")?;
            SpeechTranslationConfig::from_handle(handle.assume_init())
        }
    }

    /// Create from host URI with subscription key.
    ///
    /// Go equivalent: `NewSpeechTranslationConfigFromHostWithSubscription`
    pub fn from_host_with_subscription(
        host: &str,
        subscription: &str,
    ) -> Result<SpeechTranslationConfig> {
        let c_host = CString::new(host)?;
        let c_sub = CString::new(subscription)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_translation_config_from_host(
                handle.as_mut_ptr(),
                c_host.as_ptr(),
                c_sub.as_ptr(),
            );
            convert_err(ret, "SpeechTranslationConfig::from_host_with_subscription error")?;
            SpeechTranslationConfig::from_handle(handle.assume_init())
        }
    }

    /// Create from host URI without subscription.
    ///
    /// Go equivalent: `NewSpeechTranslationConfigFromHost`
    pub fn from_host(host: &str) -> Result<SpeechTranslationConfig> {
        let c_host = CString::new(host)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_translation_config_from_host(
                handle.as_mut_ptr(),
                c_host.as_ptr(),
                std::ptr::null(),
            );
            convert_err(ret, "SpeechTranslationConfig::from_host error")?;
            SpeechTranslationConfig::from_handle(handle.assume_init())
        }
    }

    // ─── Target language management (ported from Go SDK) ─────────────

    /// Add a target language for translation (e.g. "en", "de", "fr").
    ///
    /// Go equivalent: `(*SpeechTranslationConfig).AddTargetLanguage`
    pub fn add_target_language(&self, language: &str) -> Result<()> {
        let c_lang = CString::new(language)?;
        unsafe {
            let ret = speech_translation_config_add_target_language(
                self.handle.inner(),
                c_lang.as_ptr(),
            );
            convert_err(ret, "SpeechTranslationConfig::add_target_language error")
        }
    }

    /// Remove a target language from translation.
    ///
    /// Go equivalent: `(*SpeechTranslationConfig).RemoveTargetLanguage`
    pub fn remove_target_language(&self, language: &str) -> Result<()> {
        let c_lang = CString::new(language)?;
        unsafe {
            let ret = speech_translation_config_remove_target_language(
                self.handle.inner(),
                c_lang.as_ptr(),
            );
            convert_err(ret, "SpeechTranslationConfig::remove_target_language error")
        }
    }

    /// Get all configured target languages.
    ///
    /// Go equivalent: `(*SpeechTranslationConfig).GetTargetLanguages`
    pub fn get_target_languages(&self) -> Result<Vec<String>> {
        let languages = self.properties.get_property(
            PropertyId::SpeechServiceConnectionTranslationToLanguages,
            "",
        )?;
        if languages.is_empty() {
            return Ok(Vec::new());
        }
        Ok(languages.split(',').map(|s| s.to_string()).collect())
    }

    // ─── Voice & model settings (ported from Go SDK) ─────────────────

    /// Set the output voice name for synthesized translation audio.
    ///
    /// Go equivalent: `(*SpeechTranslationConfig).SetVoiceName`
    pub fn set_voice_name(&mut self, voice: &str) -> Result<()> {
        self.properties.set_property(
            PropertyId::SpeechServiceConnectionTranslationVoice,
            voice,
        )
    }

    /// Get the output voice name.
    ///
    /// Go equivalent: `(*SpeechTranslationConfig).GetVoiceName`
    pub fn get_voice_name(&self) -> Result<String> {
        self.properties.get_property(
            PropertyId::SpeechServiceConnectionTranslationVoice,
            "",
        )
    }

    /// Set a custom model category ID for the translation service.
    ///
    /// Go equivalent: `(*SpeechTranslationConfig).SetCustomModelCategoryID`
    pub fn set_custom_model_category_id(&self, category_id: &str) -> Result<()> {
        let c_id = CString::new(category_id)?;
        unsafe {
            let ret = speech_translation_config_set_custom_model_category_id(
                self.handle.inner(),
                c_id.as_ptr(),
            );
            convert_err(ret, "SpeechTranslationConfig::set_custom_model_category_id error")
        }
    }

    // ─── Inherited SpeechConfig properties ───────────────────────────

    pub fn set_speech_recognition_language(&mut self, lang: &str) -> Result<()> {
        self.properties.set_property(PropertyId::SpeechServiceConnectionRecoLanguage, lang)
    }

    pub fn get_speech_recognition_language(&self) -> Result<String> {
        self.properties.get_property(PropertyId::SpeechServiceConnectionRecoLanguage, "")
    }

    pub fn set_auth_token(&mut self, token: &str) -> Result<()> {
        self.properties.set_property(PropertyId::SpeechServiceAuthorizationToken, token)
    }

    pub fn get_auth_token(&self) -> Result<String> {
        self.properties.get_property(PropertyId::SpeechServiceAuthorizationToken, "")
    }
}
