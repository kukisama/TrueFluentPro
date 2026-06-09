//! Connection — 显式管理识别器/合成器与服务的连接。
//!
//! 对应微软 Speech SDK 的 `Connection`：可主动 `open`/`close`，
//! 设置消息属性、发送自定义消息，并监听 connected/disconnected 事件。

use crate::error::{convert_err, Result};
use crate::ffi::{
    connection_async_handle_release, connection_close, connection_connected_set_callback,
    connection_disconnected_set_callback, connection_from_recognizer,
    connection_from_speech_synthesizer, connection_handle_release, connection_open,
    connection_send_message_async, connection_send_message_wait_for, connection_set_message_property,
    recognizer_session_event_get_session_id, SmartHandle, SPXASYNCHANDLE, SPXCONNECTIONHANDLE,
    SPXEVENTHANDLE,
};
use crate::speech::{SpeechRecognizer, SpeechSynthesizer};
use std::ffi::CString;
use std::mem::MaybeUninit;
use std::os::raw::c_void;

type ConnectionCb = Box<dyn Fn(String) + 'static + Send>;

#[derive(Default)]
struct CallbackBag {
    connected_cb: Option<ConnectionCb>,
    disconnected_cb: Option<ConnectionCb>,
}

/// 显式连接对象。
pub struct Connection {
    handle: SmartHandle<SPXCONNECTIONHANDLE>,
    callback_bag: Box<CallbackBag>,
}

impl std::fmt::Debug for Connection {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Connection").field("handle", &self.handle).finish()
    }
}

impl Connection {
    /// 从识别器获取连接对象。
    pub fn from_recognizer(recognizer: &SpeechRecognizer) -> Result<Connection> {
        unsafe {
            let mut handle: MaybeUninit<SPXCONNECTIONHANDLE> = MaybeUninit::uninit();
            let ret = connection_from_recognizer(recognizer.handle_inner(), handle.as_mut_ptr());
            convert_err(ret, "Connection::from_recognizer error")?;
            Ok(Self::new(handle.assume_init()))
        }
    }

    /// 从合成器获取连接对象。
    pub fn from_speech_synthesizer(synthesizer: &SpeechSynthesizer) -> Result<Connection> {
        unsafe {
            let mut handle: MaybeUninit<SPXCONNECTIONHANDLE> = MaybeUninit::uninit();
            let ret =
                connection_from_speech_synthesizer(synthesizer.handle_inner(), handle.as_mut_ptr());
            convert_err(ret, "Connection::from_speech_synthesizer error")?;
            Ok(Self::new(handle.assume_init()))
        }
    }

    unsafe fn new(handle: SPXCONNECTIONHANDLE) -> Connection {
        Connection {
            handle: SmartHandle::create("Connection", handle, connection_handle_release),
            callback_bag: Box::new(CallbackBag::default()),
        }
    }

    /// 主动打开连接；`for_continuous_recognition` 表示用于连续识别。
    pub fn open(&self, for_continuous_recognition: bool) -> Result<()> {
        unsafe {
            let ret = connection_open(self.handle.inner(), for_continuous_recognition);
            convert_err(ret, "Connection::open error")
        }
    }

    /// 关闭连接。
    pub fn close(&self) -> Result<()> {
        unsafe {
            let ret = connection_close(self.handle.inner());
            convert_err(ret, "Connection::close error")
        }
    }

    /// 设置发往服务的消息属性。
    pub fn set_message_property(&self, path: &str, name: &str, value: &str) -> Result<()> {
        let c_path = CString::new(path)?;
        let c_name = CString::new(name)?;
        let c_value = CString::new(value)?;
        unsafe {
            let ret = connection_set_message_property(
                self.handle.inner(),
                c_path.as_ptr(),
                c_name.as_ptr(),
                c_value.as_ptr(),
            );
            convert_err(ret, "Connection::set_message_property error")
        }
    }

    /// 发送一条自定义文本消息（阻塞直到完成）。
    pub fn send_message(&self, path: &str, payload: &str) -> Result<()> {
        let c_path = CString::new(path)?;
        let c_payload = CString::new(payload)?;
        unsafe {
            let mut h_async: MaybeUninit<SPXASYNCHANDLE> = MaybeUninit::uninit();
            let ret = connection_send_message_async(
                self.handle.inner(),
                c_path.as_ptr(),
                c_payload.as_ptr(),
                h_async.as_mut_ptr(),
            );
            convert_err(ret, "Connection::send_message start error")?;
            let h_async = h_async.assume_init();
            let ret = connection_send_message_wait_for(h_async, u32::MAX);
            connection_async_handle_release(h_async);
            convert_err(ret, "Connection::send_message wait error")
        }
    }

    /// 监听 connected 事件，回调参数为 session id。
    pub fn set_connected_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(String) + 'static + Send,
    {
        self.callback_bag.connected_cb = Some(Box::new(f));
        unsafe {
            let ret = connection_connected_set_callback(
                self.handle.inner(),
                Some(Self::cb_connected),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "Connection::set_connected_cb error")
        }
    }

    /// 监听 disconnected 事件，回调参数为 session id。
    pub fn set_disconnected_cb<F>(&mut self, f: F) -> Result<()>
    where
        F: Fn(String) + 'static + Send,
    {
        self.callback_bag.disconnected_cb = Some(Box::new(f));
        unsafe {
            let ret = connection_disconnected_set_callback(
                self.handle.inner(),
                Some(Self::cb_disconnected),
                &*self.callback_bag as *const _ as *mut c_void,
            );
            convert_err(ret, "Connection::set_disconnected_cb error")
        }
    }

    unsafe fn session_id_from_event(hevent: SPXEVENTHANDLE) -> String {
        let mut buf = vec![0u8; 64];
        let ret = recognizer_session_event_get_session_id(
            hevent,
            buf.as_mut_ptr() as *mut std::os::raw::c_char,
            buf.len() as u32,
        );
        if ret != 0 {
            return String::new();
        }
        std::ffi::CStr::from_ptr(buf.as_ptr() as *const std::os::raw::c_char)
            .to_string_lossy()
            .into_owned()
    }

    unsafe extern "C" fn cb_connected(hevent: SPXEVENTHANDLE, context: *mut c_void) {
        let bag = &*(context as *const CallbackBag);
        if let Some(cb) = &bag.connected_cb {
            cb(Self::session_id_from_event(hevent));
        }
    }

    unsafe extern "C" fn cb_disconnected(hevent: SPXEVENTHANDLE, context: *mut c_void) {
        let bag = &*(context as *const CallbackBag);
        if let Some(cb) = &bag.disconnected_cb {
            cb(Self::session_id_from_event(hevent));
        }
    }
}

impl Drop for Connection {
    fn drop(&mut self) {
        unsafe {
            let _ = connection_connected_set_callback(
                self.handle.inner(),
                None,
                std::ptr::null_mut(),
            );
            let _ = connection_disconnected_set_callback(
                self.handle.inner(),
                None,
                std::ptr::null_mut(),
            );
        }
    }
}
