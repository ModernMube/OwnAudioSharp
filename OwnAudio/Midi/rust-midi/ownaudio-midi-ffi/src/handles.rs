//! Opaque handle types and their internal wrappers.
//!
//! Each handle is created with `Box::into_raw` and released by its matching
//! `_destroy` export through `Box::from_raw`. The C# side only ever holds the
//! pointers as `SafeHandle` values and never dereferences them.

use ownaudio_midi_core::{MidiClock, MidiFileData, MidiInputPort, MidiOutputPort, MidiTrackData};

/// Opaque handle to a native MIDI input port.
#[repr(C)]
pub struct MidiInputPortHandle {
    _private: [u8; 0],
}

/// Opaque handle to a native MIDI output port.
#[repr(C)]
pub struct MidiOutputPortHandle {
    _private: [u8; 0],
}

/// Opaque handle to a native MIDI timing clock.
#[repr(C)]
pub struct MidiClockHandle {
    _private: [u8; 0],
}

/// Opaque handle to a parsed MIDI file held in native memory.
#[repr(C)]
pub struct MidiFileHandle {
    _private: [u8; 0],
}

/// Opaque handle to an in-progress MIDI file serializer.
#[repr(C)]
pub struct MidiWriterHandle {
    _private: [u8; 0],
}

// ---------------------------------------------------------------------------
// Internal wrappers — never exposed across the FFI boundary
// ---------------------------------------------------------------------------

/// Owns a MIDI input port behind its opaque handle.
pub(crate) struct InputPortWrapper {
    pub inner: MidiInputPort,
}

/// Owns a MIDI output port behind its opaque handle.
pub(crate) struct OutputPortWrapper {
    pub inner: MidiOutputPort,
}

/// Owns a timing clock behind its opaque handle.
pub(crate) struct ClockWrapper {
    pub inner: MidiClock,
}

/// Owns a parsed MIDI file behind its opaque handle.
pub(crate) struct FileWrapper {
    pub inner: MidiFileData,
}

/// Accumulates tracks and events for the MIDI file serializer.
pub(crate) struct WriterWrapper {
    /// SMF format word to serialize.
    pub format: u16,
    /// Timing resolution in ticks per quarter note.
    pub ticks_per_beat: u16,
    /// Tracks added so far through `writer_begin_track` / `writer_add_event`.
    pub tracks: Vec<MidiTrackData>,
}

// SAFETY: midir connection objects are not all `Send` on every platform. The
// FFI contract places thread-safety responsibility on the caller: a handle must
// not be used concurrently from multiple threads without external
// synchronization. This mirrors the established convention of the audio FFI.
unsafe impl Send for OutputPortWrapper {}
unsafe impl Send for InputPortWrapper {}

// ---------------------------------------------------------------------------
// Pointer helpers
// ---------------------------------------------------------------------------

/// Casts an input port handle back to its wrapper, or `None` if null.
///
/// # Safety
/// `ptr` must originate from `ownaudio_midi_v1_input_port_open` (or a virtual
/// variant) and must not have been destroyed.
pub(crate) unsafe fn input_from_ptr<'a>(
    ptr: *mut MidiInputPortHandle,
) -> Option<&'a mut InputPortWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut InputPortWrapper))
    }
}

/// Casts an output port handle back to its wrapper, or `None` if null.
///
/// # Safety
/// `ptr` must originate from an output-port open call and must not have been
/// destroyed.
pub(crate) unsafe fn output_from_ptr<'a>(
    ptr: *mut MidiOutputPortHandle,
) -> Option<&'a mut OutputPortWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut OutputPortWrapper))
    }
}

/// Casts a clock handle back to its wrapper, or `None` if null.
///
/// # Safety
/// `ptr` must originate from `ownaudio_midi_v1_clock_create` and must not have
/// been destroyed.
pub(crate) unsafe fn clock_from_ptr<'a>(
    ptr: *mut MidiClockHandle,
) -> Option<&'a mut ClockWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut ClockWrapper))
    }
}

/// Casts a file handle back to its wrapper, or `None` if null.
///
/// # Safety
/// `ptr` must originate from `ownaudio_midi_v1_file_parse` and must not have
/// been destroyed.
pub(crate) unsafe fn file_from_ptr<'a>(
    ptr: *mut MidiFileHandle,
) -> Option<&'a mut FileWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut FileWrapper))
    }
}

/// Casts a writer handle back to its wrapper, or `None` if null.
///
/// # Safety
/// `ptr` must originate from `ownaudio_midi_v1_writer_create` and must not have
/// been destroyed.
pub(crate) unsafe fn writer_from_ptr<'a>(
    ptr: *mut MidiWriterHandle,
) -> Option<&'a mut WriterWrapper> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *(ptr as *mut WriterWrapper))
    }
}
