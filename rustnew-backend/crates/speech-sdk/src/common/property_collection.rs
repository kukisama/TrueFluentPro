//! PropertyCollection wraps the C API property bag for get/set operations.

use crate::error::{convert_err, Result};
use crate::ffi::{
    property_bag_get_string, property_bag_release, property_bag_set_string,
    SmartHandle, SPXPROPERTYBAGHANDLE,
};
use crate::common::PropertyId;
use std::ffi::{CStr, CString};

/// A collection of properties (key-value string pairs) associated with a Speech SDK object.
#[derive(Debug)]
pub struct PropertyCollection {
    pub(crate) handle: SmartHandle<SPXPROPERTYBAGHANDLE>,
}

impl PropertyCollection {
    /// Create a PropertyCollection from a native handle.
    ///
    /// # Safety
    /// `handle` must be a valid property bag handle.
    pub unsafe fn from_handle(handle: SPXPROPERTYBAGHANDLE) -> PropertyCollection {
        PropertyCollection {
            handle: SmartHandle::create("PropertyCollection", handle, property_bag_release),
        }
    }

    /// Get a property by well-known property ID.
    pub fn get_property(&self, id: PropertyId, default_value: &str) -> Result<String> {
        unsafe {
            let c_default = CString::new(default_value)?;
            let mut buffer = vec![0u8; 1024];
            let ret = property_bag_get_string(
                self.handle.inner(),
                id as i32,
                std::ptr::null(),
                c_default.as_ptr(),
                buffer.as_mut_ptr() as *mut std::os::raw::c_char,
                buffer.len() as u32,
            );
            convert_err(ret, "PropertyCollection.get_property error")?;
            let c_str = CStr::from_ptr(buffer.as_ptr() as *const std::os::raw::c_char);
            Ok(c_str.to_string_lossy().into_owned())
        }
    }

    /// Set a property by well-known property ID.
    pub fn set_property(&mut self, id: PropertyId, value: &str) -> Result<()> {
        unsafe {
            let c_value = CString::new(value)?;
            let ret = property_bag_set_string(
                self.handle.inner(),
                id as i32,
                std::ptr::null(),
                c_value.as_ptr(),
            );
            convert_err(ret, "PropertyCollection.set_property error")?;
            Ok(())
        }
    }

    /// Get a property by custom string name.
    pub fn get_property_by_string(&self, name: &str, default_value: &str) -> Result<String> {
        unsafe {
            let c_name = CString::new(name)?;
            let c_default = CString::new(default_value)?;
            let mut buffer = vec![0u8; 1024];
            let ret = property_bag_get_string(
                self.handle.inner(),
                -1, // custom name
                c_name.as_ptr(),
                c_default.as_ptr(),
                buffer.as_mut_ptr() as *mut std::os::raw::c_char,
                buffer.len() as u32,
            );
            convert_err(ret, "PropertyCollection.get_property_by_string error")?;
            let c_str = CStr::from_ptr(buffer.as_ptr() as *const std::os::raw::c_char);
            Ok(c_str.to_string_lossy().into_owned())
        }
    }

    /// Set a property by custom string name.
    pub fn set_property_by_string(&mut self, name: &str, value: &str) -> Result<()> {
        unsafe {
            let c_name = CString::new(name)?;
            let c_value = CString::new(value)?;
            let ret = property_bag_set_string(
                self.handle.inner(),
                -1, // custom name
                c_name.as_ptr(),
                c_value.as_ptr(),
            );
            convert_err(ret, "PropertyCollection.set_property_by_string error")?;
            Ok(())
        }
    }
}
