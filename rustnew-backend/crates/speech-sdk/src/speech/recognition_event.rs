//! Recognition event args — fired when speech start/end is detected.

use crate::error::{convert_err, Result};
use crate::ffi::{
    recognizer_recognition_event_get_offset, recognizer_event_handle_release,
    SPXEVENTHANDLE,
};
use std::time::Duration;

/// Event args for speech start/end detected events.
#[derive(Debug)]
pub struct RecognitionEvent {
    /// Offset of the recognized speech in 100-nanosecond ticks.
    pub offset: Duration,
}

impl RecognitionEvent {
    /// Parse a RecognitionEvent from a native event handle.
    ///
    /// # Safety
    /// `handle` must be a valid event handle.
    pub(crate) unsafe fn from_handle(handle: SPXEVENTHANDLE) -> Result<RecognitionEvent> {
        let mut offset: u64 = 0;
        let ret = recognizer_recognition_event_get_offset(handle, &mut offset);
        if ret != 0 {
            recognizer_event_handle_release(handle);
            convert_err(ret, "RecognitionEvent::from_handle error")?;
        }

        // Offset is in 100-nanosecond ticks
        let duration = Duration::from_nanos(offset * 100);

        recognizer_event_handle_release(handle);

        Ok(RecognitionEvent { offset: duration })
    }
}
