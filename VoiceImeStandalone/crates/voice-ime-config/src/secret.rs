//! 订阅密钥本机加密保护（Windows 原生 DPAPI `CryptProtectData` / `CryptUnprotectData`）。
//!
//! - 在 Windows 上按当前登录用户 SID 绑定加密，密文前缀为 `dpapi:v1:`；其他用户或机器无法解密。
//! - 若读取到旧版或手工填入的未加密字符串，运行时兼容读取并在下次保存时自动升级为密文。

use base64::{engine::general_purpose::STANDARD as BASE64, Engine as _};
use thiserror::Error;

const DPAPI_PREFIX: &str = "dpapi:v1:";
const PORTABLE_PREFIX: &str = "obf:v1:";

#[derive(Debug, Error)]
pub enum SecretError {
    #[error("凭据加密失败")]
    ProtectFailed,
    #[error("凭据解密失败")]
    UnprotectFailed,
}

pub fn is_protected_secret(value: &str) -> bool {
    value.starts_with(DPAPI_PREFIX) || value.starts_with(PORTABLE_PREFIX)
}

pub fn protect_secret(plain: &str) -> Result<String, SecretError> {
    if is_protected_secret(plain) {
        return Ok(plain.to_string());
    }
    protect_impl(plain.as_bytes())
}

pub fn unprotect_secret(stored: &str) -> Result<String, SecretError> {
    if let Some(b64) = stored.strip_prefix(DPAPI_PREFIX) {
        let cipher = BASE64.decode(b64).map_err(|_| SecretError::UnprotectFailed)?;
        let bytes = unprotect_dpapi_bytes(&cipher)?;
        return String::from_utf8(bytes).map_err(|_| SecretError::UnprotectFailed);
    }
    if let Some(b64) = stored.strip_prefix(PORTABLE_PREFIX) {
        let bytes = BASE64.decode(b64).map_err(|_| SecretError::UnprotectFailed)?;
        return String::from_utf8(bytes).map_err(|_| SecretError::UnprotectFailed);
    }
    // 兼容尚未加密的明文输入
    Ok(stored.to_string())
}

#[cfg(windows)]
fn protect_impl(plain: &[u8]) -> Result<String, SecretError> {
    use std::ptr::{null, null_mut};
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Cryptography::{
        CryptProtectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
    };

    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: plain.len() as u32,
            pbData: plain.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: null_mut(),
        };
        let ok = CryptProtectData(
            &input,
            null(),
            null(),
            null_mut(),
            null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        );
        if ok == 0 || output.pbData.is_null() {
            return Err(SecretError::ProtectFailed);
        }
        let slice = std::slice::from_raw_parts(output.pbData, output.cbData as usize);
        let encoded = BASE64.encode(slice);
        LocalFree(output.pbData as _);
        Ok(format!("{DPAPI_PREFIX}{encoded}"))
    }
}

#[cfg(not(windows))]
fn protect_impl(plain: &[u8]) -> Result<String, SecretError> {
    Ok(format!("{PORTABLE_PREFIX}{}", BASE64.encode(plain)))
}

#[cfg(windows)]
fn unprotect_dpapi_bytes(cipher: &[u8]) -> Result<Vec<u8>, SecretError> {
    use std::ptr::{null, null_mut};
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Cryptography::{
        CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
    };

    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: cipher.len() as u32,
            pbData: cipher.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: null_mut(),
        };
        let ok = CryptUnprotectData(
            &input,
            null_mut(),
            null(),
            null_mut(),
            null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        );
        if ok == 0 || output.pbData.is_null() {
            return Err(SecretError::UnprotectFailed);
        }
        let out = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        LocalFree(output.pbData as _);
        Ok(out)
    }
}

#[cfg(not(windows))]
fn unprotect_dpapi_bytes(_cipher: &[u8]) -> Result<Vec<u8>, SecretError> {
    Err(SecretError::UnprotectFailed)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn secret_roundtrip_preserves_exact_key() {
        let key = "0123456789abcdef0123456789abcdef";
        let protected = protect_secret(key).unwrap();
        assert!(is_protected_secret(&protected));
        assert_ne!(protected, key);
        assert_eq!(unprotect_secret(&protected).unwrap(), key);
    }
}
