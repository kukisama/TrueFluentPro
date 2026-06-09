//! 诊断与日志（Diagnostics / Logging）。
//!
//! 对应微软 Speech SDK 的日志能力，提供三种 MS 风格的静态日志器：
//! - [`FileLogger`]：将 SDK 内部日志写入文件。
//! - [`MemoryLogger`]：将日志保存在内存环形缓冲，可随时读取或转储。
//! - [`ConsoleLogger`]：将日志输出到控制台（stdout/stderr）。
//!
//! 这些日志器为进程级全局开关（与官方 C API 一致）。

use crate::error::{convert_err, Result};
use crate::ffi::{
    diagnostics_log_console_set_filters, diagnostics_log_console_start_logging,
    diagnostics_log_console_stop_logging, diagnostics_log_memory_dump,
    diagnostics_log_memory_get_line, diagnostics_log_memory_get_line_num_newest,
    diagnostics_log_memory_get_line_num_oldest, diagnostics_log_memory_set_filters,
    diagnostics_log_memory_start_logging, diagnostics_log_memory_stop_logging,
    diagnostics_log_start_logging, diagnostics_log_stop_logging, property_bag_create,
    property_bag_release, property_bag_set_string, Speech_LogFilename,
};
use std::ffi::{CStr, CString};
use std::ptr;

/// 文件日志器：将 SDK 内部日志写入指定文件。
pub struct FileLogger;

impl FileLogger {
    /// 开始将日志写入指定文件。
    pub fn start(file_path: &str) -> Result<()> {
        let c_path = CString::new(file_path)?;
        unsafe {
            let mut hpropbag: crate::ffi::AZAC_HANDLE = ptr::null_mut();
            let ret = property_bag_create(&mut hpropbag);
            convert_err(ret, "FileLogger::start property_bag_create error")?;

            let ret = property_bag_set_string(
                hpropbag,
                Speech_LogFilename as i32,
                ptr::null(),
                c_path.as_ptr(),
            );
            if ret != 0 {
                property_bag_release(hpropbag);
                convert_err(ret, "FileLogger::start set log filename error")?;
            }

            let ret = diagnostics_log_start_logging(hpropbag, ptr::null_mut());
            property_bag_release(hpropbag);
            convert_err(ret, "FileLogger::start start_logging error")
        }
    }

    /// 停止文件日志。
    pub fn stop() -> Result<()> {
        unsafe { convert_err(diagnostics_log_stop_logging(), "FileLogger::stop error") }
    }
}

/// 内存日志器：将日志保存在内存环形缓冲。
pub struct MemoryLogger;

impl MemoryLogger {
    /// 开始内存日志。
    pub fn start() {
        unsafe { diagnostics_log_memory_start_logging() }
    }

    /// 停止内存日志。
    pub fn stop() {
        unsafe { diagnostics_log_memory_stop_logging() }
    }

    /// 设置日志过滤器（以 ";" 分隔的关键字；匹配的日志行才会被记录）。
    pub fn set_filters(filters: &str) -> Result<()> {
        let c_filters = CString::new(filters)?;
        unsafe { diagnostics_log_memory_set_filters(c_filters.as_ptr()) }
        Ok(())
    }

    /// 清除过滤器。
    pub fn clear_filters() {
        unsafe { diagnostics_log_memory_set_filters(ptr::null()) }
    }

    /// 当前缓冲中的日志行数。
    pub fn line_count() -> usize {
        unsafe {
            let oldest = diagnostics_log_memory_get_line_num_oldest();
            let newest = diagnostics_log_memory_get_line_num_newest();
            newest.saturating_sub(oldest)
        }
    }

    /// 读取当前内存缓冲中的全部日志行（从最旧到最新）。
    pub fn lines() -> Vec<String> {
        unsafe {
            let oldest = diagnostics_log_memory_get_line_num_oldest();
            let newest = diagnostics_log_memory_get_line_num_newest();
            let mut out = Vec::new();
            for i in oldest..newest {
                let ptr = diagnostics_log_memory_get_line(i);
                if ptr.is_null() {
                    continue;
                }
                out.push(CStr::from_ptr(ptr).to_string_lossy().into_owned());
            }
            out
        }
    }

    /// 将内存日志转储到文件（可选同时输出到 stdout/stderr）。
    pub fn dump(
        file_path: &str,
        line_prefix: &str,
        emit_to_stdout: bool,
        emit_to_stderr: bool,
    ) -> Result<()> {
        let c_path = CString::new(file_path)?;
        let c_prefix = CString::new(line_prefix)?;
        unsafe {
            let ret = diagnostics_log_memory_dump(
                c_path.as_ptr(),
                c_prefix.as_ptr(),
                emit_to_stdout,
                emit_to_stderr,
            );
            convert_err(ret, "MemoryLogger::dump error")
        }
    }
}

/// 控制台日志器：将日志输出到控制台。
pub struct ConsoleLogger;

impl ConsoleLogger {
    /// 开始控制台日志（输出到 stdout）。
    pub fn start() {
        unsafe { diagnostics_log_console_start_logging(false) }
    }

    /// 开始控制台日志（输出到 stderr）。
    pub fn start_to_stderr() {
        unsafe { diagnostics_log_console_start_logging(true) }
    }

    /// 停止控制台日志。
    pub fn stop() {
        unsafe { diagnostics_log_console_stop_logging() }
    }

    /// 设置日志过滤器（以 ";" 分隔的关键字）。
    pub fn set_filters(filters: &str) -> Result<()> {
        let c_filters = CString::new(filters)?;
        unsafe { diagnostics_log_console_set_filters(c_filters.as_ptr()) }
        Ok(())
    }

    /// 清除过滤器。
    pub fn clear_filters() {
        unsafe { diagnostics_log_console_set_filters(ptr::null()) }
    }
}
