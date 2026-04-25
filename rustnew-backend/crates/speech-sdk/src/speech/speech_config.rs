//! SpeechConfig wraps the base speech configuration used by all recognizers.

use crate::common::{PropertyCollection, PropertyId};
use crate::error::{convert_err, Result};
use crate::ffi::{
    speech_config_from_subscription, speech_config_from_authorization_token,
    speech_config_from_endpoint, speech_config_from_host,
    speech_config_get_property_bag, speech_config_release,
    SmartHandle, SPXSPEECHCONFIGHANDLE, SPXPROPERTYBAGHANDLE,
};
use std::ffi::CString;
use std::mem::MaybeUninit;

/// Base speech configuration. Used directly for STT, or extended by SpeechTranslationConfig.
#[derive(Debug)]
pub struct SpeechConfig {
    pub(crate) handle: SmartHandle<SPXSPEECHCONFIGHANDLE>,
    pub(crate) properties: PropertyCollection,
}

impl SpeechConfig {
    /// Create a SpeechConfig from a raw handle (internal).
    ///
    /// # Safety
    /// `handle` must be a valid speech config handle.
    pub(crate) unsafe fn from_handle(handle: SPXSPEECHCONFIGHANDLE) -> Result<SpeechConfig> {
        let mut prop_bag_handle: MaybeUninit<SPXPROPERTYBAGHANDLE> = MaybeUninit::uninit();
        let ret = speech_config_get_property_bag(handle, prop_bag_handle.as_mut_ptr());
        convert_err(ret, "SpeechConfig::from_handle error")?;

        let mut properties = PropertyCollection::from_handle(prop_bag_handle.assume_init());
        properties.set_property_by_string("SPEECHSDK-SPEECH-CONFIG-SYSTEM-LANGUAGE", "Rust")?;

        Ok(SpeechConfig {
            handle: SmartHandle::create("SpeechConfig", handle, speech_config_release),
            properties,
        })
    }

    /// Create from subscription key and region.
    pub fn from_subscription(subscription: &str, region: &str) -> Result<SpeechConfig> {
        let c_sub = CString::new(subscription)?;
        let c_region = CString::new(region)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_config_from_subscription(
                handle.as_mut_ptr(),
                c_sub.as_ptr(),
                c_region.as_ptr(),
            );
            convert_err(ret, "SpeechConfig::from_subscription error")?;
            SpeechConfig::from_handle(handle.assume_init())
        }
    }

    /// Create from authorization token and region.
    pub fn from_auth_token(auth_token: &str, region: &str) -> Result<SpeechConfig> {
        let c_token = CString::new(auth_token)?;
        let c_region = CString::new(region)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_config_from_authorization_token(
                handle.as_mut_ptr(),
                c_token.as_ptr(),
                c_region.as_ptr(),
            );
            convert_err(ret, "SpeechConfig::from_auth_token error")?;
            SpeechConfig::from_handle(handle.assume_init())
        }
    }

    /// Create from a custom endpoint URI with subscription key.
    pub fn from_endpoint_with_subscription(endpoint: &str, subscription: &str) -> Result<SpeechConfig> {
        let c_endpoint = CString::new(endpoint)?;
        let c_sub = CString::new(subscription)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_config_from_endpoint(
                handle.as_mut_ptr(),
                c_endpoint.as_ptr(),
                c_sub.as_ptr(),
            );
            convert_err(ret, "SpeechConfig::from_endpoint_with_subscription error")?;
            SpeechConfig::from_handle(handle.assume_init())
        }
    }

    /// Create from a custom endpoint URI (no subscription in the call — set auth token after).
    pub fn from_endpoint(endpoint: &str) -> Result<SpeechConfig> {
        let c_endpoint = CString::new(endpoint)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_config_from_endpoint(
                handle.as_mut_ptr(),
                c_endpoint.as_ptr(),
                std::ptr::null(),
            );
            convert_err(ret, "SpeechConfig::from_endpoint error")?;
            SpeechConfig::from_handle(handle.assume_init())
        }
    }

    /// Create from a custom host URI with subscription key.
    pub fn from_host_with_subscription(host: &str, subscription: &str) -> Result<SpeechConfig> {
        let c_host = CString::new(host)?;
        let c_sub = CString::new(subscription)?;
        unsafe {
            let mut handle: MaybeUninit<SPXSPEECHCONFIGHANDLE> = MaybeUninit::uninit();
            let ret = speech_config_from_host(
                handle.as_mut_ptr(),
                c_host.as_ptr(),
                c_sub.as_ptr(),
            );
            convert_err(ret, "SpeechConfig::from_host_with_subscription error")?;
            SpeechConfig::from_handle(handle.assume_init())
        }
    }

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

    pub fn get_subscription_key(&self) -> Result<String> {
        self.properties.get_property(PropertyId::SpeechServiceConnectionKey, "")
    }

    pub fn get_region(&self) -> Result<String> {
        self.properties.get_property(PropertyId::SpeechServiceConnectionRegion, "")
    }

    pub fn get_endpoint_id(&self) -> Result<String> {
        self.properties.get_property(PropertyId::SpeechServiceConnectionEndpointId, "")
    }

    pub fn set_endpoint_id(&mut self, endpoint_id: &str) -> Result<()> {
        self.properties.set_property(PropertyId::SpeechServiceConnectionEndpointId, endpoint_id)
    }
}
