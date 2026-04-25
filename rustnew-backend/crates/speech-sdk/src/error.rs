//! Error types for the Speech SDK bindings.

use std::fmt;

/// Result type alias used throughout the SDK.
pub type Result<T> = std::result::Result<T, Error>;

/// Error type wrapping Speech SDK error codes.
#[derive(Debug)]
pub struct Error {
    message: String,
    code: usize,
}

impl Error {
    pub fn new(message: String, code: usize) -> Self {
        Error { message, code }
    }

    pub fn code(&self) -> usize {
        self.code
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "SpeechSDK error 0x{:04X}: {}", self.code, self.message)
    }
}

impl std::error::Error for Error {}

impl From<std::ffi::NulError> for Error {
    fn from(e: std::ffi::NulError) -> Self {
        Error::new(format!("NulError: {}", e), 0)
    }
}

/// Convert a C API return code to a Result.
/// If the return code is SPX_NOERROR, returns Ok(()).
/// Otherwise, returns an Err with the error code and context message.
#[inline]
pub fn convert_err(code: usize, context: &str) -> Result<()> {
    if code == crate::ffi::SPX_NOERROR as usize {
        Ok(())
    } else {
        Err(Error::new(context.to_string(), code))
    }
}
