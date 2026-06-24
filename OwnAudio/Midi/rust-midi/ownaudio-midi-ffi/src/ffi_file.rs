//! FFI exports for Standard MIDI File parsing and serialization.
//!
//! Parsing produces an opaque [`MidiFileHandle`] that the C# layer queries event
//! by event to rebuild its managed `MidiFile` model. Serialization uses an
//! incremental writer handle: the C# layer creates a writer, adds tracks and
//! events, then serializes to a byte buffer that it releases with
//! `ownaudio_midi_v1_free_bytes`.

use std::panic::{catch_unwind, AssertUnwindSafe};

use ownaudio_midi_core::{
    parse_midi_file, write_midi_file, MidiEventData, MidiEventKind, MidiFileData, MidiTrackData,
};

use crate::error_code::MidiErrorCode;
use crate::handles::{
    file_from_ptr, writer_from_ptr, FileWrapper, MidiFileHandle, MidiWriterHandle, WriterWrapper,
};
use crate::types::NativeMidiEvent;

// ---------------------------------------------------------------------------
// Parsing and querying
// ---------------------------------------------------------------------------

/// Parses a Standard MIDI File from `data` and writes its handle to
/// `*out_handle`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_file_parse(
    data: *const u8,
    len: usize,
    out_handle: *mut *mut MidiFileHandle,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if data.is_null() || out_handle.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }

        let bytes = unsafe { std::slice::from_raw_parts(data, len) };
        match parse_midi_file(bytes) {
            Ok(file) => {
                let boxed = Box::new(FileWrapper { inner: file });
                unsafe {
                    *out_handle = Box::into_raw(boxed) as *mut MidiFileHandle;
                }
                MidiErrorCode::Success as i32
            }
            Err(e) => MidiErrorCode::from(e) as i32,
        }
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Writes the parsed file's SMF format word to `*out_format`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_file_get_format(
    handle: *mut MidiFileHandle,
    out_format: *mut u16,
) -> i32 {
    read_file_field(handle, out_format, |f| f.inner.format)
}

/// Writes the parsed file's ticks-per-beat resolution to `*out_tpb`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_file_get_ticks_per_beat(
    handle: *mut MidiFileHandle,
    out_tpb: *mut u16,
) -> i32 {
    read_file_field(handle, out_tpb, |f| f.inner.ticks_per_beat)
}

/// Writes the parsed file's track count to `*out_count`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_file_get_track_count(
    handle: *mut MidiFileHandle,
    out_count: *mut usize,
) -> i32 {
    read_file_field(handle, out_count, |f| f.inner.tracks.len())
}

/// Writes the number of events in track `track_index` to `*out_count`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_file_get_event_count(
    handle: *mut MidiFileHandle,
    track_index: usize,
    out_count: *mut usize,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if out_count.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { file_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };
        let track = match wrapper.inner.tracks.get(track_index) {
            Some(t) => t,
            None => return MidiErrorCode::InvalidHandle as i32,
        };
        unsafe {
            *out_count = track.events.len();
        }
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Writes the event at (`track_index`, `event_index`) into `*out_event`.
///
/// For meta and SysEx events the `meta_data` pointer references memory owned by
/// `handle` and stays valid until the handle is destroyed.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_file_get_event(
    handle: *mut MidiFileHandle,
    track_index: usize,
    event_index: usize,
    out_event: *mut NativeMidiEvent,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if out_event.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { file_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };
        let event = match wrapper
            .inner
            .tracks
            .get(track_index)
            .and_then(|t| t.events.get(event_index))
        {
            Some(e) => e,
            None => return MidiErrorCode::InvalidHandle as i32,
        };

        let (meta_ptr, meta_len) = if event.meta_data.is_empty() {
            (std::ptr::null(), 0)
        } else {
            (event.meta_data.as_ptr(), event.meta_data.len())
        };

        unsafe {
            *out_event = NativeMidiEvent {
                delta_time: event.delta_time,
                event_type: event.kind.as_u8(),
                status: event.status,
                data1: event.data1,
                data2: event.data2,
                meta_type: event.meta_type,
                _pad0: 0,
                _pad1: 0,
                _pad2: 0,
                meta_data: meta_ptr,
                meta_data_len: meta_len,
            };
        }
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Shared helper for the scalar file-field getters.
fn read_file_field<T: Copy>(
    handle: *mut MidiFileHandle,
    out_value: *mut T,
    project: fn(&FileWrapper) -> T,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if out_value.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { file_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };
        unsafe {
            *out_value = project(wrapper);
        }
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Destroys a parsed file handle. Passing `null` is safe.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_file_destroy(handle: *mut MidiFileHandle) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(handle as *mut FileWrapper));
        }
    }));
}

// ---------------------------------------------------------------------------
// Serialization (incremental writer)
// ---------------------------------------------------------------------------

/// Creates a MIDI file writer with the given format and timing resolution and
/// writes its handle to `*out_handle`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_writer_create(
    format: u16,
    ticks_per_beat: u16,
    out_handle: *mut *mut MidiWriterHandle,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if out_handle.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }
        let boxed = Box::new(WriterWrapper {
            format,
            ticks_per_beat,
            tracks: Vec::new(),
        });
        unsafe {
            *out_handle = Box::into_raw(boxed) as *mut MidiWriterHandle;
        }
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Begins a new, empty track. Subsequent `writer_add_event` calls append to it.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_writer_begin_track(handle: *mut MidiWriterHandle) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        let wrapper = match unsafe { writer_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };
        wrapper.tracks.push(MidiTrackData::default());
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Appends an event to the current (most recently begun) track. The event's
/// payload bytes are copied, so the caller's buffer need only live for the call.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_writer_add_event(
    handle: *mut MidiWriterHandle,
    event: NativeMidiEvent,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        let wrapper = match unsafe { writer_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };

        let track = match wrapper.tracks.last_mut() {
            Some(t) => t,
            None => return MidiErrorCode::PortNotOpen as i32,
        };

        let meta_data = if event.meta_data.is_null() || event.meta_data_len == 0 {
            Vec::new()
        } else {
            unsafe {
                std::slice::from_raw_parts(event.meta_data, event.meta_data_len).to_vec()
            }
        };

        track.events.push(MidiEventData {
            delta_time: event.delta_time,
            kind: kind_from_u8(event.event_type),
            status: event.status,
            data1: event.data1,
            data2: event.data2,
            meta_type: event.meta_type,
            meta_data,
        });
        MidiErrorCode::Success as i32
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Serializes all added tracks to SMF bytes, writing the buffer pointer to
/// `*out_data` and its length to `*out_len`. The buffer must be released with
/// `ownaudio_midi_v1_free_bytes`.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_writer_serialize(
    handle: *mut MidiWriterHandle,
    out_data: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    let result = catch_unwind(AssertUnwindSafe(|| {
        if out_data.is_null() || out_len.is_null() {
            return MidiErrorCode::NullPointer as i32;
        }
        let wrapper = match unsafe { writer_from_ptr(handle) } {
            Some(w) => w,
            None => return MidiErrorCode::InvalidHandle as i32,
        };

        let file = MidiFileData {
            format: wrapper.format,
            ticks_per_beat: wrapper.ticks_per_beat,
            tracks: wrapper.tracks.clone(),
        };

        match write_midi_file(&file) {
            Ok(bytes) => {
                let boxed = bytes.into_boxed_slice();
                let len = boxed.len();
                unsafe {
                    *out_data = Box::into_raw(boxed) as *mut u8;
                    *out_len = len;
                }
                MidiErrorCode::Success as i32
            }
            Err(e) => MidiErrorCode::from(e) as i32,
        }
    }));

    result.unwrap_or(MidiErrorCode::InternalPanic as i32)
}

/// Destroys a writer handle. Passing `null` is safe.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_writer_destroy(handle: *mut MidiWriterHandle) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if handle.is_null() {
            return;
        }
        unsafe {
            drop(Box::from_raw(handle as *mut WriterWrapper));
        }
    }));
}

/// Releases a byte buffer returned by `ownaudio_midi_v1_writer_serialize`.
/// Passing `null` is safe.
#[no_mangle]
pub extern "C" fn ownaudio_midi_v1_free_bytes(data: *mut u8, len: usize) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if data.is_null() {
            return;
        }
        unsafe {
            let slice = std::slice::from_raw_parts_mut(data, len);
            drop(Box::from_raw(slice as *mut [u8]));
        }
    }));
}

/// Maps a C ABI event-type discriminant byte to its [`MidiEventKind`].
fn kind_from_u8(value: u8) -> MidiEventKind {
    match value {
        1 => MidiEventKind::Meta,
        2 => MidiEventKind::SysEx,
        _ => MidiEventKind::Midi,
    }
}
