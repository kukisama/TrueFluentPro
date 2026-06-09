//! PropertyCollection wraps the C API property bag for get/set operations.

use crate::error::{convert_err, Result};
use crate::ffi::{
    property_bag_free_string, property_bag_get_string, property_bag_release,
    property_bag_set_string, SmartHandle, SPXPROPERTYBAGHANDLE,
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
            // property_bag_get_string 直接返回字符串指针（由 SDK 分配），
            // 需用 property_bag_free_string 释放。
            let ptr = property_bag_get_string(
                self.handle.inner(),
                id as i32,
                std::ptr::null(),
                c_default.as_ptr(),
            );
            if ptr.is_null() {
                return Ok(String::new());
            }
            let result = CStr::from_ptr(ptr).to_string_lossy().into_owned();
            property_bag_free_string(ptr);
            Ok(result)
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
            let ptr = property_bag_get_string(
                self.handle.inner(),
                -1, // custom name
                c_name.as_ptr(),
                c_default.as_ptr(),
            );
            if ptr.is_null() {
                return Ok(String::new());
            }
            let result = CStr::from_ptr(ptr).to_string_lossy().into_owned();
            property_bag_free_string(ptr);
            Ok(result)
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
