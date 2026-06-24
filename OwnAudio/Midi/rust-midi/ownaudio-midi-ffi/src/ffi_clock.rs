//! FFI exports for the MIDI timing clock.
//!
//! The clock fires a pulse callback into the managed layer for every 24 PPQN
//! tick. Sending the actual 0xF8 Timing Clock message — and the transport
//! Start/Stop/Continue messages — is done by the C# side, so the clock works
//! with any `IMidiOutputPort`, including pure managed test doubles. This keeps
//! the timing source decoupled from any particular native port.

use std::os::raw::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};

use ownaudio_midi_core::MidiClock;

use crate::error_code::MidiErrorCode;
use crate::handles::{clock_from_ptr, ClockWrapper, MidiClockHandle};

/// Callback type invoked once per timing pulse.
pub type MidiPulseCallback = extern "C" fn(*mut c_void);

/// Holds the pulse callback and user pointer for the running clock thread.
struct PulseState {
    callback: MidiPulseCallback,
    user_data: *mut c_void,
}

// SAFETY: the caller guarantees `user_data` stays valid while the clock runs and
// that the callback may be invoked from the clock thread.
unsafe impl Send for PulseState {}

impl PulseState {
    /// Invokes the managed pulse callback, catching any panic it raises.
    fn fire(&self) {
        let callback = self.callback;
        let user_data = self.user_data;
        let _ = catch_unwind(AssertUnwindSafe(|| callback(user_data)));
    }
}

/// Creates a stopped clock at `bpm` (clamped to [20, 300]) and writes its handle
/// to `*out_handle`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_clock_create(
    bpm: f64,
    out_handle: *mut *mut MidiClockHandle,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if out_handle.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }

        let boxed = Box::new(ClockWrapper {
            inner: MidiClock::new(bpm),
        });
        unsafe {
            *out_handle = Box::into_raw(boxed) as *mut MidiClockHandle;
        }
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Sets the clock tempo in beats per minute (clamped to [20, 300]).
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_clock_set_bpm(handle: *mut MidiClockHandle, bpm: f64) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        let wrapper = match unsafe { clock_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };
        wrapper.inner.set_bpm(bpm);
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Starts the clock thread, invoking `callback` for each timing pulse.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_clock_start(
    handle: *mut MidiClockHandle,
    callback: Option<MidiPulseCallback>,
    user_data: *mut c_void,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        let wrapper = match unsafe { clock_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };

        let callback = match callback {
            Some(cb) => cb,
            None => return MidiErrorCode::NullPointer as i32,
        };

        let state = PulseState { callback, user_data };
        wrapper.inner.start(Box::new(move || state.fire()));
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Stops the clock thread and waits for it to exit.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_clock_stop(handle: *mut MidiClockHandle) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        let wrapper = match unsafe { clock_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };
        wrapper.inner.stop();
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Destroys a clock handle, stopping the thread first. Passing `null` is safe.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_clock_destroy(handle: *mut MidiClockHandle) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(handle as *mut ClockWrapper));
        }
    }));
}
