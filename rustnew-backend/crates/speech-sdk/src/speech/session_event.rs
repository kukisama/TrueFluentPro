//! Session event args — fired when a recognition session starts/stops.

use crate::error::{convert_err, Result};
use crate::ffi::{
    recognizer_session_event_get_session_id, recognizer_event_handle_release,
    SPXEVENTHANDLE,
};
use std::ffi::CStr;

/// Event args for session start/stop events.
#[derive(Debug)]
pub struct SessionEvent {
    pub session_id: String,
}

impl SessionEvent {
    /// Parse a SessionEvent from a native event handle.
    ///
    /// # Safety
    /// `handle` must be a valid event handle.
    pub(crate) unsafe fn from_handle(handle: SPXEVENTHANDLE) -> Result<SessionEvent> {
        let mut buffer = vec![0u8; 64];
        let ret = recognizer_session_event_get_session_id(
            handle,
            buffer.as_mut_ptr() as *mut std::os::raw::c_char,
            buffer.len() as u32,
        );
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "SessionEvent::from_handle error")?;
        }
        let c_str = CStr::from_ptr(buffer.as_ptr() as *const std::os::raw::c_char);
        let session_id = c_str.to_string_lossy().into_owned();

        // Release the event handle after extraction
        recognizer_event_handle_release(handle);

        Ok(SessionEvent { session_id })
    }
}
