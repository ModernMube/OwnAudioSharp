//! FFI exports for MIDI port enumeration and the input/output port lifecycle.

use std::ffi::{CStr, CString};
use std::os::raw::{c_char, c_void};
use std::panic::{catch_unwind, AssertUnwindSafe};

use ownaudio_midi_core::{
    list_input_port_names, list_output_port_names, MidiInputPort, MidiOutputPort,
};

use crate::error_code::MidiErrorCode;
use crate::handles::{
    input_from_ptr, output_from_ptr, InputPortWrapper, MidiInputPortHandle, MidiOutputPortHandle,
    OutputPortWrapper,
};
use crate::types::NativeMidiMessage;

/// Callback type the C# layer registers for short incoming MIDI messages.
pub type MidiMessageCallback = extern "C" fn(NativeMidiMessage, *mut c_void);

/// Callback type the C# layer registers for incoming SysEx messages.
pub type MidiSysExCallback = extern "C" fn(*const u8, usize, *mut c_void);

/// Holds the C# callbacks and user pointer for the duration of an active input
/// connection, dispatching each complete message to the right callback.
struct InputCallbackState {
    msg_callback: Option<MidiMessageCallback>,
    sysex_callback: Option<MidiSysExCallback>,
    user_data: *mut c_void,
}

// SAFETY: the caller guarantees `user_data` remains valid for the lifetime of
// the connection and that the callbacks may be invoked from the backend thread.
unsafe impl Send for InputCallbackState {}

impl InputCallbackState {
    /// Routes one complete MIDI message to the appropriate callback. Any panic
    /// inside the foreign callback is caught so it cannot unwind into `midir`.
    fn dispatch(&self, timestamp_us: u64, data: &[u8]) {
        if data.is_empty() {
            return;
        }

        if data[0] == 0xF0 {
            if let Some(cb) = self.sysex_callback {
                let ptr = data.as_ptr();
                let len = data.len();
                let user_data = self.user_data;
                let _ = catch_unwind(AssertUnwindSafe(|| cb(ptr, len, user_data)));
            }
            return;
        }

        if let Some(cb) = self.msg_callback {
            let msg = NativeMidiMessage {
                status: data[0],
                data1: data.get(1).copied().unwrap_or(0),
                data2: data.get(2).copied().unwrap_or(0),
                _pad: 0,
                timestamp_us: timestamp_us as i64,
            };
            let user_data = self.user_data;
            let _ = catch_unwind(AssertUnwindSafe(|| cb(msg, user_data)));
        }
    }
}

// ---------------------------------------------------------------------------
// Port enumeration
// ---------------------------------------------------------------------------

/// Writes a newly allocated array of null-terminated UTF-8 input port names to
/// `*out_names` and its length to `*out_count`.
///
/// The caller must release the array with `ownaudio_midi_v1_free_port_names`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_list_input_ports(
    out_names: *mut *mut *mut c_char,
    out_count: *mut usize,
) -> i32 {
    list_ports(out_names, out_count, list_input_port_names)
}

/// Writes a newly allocated array of null-terminated UTF-8 output port names to
/// `*out_names` and its length to `*out_count`.
///
/// The caller must release the array with `ownaudio_midi_v1_free_port_names`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_list_output_ports(
    out_names: *mut *mut *mut c_char,
    out_count: *mut usize,
) -> i32 {
    list_ports(out_names, out_count, list_output_port_names)
}

/// Shared implementation for the two port-enumeration exports.
fn list_ports(
    out_names: *mut *mut *mut c_char,
    out_count: *mut usize,
    enumerate: fn() -> Vec<String>,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if out_names.is_null() || out_count.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }

        let names = enumerate();
        let ptrs: Vec<*mut c_char> = names
            .into_iter()
            .map(|s| CString::new(s).unwrap_or_default().into_raw())
            .collect();
        let count = ptrs.len();
        let boxed = ptrs.into_boxed_slice();

        unsafe {
            *out_names = Box::into_raw(boxed) as *mut *mut c_char;
            *out_count = count;
        }
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Releases a port-name array previously returned by one of the enumeration
/// exports. Passing `null` is safe.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_free_port_names(names: *mut *mut c_char, count: usize) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if names.is_null() {
            return;
        }
        unsafe {
            let slice = std::slice::from_raw_parts_mut(names, count);
            for &mut entry in slice.iter_mut() {
                if !entry.is_null() {
                    drop(CString::from_raw(entry));
                }
            }
            drop(Box::from_raw(slice as *mut [*mut c_char]));
        }
    }));
}

// ---------------------------------------------------------------------------
// Input port lifecycle
// ---------------------------------------------------------------------------

/// Opens a hardware MIDI input port by name and writes its handle to
/// `*out_handle`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_input_port_open(
    port_name: *const c_char,
    out_handle: *mut *mut MidiInputPortHandle,
) -> i32 {
    open_input(port_name, out_handle, MidiInputPort::open)
}

/// Creates a virtual MIDI input port and writes its handle to `*out_handle`.
///
/// Returns [`MidiErrorCode::PlatformUnsupported`] where virtual ports are not
/// available (for example under WinMM on Windows).
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_create_virtual_input(
    name: *const c_char,
    out_handle: *mut *mut MidiInputPortHandle,
) -> i32 {
    open_input(name, out_handle, MidiInputPort::create_virtual)
}

/// Shared open implementation for hardware and virtual input ports.
fn open_input(
    name: *const c_char,
    out_handle: *mut *mut MidiInputPortHandle,
    factory: fn(&str) -> Result<MidiInputPort, ownaudio_midi_core::MidiError>,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if name.is_null() || out_handle.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }

        let name_str = match unsafe { CStr::from_ptr(name) }.to_str() {
            Ok(s) => s,
            Err(_) => return MidiErrorCode::InvalidHandle as i32,
        };

        match factory(name_str) {
            Ok(port) => {
                let boxed = Box::new(InputPortWrapper { inner: port });
                unsafe {
                    *out_handle = Box::into_raw(boxed) as *mut MidiInputPortHandle;
                }
                MidiErrorCode::Success as i32
            }
            Err(e) => MidiErrorCode::from(e) as i32,
        }
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Starts delivering incoming short messages to `callback`, ignoring SysEx.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_input_port_start(
    handle: *mut MidiInputPortHandle,
    callback: Option<MidiMessageCallback>,
    user_data: *mut c_void,
) -> i32 {
    start_input(handle, callback, None, user_data)
}

/// Starts delivering incoming short messages and SysEx messages to the two
/// supplied callbacks.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_input_port_start_with_sysex(
    handle: *mut MidiInputPortHandle,
    msg_callback: Option<MidiMessageCallback>,
    sysex_callback: Option<MidiSysExCallback>,
    user_data: *mut c_void,
) -> i32 {
    start_input(handle, msg_callback, sysex_callback, user_data)
}

/// Shared implementation that connects the backend and installs the trampoline.
fn start_input(
    handle: *mut MidiInputPortHandle,
    msg_callback: Option<MidiMessageCallback>,
    sysex_callback: Option<MidiSysExCallback>,
    user_data: *mut c_void,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        let wrapper = match unsafe { input_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };

        let state = InputCallbackState {
            msg_callback,
            sysex_callback,
            user_data,
        };

        match wrapper
            .inner
            .start(Box::new(move |stamp, data| state.dispatch(stamp, data)))
        {
            Ok(()) => MidiErrorCode::Success as i32,
            Err(e) => MidiErrorCode::from(e) as i32,
        }
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Stops delivery of incoming messages, closing the active connection.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_input_port_stop(handle: *mut MidiInputPortHandle) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        let wrapper = match unsafe { input_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };
        wrapper.inner.stop();
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Destroys an input port handle, stopping delivery first. Passing `null` is safe.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_input_port_destroy(handle: *mut MidiInputPortHandle) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(handle as *mut InputPortWrapper));
        }
    }));
}

// ---------------------------------------------------------------------------
// Output port lifecycle
// ---------------------------------------------------------------------------

/// Opens a hardware MIDI output port by name and writes its handle to
/// `*out_handle`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_output_port_open(
    port_name: *const c_char,
    out_handle: *mut *mut MidiOutputPortHandle,
) -> i32 {
    open_output(port_name, out_handle, MidiOutputPort::open)
}

/// Creates a virtual MIDI output port and writes its handle to `*out_handle`.
///
/// Returns [`MidiErrorCode::PlatformUnsupported`] where virtual ports are not
/// available.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_create_virtual_output(
    name: *const c_char,
    out_handle: *mut *mut MidiOutputPortHandle,
) -> i32 {
    open_output(name, out_handle, MidiOutputPort::create_virtual)
}

/// Shared open implementation for hardware and virtual output ports.
fn open_output(
    name: *const c_char,
    out_handle: *mut *mut MidiOutputPortHandle,
    factory: fn(&str) -> Result<MidiOutputPort, ownaudio_midi_core::MidiError>,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if name.is_null() || out_handle.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }

        let name_str = match unsafe { CStr::from_ptr(name) }.to_str() {
            Ok(s) => s,
            Err(_) => return MidiErrorCode::InvalidHandle as i32,
        };

        match factory(name_str) {
            Ok(port) => {
                let boxed = Box::new(OutputPortWrapper { inner: port });
                unsafe {
                    *out_handle = Box::into_raw(boxed) as *mut MidiOutputPortHandle;
                }
                MidiErrorCode::Success as i32
            }
            Err(e) => MidiErrorCode::from(e) as i32,
        }
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Sends a short MIDI message to the output port.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_output_port_send(
    handle: *mut MidiOutputPortHandle,
    msg: NativeMidiMessage,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        let wrapper = match unsafe { output_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };

        match wrapper.inner.send(msg.status, msg.data1, msg.data2) {
            Ok(()) => MidiErrorCode::Success as i32,
            Err(e) => MidiErrorCode::from(e) as i32,
        }
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Sends a raw SysEx byte sequence to the output port.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_output_port_send_sysex(
    handle: *mut MidiOutputPortHandle,
    data: *const u8,
    len: usize,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if data.is_null() && len != 0 {
            return MidiErrorCode::NullPointer as i32;
        }

        let wrapper = match unsafe { output_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };

        let bytes = if len == 0 {
            &[][..]
        } else {
            unsafe { std::slice::from_raw_parts(data, len) }
        };

        match wrapper.inner.send_raw(bytes) {
            Ok(()) => MidiErrorCode::Success as i32,
            Err(e) => MidiErrorCode::from(e) as i32,
        }
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Destroys an output port handle. Passing `null` is safe.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_output_port_destroy(handle: *mut MidiOutputPortHandle) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(handle as *mut OutputPortWrapper));
        }
    }));
}
